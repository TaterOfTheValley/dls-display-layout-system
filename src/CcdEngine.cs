using System.Text;

namespace MonitorLayoutSwitcher;

/// <summary>
/// One display path as reported by QueryDisplayConfig, with its monitor identity
/// already resolved. A "path" is a source (a desktop surface Windows can draw)
/// wired to a target (a physical connector). A monitor can appear on several
/// paths; at most one of them is active at a time.
/// </summary>
internal sealed class CcdPath
{
    public int Index;
    public string MonitorDevicePath = string.Empty;  // the stable identity — compare whole, never parse
    public string FriendlyName = string.Empty;
    public string GdiDeviceName = string.Empty;      // \\.\DISPLAY1 — only meaningful while active
    public bool IsActive;
    public bool TargetAvailable;

    public int X, Y, Width, Height;
    public DISPLAYCONFIG_RATIONAL RefreshRate;
    public uint Rotation = 1;   // DISPLAYCONFIG_ROTATION_IDENTITY
    public uint Scaling = 1;    // DISPLAYCONFIG_SCALING_IDENTITY
    public uint OutputTechnology;

    public bool HasSourceMode;
    public DISPLAYCONFIG_SOURCE_MODE SourceMode;
    public bool HasTargetMode;
    public DISPLAYCONFIG_TARGET_MODE TargetMode;

    public bool IsPrimary => IsActive && X == 0 && Y == 0;

    public override string ToString() =>
        $"[{Index}] {FriendlyName} {(IsActive ? "ACTIVE" : "off")} {Width}x{Height}@{RefreshRate.AsHz:F3} ({X},{Y}) {GdiDeviceName}";
}

/// <summary>A full snapshot of the display topology: the raw arrays plus resolved identity.</summary>
internal sealed class CcdTopology
{
    public DISPLAYCONFIG_PATH_INFO[] Paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
    public DISPLAYCONFIG_MODE_INFO[] Modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
    public List<CcdPath> Entries = new();

    public IEnumerable<CcdPath> Active => Entries.Where(e => e.IsActive);
}

internal sealed record CcdResult(bool Ok, string Message, string Detail = "")
{
    public static CcdResult Success(string message = "") => new(true, message);
    public static CcdResult Fail(string message, string detail = "") => new(false, message, detail);
}

/// <summary>
/// The display engine. Everything goes through QueryDisplayConfig /
/// SetDisplayConfig — the same API the Windows display settings page uses.
///
/// The central property that makes this reliable, and that ChangeDisplaySettingsEx
/// could not offer: an apply is ONE call carrying the complete desired topology.
/// There is no per-device sequencing, no staging, no commit ordering, and no
/// window in which the desktop is half-configured.
/// </summary>
internal static class CcdEngine
{
    // ---------------------------------------------------------------- query

