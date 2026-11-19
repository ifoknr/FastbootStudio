using FastbootEnhance.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace FastbootEnhance
{
    class Helper
    {

        public class TaskbarItemHelper
        {
            public static void start()
            {
                MainWindow.THIS.taskbariteminfo.ProgressValue = 0;
                MainWindow.THIS.taskbariteminfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
            }

            public static void stop()
            {
                MainWindow.THIS.taskbariteminfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
            }

            public static void update(int percent)
            {
                MainWindow.THIS.taskbariteminfo.ProgressValue = percent / 100.0;
            }

            public static void error()
            {
                MainWindow.THIS.taskbariteminfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Error;
            }
        }

        public static void offloadAndRun(Action bigtask, Action callbackOnUIThread)
        {
            Thread thread = new Thread(new ThreadStart(delegate
            {
                bigtask();
                try
                {
                    MainWindow.THIS.Dispatcher.Invoke(callbackOnUIThread);
                }
                catch (System.Threading.Tasks.TaskCanceledException)
                {
                    // The app closed while the task ran: there is no window left to tell.
                }
            }));
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// A yes/no prompt. Returns true only when the user picks yes. A destructive question is
        /// drawn as a warning, with a red "yes", and Enter answers no.
        /// </summary>
        public static bool confirm(string message, string title, bool destructive = false)
        {
            return ThemedDialog.Show(message, title, MessageBoxButton.YesNo,
                    destructive ? MessageBoxImage.Warning : MessageBoxImage.Question,
                    destructive ? MessageBoxResult.No : MessageBoxResult.Yes)
                == MessageBoxResult.Yes;
        }

        public class ListHelper<T>
        {
            public delegate bool Filter(T t);

            ListView listView;
            List<T> items;
            Filter filter;

            public ListHelper(ListView listView, Filter filter)
            {
                this.listView = listView;
                items = new List<T>();
                this.filter = filter;
            }

            public void clear()
            {
                listView.SelectedItems.Clear();
                listView.Items.Clear();
                items = new List<T>();
            }

            public void addItem(T item)
            {
                items.Add(item);
            }

            public void render()
            {
                listView.Items.Clear();
                foreach (T t in items)
                {
                    listView.Items.Add(t);
                }
            }

            public void doFilter()
            {
                listView.Items.Clear();
                foreach (T t in items)
                {
                    if (filter(t))
                        listView.Items.Add(t);
                }
            }
        }

        public delegate void PathSelectCallback(string path);

        /// <summary>
        /// <paramref name="remember"/> names what is being picked ("payload", "boot_image"): the
        /// dialog then opens in the folder the last pick of that kind came from.
        /// </summary>
        public static void fileSelect(PathSelectCallback callback, string filter = "All Files|*.*", string remember = null)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = filter;
            if (showOpen(dialog, remember))
            {
                callback(dialog.FileName);
            }
        }

        public static void pathSelect(PathSelectCallback callback, string remember = null)
        {
            Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog();
            dialog.Title = Properties.Resources.select_save_path;
            string last = lastFolder(remember);
            if (last != null)
                dialog.InitialDirectory = last;
            if (dialog.ShowDialog() == true)
            {
                rememberFolder(remember, dialog.FolderName);
                callback(dialog.FolderName);
            }
        }

        /// <summary>The folder last used for <paramref name="what"/>, when it still exists; null otherwise.</summary>
        public static string lastFolder(string what)
        {
            if (string.IsNullOrEmpty(what))
                return null;
            string folder = Settings.Get("folder_" + what);
            try
            {
                return !string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder) ? folder : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void rememberFolder(string what, string folder)
        {
            if (!string.IsNullOrEmpty(what) && !string.IsNullOrEmpty(folder))
                Settings.Set("folder_" + what, folder);
        }

        /// <summary>
        /// Shows an open dialog starting in the folder last used for <paramref name="what"/>, and
        /// remembers the folder of what was picked. True when a file was picked.
        /// </summary>
        public static bool showOpen(Microsoft.Win32.OpenFileDialog dialog, string what, Window owner = null)
        {
            string last = lastFolder(what);
            if (last != null)
                dialog.InitialDirectory = last;
            bool picked = (owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;
            if (picked)
            {
                try
                {
                    rememberFolder(what, System.IO.Path.GetDirectoryName(dialog.FileName));
                }
                catch (ArgumentException)
                {
                }
            }
            return picked;
        }

        /// <summary>
        /// Puts text on the clipboard without the left-to-right marks <see cref="ltr"/> adds.
        /// False when another app is holding the clipboard.
        /// </summary>
        public static bool copyText(string text)
        {
            try
            {
                Clipboard.SetText((text ?? "").Replace("\u200E", ""));
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }

        /// <summary>
        /// Formats a unix timestamp in the machine's own time zone. Timestamps come from the
        /// payload manifest, so a nonsensical value must not be allowed to throw.
        /// </summary>
        public static DateTime? timeStamp2DataTime(Int64 timestamp)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().DateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        public static string byte2AUnit(ulong size)
        {
            return ltr(ByteSize.Format(size > long.MaxValue ? long.MaxValue : (long)size));
        }

        public static string byte2AUnit(long size)
        {
            return ltr(ByteSize.Format(size));
        }

        /// <summary>
        /// In a right-to-left interface, "16.00 MB" or "326 (95.0%)" would be laid out back to
        /// front; left-to-right marks around the value keep it in reading order. A no-op in
        /// left-to-right languages, so logs and copied text are unaffected there.
        /// </summary>
        /// <summary>
        /// <see cref="ltr"/> for a value that is wholly Latin (a file name, a serial, a command):
        /// one holding right-to-left letters is returned as it is.
        /// </summary>
        public static string ltrLatin(string value)
        {
            if (!Languages.RightToLeft || string.IsNullOrEmpty(value))
                return value;
            foreach (char c in value)
            {
                if ((c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFEFC'))
                    return value;
            }
            return ltr(value);
        }

        public static string ltr(string value)
        {
            if (!Languages.RightToLeft || string.IsNullOrEmpty(value))
                return value;
            return "\u200E" + value + "\u200E";
        }
    }
}
