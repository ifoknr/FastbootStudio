using System;
using System.Collections.Generic;
using System.Text;

namespace FastbootEnhance.Core.Help
{
    public enum HelpSectionKind
    {
        /// <summary>What the page is for.</summary>
        Purpose,

        /// <summary>The steps, in order.</summary>
        Steps,

        /// <summary>What can go wrong for good: shown in red.</summary>
        Danger,

        /// <summary>Good to know.</summary>
        Tips,

        /// <summary>Words the page uses, each with a short meaning.</summary>
        Terms,
    }

    public enum HelpItemKind
    {
        Paragraph,
        Numbered,
        Bullet,
        Term,
    }

    public sealed class HelpItem
    {
        public HelpItem(HelpItemKind kind, string text, string term = null)
        {
            Kind = kind;
            Text = text;
            Term = term;
        }

        public HelpItemKind Kind { get; }

        /// <summary>The text; **bold** marks are kept, see <see cref="HelpDoc.Spans"/>.</summary>
        public string Text { get; }

        /// <summary>For a term: the word itself.</summary>
        public string Term { get; }
    }

    public sealed class HelpSection
    {
        public HelpSection(HelpSectionKind kind)
        {
            Kind = kind;
        }

        public HelpSectionKind Kind { get; }
        public List<HelpItem> Items { get; } = new List<HelpItem>();
    }

    /// <summary>
    /// One page's help, written as a small Markdown file: "## purpose", "## steps",
    /// "## danger", "## tips" and "## terms" headings; "1. " numbered steps, "- " bullets,
    /// "word | meaning" terms, and plain lines for paragraphs. Lines next to each other make
    /// one paragraph or item; an empty line ends it.
    /// </summary>
    public sealed class HelpDoc
    {
        public List<HelpSection> Sections { get; } = new List<HelpSection>();

        /// <summary>What does not follow the format: an unknown heading, text before the first one.</summary>
        public List<string> Problems { get; } = new List<string>();

        public HelpSection Get(HelpSectionKind kind)
        {
            foreach (HelpSection s in Sections)
            {
                if (s.Kind == kind)
                    return s;
            }
            return null;
        }

        public static HelpDoc Parse(string text)
        {
            HelpDoc doc = new HelpDoc();
            HelpSection section = null;
            HelpItemKind? kind = null;
            string term = null;
            StringBuilder current = new StringBuilder();
            int number = 0;

            void flush()
            {
                if (kind != null && current.Length > 0)
                    section.Items.Add(new HelpItem(kind.Value, current.ToString().Trim(), term));
                kind = null;
                term = null;
                current.Clear();
            }

            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                number++;
                string line = raw.Trim();
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (section != null)
                        flush();
                    HelpSectionKind parsed;
                    if (!Enum.TryParse(line.Substring(3).Trim(), true, out parsed))
                    {
                        doc.Problems.Add("line " + number + ": unknown heading \"" + line + "\"");
                        section = null;
                        continue;
                    }
                    if (doc.Get(parsed) != null)
                        doc.Problems.Add("line " + number + ": \"" + line + "\" is there twice");
                    section = new HelpSection(parsed);
                    doc.Sections.Add(section);
                    continue;
                }
                if (line.Length == 0)
                {
                    if (section != null)
                        flush();
                    continue;
                }
                if (section == null)
                {
                    doc.Problems.Add("line " + number + ": text outside a section");
                    continue;
                }

                int dot = NumberedPrefix(line);
                if (dot > 0)
                {
                    flush();
                    kind = HelpItemKind.Numbered;
                    current.Append(line.Substring(dot).Trim());
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    flush();
                    kind = HelpItemKind.Bullet;
                    current.Append(line.Substring(2).Trim());
                }
                else if (section.Kind == HelpSectionKind.Terms && kind == null && line.IndexOf('|') > 0)
                {
                    int bar = line.IndexOf('|');
                    kind = HelpItemKind.Term;
                    term = line.Substring(0, bar).Trim();
                    current.Append(line.Substring(bar + 1).Trim());
                    flush();
                }
                else
                {
                    // A line right under an item carries it on; otherwise it starts a paragraph.
                    if (kind == null)
                        kind = HelpItemKind.Paragraph;
                    else
                        current.Append(' ');
                    current.Append(line);
                }
            }
            if (section != null)
                flush();
            return doc;
        }

        /// <summary>"12. text" → the length of "12."; 0 when the line is not a numbered step.</summary>
        static int NumberedPrefix(string line)
        {
            int i = 0;
            while (i < line.Length && i < 3 && char.IsDigit(line[i]))
                i++;
            return i > 0 && i + 1 < line.Length && line[i] == '.' && line[i + 1] == ' ' ? i + 1 : 0;
        }

        /// <summary>The text cut at its **bold** marks: (text, bold) pieces in order. An unclosed mark is kept as text.</summary>
        public static List<KeyValuePair<string, bool>> Spans(string text)
        {
            List<KeyValuePair<string, bool>> spans = new List<KeyValuePair<string, bool>>();
            text = text ?? "";
            int at = 0;
            while (at < text.Length)
            {
                int open = text.IndexOf("**", at, StringComparison.Ordinal);
                int close = open < 0 ? -1 : text.IndexOf("**", open + 2, StringComparison.Ordinal);
                if (open < 0 || close < 0)
                {
                    spans.Add(new KeyValuePair<string, bool>(text.Substring(at), false));
                    break;
                }
                if (open > at)
                    spans.Add(new KeyValuePair<string, bool>(text.Substring(at, open - at), false));
                if (close > open + 2)
                    spans.Add(new KeyValuePair<string, bool>(text.Substring(open + 2, close - open - 2), true));
                at = close + 2;
            }
            return spans;
        }
    }
}
