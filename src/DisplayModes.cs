using System.Runtime.InteropServices;

namespace DLS;

/// <summary>
/// Enumerates the modes a monitor can actually run, for the editor's resolution and
/// refresh-rate dropdowns.
///
/// This is the one place ChangeDisplaySettingsEx's sibling still earns its keep.
/// EnumDisplaySettings was never the problem with the old engine — *applying* through
/// that API was. As a read-only mode enumerator for an attached display it is
/// accurate, and CCD offers no equivalent.
///
/// Its documented weakness is real and handled: a currently-disabled output reports
/// only a generic low-resolution list, which is exactly the case where the editor most
/// needs to offer 4K. So for those we fall back to the panel's native mode from EDID
/// plus a standard ladder, and let the apply ladder in CcdEngine sort out anything the
/// hardware turns out not to support.
/// </summary>
internal static class DisplayModes
{
    internal readonly record struct Mode(int Width, int Height, int Hz);

    private const int ENUM_CURRENT_SETTINGS = -1;

    private static readonly Dictionary<string, List<Mode>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops the cache. Called when the hardware set changes, since a monitor
    /// that was just enabled can now report its real mode list.</summary>
    public static void Invalidate() => Cache.Clear();

    /// <summary>
    /// Every mode this monitor can run, best-effort. <paramref name="gdiDeviceName"/>
    /// is the \\.\DISPLAYn name and is only meaningful while the monitor is attached;
    /// pass null or empty for a disabled one.
    /// </summary>
    public static List<Mode> For(DisplayTargetConfig cfg, string? gdiDeviceName)
    {
        var modes = new List<Mode>();

        if (!string.IsNullOrWhiteSpace(gdiDeviceName) && gdiDeviceName.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            modes.AddRange(Enumerate(gdiDeviceName));
        }

        bool enumeratedNative = cfg.NativeWidth > 0 &&
                                modes.Any(m => m.Width == cfg.NativeWidth && m.Height == cfg.NativeHeight);

        // A disabled monitor's enumeration is untrustworthy — if it does not even
        // contain the panel's own native resolution, synthesise the list instead.
        if (modes.Count == 0 || !enumeratedNative)
        {
            modes.AddRange(Synthesize(cfg));
        }

        int savedHz = cfg.RefreshDenominator > 0
            ? (int)Math.Round((double)cfg.RefreshNumerator / cfg.RefreshDenominator)
            : cfg.RefreshRate;
        if (cfg.Width > 0 && cfg.Height > 0 && savedHz > 0)
        {
            modes.Add(new Mode(cfg.Width, cfg.Height, savedHz));
        }

        return modes
            .Where(m => m.Width > 0 && m.Height > 0 && m.Hz > 0)
            .Distinct()
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Hz)
            .ToList();
    }

    /// <summary>Resolutions, largest first.</summary>
    public static List<(int Width, int Height)> Resolutions(IEnumerable<Mode> modes) =>
        modes.Select(m => (m.Width, m.Height))
             .Distinct()
             .OrderByDescending(r => (long)r.Width * r.Height)
             .ToList();

    /// <summary>Refresh rates available at one resolution, highest first.</summary>
    public static List<int> RatesFor(IEnumerable<Mode> modes, int width, int height) =>
        modes.Where(m => m.Width == width && m.Height == height)
             .Select(m => m.Hz)
             .Distinct()
             .OrderByDescending(hz => hz)
             .ToList();

    /// <summary>The highest rate this monitor can run at that size, or 0 if unknown.</summary>
    public static int BestRateFor(IEnumerable<Mode> modes, int width, int height)
    {
        var rates = RatesFor(modes, width, height);
        return rates.Count > 0 ? rates[0] : 0;
    }

    private static List<Mode> Enumerate(string gdiDeviceName)
    {
        if (Cache.TryGetValue(gdiDeviceName, out var cached)) return cached;

        var found = new List<Mode>();
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            for (int i = 0; EnumDisplaySettings(gdiDeviceName, i, ref dm); i++)
            {
                // 32-bit colour only: the same resolution repeated at lower bit depths
                // is noise in a dropdown, and nothing here would ever pick one.
                if (dm.dmBitsPerPel != 32) continue;
                found.Add(new Mode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency));
            }
        }
        catch
        {
            // Enumeration is a convenience; a failure just means a synthesised list.
        }

        Cache[gdiDeviceName] = found;
        return found;
    }

    /// <summary>
    /// A plausible mode list for a monitor we cannot enumerate: its native resolution
    /// and the standard sizes below it, each at the common refresh rates. Anything the
    /// panel cannot actually do is caught by SetDisplayConfig's validation, which is a
    /// far better authority than a guess here.
    /// </summary>
    private static IEnumerable<Mode> Synthesize(DisplayTargetConfig cfg)
    {
        int maxW = cfg.NativeWidth > 0 ? cfg.NativeWidth : cfg.Width;
        int maxH = cfg.NativeHeight > 0 ? cfg.NativeHeight : cfg.Height;
        if (maxW <= 0 || maxH <= 0) yield break;

        var sizes = new List<(int W, int H)> { (maxW, maxH) };
        foreach (var (w, h) in new[]
                 {
                     (3840, 2160), (3440, 1440), (2560, 1440), (2560, 1080), (1920, 1200),
                     (1920, 1080), (1680, 1050), (1600, 900), (1440, 900), (1280, 720)
                 })
        {
            if (w <= maxW && h <= maxH && !sizes.Contains((w, h))) sizes.Add((w, h));
        }

        int nativeHz = cfg.RefreshDenominator > 0
            ? (int)Math.Round((double)cfg.RefreshNumerator / cfg.RefreshDenominator)
            : 0;

        foreach (var (w, h) in sizes)
        {
            foreach (int hz in new[] { 240, 165, 144, 120, 100, 75, 60 })
            {
                yield return new Mode(w, h, hz);
            }

            if (nativeHz > 0) yield return new Mode(w, h, nativeHz);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
