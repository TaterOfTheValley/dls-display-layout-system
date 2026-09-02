using System.Runtime.InteropServices;

namespace DLS;

// Interop for the CCD ("Connecting and Configuring Displays") API —
// QueryDisplayConfig / SetDisplayConfig. This is the API the Windows Settings
// display page itself uses, and it replaces ChangeDisplaySettingsEx entirely.
//
// Why it matters here: SetDisplayConfig takes the WHOLE desktop topology (every
// path, every mode) and applies it as ONE atomic transaction. Enabling one
// monitor while disabling two others is a single call. The old CDS path had to
// stage that as wake -> configure -> commit -> detach, with hand-tuned ordering,
// and was still not atomic.
//
// Every struct here is deliberately blittable — no `bool`, no `string` fields in
// anything that goes through an array marshal or an explicit-layout union.
// BOOL is kept as uint (see TargetAvailable). This keeps the marshaller on the
// fast path and, more importantly, keeps DISPLAYCONFIG_MODE_INFO's union legal.

#region Constants

internal static class Ccd
{
    // QueryDisplayConfig flags
    public const uint QDC_ALL_PATHS = 0x00000001;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint QDC_DATABASE_CURRENT = 0x00000004;

    // SetDisplayConfig flags
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    public const uint SDC_VALIDATE = 0x00000040;
    public const uint SDC_APPLY = 0x00000080;
    public const uint SDC_NO_OPTIMIZATION = 0x00000100;
    public const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    public const uint SDC_ALLOW_CHANGES = 0x00000400;
    public const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800;
    public const uint SDC_FORCE_MODE_ENUMERATION = 0x00001000;
    public const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;

    // Path flags
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

    // Mode info types
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;

    // DisplayConfigGetDeviceInfo request types
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    // Per-monitor DPI. These two are undocumented — Windows exposes no supported API
    // for reading or setting display scaling, and the Settings app uses these. They
    // have been stable since Windows 10 1607, but treat every call as best-effort and
    // never let a failure here fail a layout switch.
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = unchecked((uint)-3);
    public const uint DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = unchecked((uint)-4);

    public const uint DISPLAYCONFIG_PIXELFORMAT_32BPP = 4;

    /// <summary>The scaling percentages Windows offers, in order. The API works in
    /// steps through this table rather than in percentages.</summary>
    public static readonly int[] DpiScaleSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    // Win32 error codes returned directly (not HRESULTs) by these APIs.
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_BADDB = 1009;
}

#endregion

#region Structs

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;

    public bool SameAs(LUID other) => LowPart == other.LowPart && HighPart == other.HighPart;
    public override string ToString() => $"{HighPart:X8}-{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;

    public double AsHz => Denominator == 0 ? 0 : (double)Numerator / Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECTL
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_2DREGION
{
    public uint cx;
    public uint cy;
}

// 20 bytes
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

// 48 bytes
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    public uint targetAvailable;   // BOOL — kept as uint to stay blittable
    public uint statusFlags;
}

// 72 bytes
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;

    public bool IsActive => (flags & Ccd.DISPLAYCONFIG_PATH_ACTIVE) != 0;
}

// 48 bytes
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong pixelRate;
    public DISPLAYCONFIG_RATIONAL hSyncFreq;
    public DISPLAYCONFIG_RATIONAL vSyncFreq;
    public DISPLAYCONFIG_2DREGION activeSize;
    public DISPLAYCONFIG_2DREGION totalSize;
    public uint videoStandard;      // union: AdditionalSignalInfo bitfield
    public uint scanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_TARGET_MODE
{
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
}

// 20 bytes
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SOURCE_MODE
{
    public uint width;
    public uint height;
    public uint pixelFormat;
    public POINTL position;
}

// 40 bytes
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
{
    public POINTL PathSourceSize;
    public RECTL DesktopImageRegion;
    public RECTL DesktopImageClip;
}

// The union inside DISPLAYCONFIG_MODE_INFO. Size is the largest member
// (targetMode, 48). All members are blittable, which is what makes an
// overlapping explicit layout legal here.
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct DISPLAYCONFIG_MODE_INFO_UNION
{
    [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
    [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
}

// 64 bytes
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public uint infoType;
    public uint id;
    public LUID adapterId;
    public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public uint type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;
}

// 80 bytes, not 76: header(20) + width(4) + height(4) leaves 28, and targetMode
// contains a UINT64 so it must start 8-aligned — the compiler inserts 4 bytes of
// padding, and the native struct is padded identically.
// The monitor's native mode, available even while the output is inactive —
// which is the only way to learn that a currently-disabled panel is 4K rather than
// guessing 1080p.
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint width;
    public uint height;
    public DISPLAYCONFIG_TARGET_MODE targetMode;
}

// 32 bytes. Scale is expressed relative to the value Windows recommends for the
// panel, not as an absolute percentage: minScaleRel is how many steps below the
// recommendation you may go, so the recommended entry sits at index -minScaleRel
// in Ccd.DpiScaleSteps.
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public int minScaleRel;
    public int curScaleRel;
    public int maxScaleRel;
}

// 24 bytes.
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public int scaleRel;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string viewGdiDeviceName;
}

#endregion

internal static class CcdNative
{
    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    internal static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE requestPacket);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET requestPacket);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET requestPacket);

    /// <summary>
    /// Verifies the P/Invoke struct layouts against the sizes winuser.h defines on
    /// x64. A mismatch here corrupts every subsequent call in ways that surface as
    /// baffling ERROR_INVALID_PARAMETERs, so it is cheap insurance to check once.
    /// </summary>
    internal static void AssertLayout()
    {
        Check<DISPLAYCONFIG_PATH_SOURCE_INFO>(20);
        Check<DISPLAYCONFIG_PATH_TARGET_INFO>(48);
        Check<DISPLAYCONFIG_PATH_INFO>(72);
        Check<DISPLAYCONFIG_VIDEO_SIGNAL_INFO>(48);
        Check<DISPLAYCONFIG_SOURCE_MODE>(20);
        Check<DISPLAYCONFIG_DESKTOP_IMAGE_INFO>(40);
        Check<DISPLAYCONFIG_MODE_INFO>(64);
        Check<DISPLAYCONFIG_DEVICE_INFO_HEADER>(20);
        Check<DISPLAYCONFIG_TARGET_DEVICE_NAME>(420);
        Check<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(84);
        Check<DISPLAYCONFIG_TARGET_PREFERRED_MODE>(80);
        Check<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(32);
        Check<DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(24);

        static void Check<T>(int expected) where T : struct
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"CCD interop layout error: sizeof({typeof(T).Name}) is {actual}, expected {expected}.");
            }
        }
    }
}
