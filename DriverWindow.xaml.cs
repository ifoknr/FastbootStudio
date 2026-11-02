using FastbootEnhance.Core.Usb;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FastbootEnhance
{
    /// <summary>
    /// Downloads Samsung's USB driver from developer.samsung.com, keeps it only when Windows
    /// finds a valid signature from Samsung Electronics on it, and runs its installer, which
    /// asks Windows for permission itself. Google's USB driver comes from dl.google.com, is kept
    /// only when its size and checksum match Google's list, and is added with pnputil, which asks
    /// for permission and checks the driver's own signature. Any step that fails offers the
    /// maker's page instead.
    /// </summary>
    public partial class DriverWindow : Window
    {
        const string GlyphDownload = "M12,3 V15 M7,10.5 L12,15.5 L17,10.5 M5,20 H19";
        const string GlyphDone = "M5,12.5 L10,17.5 L19.5,7";
        const string GlyphError = "M12,2 A10,10 0 1 1 11.99,2 Z M9,9 L15,15 M15,9 L9,15";

        static readonly HttpClient http = makeClient();

        static HttpClient makeClient()
        {
            HttpClient client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FastbootStudio/" + MainWindow.version);
            return client;
        }

        /// <summary>Google's driver instead of Samsung's.</summary>
        readonly bool google;
        readonly string logName;

        CancellationTokenSource working;
        bool installerRunning;
        Action onPrimary;
        Action onSecondary;

        DriverWindow(bool google)
        {
            this.google = google;
            logName = google ? "google driver: " : "samsung driver: ";
            InitializeComponent();
            FlowDirection = Languages.Flow;
            header.MouseLeftButtonDown += delegate { DragMove(); };
            SourceInitialized += delegate { DarkTitleBar.Apply(this); };
            primary.Click += delegate { onPrimary?.Invoke(); };
            secondary.Click += delegate { onSecondary?.Invoke(); };
            Closing += (sender, e) =>
            {
                // The installer is Samsung's own window: it carries on, and this one waits for it.
                if (installerRunning)
                {
                    e.Cancel = true;
                    return;
                }
                working?.Cancel();
            };
            PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape && !installerRunning)
                {
                    Close();
                    e.Handled = true;
                }
            };
            showOffer();
        }

        public static void Show(Action after, bool google)
        {
            DriverWindow dialog = new DriverWindow(google);
            if (MainWindow.THIS != null && MainWindow.THIS.IsVisible)
            {
                dialog.Owner = MainWindow.THIS;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
            }
            dialog.ShowDialog();
            after?.Invoke();
        }

        void showOffer()
        {
            Title = google ? Properties.Resources.driver_google_title : Properties.Resources.driver_samsung_title;
            heading.Text = Title;
            subheading.Text = source;
            setLook("Accent", "AccentSoft", "AccentLine", GlyphDownload);
            string offer = google ? Properties.Resources.driver_google_offer : Properties.Resources.driver_samsung_offer;
            message.Text = installed() ? Properties.Resources.driver_samsung_already + "\n\n" + offer : offer;
            progress.Visibility = Visibility.Collapsed;
            primary.Visibility = Visibility.Visible;
            primary.Content = Properties.Resources.driver_download_install;
            onPrimary = start;
            secondary.Content = Properties.Resources.cancel;
            onSecondary = Close;
        }

        string source => google ? Properties.Resources.driver_google_source : Properties.Resources.driver_samsung_source;

        bool installed() => google ? DriverUI.GoogleInstalled() : DriverUI.SamsungInstalled();

        void start()
        {
            heading.Text = google ? Properties.Resources.driver_google_downloading : Properties.Resources.driver_downloading;
            subheading.Text = source;
            message.Text = google ? Properties.Resources.driver_google_downloading_note : Properties.Resources.driver_downloading_note;
            progress.Visibility = Visibility.Visible;
            progress.IsIndeterminate = true;
            progress.Value = 0;
            primary.Visibility = Visibility.Collapsed;
            secondary.Content = Properties.Resources.cancel;
            onSecondary = Close;

            CancellationTokenSource cancel = new CancellationTokenSource();
            working = cancel;
            Progress<(long got, long total)> got = new Progress<(long got, long total)>(p =>
            {
                if (p.total > 0)
                {
                    progress.IsIndeterminate = false;
                    progress.Value = Math.Min(100, p.got * 100.0 / p.total);
                    subheading.Text = Helper.byte2AUnit(p.got) + " / " + Helper.byte2AUnit(p.total);
                }
                else
                {
                    subheading.Text = Helper.byte2AUnit(p.got);
                }
            });

            Task.Run(() => google ? downloadGoogle(got, cancel.Token) : download(got, cancel.Token)).ContinueWith(task =>
            {
                working = null;
                if (cancel.IsCancellationRequested)
                {
                    // Closed just as the file was checked: it is not run, and not left behind.
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        string folder = Path.GetDirectoryName(task.Result.Name);
                        task.Result.Dispose();
                        tryDeleteFolder(folder);
                    }
                    return;
                }
                if (task.IsFaulted || task.IsCanceled)
                {
                    Exception e = task.Exception?.GetBaseException() ?? new OperationCanceledException();
                    LogStore.Append(logName + e.Message);
                    showFailure(e is DriverException ? e.Message : string.Format(Properties.Resources.driver_download_failed, e.Message));
                    return;
                }
                runInstaller(task.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>A failure already written for the person.</summary>
        sealed class DriverException : Exception
        {
            public DriverException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// Samsung's page names the current installer; its link is followed to the file, which
        /// is kept only when Windows verifies Samsung's signature on it and it names itself as
        /// the driver. Returns it opened without write or delete sharing, in a folder of its own:
        /// nothing can swap it between the check and the run. The caller closes it.
        /// </summary>
        static async Task<FileStream> download(IProgress<(long, long)> got, CancellationToken cancel)
        {
            string link = null;
            try
            {
                using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(20));
                    link = SamsungDriver.FindDownloadLink(await http.GetStringAsync(SamsungDriver.PageUrl, limit.Token));
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
            {
                if (cancel.IsCancellationRequested)
                    throw;
                LogStore.Append("samsung driver: page not read (" + e.Message + "), using the known link");
            }
            link = link ?? SamsungDriver.KnownFileUrl;
            LogStore.Append("samsung driver: downloading " + link);

            string folder = Path.Combine(Path.GetTempPath(), "FastbootStudio", "driver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string final = Path.Combine(folder, "SAMSUNG_USB_Driver_for_Mobile_Phones.exe");
            FileStream locked = null;

            try
            {
                using (HttpResponseMessage response = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, cancel))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                        throw new DriverException(Properties.Resources.driver_not_https);
                    long total = response.Content.Headers.ContentLength ?? -1;
                    if (total > SamsungDriver.MaxDownloadBytes)
                        throw new DriverException(Properties.Resources.driver_too_big);
                    using (Stream source = await response.Content.ReadAsStreamAsync(cancel))
                    using (FileStream target = new FileStream(final, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[1 << 16];
                        long done = 0;
                        DateTime shown = DateTime.MinValue;
                        while (true)
                        {
                            int read = await source.ReadAsync(buffer, 0, buffer.Length, cancel);
                            if (read == 0)
                                break;
                            done += read;
                            if (done > SamsungDriver.MaxDownloadBytes)
                                throw new DriverException(Properties.Resources.driver_too_big);
                            await target.WriteAsync(buffer, 0, read, cancel);
                            if (DateTime.UtcNow - shown > TimeSpan.FromMilliseconds(150))
                            {
                                shown = DateTime.UtcNow;
                                got.Report((done, total));
                            }
                        }
                        got.Report((done, total));
                    }
                }

                locked = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read);
                string signer = Authenticode.VerifiedSigner(locked);
                LogStore.Append("samsung driver: signed by " + (signer?.Replace("\r\n", ", ") ?? "nobody Windows trusts"));
                if (signer == null || !SamsungDriver.IsSamsungSubject(signer))
                    throw new DriverException(Properties.Resources.driver_signature_bad);
                // Read from the signed file itself, so another Samsung-signed program is not run instead.
                string product = FileVersionInfo.GetVersionInfo(final).ProductName;
                LogStore.Append("samsung driver: product " + (product ?? "(none)"));
                if (!SamsungDriver.IsInstallerProduct(product))
                    throw new DriverException(Properties.Resources.driver_signature_bad);
                return locked;
            }
            catch (Exception)
            {
                locked?.Dispose();
                tryDeleteFolder(folder);
                throw;
            }
        }

        /// <summary>
        /// Google's catalogue names the current zip with its size and SHA-1 (r13 is pinned by
        /// SHA-256 too); the zip is kept only when it matches, and its driver folder is unpacked
        /// into a folder of its own. Returns the setup file opened without write or delete
        /// sharing, so it cannot be swapped before pnputil reads it. The caller closes it.
        /// </summary>
        static async Task<FileStream> downloadGoogle(IProgress<(long, long)> got, CancellationToken cancel)
        {
            GoogleDriver.DriverArchive archive = null;
            try
            {
                using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(20));
                    archive = GoogleDriver.FromCatalog(await http.GetStringAsync(GoogleDriver.CatalogUrl, limit.Token));
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
            {
                if (cancel.IsCancellationRequested)
                    throw;
                LogStore.Append("google driver: catalogue not read (" + e.Message + "), using r13");
            }
            archive = archive ?? GoogleDriver.Known;
            LogStore.Append("google driver: downloading r" + archive.Revision + " " + archive.Url);

            string folder = Path.Combine(Path.GetTempPath(), "FastbootStudio", "driver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string zipPath = Path.Combine(folder, "usb_driver.zip");
            FileStream locked = null;
            try
            {
                using (HttpResponseMessage response = await http.GetAsync(archive.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                        throw new DriverException(Properties.Resources.driver_not_https);
                    long total = response.Content.Headers.ContentLength ?? archive.Size;
                    if (total > GoogleDriver.MaxDownloadBytes)
                        throw new DriverException(Properties.Resources.driver_too_big);
                    using (Stream from = await response.Content.ReadAsStreamAsync(cancel))
                    using (FileStream target = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[1 << 16];
                        long done = 0;
                        DateTime shown = DateTime.MinValue;
                        while (true)
                        {
                            int read = await from.ReadAsync(buffer, 0, buffer.Length, cancel);
                            if (read == 0)
                                break;
                            done += read;
                            if (done > GoogleDriver.MaxDownloadBytes)
                                throw new DriverException(Properties.Resources.driver_too_big);
                            await target.WriteAsync(buffer, 0, read, cancel);
                            if (DateTime.UtcNow - shown > TimeSpan.FromMilliseconds(150))
                            {
                                shown = DateTime.UtcNow;
                                got.Report((done, total));
                            }
                        }
                        got.Report((done, total));
                    }
                }

                string inf;
                using (FileStream zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    string mismatch = GoogleDriver.Check(zip, archive);
                    LogStore.Append("google driver: " + (mismatch ?? "size and checksum match"));
                    if (mismatch != null)
                        throw new DriverException(string.Format(Properties.Resources.driver_google_check_bad, mismatch));
                    inf = GoogleDriver.Extract(zip, Path.Combine(folder, "usb_driver"));
                }
                locked = new FileStream(inf, FileMode.Open, FileAccess.Read, FileShare.Read);
                return locked;
            }
            catch (Exception)
            {
                locked?.Dispose();
                tryDeleteFolder(folder);
                throw;
            }
        }

        /// <summary>Runs the checked installer; <paramref name="locked"/> stays open until it has ended.</summary>
        void runInstaller(FileStream locked)
        {
            if (google)
            {
                runPnputil(locked);
                return;
            }
            string path = locked.Name;
            heading.Text = Properties.Resources.driver_installing;
            subheading.Text = Path.GetFileName(path);
            message.Text = Properties.Resources.driver_installing_note;
            progress.IsIndeterminate = true;
            primary.Visibility = Visibility.Collapsed;
            secondary.Visibility = Visibility.Collapsed;

            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
            }
            catch (Win32Exception e)
            {
                locked.Dispose();
                tryDeleteFolder(Path.GetDirectoryName(path));
                secondary.Visibility = Visibility.Visible;
                // 1223: the person said no to Windows' permission prompt.
                showFailure(e.NativeErrorCode == 1223 ? Properties.Resources.driver_no_permission : e.Message, retry: true);
                return;
            }
            installerRunning = true;
            LogStore.Append(logName + "installer started");
            Task.Run(() =>
            {
                try
                {
                    process?.WaitForExit();
                }
                finally
                {
                    process?.Dispose();
                    locked.Dispose();
                    tryDeleteFolder(Path.GetDirectoryName(path));
                }
            }).ContinueWith(_ =>
            {
                installerRunning = false;
                secondary.Visibility = Visibility.Visible;
                showResult();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// pnputil adds the driver to Windows' store and installs it on any plugged-in device it
        /// fits; it asks for permission (it needs administrator rights) and refuses a driver
        /// whose signature Windows does not trust. The unpacked folder is deleted afterwards.
        /// </summary>
        void runPnputil(FileStream locked)
        {
            string inf = locked.Name;
            string folder = Path.GetDirectoryName(Path.GetDirectoryName(inf));
            heading.Text = Properties.Resources.driver_google_installing;
            subheading.Text = Path.GetFileName(inf);
            message.Text = Properties.Resources.driver_google_installing_note;
            progress.IsIndeterminate = true;
            primary.Visibility = Visibility.Collapsed;
            secondary.Visibility = Visibility.Collapsed;

            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), GoogleDriver.PnputilArguments(inf))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(inf),
                });
            }
            catch (Win32Exception e)
            {
                locked.Dispose();
                tryDeleteFolder(folder);
                secondary.Visibility = Visibility.Visible;
                showFailure(e.NativeErrorCode == 1223 ? Properties.Resources.driver_no_permission : e.Message, retry: true);
                return;
            }
            installerRunning = true;
            LogStore.Append("google driver: pnputil started");
            Task.Run(() =>
            {
                try
                {
                    process?.WaitForExit();
                    return process?.ExitCode ?? -1;
                }
                finally
                {
                    process?.Dispose();
                    locked.Dispose();
                    tryDeleteFolder(folder);
                }
            }).ContinueWith(task =>
            {
                installerRunning = false;
                secondary.Visibility = Visibility.Visible;
                int code = task.Status == TaskStatus.RanToCompletion ? task.Result : -1;
                LogStore.Append("google driver: pnputil ended with " + code);
                if (!GoogleDriver.PnputilSucceeded(code))
                {
                    showFailure(string.Format(Properties.Resources.driver_google_failed_code, code));
                    return;
                }
                showResult();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void showResult()
        {
            progress.Visibility = Visibility.Collapsed;
            if (installed())
            {
                LogStore.Append(logName + "installed");
                heading.Text = google ? Properties.Resources.driver_google_done : Properties.Resources.driver_done;
                subheading.Text = Title;
                setLook("Ok", "AccentSoft", "AccentLine", GlyphDone);
                message.Text = google ? Properties.Resources.driver_google_done_note : Properties.Resources.driver_done_note;
                primary.Visibility = Visibility.Visible;
                primary.Content = Properties.Resources.driver_close;
                onPrimary = Close;
                secondary.Visibility = Visibility.Collapsed;
                return;
            }
            // Some installers hand over to a second process and return at once.
            heading.Text = Properties.Resources.driver_not_yet;
            setLook("Warn", "WarnSoft", "WarnLine", GlyphDownload);
            message.Text = Properties.Resources.driver_not_yet_note;
            primary.Visibility = Visibility.Visible;
            primary.Content = Properties.Resources.driver_check_again;
            onPrimary = showResult;
            secondary.Content = Properties.Resources.driver_close;
            onSecondary = Close;
        }

        void showFailure(string why, bool retry = false)
        {
            heading.Text = google ? Properties.Resources.driver_google_failed : Properties.Resources.driver_failed;
            subheading.Text = Title;
            setLook("Danger", "DangerSoft", "DangerLine", GlyphError);
            progress.Visibility = Visibility.Collapsed;
            message.Text = why + "\n\n" + (google ? Properties.Resources.driver_google_failed_note : Properties.Resources.driver_failed_note);
            primary.Visibility = Visibility.Visible;
            secondary.Visibility = Visibility.Visible;
            if (retry)
            {
                primary.Content = Properties.Resources.driver_try_again;
                onPrimary = start;
            }
            else
            {
                primary.Content = google ? Properties.Resources.driver_google_open_page : Properties.Resources.driver_open_page;
                onPrimary = () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(google ? GoogleDriver.PageUrl : SamsungDriver.PageUrl) { UseShellExecute = true });
                    }
                    catch (Exception e)
                    {
                        ThemedDialog.Show(e.Message);
                    }
                    Close();
                };
            }
            secondary.Content = Properties.Resources.driver_close;
            onSecondary = Close;
        }

        void setLook(string color, string soft, string line, string path)
        {
            strip.Background = Palette.Get(color);
            badge.Background = Palette.Get(soft);
            badge.BorderBrush = Palette.Get(line);
            glyph.Stroke = Palette.Get(color);
            glyph.Data = Geometry.Parse(path);
        }

        static void tryDeleteFolder(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
