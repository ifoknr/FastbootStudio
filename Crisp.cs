using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FastbootEnhance
{
    /// <summary>
    /// How text and edges are drawn, set in one place for every window, menu and tool tip.
    /// Menus and tool tips are windows of their own: what the main window sets does not always
    /// reach them, so each gets it as it loads.
    /// Text: "Ideal" formatting. Roboto and Noto Kufi are not hinted for the pixel-snapped
    /// "Display" metrics; with those, letters came out cramped and uneven (worst in semi-bold
    /// labels), and under the text-size scale they blurred. ClearType where the window is
    /// opaque; grey-scale where it is see-through (dialogs, menus, tool tips), since ClearType
    /// needs a solid background and otherwise shows coloured fringes.
    /// Edges: layout rounded to whole pixels and lines snapped to them, at any display scale.
    /// </summary>
    static class Crisp
    {
        static bool registered;

        /// <summary>Called once at start, before the first window.</summary>
        public static void Register()
        {
            if (registered)
                return;
            registered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) => Apply((Window)sender)));
            EventManager.RegisterClassHandler(typeof(ContextMenu), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) => ApplyTo((FrameworkElement)sender, false)));
            EventManager.RegisterClassHandler(typeof(ToolTip), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) => ApplyTo((FrameworkElement)sender, false)));
        }

        public static void Apply(Window window)
        {
            // With the Windows 11 backdrop the window itself is see-through too.
            ApplyTo(window, !window.AllowsTransparency && !DarkTitleBar.BackdropWanted);
        }

        static void ApplyTo(FrameworkElement root, bool opaque)
        {
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
            TextOptions.SetTextHintingMode(root, TextHintingMode.Fixed);
            TextOptions.SetTextRenderingMode(root, opaque ? TextRenderingMode.ClearType : TextRenderingMode.Grayscale);
            root.UseLayoutRounding = true;
            root.SnapsToDevicePixels = true;
        }
    }
}
