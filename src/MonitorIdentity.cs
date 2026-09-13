using Microsoft.Win32;

namespace DLS;

/// <summary>
/// A monitor identity that survives a graphics card being replaced.
///
/// The CCD monitorDevicePath is the authoritative identity everywhere else in this
/// app, and for good reason: it is unique per physical port and never lies. But it
/// is not a property of the *panel*. It looks like
///
///     \\?\DISPLAY#DELF13D#7&amp;28c17335&amp;0&amp;UID776#{e6f07b5f-...}
///                 ^model   ^adapter instance ^connector
///
/// and the middle segment names the adapter the monitor is plugged into. Swap the
/// GPU and every path on the machine changes at once, so every saved layout stops
/// recognising every monitor simultaneously — the layouts look intact but refuse to
/// apply ("cannot enable a monitor that is not connected"), while the same monitors
/// reappear as brand-new unused ones. Re-plugging into different ports on the same
/// card does the same thing on a smaller scale.
///
/// So identity needs a second, weaker key that belongs to the panel rather than to
/// the wiring: the EDID the monitor itself reports. The model token is already in
/// the path for free; the serial number has to come from the registry, where Windows
/// caches the raw EDID blob per device instance. Both together are what
/// <see cref="Rebind"/> uses to re-attach a saved layout to hardware that moved.
///
/// This is deliberately a *fallback*. The device path is still matched first and
/// whole, and the key is only consulted when a path no longer resolves.
/// </summary>
internal static class MonitorIdentity
{
    /// <summary>
    /// The stable key for a monitor, e.g. <c>DELF13D#43310C4C-2RZKXV3</c> — model
    /// token, then (when the panel reports one) its EDID serial. Empty for a path
    /// that names no monitor.
    /// </summary>
    public static string KeyFor(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return string.Empty;

        lock (KeyCache)
        {
            if (KeyCache.TryGetValue(devicePath, out string? cached)) return cached;
        }

        string model = ModelOf(devicePath);
        string serial = ReadSerial(devicePath);
        string key = string.IsNullOrEmpty(serial) ? model : $"{model}#{serial}";

        lock (KeyCache) KeyCache[devicePath] = key;
        return key;
    }

    /// <summary>Drops cached EDID reads. Called on a display change, alongside the
    /// other per-port caches.</summary>
    public static void InvalidateCaches()
    {
        lock (KeyCache) KeyCache.Clear();
    }

    /// <summary>The model token — EDID manufacturer plus product code, e.g.
    /// <c>DELF13D</c>. Identical for two monitors of the same model, which is exactly
    /// why it is never used on its own to pick between candidates.</summary>
    public static string ModelOf(string pathOrKey)
    {
        if (string.IsNullOrWhiteSpace(pathOrKey)) return string.Empty;

        // A key is "MODEL" or "MODEL#SERIAL"; a path is "\\?\DISPLAY#MODEL#INSTANCE#{guid}".
        var parts = pathOrKey.Split('#');
        if (parts.Length == 1) return parts[0].ToUpperInvariant();
        if (!pathOrKey.StartsWith(@"\\", StringComparison.Ordinal)) return parts[0].ToUpperInvariant();
        return parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
    }

    /// <summary>The serial half of a key, or empty when the panel reports none.</summary>
    private static string SerialOf(string key)
    {
        int hash = key.IndexOf('#');
        return hash < 0 ? string.Empty : key[(hash + 1)..];
    }

    /// <summary>
    /// The adapter instance a path is wired through, e.g. <c>7&amp;28c17335&amp;0</c>.
    /// When no live monitor is on a given adapter any more, that card is gone from
    /// the machine — which is the difference between "this monitor moved" and "this
    /// monitor is merely unplugged".
    /// </summary>
    public static string AdapterOf(string devicePath)
    {
        var parts = devicePath.Split('#');
        if (parts.Length < 3) return string.Empty;

        // "7&28c17335&0&UID776" — everything up to the connector UID.
        string instance = parts[2];
        int uid = instance.LastIndexOf("&UID", StringComparison.OrdinalIgnoreCase);
        return (uid < 0 ? instance : instance[..uid]).ToUpperInvariant();
    }

    // ------------------------------------------------------------------ matching

