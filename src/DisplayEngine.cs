using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MonitorLayoutSwitcher;

public class DisplayInfo
{
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceString { get; set; } = string.Empty;
    public string MonitorId { get; set; } = string.Empty; // EDID / Friendly name
    public string HardwareId { get; set; } = string.Empty; // Stable monitor PnP / EDID identity
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

internal class DisplayDeviceInfo
{
    public string DeviceName { get; set; } = string.Empty;
    public string MonitorId { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public bool Attached { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
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

    public static DisplayTargetConfig Clone(this DisplayTargetConfig d) => new()
    {
        DeviceName = d.DeviceName,
        MonitorId = d.MonitorId,
        HardwareId = d.HardwareId,
        RelativePosition = d.RelativePosition,
        Enabled = d.Enabled,
        X = d.X,
        Y = d.Y,
        Width = d.Width,
        Height = d.Height,
        RefreshRate = d.RefreshRate,
        IsPrimary = d.IsPrimary
    };
}

public class DisplayProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty; // e.g., "Ctrl+Alt+1"
    public List<DisplayTargetConfig> Displays { get; set; } = new();
}

public class DisplayTargetConfig
{
    public string DeviceName { get; set; } = string.Empty;
    public string MonitorId { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public string RelativePosition { get; set; } = string.Empty; // Left, Center, Right
    public bool Enabled { get; set; } = true;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
    public bool IsPrimary { get; set; }
}

public static class DisplayEngine
{
    private sealed record EnumeratedDisplay(string DeviceName, string MonitorId, string HardwareId);
    private sealed record ResolvedTarget(DisplayTargetConfig Target, DisplayInfo? Current, string? DeviceName);
    private sealed record ActiveConfigTarget(LUID AdapterId, uint Id, string FriendlyName, string DevicePath);

    #region Win32 Native Interop

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        IntPtr pathArray,
        ref uint numModeInfoArrayElements,
        IntPtr modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);


    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_TEST = 0x00000002;
    private const uint CDS_SET_PRIMARY = 0x00000010;
    // CDS_NORESET is only a valid flag when combined with CDS_UPDATEREGISTRY — Windows
    // rejects it alone with DISP_CHANGE_BADFLAGS ("Invalid display flags").
    private const uint CDS_NORESET = 0x10000000;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int DISPLAYCONFIG_PATH_INFO_SIZE = 72;
    private const int DISPLAYCONFIG_MODE_INFO_SIZE = 64;


    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int DISP_CHANGE_FAILED = -1;
    private const int DISP_CHANGE_BADMODE = -2;
    private const int DISP_CHANGE_NOTUPDATED = -3;
    private const int DISP_CHANGE_BADFLAGS = -4;
    private const int DISP_CHANGE_BADPARAM = -5;

    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_POSITION = 0x00000020;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    #endregion

