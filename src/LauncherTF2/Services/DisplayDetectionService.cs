using System.Runtime.InteropServices;
using LauncherTF2.Core;

namespace LauncherTF2.Services;

/// <summary>
/// Detects the primary display's *physical* resolution and refresh rate via
/// <c>EnumDisplaySettings</c>. WPF's <c>SystemParameters</c> reports DIP-space
/// values which mismatch what TF2 expects when DPI scaling is active — we go
/// straight to Win32 instead so the numbers match what users see in their
/// Windows display settings.
///
/// Used on first-run by <see cref="SettingsService"/> to seed reasonable
/// width / height / refresh rate defaults so the launcher doesn't ship with
/// 1920×1080@60 for a 1440p / 144Hz user.
/// </summary>
internal static class DisplayDetectionService
{
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int   dmFields;
        public int   dmPositionX;
        public int   dmPositionY;
        public int   dmDisplayOrientation;
        public int   dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int   dmBitsPerPel;
        public int   dmPelsWidth;
        public int   dmPelsHeight;
        public int   dmDisplayFlags;
        public int   dmDisplayFrequency;
        public int   dmICMMethod;
        public int   dmICMIntent;
        public int   dmMediaType;
        public int   dmDitherType;
        public int   dmReserved1;
        public int   dmReserved2;
        public int   dmPanningWidth;
        public int   dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    public readonly record struct DisplayInfo(int Width, int Height, int RefreshRate);

    /// <summary>
    /// Returns the current display mode of the primary screen, or null when
    /// the Win32 call fails (e.g. running headlessly in a CI environment).
    /// </summary>
    public static DisplayInfo? GetPrimaryDisplay()
    {
        try
        {
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode))
            {
                Logger.LogWarning("[DisplayDetection] EnumDisplaySettings returned false");
                return null;
            }

            // dmDisplayFrequency is sometimes 0 or 1 on headless / RDP sessions —
            // anything below 24 is meaningless for a TF2 game session.
            var hz = mode.dmDisplayFrequency < 24 ? 60 : mode.dmDisplayFrequency;
            return new DisplayInfo(mode.dmPelsWidth, mode.dmPelsHeight, hz);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[DisplayDetection] Probe failed: {ex.Message}");
            return null;
        }
    }
}
