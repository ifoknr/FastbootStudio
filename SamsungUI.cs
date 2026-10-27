using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows;
using FastbootEnhance.Core.Adb;
using FastbootEnhance.Core.Fastboot;
using FastbootEnhance.Core.Samsung;

namespace FastbootEnhance
{
    /// <summary>
    /// The Samsung page: opens Samsung firmware (AP, BL, CP, CSC, HOME_CSC), checks each file's
    /// MD5, takes images out of it with LZ4 undone, compares it with the phone over adb, and
    /// builds the tars Magisk and Odin use. Nothing here talks to a phone in Download mode or
    /// writes to a phone; flashing stays with Odin.
    /// </summary>
    static class SamsungUI
    {
        static MainWindow W => MainWindow.THIS;

        static FirmwareSet firmware;
        static SamsungPhone phone;
        static string outputFolder;
        static string lastSuper;

        static readonly ObservableCollection<SamsungFileRow> fileRows = new ObservableCollection<SamsungFileRow>();
        static readonly ObservableCollection<SamsungEntryRow> entryRows = new ObservableCollection<SamsungEntryRow>();

        public static volatile bool busy;
        static CancellationTokenSource cancel;
        static volatile Thread worker;

        static readonly string[] BootImages = { "boot.img", "init_boot.img", "vendor_boot.img", "vbmeta.img", "recovery.img", "dtbo.img" };

        static void log(string line) => LogStore.Append("samsung: " + line);

        static void ui(Action action) => W.Dispatcher.BeginInvoke(action);

        public static void init()
        {
            W.samsung_files.ItemsSource = fileRows;
            W.samsung_entries.ItemsSource = entryRows;
            showLoaded(false);

            W.samsung_open.Click += delegate { pickFiles(); };
            W.samsung_open_other.Click += delegate { pickFiles(); };
            W.samsung_open_folder_pick.Click += delegate { Helper.pathSelect(path => open(new[] { path })); };
            W.samsung_close.Click += delegate
            {
                if (!busy)
                    close();
            };

            foreach (UIElement target in new UIElement[] { W.samsung_empty, W.samsung_loaded })
            {
                target.DragEnter += dragOver;
                target.DragOver += dragOver;
                target.Drop += delegate (object sender, DragEventArgs e)
                {
                    string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0 && !busy)
                        open(files);
                };
            }

            SamsungEntryRow.SelectionChanged += showSelection;
            W.samsung_select_boot.Click += delegate
            {
                foreach (SamsungEntryRow row in entryRows)
                    row.Selected = BootImages.Contains(row.Entry.PlainName, StringComparer.OrdinalIgnoreCase);
            };
            W.samsung_select_none.Click += delegate
            {
                foreach (SamsungEntryRow row in entryRows)
                    row.Selected = false;
            };

            W.samsung_change_folder.Click += delegate
            {
                Helper.pathSelect(delegate (string path)
                {
                    outputFolder = path;
                    showOutputFolder();
                });
            };
            W.samsung_open_out.Click += delegate { openInExplorer(outputFolder); };
            W.samsung_super_tools.Click += delegate
            {
                if (lastSuper != null && File.Exists(lastSuper) && !ImageToolsUI.busy)
                {
                    W.main_tabs.SelectedItem = W.images_tab;
                    ImageToolsUI.openFromPath(lastSuper);
                }
            };
            W.samsung_read_phone.Click += delegate { readPhone(true); };
            W.samsung_extract.Click += delegate { extract(); };
            W.samsung_magisk.Click += delegate { magisk(); };
            W.samsung_check_patched.Click += delegate { checkPatchedFile(); };
            W.samsung_build_tar.Click += delegate { buildTar(); };
            W.samsung_cancel.Click += delegate { cancel?.Cancel(); };
            W.samsung_driver_install.Click += delegate { DriverUI.InstallSamsung(showDriver); };
            showDriver();
        }

        /// <summary>Whether Samsung's USB driver is installed, which Download mode needs; read off the UI thread.</summary>
        static void showDriver()
        {
            bool installed = false;
            Helper.offloadAndRun(delegate { installed = DriverUI.SamsungInstalled(); }, delegate
            {
                W.samsung_driver_text.Text = installed ? Properties.Resources.samsung_driver_ok : Properties.Resources.samsung_driver_missing;
                W.samsung_driver_text.Foreground = Palette.Get(installed ? "Dim" : "Warn");
                W.samsung_driver_mark.Text = installed ? "✓" : "!";
                W.samsung_driver_mark.Foreground = Palette.Get(installed ? "Ok" : "Warn");
                W.samsung_driver_install.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            });
        }

