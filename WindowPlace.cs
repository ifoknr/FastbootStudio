using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastbootEnhance
{
    /// <summary>
    /// Remembers where the main window was: which monitor, where on it, how big, and whether it
    /// was maximised. Kept in settings.json as the monitor's name, the window's offset from that
    /// monitor's work area and its size, both in units that do not depend on the display scale,
    /// so the same window comes back the same size on a monitor whose scale has changed.
    /// It is put back only when that monitor is still connected; the window is then moved fully
    /// inside the monitor's work area. Otherwise Windows centres it on the main screen, as on a
    /// first start. A minimised window is never saved or restored as minimised.
    /// </summary>
    static class WindowPlace
    {
        const string Key = "window";

        const int WM_EXITSIZEMOVE = 0x0232;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3;
        const int WPF_RESTORETOMAXIMIZED = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // Windows 8.1 and later; older systems report the one system scale.
        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        /// <summary>Restores the window's place when its handle exists, and saves it when it is moved, resized or closed.</summary>
        public static void Attach(Window window)
        {
            window.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                try
                {
                    restore(window, hwnd);
                }
                catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is FormatException)
                {
                    LogStore.Append("window place not restored: " + e.Message);
                }
                HwndSource source = HwndSource.FromHwnd(hwnd);
                if (source != null)
                {
                    source.AddHook(delegate (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
                    {
                        if (message == WM_EXITSIZEMOVE)
                            save(window);
                        return IntPtr.Zero;
                    });
                }
            };
            window.StateChanged += delegate { save(window); };
            window.Closing += delegate { save(window); };
        }

        static bool info(IntPtr monitor, out MONITORINFOEX found)
        {
            found = new MONITORINFOEX();
            found.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref found);
        }

        static double scale(IntPtr monitor)
        {
            try
            {
                uint x, y;
                if (GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0)
                    return x / 96.0;
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
            return 1.0;
        }

        static string number(double value)
        {
            return Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"device|left|top|width|height|max": offsets from the monitor's work area and sizes, in scale-free units.</summary>
        static void save(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;
                WINDOWPLACEMENT placement = new WINDOWPLACEMENT();
                placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (!GetWindowPlacement(hwnd, ref placement))
                    return;
                // Minimised: nothing is saved, so the last place it was seen at stays remembered.
                if (placement.showCmd == SW_SHOWMINIMIZED || window.WindowState == WindowState.Minimized)
                    return;

                bool maximised = placement.showCmd == SW_SHOWMAXIMIZED;
                RECT rect;
                if (maximised)
                    rect = placement.rcNormalPosition;   // where it goes back to when restored
                else if (!GetWindowRect(hwnd, out rect))
                    return;

                MONITORINFOEX monitor;
                IntPtr handle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (!info(handle, out monitor))
                    return;
                double k = scale(handle);
                string value = string.Join("|", monitor.szDevice,
                    number((rect.Left - monitor.rcWork.Left) / k), number((rect.Top - monitor.rcWork.Top) / k),
                    number((rect.Right - rect.Left) / k), number((rect.Bottom - rect.Top) / k), maximised ? "1" : "0");
                if (value != lastSaved)
                {
                    lastSaved = value;
                    Settings.Set(Key, value);
                }
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
            }
        }

        static string lastSaved;

        static void restore(Window window, IntPtr hwnd)
        {
            string saved = Settings.Get(Key);
            lastSaved = saved;
            if (string.IsNullOrEmpty(saved) || hwnd == IntPtr.Zero)
                return;
            string[] parts = saved.Split('|');
            if (parts.Length != 6)
                return;
            double left = double.Parse(parts[1], CultureInfo.InvariantCulture), top = double.Parse(parts[2], CultureInfo.InvariantCulture);
            double width = double.Parse(parts[3], CultureInfo.InvariantCulture), height = double.Parse(parts[4], CultureInfo.InvariantCulture);
            if (width < 200 || height < 150)
                return;

            // The monitor it was on, by name; gone (unplugged, another arrangement) means a fresh, centred start.
            IntPtr monitor = IntPtr.Zero;
            MONITORINFOEX found = new MONITORINFOEX();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr handle, IntPtr hdc, IntPtr rect, IntPtr data)
            {
                MONITORINFOEX each;
                if (info(handle, out each) && each.szDevice == parts[0])
                {
                    monitor = handle;
                    found = each;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (monitor == IntPtr.Zero)
                return;

            double k = scale(monitor);
            int workWidth = found.rcWork.Right - found.rcWork.Left, workHeight = found.rcWork.Bottom - found.rcWork.Top;
            int w = Math.Min((int)Math.Round(width * k), workWidth), h = Math.Min((int)Math.Round(height * k), workHeight);
            // Inside the work area, whatever the taskbar or the resolution has become since.
            int x = found.rcWork.Left + Math.Max(0, Math.Min((int)Math.Round(left * k), workWidth - w));
            int y = found.rcWork.Top + Math.Max(0, Math.Min((int)Math.Round(top * k), workHeight - h));

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            // Twice: landing on a monitor with another scale makes Windows resize the window by
            // the ratio of the two scales; the second call puts back the size worked out for it.
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            if (parts[5] == "1")
                window.WindowState = WindowState.Maximized;
        }
    }
}
