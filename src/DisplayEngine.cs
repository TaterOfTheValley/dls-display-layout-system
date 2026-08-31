using System.Runtime.InteropServices;

namespace MonitorLayoutSwitcher;

public class DisplayInfo
{
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceString { get; set; } = string.Empty;
    public string MonitorId { get; set; } = string.Empty; // EDID / Friendly name
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
    #region Win32 Native Interop

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_TEST = 0x00000002;
    private const uint CDS_RESET = 0x40000000;
    private const uint CDS_NORESET = 0x10000000;
    private const uint CDS_SET_PRIMARY = 0x00000010;

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
            var monDev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(dev.DeviceName, 0, ref monDev, 0))
            {
                if (!string.IsNullOrWhiteSpace(monDev.DeviceString))
                {
                    monitorId = monDev.DeviceString;
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

            // Only include displays that are attached or have a valid negotiated resolution/mode
            if (isAttached || (width > 0 && height > 0))
            {
                list.Add(new DisplayInfo
                {
                    DeviceName = dev.DeviceName,
                    DeviceString = dev.DeviceString,
                    MonitorId = monitorId,
                    IsAttached = isAttached,
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

    public static bool ApplyProfile(DisplayProfile profile, out string errorMessage)
    {
        errorMessage = string.Empty;
        var currentDisplays = GetCurrentDisplays();

        // 1. Stage changes with CDS_NORESET | CDS_UPDATEREGISTRY
        foreach (var target in profile.Displays)
        {
            var matched = currentDisplays.FirstOrDefault(d => 
                string.Equals(d.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.RelativePosition, target.RelativePosition, StringComparison.OrdinalIgnoreCase));

            if (matched == null) continue;

            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

            if (!target.Enabled)
            {
                // Detach display
                dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION;
                dm.dmPelsWidth = 0;
                dm.dmPelsHeight = 0;
                dm.dmPositionX = 0;
                dm.dmPositionY = 0;

                int res = ChangeDisplaySettingsEx(matched.DeviceName, ref dm, IntPtr.Zero, CDS_NORESET | CDS_UPDATEREGISTRY, IntPtr.Zero);
                if (res != DISP_CHANGE_SUCCESSFUL)
                {
                    errorMessage = $"Failed to disable display {matched.DeviceName} (Code: {res})";
                    return false;
                }
            }
            else
            {
                // Attach / configure display
                dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION;
                dm.dmPelsWidth = target.Width > 0 ? target.Width : matched.Width;
                dm.dmPelsHeight = target.Height > 0 ? target.Height : matched.Height;
                dm.dmPositionX = target.X;
                dm.dmPositionY = target.Y;

                if (target.RefreshRate > 0)
                {
                    dm.dmFields |= DM_DISPLAYFREQUENCY;
                    dm.dmDisplayFrequency = target.RefreshRate;
                }

                uint flags = CDS_NORESET | CDS_UPDATEREGISTRY;
                if (target.IsPrimary)
                {
                    flags |= CDS_SET_PRIMARY;
                }

                int res = ChangeDisplaySettingsEx(matched.DeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
                if (res != DISP_CHANGE_SUCCESSFUL)
                {
                    errorMessage = $"Failed to configure display {matched.DeviceName} at ({target.X},{target.Y}) {target.Width}x{target.Height} (Code: {res})";
                    return false;
                }
            }
        }

        // 2. Commit all staged display changes dynamically
        int commitRes = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, CDS_RESET, IntPtr.Zero);
        if (commitRes != DISP_CHANGE_SUCCESSFUL)
        {
            // Second try without CDS_RESET flag
            commitRes = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        }

        if (commitRes != DISP_CHANGE_SUCCESSFUL)
        {
            errorMessage = $"Failed to commit display changes (Code: {commitRes})";
            return false;
        }

        return true;
    }

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