    /// <summary>Same panel, proven by a serial both sides report.</summary>
    private static bool SameSerial(string a, string b)
    {
        string sa = SerialOf(a), sb = SerialOf(b);
        return sa.Length > 0 && sb.Length > 0 &&
               string.Equals(ModelOf(a), ModelOf(b), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Same model. True for two monitors off the same production line, so a
    /// caller must also establish that the choice is unambiguous.</summary>
    private static bool SameModel(string a, string b) =>
        ModelOf(a).Length > 0 && string.Equals(ModelOf(a), ModelOf(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Two serials that both exist and disagree are two different panels,
    /// whatever the model says.</summary>
    private static bool SerialsConflict(string a, string b)
    {
        string sa = SerialOf(a), sb = SerialOf(b);
        return sa.Length > 0 && sb.Length > 0 && !string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- reconciliation

    /// <summary>
    /// Re-attaches a layout's saved monitors to the hardware as it is now, when their
    /// device paths no longer resolve. Returns the number of targets changed, so a
    /// caller can decide whether the result is worth writing back to disk.
    ///
    /// Conservative by construction. A target is only ever re-pointed at a monitor
    /// the EDID says is the same panel, and only when that choice is the only one
    /// available; anything ambiguous is left exactly as it was for the user to sort
    /// out in the editor, because silently binding a layout to the wrong panel is a
    /// far worse failure than leaving it visibly broken.
    /// </summary>
    public static int Rebind(DisplayProfile profile, IReadOnlyList<DisplayInfo> live)
    {
        if (live.Count == 0 || profile.Displays.Count == 0) return 0;

        int changes = 0;
        var liveAdapters = live
            .Select(d => AdapterOf(d.MonitorDevicePath))
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DisplayInfo? LiveFor(DisplayTargetConfig t) =>
            live.FirstOrDefault(d => CcdEngine.SameMonitor(d.MonitorDevicePath, t.MonitorDevicePath));

        string KeyOf(DisplayTargetConfig t) =>
            string.IsNullOrWhiteSpace(t.MonitorKey) ? ModelOf(t.MonitorDevicePath) : t.MonitorKey;

        // Pass 1 — a saved monitor whose port no longer exists, and a connected
        // monitor no layout entry is pointing at: if the EDID says they are the same
        // panel and there is only one way to read that, they are the same panel.
        var orphans = profile.Displays.Where(t => LiveFor(t) == null).ToList();
        foreach (var orphan in orphans.OrderByDescending(t => t.Enabled))
        {
            var claimed = profile.Displays
                .Select(LiveFor)
                .Where(d => d != null)
                .Select(d => d!.MonitorDevicePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var free = live.Where(d => !claimed.Contains(d.MonitorDevicePath)).ToList();
            string key = KeyOf(orphan);

            var bySerial = free.Where(d => SameSerial(key, d.MonitorKey)).ToList();
            var byModel = free
                .Where(d => SameModel(key, d.MonitorKey) && !SerialsConflict(key, d.MonitorKey))
                .ToList();

            // A serial match is proof and wins outright. A model match is only a
            // guess, so it is taken solely when there is nothing to guess between.
            var pick = bySerial.Count == 1 ? bySerial[0]
                     : bySerial.Count == 0 && byModel.Count == 1 ? byModel[0]
                     : null;

            if (pick == null) continue;

            AdoptIdentity(orphan, pick);
            changes++;
        }

        // Pass 2 — the same monitor saved twice. This is what a GPU swap leaves
        // behind: the layout still holds the pre-swap entry, and the editor, seeing
        // an unrecognised monitor, has already added a second entry for the same
        // panel on its new port. One of the two carries the arrangement the user
        // actually built, and it is not necessarily the live one.
        foreach (var orphan in profile.Displays.Where(t => LiveFor(t) == null).ToList())
        {
            string key = KeyOf(orphan);
            var host = profile.Displays.FirstOrDefault(t =>
                !ReferenceEquals(t, orphan) && LiveFor(t) != null &&
                (SameSerial(key, KeyOf(t)) ||
                 (SameModel(key, KeyOf(t)) && !SerialsConflict(key, KeyOf(t)))));

            if (host == null) continue;   // genuinely absent monitor — keep it saved

            bool adapterGone = !liveAdapters.Contains(AdapterOf(orphan.MonitorDevicePath));

            // An entry that is switched off carries no arrangement to lose, and the
            // editor re-adds it by itself the moment the monitor turns up. An entry
            // that is switched ON does carry one, so it is only ever discarded when
            // the adapter it names has left the machine — never when the monitor
            // might simply be unplugged from a card that is still there.
            if (!adapterGone && orphan.Enabled) continue;

            if (orphan.Enabled && !host.Enabled) AdoptArrangement(host, orphan);
            profile.Displays.Remove(orphan);
            changes++;
        }

        return changes;
    }

    /// <summary>Points a saved target at the port a monitor is on now.</summary>
    private static void AdoptIdentity(DisplayTargetConfig target, DisplayInfo live)
    {
        target.MonitorDevicePath = live.MonitorDevicePath;
        target.HardwareId = live.MonitorDevicePath;
        target.MonitorKey = live.MonitorKey;
        target.DeviceName = live.DeviceName;
        if (string.IsNullOrWhiteSpace(target.MonitorId)) target.MonitorId = live.MonitorId;

        // The saved timings were measured through the old adapter. They are usually
        // still right, but "usually" is not good enough to hand to SetDisplayConfig,
        // and dropping them only costs one fallback tier inside CcdEngine.Apply.
        target.HasTargetMode = false;
    }

    /// <summary>
    /// Moves the user's arrangement — what is on, where, and at what mode — from a
    /// stale duplicate onto the entry that still resolves. Identity stays the host's;
    /// everything the user chose comes from the entry being retired.
    /// </summary>
    private static void AdoptArrangement(DisplayTargetConfig host, DisplayTargetConfig from)
    {
        bool sameMode = host.Width == from.Width && host.Height == from.Height &&
                        host.RefreshRate == from.RefreshRate;

        host.Enabled = from.Enabled;
        host.IsPrimary = from.IsPrimary;
        host.X = from.X;
        host.Y = from.Y;
        host.Width = from.Width;
        host.Height = from.Height;
        host.RefreshRate = from.RefreshRate;
        host.RefreshNumerator = from.RefreshNumerator;
        host.RefreshDenominator = from.RefreshDenominator;
        host.ScalePercent = from.ScalePercent;
        host.Rotation = from.Rotation;
        host.Scaling = from.Scaling;
        if (!string.IsNullOrWhiteSpace(from.RelativePosition)) host.RelativePosition = from.RelativePosition;

        // The host's captured timings describe the host's captured resolution. Once
        // that no longer matches what the layout asks for, they are worse than
        // nothing — let Windows resolve the mode instead.
        if (!sameMode) host.HasTargetMode = false;
    }

    // ---------------------------------------------------------------- EDID reading

    private static readonly Dictionary<string, string> KeyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The panel's own serial, from the EDID Windows caches under the device's
    /// registry key. Empty when the monitor reports none — which is common, and the
    /// reason model matching exists at all.
    /// </summary>
    private static string ReadSerial(string devicePath)
    {
        byte[]? edid = ReadEdid(devicePath);
        if (edid == null || edid.Length < 128) return string.Empty;

        // Every EDID starts 00 FF FF FF FF FF FF 00. Anything else is not an EDID.
        if (edid[0] != 0x00 || edid[7] != 0x00) return string.Empty;
        for (int i = 1; i <= 6; i++) if (edid[i] != 0xFF) return string.Empty;

        uint serial = BitConverter.ToUInt32(edid, 12);
        string text = SerialDescriptor(edid);

        if (serial == 0 && text.Length == 0) return string.Empty;
        if (serial == 0) return text;
        return text.Length == 0 ? serial.ToString("X8") : $"{serial:X8}-{text}";
    }

    /// <summary>
    /// The ASCII serial from the four 18-byte descriptor blocks, if one carries tag
    /// 0xFF. Placeholders some vendors ship ("0000000000000") are rejected: a value
    /// every unit of a model shares is not a serial, and treating it as one would
    /// make two identical monitors look like proven matches.
    /// </summary>
    private static string SerialDescriptor(byte[] edid)
    {
        foreach (int offset in new[] { 54, 72, 90, 108 })
        {
            if (offset + 18 > edid.Length) break;
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0) continue;
            if (edid[offset + 3] != 0xFF) continue;

            var chars = new List<char>();
            for (int i = offset + 5; i < offset + 18; i++)
            {
                if (edid[i] == 0x0A) break;
                if (edid[i] < 0x20 || edid[i] > 0x7E) continue;
                chars.Add((char)edid[i]);
            }

            string text = new string(chars.ToArray()).Trim();
            if (text.Length == 0 || text.All(c => c == '0')) continue;
            return text;
        }

        return string.Empty;
    }

    /// <summary>
    /// The raw EDID blob for a device path. The path already contains both halves of
    /// the registry location, which is why this needs no device enumeration:
    ///
    ///     \\?\DISPLAY#DELF13D#7&amp;28c17335&amp;0&amp;UID776#{guid}
    ///     HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\DELF13D\7&amp;28c17335&amp;0&amp;UID776\Device Parameters
    /// </summary>
    private static byte[]? ReadEdid(string devicePath)
    {
        var parts = devicePath.Split('#');
        if (parts.Length < 3) return null;

        string hardwareId = parts[1];
        string instance = parts[2];
        if (hardwareId.Length == 0 || instance.Length == 0) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareId}\{instance}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch
        {
            // No read access, or the device is gone from the registry. Both mean the
            // model token is all the identity there is, which is a supported case.
            return null;
        }
    }
}
