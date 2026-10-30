using FastbootEnhance.Core.Help;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FastbootEnhance
{
    /// <summary>
    /// The ؟ in the corner of every page and the panel it opens at the side: what the page is
    /// for, its steps, what can go wrong for good, and the words it uses. The text is in
    /// Help\&lt;language&gt;\&lt;page&gt;.md, inside the exe; languages without their own files get English.
    /// </summary>
    static class HelpUI
    {
        /// <summary>Where questions go: the Telegram group.</summary>
        const string AskUrl = "https://t.me/BeNeXTBrO";

        static MainWindow W => MainWindow.THIS;

        static readonly Dictionary<TabItem, string> pages = new Dictionary<TabItem, string>();
        static readonly Dictionary<string, HelpDoc> loaded = new Dictionary<string, HelpDoc>(StringComparer.Ordinal);
        static TabItem shownFor;

        public static bool IsOpen => W.help_layer.Visibility == Visibility.Visible;

        public static void init()
        {
            foreach (TabItem tab in W.main_tabs.Items.OfType<TabItem>())
            {
                string page = tab.Name.EndsWith("_tab", StringComparison.Ordinal) ? tab.Name.Substring(0, tab.Name.Length - 4) : tab.Name;
                pages[tab] = page;
                Button button = page == "about" ? W.about_help : addButton(tab);
                if (button == null)
                    continue;
                button.Content = Languages.RightToLeft ? "؟" : "?";
                button.ToolTip = Properties.Resources.help_tooltip;
                AutomationProperties.SetAutomationId(button, "help_" + page);
                AutomationProperties.SetName(button, Properties.Resources.help_tooltip);
                TabItem target = tab;
                button.Click += delegate
                {
                    if (IsOpen && shownFor == target)
                        close();
                    else
                        open(target);
                };
            }

            W.help_close.Click += delegate { close(); };
            W.help_scrim.MouseLeftButtonDown += delegate { close(); };
            W.help_ask.Click += delegate { openLink(AskUrl); };

            // Another page while the help is open: its own help follows.
            W.main_tabs.SelectionChanged += (sender, e) =>
            {
                if (e.OriginalSource == W.main_tabs && IsOpen && W.main_tabs.SelectedItem is TabItem tab && tab != shownFor)
                    open(tab);
            };
        }

        /// <summary>Esc closes the panel; true when it was open.</summary>
        public static bool HandleKey(KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !IsOpen)
                return false;
            close();
            e.Handled = true;
            return true;
        }

        /// <summary>A ؟ at the outer end of the page's header, after anything the page shows there.</summary>
        static Button addButton(TabItem tab)
        {
            Border header = pageHeader(tab);
            if (!(header?.Child is DockPanel dock))
                return null;
            Button button = new Button
            {
                Style = (Style)W.FindResource("HelpButton"),
                Margin = new Thickness(12, 0, 0, 0),
            };
            DockPanel.SetDock(button, Dock.Right);
            // The first child docked right takes the outer edge.
            dock.Children.Insert(0, button);
            return button;
        }

        static Border pageHeader(TabItem tab)
        {
            Style style = (Style)W.FindResource("PageHeader");
            return (tab.Content as Panel)?.Children.OfType<Border>().FirstOrDefault(b => b.Style == style);
        }

        static void open(TabItem tab)
        {
            string page;
            if (!pages.TryGetValue(tab, out page))
                return;
            if (W.main_tabs.SelectedItem != tab)
                W.main_tabs.SelectedItem = tab;

            HelpDoc doc = load(page);
            W.help_title.Text = tab.Header as string ?? page;
            W.help_body.Children.Clear();
            if (doc == null)
                W.help_body.Children.Add(paragraph(Properties.Resources.help_missing, "Dim"));
            else
                render(doc);
            W.help_scroll.ScrollToTop();

            shownFor = tab;
            W.help_layer.Visibility = Visibility.Visible;
            placeUnderHeader(tab);
            // A page shown for the first time has no size yet while its tab is being selected:
            // placed again once it is laid out.
            W.Dispatcher.BeginInvoke(new Action(() => { if (shownFor == tab) placeUnderHeader(tab); }),
                System.Windows.Threading.DispatcherPriority.Loaded);
            LogStore.Append("help: " + page);
        }

        /// <summary>Under the page's header, so its ؟ stays in sight to close the panel again.</summary>
        static void placeUnderHeader(TabItem tab)
        {
            Border header = pageHeader(tab);
            double top = header != null && header.IsLoaded ? header.ActualHeight : 0;
            W.help_scrim.Margin = new Thickness(0, top, 0, 0);
            W.help_panel.Margin = new Thickness(0, top, 0, 0);
        }

        static void close()
        {
            W.help_layer.Visibility = Visibility.Collapsed;
            shownFor = null;
        }

        /// <summary>The page's help in the app's language, else in English; null when there is none.</summary>
        static HelpDoc load(string page)
        {
            string language = Languages.Current.TwoLetterISOLanguageName == "ar" ? "ar" : "en";
            string key = language + "/" + page;
            HelpDoc doc;
            if (loaded.TryGetValue(key, out doc))
                return doc;

            string text = read("Help." + language + "." + page + ".md") ?? read("Help.en." + page + ".md");
            doc = text == null ? null : HelpDoc.Parse(text);
            loaded[key] = doc;
            return doc;
        }

        static string read(string resource)
        {
            using (Stream stream = typeof(HelpUI).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null)
                    return null;
                using (StreamReader reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
        }

        // ------------------------------------------------------------------ drawing

        static void render(HelpDoc doc)
        {
            foreach (HelpSection section in doc.Sections)
            {
                if (section.Items.Count == 0)
                    continue;
                if (section.Kind == HelpSectionKind.Danger)
                {
                    StackPanel inside = new StackPanel();
                    inside.Children.Add(heading("⚠  " + Properties.Resources.help_danger, "Danger"));
                    addItems(inside, section);
                    W.help_body.Children.Add(new Border
                    {
                        Background = Palette.Get("DangerSoft"),
                        BorderBrush = Palette.Get("DangerLine"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(14, 12, 14, 8),
                        Margin = new Thickness(0, 2, 0, 16),
                        Child = inside,
                    });
                    continue;
                }

                StackPanel block = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                block.Children.Add(heading(title(section.Kind), null));
                addItems(block, section);
                W.help_body.Children.Add(block);
            }
        }

        static string title(HelpSectionKind kind)
        {
            switch (kind)
            {
                case HelpSectionKind.Purpose: return Properties.Resources.help_purpose;
                case HelpSectionKind.Steps: return Properties.Resources.help_steps;
                case HelpSectionKind.Tips: return Properties.Resources.help_tips;
                case HelpSectionKind.Terms: return Properties.Resources.help_terms;
                default: return Properties.Resources.help_danger;
            }
        }

        static void addItems(Panel into, HelpSection section)
        {
            bool danger = section.Kind == HelpSectionKind.Danger;
            int number = 0;
            foreach (HelpItem item in section.Items)
            {
                switch (item.Kind)
                {
                    case HelpItemKind.Numbered:
                        number++;
                        into.Children.Add(marked(number + ".", item.Text, danger));
                        break;
                    case HelpItemKind.Bullet:
                        into.Children.Add(marked("•", item.Text, danger));
                        break;
                    case HelpItemKind.Term:
                        into.Children.Add(term(item.Term, item.Text));
                        break;
                    default:
                        TextBlock p = paragraph(item.Text, danger ? "Text" : null);
                        p.Margin = new Thickness(0, 0, 0, 6);
                        into.Children.Add(p);
                        break;
                }
            }
        }

        static FrameworkElement heading(string text, string color)
        {
            DockPanel row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            if (color == null)
            {
                Ellipse dot = new Ellipse { Width = 7, Height = 7, Fill = Palette.Get("Accent"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
            }
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Get(color ?? "Text"),
                TextWrapping = TextWrapping.Wrap,
            });
            return row;
        }

        /// <summary>A step or bullet: its mark in a narrow column, the text wrapping beside it.</summary>
        static FrameworkElement marked(string mark, string text, bool danger)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock m = new TextBlock
            {
                Text = mark,
                FontWeight = FontWeights.Bold,
                Foreground = Palette.Get(danger ? "Danger" : "Accent"),
                FontSize = 12.5,
                LineHeight = 20,
            };
            TextBlock t = paragraph(text, null);
            Grid.SetColumn(t, 1);
            row.Children.Add(m);
            row.Children.Add(t);
            return row;
        }

        static FrameworkElement term(string word, string meaning)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock w = new TextBlock
            {
                Text = word,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Get("Accent"),
                FontSize = 12.5,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 8, 0),
            };
            TextBlock t = paragraph(meaning, null);
            Grid.SetColumn(t, 1);
            row.Children.Add(w);
            row.Children.Add(t);
            return row;
        }

        /// <summary>Wrapped text with its **bold** parts in bold.</summary>
        static TextBlock paragraph(string text, string color)
        {
            TextBlock block = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = Palette.Get(color ?? "Text"),
            };
            foreach (KeyValuePair<string, bool> span in HelpDoc.Spans(text))
            {
                Run run = new Run(span.Key);
                if (span.Value)
                    run.FontWeight = FontWeights.SemiBold;
                block.Inlines.Add(run);
            }
            return block;
        }

        static void openLink(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message);
            }
        }
    }
}