    public static List<DisplayInfo> GetCurrentDisplays()
    {
        var list = new List<DisplayInfo>();
        var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

        uint devNum = 0;
        while (EnumDisplayDevices(null, devNum, ref dev, 0))
        {
            // Check if attached or available
            bool isAttached = (dev.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            bool isPrimary = (dev.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;

            // Query monitor info for friendly monitor name
            string monitorId = dev.DeviceString;
            string hardwareId = dev.DeviceID;
            var monDev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(dev.DeviceName, 0, ref monDev, 0))
            {
                if (!string.IsNullOrWhiteSpace(monDev.DeviceString))
                {
                    monitorId = monDev.DeviceString;
                }
                if (!string.IsNullOrWhiteSpace(monDev.DeviceID))
                {
                    hardwareId = monDev.DeviceID;
                }
            }

            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            int x = 0, y = 0, width = 0, height = 0, freq = 0;

            if (EnumDisplaySettings(dev.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
            {
                x = dm.dmPositionX;
                y = dm.dmPositionY;
                width = dm.dmPelsWidth;
                height = dm.dmPelsHeight;
                freq = dm.dmDisplayFrequency;
            }

            // Only include displays that are physically attached to the active desktop
            if (isAttached && width > 0 && height > 0)
            {
                list.Add(new DisplayInfo
                {
                    DeviceName = dev.DeviceName,
                    DeviceString = dev.DeviceString,
                    MonitorId = monitorId,
                    HardwareId = hardwareId,
                    IsAttached = true,
                    IsPrimary = isPrimary,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    RefreshRate = freq
                });
            }

            devNum++;
        }

        ApplyDisplayConfigIdentity(list);

        // Calculate relative position based on X coordinates of attached displays
        var attached = list.Where(d => d.IsAttached).OrderBy(d => d.X).ToList();
        if (attached.Count == 1)
        {
            attached[0].RelativePosition = "Left";
        }
        else if (attached.Count == 2)
        {
            attached[0].RelativePosition = "Left";
            attached[1].RelativePosition = "Right";
        }
        else if (attached.Count >= 3)
        {
            attached[0].RelativePosition = "Left";
            for (int i = 1; i < attached.Count - 1; i++)
            {
                attached[i].RelativePosition = "Center";
            }
            attached[^1].RelativePosition = "Right";
        }

        return list;
    }

    private static void ApplyDisplayConfigIdentity(List<DisplayInfo> displays)
    {
        var activeTargets = GetActiveDisplayConfigTargets();
        if (activeTargets.Count == 0) return;

        var unmatchedTargets = new List<ActiveConfigTarget>(activeTargets);
        foreach (var display in displays)
        {
            var match = unmatchedTargets.FirstOrDefault(target =>
                SameHardwareIdentity(display.HardwareId, target.DevicePath));
            if (match == null) continue;

            display.MonitorId = string.IsNullOrWhiteSpace(match.FriendlyName)
                ? display.MonitorId
                : match.FriendlyName;
            display.HardwareId = match.DevicePath;
            unmatchedTargets.Remove(match);
        }

        // If Windows exposes exactly one active path and the GDI name changed,
        // the one-to-one mapping is safe even when the legacy monitor name was generic.
        if (displays.Count == 1 && activeTargets.Count == 1)
        {
            var target = activeTargets[0];
            displays[0].MonitorId = string.IsNullOrWhiteSpace(target.FriendlyName)
                ? displays[0].MonitorId
                : target.FriendlyName;
            displays[0].HardwareId = target.DevicePath;
        }
    }

    private static List<ActiveConfigTarget> GetActiveDisplayConfigTargets()
    {
        var result = new List<ActiveConfigTarget>();
        try
        {
            int status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (status != 0 || pathCount == 0) return result;

            while (true)
            {
                IntPtr paths = Marshal.AllocHGlobal(checked((int)(pathCount * DISPLAYCONFIG_PATH_INFO_SIZE)));
                IntPtr modes = Marshal.AllocHGlobal(checked((int)(Math.Max(1, modeCount) * DISPLAYCONFIG_MODE_INFO_SIZE)));
                try
                {
                    uint pathsToQuery = pathCount;
                    uint modesToQuery = modeCount;
                    status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathsToQuery, paths, ref modesToQuery, modes, IntPtr.Zero);
                    if (status == ERROR_INSUFFICIENT_BUFFER)
                    {
                        GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
                        continue;
                    }
                    if (status != 0) return result;

                    for (int i = 0; i < pathsToQuery; i++)
                    {
                        IntPtr path = IntPtr.Add(paths, checked(i * DISPLAYCONFIG_PATH_INFO_SIZE));
                        var adapterId = Marshal.PtrToStructure<LUID>(IntPtr.Add(path, 20));
                        uint targetId = (uint)Marshal.ReadInt32(path, 28);
                        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                        {
                            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                            {
                                Type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                                AdapterId = adapterId,
                                Id = targetId
                            },
                            MonitorFriendlyDeviceName = string.Empty,
                            MonitorDevicePath = string.Empty
                        };

                        if (DisplayConfigGetDeviceInfo(ref request) == 0)
                        {
                            result.Add(new ActiveConfigTarget(
                                adapterId,
                                targetId,
                                request.MonitorFriendlyDeviceName ?? string.Empty,
                                request.MonitorDevicePath ?? string.Empty));
                        }
                    }
                    return result;
                }
                finally
                {
                    Marshal.FreeHGlobal(paths);
                    Marshal.FreeHGlobal(modes);
                }
            }
        }
        catch
        {
            return result;
        }
    }

    internal static bool SameHardwareIdentity(string first, string second)
    {
        string firstKey = ExtractMonitorKey(first);
        string secondKey = ExtractMonitorKey(second);
        return !string.IsNullOrWhiteSpace(firstKey) &&
               string.Equals(firstKey, secondKey, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SameMonitorDescription(string first, string second)
    {
        string firstKey = NormalizeMonitorDescription(first);
        string secondKey = NormalizeMonitorDescription(second);
        return !string.IsNullOrWhiteSpace(firstKey) &&
               string.Equals(firstKey, secondKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMonitorDescription(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string normalized = value.Trim();
        int connectorSuffix = normalized.IndexOf(" (", StringComparison.Ordinal);
        return connectorSuffix > 0 ? normalized[..connectorSuffix].Trim() : normalized;
    }

    private static string ExtractMonitorKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string normalized = value.Replace('/', '\\');

        int start = normalized.IndexOf("MONITOR\\", StringComparison.OrdinalIgnoreCase);
        char separator = '\\';
        if (start >= 0)
        {
            start += "MONITOR\\".Length;
        }
        else
        {
            start = normalized.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                start += "DISPLAY#".Length;
                separator = '#';
            }
        }
        if (start < 0) return string.Empty;

        string remainder = normalized[start..];

        // Prefer the UID#### instance token when present. It's the modern
        // DISPLAYCONFIG MonitorDevicePath id's instance marker, and some legacy
        // EnumDisplayDevices ids carry the same token — when they do, stopping right
        // after it (dropping a trailing "_0" output-index or "#{class-GUID}" suffix,
        // whichever the format has) lets a legacy id and a modern id for the very same
        // physical monitor produce identical keys, which ApplyDisplayConfigIdentity
        // relies on to compare the two directly.
        var uid = Regex.Match(remainder, "UID[0-9]+", RegexOptions.IgnoreCase);
        if (uid.Success)
        {
            return remainder[..(uid.Index + uid.Length)].Replace('#', '\\');
        }

        // Otherwise this is the "PnP device instance id" style EnumDisplayDevices
        // actually returns on many systems — e.g.
        // "MONITOR\DELF13D\{4d36e96e-e325-11ce-bfc1-08002be10318}\0002" — where there
        // is no UID token at all, only a shared device-interface-class GUID segment
        // sitting between the model and the trailing per-instance number. Keep every
        // segment except that GUID, in particular the trailing instance number.
        // Stopping at the first separator (the old behavior) kept only the model name,
        // so two monitors of the same or similar model collapsed onto the same
        // "identity" and could be matched interchangeably.
        var keySegments = remainder
            .Split(separator)
            .Where(segment => !(segment.StartsWith('{') && segment.EndsWith('}')));
        return string.Join('\\', keySegments);
    }

    // Looks up a mode straight from the driver's own advertised list (EnumDisplaySettings
    // with an increasing mode index) rather than trusting "current" or "registry"
    // settings, which can be stale or invalid for a display that is currently disabled.
    // Prefers an exact width/height/refresh-rate match; falls back to the first mode at
    // the requested resolution (any refresh rate) if the exact rate isn't listed, since
    // saved refresh rates can drift slightly between driver versions.
    private static bool TryFindSupportedMode(string deviceName, int width, int height, int refreshRate, out DEVMODE mode)
    {
        DEVMODE? bestMatch = null;
        int modeNum = 0;
        while (true)
        {
            var candidate = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(deviceName, modeNum, ref candidate)) break;

            if (candidate.dmPelsWidth == width && candidate.dmPelsHeight == height)
            {
                if (refreshRate <= 0 || candidate.dmDisplayFrequency == refreshRate)
                {
                    mode = candidate;
                    return true;
                }
                bestMatch ??= candidate;
            }

            modeNum++;
        }

        if (bestMatch.HasValue)
        {
            mode = bestMatch.Value;
            return true;
        }

        mode = default;
        return false;
    }

    // Picks the highest-resolution (then highest-refresh) mode a device currently
    // advertises, with no target in mind. Used to "wake" a currently-disabled display
    // at whatever it offers in that state — its true native mode list (e.g. 4K/144Hz)
    // was observed to only become available via EnumDisplaySettings once the display
    // is actually attached, capped at a much lower generic list (e.g. 2560x1440)
    // while disabled.
    private static bool TryFindBestAvailableMode(string deviceName, out DEVMODE mode)
    {
        DEVMODE? best = null;
        int modeNum = 0;
        while (true)
        {
            var candidate = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(deviceName, modeNum, ref candidate)) break;

            if (best == null ||
                candidate.dmPelsWidth * candidate.dmPelsHeight > best.Value.dmPelsWidth * best.Value.dmPelsHeight ||
                (candidate.dmPelsWidth * candidate.dmPelsHeight == best.Value.dmPelsWidth * best.Value.dmPelsHeight &&
                 candidate.dmDisplayFrequency > best.Value.dmDisplayFrequency))
            {
                best = candidate;
            }

            modeNum++;
        }

        if (best.HasValue)
        {
            mode = best.Value;
            return true;
        }

        mode = default;
        return false;
    }

    // Read-only diagnostic: prints exactly what ApplyProfile would resolve and send
    // for each target, without calling ChangeDisplaySettingsEx at all. Used to debug
    // target-to-device resolution and positioning issues without touching the live
    // display topology.
    public static void TraceApplyResolution(DisplayProfile profile)
    {
        var currentDisplays = GetCurrentDisplays();
        var enumeratedDevices = GetEnumeratedDevices();

        Console.WriteLine($"Current displays ({currentDisplays.Count}):");
        foreach (var d in currentDisplays)
        {
            Console.WriteLine($"  {d.DeviceName} | Adapter={d.DeviceString} | HardwareId={d.HardwareId} | MonitorId={d.MonitorId} | ({d.X},{d.Y}) {d.Width}x{d.Height}@{d.RefreshRate} Primary={d.IsPrimary}");
        }

        Console.WriteLine($"Enumerated devices ({enumeratedDevices.Count}):");
        foreach (var d in enumeratedDevices)
        {
            Console.WriteLine($"  {d.DeviceName} | HardwareId={d.HardwareId} | MonitorId={d.MonitorId}");
        }

        Console.WriteLine($"Profile '{profile.Name}' targets:");
        foreach (var target in profile.Displays)
        {
            DisplayInfo? current = null;
            if (!string.IsNullOrWhiteSpace(target.HardwareId))
            {
                current = currentDisplays.FirstOrDefault(d =>
                    SameHardwareIdentity(d.HardwareId, target.HardwareId));
            }
            current ??= currentDisplays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase));

            // Mirrors ApplyProfile's actual resolution logic exactly (kept in sync
            // deliberately — this trace is worthless as a debugging tool the moment it
            // diverges from what ApplyProfile really does).
            string resolutionPath;
            string? resolvedDeviceName = current?.DeviceName;
            if (resolvedDeviceName != null)
            {
                resolutionPath = "current (live) match";
            }
            else
            {
                resolvedDeviceName = enumeratedDevices.FirstOrDefault(d =>
                    string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))?.DeviceName;
                resolutionPath = resolvedDeviceName != null ? "literal saved DeviceName fallback" : "none";
            }
            EnumeratedDisplay? device = resolvedDeviceName != null
                ? new EnumeratedDisplay(resolvedDeviceName, string.Empty, string.Empty)
                : null;

            string sizeDescription;
            if (target.Width > 0 && target.Height > 0 && device != null &&
                TryFindSupportedMode(device.DeviceName, target.Width, target.Height, target.RefreshRate, out DEVMODE mode))
            {
                sizeDescription = $"{mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency} (from mode list)";
            }
            else
            {
                sizeDescription = $"{target.Width}x{target.Height}@{target.RefreshRate} (patched, no exact mode found)";
            }

            Console.WriteLine($"  Saved: DeviceName={target.DeviceName} HardwareId={target.HardwareId} MonitorId={target.MonitorId} Enabled={target.Enabled} Primary={target.IsPrimary} Pos=({target.X},{target.Y}) Size={target.Width}x{target.Height}@{target.RefreshRate}");
            Console.WriteLine($"    -> Resolved device: {device?.DeviceName ?? "NONE"} (via {resolutionPath})");
            if (target.Enabled)
            {
                bool alreadyMatches = current != null &&
                    current.X == target.X &&
                    current.Y == target.Y &&
                    current.Width == target.Width &&
                    current.Height == target.Height &&
                    current.IsPrimary == target.IsPrimary &&
                    (target.RefreshRate <= 0 || current.RefreshRate == target.RefreshRate);
                Console.WriteLine($"    -> Would send: Pos=({target.X},{target.Y}) Size={sizeDescription} Primary={target.IsPrimary} | SkipAsAlreadyMatching={alreadyMatches}" +
                    (current != null ? $" (current: Pos=({current.X},{current.Y}) {current.Width}x{current.Height}@{current.RefreshRate} Primary={current.IsPrimary})" : " (not currently attached)"));
            }
            else if (current != null)
            {
                Console.WriteLine($"    -> Would disable (currently: Pos=({current.X},{current.Y}) {current.Width}x{current.Height}@{current.RefreshRate} Primary={current.IsPrimary})");
            }
        }
    }

