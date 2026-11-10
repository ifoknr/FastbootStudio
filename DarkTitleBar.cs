using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastbootEnhance
{
    /// <summary>
    /// Tells Windows the window's colours (dark or light, see Theme). The app draws its own title bar (ThemedWindow), so this
    /// only touches what Windows still draws: the system menu, the thin outline and, on
    /// Windows 11, the rounded corners. Older systems ignore the calls.
    /// </summary>
    static class DarkTitleBar
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_CAPTION_COLOR = 35;
        const int DWMWA_TEXT_COLOR = 36;
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMSBT_NONE = 1;
        const int DWMSBT_MAINWINDOW = 2;   // Mica

        /// <summary>The Settings key of the glass backdrop: "off" to keep the window's own gradient.</summary>
        public const string BackdropKey = "backdrop";

        /// <summary>Windows 11 22H2 and later draw Mica; older systems have nothing to turn on.</summary>
        public static bool BackdropAvailable => Environment.OSVersion.Version.Build >= 22621;

        /// <summary>
        /// Off unless turned on in Settings, and only with graphics-card drawing: under software
        /// drawing (the app's default) Windows paints nothing behind a clear window, so the page
        /// turned black.
        /// </summary>
        public static bool BackdropWanted => BackdropAvailable && Rendering.Chosen == Rendering.Mode.Gpu
            && Settings.Get(BackdropKey) == "on";

        /// <summary>COLORREF (0x00BBGGRR) of one of the theme's brushes: Bg, Line or Text.</summary>
        static int ColorRef(string key)
        {
            System.Windows.Media.Color color = ((System.Windows.Media.SolidColorBrush)Palette.Get(key)).Color;
            return color.R | (color.G << 8) | (color.B << 16);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Call from SourceInitialized, when the window has a handle but is not yet shown.</summary>
        public static void Apply(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                int dark = Theme.IsLight ? 0 : 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

                int caption = ColorRef("Bg"), border = ColorRef("Line"), text = ColorRef("Text");
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));

                // Mica: the desktop shows through the window, blurred. The window's own
                // background has to be clear for it to show, so the sky brush goes when it is on.
                if (BackdropAvailable)
                {
                    bool mica = BackdropWanted;
                    int backdrop = mica ? DWMSBT_MAINWINDOW : DWMSBT_NONE;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                    {
                        HwndSource source = HwndSource.FromHwnd(hwnd);
                        if (source?.CompositionTarget != null)
                            source.CompositionTarget.BackgroundColor = mica ? System.Windows.Media.Colors.Transparent
                                : ((System.Windows.Media.SolidColorBrush)Palette.Get("Bg")).Color;
                        window.Background = mica ? System.Windows.Media.Brushes.Transparent : Palette.Get("SkyBrush");
                    }
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }
}
