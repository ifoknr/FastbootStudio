using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FastbootEnhance
{
    /// <summary>
    /// Find and copy for a read-only log box (the Logs page, the Flash page's live log, the
    /// Terminal's output). Ctrl+F opens a slim bar above the box: a field, "3 of 17", previous
    /// and next (Enter, Shift+Enter, F3), a case switch, and Esc to close. Every match in view is
    /// marked, the current one more strongly. The right-click menu copies the selection, the
    /// line under the mouse or everything, selects all, and opens the bar.
    /// A log can hold hundreds of thousands of characters, so the box's text is copied out once
    /// when it has changed, not on each keystroke; the search waits for a pause in typing; and
    /// only the matches on screen are drawn, over the text, which is never re-rendered.
    /// </summary>
    sealed class LogFind
    {
        /// <summary>More than this many matches are not counted further ("50000+" is no more useful than the cap).</summary>
        const int MaxMatches = 50000;

        readonly TextBox box;
        readonly string id;
        readonly List<int> matches = new List<int>();
        readonly DispatcherTimer pause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };

        Border bar;
        TextBox query;
        TextBlock count;
        Button caseButton;
        Marks marks;

        string text = "";
        bool stale = true;
        bool matchCase;
        int current = -1;
        int length;
        int menuLine = -1;

        LogFind(TextBox box, string id)
        {
            this.box = box;
            this.id = id;
        }

        /// <summary>True while the bar is showing.</summary>
        public bool IsOpen => bar != null && bar.Visibility == Visibility.Visible;

        /// <summary>
        /// Gives <paramref name="box"/> the find bar and the menu. <paramref name="id"/> names the
        /// parts for UI automation ("find_box_logs").
        /// </summary>
        public static LogFind Attach(TextBox box, string id)
        {
            LogFind find = new LogFind(box, id);
            find.build();
            return find;
        }

        void build()
        {
            // Shown while the keyboard is in the find field, or the current match could not be seen.
            box.IsInactiveSelectionHighlightEnabled = true;
            box.ContextMenu = menu();
            box.ContextMenuOpening += delegate
            {
                int at = box.GetCharacterIndexFromPoint(Mouse.GetPosition(box), true);
                menuLine = at >= 0 ? box.GetLineIndexFromCharacterIndex(at) : -1;
            };
            box.PreviewKeyDown += onBoxKey;
            box.TextChanged += delegate
            {
                stale = true;
                if (!IsOpen)
                    return;
                // A running flash adds lines many times a second: one search after it pauses.
                pause.Stop();
                pause.Start();
            };
            box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(delegate { redraw(); }));
            box.SizeChanged += delegate { redraw(); };
            pause.Tick += delegate
            {
                pause.Stop();
                search(false);
            };

            bar = makeBar();
            if (!placeBar())
                bar = null;   // nowhere to put it: the menu and Ctrl+C still work
        }

        /// <summary>Puts the bar above the box, in a panel that takes the box's place in its parent.</summary>
        bool placeBar()
        {
            DependencyObject parent = box.Parent;
            DockPanel host = new DockPanel();
            if (parent is Decorator decorator)
            {
                decorator.Child = null;
                decorator.Child = host;
            }
            else if (parent is Panel panel)
            {
                int index = panel.Children.IndexOf(box);
                if (index < 0)
                    return false;
                foreach (DependencyProperty place in new[] { Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty,
                             Grid.ColumnSpanProperty, DockPanel.DockProperty })
                {
                    object value = box.ReadLocalValue(place);
                    if (value != DependencyProperty.UnsetValue)
                    {
                        host.SetValue(place, value);
                        box.ClearValue(place);
                    }
                }
                panel.Children.RemoveAt(index);
                panel.Children.Insert(index, host);
            }
            else
            {
                return false;
            }
            DockPanel.SetDock(bar, Dock.Top);
            host.Children.Add(bar);
            host.Children.Add(box);
            return true;
        }

        Border makeBar()
        {
            Style ghost = (Style)box.FindResource("GhostButton");
            Button small(string label, string tip, string part)
            {
                Button button = new Button
                {
                    Content = label,
                    ToolTip = tip,
                    Style = ghost,
                    MinHeight = 26,
                    MinWidth = 30,
                    Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetName(button, tip);
                AutomationProperties.SetAutomationId(button, "find_" + part + "_" + id);
                DockPanel.SetDock(button, Dock.Right);
                return button;
            }

            Button close = small("✕", Properties.Resources.find_close, "close");
            Button next = small("↓", Properties.Resources.find_next, "next");
            Button previous = small("↑", Properties.Resources.find_prev, "prev");
            caseButton = small("Aa", Properties.Resources.find_case, "case");
            close.Click += delegate { Close(); };
            next.Click += delegate { step(1); };
            previous.Click += delegate { step(-1); };
            caseButton.Click += delegate
            {
                matchCase = !matchCase;
                paintCase();
                search(true);
            };

            count = new TextBlock
            {
                Foreground = Palette.Get("Dim"),
                FontSize = 12,
                Margin = new Thickness(10, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(count, "find_count_" + id);
            DockPanel.SetDock(count, Dock.Right);

            query = new TextBox
            {
                Tag = Properties.Resources.find_hint,
                MinHeight = 26,
                Padding = new Thickness(6, 3, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(query, Properties.Resources.find_hint);
            AutomationProperties.SetAutomationId(query, "find_box_" + id);
            query.TextChanged += delegate
            {
                pause.Stop();
                pause.Start();
            };
            query.PreviewKeyDown += onQueryKey;

            DockPanel row = new DockPanel();
            // Docked right in this order: close at the far end, then next, previous, case and the count.
            row.Children.Add(close);
            row.Children.Add(next);
            row.Children.Add(previous);
            row.Children.Add(caseButton);
            row.Children.Add(count);
            row.Children.Add(query);

            Border border = new Border
            {
                Background = Palette.Get("Raised"),
                BorderBrush = Palette.Get("GlassLine2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true,
                Child = row,
            };
            AutomationProperties.SetAutomationId(border, "find_bar_" + id);
            return border;
        }

        void paintCase()
        {
            caseButton.Style = (Style)box.FindResource(matchCase ? "AccentButton" : "GhostButton");
        }

        ContextMenu menu()
        {
            MenuItem copy = new MenuItem { Header = Properties.Resources.logs_copy };
            copy.Click += delegate { Helper.copyText(box.SelectedText); };
            MenuItem copyLine = new MenuItem { Header = Properties.Resources.copy_line };
            copyLine.Click += delegate
            {
                if (menuLine >= 0 && menuLine < box.LineCount)
                    Helper.copyText(box.GetLineText(menuLine).TrimEnd('\r', '\n'));
            };
            MenuItem copyAll = new MenuItem { Header = Properties.Resources.copy_all };
            copyAll.Click += delegate { Helper.copyText(box.Text); };
            MenuItem selectAll = new MenuItem { Header = Properties.Resources.select_all };
            selectAll.Click += delegate
            {
                box.Focus();
                box.SelectAll();
            };
            MenuItem find = new MenuItem { Header = Properties.Resources.find_menu, InputGestureText = "Ctrl+F" };
            find.Click += delegate { Open(); };

            ContextMenu items = new ContextMenu();
            items.Items.Add(copy);
            items.Items.Add(copyLine);
            items.Items.Add(copyAll);
            items.Items.Add(selectAll);
            items.Items.Add(find);
            items.Opened += delegate
            {
                copy.IsEnabled = box.SelectionLength > 0;
                copyLine.IsEnabled = menuLine >= 0;
            };
            AutomationProperties.SetAutomationId(items, "log_menu_" + id);
            return items;
        }

        /// <summary>Shows the bar with the keyboard in its field; what is selected in the log is looked for.</summary>
        public void Open()
        {
            if (bar == null)
                return;
            if (marks == null)
            {
                AdornerLayer layer = AdornerLayer.GetAdornerLayer(box);
                if (layer != null)
                {
                    marks = new Marks(box, this);
                    layer.Add(marks);
                }
            }
            bar.Visibility = Visibility.Visible;
            string picked = box.SelectedText;
            if (!string.IsNullOrEmpty(picked) && picked.Length <= 200 && picked.IndexOf('\n') < 0)
                query.Text = picked;
            query.Focus();
            query.SelectAll();
            search(true);
        }

        /// <summary>Hides the bar and the marks, and hands the keyboard back to the log.</summary>
        public void Close()
        {
            if (bar == null)
                return;
            pause.Stop();
            bar.Visibility = Visibility.Collapsed;
            matches.Clear();
            current = -1;
            redraw();
            box.Focus();
        }

        void onBoxKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.F)
            {
                Open();
                e.Handled = true;
            }
            else if (IsOpen && e.Key == Key.F3)
            {
                step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
            }
            else if (IsOpen && e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        void onQueryKey(object sender, KeyEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (e.Key == Key.Enter || e.Key == Key.F3)
            {
                // The search may still be waiting for the typing to pause.
                if (pause.IsEnabled)
                {
                    pause.Stop();
                    search(true);
                }
                else
                {
                    step(shift ? -1 : 1);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Finds every match. <paramref name="jump"/> scrolls to the first one at or after what
        /// is on screen; without it (the log grew) the view stays where the person left it.
        /// </summary>
        void search(bool jump)
        {
            if (!IsOpen)
                return;
            if (stale)
            {
                text = box.Text;
                stale = false;
            }

            int keep = current >= 0 && current < matches.Count ? matches[current] : -1;
            matches.Clear();
            string wanted = query.Text;
            length = wanted.Length;
            if (length > 0)
            {
                StringComparison how = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                int at = 0;
                while (matches.Count < MaxMatches && (at = text.IndexOf(wanted, at, how)) >= 0)
                {
                    matches.Add(at);
                    at += length;
                }
            }

            current = -1;
            if (matches.Count > 0)
            {
                int from = jump ? firstVisibleCharacter() : keep;
                int index = matches.BinarySearch(Math.Max(0, from));
                if (index < 0)
                    index = ~index;
                current = index < matches.Count ? index : 0;
            }
            show(jump);
        }

        int firstVisibleCharacter()
        {
            int line = box.GetFirstVisibleLineIndex();
            return line >= 0 ? box.GetCharacterIndexFromLineIndex(line) : 0;
        }

        void step(int by)
        {
            if (stale || pause.IsEnabled)
            {
                pause.Stop();
                search(true);
                return;
            }
            if (matches.Count == 0)
                return;
            current = ((current < 0 ? 0 : current + by) % matches.Count + matches.Count) % matches.Count;
            show(true);
        }

        void show(bool scroll)
        {
            if (length == 0)
                count.Text = "";
            else if (matches.Count == 0)
                count.Text = Properties.Resources.find_none;
            else
                count.Text = string.Format(Properties.Resources.find_count, current + 1,
                    matches.Count >= MaxMatches ? MaxMatches + "+" : matches.Count.ToString());
            count.Foreground = Palette.Get(length > 0 && matches.Count == 0 ? "Warn" : "Dim");

            if (current >= 0 && scroll)
            {
                int start = matches[current];
                box.Select(start, length);
                int line = box.GetLineIndexFromCharacterIndex(start);
                if (line >= 0 && (line < box.GetFirstVisibleLineIndex() || line > box.GetLastVisibleLineIndex()))
                    box.ScrollToLine(Math.Max(0, line - 2));
            }
            redraw();
        }

        void redraw()
        {
            if (marks != null)
                marks.InvalidateVisual();
        }

        /// <summary>The marks over the box: one faint block per match in view, a stronger one on the current match.</summary>
        sealed class Marks : Adorner
        {
            readonly LogFind owner;
            readonly Brush every;
            readonly Brush chosen;

            public Marks(TextBox box, LogFind owner) : base(box)
            {
                this.owner = owner;
                IsHitTestVisible = false;
                ClipToBounds = true;
                every = tint("Warn", 70);
                chosen = tint("Accent", 120);
            }

            static Brush tint(string key, byte alpha)
            {
                Color color = ((SolidColorBrush)Palette.Get(key)).Color;
                SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
                brush.Freeze();
                return brush;
            }

            protected override void OnRender(DrawingContext drawing)
            {
                List<int> found = owner.matches;
                int length = owner.length;
                if (!owner.IsOpen || found.Count == 0 || length == 0)
                    return;

                TextBox box = (TextBox)AdornedElement;
                int firstLine = box.GetFirstVisibleLineIndex(), lastLine = box.GetLastVisibleLineIndex();
                if (firstLine < 0 || lastLine < firstLine)
                    return;
                int from = box.GetCharacterIndexFromLineIndex(firstLine);
                int to = box.GetCharacterIndexFromLineIndex(lastLine) + box.GetLineLength(lastLine);
                if (from < 0 || to < from)
                    return;

                int index = found.BinarySearch(Math.Max(0, from - length));
                if (index < 0)
                    index = ~index;
                for (; index < found.Count && found[index] <= to; index++)
                {
                    Rect head = box.GetRectFromCharacterIndex(found[index]);
                    Rect tail = box.GetRectFromCharacterIndex(found[index] + length);
                    if (head.IsEmpty || tail.IsEmpty)
                        continue;
                    // A match that wraps onto the next line is marked to the end of its first line.
                    Rect block = Math.Abs(head.Top - tail.Top) < 1
                        ? new Rect(new Point(Math.Min(head.Left, tail.Left), head.Top), new Point(Math.Max(head.Left, tail.Left), head.Bottom))
                        : new Rect(head.Left, head.Top, Math.Max(2, box.ActualWidth - head.Left), head.Height);
                    drawing.DrawRoundedRectangle(index == owner.current ? chosen : every, null, block, 2, 2);
                }
            }
        }
    }
}
