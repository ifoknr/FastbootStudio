using FastbootEnhance.Core.Update;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FastbootEnhance
{
    /// <summary>
    /// The update dialog: the new version's notes, then a download that is kept only when its
    /// SHA-256 matches what the release says. The app never replaces itself: the person closes
    /// it and unzips the new version, the same way they installed it.
    /// </summary>
    public partial class UpdateWindow : Window
    {
        const string GlyphDownload = "M12,3 V15 M7,10.5 L12,15.5 L17,10.5 M5,20 H19";
        const string GlyphDone = "M5,12.5 L10,17.5 L19.5,7";
        const string GlyphError = "M12,2 A10,10 0 1 1 11.99,2 Z M9,9 L15,15 M15,9 L9,15";

        readonly ReleaseInfo release;
        CancellationTokenSource downloading;
        string savedPath;

        // What the two buttons do in the current state.
        Action onPrimary;
        Action onSecondary;

        UpdateWindow(ReleaseInfo release)
        {
            InitializeComponent();
            FlowDirection = Languages.Flow;
            this.release = release;

            header.MouseLeftButtonDown += delegate { DragMove(); };
            SourceInitialized += delegate { DarkTitleBar.Apply(this); };
            primary.Click += delegate { if (onPrimary != null) onPrimary(); };
            secondary.Click += delegate { if (onSecondary != null) onSecondary(); };
            skip.Click += delegate
            {
                UpdateUI.Skip(release.Tag);
                Close();
            };
            Closing += delegate { if (downloading != null) downloading.Cancel(); };
            PreviewKeyDown += onKey;

            showOffer();
        }

        /// <summary>Shows the dialog for <paramref name="release"/> over the main window.</summary>
        public static void Show(ReleaseInfo release)
        {
            UpdateWindow dialog = new UpdateWindow(release);
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
        }

        void showOffer()
        {
            Title = string.Format(Properties.Resources.update_available_title, release.Tag);
            heading.Text = Title;
            subheading.Text = release.Published == null
                ? string.Format(Properties.Resources.update_have_nodate, MainWindow.version)
                : string.Format(Properties.Resources.update_have, MainWindow.version,
                    release.Published.Value.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
            setLook("Accent", "AccentSoft", "AccentLine", GlyphDownload);

            var list = Languages.RightToLeft && release.NotesArabic.Count > 0 ? release.NotesArabic
                : release.NotesEnglish.Count > 0 ? release.NotesEnglish
                : release.NotesArabic;
            notes.ItemsSource = list.Count > 0 ? list.ToList() : new[] { Properties.Resources.update_no_notes }.ToList();
            notes_panel.Visibility = Visibility.Visible;
            progress.Visibility = Visibility.Collapsed;

            skip.Visibility = Visibility.Visible;
            secondary.Content = Properties.Resources.update_later;
            onSecondary = Close;
            if (release.ZipUrl == null)
            {
                message.Text = Properties.Resources.update_no_zip;
                primary.Content = Properties.Resources.update_open_page;
                onPrimary = () => { UpdateUI.Open(release.Page); Close(); };
            }
            else
            {
                message.Text = Properties.Resources.update_note;
                primary.Content = Properties.Resources.update_download;
                onPrimary = startDownload;
            }
        }

        void startDownload()
        {
            heading.Text = string.Format(Properties.Resources.update_downloading, release.Tag);
            subheading.Text = release.ZipName;
            notes_panel.Visibility = Visibility.Collapsed;
            progress.Visibility = Visibility.Visible;
            progress.Value = 0;
            progress.IsIndeterminate = release.ZipBytes <= 0;
            message.Text = Properties.Resources.update_downloading_note;
            skip.Visibility = Visibility.Collapsed;
            primary.Visibility = Visibility.Collapsed;
            secondary.Content = Properties.Resources.cancel;
            onSecondary = Close;

            CancellationTokenSource cancel = new CancellationTokenSource();
            downloading = cancel;
            Progress<long> got = new Progress<long>(bytes =>
            {
                if (release.ZipBytes <= 0)
                    return;
                progress.Value = Math.Min(100, bytes * 100.0 / release.ZipBytes);
                subheading.Text = release.ZipName + " · " + Helper.byte2AUnit(bytes) + " / " + Helper.byte2AUnit(release.ZipBytes);
            });

            Task.Run(() => download(release, got, cancel.Token)).ContinueWith(task =>
            {
                downloading = null;
                if (cancel.IsCancellationRequested)
                    return;
                if (task.IsFaulted)
                {
                    Exception e = task.Exception.GetBaseException();
                    LogStore.Append("update download failed: " + e.Message);
                    showFailure(e is UpdateException ? e.Message : string.Format(Properties.Resources.update_download_failed, e.Message));
                    return;
                }
                savedPath = task.Result;
                LogStore.Append("update downloaded and checked: " + savedPath);
                showDone();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void showDone()
        {
            heading.Text = Properties.Resources.update_done_title;
            subheading.Text = string.Format(Properties.Resources.update_done_where, Path.GetFileName(savedPath));
            setLook("Ok", "AccentSoft", "AccentLine", GlyphDone);
            progress.Visibility = Visibility.Collapsed;
            message.Text = Properties.Resources.update_done_note;
            primary.Visibility = Visibility.Visible;
            primary.Content = Properties.Resources.update_open_close;
            onPrimary = () =>
            {
                showInFolder(savedPath);
                Close();
                if (UpdateUI.Busy)
                {
                    ThemedDialog.Show(Properties.Resources.update_busy);
                    return;
                }
                if (MainWindow.THIS != null)
                    MainWindow.THIS.Close();
            };
            secondary.Content = Properties.Resources.update_open_folder;
            onSecondary = () => { showInFolder(savedPath); Close(); };
        }

        void showFailure(string why)
        {
            heading.Text = Properties.Resources.update_failed_title;
            subheading.Text = release.ZipName;
            setLook("Danger", "DangerSoft", "DangerLine", GlyphError);
            progress.Visibility = Visibility.Collapsed;
            message.Text = why;
            primary.Visibility = Visibility.Visible;
            primary.Content = Properties.Resources.update_open_page;
            onPrimary = () => { UpdateUI.Open(release.Page); Close(); };
            secondary.Content = Properties.Resources.update_close;
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

        void onKey(object sender, KeyEventArgs e)
        {
            // Escape only closes (and stops a download); it never opens a folder or a page.
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                try
                {
                    Clipboard.SetText(heading.Text + "\n" + subheading.Text + "\n\n"
                        + string.Join("\n", (notes.ItemsSource ?? new string[0]).Cast<object>().Select(n => "- " + n))
                        + "\n\n" + message.Text);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                }
                e.Handled = true;
            }
        }

        /// <summary>A failure whose message is already written for the person.</summary>
        sealed class UpdateException : Exception
        {
            public UpdateException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// Downloads the zip next to its final name, checks it against the release's SHA-256,
        /// and only then gives it its name in Downloads. A file that does not match is deleted.
        /// </summary>
        static async Task<string> download(ReleaseInfo release, IProgress<long> got, CancellationToken cancel)
        {
            string expected = release.ZipSha256;
            if (expected == null && release.Sha256Url != null)
                expected = ReleaseFeed.ParseSha256File(await UpdateUI.ReadText(release.Sha256Url, cancel), release.ZipName);
            if (expected == null)
                throw new UpdateException(Properties.Resources.update_no_hash);

            string folder = UpdateUI.DownloadsFolder();
            Directory.CreateDirectory(folder);
            string final = Path.Combine(folder, release.ZipName);
            string part = final + ".part";

            string actual;
            try
            {
                using (Stream source = await UpdateUI.OpenRead(release.ZipUrl, cancel))
                using (FileStream target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                using (IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    byte[] buffer = new byte[1 << 16];
                    long total = 0;
                    DateTime shown = DateTime.MinValue;
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer, 0, buffer.Length, cancel);
                        if (read == 0)
                            break;
                        sha.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer, 0, read, cancel);
                        total += read;
                        if (DateTime.UtcNow - shown > TimeSpan.FromMilliseconds(150))
                        {
                            shown = DateTime.UtcNow;
                            got.Report(total);
                        }
                    }
                    got.Report(total);
                    actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }
            }
            catch (Exception)
            {
                tryDelete(part);
                throw;
            }

            if (actual != expected)
            {
                tryDelete(part);
                LogStore.Append("update: SHA-256 " + actual + " does not match " + expected);
                throw new UpdateException(Properties.Resources.update_hash_mismatch);
            }
            File.Move(part, final, true);
            return final;
        }

        static void tryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static void showInFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message);
            }
        }
    }
}
