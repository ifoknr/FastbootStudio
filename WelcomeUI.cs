using FastbootEnhance.Core.Update;
using FastbootEnhance.Core.Usb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FastbootEnhance
{
    /// <summary>
    /// The first start: a short tour in four steps (language, what each page does, a quick
    /// check of this PC, and the rules that keep a phone safe). The first start of a newer
    /// version instead shows what changed, from the CHANGELOG.md section built into the exe.
    /// Both can be opened again from About. FASTBOOT_STUDIO_WELCOME, for tests: "off" never
    /// shows either, "on" always starts the tour, "whatsnew" always shows what changed.
    /// </summary>
    static class WelcomeUI
    {
        /// <summary>null: never shown; "started": the tour is under way (a language change restarts the app); "done".</summary>
        const string TourKey = "welcome";

        /// <summary>The version whose start was last greeted, by the tour or by What's new.</summary>
        const string SeenKey = "seen_version";

        const int Steps = 4;

        static readonly string Override = Environment.GetEnvironmentVariable("FASTBOOT_STUDIO_WELCOME");

        static MainWindow W => MainWindow.THIS;

        static int step;
        static bool whatsNew;

        /// <summary>The page picked on the map: shown when the tour ends.</summary>
        static TabItem goTo;

        // The quick check, filled in once read.
        static StackPanel checkRows;

        public static bool IsOpen => W.welcome_layer.Visibility == Visibility.Visible;

        public static void init()
        {
            W.welcome_back.Click += delegate { back(); };
            W.welcome_next.Click += delegate { next(); };
            W.about_welcome.Click += delegate { showTour(); };
            W.about_whats_new.Content = string.Format(Properties.Resources.whats_new_button, "v" + MainWindow.version);
            W.about_whats_new.Click += delegate
            {
                if (notes() == null)
                    ThemedDialog.Show(Properties.Resources.whats_new_none);
                else
                    showWhatsNew();
            };
            W.Loaded += delegate { atStart(); };
        }

        static void atStart()
        {
            string mode = (Override ?? "").Trim().ToLowerInvariant();
            if (mode == "off")
                return;
            if (mode == "on")
            {
                showTour();
                return;
            }
            if (mode == "whatsnew")
            {
                showWhatsNew();
                return;
            }

            string tour = Settings.Get(TourKey);
            string seen = Settings.Get(SeenKey);
            // Someone who used an older version has settings already: they get What's new, not the tour.
            bool firstStart = tour == null && !Settings.Any();
            if (firstStart || tour == "started")
            {
                Settings.Set(TourKey, "started");
                showTour();
                return;
            }
            bool newer = seen == null || ReleaseFeed.IsNewer(MainWindow.version, seen);
            if (newer && notes() != null)
                showWhatsNew();
            else
                remember();
        }

        /// <summary>Done with this version: neither the tour nor What's new comes back by itself.</summary>
        static void remember()
        {
            Settings.Set(TourKey, "done");
            Settings.Set(SeenKey, MainWindow.version);
        }

        /// <summary>Esc ends the tour, or closes What's new; true when one was open.</summary>
        public static bool HandleKey(KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !IsOpen)
                return false;
            finish();
            e.Handled = true;
            return true;
        }

        static void showTour()
        {
            whatsNew = false;
            goTo = null;
            step = 0;
            W.welcome_layer.Visibility = Visibility.Visible;
            showStep();
        }

        static void back()
        {
            if (whatsNew)
            {
                // "Take the tour" from What's new.
                showTour();
                return;
            }
            if (step == 0)
            {
                LogStore.Append("welcome: skipped");
                finish();
                return;
            }
            step--;
            showStep();
        }

        static void next()
        {
            if (whatsNew || step == Steps - 1)
            {
                finish();
                return;
            }
            step++;
            showStep();
        }

        static void finish()
        {
            W.welcome_layer.Visibility = Visibility.Collapsed;
            remember();
            if (!whatsNew && goTo != null)
                W.main_tabs.SelectedItem = goTo;
            goTo = null;
            checkRows = null;
        }

        static void showStep()
        {
            W.welcome_dots.Visibility = Visibility.Visible;
            W.welcome_dots.Children.Clear();
            for (int i = 0; i < Steps; i++)
            {
                W.welcome_dots.Children.Add(new Border
                {
                    Width = i == step ? 26 : 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(4, 0, 4, 0),
                    Background = Palette.Get(i == step ? "Accent" : "Line"),
                });
            }
            W.welcome_back.Content = step == 0 ? Properties.Resources.welcome_skip : Properties.Resources.welcome_back;
            W.welcome_next.Content = step == Steps - 1 ? Properties.Resources.welcome_start : Properties.Resources.welcome_next;
            AutomationProperties.SetAutomationId(W.welcome_body, "welcome_step_" + (step + 1));

            switch (step)
            {
                case 0: languageStep(); break;
                case 1: mapStep(); break;
                case 2: checkStep(); break;
                default: safetyStep(); break;
            }
            W.welcome_scroll.ScrollToTop();
            W.welcome_next.Focus();
        }

        // ------------------------------------------------------------------ 1. language

        static void languageStep()
        {
            W.welcome_title.Text = Properties.Resources.welcome_title;
            W.welcome_subtitle.Text = Properties.Resources.welcome_intro;

            StackPanel body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            body.Children.Add(new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/big_icon.png")),
                Width = 84,
                Height = 84,
                Margin = new Thickness(0, 0, 0, 22),
                FlowDirection = FlowDirection.LeftToRight,
            });
            body.Children.Add(new TextBlock
            {
                // In every language, so whoever is reading finds it.
                Text = "Choose your language · اختر لغتك",
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
            });

            WrapPanel choices = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            Languages.Option current = Languages.CurrentOption;
            foreach (Languages.Option option in Languages.All)
            {
                Languages.Option choice = option;
                bool on = choice == current;
                Button card = new Button
                {
                    Width = 170,
                    Height = 84,
                    Margin = new Thickness(6),
                    Background = Palette.Get(on ? "AccentSoft" : "Surface"),
                    BorderBrush = Palette.Get(on ? "Accent" : "Line"),
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = choice.NativeName, FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                            new TextBlock
                            {
                                Text = on ? Properties.Resources.welcome_language_current : " ",
                                FontSize = 11.5,
                                Foreground = Palette.Get("Accent"),
                                HorizontalAlignment = HorizontalAlignment.Center,
                                Margin = new Thickness(0, 4, 0, 0),
                            },
                        },
                    },
                };
                AutomationProperties.SetAutomationId(card, "welcome_language_" + choice.Code);
                card.Click += delegate
                {
                    if (choice == Languages.CurrentOption)
                        return;
                    // The app shows a language from its start; the tour picks up again after it.
                    Languages.Save(choice.Code);
                    Settings.Set(TourKey, "started");
                    LogStore.Append("welcome: language " + choice.Code);
                    App.Restart();
                };
                choices.Children.Add(card);
            }
            body.Children.Add(choices);
            W.welcome_body.Content = body;
        }

        // ------------------------------------------------------------------ 2. the pages

        static void mapStep()
        {
            W.welcome_title.Text = Properties.Resources.welcome_map_title;
            W.welcome_subtitle.Text = Properties.Resources.welcome_map_intro;

            UniformGrid grid = new UniformGrid { Columns = 4 };
            foreach (TabItem tab in W.main_tabs.Items.OfType<TabItem>())
            {
                string page = tab.Name.EndsWith("_tab", StringComparison.Ordinal) ? tab.Name.Substring(0, tab.Name.Length - 4) : tab.Name;
                string what = Properties.Resources.ResourceManager.GetString("welcome_page_" + page, Properties.Resources.Culture);
                TabItem target = tab;
                bool picked = goTo == tab;

                StackPanel inside = new StackPanel();
                inside.Children.Add(new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(9),
                    Background = Palette.Get("AccentSoft"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = tab.Tag is Geometry icon ? new System.Windows.Shapes.Path
                    {
                        Data = icon,
                        Stroke = Palette.Get("Accent"),
                        StrokeThickness = 1.8,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(8),
                        FlowDirection = FlowDirection.LeftToRight,
                    } : null,
                });
                inside.Children.Add(new TextBlock
                {
                    Text = tab.Header as string,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });
                inside.Children.Add(new TextBlock
                {
                    Text = what ?? "",
                    Style = (Style)W.FindResource("Note"),
                    FontSize = 11.5,
                    LineHeight = 17,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0),
                });

                Button tile = new Button
                {
                    Margin = new Thickness(5),
                    Padding = new Thickness(12, 12, 12, 10),
                    MinHeight = 128,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    Background = Palette.Get(picked ? "AccentSoft" : "Surface"),
                    BorderBrush = Palette.Get(picked ? "Accent" : "Line"),
                    Content = inside,
                    ToolTip = Properties.Resources.welcome_map_pick,
                };
                AutomationProperties.SetAutomationId(tile, "welcome_page_" + page);
                tile.Click += delegate
                {
                    goTo = goTo == target ? null : target;
                    mapStep();
                };
                grid.Children.Add(tile);
            }
            W.welcome_body.Content = grid;
        }

        // ------------------------------------------------------------------ 3. this PC

        static void checkStep()
        {
            W.welcome_title.Text = Properties.Resources.welcome_check_title;
            W.welcome_subtitle.Text = Properties.Resources.welcome_check_intro;

            StackPanel rows = new StackPanel { MaxWidth = 640 };
            checkRows = rows;
            rows.Children.Add(checkRow("…", "Raised", "Dim", Properties.Resources.welcome_checking, null, null));
            W.welcome_body.Content = rows;

            bool tools = false, samsung = false;
            IReadOnlyList<UsbFinding> usb = null;
            Helper.offloadAndRun(delegate
            {
                string dir = AppContext.BaseDirectory;
                tools = File.Exists(System.IO.Path.Combine(dir, "adb.exe")) && File.Exists(System.IO.Path.Combine(dir, "fastboot.exe"));
                samsung = DriverUI.SamsungInstalled();
                try
                {
                    usb = UsbDiagnosis.Diagnose(UsbScan.Read());
                }
                catch (Exception e)
                {
                    LogStore.Append("welcome: USB list not read: " + e.Message);
                }
            }, delegate
            {
                // The person may have moved on while the check ran.
                if (checkRows != rows || !IsOpen || whatsNew || step != 2)
                    return;
                rows.Children.Clear();

                rows.Children.Add(tools
                    ? checkRow("✓", "Ok", "Ok", Properties.Resources.welcome_check_tools, Properties.Resources.welcome_check_tools_detail, null)
                    : checkRow("✕", "Danger", "Danger", Properties.Resources.welcome_check_tools_missing, Properties.Resources.welcome_check_tools_missing_detail, null));

                if (samsung)
                {
                    rows.Children.Add(checkRow("✓", "Ok", "Ok", Properties.Resources.welcome_check_samsung, Properties.Resources.welcome_check_samsung_detail, null));
                }
                else
                {
                    Button install = new Button { Content = Properties.Resources.driver_install, Margin = new Thickness(12, 0, 0, 0) };
                    AutomationProperties.SetAutomationId(install, "welcome_install_samsung");
                    install.Click += delegate { DriverUI.InstallSamsung(() => { if (IsOpen && !whatsNew && step == 2) checkStep(); }); };
                    rows.Children.Add(checkRow("!", "Warn", "Warn", Properties.Resources.welcome_check_samsung_missing,
                        Properties.Resources.welcome_check_samsung_missing_detail, install));
                }

                rows.Children.Add(usbRow(usb));
            });
        }

        static FrameworkElement usbRow(IReadOnlyList<UsbFinding> usb)
        {
            if (usb == null)
                return checkRow("…", "Raised", "Dim", Properties.Resources.welcome_check_usb_unknown, null, null);

            List<UsbFinding> trouble = usb.Where(f => f.Kind == UsbFindingKind.MissingDriver || f.Kind == UsbFindingKind.DriverProblem).ToList();
            if (trouble.Count > 0)
            {
                Button check = new Button { Content = Properties.Resources.usb_check, Margin = new Thickness(12, 0, 0, 0) };
                AutomationProperties.SetAutomationId(check, "welcome_usb_check");
                check.Click += delegate { FastbootUI.checkUsb(); };
                return checkRow("!", "Warn", "Warn", Properties.Resources.welcome_check_usb_trouble,
                    string.Join("\n", trouble.Select(f => Helper.ltr(f.Device.Name + (f.UsbId != null ? "  (" + f.UsbId + ")" : "")))), check);
            }
            string connected =
                usb.Any(f => f.Kind == UsbFindingKind.FastbootReady) ? Properties.Resources.welcome_check_usb_fastboot
                : usb.Any(f => f.Kind == UsbFindingKind.AdbReady) ? Properties.Resources.welcome_check_usb_adb
                : usb.Any(f => f.Kind == UsbFindingKind.SamsungDownload) ? Properties.Resources.welcome_check_usb_samsung
                : usb.Any(f => f.Kind == UsbFindingKind.QualcommEdl || f.Kind == UsbFindingKind.MediaTekDownload || f.Kind == UsbFindingKind.UnisocDownload)
                    ? Properties.Resources.welcome_check_usb_download
                : null;
            return connected != null
                ? checkRow("✓", "Ok", "Ok", connected, null, null)
                : checkRow("…", "Raised", "Dim", Properties.Resources.welcome_check_usb_none, Properties.Resources.welcome_check_usb_none_detail, null);
        }

        /// <summary>One line of the quick check: a round mark, what was found, and an action when there is one.</summary>
        static FrameworkElement checkRow(string mark, string markBack, string markColor, string title, string detail, Button action)
        {
            DockPanel row = new DockPanel();
            Border dot = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = Palette.Get(markBack == "Raised" ? "Raised" : markBack == "Ok" ? "AccentSoft" : markBack + "Soft"),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = mark,
                    FontWeight = FontWeights.Bold,
                    Foreground = Palette.Get(markColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            if (action != null)
            {
                action.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(action, Dock.Right);
                row.Children.Add(action);
            }
            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(detail))
                text.Children.Add(new TextBlock { Text = detail, Style = (Style)W.FindResource("Note"), Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(text);

            return new Border
            {
                Style = (Style)W.FindResource("Card"),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = row,
            };
        }

        // ------------------------------------------------------------------ 4. safety

        const string GlyphBattery = "M3,7 H18 A1,1 0 0 1 19,8 V16 A1,1 0 0 1 18,17 H3 A1,1 0 0 1 2,16 V8 A1,1 0 0 1 3,7 Z M22,11 V13 M6,10 V14 M10,10 V14";
        const string GlyphCable = "M8,2 V7 M16,2 V7 M6,7 H18 V11 A6,6 0 0 1 6,11 Z M12,17 V22";
        const string GlyphShield = "M12,2 L4,5 V11 C4,16 7.5,20 12,22 C16.5,20 20,16 20,11 V5 Z M9,12 L11,14 L15,10";

        static void safetyStep()
        {
            W.welcome_title.Text = Properties.Resources.welcome_safety_title;
            W.welcome_subtitle.Text = Properties.Resources.welcome_safety_intro;

            UniformGrid grid = new UniformGrid { Columns = 2, MaxWidth = 780 };
            grid.Children.Add(rule((Geometry)W.FindResource("IconBackup"), "Accent", Properties.Resources.welcome_rule_backup, Properties.Resources.welcome_rule_backup_detail));
            grid.Children.Add(rule(Geometry.Parse(GlyphBattery), "Warn", Properties.Resources.welcome_rule_battery, Properties.Resources.welcome_rule_battery_detail));
            grid.Children.Add(rule(Geometry.Parse(GlyphCable), "Danger", Properties.Resources.welcome_rule_cable, Properties.Resources.welcome_rule_cable_detail));
            grid.Children.Add(rule(Geometry.Parse(GlyphShield), "Accent", Properties.Resources.welcome_rule_files, Properties.Resources.welcome_rule_files_detail));

            StackPanel body = new StackPanel();
            body.Children.Add(grid);
            body.Children.Add(new TextBlock
            {
                Text = Properties.Resources.welcome_again_note,
                Style = (Style)W.FindResource("Note"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            });
            W.welcome_body.Content = body;
        }

        static FrameworkElement rule(Geometry icon, string color, string title, string detail)
        {
            DockPanel row = new DockPanel();
            Border mark = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(10),
                Background = Palette.Get(color == "Accent" ? "AccentSoft" : color + "Soft"),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new System.Windows.Shapes.Path
                {
                    Data = icon,
                    Stroke = Palette.Get(color),
                    StrokeThickness = 1.8,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(9),
                    FlowDirection = FlowDirection.LeftToRight,
                },
            };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            StackPanel text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = detail, Style = (Style)W.FindResource("Note"), Margin = new Thickness(0, 4, 0, 0) });
            row.Children.Add(text);
            return new Border
            {
                Style = (Style)W.FindResource("Card"),
                Margin = new Thickness(6),
                Child = row,
            };
        }

        // ------------------------------------------------------------------ What's new

        static void showWhatsNew()
        {
            List<string> list = notes();
            if (list == null)
                return;
            whatsNew = true;
            W.welcome_layer.Visibility = Visibility.Visible;
            W.welcome_dots.Visibility = Visibility.Collapsed;
            W.welcome_title.Text = string.Format(Properties.Resources.whats_new_title, "v" + MainWindow.version);
            W.welcome_subtitle.Text = Properties.Resources.whats_new_intro;
            W.welcome_back.Content = Properties.Resources.welcome_take_tour;
            W.welcome_next.Content = Properties.Resources.whats_new_ok;
            AutomationProperties.SetAutomationId(W.welcome_body, "whats_new");

            StackPanel bullets = new StackPanel();
            foreach (string note in list)
            {
                Grid row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                TextBlock dot = new TextBlock { Text = "•", FontWeight = FontWeights.Bold, Foreground = Palette.Get("Accent"), FontSize = 14 };
                TextBlock text = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 22 };
                Grid.SetColumn(text, 1);
                row.Children.Add(dot);
                row.Children.Add(text);
                bullets.Children.Add(row);
            }
            W.welcome_body.Content = new Border
            {
                Style = (Style)W.FindResource("Card"),
                MaxWidth = 720,
                Padding = new Thickness(24, 20, 24, 12),
                Child = bullets,
            };
            W.welcome_scroll.ScrollToTop();
            W.welcome_next.Focus();
            LogStore.Append("what's new: v" + MainWindow.version);
        }

        /// <summary>This version's notes in the app's language (Arabic, or English for the others); null when there are none.</summary>
        static List<string> notes()
        {
            string changelog;
            using (Stream stream = typeof(WelcomeUI).Assembly.GetManifestResourceStream("CHANGELOG.md"))
            {
                if (stream == null)
                    return null;
                using (StreamReader reader = new StreamReader(stream))
                    changelog = reader.ReadToEnd();
            }
            string section = ReleaseFeed.ChangelogSection(changelog, MainWindow.version);
            if (section == null)
                return null;
            ReleaseFeed.SplitNotes(section, out List<string> arabic, out List<string> english);
            List<string> list = Languages.Current.TwoLetterISOLanguageName == "ar" && arabic.Count > 0 ? arabic
                : english.Count > 0 ? english : arabic;
            return list.Count > 0 ? list : null;
        }
    }
}