    // Narrow, real (mutating) diagnostic: tries disabling exactly one device, with a
    // few different flag combinations in isolation, to bisect whether CDS_NORESET
    // staging itself is the problem or the DEVMODE/flags are rejected regardless.
    // Restores the device's original mode after each attempt so it doesn't leave the
    // display off if a variant happens to fail.
    public static void TestDisableIsolated(string deviceName)
    {
        var before = GetCurrentDisplays().FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (before == null)
        {
            Console.WriteLine($"{deviceName} is not currently attached — nothing to test.");
            return;
        }
        Console.WriteLine($"Baseline: {deviceName} at ({before.X},{before.Y}) {before.Width}x{before.Height}@{before.RefreshRate} Primary={before.IsPrimary}");

        void Restore()
        {
            if (!TryFindSupportedMode(deviceName, before.Width, before.Height, before.RefreshRate, out DEVMODE rdm))
            {
                rdm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref rdm);
                rdm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT;
                rdm.dmPelsWidth = before.Width;
                rdm.dmPelsHeight = before.Height;
            }
            rdm.dmFields |= DM_POSITION;
            rdm.dmPositionX = before.X;
            rdm.dmPositionY = before.Y;
            uint rflags = (before.IsPrimary ? CDS_SET_PRIMARY : 0);
            int rres = ChangeDisplaySettingsEx(deviceName, ref rdm, IntPtr.Zero, rflags, IntPtr.Zero);
            ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            Console.WriteLine($"  Restore result: {rres}");
        }

