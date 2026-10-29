using System.Windows;
using System.Windows.Media;

namespace FastbootEnhance
{
    /// <summary>
    /// The text size chosen in Settings. The whole window is scaled, so every page, table and
    /// button follows at once and nothing needs a restart.
    /// </summary>
    static class TextSize
    {
        public static readonly string[] Codes = { "small", "default", "large", "larger" };
        static readonly double[] Scales = { 0.9, 1.0, 1.12, 1.25 };

        public static string Chosen { get; private set; } = "default";

        public static double Scale(string code)
        {
            int i = System.Array.IndexOf(Codes, code);
            return i >= 0 ? Scales[i] : 1.0;
        }

        /// <summary>Reads the saved size and applies it to the window's content.</summary>
        public static void Apply(FrameworkElement content)
        {
            string saved = Settings.Get("text_size");
            Chosen = System.Array.IndexOf(Codes, saved) >= 0 ? saved : "default";
            Set(content, Chosen);
        }

        public static void Set(FrameworkElement content, string code)
        {
            Chosen = code;
            double k = Scale(code);
            content.LayoutTransform = k == 1.0 ? Transform.Identity : new ScaleTransform(k, k);
        }

        public static bool Save(string code)
        {
            return Settings.Set("text_size", code);
        }
    }
}
