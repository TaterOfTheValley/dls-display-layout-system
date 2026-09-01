
namespace MonitorLayoutSwitcher;

public class DisplayInfo
{
    public string DeviceName { get; set; } = string.Empty;
    public string MonitorId { get; set; } = string.Empty; // EDID / Friendly name
    public string HardwareId { get; set; } = string.Empty; // Stable monitor PnP / EDID identity
    public string MonitorDevicePath { get; set; } = string.Empty; // CCD identity — the authoritative one
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsAttached { get; set; }
    public string RelativePosition { get; set; } = "Center"; // Left, Center, Right

    public override string ToString() =>
        $"{DeviceName} ({MonitorId}) [{RelativePosition}] - {Width}x{Height} @ ({X},{Y}) Primary={IsPrimary} Attached={IsAttached}";
}

public static class DisplayClone
{
    public static DisplayProfile Clone(this DisplayProfile profile, bool newId = true) => new()
    {
        Id = newId ? Guid.NewGuid().ToString("N") : profile.Id,
        Name = profile.Name,
        Hotkey = profile.Hotkey,
        SchemaVersion = profile.SchemaVersion,
        Displays = profile.Displays.Select(d => d.Clone()).ToList()
    };

    // Memberwise, deliberately: DisplayTargetConfig is all strings and value types,
    // so this is a true deep copy, and it cannot silently drop a field the way a
    // hand-written initializer does when someone adds one.
    public static DisplayTargetConfig Clone(this DisplayTargetConfig d) => d.CopyOf();
}

public class DisplayProfile
{
    /// <summary>
    /// 1 = the original ChangeDisplaySettingsEx-era schema, identified monitors by a
    /// parsed EnumDisplayDevices key. That key could not tell two same-model monitors
    /// apart, so v1 profiles are not migrated — they are marked for re-capture.
    /// 2 = CCD schema, identified by monitorDevicePath.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty; // e.g., "Ctrl+Alt+1"
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<DisplayTargetConfig> Displays { get; set; } = new();

    /// <summary>True when this profile predates CCD identity and must be re-captured.</summary>
    public bool NeedsRecapture =>
        SchemaVersion < CurrentSchemaVersion ||
        Displays.Any(d => string.IsNullOrWhiteSpace(d.MonitorDevicePath));
}

public class DisplayTargetConfig
{
    // Identity. This is the CCD monitorDevicePath, e.g.
    //   \\?\DISPLAY#DELF13D#5&2b7bf8c&0&UID4352#{e6f07b5f-...}
    // It is unique per physical port and stable across enable/disable and reboots.
    // Compare it whole; never parse it. HardwareId is kept only so profiles written
    // by older builds still deserialize, and is mirrored from this on capture.
    public string MonitorDevicePath { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;   // \\.\DISPLAYn — display only, never matched on
    public string MonitorId { get; set; } = string.Empty;    // friendly name, e.g. "DELL U2723QE"
    public string HardwareId { get; set; } = string.Empty;   // legacy identity field
    public string RelativePosition { get; set; } = string.Empty; // Left, Center, Right
    public bool Enabled { get; set; } = true;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }                     // rounded, for display only
    public bool IsPrimary { get; set; }

    // --- CCD mode detail -----------------------------------------------------
    // Refresh is a rational in CCD (143.998 Hz = 2400000/16667). Storing only the
    // rounded integer is a real source of "mode not found" failures on high-refresh
    // panels, so the exact ratio is kept alongside it.
    public uint RefreshNumerator { get; set; }
    public uint RefreshDenominator { get; set; }
    public uint Rotation { get; set; } = 1;   // DISPLAYCONFIG_ROTATION_IDENTITY
    public uint Scaling { get; set; } = 1;    // DISPLAYCONFIG_SCALING_IDENTITY
    public uint OutputTechnology { get; set; }
    public uint PixelFormat { get; set; } = 4; // DISPLAYCONFIG_PIXELFORMAT_32BPP

    // The full target video signal info, captured while the monitor was live. This
    // is what lets a currently-disabled monitor be re-enabled at its true native
    // mode in a single call — the old CDS path had to "wake" it at a low mode first
    // because a disabled output only advertises a generic mode list.
    public bool HasTargetMode { get; set; }
    public ulong PixelRate { get; set; }
    public uint HSyncNumerator { get; set; }
    public uint HSyncDenominator { get; set; }
    public uint VSyncNumerator { get; set; }
    public uint VSyncDenominator { get; set; }
    public uint ActiveCx { get; set; }
    public uint ActiveCy { get; set; }
    public uint TotalCx { get; set; }
    public uint TotalCy { get; set; }
    public uint VideoStandard { get; set; }
    public uint ScanLineOrdering { get; set; }

    internal DisplayTargetConfig CopyOf() => (DisplayTargetConfig)MemberwiseClone();
}

