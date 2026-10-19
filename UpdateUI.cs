using FastbootEnhance.Core.Update;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FastbootEnhance
{
    /// <summary>
    /// Tells the person when a new version is out. Once a day (unless turned off in About) the
    /// latest GitHub release is read; a newer one puts a badge in the sidebar and opens the
    /// update dialog, unless the app is busy or the person chose to skip that version.
    /// FASTBOOT_STUDIO_UPDATE_FEED, for tests, names a local JSON file to read instead of
    /// GitHub, or "off" to never check by itself.
    /// </summary>
    static class UpdateUI
    {
        const string AutoKey = "update_auto";
        const string LastKey = "update_last";
        const string SkipKey = "update_skip";

        static readonly string Feed = Environment.GetEnvironmentVariable("FASTBOOT_STUDIO_UPDATE_FEED");
        static bool FeedOff => string.Equals(Feed, "off", StringComparison.OrdinalIgnoreCase);
        static bool FeedFile => !string.IsNullOrEmpty(Feed) && !FeedOff;

        static readonly HttpClient http = makeClient();

        /// <summary>The newer release found this session, for the sidebar badge.</summary>
        static ReleaseInfo available;
        static bool checking;

        static HttpClient makeClient()
        {
            HttpClient client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            // GitHub's API refuses requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FastbootStudio/" + MainWindow.version);
            return client;
        }

        public static bool AutoCheck => Settings.Get(AutoKey) != "off";

        /// <summary>A flash, an extraction or a build is running: no dialog pops up, and the app is not closed.</summary>
        public static bool Busy =>
            FastbootUI.flashing || PayloadUI.extracting || ImageToolsUI.busy || SuperUI.busy || SamsungUI.busy;

        public static void init()
        {
            MainWindow window = MainWindow.THIS;
            window.update_auto.IsChecked = AutoCheck;
            window.update_auto.Click += delegate
            {
                Settings.Set(AutoKey, window.update_auto.IsChecked == true ? "on" : "off");
            };
            window.update_check_now.Click += delegate { check(manual: true); };
            window.update_status.Text = "v" + MainWindow.version;

            window.Loaded += delegate
            {
                Button badge = findBadge();
                if (badge != null)
                    badge.Click += delegate { if (available != null) UpdateWindow.Show(available); };

                if (FeedOff || !AutoCheck)
                    return;
                DateTime last;
                bool recent = DateTime.TryParse(Settings.Get(LastKey), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out last)
                    && DateTime.UtcNow - last < ReleaseFeed.Every && DateTime.UtcNow >= last;
                if (recent && !FeedFile)
                    return;

                // A moment after start, so the first thing the person sees is the app itself.
                System.Windows.Threading.DispatcherTimer delay = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                delay.Tick += delegate
                {
                    delay.Stop();
                    check(manual: false);
                };
                delay.Start();
            };
        }

        static Button findBadge()
        {
            TabControl tabs = MainWindow.THIS.main_tabs;
            tabs.ApplyTemplate();
            return tabs.Template == null ? null : tabs.Template.FindName("update_badge", tabs) as Button;
        }

        static void showBadge(ReleaseInfo release)
        {
            available = release;
            Button badge = findBadge();
            if (badge == null)
                return;
            badge.Content = string.Format(Properties.Resources.update_badge, release.Tag);
            badge.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Reads the latest release. A manual check reports what it found in About and always
        /// opens the dialog for a newer version; an automatic one stays quiet unless there is one.
        /// </summary>
        static async void check(bool manual)
        {
            if (checking)
                return;
            checking = true;
            MainWindow window = MainWindow.THIS;
            window.update_check_now.IsEnabled = false;
            if (manual)
                window.update_status.Text = Properties.Resources.update_checking;

            ReleaseInfo release = null;
            string failure = null;
            try
            {
                using (CancellationTokenSource limit = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    string json = FeedFile ? await Task.Run(() => File.ReadAllText(Feed)) : await ReadText(ReleaseFeed.Api, limit.Token);
                    release = ReleaseFeed.Parse(json, allowLocal: FeedFile);
                    if (release == null)
                        failure = "the release could not be read";
                }
                if (!FeedFile)
                    Settings.Set(LastKey, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }
            catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException || e is IOException
                || e is UnauthorizedAccessException)
            {
                failure = e is OperationCanceledException ? "no answer from GitHub" : e.Message;
            }
            finally
            {
                checking = false;
                window.update_check_now.IsEnabled = true;
            }

            if (failure != null)
            {
                LogStore.Append("update check failed: " + failure);
                if (manual)
                    window.update_status.Text = string.Format(Properties.Resources.update_check_failed, failure);
                return;
            }

            bool newer = ReleaseFeed.IsNewer(release.Version, MainWindow.version);
            LogStore.Append("update check: latest " + release.Tag + ", this is v" + MainWindow.version);
            if (!newer)
            {
                window.update_status.Text = "v" + MainWindow.version + " · " + Properties.Resources.update_latest;
                return;
            }

            window.update_status.Text = string.Format(Properties.Resources.update_found, release.Tag);
            showBadge(release);

            if (manual)
            {
                UpdateWindow.Show(release);
                return;
            }
            // Skipped by the person, or not a good moment: the badge is enough.
            if (Settings.Get(SkipKey) == release.Tag || Busy || window.OwnedWindows.Count > 0 || !window.IsActive && !FeedFile)
                return;
            UpdateWindow.Show(release);
        }

        /// <summary>"Skip this version": no dialog by itself for this tag; the badge and About still say it.</summary>
        public static void Skip(string tag)
        {
            Settings.Set(SkipKey, tag);
            LogStore.Append("update " + tag + " skipped");
        }

        public static async Task<string> ReadText(string url, CancellationToken cancel)
        {
            if (FeedFile && Path.IsPathRooted(url))
                return await Task.Run(() => File.ReadAllText(url), cancel);
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (url == ReleaseFeed.Api)
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using (HttpResponseMessage response = await http.SendAsync(request, cancel))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(cancel);
                }
            }
        }

        /// <summary>The zip as a stream: from GitHub, or from a local file when a test feed is in use.</summary>
        public static async Task<Stream> OpenRead(string url, CancellationToken cancel)
        {
            if (FeedFile && Path.IsPathRooted(url))
                return File.OpenRead(url);
            HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            try
            {
                response.EnsureSuccessStatusCode();
                return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancel));
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        /// <summary>A response body that disposes its response with it.</summary>
        sealed class ResponseStream : Stream
        {
            readonly HttpResponseMessage response;
            readonly Stream inner;

            public ResponseStream(HttpResponseMessage response, Stream inner)
            {
                this.response = response;
                this.inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                inner.ReadAsync(buffer, offset, count, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    response.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        /// <summary>The person's Downloads folder, wherever they moved it.</summary>
        public static string DownloadsFolder()
        {
            IntPtr raw;
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out raw) == 0)
            {
                try
                {
                    string path = Marshal.PtrToStringUni(raw);
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(raw);
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        /// <summary>Opens one of the project's pages in the browser.</summary>
        public static void Open(string url)
        {
            if (url == null || !url.StartsWith(ReleaseFeed.Releases, StringComparison.Ordinal))
                url = ReleaseFeed.Releases;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message);
            }
        }
    }
}