        static void dragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !busy ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>Stops a running job when the window closes and waits for it to clean up.</summary>
        public static void shutdown()
        {
            cancel?.Cancel();
            worker?.Join(TimeSpan.FromSeconds(10));
        }

        // ------------------------------------------------------------------ open

        static void pickFiles()
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = Properties.Resources.samsung_filter + "|*.tar.md5;*.tar;*.zip|" + Properties.Resources.all_files + "|*.*",
            };
            if (dialog.ShowDialog() == true)
                open(dialog.FileNames);
        }

        static void open(string[] paths)
        {
            if (busy)
                return;
            busy = true;
            W.samsung_open_buttons.Visibility = Visibility.Hidden;
            W.samsung_opening.Visibility = Visibility.Visible;
            W.samsung_opening_bar.IsIndeterminate = true;
            W.samsung_opening_text.Text = Properties.Resources.samsung_opening;

            CancellationTokenSource source = new CancellationTokenSource();
            cancel = source;
            Thread thread = new Thread(delegate ()
            {
                FirmwareSet set = null;
                Dictionary<FirmwareFile, List<TarEntry>> listed = new Dictionary<FirmwareFile, List<TarEntry>>();
                Dictionary<FirmwareFile, string> unreadable = new Dictionary<FirmwareFile, string>();
                Exception failure = null;
                try
                {
                    // The zip a firmware site gives: its tars are unpacked next to it first.
                    if (paths.Length == 1 && paths[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(paths[0]))
                        paths = new[] { unzip(paths[0], source.Token) };

                    set = paths.Length == 1 && Directory.Exists(paths[0])
                        ? FirmwareSet.FromFolder(paths[0])
                        : FirmwareSet.FromPaths(paths.Where(File.Exists));
                    if (set.Files.Count == 0)
                        throw new InvalidDataException(Properties.Resources.samsung_not_firmware);

                    foreach (FirmwareFile f in set.Files)
                    {
                        try
                        {
                            using (FileStream s = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                                listed[f] = OdinTar.List(s);
                        }
                        catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
                        {
                            unreadable[f] = e.Message;
                        }
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }

                ui(delegate
                {
                    worker = null;
                    cancel = null;
                    busy = false;
                    W.samsung_open_buttons.Visibility = Visibility.Visible;
                    W.samsung_opening.Visibility = Visibility.Hidden;
                    W.samsung_opening_bar.IsIndeterminate = false;
                    if (failure is OperationCanceledException)
                        return;
                    if (failure != null)
                    {
                        log("cannot open " + string.Join(", ", paths) + ": " + failure.Message);
                        ThemedDialog.Show(failure.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    show(set, listed, unreadable);
                    verifyAll();
                    readPhone(false);
                });
            });
            thread.IsBackground = true;
            worker = thread;
            thread.Start();
        }

        /// <summary>Unpacks the firmware tars of a zip into a folder named after it; returns the folder.</summary>
        static string unzip(string zipPath, CancellationToken token)
        {
            string folder = Path.Combine(Path.GetDirectoryName(zipPath), Path.GetFileNameWithoutExtension(zipPath));
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                List<ZipArchiveEntry> tars = zip.Entries
                    .Where(e => FirmwareFile.FromPath(e.Name) != null
                        && (e.Name.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (tars.Count == 0)
                    throw new InvalidDataException(Properties.Resources.samsung_not_firmware);
                Directory.CreateDirectory(folder);
                long total = Math.Max(1, tars.Sum(e => e.Length)), done = 0;
                byte[] buffer = new byte[1 << 20];
                foreach (ZipArchiveEntry e in tars)
                {
                    string target = Path.Combine(folder, Path.GetFileName(e.Name));
                    if (File.Exists(target) && new FileInfo(target).Length == e.Length)
                    {
                        done += e.Length;
                        continue; // unpacked before
                    }
                    log("unzipping " + e.Name);
                    string part = target + ".part";
                    using (Stream input = e.Open())
                    using (FileStream output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, n);
                            done += n;
                            int percent = (int)(done * 100 / total);
                            ui(delegate
                            {
                                W.samsung_opening_bar.IsIndeterminate = false;
                                W.samsung_opening_bar.Value = percent;
                                W.samsung_opening_text.Text = string.Format(Properties.Resources.samsung_unzipping, percent);
                            });
                        }
                    }
                    if (File.Exists(target))
                        File.Delete(target);
                    File.Move(part, target);
                }
            }
            return folder;
        }

        static void show(FirmwareSet set, Dictionary<FirmwareFile, List<TarEntry>> listed, Dictionary<FirmwareFile, string> unreadable)
        {
            firmware = set;
            fileRows.Clear();
            entryRows.Clear();
            lastSuper = null;
            W.samsung_super_tools.Visibility = Visibility.Collapsed;
            foreach (FirmwareFile f in set.Files)
            {
                SamsungFileRow row = new SamsungFileRow(f, new FileInfo(f.Path).Length);
                if (unreadable.TryGetValue(f, out string problem))
                {
                    row.Current = SamsungFileRow.Check.Unreadable;
                    row.Problem = problem;
                }
                fileRows.Add(row);
                if (listed.TryGetValue(f, out List<TarEntry> entries))
                    foreach (TarEntry e in entries)
                        entryRows.Add(new SamsungEntryRow(row, e));
            }

            SamsungBuild build = set.Build;
            W.samsung_build_chip.Text = build?.Text ?? set.Files[0].FileName;
            List<InfoRow> info = new List<InfoRow>();
            if (set.Model != null)
                info.Add(new InfoRow(Properties.Resources.samsung_info_model, set.Model));
            if (build != null)
            {
                info.Add(new InfoRow(Properties.Resources.samsung_info_build, build.Text));
                info.Add(new InfoRow(Properties.Resources.samsung_info_binary, build.BinaryChar.ToString()));
                info.Add(new InfoRow(Properties.Resources.samsung_info_date, build.Year + "-" + build.Month.ToString("00")));
            }
            if (set.Android != null)
                info.Add(new InfoRow(Properties.Resources.samsung_info_android, set.Android.ToString()));
            if (set.CscCode != null)
                info.Add(new InfoRow(Properties.Resources.samsung_info_csc, set.CscCode));
            if (set.Unknown.Count > 0)
                info.Add(new InfoRow(Properties.Resources.samsung_info_other, string.Join(", ", set.Unknown.Select(Path.GetFileName))));
            W.samsung_info.ItemsSource = info;

            // Next to the firmware, in a folder named after its build.
            outputFolder = Path.Combine(Path.GetDirectoryName(set.Files[0].Path), (build?.Text ?? "firmware") + "_files");
            showOutputFolder();
            showMatch();
            showSelection();
            showLoaded(true);
            log("opened " + string.Join(", ", set.Files.Select(f => f.FileName)) + " (" + entryRows.Count + " files inside)");
        }

        static void close()
        {
            firmware = null;
            fileRows.Clear();
            entryRows.Clear();
            showLoaded(false);
        }

        static void showLoaded(bool loaded)
        {
            W.samsung_empty.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
            W.samsung_loaded.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
            W.samsung_header_actions.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        }

        static void showOutputFolder()
        {
            W.samsung_out_folder.Text = outputFolder;
            W.samsung_out_folder.ToolTip = outputFolder;
        }

        static void showSelection()
        {
            List<SamsungEntryRow> chosen = entryRows.Where(r => r.Selected).ToList();
            W.samsung_selected_count.Text = chosen.Count == 0 ? "" : string.Format(Properties.Resources.samsung_selected,
                chosen.Count, Helper.byte2AUnit(chosen.Sum(r => r.Entry.Size)));
        }

        // ------------------------------------------------------------------ firmware against phone

        /// <summary>Reads the one Samsung phone in Android over adb. Quiet unless asked by the button.</summary>
        static void readPhone(bool asked)
        {
            W.samsung_read_phone.IsEnabled = false;
            W.samsung_phone_note.Text = Properties.Resources.samsung_phone_reading;
            SamsungPhone found = null;
            string problem = null;
            Helper.offloadAndRun(delegate
            {
                try
                {
                    Adb.Result list = Adb.Run("devices -l", Adb.ShortCommand);
                    List<AdbDevice> phones = AdbDevice.ParseList(list.Output).Where(d => d.State == "device").ToList();
                    foreach (AdbDevice d in phones)
                    {
                        Adb.Result r = Adb.Run(AdbCommand.Shell(d.Serial, SamsungPhone.Command), Adb.ShortCommand);
                        if (!r.Succeeded)
                            continue;
                        SamsungPhone p = SamsungPhone.Parse(d.Serial, r.Output);
                        // A Samsung first; another phone only when it is the only one.
                        if (p.IsSamsung || found == null)
                            found = p;
                        if (p.IsSamsung)
                            break;
                    }
                    if (found == null)
                        problem = Properties.Resources.samsung_phone_none;
                }
                catch (FileNotFoundException)
                {
                    problem = Properties.Resources.samsung_phone_none;
                }
                catch (Exception e)
                {
                    problem = e.Message;
                }
            }, delegate
            {
                W.samsung_read_phone.IsEnabled = true;
                if (found != null)
                {
                    phone = found;
                    log("phone " + found.Serial + ": " + found.Maker + " " + found.Model + ", bootloader " + (found.Bootloader ?? "?")
                        + ", Android " + (found.Android ?? "?") + ", CSC " + (found.CscVersion ?? "?"));
                }
                else if (asked)
                {
                    phone = null;
                }
                showMatch(found == null ? problem : null);
            });
        }

        static void showMatch(string phoneProblem = null)
        {
            if (firmware == null)
                return;
            List<MatchFinding> findings = FirmwareMatch.Check(firmware, phone);
            List<FindingRow> rows = new List<FindingRow>();
            foreach (MatchFinding f in findings)
            {
                if (f.Code == MatchCode.PhoneUnknown)
                    continue;
                string key = f.Level == MatchLevel.Block ? "Danger" : f.Level == MatchLevel.Warn ? "Warn" : f.Level == MatchLevel.Notice ? "Warn" : "Ok";
                string glyph = f.Level == MatchLevel.Block ? "✕" : f.Level == MatchLevel.Ok ? "✓" : "!";
                string title, detail;
                describe(f, out title, out detail);
                rows.Add(new FindingRow(glyph, key, title, detail));
            }
            W.samsung_findings.ItemsSource = rows;

            bool known = phone != null && phone.Model != null;
            if (!known)
            {
                W.samsung_verdict.Visibility = Visibility.Collapsed;
                W.samsung_phone_note.Text = phoneProblem ?? Properties.Resources.samsung_phone_none;
                return;
            }
            MatchLevel overall = FirmwareMatch.Overall(findings);
            string brush = overall == MatchLevel.Block ? "Danger" : overall == MatchLevel.Warn ? "Warn" : "Ok";
            W.samsung_verdict.Visibility = Visibility.Visible;
            W.samsung_verdict.Background = Palette.Get(brush == "Ok" ? "AccentSoft" : brush + "Soft");
            W.samsung_verdict.BorderBrush = Palette.Get(brush == "Ok" ? "AccentLine" : brush + "Line");
            W.samsung_verdict_title.Foreground = Palette.Get(brush);
            W.samsung_verdict_title.Text = overall == MatchLevel.Block ? Properties.Resources.samsung_verdict_block
                : overall == MatchLevel.Warn ? Properties.Resources.samsung_verdict_warn
                : Properties.Resources.samsung_verdict_ok;
            W.samsung_verdict_text.Text = overall == MatchLevel.Block ? Properties.Resources.samsung_verdict_block_text
                : overall == MatchLevel.Warn ? Properties.Resources.samsung_verdict_warn_text
                : Properties.Resources.samsung_verdict_ok_text;
            W.samsung_phone_note.Text = string.Format(Properties.Resources.samsung_phone_read, Helper.ltr(phone.Model),
                Helper.ltr(phone.Serial));
        }

        static void describe(MatchFinding f, out string title, out string detail)
        {
            string fw = Helper.ltr(f.Firmware ?? ""), ph = Helper.ltr(f.Phone ?? "");
            switch (f.Code)
            {
                case MatchCode.NotSamsung:
                    title = Properties.Resources.samsung_m_not_samsung;
                    detail = ph;
                    break;
                case MatchCode.SameModel:
                    title = Properties.Resources.samsung_m_same_model;
                    detail = fw;
                    break;
                case MatchCode.OtherModel:
                    title = Properties.Resources.samsung_m_other_model;
                    detail = string.Format(Properties.Resources.samsung_m_fw_phone, fw, ph);
                    break;
                case MatchCode.SameBinary:
                    title = Properties.Resources.samsung_m_same_binary;
                    detail = string.Format(Properties.Resources.samsung_m_fw_phone, fw, ph);
                    break;
                case MatchCode.NewerBinary:
                    title = Properties.Resources.samsung_m_newer_binary;
                    detail = string.Format(Properties.Resources.samsung_m_newer_binary_detail, fw, ph);
                    break;
                case MatchCode.OlderBinary:
                    title = Properties.Resources.samsung_m_older_binary;
                    detail = string.Format(Properties.Resources.samsung_m_older_binary_detail, fw, ph);
                    break;
                case MatchCode.SameAndroid:
                case MatchCode.NewerAndroid:
                    title = Properties.Resources.samsung_m_android;
                    detail = string.Format(Properties.Resources.samsung_m_fw_phone, fw, ph);
                    break;
                case MatchCode.OlderAndroid:
                    title = Properties.Resources.samsung_m_older_android;
                    detail = string.Format(Properties.Resources.samsung_m_older_android_detail, fw, ph);
                    break;
                case MatchCode.SameCsc:
                    title = Properties.Resources.samsung_m_same_csc;
                    detail = string.Format(Properties.Resources.samsung_m_fw_phone, fw, ph);
                    break;
                case MatchCode.OtherCsc:
                    title = Properties.Resources.samsung_m_other_csc;
                    detail = string.Format(Properties.Resources.samsung_m_other_csc_detail, fw, ph);
                    break;
                case MatchCode.CscWipes:
                    title = Properties.Resources.samsung_m_csc_wipes;
                    detail = f.Phone != null ? Properties.Resources.samsung_m_csc_wipes_detail : Properties.Resources.samsung_m_csc_wipes_no_home;
                    break;
                case MatchCode.KnoxIntact:
                    title = Properties.Resources.samsung_m_knox_ok;
                    detail = Properties.Resources.samsung_m_knox_ok_detail;
                    break;
                case MatchCode.KnoxTripped:
                    title = Properties.Resources.samsung_m_knox_tripped;
                    detail = Properties.Resources.samsung_m_knox_tripped_detail;
                    break;
                case MatchCode.MixedFirmware:
                    title = Properties.Resources.samsung_m_mixed;
                    detail = fw;
                    break;
                default:
                    title = f.Code.ToString();
                    detail = "";
                    break;
            }
        }

        // ------------------------------------------------------------------ work

        static void setBusy(bool value)
        {
            busy = value;
            foreach (UIElement control in new UIElement[]
            {
                W.samsung_extract, W.samsung_magisk, W.samsung_check_patched, W.samsung_build_tar, W.samsung_change_folder,
                W.samsung_open_other, W.samsung_close, W.samsung_select_boot, W.samsung_select_none, W.samsung_entries,
            })
            {
                control.IsEnabled = !value;
            }
            W.samsung_cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
                Helper.TaskbarItemHelper.start();
            else
                Helper.TaskbarItemHelper.stop();
        }

        static void progress(string what, int percent)
        {
            ui(delegate
            {
                W.samsung_progress.Value = percent;
                W.samsung_progress_text.Text = what + "  ·  " + percent + "%";
                Helper.TaskbarItemHelper.update(percent);
            });
        }

        /// <summary>
        /// Runs one job on a worker thread. <paramref name="work"/> returns the message for the
        /// Done box, or null for none; <paramref name="after"/> runs on the UI thread when it succeeded.
        /// </summary>
        static void run(string what, Func<CancellationToken, string> work, Action after = null, bool quiet = false)
        {
            if (busy)
                return;
            CancellationTokenSource source = new CancellationTokenSource();
            cancel = source;
            setBusy(true);
            W.samsung_progress.Value = 0;
            W.samsung_progress_text.Text = what;
            Stopwatch clock = Stopwatch.StartNew();
            log(what);

            Thread thread = new Thread(delegate ()
            {
                string message = null;
                Exception failure = null;
                try
                {
                    message = work(source.Token);
                }
                catch (Exception e)
                {
                    failure = e;
                }
                string elapsed = clock.Elapsed.TotalSeconds.ToString("F1");
                ui(delegate
                {
                    worker = null;
                    cancel = null;
                    setBusy(false);
                    if (failure == null)
                    {
                        W.samsung_progress.Value = 100;
                        W.samsung_progress_text.Text = string.Format(Properties.Resources.images_done_in, elapsed);
                        log("done in " + elapsed + " s");
                        after?.Invoke();
                        if (message != null && !quiet)
                            ThemedDialog.Done(message);
                    }
                    else if (failure is OperationCanceledException)
                    {
                        W.samsung_progress.Value = 0;
                        W.samsung_progress_text.Text = Properties.Resources.backup_cancelled;
                        log("cancelled");
                    }
                    else
                    {
                        W.samsung_progress_text.Text = "";
                        Helper.TaskbarItemHelper.error();
                        log("failed: " + failure.Message);
                        ThemedDialog.Show(failure.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            });
            thread.IsBackground = true;
            worker = thread;
            thread.Start();
        }

        /// <summary>Checks the MD5 of every file not checked yet. Throws when one does not match.</summary>
        static void verify(IEnumerable<SamsungFileRow> rows, CancellationToken token)
        {
            foreach (SamsungFileRow row in rows.ToList())
            {
                if (row.Current == SamsungFileRow.Check.Ok || row.Current == SamsungFileRow.Check.NoMd5)
                    continue;
                if (row.Current == SamsungFileRow.Check.Bad || row.Current == SamsungFileRow.Check.Unreadable)
                    throw new InvalidDataException(string.Format(Properties.Resources.samsung_refused_bad, row.Name));

                long size = new FileInfo(row.File.Path).Length;
                ui(delegate { row.Current = SamsungFileRow.Check.Checking; row.Percent = 0; });
                string what = string.Format(Properties.Resources.samsung_md5_job, row.Part);
                int last = -1;
                string problem;
                bool hasLine;
                using (FileStream f = new FileStream(row.File.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    hasLine = OdinTar.ReadTrailer(f) != null;
                if (!hasLine)
                {
                    ui(delegate { row.Current = SamsungFileRow.Check.NoMd5; });
                    log(row.Name + ": no MD5 line (a plain tar)");
                    continue;
                }
                try
                {
                    problem = OdinTar.VerifyMd5(row.File.Path, done =>
                    {
                        int percent = (int)(done * 100 / Math.Max(1, size));
                        if (percent == last)
                            return;
                        last = percent;
                        ui(delegate { row.Percent = percent; });
                        progress(what, percent);
                    }, token);
                }
                catch (OperationCanceledException)
                {
                    ui(delegate { row.Current = SamsungFileRow.Check.Waiting; });
                    throw;
                }
                ui(delegate
                {
                    row.Current = problem == null ? SamsungFileRow.Check.Ok : SamsungFileRow.Check.Bad;
                    row.Problem = problem;
                });
                log(row.Name + ": " + (problem ?? "MD5 matches"));
                if (problem != null)
                    throw new InvalidDataException(string.Format(Properties.Resources.samsung_refused_bad, row.Name) + "\n\n" + problem);
            }
        }

        /// <summary>Checks every file once it is opened; a damaged one is shown, not just logged.</summary>
        static void verifyAll()
        {
            run(Properties.Resources.samsung_md5_all, token =>
            {
                verify(fileRows, token);
                return null;
            }, quiet: true);
        }

        static void extract()
        {
            List<SamsungEntryRow> chosen = entryRows.Where(r => r.Selected).ToList();
            if (chosen.Count == 0)
            {
                ThemedDialog.Show(Properties.Resources.samsung_pick_first, Properties.Resources.nav_samsung);
                return;
            }
            string folder = outputFolder;
            List<string> targets = chosen.Select(r => Path.Combine(folder, Path.GetFileName(r.Entry.PlainName))).ToList();
            List<string> existing = targets.Where(File.Exists).ToList();
            if (existing.Count > 0 && !Helper.confirm(string.Format(Properties.Resources.images_overwrite,
                    string.Join("\n", existing.Select(Path.GetFileName))), Properties.Resources.confirm_title, true))
                return;
            foreach (SamsungEntryRow r in chosen)
                r.Current = SamsungEntryRow.Stage.Queued;

            List<string> saved = new List<string>();
            run(string.Format(Properties.Resources.samsung_extract_job, chosen.Count), token =>
            {
                verify(chosen.Select(r => r.File).Distinct(), token);
                foreach (SamsungEntryRow row in chosen)
                {
                    SamsungEntryRow current = row;
                    ui(delegate { current.Current = SamsungEntryRow.Stage.Working; current.Percent = 0; });
                    int last = -1;
                    string what = current.Entry.PlainName;
                    try
                    {
                        string path = FirmwareTools.Extract(current.File.File.Path, current.Entry, folder, done =>
                        {
                            int percent = (int)(done * 100 / Math.Max(1, current.Entry.Size));
                            if (percent == last)
                                return;
                            last = percent;
                            ui(delegate { current.Percent = percent; });
                            progress(what, percent);
                        }, token);
                        saved.Add(path);
                        ui(delegate { current.Current = SamsungEntryRow.Stage.Saved; });
                        log("saved " + path);
                    }
                    catch
                    {
                        ui(delegate { current.Current = SamsungEntryRow.Stage.Failed; });
                        foreach (SamsungEntryRow rest in chosen.Where(r => r.Current == SamsungEntryRow.Stage.Queued))
                        {
                            SamsungEntryRow waiting = rest;
                            ui(delegate { waiting.Current = SamsungEntryRow.Stage.Idle; });
                        }
                        throw;
                    }
                }
                return string.Format(Properties.Resources.samsung_extract_done, saved.Count, folder);
            }, delegate
            {
                lastSuper = saved.FirstOrDefault(p => string.Equals(Path.GetFileName(p), "super.img", StringComparison.OrdinalIgnoreCase));
                W.samsung_super_tools.Visibility = lastSuper != null ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        // ------------------------------------------------------------------ Magisk

        static SamsungFileRow apRow() => fileRows.FirstOrDefault(r => r.File.Part == FirmwarePart.AP);

        /// <summary>
        /// Builds the small tar Magisk patches (init_boot and vbmeta of the AP), sends it to the
        /// phone, waits while Magisk patches it, then fetches and checks magisk_patched-….tar.
        /// </summary>
        static void magisk()
        {
            SamsungFileRow ap = apRow();
            List<TarEntry> entries = ap == null ? null
                : FirmwareTools.MagiskEntries(entryRows.Where(r => r.File == ap).Select(r => r.Entry).ToList());
            if (entries == null)
            {
                ThemedDialog.Show(Properties.Resources.samsung_magisk_no_ap, Properties.Resources.samsung_magisk,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string folder = outputFolder;
            string name = "magisk_input_" + (firmware.Build?.Text ?? "AP") + ".tar";
            string local = Path.Combine(folder, name);
            AdbDevice target = null;
            string pushProblem = null;
            DateTime sentAt = DateTime.MinValue;

            run(Properties.Resources.samsung_magisk_job, token =>
            {
                verify(new[] { ap }, token);
                Directory.CreateDirectory(folder);
                FirmwareTools.CopyEntries(ap.File.Path, entries, local, token);
                log("built " + local + " with " + string.Join(", ", entries.Select(e => e.Name)));
                try
                {
                    Adb.Result list = Adb.Run("devices -l", Adb.ShortCommand);
                    target = AdbDevice.ParseList(list.Output).FirstOrDefault(d => d.State == "device");
                    if (target != null)
                    {
                        Adb.Result clock = Adb.Run(AdbCommand.Shell(target.Serial, "date +%s"), Adb.ShortCommand);
                        sentAt = clock.Succeeded && long.TryParse(clock.Output.Trim(), out long seconds)
                            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddMinutes(-1)
                            : DateTime.UtcNow.AddMinutes(-1);
                        Adb.Result sent = Adb.Run(AdbCommand.Push(target.Serial, local, FirmwareTools.PhoneFolder + "/" + name), Fastboot.LongCommand);
                        if (!sent.Succeeded)
                            pushProblem = sent.Problem;
                    }
                }
                catch (FileNotFoundException)
                {
                    target = null;
                }
                return null;
            }, delegate
            {
                if (target == null || pushProblem != null)
                {
                    if (pushProblem != null)
                        log("could not send " + name + ": " + pushProblem);
                    ThemedDialog.Done(string.Format(Properties.Resources.samsung_magisk_manual, Helper.ltr(name), Helper.ltr(folder)));
                    return;
                }
                log("sent " + name + " to " + target.Serial);
                if (ThemedDialog.Show(string.Format(Properties.Resources.samsung_magisk_on_phone, Helper.ltr(name)),
                        Properties.Resources.samsung_magisk, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                    return;
                fetchPatched(target.Serial, sentAt, folder);
            });
        }

        static void fetchPatched(string serial, DateTime sentAt, string folder)
        {
            string local = null, problem = null;
            FirmwareTools.PatchedCheck check = null;
            run(Properties.Resources.samsung_magisk_fetch, token =>
            {
                Adb.Result listing = Adb.Run(AdbCommand.Shell(serial, FirmwareTools.ListPatchedCommand), Adb.ShortCommand);
                PatchedImage found = RootHelper.FindNewest(listing.Output, sentAt);
                if (found == null)
                {
                    problem = Properties.Resources.samsung_magisk_not_found;
                    return null;
                }
                local = Path.Combine(folder, found.Name);
                Adb.Result pulled = Adb.Run(AdbCommand.Pull(serial, found.Path, local), Fastboot.LongCommand);
                if (!pulled.Succeeded || !File.Exists(local))
                {
                    problem = pulled.Problem;
                    return null;
                }
                check = FirmwareTools.CheckPatched(local);
                return null;
            }, delegate
            {
                if (problem != null)
                {
                    log("no patched tar: " + problem);
                    ThemedDialog.Show(problem, Properties.Resources.samsung_magisk, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                showPatched(local, check);
            });
        }

        static void checkPatchedFile()
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "tar|*.tar;*.tar.md5|" + Properties.Resources.all_files + "|*.*",
            };
            if (dialog.ShowDialog() != true)
                return;
            string path = dialog.FileName;
            FirmwareTools.PatchedCheck check = null;
            run(Properties.Resources.samsung_magisk_checking, token =>
            {
                check = FirmwareTools.CheckPatched(path);
                return null;
            }, delegate { showPatched(path, check); });
        }

        static void showPatched(string path, FirmwareTools.PatchedCheck check)
        {
            log("checked " + path + ": " + (check == null ? "no boot image" : check.Image + ", root " + check.Root));
            if (check == null)
            {
                ThemedDialog.Show(Properties.Resources.samsung_patched_no_boot, Properties.Resources.samsung_magisk,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!check.Patched)
            {
                ThemedDialog.Show(string.Format(Properties.Resources.samsung_patched_not, Helper.ltr(check.Image)),
                    Properties.Resources.samsung_magisk, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ThemedDialog.Done(string.Format(Properties.Resources.samsung_patched_ok, Helper.ltr(check.Image), Helper.ltr(path)));
        }

        // ------------------------------------------------------------------ a tar for Odin

        static void buildTar()
        {
            Microsoft.Win32.OpenFileDialog pick = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = Properties.Resources.samsung_build_tar,
                Filter = Properties.Resources.samsung_tar_images + "|*.img;*.lz4;*.bin;*.mbn;*.elf|" + Properties.Resources.all_files + "|*.*",
            };
            if (pick.ShowDialog() != true)
                return;
            string[] files = pick.FileNames;
            string bad = files.Select(Path.GetFileName).FirstOrDefault(n => System.Text.Encoding.UTF8.GetByteCount(n) > 99);
            if (bad != null)
            {
                ThemedDialog.Show(string.Format(Properties.Resources.samsung_tar_long_name, bad), Properties.Resources.error,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Microsoft.Win32.SaveFileDialog save = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(files[0]) + ".tar.md5",
                InitialDirectory = Path.GetDirectoryName(files[0]),
                Filter = "Odin tar|*.tar.md5",
            };
            if (save.ShowDialog() != true)
                return;
            string output = save.FileName;
            run(Properties.Resources.samsung_tar_job, token =>
            {
                FirmwareTools.BuildTar(files, output, withMd5: true, token);
                return string.Format(Properties.Resources.samsung_tar_done, Helper.ltr(output),
                    string.Join(", ", files.Select(Path.GetFileName)));
            });
        }

        static void openInExplorer(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return;
            if (!Directory.Exists(folder))
                folder = Path.GetDirectoryName(folder);
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", AdbCommand.WindowsArgument(folder)) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
