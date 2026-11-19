using System;
using System.Windows;
using System.Windows.Controls;

namespace FastbootEnhance
{
    /// <summary>
    /// A page that fills the window when there is room and scrolls when there is not. The page's
    /// body sits in a ScrollViewer and is given a height here: the visible height, but never less
    /// than what its cards need. So in a small window (or with larger text) nothing is squeezed
    /// until it is cut off; the page scrolls instead, and its lists still scroll inside
    /// themselves, because the body has a real height rather than "as tall as its content".
    /// </summary>
    static class PageFit
    {
        /// <summary>
        /// <paramref name="needed"/> is asked each time: the least height the body can be drawn
        /// in. Returns an action that fits the page again, for when what it needs has changed.
        /// </summary>
        public static Action Attach(ScrollViewer scroll, FrameworkElement body, Func<double> needed)
        {
            Action fit = delegate
            {
                // The body's own margin is part of what scrolls, so it comes off the visible height.
                double height = Math.Max(scroll.ViewportHeight - body.Margin.Top - body.Margin.Bottom, needed());
                if (height > 0 && !(Math.Abs(body.Height - height) <= 0.5))
                    body.Height = height;
            };
            scroll.ScrollChanged += delegate (object sender, ScrollChangedEventArgs e)
            {
                if (e.ViewportHeightChange != 0)
                    fit();
            };
            scroll.Loaded += delegate { fit(); };
            fit();
            return fit;
        }
    }
}