        DEVMODE MakeDisableDm() => new()
        {
            dmSize = (short)Marshal.SizeOf<DEVMODE>(),
            dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION,
            dmPelsWidth = 0,
            dmPelsHeight = 0,
            dmPositionX = 0,
            dmPositionY = 0
        };

        // Variant A: immediate, no CDS_UPDATEREGISTRY, no CDS_NORESET.
        {
            var dm = MakeDisableDm();
            int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            Console.WriteLine($"Variant A (immediate, flags=0): {res}");
            if (res == DISP_CHANGE_SUCCESSFUL) Restore();
        }

        // Variant B: immediate, CDS_UPDATEREGISTRY only (no NORESET).
        {
            var dm = MakeDisableDm();
            int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            Console.WriteLine($"Variant B (immediate, CDS_UPDATEREGISTRY): {res}");
            if (res == DISP_CHANGE_SUCCESSFUL) Restore();
        }

        // Variant C: staged (CDS_UPDATEREGISTRY | CDS_NORESET) + explicit commit, alone.
        {
            var dm = MakeDisableDm();
            int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            Console.WriteLine($"Variant C (staged CDS_UPDATEREGISTRY|CDS_NORESET): {res}");
            if (res == DISP_CHANGE_SUCCESSFUL)
            {
                int commitRes = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                Console.WriteLine($"  Commit result: {commitRes}");
                Restore();
            }
        }