/// <summary>
/// The public display API used by the rest of the app. Since the CCD rewrite this
/// is a thin facade over <see cref="CcdEngine"/> — all the real work is one
/// SetDisplayConfig call carrying the complete topology.
///
/// What used to live here: a four-phase ChangeDisplaySettingsEx apply (wake,
/// configure, commit, detach) with hand-tuned ordering, plus device-ID string
/// parsing to tell same-model monitors apart. Both are gone. Identity is now the
/// CCD monitorDevicePath, compared whole.
/// </summary>
public static class DisplayEngine
{
    /// <summary>
    /// Every physically connected monitor, whether or not it is currently enabled.
    /// Paths with no monitor behind them — unused GPU ports, phantom adapter
    /// endpoints — never appear, because they have no monitorDevicePath.
    /// Disabled-but-connected monitors DO appear, with IsAttached false; the layout
    /// editor needs them in order to turn them back on.
    /// </summary>
    public static List<DisplayInfo> GetCurrentDisplays()
    {
        var topo = CcdEngine.Query(includeInactive: true);
        var result = new List<DisplayInfo>();

        foreach (var group in topo.Entries.GroupBy(e => e.MonitorDevicePath, StringComparer.OrdinalIgnoreCase))
        {
            var entry = group.FirstOrDefault(e => e.IsActive) ?? group.First();

            // An inactive path carries no source mode. Fall back to the target's
            // active pixel count so the editor can still draw it at the right shape.
            int width = entry.Width, height = entry.Height;
            if (width <= 0 || height <= 0)
            {
                var active = entry.TargetMode.targetVideoSignalInfo.activeSize;
                width = active.cx > 0 ? (int)active.cx : 1920;
                height = active.cy > 0 ? (int)active.cy : 1080;
            }

            result.Add(new DisplayInfo
            {
                DeviceName = string.IsNullOrWhiteSpace(entry.GdiDeviceName)
                    ? entry.MonitorDevicePath
                    : entry.GdiDeviceName,
                MonitorId = entry.FriendlyName,
                MonitorDevicePath = entry.MonitorDevicePath,
                HardwareId = entry.MonitorDevicePath,
                X = entry.X,
                Y = entry.Y,
                Width = width,
                Height = height,
                RefreshRate = (int)Math.Round(entry.RefreshRate.AsHz),
                IsPrimary = entry.IsPrimary,
                IsAttached = entry.IsActive
            });
        }

        AssignRelativePositions(result);
        return result;
    }