    public static CcdTopology Query(bool includeInactive = true)
    {
        uint flags = includeInactive ? Ccd.QDC_ALL_PATHS : Ccd.QDC_ONLY_ACTIVE_PATHS;
        var topo = new CcdTopology();

        // The path count can change between sizing and querying (a hotplug lands
        // in between), which is exactly what ERROR_INSUFFICIENT_BUFFER means here.
        // Re-size and retry rather than failing.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int status = CcdNative.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (status != Ccd.ERROR_SUCCESS || pathCount == 0) return topo;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(1, modeCount)];

            status = CcdNative.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status == Ccd.ERROR_INSUFFICIENT_BUFFER) continue;
            if (status != Ccd.ERROR_SUCCESS) return topo;

            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            topo.Paths = paths;
            topo.Modes = modes;
            break;
        }

        for (int i = 0; i < topo.Paths.Length; i++)
        {
            var entry = Describe(topo, i);
            if (entry != null) topo.Entries.Add(entry);
        }

        return topo;
    }

    private static CcdPath? Describe(CcdTopology topo, int index)
    {
        ref var path = ref topo.Paths[index];

        string devicePath = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id, out string friendly);
        if (string.IsNullOrWhiteSpace(devicePath)) return null;   // no real monitor behind this path

        var entry = new CcdPath
        {
            Index = index,
            MonitorDevicePath = devicePath,
            FriendlyName = string.IsNullOrWhiteSpace(friendly) ? DescribeFromPath(devicePath) : friendly,
            IsActive = path.IsActive,
            TargetAvailable = path.targetInfo.targetAvailable != 0,
            RefreshRate = path.targetInfo.refreshRate,
            Rotation = path.targetInfo.rotation == 0 ? 1 : path.targetInfo.rotation,
            Scaling = path.targetInfo.scaling == 0 ? 1 : path.targetInfo.scaling,
            OutputTechnology = path.targetInfo.outputTechnology
        };

        if (path.IsActive)
        {
            entry.GdiDeviceName = GetSourceName(path.sourceInfo.adapterId, path.sourceInfo.id);
        }

        uint srcIdx = path.sourceInfo.modeInfoIdx;
        if (srcIdx != Ccd.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && srcIdx < topo.Modes.Length &&
            topo.Modes[srcIdx].infoType == Ccd.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            var src = topo.Modes[srcIdx].modeInfo.sourceMode;
            entry.HasSourceMode = true;
            entry.SourceMode = src;
            entry.X = src.position.x;
            entry.Y = src.position.y;
            entry.Width = (int)src.width;
            entry.Height = (int)src.height;
        }

        uint tgtIdx = path.targetInfo.modeInfoIdx;
        if (tgtIdx != Ccd.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && tgtIdx < topo.Modes.Length &&
            topo.Modes[tgtIdx].infoType == Ccd.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
        {
            entry.HasTargetMode = true;
            entry.TargetMode = topo.Modes[tgtIdx].modeInfo.targetMode;
        }

        return entry;
    }

    private static string GetTargetName(LUID adapterId, uint targetId, out string friendlyName)
    {
        friendlyName = string.Empty;
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Ccd.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty
        };

        if (CcdNative.DisplayConfigGetDeviceInfo(ref request) != Ccd.ERROR_SUCCESS) return string.Empty;
        friendlyName = request.monitorFriendlyDeviceName ?? string.Empty;
        return request.monitorDevicePath ?? string.Empty;
    }

    private static string GetSourceName(LUID adapterId, uint sourceId)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Ccd.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId
            },
            viewGdiDeviceName = string.Empty
        };

        return CcdNative.DisplayConfigGetDeviceInfo(ref request) == Ccd.ERROR_SUCCESS
            ? request.viewGdiDeviceName ?? string.Empty
            : string.Empty;
    }

    /// <summary>Last-resort label when a monitor reports no friendly name (common for
    /// generic/no-EDID panels): pull the model token out of the device path.</summary>
    private static string DescribeFromPath(string devicePath)
    {
        // \\?\DISPLAY#DELF13D#5&2b7bf8c&0&UID4352#{guid}  ->  DELF13D
        var parts = devicePath.Split('#');
        return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "Display";
    }

    /// <summary>Whole-string identity comparison. There is deliberately no parsing here.</summary>
    public static bool SameMonitor(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------- capture

    /// <summary>
    /// Captures the live topology into profile targets. Every field needed to
    /// reproduce this exact arrangement is stored, including the full target video
    /// signal info — that is what lets a disabled monitor be re-enabled at its real
    /// mode in a single call, without having to wake it first to discover its modes.
    /// </summary>
    public static List<DisplayTargetConfig> Capture()
    {
        var topo = Query(includeInactive: true);
        var result = new List<DisplayTargetConfig>();

        // One entry per physical monitor, preferring its active path.
        foreach (var group in topo.Entries.GroupBy(e => e.MonitorDevicePath, StringComparer.OrdinalIgnoreCase))
        {
            var entry = group.FirstOrDefault(e => e.IsActive) ?? group.First();
            result.Add(ToTarget(entry));
        }

        return result;
    }

    public static DisplayTargetConfig ToTarget(CcdPath entry)
    {
        var signal = entry.TargetMode.targetVideoSignalInfo;
        return new DisplayTargetConfig
        {
            MonitorDevicePath = entry.MonitorDevicePath,
            MonitorId = entry.FriendlyName,
            DeviceName = entry.GdiDeviceName,
            HardwareId = entry.MonitorDevicePath,   // legacy field, kept in sync for old UI bindings
            Enabled = entry.IsActive,
            X = entry.X,
            Y = entry.Y,
            Width = entry.Width,
            Height = entry.Height,
            RefreshRate = (int)Math.Round(entry.RefreshRate.AsHz),
            IsPrimary = entry.IsPrimary,

            RefreshNumerator = entry.RefreshRate.Numerator,
            RefreshDenominator = entry.RefreshRate.Denominator,
            Rotation = entry.Rotation,
            Scaling = entry.Scaling,
            OutputTechnology = entry.OutputTechnology,

            HasTargetMode = entry.HasTargetMode,
            PixelRate = signal.pixelRate,
            HSyncNumerator = signal.hSyncFreq.Numerator,
            HSyncDenominator = signal.hSyncFreq.Denominator,
            VSyncNumerator = signal.vSyncFreq.Numerator,
            VSyncDenominator = signal.vSyncFreq.Denominator,
            ActiveCx = signal.activeSize.cx,
            ActiveCy = signal.activeSize.cy,
            TotalCx = signal.totalSize.cx,
            TotalCy = signal.totalSize.cy,
            VideoStandard = signal.videoStandard,
            ScanLineOrdering = signal.scanLineOrdering,
            PixelFormat = entry.HasSourceMode ? entry.SourceMode.pixelFormat : Ccd.DISPLAYCONFIG_PIXELFORMAT_32BPP
        };
    }

    // ---------------------------------------------------------------- apply

    public static CcdResult Apply(DisplayProfile profile, bool validateOnly = false)
    {
        var wanted = profile.Displays.Where(d => d.Enabled).ToList();
        if (wanted.Count == 0)
            return CcdResult.Fail("A layout must keep at least one monitor enabled. No changes were made.");

        var topo = Query(includeInactive: true);
        if (topo.Entries.Count == 0)
            return CcdResult.Fail("Could not read the current display configuration.");

        // Resolve each enabled profile entry to a live path. A monitor usually has
        // several candidate paths; prefer the one already active, then any the
        // adapter reports as available.
        var options = new List<(DisplayTargetConfig Target, List<CcdPath> Candidates)>();
        var missing = new List<string>();

        foreach (var target in wanted)
        {
            string identity = IdentityOf(target);
            var candidates = topo.Entries
                .Where(e => SameMonitor(e.MonitorDevicePath, identity))
                .OrderByDescending(e => e.IsActive)
                .ThenByDescending(e => e.TargetAvailable)
                .ToList();

            if (candidates.Count == 0)
            {
                missing.Add(string.IsNullOrWhiteSpace(target.MonitorId) ? identity : target.MonitorId);
                continue;
            }

            options.Add((target, candidates));
        }

        if (missing.Count > 0)
        {
            return CcdResult.Fail(
                $"Cannot enable a monitor that is not connected: {string.Join(", ", missing.Distinct())}");
        }

        // THIS is what makes the desktop extend rather than clone. In CCD, "clone"
        // is not a mode you ask for — it is simply what you get when two active
        // paths share one source (same adapter + same sourceInfo.id): two monitors
        // showing one desktop surface. Extend means every active path owns a
        // distinct source.
        //
        // A monitor typically has several candidate paths, and the obvious choice —
        // take each monitor's first candidate independently — can easily hand two
        // monitors the same source id. That is exactly what happened when going from
        // left-only back to all three: the paths chosen for the re-enabled center and
        // right monitors collided on one source, so Windows cloned them.
        //
        // So pick paths jointly instead of independently, requiring distinct sources.
        // Candidates are ordered active-first, so an arrangement that is already
        // correct is preserved rather than reshuffled.
        var chosen = AssignDistinctSources(topo, options);
        if (chosen == null)
        {
            return CcdResult.Fail(
                "Your graphics adapter can't drive these monitors as separate desktops " +
                "(it has fewer independent display sources than the layout needs).");
        }

        NormalizeOrigin(chosen);

        // Tier 1 — supply the complete topology AND exact modes. One call, no
        // ambiguity, exact refresh rates and positions preserved.
        var attempt = BuildExact(topo, chosen);
        var result = Submit(attempt.Paths, attempt.Modes,
            Ccd.SDC_USE_SUPPLIED_DISPLAY_CONFIG, validateOnly, "exact");
        if (result.Ok) return result;

        // Tier 2 — same topology, but let Windows adjust modes it can't honour
        // verbatim (stale saved timings, a driver update, a different cable).
        attempt = BuildExact(topo, chosen);
        result = Submit(attempt.Paths, attempt.Modes,
            Ccd.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Ccd.SDC_ALLOW_CHANGES, validateOnly, "relaxed");
        if (result.Ok) return result;

        // Tier 3 — topology only: say which monitors are on and let Windows pick
        // every mode from its own per-topology database. Loses exact positions, so
        // a second exact pass follows once the displays are live and their real
        // modes are queryable.
        var topologyOnly = BuildTopologyOnly(topo, chosen);
        result = Submit(topologyOnly, null, Ccd.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Ccd.SDC_ALLOW_CHANGES,
            validateOnly, "topology-only");
        if (!result.Ok) return result;
        if (validateOnly) return result;

        // Re-run the exact pass now that everything is attached. Best-effort: the
        // requested monitors are already on, which is the part that matters, so a
        // failure here is not fatal.
        var refreshed = Query(includeInactive: true);
        var rechosen = Rechoose(refreshed, chosen);
        if (rechosen != null)
        {
            var second = BuildExact(refreshed, rechosen);
            Submit(second.Paths, second.Modes,
                Ccd.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Ccd.SDC_ALLOW_CHANGES, false, "exact-after-topology");
        }

        return CcdResult.Success("Applied (Windows chose display modes for one or more monitors).");
    }

    private static List<(DisplayTargetConfig, CcdPath)>? Rechoose(
        CcdTopology topo, List<(DisplayTargetConfig Target, CcdPath Path)> previous)
    {
        var result = new List<(DisplayTargetConfig, CcdPath)>();
        foreach (var (target, _) in previous)
        {
            var match = topo.Entries
                .Where(e => SameMonitor(e.MonitorDevicePath, IdentityOf(target)))
                .OrderByDescending(e => e.IsActive)
                .FirstOrDefault();
            if (match == null) return null;
            result.Add((target, match));
        }
        return result;
    }

    /// <summary>
    /// Identifies the desktop surface a path draws from. Two active paths sharing
    /// this key are, by definition, cloned.
    /// </summary>
    private static string SourceKey(CcdTopology topo, CcdPath entry)
    {
        var s = topo.Paths[entry.Index].sourceInfo;
        return $"{s.adapterId.HighPart:X8}{s.adapterId.LowPart:X8}:{s.id}";
    }

    /// <summary>
    /// Chooses one path per monitor such that no two share a source. Exhaustive
    /// backtracking — with a handful of monitors and a handful of paths each, the
    /// search is trivial, and unlike a greedy pass it never fails on an assignment
    /// that is actually possible.
    /// </summary>
    private static List<(DisplayTargetConfig Target, CcdPath Path)>? AssignDistinctSources(
        CcdTopology topo, List<(DisplayTargetConfig Target, List<CcdPath> Candidates)> options)
    {
        var chosen = new (DisplayTargetConfig Target, CcdPath Path)[options.Count];
        var usedSources = new HashSet<string>(StringComparer.Ordinal);
        var usedPaths = new HashSet<int>();

        bool Solve(int i)
        {
            if (i == options.Count) return true;

            foreach (var candidate in options[i].Candidates)
            {
                string key = SourceKey(topo, candidate);
                if (!usedPaths.Add(candidate.Index)) continue;
                if (!usedSources.Add(key))
                {
                    usedPaths.Remove(candidate.Index);
                    continue;
                }

                chosen[i] = (options[i].Target, candidate);
                if (Solve(i + 1)) return true;

                usedPaths.Remove(candidate.Index);
                usedSources.Remove(key);
            }

            return false;
        }

        return Solve(0) ? chosen.ToList() : null;
    }

    private static string IdentityOf(DisplayTargetConfig target) =>
        !string.IsNullOrWhiteSpace(target.MonitorDevicePath) ? target.MonitorDevicePath : target.HardwareId;

    /// <summary>
    /// Windows requires exactly one active source sitting at (0,0) — that source is
    /// the primary display. A layout dragged around in the editor can easily end up
    /// with no entry at the origin, which SetDisplayConfig rejects with
    /// ERROR_INVALID_PARAMETER and no further explanation. Translate the whole
    /// arrangement so the intended primary lands on the origin; relative geometry is
    /// unchanged.
    /// </summary>
    private static void NormalizeOrigin(List<(DisplayTargetConfig Target, CcdPath Path)> chosen)
    {
        var primary = chosen.FirstOrDefault(c => c.Target.IsPrimary).Target
                      ?? chosen.OrderBy(c => c.Target.X).ThenBy(c => c.Target.Y).First().Target;

        int dx = primary.X;
        int dy = primary.Y;
        if (dx == 0 && dy == 0) return;

        foreach (var (target, _) in chosen)
        {
            target.X -= dx;
            target.Y -= dy;
        }
    }

    private static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) BuildExact(
        CcdTopology topo, List<(DisplayTargetConfig Target, CcdPath Path)> chosen)
    {
        var paths = (DISPLAYCONFIG_PATH_INFO[])topo.Paths.Clone();
        var modes = new List<DISPLAYCONFIG_MODE_INFO>();
        var activeIndices = new HashSet<int>(chosen.Select(c => c.Path.Index));

        foreach (var (target, live) in chosen)
        {
            ref var path = ref paths[live.Index];

            var sourceMode = new DISPLAYCONFIG_MODE_INFO
            {
                infoType = Ccd.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
                id = path.sourceInfo.id,
                adapterId = path.sourceInfo.adapterId,
                modeInfo = new DISPLAYCONFIG_MODE_INFO_UNION
                {
                    sourceMode = new DISPLAYCONFIG_SOURCE_MODE
                    {
                        width = (uint)target.Width,
                        height = (uint)target.Height,
                        pixelFormat = target.PixelFormat == 0
                            ? Ccd.DISPLAYCONFIG_PIXELFORMAT_32BPP
                            : target.PixelFormat,
                        position = new POINTL { x = target.X, y = target.Y }
                    }
                }
            };

            int sourceIdx = modes.Count;
            modes.Add(sourceMode);

            // Target mode: the saved signal info if the profile carries one (captured
            // while this monitor was live, so the timings are real), otherwise the
            // live one, otherwise none at all — INVALID tells Windows to choose.
            int targetIdx = -1;
            if (target.HasTargetMode || live.HasTargetMode)
            {
                var signal = target.HasTargetMode
                    ? new DISPLAYCONFIG_VIDEO_SIGNAL_INFO
                    {
                        pixelRate = target.PixelRate,
                        hSyncFreq = new DISPLAYCONFIG_RATIONAL
                            { Numerator = target.HSyncNumerator, Denominator = target.HSyncDenominator },
                        vSyncFreq = new DISPLAYCONFIG_RATIONAL
                            { Numerator = target.VSyncNumerator, Denominator = target.VSyncDenominator },
                        activeSize = new DISPLAYCONFIG_2DREGION { cx = target.ActiveCx, cy = target.ActiveCy },
                        totalSize = new DISPLAYCONFIG_2DREGION { cx = target.TotalCx, cy = target.TotalCy },
                        videoStandard = target.VideoStandard,
                        scanLineOrdering = target.ScanLineOrdering
                    }
                    : live.TargetMode.targetVideoSignalInfo;

                targetIdx = modes.Count;
                modes.Add(new DISPLAYCONFIG_MODE_INFO
                {
                    infoType = Ccd.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
                    id = path.targetInfo.id,
                    adapterId = path.targetInfo.adapterId,
                    modeInfo = new DISPLAYCONFIG_MODE_INFO_UNION
                    {
                        targetMode = new DISPLAYCONFIG_TARGET_MODE { targetVideoSignalInfo = signal }
                    }
                });
            }

            path.flags |= Ccd.DISPLAYCONFIG_PATH_ACTIVE;
            path.sourceInfo.modeInfoIdx = (uint)sourceIdx;
            path.targetInfo.modeInfoIdx = targetIdx >= 0
                ? (uint)targetIdx
                : Ccd.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

            if (target.RefreshNumerator > 0 && target.RefreshDenominator > 0)
            {
                path.targetInfo.refreshRate = new DISPLAYCONFIG_RATIONAL
                {
                    Numerator = target.RefreshNumerator,
                    Denominator = target.RefreshDenominator
                };
            }

            if (target.Rotation > 0) path.targetInfo.rotation = target.Rotation;
            if (target.Scaling > 0) path.targetInfo.scaling = target.Scaling;
            path.targetInfo.scanLineOrdering = target.ScanLineOrdering;
        }

        Deactivate(paths, activeIndices);
        return (paths, modes.ToArray());
    }

    private static DISPLAYCONFIG_PATH_INFO[] BuildTopologyOnly(
        CcdTopology topo, List<(DisplayTargetConfig Target, CcdPath Path)> chosen)
    {
        var paths = (DISPLAYCONFIG_PATH_INFO[])topo.Paths.Clone();
        var activeIndices = new HashSet<int>(chosen.Select(c => c.Path.Index));

        foreach (int index in activeIndices)
        {
            ref var path = ref paths[index];
            path.flags |= Ccd.DISPLAYCONFIG_PATH_ACTIVE;
            path.sourceInfo.modeInfoIdx = Ccd.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            path.targetInfo.modeInfoIdx = Ccd.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        }

        Deactivate(paths, activeIndices);
        return paths;
    }

    /// <summary>
    /// Turns off every path not in the keep-set. This is the entire "disable a
    /// monitor" implementation: clear the active bit and invalidate its mode
    /// indices. Monitors omitted from the profile are turned off too, so applying a
    /// layout always produces exactly the requested topology.
    /// </summary>
    private static void Deactivate(DISPLAYCONFIG_PATH_INFO[] paths, HashSet<int> keep)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if (keep.Contains(i)) continue;
            paths[i].flags &= ~Ccd.DISPLAYCONFIG_PATH_ACTIVE;
            paths[i].sourceInfo.modeInfoIdx = Ccd.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            paths[i].targetInfo.modeInfoIdx = Ccd.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        }
    }

    private static CcdResult Submit(
        DISPLAYCONFIG_PATH_INFO[] paths,
        DISPLAYCONFIG_MODE_INFO[]? modes,
        uint baseFlags,
        bool validateOnly,
        string tier)
    {
        uint modeCount = modes == null ? 0u : (uint)modes.Length;

        int validate = CcdNative.SetDisplayConfig(
            (uint)paths.Length, paths, modeCount, modes, baseFlags | Ccd.SDC_VALIDATE);
        if (validate != Ccd.ERROR_SUCCESS)
            return CcdResult.Fail(Explain(validate), $"{tier} validate -> {ErrorName(validate)}");

        if (validateOnly)
            return CcdResult.Success($"Layout is valid ({tier}).");

        int apply = CcdNative.SetDisplayConfig(
            (uint)paths.Length, paths, modeCount, modes,
            baseFlags | Ccd.SDC_APPLY | Ccd.SDC_SAVE_TO_DATABASE);

        if (apply == Ccd.ERROR_ACCESS_DENIED)
        {
            // Something else is mid-change (a driver applying its own settings, a
            // hotplug being processed). One retry clears this in practice.
            Thread.Sleep(500);
            apply = CcdNative.SetDisplayConfig(
                (uint)paths.Length, paths, modeCount, modes,
                baseFlags | Ccd.SDC_APPLY | Ccd.SDC_SAVE_TO_DATABASE);
        }

        return apply == Ccd.ERROR_SUCCESS
            ? CcdResult.Success()
            : CcdResult.Fail(Explain(apply), $"{tier} apply -> {ErrorName(apply)}");
    }

    // ----------------------------------------------------------- diagnostics

    public static string Explain(int code) => code switch
    {
        Ccd.ERROR_INVALID_PARAMETER =>
            "Windows rejected this layout as invalid. Re-capture the profile from a working arrangement.",
        Ccd.ERROR_NOT_SUPPORTED =>
            "Your graphics adapter can't drive this combination of monitors and modes.",
        Ccd.ERROR_ACCESS_DENIED =>
            "Another program is changing the display configuration right now. Try again in a moment.",
        Ccd.ERROR_GEN_FAILURE =>
            "The display driver failed to apply this layout.",
        Ccd.ERROR_BADDB =>
            "Windows' display database is out of date for this monitor set. Re-capture the profile.",
        _ => $"Windows could not apply this layout (error {code})."
    };

    private static string ErrorName(int code) => code switch
    {
        Ccd.ERROR_ACCESS_DENIED => "ERROR_ACCESS_DENIED(5)",
        Ccd.ERROR_GEN_FAILURE => "ERROR_GEN_FAILURE(31)",
        Ccd.ERROR_NOT_SUPPORTED => "ERROR_NOT_SUPPORTED(50)",
        Ccd.ERROR_INVALID_PARAMETER => "ERROR_INVALID_PARAMETER(87)",
        Ccd.ERROR_INSUFFICIENT_BUFFER => "ERROR_INSUFFICIENT_BUFFER(122)",
        Ccd.ERROR_BADDB => "ERROR_BADDB(1009)",
        _ => $"error({code})"
    };

    public static string Dump()
    {
        var sb = new StringBuilder();
        var topo = Query(includeInactive: true);
        sb.AppendLine($"{topo.Paths.Length} path(s), {topo.Modes.Length} mode(s), {topo.Entries.Count} with a monitor attached.");
        sb.AppendLine();

        foreach (var group in topo.Entries.GroupBy(e => e.MonitorDevicePath, StringComparer.OrdinalIgnoreCase))
        {
            var best = group.FirstOrDefault(e => e.IsActive) ?? group.First();
            sb.AppendLine($"{best.FriendlyName}");
            sb.AppendLine($"  identity : {best.MonitorDevicePath}");
            sb.AppendLine($"  state    : {(best.IsActive ? "ACTIVE" : "connected, disabled")}" +
                          $"{(best.IsPrimary ? " (PRIMARY)" : "")}");
            if (best.IsActive)
            {
                sb.AppendLine($"  mode     : {best.Width}x{best.Height} @ {best.RefreshRate.AsHz:F3} Hz " +
                              $"({best.RefreshRate.Numerator}/{best.RefreshRate.Denominator})");
                sb.AppendLine($"  position : ({best.X},{best.Y})   gdi: {best.GdiDeviceName}");
            }
            sb.AppendLine($"  paths    : {string.Join(", ", group.Select(e => $"#{e.Index}{(e.IsActive ? "*" : "")}{(e.TargetAvailable ? "" : " (unavailable)")}"))}");
            // Two ACTIVE monitors printing the same source are cloned, not extended.
            sb.AppendLine($"  source   : {SourceKey(topo, best)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