        // Variant D: staged CDS_NORESET only (no CDS_UPDATEREGISTRY) — expected to be
        // rejected as an invalid flag combination per documented API behavior.
        {
            var dm = MakeDisableDm();
            int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_NORESET, IntPtr.Zero);
            Console.WriteLine($"Variant D (staged CDS_NORESET only): {res}");
            if (res == DISP_CHANGE_SUCCESSFUL)
            {
                int commitRes = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                Console.WriteLine($"  Commit result: {commitRes}");
                Restore();
            }
        }
    }

    // Read-only diagnostic: dumps every mode EnumDisplaySettings reports for a device,
    // including while it's currently disabled, to see what the driver actually
    // advertises in that state.
    // Real (mutating), isolated diagnostic: repositions exactly one currently-attached
    // device to (x,y), keeping its current resolution/refresh, with no other device
    // touched and no staging — to isolate whether a specific position/device
    // combination is rejected on its own, independent of any multi-device batching.
    // Real (mutating), minimal diagnostic: claims primary for a device using its own
    // exact current settings, unchanged, plus only CDS_SET_PRIMARY — to isolate
    // whether the primary claim itself is what's silently no-op'd on this driver, or
    // whether it's something about the mode/position fields sent alongside it.
    public static void TestSetPrimaryMinimal(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
        {
            Console.WriteLine($"{deviceName}: could not read current settings.");
            return;
        }
        Console.WriteLine($"Current: {deviceName} at ({dm.dmPositionX},{dm.dmPositionY}) {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency} fields=0x{dm.dmFields:X}");

        int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_SET_PRIMARY, IntPtr.Zero);
        Console.WriteLine($"CDS_SET_PRIMARY only (unchanged devmode): result={res}");

        var after = GetCurrentDisplays();
        Console.WriteLine("State after: " + string.Join(" | ", after.Select(d => $"{d.DeviceName}@({d.X},{d.Y}) Primary={d.IsPrimary}")));
    }

    public static void TestReposition(string deviceName, int x, int y)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
        {
            Console.WriteLine($"{deviceName}: could not read current settings.");
            return;
        }
        Console.WriteLine($"Current: {deviceName} at ({dm.dmPositionX},{dm.dmPositionY}) {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}");

        dm.dmFields |= DM_POSITION;
        dm.dmPositionX = x;
        dm.dmPositionY = y;
        int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
        Console.WriteLine($"Immediate reposition to ({x},{y}): result={res}");
    }

    public static void ListModes(string deviceName)
    {
        int modeNum = 0;
        int count = 0;
        while (true)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(deviceName, modeNum, ref dm)) break;
            Console.WriteLine($"  [{modeNum}] {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency} bpp={dm.dmBitsPerPel} fields=0x{dm.dmFields:X}");
            modeNum++;
            count++;
        }
        Console.WriteLine($"{deviceName}: {count} modes total.");

        var current = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        bool curOk = EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref current);
        Console.WriteLine($"ENUM_CURRENT_SETTINGS: ok={curOk} {current.dmPelsWidth}x{current.dmPelsHeight}@{current.dmDisplayFrequency} bpp={current.dmBitsPerPel}");

        var registry = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        bool regOk = EnumDisplaySettings(deviceName, -2, ref registry);
        Console.WriteLine($"ENUM_REGISTRY_SETTINGS: ok={regOk} {registry.dmPelsWidth}x{registry.dmPelsHeight}@{registry.dmDisplayFrequency} bpp={registry.dmBitsPerPel}");
    }

    public static bool ApplyProfile(DisplayProfile profile, out string errorMessage)
    {
        errorMessage = string.Empty;
        var currentDisplays = GetCurrentDisplays();

        var enabledTargets = profile.Displays.Where(d => d.Enabled).ToList();
        if (enabledTargets.Count == 0)
        {
            errorMessage = "A layout must keep at least one monitor enabled. No changes were made.";
            return false;
        }

        // Deliberately not gating on ValidateProfile here: it CDS_TEST-checks each
        // display one at a time against the *current* topology, which can reject a
        // fully valid target layout when re-enabling a currently-disabled display —
        // Windows doesn't reliably validate "attach this detached output" as an
        // isolated single-device test the way it does an already-attached display's
        // mode change. The staged apply below (CDS_NORESET per device + one commit)
        // is the real, authoritative check: each step's own DISP_CHANGE_SUCCESSFUL
        // check already reports a specific, actionable error if something is actually
        // wrong, without the false negatives this pre-check produced.
        var enumeratedDevices = GetEnumeratedDevices();

        // Resolve the current Windows display name from the stable PnP identity
        // before calling ChangeDisplaySettingsEx. DISPLAYn can be renumbered when
        // a monitor is disabled.
        var targets = profile.Displays
            .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName))
            .Select(target =>
            {
                DisplayInfo? current = null;
                if (!string.IsNullOrWhiteSpace(target.HardwareId))
                {
                    current = currentDisplays.FirstOrDefault(d =>
                        SameHardwareIdentity(d.HardwareId, target.HardwareId));
                }
                current ??= currentDisplays.FirstOrDefault(d =>
                    string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase));

                // If the target is currently live, its device name is already known —
                // it came from this same live query, so trust it directly. Only fall
                // back to the full enumerated device list (which also covers
                // disabled/detached adapters) when the target isn't currently
                // attached, and even then prefer the literal saved DeviceName over a
                // HardwareId lookup there: EnumDisplayDevices' per-adapter "attached
                // monitor" sub-query was observed to become unreliable once several
                // outputs are simultaneously disabled — returning the same stale
                // monitor identity for multiple different DISPLAYn adapters — which
                // can bind two different profile targets to the same physical output.
                // DISPLAYn itself was not observed to get renumbered by simply
                // enabling/disabling the same physical monitors, so the literal name
                // is the more reliable choice specifically for this fallback.
                string? deviceName = current?.DeviceName ?? enumeratedDevices.FirstOrDefault(d =>
                    string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))?.DeviceName;

                return new ResolvedTarget(target, current, deviceName);
            })
            .ToList();

        // A profile may have been created before a monitor was discovered. Treat
        // any currently active monitor omitted from that profile as disabled so
        // applying a profile always produces the requested topology.
        foreach (var current in currentDisplays)
        {
            if (targets.Any(entry =>
                    string.Equals(entry.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                    ReferenceEquals(entry.Current, current)))
            {
                continue;
            }

            targets.Add(new ResolvedTarget(new DisplayTargetConfig
            {
                DeviceName = current.DeviceName,
                MonitorId = current.MonitorId,
                HardwareId = current.HardwareId,
                RelativePosition = current.RelativePosition,
                Enabled = false,
                X = current.X,
                Y = current.Y,
                Width = current.Width,
                Height = current.Height,
                RefreshRate = current.RefreshRate,
                IsPrimary = false
            }, current, current.DeviceName));
        }

        if (enabledTargets.Any(target => targets.FirstOrDefault(entry => ReferenceEquals(entry.Target, target))?.DeviceName == null))
        {
            var missing = enabledTargets
                .Where(target => targets.FirstOrDefault(entry => ReferenceEquals(entry.Target, target))?.DeviceName == null)
                .Select(target => string.IsNullOrWhiteSpace(target.MonitorId) ? target.DeviceName : target.MonitorId)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            errorMessage = $"Cannot enable a monitor that is not currently connected: {string.Join(", ", missing)}";
            return false;
        }

        // 1. Wake any currently-disabled target whose saved resolution isn't in the
        // limited mode list a disabled display advertises (see TryFindBestAvailableMode
        // above), by enabling it immediately at whatever its best available mode is.
        // EnumDisplaySettings was observed to report the display's true native mode
        // list (e.g. 4K/144Hz) only once it's actually attached — while disabled it's
        // capped at a much lower generic list — so a display that needs a resolution
        // higher than that generic cap has to be woken at a reachable mode first
        // before the real target mode becomes queryable at all.
        //
        // Woken at a safe position well clear of every currently-live display, NOT its
        // final target position: this call is immediate (flags=0, not staged), and
        // another display can easily still be really sitting at that final position
        // right now (it may not move away until later, in the same staged batch below)
        // — placing two displays at the same spot via an immediate call was observed
        // to be rejected outright. The main staged loop below moves it to its true
        // final position as part of the single coordinated commit, which is exactly
        // what staging is for.
        if (targets.Any(e => e.Target.Enabled && e.Current == null))
        {
            int safeX = currentDisplays.Count > 0 ? currentDisplays.Max(d => d.X + d.Width) + 4096 : 0;
            foreach (var entry in targets.Where(e => e.Target.Enabled && e.Current == null))
            {
                var wakeTarget = entry.Target;
                string wakeDeviceName = entry.DeviceName!;
                if (wakeTarget.Width > 0 && wakeTarget.Height > 0 &&
                    TryFindSupportedMode(wakeDeviceName, wakeTarget.Width, wakeTarget.Height, wakeTarget.RefreshRate, out _))
                {
                    continue; // the true target mode is already reachable; nothing to wake.
                }

                if (!TryFindBestAvailableMode(wakeDeviceName, out DEVMODE bestMode)) continue;

                // A bare DEVMODE carrying only the fields we actually mean to set —
                // not the borrowed dmDisplayFlags/dmDisplayOrientation/dmBitsPerPel
                // that came along with the enumerated candidate, which weren't
                // observed to combine safely when actually committed.
                var wakeMode = new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf<DEVMODE>(),
                    dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION,
                    dmPelsWidth = bestMode.dmPelsWidth,
                    dmPelsHeight = bestMode.dmPelsHeight,
                    dmDisplayFrequency = bestMode.dmDisplayFrequency,
                    dmPositionX = safeX,
                    dmPositionY = 0
                };
                ChangeDisplaySettingsEx(wakeDeviceName, ref wakeMode, IntPtr.Zero, 0, IntPtr.Zero);
                safeX += wakeMode.dmPelsWidth;
            }
        }

        // 2a. If this profile hands primary off to a display that isn't already
        // primary, claim it first — deliberately WITHOUT specifying a position.
        // CDS_SET_PRIMARY is documented to shift the entire virtual desktop so the
        // newly-primary display lands at (0,0), recalculating every other display's
        // coordinates automatically. Fighting that by also asking for an explicit
        // (and usually different) position in the very same call was rejected
        // outright whenever the outgoing primary was still really sitting at (0,0) —
        // and moving the outgoing primary away first, before anyone else was already
        // in place to take over, was rejected too. Letting Windows do its own
        // coordinate shift sidesteps both: every display (including this one) is
        // moved to its true final coordinates in the position-only pass below, once
        // primary is no longer in question for anybody.
        var futurePrimaryEntry = targets.FirstOrDefault(e => e.Target.Enabled && e.Target.IsPrimary);
        if (futurePrimaryEntry != null && futurePrimaryEntry.Current?.IsPrimary != true)
        {
            string primaryDeviceName = futurePrimaryEntry.DeviceName!;
            var primaryTarget = futurePrimaryEntry.Target;
            DEVMODE primaryDm;
            if (!(primaryTarget.Width > 0 && primaryTarget.Height > 0 &&
                  TryFindSupportedMode(primaryDeviceName, primaryTarget.Width, primaryTarget.Height, primaryTarget.RefreshRate, out primaryDm)))
            {
                primaryDm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(primaryDeviceName, ENUM_CURRENT_SETTINGS, ref primaryDm))
                {
                    EnumDisplaySettings(primaryDeviceName, -2 /* ENUM_REGISTRY_SETTINGS */, ref primaryDm);
                }

                primaryDm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT;
                if (primaryTarget.Width > 0) primaryDm.dmPelsWidth = primaryTarget.Width;
                if (primaryTarget.Height > 0) primaryDm.dmPelsHeight = primaryTarget.Height;
                if (primaryTarget.RefreshRate > 0)
                {
                    primaryDm.dmFields |= DM_DISPLAYFREQUENCY;
                    primaryDm.dmDisplayFrequency = primaryTarget.RefreshRate;
                }
            }

            int primaryRes = ChangeDisplaySettingsEx(primaryDeviceName, ref primaryDm, IntPtr.Zero, CDS_SET_PRIMARY, IntPtr.Zero);
            Console.WriteLine($"[primary] {primaryDeviceName} claim: fields=0x{primaryDm.dmFields:X} {primaryDm.dmPelsWidth}x{primaryDm.dmPelsHeight}@{primaryDm.dmDisplayFrequency} result={primaryRes}");
            if (primaryRes != DISP_CHANGE_SUCCESSFUL)
            {
                errorMessage = $"Failed to make display {primaryDeviceName} primary (Code: {primaryRes})";
                return false;
            }

            // Verify it actually took: a call reporting success here was observed to
            // sometimes leave the previous primary in place anyway (Windows silently
            // auto-arranging the requested device elsewhere instead), asymmetrically
            // depending on which two specific displays were trading primary status.
            // Retry once with the explicit final target position included — the
            // combination that failed outright in the opposite handoff direction
            // (claiming (0,0) while the outgoing primary was still really there)
            // was, empirically, exactly what this direction needed instead.
            var afterPrimaryCheck = GetCurrentDisplays();
            Console.WriteLine("[primary] state after claim: " + string.Join(" | ", afterPrimaryCheck.Select(d => $"{d.DeviceName}@({d.X},{d.Y}) Primary={d.IsPrimary}")));
            bool becamePrimary = afterPrimaryCheck.FirstOrDefault(d =>
                string.Equals(d.DeviceName, primaryDeviceName, StringComparison.OrdinalIgnoreCase))?.IsPrimary == true;
            if (!becamePrimary)
            {
                primaryDm.dmFields |= DM_POSITION;
                primaryDm.dmPositionX = primaryTarget.X;
                primaryDm.dmPositionY = primaryTarget.Y;
                int retryRes = ChangeDisplaySettingsEx(primaryDeviceName, ref primaryDm, IntPtr.Zero, CDS_SET_PRIMARY, IntPtr.Zero);
                Console.WriteLine($"[primary] retry with position ({primaryTarget.X},{primaryTarget.Y}): result={retryRes}");
                if (retryRes != DISP_CHANGE_SUCCESSFUL)
                {
                    errorMessage = $"Failed to make display {primaryDeviceName} primary (Code: {retryRes})";
                    return false;
                }
                var afterRetryCheck = GetCurrentDisplays();
                Console.WriteLine("[primary] state after retry: " + string.Join(" | ", afterRetryCheck.Select(d => $"{d.DeviceName}@({d.X},{d.Y}) Primary={d.IsPrimary}")));
            }
        }

        // 2b. Move every enabled display to its true final position (and resolution,
        // if the primary claim above didn't already take care of it). Primary is
        // already settled by this point for everyone, so no CDS_SET_PRIMARY here —
        // these are now plain, uncontested repositions.
        foreach (var entry in targets.Where(entry => entry.Target.Enabled))
        {
            var target = entry.Target;
            string deviceName = entry.DeviceName!;

            // If this device is already exactly where the target wants it (same
            // position, size, refresh rate, and primary status), don't ask Windows to
            // "change" it at all. Re-asserting an unchanged mode — especially
            // CDS_SET_PRIMARY on a display that's already primary — is a known rough
            // edge of this API: some drivers reject that as an invalid no-op
            // (DISP_CHANGE_FAILED) even though nothing is actually wrong. This showed
            // up concretely as "Failed to configure display X at (0,0) 3840x2160
            // (Code: -1)" for a display that was already at (0,0) 3840x2160.
            if (entry.Current != null &&
                entry.Current.X == target.X &&
                entry.Current.Y == target.Y &&
                entry.Current.Width == target.Width &&
                entry.Current.Height == target.Height &&
                entry.Current.IsPrimary == target.IsPrimary &&
                (target.RefreshRate <= 0 || entry.Current.RefreshRate == target.RefreshRate))
            {
                continue;
            }

            DEVMODE dm;

            // Prefer a mode straight from the driver's own advertised mode list over
            // patching width/height/frequency onto a DEVMODE seeded from "current" or
            // "registry" settings. For a display that is currently disabled (the exact
            // case when re-enabling one), the current/registry DEVMODE can carry stale
            // or mismatched fields (bit depth, display flags, etc.) left over from
            // whatever state it was in when last turned off — patching only a few
            // fields onto that can produce a field combination the driver doesn't
            // actually support, which Windows rejects with DISP_CHANGE_BADMODE
            // ("that resolution is not supported") even though the resolution itself
            // is perfectly valid for that monitor.
            if (!(target.Width > 0 && target.Height > 0 &&
                  TryFindSupportedMode(deviceName, target.Width, target.Height, target.RefreshRate, out dm)))
            {
                dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
                {
                    EnumDisplaySettings(deviceName, -2 /* ENUM_REGISTRY_SETTINGS */, ref dm);
                }

                dm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT;
                if (target.Width > 0) dm.dmPelsWidth = target.Width;
                if (target.Height > 0) dm.dmPelsHeight = target.Height;

                if (target.RefreshRate > 0)
                {
                    dm.dmFields |= DM_DISPLAYFREQUENCY;
                    dm.dmDisplayFrequency = target.RefreshRate;
                }
            }

            dm.dmFields |= DM_POSITION;
            dm.dmPositionX = target.X;
            dm.dmPositionY = target.Y;

            int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (res != DISP_CHANGE_SUCCESSFUL)
            {
                errorMessage = $"Failed to configure display {deviceName} at ({target.X},{target.Y}) {target.Width}x{target.Height} (Code: {res})";
                return false;
            }
        }

        // 3. Detach disabled displays LAST, applied immediately with flags=0 — no
        // CDS_UPDATEREGISTRY, no CDS_NORESET staging. Empirically, on this driver, a
        // detach (0x0 DEVMODE) is rejected with DISP_CHANGE_FAILED whenever
        // CDS_UPDATEREGISTRY is present, even for a perfectly ordinary,
        // currently-attached display — flags=0 is the only combination that succeeds.
        foreach (var entry in targets.Where(entry => !entry.Target.Enabled && entry.Current != null))
        {
            string deviceName = entry.DeviceName!;
            var dm = new DEVMODE
            {
                dmSize = (short)Marshal.SizeOf<DEVMODE>(),
                dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION,
                dmPelsWidth = 0,
                dmPelsHeight = 0,
                dmPositionX = 0,
                dmPositionY = 0
            };

            int disableRes = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (disableRes != DISP_CHANGE_SUCCESSFUL)
            {
                errorMessage = $"Failed to disable display {deviceName} (Code: {disableRes})";
                return false;
            }
        }

        return true;
    }

    // Best-effort, non-mutating dry run used only by the --test-apply CLI diagnostic.
    // It CDS_TEST-checks each enabled display one at a time against the *current*
    // topology, so it can report a false failure for a target layout that re-enables
    // a currently-disabled display (Windows doesn't reliably validate "attach this
    // detached output" as an isolated single-device test). ApplyProfile does NOT call
    // this — its own staged apply is the authoritative check.
    public static bool ValidateProfile(DisplayProfile profile, out string errorMessage)
    {
        errorMessage = string.Empty;
        var enabledTargets = profile.Displays.Where(d => d.Enabled).ToList();
        if (enabledTargets.Count == 0)
        {
            errorMessage = "A layout must keep at least one monitor enabled.";
            return false;
        }

        var devices = GetEnumeratedDevices();
        foreach (var target in enabledTargets)
        {
            EnumeratedDisplay? device = null;
            if (!string.IsNullOrWhiteSpace(target.HardwareId))
            {
                var matches = devices.Where(d =>
                    SameHardwareIdentity(d.HardwareId, target.HardwareId)).ToList();
                if (matches.Count == 1) device = matches[0];
            }
            device ??= devices.FirstOrDefault(d =>
                string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                errorMessage = $"Monitor '{target.MonitorId}' is not currently available.";
                return false;
            }

            string deviceName = device.DeviceName;

            // See the matching comment in ApplyProfile: prefer a mode straight from the
            // driver's own advertised mode list over patching a DEVMODE seeded from
            // "current"/"registry" settings, which can be stale or invalid for a
            // display that is currently disabled and produce a false BADMODE rejection.
            if (!(target.Width > 0 && target.Height > 0 &&
                  TryFindSupportedMode(deviceName, target.Width, target.Height, target.RefreshRate, out DEVMODE dm)))
            {
                dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
                {
                    EnumDisplaySettings(deviceName, -2, ref dm);
                }

                dm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT;
                if (target.Width > 0) dm.dmPelsWidth = target.Width;
                if (target.Height > 0) dm.dmPelsHeight = target.Height;
                if (target.RefreshRate > 0)
                {
                    dm.dmFields |= DM_DISPLAYFREQUENCY;
                    dm.dmDisplayFrequency = target.RefreshRate;
                }
            }

            dm.dmFields |= DM_POSITION;
            dm.dmPositionX = target.X;
            dm.dmPositionY = target.Y;

            int result = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (result != DISP_CHANGE_SUCCESSFUL)
            {
                errorMessage = $"Windows rejected {deviceName} at {target.Width}x{target.Height} (Code: {result}).";
                return false;
            }
        }

        return true;
    }

    public static string FriendlyError(string raw)
    {
        if (raw.Contains("Code: -1")) return "Windows rejected that display mode. Capture the current layout, then apply again.";
        if (raw.Contains("Code: -2")) return "That resolution or refresh rate is not supported on this monitor.";
        if (raw.Contains("Code: -3")) return "Windows could not write the display settings.";
        if (raw.Contains("Code: -4")) return "Invalid display flags. Try applying again.";
        if (raw.Contains("Code: -5")) return "Invalid display parameters. Recapture this layout and retry.";
        return raw;
    }

    public static List<string> MonitorsThatWouldDisable(DisplayProfile profile)
    {
        var current = GetCurrentDisplays();
        var keepNames = profile.Displays
            .Where(d => d.Enabled)
            .Select(d => d.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return current
            .Where(d => !keepNames.Contains(d.DeviceName) &&
                        !profile.Displays.Any(target => target.Enabled &&
                            !string.IsNullOrWhiteSpace(target.HardwareId) &&
                            SameHardwareIdentity(target.HardwareId, d.HardwareId)))
            .Select(d => string.IsNullOrWhiteSpace(d.MonitorId) ? d.DeviceName : $"{d.RelativePosition} — {d.MonitorId}")
            .ToList();
    }

    public static bool MatchesCurrent(DisplayProfile profile, int slack = 96)
    {
        var current = GetCurrentDisplays();
        var enabled = profile.Displays.Where(d => d.Enabled).ToList();
        if (enabled.Count == 0 || enabled.Count != current.Count) return false;

        foreach (var target in enabled)
        {
            var hw = !string.IsNullOrWhiteSpace(target.HardwareId)
                ? current.FirstOrDefault(d => SameHardwareIdentity(d.HardwareId, target.HardwareId))
                : null;
            hw ??= current.FirstOrDefault(d =>
                string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (hw == null) return false;
            if (target.IsPrimary != hw.IsPrimary) return false;
            if (Math.Abs(hw.X - target.X) > slack || Math.Abs(hw.Y - target.Y) > slack) return false;
            if (target.Width > 0 && Math.Abs(hw.Width - target.Width) > 8) return false;
            if (target.Height > 0 && Math.Abs(hw.Height - target.Height) > 8) return false;
        }

        return current.All(hw => enabled.Any(t =>
            (!string.IsNullOrWhiteSpace(t.HardwareId) &&
             SameHardwareIdentity(t.HardwareId, hw.HardwareId)) ||
            string.Equals(t.DeviceName, hw.DeviceName, StringComparison.OrdinalIgnoreCase)));
    }

    public static DisplayProfile? FindMatchingProfile(IEnumerable<DisplayProfile> profiles) =>
        profiles.FirstOrDefault(profile => MatchesCurrent(profile));

    public static (bool Matched, string Summary) Verify(DisplayProfile profile)
    {
        var current = GetCurrentDisplays();
        var missing = profile.Displays
            .Where(d => d.Enabled && !IsAttached(d, current))
            .Select(d => d.MonitorId)
            .ToList();
        var stillOn = profile.Displays
            .Where(d => !d.Enabled && IsAttached(d, current))
            .Select(d => d.MonitorId)
            .ToList();

        if (missing.Count == 0 && stillOn.Count == 0 && MatchesCurrent(profile))
        {
            return (true, "Windows layout matches the profile.");
        }

        var parts = new List<string>();
        if (missing.Count > 0) parts.Add("not attached: " + string.Join(", ", missing));
        if (stillOn.Count > 0) parts.Add("still on: " + string.Join(", ", stillOn));
        if (parts.Count == 0) parts.Add("positions differ slightly from the saved profile");
        return (false, string.Join("; ", parts));
    }

    private static bool IsAttached(DisplayTargetConfig target, IEnumerable<DisplayInfo> current) =>
        current.Any(d =>
            (!string.IsNullOrWhiteSpace(target.HardwareId) &&
             SameHardwareIdentity(d.HardwareId, target.HardwareId)) ||
            string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase));

    private static List<EnumeratedDisplay> GetEnumeratedDevices()
    {
        var result = new List<EnumeratedDisplay>();
        var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        uint devNum = 0;
        while (EnumDisplayDevices(null, devNum, ref dev, 0))
        {
            if (!string.IsNullOrWhiteSpace(dev.DeviceName))
            {
                string monitorId = dev.DeviceString;
                string hardwareId = dev.DeviceID;
                var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (EnumDisplayDevices(dev.DeviceName, 0, ref monitor, 0))
                {
                    if (!string.IsNullOrWhiteSpace(monitor.DeviceString)) monitorId = monitor.DeviceString;
                    if (!string.IsNullOrWhiteSpace(monitor.DeviceID)) hardwareId = monitor.DeviceID;
                }
                result.Add(new EnumeratedDisplay(dev.DeviceName, monitorId, hardwareId));
            }

            devNum++;
            dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return result;
    }

    internal static List<DisplayDeviceInfo> GetAllDisplayDevices() =>
        GetEnumeratedDevices()
            .Select(d => new DisplayDeviceInfo
            {
                DeviceName = d.DeviceName,
                MonitorId = d.MonitorId,
                HardwareId = d.HardwareId,
                Attached = GetCurrentDisplay(d.DeviceName, d.HardwareId) != null,
                Width = GetCurrentDisplay(d.DeviceName, d.HardwareId)?.Width ?? 0,
                Height = GetCurrentDisplay(d.DeviceName, d.HardwareId)?.Height ?? 0,
                RefreshRate = GetCurrentDisplay(d.DeviceName, d.HardwareId)?.RefreshRate ?? 0
            })
            .ToList();

    private static DisplayInfo? GetCurrentDisplay(string deviceName, string hardwareId) =>
        GetCurrentDisplays().FirstOrDefault(d =>
            string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(hardwareId) &&
             SameHardwareIdentity(d.HardwareId, hardwareId)));

    public static DisplayProfile CaptureCurrentLayoutAsProfile(string profileName, string hotkey)
    {
        var displays = GetCurrentDisplays();
        var profile = new DisplayProfile
        {
            Name = profileName,
            Hotkey = hotkey,
            Displays = new List<DisplayTargetConfig>()
        };

        foreach (var d in displays)
        {
            profile.Displays.Add(new DisplayTargetConfig
            {
                DeviceName = d.DeviceName,
                MonitorId = d.MonitorId,
                HardwareId = d.HardwareId,
                RelativePosition = d.RelativePosition,
                Enabled = d.IsAttached,
                X = d.X,
                Y = d.Y,
                Width = d.Width,
                Height = d.Height,
                RefreshRate = d.RefreshRate,
                IsPrimary = d.IsPrimary
            });
        }

        return profile;
    }
}
