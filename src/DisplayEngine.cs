
namespace DLS;

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
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }
    public int ScalePercent { get; set; }
    public int RecommendedScalePercent { get; set; }
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
        Displays = profile.Displays.Select(d => d.Clone()).ToList()
    };

    // Memberwise, deliberately: DisplayTargetConfig is all strings and value types,
    // so this is a true deep copy, and it cannot silently drop a field the way a
    // hand-written initializer does when someone adds one.
    public static DisplayTargetConfig Clone(this DisplayTargetConfig d) => d.CopyOf();
}

public class DisplayProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty; // e.g., "Ctrl+Alt+1"
    public List<DisplayTargetConfig> Displays { get; set; } = new();

    /// <summary>
    /// True when this layout cannot identify its monitors and must be re-captured.
    ///
    /// Derived from the data rather than stored as a version number: a layout is
    /// unusable precisely when a target has no device path, so reading that directly
    /// cannot disagree with the data the way a persisted flag eventually would. It is
    /// also never written to the settings file.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsRecapture =>
        Displays.Count == 0 ||
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

    /// <summary>The panel's native resolution, for offering "native" in the editor.</summary>
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }

    /// <summary>Windows scaling to apply after the switch, as a percentage.
    /// 0 means "leave whatever Windows already has".</summary>
    public int ScalePercent { get; set; }

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
                width = entry.NativeWidth > 0 ? entry.NativeWidth : (active.cx > 0 ? (int)active.cx : 1920);
                height = entry.NativeHeight > 0 ? entry.NativeHeight : (active.cy > 0 ? (int)active.cy : 1080);
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
                IsAttached = entry.IsActive,
                NativeWidth = entry.NativeWidth,
                NativeHeight = entry.NativeHeight,
                ScalePercent = entry.ScalePercent,
                RecommendedScalePercent = entry.RecommendedScalePercent
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
        Displays = CcdEngine.Capture()
    };

    /// <summary>
    /// Whole-string identity comparison on the CCD monitorDevicePath. There is no
    /// parsing, no model-name extraction, and no fuzzy matching — that is precisely
    /// what made same-model monitors indistinguishable before.
    /// </summary>
    public static bool SameHardwareIdentity(string first, string second) =>
        CcdEngine.SameMonitor(first, second);

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
    public static bool MatchesCurrent(DisplayProfile profile, List<DisplayInfo> displays, int slack = 96) =>
        Compare(profile, displays, slack).Count == 0;

    /// <summary>
    /// One way in which a layout disagrees with the desktop as it is right now — i.e.
    /// one thing applying it would change.
    /// </summary>
    public sealed record LayoutDifference(string Monitor, string Change)
    {
        public override string ToString() => $"{Monitor} {Change}";
    }

    /// <summary>
    /// Everything applying <paramref name="profile"/> would change, or an empty list
    /// when the desktop already looks like it.
    ///
    /// This is the single definition of "does this layout match?" — <see
    /// cref="MatchesCurrent"/> is just "nothing differs". Keeping them one function
    /// matters: the editor's pending-changes indicator and the tray's live checkmark
    /// have to agree with the Apply button's own "already active" shortcut, and three
    /// separate comparisons would eventually disagree about a borderline case and
    /// leave the user reading a stale badge.
    /// </summary>
    public static List<LayoutDifference> Compare(DisplayProfile profile, List<DisplayInfo> displays, int slack = 96)
    {
        var differences = new List<LayoutDifference>();

        if (profile.NeedsRecapture)
        {
            differences.Add(new LayoutDifference(profile.Name, "needs to be re-captured"));
            return differences;
        }

        var live = displays.Where(d => d.IsAttached).ToList();
        var wanted = profile.Displays.Where(d => d.Enabled).ToList();

        foreach (var target in wanted)
        {
            string name = NameOf(target);
            var match = live.FirstOrDefault(d =>
                SameHardwareIdentity(d.MonitorDevicePath, IdentityOf(target)));

            if (match == null)
            {
                differences.Add(new LayoutDifference(name, "turns on"));
                continue;
            }

            if (match.Width != target.Width || match.Height != target.Height)
            {
                differences.Add(new LayoutDifference(name,
                    $"{match.Width}×{match.Height} → {target.Width}×{target.Height}"));
            }

            if (Math.Abs(match.X - target.X) > slack || Math.Abs(match.Y - target.Y) > slack)
            {
                // Promoting a monitor to main is what moves every other monitor's
                // coordinates, so it reaches here as a position difference. Saying so
                // is more use than reporting the side effect.
                differences.Add(new LayoutDifference(name,
                    target.IsPrimary && !match.IsPrimary ? "becomes the main display" : "moves"));
            }

            // Refresh rate and scaling are part of what a layout specifies, so a
            // difference in either means it is NOT the live layout. Without this,
            // changing only the refresh rate left the layout still "matching", and
            // Apply reported "already active" and returned without doing anything.
            //
            // The 1Hz tolerance absorbs rounding: a captured 59.94Hz mode and a
            // requested 60Hz are the same mode.
            if (target.RefreshRate > 0 && match.RefreshRate > 0 &&
                Math.Abs(match.RefreshRate - target.RefreshRate) > 1)
            {
                differences.Add(new LayoutDifference(name, $"{match.RefreshRate} → {target.RefreshRate} Hz"));
            }

            if (target.ScalePercent > 0 && match.ScalePercent > 0 &&
                match.ScalePercent != target.ScalePercent)
            {
                differences.Add(new LayoutDifference(name, $"{match.ScalePercent}% → {target.ScalePercent}% scale"));
            }
        }

        // A monitor that is on now and not in the layout gets switched off by it —
        // the difference the user most wants warned about, and the reason a plain
        // count comparison was never enough.
        foreach (var extra in live.Where(d =>
                     !wanted.Any(t => SameHardwareIdentity(d.MonitorDevicePath, IdentityOf(t)))))
        {
            differences.Add(new LayoutDifference(NameOf(extra), "turns off"));
        }

        return differences;
    }

    private static string NameOf(DisplayTargetConfig target) =>
        string.IsNullOrWhiteSpace(target.MonitorId) ? target.DeviceName : target.MonitorId;

    private static string NameOf(DisplayInfo display) =>
        string.IsNullOrWhiteSpace(display.MonitorId) ? display.DeviceName : display.MonitorId;

    private static string IdentityOf(DisplayTargetConfig target) =>
        string.IsNullOrWhiteSpace(target.MonitorDevicePath) ? target.HardwareId : target.MonitorDevicePath;

    /// <summary>
    /// The saved layout the desktop currently matches, if any. Queries the display
    /// configuration ONCE — the obvious `FirstOrDefault(MatchesCurrent)` does a full
    /// CCD enumeration per layout, which is expensive enough to be visible as UI
    /// stutter when this is called from a timer.
    /// </summary>
    public static DisplayProfile? FindMatchingProfile(IEnumerable<DisplayProfile> profiles)
    {
        var displays = GetCurrentDisplays();
        return profiles.FirstOrDefault(p => MatchesCurrent(p, displays));
    }

    /// <summary>Post-apply check: did the desktop actually end up where we asked?</summary>
    public static (bool Matched, string Summary) Verify(DisplayProfile profile)
    {
        var live = GetCurrentDisplays().Where(d => d.IsAttached).ToList();
        var wanted = profile.Displays.Where(d => d.Enabled).ToList();

        var missing = wanted
            .Where(t => !live.Any(d => SameHardwareIdentity(d.MonitorDevicePath, IdentityOf(t))))
            .Select(NameOf)
            .ToList();

        if (missing.Count > 0)
            return (false, $"{string.Join(", ", missing)} did not come on");

        var extra = live
            .Where(d => !wanted.Any(t => SameHardwareIdentity(d.MonitorDevicePath, IdentityOf(t))))
            .Select(NameOf)
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
