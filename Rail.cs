using System.Windows;

namespace FastbootEnhance
{
    /// <summary>
    /// Attached slots of the navigation rail (Themes/Styles.xaml, NavRail): the device bar
    /// above the page, the status bar below it, and the pages at the foot of the rail
    /// (Settings, About). They are set in MainWindow.xaml, outside the template, so their
    /// parts can carry names the code reaches.
    /// </summary>
    public static class Rail
    {
        public static readonly DependencyProperty TopBarProperty = DependencyProperty.RegisterAttached(
            "TopBar", typeof(object), typeof(Rail), new PropertyMetadata(null));

        public static readonly DependencyProperty BottomBarProperty = DependencyProperty.RegisterAttached(
            "BottomBar", typeof(object), typeof(Rail), new PropertyMetadata(null));

        public static readonly DependencyProperty FootProperty = DependencyProperty.RegisterAttached(
            "Foot", typeof(object), typeof(Rail), new PropertyMetadata(null));

        public static object GetTopBar(DependencyObject element) => element.GetValue(TopBarProperty);
        public static void SetTopBar(DependencyObject element, object value) => element.SetValue(TopBarProperty, value);

        public static object GetBottomBar(DependencyObject element) => element.GetValue(BottomBarProperty);
        public static void SetBottomBar(DependencyObject element, object value) => element.SetValue(BottomBarProperty, value);

        public static object GetFoot(DependencyObject element) => element.GetValue(FootProperty);
        public static void SetFoot(DependencyObject element, object value) => element.SetValue(FootProperty, value);
    }
}