    /// <summary>Labels monitors Left / Center / Right by desktop X order, for the UI.</summary>
    private static void AssignRelativePositions(List<DisplayInfo> displays)
    {
        var ordered = displays.Where(d => d.IsAttached).OrderBy(d => d.X).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].RelativePosition = ordered.Count == 1 ? "Left"
                : i == 0 ? "Left"
                : i == ordered.Count - 1 ? "Right"
                : "Center";
        }

        foreach (var off in displays.Where(d => !d.IsAttached))
        {
            off.RelativePosition = string.IsNullOrWhiteSpace(off.RelativePosition) ? "Center" : off.RelativePosition;
        }
    }

    /// <summary>
    /// Applies a whole layout. One atomic SetDisplayConfig call in the normal case;
    /// see CcdEngine.Apply for the fallback ladder when saved modes can't be honoured
    /// verbatim.
    /// </summary>
    public static bool ApplyProfile(DisplayProfile profile, out string errorMessage)
    {
        if (profile.NeedsRecapture)
        {
            errorMessage = "This profile was saved by an older version and can't identify your " +
                           "monitors reliably. Open it, arrange the monitors, and save it again.";
            return false;
        }

        var result = CcdEngine.Apply(profile);
        errorMessage = result.Ok ? string.Empty : Combine(result);
        return result.Ok;
    }

    /// <summary>
    /// A true dry run: SDC_VALIDATE checks the complete proposed topology at once.
    /// Unlike the old per-device CDS_TEST this does not produce false negatives when
    /// re-enabling a currently-disabled monitor, so it is safe to gate on.
    /// </summary>
    public static bool ValidateProfile(DisplayProfile profile, out string errorMessage)
    {
        if (profile.NeedsRecapture)
        {
            errorMessage = "This profile was saved by an older version and needs to be re-captured.";
            return false;
        }

        var result = CcdEngine.Apply(profile, validateOnly: true);
        errorMessage = result.Ok ? string.Empty : Combine(result);
        return result.Ok;
    }

    private static string Combine(CcdResult result) =>
        string.IsNullOrWhiteSpace(result.Detail) ? result.Message : $"{result.Message} [{result.Detail}]";

    /// <summary>Full CCD capture of every connected monitor, including mode detail.</summary>
    public static List<DisplayTargetConfig> CaptureTargets() => CcdEngine.Capture();

    public static DisplayProfile CaptureCurrentLayoutAsProfile(string profileName, string hotkey) => new()
    {
        Name = profileName,
        Hotkey = hotkey,
        SchemaVersion = DisplayProfile.CurrentSchemaVersion,
        Displays = CcdEngine.Capture()
    };

    /// <summary>
    /// Whole-string identity comparison on the CCD monitorDevicePath. There is no
    /// parsing, no model-name extraction, and no fuzzy matching — that is precisely
    /// what made same-model monitors indistinguishable before.
    /// </summary>
    public static bool SameHardwareIdentity(string first, string second) =>
        CcdEngine.SameMonitor(first, second);

    /// <summary>Names of monitors that are on now but would be switched off by this profile.</summary>
    public static List<string> MonitorsThatWouldDisable(DisplayProfile profile)
    {
        var live = GetCurrentDisplays().Where(d => d.IsAttached).ToList();
        var keeping = profile.Displays
            .Where(d => d.Enabled)
            .Select(d => string.IsNullOrWhiteSpace(d.MonitorDevicePath) ? d.HardwareId : d.MonitorDevicePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return live
            .Where(d => !keeping.Contains(d.MonitorDevicePath))
            .Select(d => string.IsNullOrWhiteSpace(d.MonitorId) ? d.DeviceName : d.MonitorId)
            .ToList();
    }

    /// <summary>
    /// True when the desktop already looks like this profile. Used for the tray
    /// menu's checkmark, so a little positional slack is fine.
    /// </summary>
    public static bool MatchesCurrent(DisplayProfile profile, int slack = 96) =>
        MatchesCurrent(profile, GetCurrentDisplays(), slack);

    /// <summary>
    /// Overload taking an already-fetched display list. Each GetCurrentDisplays call
    /// is a full QueryDisplayConfig plus a DisplayConfigGetDeviceInfo per path, so a
    /// caller testing several profiles at once — the tray menu does exactly that —
    /// should query once and pass the result in rather than re-querying per profile.
    /// </summary>
    public static bool MatchesCurrent(DisplayProfile profile, List<DisplayInfo> displays, int slack = 96)
    {
        if (profile.NeedsRecapture) return false;

        var live = displays.Where(d => d.IsAttached).ToList();
        var wanted = profile.Displays.Where(d => d.Enabled).ToList();
        if (live.Count != wanted.Count) return false;

        foreach (var target in wanted)
        {
            var match = live.FirstOrDefault(d =>
                SameHardwareIdentity(d.MonitorDevicePath, IdentityOf(target)));
            if (match == null) return false;
            if (match.Width != target.Width || match.Height != target.Height) return false;
            if (Math.Abs(match.X - target.X) > slack || Math.Abs(match.Y - target.Y) > slack) return false;
        }

        return true;
    }

    private static string IdentityOf(DisplayTargetConfig target) =>
        string.IsNullOrWhiteSpace(target.MonitorDevicePath) ? target.HardwareId : target.MonitorDevicePath;

    public static DisplayProfile? FindMatchingProfile(IEnumerable<DisplayProfile> profiles) =>
        profiles.FirstOrDefault(p => MatchesCurrent(p));

    /// <summary>Post-apply check: did the desktop actually end up where we asked?</summary>
    public static (bool Matched, string Summary) Verify(DisplayProfile profile)
    {
        var live = GetCurrentDisplays().Where(d => d.IsAttached).ToList();
        var wanted = profile.Displays.Where(d => d.Enabled).ToList();

        var missing = wanted
            .Where(t => !live.Any(d => SameHardwareIdentity(d.MonitorDevicePath, IdentityOf(t))))
            .Select(t => string.IsNullOrWhiteSpace(t.MonitorId) ? t.DeviceName : t.MonitorId)
            .ToList();

        if (missing.Count > 0)
            return (false, $"{string.Join(", ", missing)} did not come on");

        var extra = live
            .Where(d => !wanted.Any(t => SameHardwareIdentity(d.MonitorDevicePath, IdentityOf(t))))
            .Select(d => string.IsNullOrWhiteSpace(d.MonitorId) ? d.DeviceName : d.MonitorId)
            .ToList();

        if (extra.Count > 0)
            return (false, $"{string.Join(", ", extra)} stayed on");

        return (true, "layout matches");
    }

    /// <summary>
    /// Error text is already written for humans by CcdEngine.Explain, so this is a
    /// pass-through. Kept because callers still route messages through it.
    /// </summary>
    public static string FriendlyError(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? "The layout could not be applied." : raw;

    /// <summary>Human-readable dump of the live topology, for --dump-config.</summary>
    public static string DumpConfiguration() => CcdEngine.Dump();
}
