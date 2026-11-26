using System;
using System.Windows;

namespace FastbootEnhance
{
    /// <summary>
    /// How much room the interface takes: "comfortable" (the default) or "compact", for a
    /// 1366 x 768 laptop, where the pages would otherwise need scrolling. The sizes that differ
    /// (card padding, row and button heights, the bars, the rail) are resources the styles read
    /// with DynamicResource, so a change shows at once on every page, like the text size.
    /// Saved in settings.json; on the first start a short screen gets compact by itself.
    /// </summary>
    static class Density
    {
        public static readonly string[] Codes = { "comfortable", "compact" };

        const string Key = "density";

        /// <summary>For automated runs: overrides the saved choice without touching it.</summary>
        const string OverrideVariable = "FASTBOOT_STUDIO_DENSITY";

        /// <summary>A work area lower than this many units gets compact on the first start (768 minus a taskbar is about 728).</summary>
        const double ShortScreen = 800;

        public static string Chosen { get; private set; } = "comfortable";

        public static bool Compact => Chosen == "compact";

        /// <summary>Reads the choice (override, saved, or by the screen on a first start) and sets the sizes. Before the first window.</summary>
        public static void Apply(ResourceDictionary resources)
        {
            string code = Environment.GetEnvironmentVariable(OverrideVariable);
            if (string.IsNullOrWhiteSpace(code))
            {
                code = Settings.Get(Key);
                if (code == null && SystemParameters.WorkArea.Height < ShortScreen)
                {
                    code = "compact";
                    Save(code);
                }
            }
            code = (code ?? "").Trim().ToLowerInvariant();
            Set(resources, Array.IndexOf(Codes, code) >= 0 ? code : "comfortable");
        }

        /// <summary>Puts the sizes of <paramref name="code"/> into the app's resources; everything drawn follows.</summary>
        public static void Set(ResourceDictionary resources, string code)
        {
            Chosen = code;
            bool compact = Compact;
            resources["CardPadding"] = compact ? new Thickness(12, 10, 12, 10) : new Thickness(16, 14, 16, 14);
            resources["CardTitleMargin"] = new Thickness(0, 0, 0, compact ? 6 : 10);
            // The top space keeps its proportion to the comfortable one (12 to 14), so a title never sits against the device bar.
            resources["PageHeaderPadding"] = compact ? new Thickness(20, 12, 20, 6) : new Thickness(24, 14, 24, 8);
            resources["PageMargin"] = compact ? new Thickness(20, 6, 20, 10) : new Thickness(24, 10, 24, 16);
            resources["ButtonMinHeight"] = compact ? 28.0 : 32.0;
            resources["SegmentMinHeight"] = compact ? 28.0 : 30.0;
            resources["InputPadding"] = compact ? new Thickness(8, 3, 8, 3) : new Thickness(8, 5, 8, 5);
            resources["RowPadding"] = compact ? new Thickness(4, 4, 4, 4) : new Thickness(4, 5, 4, 5);
            resources["HeaderPadding"] = compact ? new Thickness(10, 4, 10, 4) : new Thickness(10, 6, 10, 6);
            resources["TabStripMargin"] = new Thickness(0, 0, 0, compact ? 8 : 10);
            resources["NavItemHeight"] = compact ? 28.0 : 32.0;
            resources["RailWidth"] = new GridLength(compact ? 196 : 212);
            resources["BarHeight"] = compact ? 40.0 : 46.0;
            resources["StatusHeight"] = compact ? 22.0 : 24.0;
        }

        public static bool Save(string code)
        {
            return Settings.Set(Key, code);
        }
    }
}
