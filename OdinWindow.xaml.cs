using FastbootEnhance.Core.Samsung.Odin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastbootEnhance
{
    /// <summary>
    /// A Samsung phone in Download mode: how to get it there, then its partition table (PIT),
    /// model, serial and region, and a restart. From that reading it flashes: the chosen files
    /// are read and checked first (MD5, LZ4, the PIT, the model), the person reads the plan and
    /// its warnings, and only then is anything written, in a new session that checks the PIT
    /// again before the first file.
    /// </summary>
    public partial class OdinWindow : Window
    {
        const string GlyphDone = "M5,12.5 L10,17.5 L19.5,7";
        const string GlyphError = "M12,2 A10,10 0 1 1 11.99,2 Z M9,9 L15,15 M15,9 L9,15";

        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        bool scanning;
        bool reading;
        bool? driverInstalled;
        OdinPortInfo found;
        OdinReading last;
        OdinReboot lastReboot;

        // Flashing: the files read, the plan made from them, and the reading of them under way.
        List<OdinFlashSource> sources;
        OdinFlashPlan plan;
        System.Threading.CancellationTokenSource checking;
        Action onPrimary;
        Action onSecondary;
        Action onExtra;

        static void log(string line) => LogStore.Append(line.StartsWith("odin:", StringComparison.Ordinal) ? line : "odin: " + line);

        OdinWindow()
        {
            InitializeComponent();
            FlowDirection = Languages.Flow;
            Title = Properties.Resources.odin_title;
            header.MouseLeftButtonDown += delegate { DragMove(); };
            SourceInitialized += delegate { DarkTitleBar.Apply(this); };
            primary.Click += delegate { onPrimary?.Invoke(); };
            secondary.Click += delegate { onSecondary?.Invoke(); };
            extra.Click += delegate { onExtra?.Invoke(); };
            driver_button.Click += delegate { DriverUI.InstallSamsung(checkDriver); };
            pit_save.Click += delegate { savePit(); };
            flash_choose.Click += delegate { chooseFiles(); };
            plan_ack.Checked += delegate { updateFlashButton(); };
            plan_ack.Unchecked += delegate { updateFlashButton(); };
            AutomationProperties.SetAutomationId(flash_choose, "odin_flash_choose");
            AutomationProperties.SetAutomationId(plan_ack, "odin_flash_ack");
            AutomationProperties.SetAutomationId(pit_save, "odin_pit_save");
            AutomationProperties.SetAutomationId(primary, "odin_primary");
            AutomationProperties.SetAutomationId(secondary, "odin_secondary");
            AutomationProperties.SetAutomationId(extra, "odin_extra");
            AutomationProperties.SetAutomationId(after_android, "odin_after_android");
            AutomationProperties.SetAutomationId(after_download, "odin_after_download");

            Closing += (sender, e) =>
            {
                // The phone is in the middle of a session: closing now would leave it waiting.
                if (reading)
                    e.Cancel = true;
            };
            Closed += delegate
            {
                poll.Stop();
                checking?.Cancel();
            };
            PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape && !reading)
                {
                    Close();
                    e.Handled = true;
                }
            };
            poll.Tick += delegate { scan(); };

            fillSteps();
            checkDriver();
            showPrepare();
        }

        public static void Show(Window owner)
        {
            OdinWindow dialog = new OdinWindow();
            if (owner != null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
            }
            log("window opened");
            dialog.ShowDialog();
        }

        // ------------------------------------------------------------------ getting ready

        void fillSteps()
        {
            string[] texts = { Properties.Resources.odin_step_off, Properties.Resources.odin_step_keys, Properties.Resources.odin_step_continue };
            for (int i = 0; i < texts.Length; i++)
            {
                Grid row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = (i + 1) + ".", FontWeight = FontWeights.Bold, Foreground = Palette.Get("Accent"), LineHeight = 19 });
                TextBlock text = new TextBlock { Text = texts[i], TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 19 };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                steps.Items.Add(row);
            }
        }

        void showPrepare()
        {
            heading.Text = Properties.Resources.odin_title;
            subheading.Text = Properties.Resources.odin_subtitle;
            setLook("Accent", "AccentSoft", "AccentLine", null);
            prepare_view.Visibility = Visibility.Visible;
            work_view.Visibility = Visibility.Collapsed;
            result_view.Visibility = Visibility.Collapsed;

            primary.Visibility = Visibility.Visible;
            primary.Content = Properties.Resources.odin_read;
            onPrimary = read;
            secondary.Content = Properties.Resources.driver_close;
            onSecondary = Close;
            extra.Visibility = Visibility.Collapsed;

            found = null;
            showStatus("…", "Dim", Properties.Resources.odin_looking, false);
            primary.IsEnabled = false;
            scan();
            poll.Start();
        }

        void checkDriver()
        {
            bool installed = false;
            Helper.offloadAndRun(delegate { installed = DriverUI.SamsungInstalled(); }, delegate
            {
                driverInstalled = installed;
                if (prepare_view.Visibility == Visibility.Visible && found == null)
                    scan();
            });
        }

        /// <summary>The phone is looked for while getting ready to read, and while getting ready to flash.</summary>
        bool watching => prepare_view.Visibility == Visibility.Visible || plan_view.Visibility == Visibility.Visible;

        /// <summary>Looks for the phone off the UI thread; one look at a time.</summary>
        void scan()
        {
            if (scanning || reading || !watching)
                return;
            scanning = true;
            Task.Run(() => OdinUI.Find()).ContinueWith(task =>
            {
                scanning = false;
                if (reading || !watching)
                    return;
                if (task.IsFaulted)
                {
                    log("looking for the phone failed: " + task.Exception.GetBaseException().Message);
                    return;
                }
                showScan(task.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void showScan(OdinUI.Scan scan)
        {
            OdinPortInfo before = found;
            found = scan.Ports.Count == 1 ? scan.Ports[0] : null;
            if (plan_view.Visibility == Visibility.Visible)
            {
                showFlashStatus(scan);
                updateFlashButton();
                return;
            }
            primary.IsEnabled = found != null;

            if (scan.Ports.Count > 1)
                showStatus("!", "Warn", string.Format(Properties.Resources.odin_found_many, scan.Ports.Count), false);
            else if (found != null)
                showStatus("✓", "Ok", string.Format(Properties.Resources.odin_found, Helper.ltr(found.Port)), false);
            else if (scan.SeenWithoutPort || driverInstalled == false)
                showStatus("!", "Warn", scan.SeenWithoutPort ? Properties.Resources.odin_seen_no_driver : Properties.Resources.odin_no_driver, true);
            else
                showStatus("…", "Dim", Properties.Resources.odin_not_found, false);

            if (found != null && (before == null || before.Port != found.Port))
                log("phone in Download mode on " + found.Port + " (" + found.Name + ")");
        }

        void showStatus(string mark, string color, string text, bool offerDriver)
        {
            status_mark.Text = mark;
            status_mark.Foreground = Palette.Get(color);
            status_text.Text = text;
            status_text.Foreground = Palette.Get(color == "Warn" ? "Warn" : "Text");
            driver_button.Visibility = offerDriver ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ reading

        void read()
        {
            if (found == null || reading)
                return;
            OdinPortInfo port = found;
            OdinReboot reboot = after_download.IsChecked == true ? OdinReboot.Download : OdinReboot.Normal;
            poll.Stop();
            reading = true;

            heading.Text = Properties.Resources.odin_reading_title;
            subheading.Text = Helper.ltr(port.Port);
            prepare_view.Visibility = Visibility.Collapsed;
            result_view.Visibility = Visibility.Collapsed;
            work_view.Visibility = Visibility.Visible;
            progress.Visibility = Visibility.Visible;
            message.Text = Properties.Resources.odin_reading;
            primary.Visibility = Visibility.Collapsed;
            secondary.IsEnabled = false;
            extra.Visibility = Visibility.Collapsed;
            log("reading " + port.Port + ", then " + reboot);

            Task.Run(() =>
            {
                using (IOdinTransport link = OdinUI.Open(port, log))
                    return OdinReader.Read(link, reboot, log);
            }).ContinueWith(task =>
            {
                reading = false;
                secondary.IsEnabled = true;
                if (task.IsFaulted)
                {
                    Exception e = task.Exception.GetBaseException();
                    log("reading failed: " + e.Message);
                    showFailure(e, port);
                    return;
                }
                last = task.Result;
                lastReboot = reboot;
                showResult(task.Result, reboot);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void showFailure(Exception e, OdinPortInfo port)
        {
            heading.Text = Properties.Resources.odin_failed;
            subheading.Text = Helper.ltr(port.Port);
            setLook("Danger", "DangerSoft", "DangerLine", GlyphError);
            progress.Visibility = Visibility.Collapsed;

            string why;
            bool sessionStarted = true;
            if (e is OdinPortException open)
            {
                sessionStarted = false;
                why = open.Code == OdinPortException.InUse ? string.Format(Properties.Resources.odin_err_busy, Helper.ltr(port.Port))
                    : open.Code == OdinPortException.NotFound ? Properties.Resources.odin_err_gone
                    : string.Format(Properties.Resources.odin_err_open, open.Message);
            }
            else if (e is OdinException && e.Message.StartsWith("the handshake", StringComparison.Ordinal))
            {
                why = Properties.Resources.odin_err_handshake;
            }
            else if (e is IOException)
            {
                why = Properties.Resources.odin_err_gone + "\n\n" + e.Message;
            }
            else
            {
                why = string.Format(Properties.Resources.odin_err_other, e.Message);
            }
            message.Text = why + "\n\n" + (sessionStarted ? Properties.Resources.odin_err_restart + "\n\n" : "") + Properties.Resources.odin_err_cable;

            primary.Visibility = Visibility.Visible;
            primary.Content = Properties.Resources.odin_try_again;
            onPrimary = showPrepare;
            secondary.Content = Properties.Resources.driver_close;
            onSecondary = Close;
        }

        void showResult(OdinReading r, OdinReboot reboot)
        {
            string model = r.Info?.Model;
            heading.Text = model != null ? string.Format(Properties.Resources.odin_done_model, Helper.ltr(model)) : Properties.Resources.odin_done;
            subheading.Text = Properties.Resources.odin_done_note;
            setLook("Ok", "AccentSoft", "AccentLine", GlyphDone);
            work_view.Visibility = Visibility.Collapsed;
            plan_view.Visibility = Visibility.Collapsed;
            result_view.Visibility = Visibility.Visible;

            facts.Children.Clear();
            facts.RowDefinitions.Clear();
            // The phone's own words read left to right; the lines this app writes follow the language.
            HashSet<string> written = new HashSet<string> { Properties.Resources.odin_protocol, Properties.Resources.odin_pit };
            foreach (KeyValuePair<string, string> fact in factsOf(r))
                addFact(fact.Key, written.Contains(fact.Key) ? fact.Value : Helper.ltr(fact.Value));

            info_note.Visibility = r.Info == null ? Visibility.Visible : Visibility.Collapsed;
            info_note.Text = r.InfoProblem != null ? Properties.Resources.odin_info_failed : Properties.Resources.odin_info_missing;

            fillPit(r.Pit);

            end_note.Text = !r.Ended ? Properties.Resources.odin_not_ended
                : r.Restart == OdinReboot.Download ? Properties.Resources.odin_ended_download
                : reboot == OdinReboot.Download ? Properties.Resources.odin_ended_android_instead : Properties.Resources.odin_ended_android;
            end_note.Foreground = Palette.Get(r.Ended ? "Dim" : "Warn");

            primary.Visibility = Visibility.Visible;
            primary.IsEnabled = true;
            primary.Content = Properties.Resources.odin_flash;
            onPrimary = startFlash;
            secondary.Content = Properties.Resources.driver_close;
            onSecondary = Close;
            extra.Visibility = Visibility.Visible;
            extra.Content = Properties.Resources.odin_copy;
            onExtra = copy;
        }

        static List<KeyValuePair<string, string>> factsOf(OdinReading r)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            void add(string label, string value)
            {
                if (!string.IsNullOrEmpty(value))
                    list.Add(new KeyValuePair<string, string>(label, value));
            }
            add(Properties.Resources.odin_model, r.Info?.Model);
            add(Properties.Resources.odin_serial, r.Info?.Serial);
            add(Properties.Resources.odin_region, r.Info?.SalesCode);
            add(Properties.Resources.odin_carrier, r.Info?.CarrierId);
            add(Properties.Resources.odin_protocol, string.Format(r.TakesLz4 ? Properties.Resources.odin_protocol_lz4 : Properties.Resources.odin_protocol_value, r.ProtocolVersion));
            string project = string.IsNullOrEmpty(r.Pit.Project) ? "" : " · " + r.Pit.Project;
            add(Properties.Resources.odin_pit, string.Format(Properties.Resources.odin_pit_value, r.Pit.Entries.Count) + project
                + " · " + Helper.byte2AUnit(r.PitData.Length));
            return list;
        }

        void addFact(string label, string value)
        {
            int row = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock l = new TextBlock { Text = label, Foreground = Palette.Get("Dim"), FontSize = 12.5, Margin = new Thickness(0, 0, 14, 5) };
            TextBlock v = new TextBlock { Text = value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            facts.Children.Add(l);
            facts.Children.Add(v);
        }

        static readonly GridLength[] PitColumns = { new GridLength(48), new GridLength(170), new GridLength(1, GridUnitType.Star), new GridLength(96) };

        Grid pitRow(string id, string partition, string file, string blocks, string color, bool bold, bool mono)
        {
            Grid row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            foreach (GridLength width in PitColumns)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            string[] cells = { id, partition, file, blocks };
            for (int i = 0; i < cells.Length; i++)
            {
                TextBlock cell = new TextBlock
                {
                    Text = cells[i],
                    FontSize = mono ? 11.5 : 12,
                    Foreground = Palette.Get(i == 1 && !bold ? "Text" : color),
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 8, 0),
                    TextAlignment = i == 3 ? TextAlignment.Right : TextAlignment.Left,
                };
                if (mono)
                {
                    cell.FontFamily = (FontFamily)FindResource("Mono");
                    cell.FlowDirection = FlowDirection.LeftToRight;
                    if (i == 3 && Languages.RightToLeft)
                        cell.TextAlignment = TextAlignment.Left;
                }
                Grid.SetColumn(cell, i);
                row.Children.Add(cell);
            }
            return row;
        }

        void fillPit(SamsungPit pit)
        {
            pit_head.Children.Clear();
            pit_head.Children.Add(pitRow(Properties.Resources.odin_col_id, Properties.Resources.odin_col_partition,
                Properties.Resources.odin_col_file, Properties.Resources.odin_col_blocks, "Dim", true, false));
            pit_rows.Children.Clear();
            foreach (PitEntry e in pit.Entries)
            {
                string file = string.IsNullOrEmpty(e.FileName) ? "—" : e.FileName;
                if (e.IsModem)
                    file += "  (CP)";
                pit_rows.Children.Add(pitRow(e.PartitionId.ToString(), e.Partition, file, e.BlockCount.ToString("N0"), "Dim", false, true));
            }
        }

        // ------------------------------------------------------------------ flashing

        /// <summary>The files, the plan and the phone's port, before anything is written.</summary>
        void startFlash()
        {
            if (last == null)
                return;
            heading.Text = Properties.Resources.odin_flash_title;
            subheading.Text = last.Info?.Model != null ? Helper.ltr(last.Info.Model) : Properties.Resources.odin_subtitle;
            setLook("Accent", "AccentSoft", "AccentLine", null);
            result_view.Visibility = Visibility.Collapsed;
            work_view.Visibility = Visibility.Collapsed;
            plan_view.Visibility = Visibility.Visible;
            extra.Visibility = Visibility.Collapsed;
            primary.Visibility = Visibility.Visible;
            primary.Content = Properties.Resources.odin_flash;
            onPrimary = confirmFlash;
            secondary.Content = Properties.Resources.odin_flash_back;
            onSecondary = () => showResult(last, lastReboot);
            log("flash: choosing files");

            // The phone may have been read again since: the plan is checked against this reading.
            if (sources != null)
                buildPlan();
            else
                renderPlan();
            found = null;
            flash_status_mark.Text = "…";
            flash_status_mark.Foreground = Palette.Get("Dim");
            flash_status_text.Text = Properties.Resources.odin_flash_waiting;
            updateFlashButton();
            scan();
            poll.Start();

            // Tests choose files without the dialog: FASTBOOT_STUDIO_FAKE_ODIN_FILES, paths split by |.
            string standIn = Environment.GetEnvironmentVariable("FASTBOOT_STUDIO_FAKE_ODIN_FILES");
            if (!string.IsNullOrEmpty(standIn) && sources == null)
                readFiles(standIn.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
        }

        void showFlashStatus(OdinUI.Scan scan)
        {
            if (scan.Ports.Count > 1)
                setFlashStatus("!", "Warn", string.Format(Properties.Resources.odin_found_many, scan.Ports.Count));
            else if (found != null)
                setFlashStatus("✓", "Ok", string.Format(Properties.Resources.odin_found, Helper.ltr(found.Port)));
            else
                setFlashStatus("…", "Dim", Properties.Resources.odin_flash_waiting);
        }

        void setFlashStatus(string mark, string color, string text)
        {
            flash_status_mark.Text = mark;
            flash_status_mark.Foreground = Palette.Get(color);
            flash_status_text.Text = text;
            flash_status_text.Foreground = Palette.Get(color == "Warn" ? "Warn" : "Text");
        }

        void chooseFiles()
        {
            Microsoft.Win32.OpenFileDialog open = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = Properties.Resources.odin_flash_filter + "|*.tar.md5;*.tar;*.img;*.bin;*.lz4;*.mbn;*.elf|*.*|*.*",
            };
            if (open.ShowDialog(this) == true)
                readFiles(open.FileNames);
        }

        /// <summary>Reads and checks the files off the UI thread: MD5 lines, tar lists, LZ4 decoded once.</summary>
        void readFiles(string[] paths)
        {
            checking?.Cancel();
            System.Threading.CancellationTokenSource cancel = new System.Threading.CancellationTokenSource();
            checking = cancel;
            sources = null;
            plan = null;
            renderPlan();
            flash_choose.IsEnabled = false;
            plan_reading.Visibility = Visibility.Visible;
            plan_progress.Value = 0;
            log("flash: reading " + string.Join(", ", paths.Select(Path.GetFileName)));

            DateTime shown = DateTime.MinValue;
            Action<string, long, long> progress = (what, done, total) =>
            {
                if (DateTime.UtcNow - shown < TimeSpan.FromMilliseconds(120) && done < total)
                    return;
                shown = DateTime.UtcNow;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    plan_progress.Value = total > 0 ? done * 100.0 / total : 0;
                    plan_reading_text.Text = string.Format(Properties.Resources.odin_flash_reading, Helper.ltr(what));
                }));
            };
            Task.Run(() => OdinSources.Read(paths, progress, cancel.Token), cancel.Token).ContinueWith(task =>
            {
                if (cancel.IsCancellationRequested)
                    return;
                checking = null;
                flash_choose.IsEnabled = true;
                plan_reading.Visibility = Visibility.Collapsed;
                if (task.IsFaulted)
                {
                    Exception e = task.Exception.GetBaseException();
                    log("flash: " + e.Message);
                    ThemedDialog.Show(e is InvalidDataException ? string.Format(Properties.Resources.odin_flash_damaged, e.Message) : e.Message,
                        Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                sources = task.Result;
                buildPlan();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void buildPlan()
        {
            plan = sources == null ? null : OdinFlashPlan.Build(sources, last.PitData, last.Info?.Model);
            if (plan != null)
            {
                foreach (OdinCheck c in plan.Checks)
                    log("flash: " + c);
                log("flash: " + plan.Items.Count + " files, " + plan.TotalBytes + " bytes, " + (plan.Blocked ? "blocked" : "allowed"));
            }
            plan_ack.IsChecked = false;
            renderPlan();
            updateFlashButton();
        }

        void renderPlan()
        {
            plan_rows.Children.Clear();
            plan_targets.Children.Clear();
            plan_checks.Children.Clear();
            plan_box.Visibility = plan != null && plan.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            plan_total.Text = plan == null ? "" : string.Format(Properties.Resources.odin_flash_total, plan.Items.Count, Helper.byte2AUnit(plan.TotalBytes));
            plan_ack.Visibility = plan != null && !plan.Blocked && plan.Checks.Any(c => c.Level == OdinCheckLevel.Warn)
                ? Visibility.Visible : Visibility.Collapsed;
            if (plan == null)
                return;

            foreach (OdinFlashItem item in plan.Items)
                plan_rows.Children.Add(pitRow(item.Entry.PartitionId.ToString(), item.Entry.Partition,
                    "← " + item.Source.Name, Helper.byte2AUnit(item.Source.Size), "Dim", false, true));

            // An image whose name the PIT does not know: the person picks its partition.
            List<string> names = last.Pit.Entries.Where(e => !e.IsModem).Select(e => e.Partition).OrderBy(n => n, StringComparer.Ordinal).ToList();
            foreach (OdinFlashSource source in sources.Where(x => x.Part == null && !x.FileName.EndsWith(".pit", StringComparison.OrdinalIgnoreCase)
                && last.Pit.ForFile(x.FileName) == null))
            {
                DockPanel row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                ComboBox box = new ComboBox { MinWidth = 170, ItemsSource = names, SelectedItem = source.Target?.ToUpperInvariant() };
                DockPanel.SetDock(box, Dock.Right);
                OdinFlashSource target = source;
                box.SelectionChanged += delegate
                {
                    target.Target = box.SelectedItem as string;
                    buildPlan();
                };
                row.Children.Add(box);
                row.Children.Add(new TextBlock
                {
                    Text = string.Format(Properties.Resources.odin_flash_target, Helper.ltr(source.Name)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 10, 0),
                });
                plan_targets.Children.Add(row);
            }

            foreach (OdinCheck check in plan.Checks)
            {
                string color = check.Level == OdinCheckLevel.Block ? "Danger" : check.Level == OdinCheckLevel.Warn ? "Warn" : "Dim";
                Border box = new Border
                {
                    Margin = new Thickness(0, 4, 0, 0),
                    Padding = new Thickness(12, 7, 12, 7),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Background = Palette.Get(check.Level == OdinCheckLevel.Info ? "Well" : color + "Soft"),
                    BorderBrush = Palette.Get(check.Level == OdinCheckLevel.Info ? "Line" : color + "Line"),
                    Child = new TextBlock { Text = checkText(check), TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 18 },
                };
                plan_checks.Children.Add(box);
            }
        }

        static string checkText(OdinCheck check)
        {
            string format = Properties.Resources.ResourceManager.GetString("odin_check_" + check.Code, Properties.Resources.Culture) ?? check.ToString();
            string[] args = check.Args.ToArray();
            if (check.Code == OdinCheckCode.TooBig)
                args = new[] { Helper.ltr(args[0]), args[1], Helper.byte2AUnit(long.Parse(args[2])), Helper.byte2AUnit(long.Parse(args[3])) };
            else if (check.Code == OdinCheckCode.Mixed)
                args = new[] { string.Join(", ", args.Select(Helper.ltr)) };
            else
                args = args.Select(Helper.ltr).ToArray();
            return string.Format(format, args);
        }

        void updateFlashButton()
        {
            if (plan_view.Visibility != Visibility.Visible)
                return;
            bool warned = plan != null && plan.Checks.Any(c => c.Level == OdinCheckLevel.Warn);
            primary.IsEnabled = plan != null && !plan.Blocked && found != null && checking == null && (!warned || plan_ack.IsChecked == true);
        }

        void confirmFlash()
        {
            if (plan == null || plan.Blocked || found == null)
                return;
            if (!Helper.confirm(string.Format(Properties.Resources.odin_flash_confirm, plan.Items.Count, Helper.byte2AUnit(plan.TotalBytes)),
                    Properties.Resources.odin_flash_title))
                return;
            flash();
        }

        /// <summary>
        /// The flash itself, off the UI thread. The window cannot be closed meanwhile: a phone
        /// left in the middle of a file is worse off than one never started.
        /// </summary>
        void flash()
        {
            OdinPortInfo port = found;
            OdinFlashPlan flashing = plan;
            OdinReboot reboot = plan_restart.IsChecked == true ? OdinReboot.Normal : OdinReboot.None;
            poll.Stop();
            reading = true;

            heading.Text = Properties.Resources.odin_flash_title;
            subheading.Text = Helper.ltr(port.Port);
            plan_view.Visibility = Visibility.Collapsed;
            work_view.Visibility = Visibility.Visible;
            progress.Visibility = Visibility.Visible;
            progress.IsIndeterminate = true;
            progress.Value = 0;
            progress_text.Visibility = Visibility.Visible;
            progress_text.Text = "";
            message.Text = Properties.Resources.odin_flash_keep;
            message.Foreground = Palette.Get("Warn");
            primary.Visibility = Visibility.Collapsed;
            secondary.IsEnabled = false;
            extra.Visibility = Visibility.Collapsed;
            log("flash: " + flashing.Items.Count + " files to " + port.Port + ", then " + reboot);

            DateTime shown = DateTime.MinValue;
            Action<string, long, long> step = (partition, done, total) =>
            {
                if (DateTime.UtcNow - shown < TimeSpan.FromMilliseconds(150) && done < total)
                    return;
                shown = DateTime.UtcNow;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    progress.IsIndeterminate = false;
                    progress.Value = total > 0 ? done * 100.0 / total : 0;
                    heading.Text = string.Format(Properties.Resources.odin_flash_writing, Helper.ltr(partition));
                    progress_text.Text = Math.Floor(progress.Value) + "%  ·  "
                        + string.Format(Properties.Resources.odin_flash_progress, Helper.byte2AUnit(done), Helper.byte2AUnit(total));
                }));
            };

            Task.Run(() =>
            {
                using (IOdinTransport link = OdinUI.Open(port, log))
                    return OdinFlasher.Flash(link, flashing, reboot, step, log);
            }).ContinueWith(task =>
            {
                reading = false;
                secondary.IsEnabled = true;
                message.Foreground = Palette.Get("Text");
                progress_text.Visibility = Visibility.Collapsed;
                if (task.IsFaulted)
                {
                    // It failed before the first file: the handshake, the port, the session.
                    Exception e = task.Exception.GetBaseException();
                    log("flash failed: " + e.Message);
                    showFlashResult(new OdinFlashResultView { Error = e }, reboot);
                    return;
                }
                OdinFlashResult r = task.Result;
                showFlashResult(new OdinFlashResultView
                {
                    Error = r.Error,
                    Written = r.Written,
                    FailedAt = r.FailedAt,
                    PitChanged = r.PitChanged,
                    Ended = r.Ended,
                    Restart = r.Restart,
                }, reboot);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        sealed class OdinFlashResultView
        {
            public Exception Error;
            public List<string> Written = new List<string>();
            public string FailedAt;
            public bool PitChanged;
            public bool Ended;
            public OdinReboot Restart;
        }

        void showFlashResult(OdinFlashResultView r, OdinReboot reboot)
        {
            progress.Visibility = Visibility.Collapsed;
            found = null;
            string written = r.Written.Count > 0 ? string.Format(Properties.Resources.odin_flash_written, string.Join(", ", r.Written)) : "";
            if (r.Error == null)
            {
                heading.Text = Properties.Resources.odin_flash_done;
                setLook("Ok", "AccentSoft", "AccentLine", GlyphDone);
                message.Text = written + "\n\n" + (r.Restart == OdinReboot.Normal ? Properties.Resources.odin_flash_done_restart
                    : !r.Ended ? Properties.Resources.odin_not_ended : Properties.Resources.odin_flash_done_stay);
                primary.Visibility = Visibility.Visible;
                primary.Content = Properties.Resources.driver_close;
                onPrimary = Close;
                secondary.Visibility = Visibility.Collapsed;
                log("flash: done, " + string.Join(", ", r.Written));
                return;
            }

            heading.Text = Properties.Resources.odin_flash_failed;
            setLook("Danger", "DangerSoft", "DangerLine", GlyphError);
            string why = r.PitChanged ? Properties.Resources.odin_flash_pit_changed
                : r.FailedAt != null ? string.Format(Properties.Resources.odin_flash_failed_at, r.FailedAt, r.Error.Message)
                : string.Format(Properties.Resources.odin_flash_failed_before, r.Error.Message);
            message.Text = why + (written.Length > 0 ? "\n\n" + written : "") + "\n\n" + Properties.Resources.odin_flash_failed_note;
            primary.Visibility = Visibility.Visible;
            primary.IsEnabled = true;
            primary.Content = r.PitChanged ? Properties.Resources.odin_try_again : Properties.Resources.odin_flash_again;
            onPrimary = r.PitChanged ? (Action)showPrepare : startFlash;
            secondary.Visibility = Visibility.Visible;
            secondary.Content = Properties.Resources.driver_close;
            onSecondary = Close;
        }

        // ------------------------------------------------------------------ keeping it

        void savePit()
        {
            if (last == null)
                return;
            string name = safeName(last.Info?.Model) ?? safeName(last.Pit.Project) ?? "samsung";
            Microsoft.Win32.SaveFileDialog save = new Microsoft.Win32.SaveFileDialog
            {
                FileName = name + ".pit",
                Filter = "PIT|*.pit",
            };
            if (save.ShowDialog(this) != true)
                return;
            try
            {
                File.WriteAllBytes(save.FileName, last.PitData);
                log("PIT saved to " + save.FileName);
                end_note.Text = string.Format(Properties.Resources.odin_saved, Helper.ltr(save.FileName));
                end_note.Foreground = Palette.Get("Dim");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        static string safeName(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            char[] bad = Path.GetInvalidFileNameChars();
            string clean = new string(text.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
            return clean.Length == 0 ? null : clean;
        }

        void copy()
        {
            if (last == null)
                return;
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, string> fact in factsOf(last))
                text.Append(fact.Key).Append(": ").Append(fact.Value).Append('\n');
            text.Append('\n');
            foreach (PitEntry e in last.Pit.Entries)
                text.Append(e.PartitionId.ToString().PadLeft(3)).Append("  ").Append(e.Partition.PadRight(20)).Append(' ')
                    .Append((e.FileName ?? "").PadRight(24)).Append(' ').Append(e.BlockCount).Append(e.IsModem ? "  CP" : "").Append('\n');
            try
            {
                Clipboard.SetText(text.ToString());
                extra.Content = Properties.Resources.odin_copied;
            }
            catch (System.Runtime.InteropServices.ExternalException e)
            {
                log("copy failed: " + e.Message);
            }
        }

        void setLook(string color, string soft, string line, string path)
        {
            strip.Background = Palette.Get(color);
            badge.Background = Palette.Get(soft);
            badge.BorderBrush = Palette.Get(line);
            glyph.Stroke = Palette.Get(color);
            glyph.Data = path == null ? (Geometry)FindResource("IconSamsung") : Geometry.Parse(path);
        }
    }
}
