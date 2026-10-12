using System;
using System.Windows.Interop;
using System.Windows.Media;

namespace FastbootEnhance
{
    /// <summary>
    /// How WPF draws the app. With the graphics card, some drivers and remote-desktop apps leave
    /// parts of the window undrawn: menu items vanish under the mouse and buttons turn into
    /// stripes. Drawing in software avoids that and is plenty fast for this app, so it is the
    /// default; the graphics card can be chosen on the About page. Applies on restart.
    /// </summary>
    static class Rendering
    {
        public enum Mode
        {
            Software,
            Gpu,
        }

        /// <summary>For automated runs and troubleshooting: overrides the saved choice without touching it.</summary>
        const string OverrideVariable = "FASTBOOT_STUDIO_RENDERING";

        public static Mode Chosen { get; private set; } = Mode.Software;

        public static Mode Parse(string raw)
        {
            return (raw ?? "").Trim().ToLowerInvariant() == "gpu" ? Mode.Gpu : Mode.Software;
        }

        /// <summary>Called once, before the first window or dialog.</summary>
        public static void Apply()
        {
            string raw = Environment.GetEnvironmentVariable(OverrideVariable);
            if (string.IsNullOrWhiteSpace(raw))
                raw = Settings.Get("rendering");
            Chosen = Parse(raw);
            RenderOptions.ProcessRenderMode = Chosen == Mode.Software ? RenderMode.SoftwareOnly : RenderMode.Default;
        }

        public static bool Save(Mode mode)
        {
            return Settings.Set("rendering", mode == Mode.Gpu ? "gpu" : "software");
        }
    }
}
