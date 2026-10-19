using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FastbootEnhance.Core.Update
{
    /// <summary>The newest release as the app shows it in its update dialog.</summary>
    public sealed class ReleaseInfo
    {
        /// <summary>"v2.6.0".</summary>
        public string Tag;
        /// <summary>"2.6.0".</summary>
        public string Version;
        /// <summary>The release page; always one of the project's own.</summary>
        public string Page;
        public DateTimeOffset? Published;
        /// <summary>The bullets of the release notes, in Arabic and in English.</summary>
        public IReadOnlyList<string> NotesArabic = new string[0];
        public IReadOnlyList<string> NotesEnglish = new string[0];

        /// <summary>The Windows zip: name, link, size, and its SHA-256 as GitHub reports it (null when not given).</summary>
        public string ZipName;
        public string ZipUrl;
        public long ZipBytes;
        public string ZipSha256;
        /// <summary>The "&lt;zip&gt;.sha256" file published next to the zip, or null.</summary>
        public string Sha256Url;
    }

    /// <summary>
    /// Reads the latest GitHub release of Fastboot Studio. Nothing from the reply is trusted
    /// further than it has to be: the page opened is always the project's own, the zip is only
    /// taken from the project's own release downloads, and its SHA-256 is checked before the
    /// person is told to open it.
    /// </summary>
    public static class ReleaseFeed
    {
        public const string Api = "https://api.github.com/repos/ifoknr/FastbootStudio/releases/latest";
        public const string Releases = "https://github.com/ifoknr/FastbootStudio/releases";
        const string Downloads = "https://github.com/ifoknr/FastbootStudio/releases/download/";

        /// <summary>At most one automatic check a day.</summary>
        public static readonly TimeSpan Every = TimeSpan.FromHours(24);

        /// <summary>Notes longer than this are cut, so a damaged reply cannot fill the dialog.</summary>
        const int MaxNotes = 12;
        const int MaxNoteChars = 300;

        static readonly Regex ZipPattern = new Regex(@"^FastbootStudio-v[0-9][0-9A-Za-z.\-]*-win-x64\.zip$");
        static readonly Regex TagFolder = new Regex(@"^[0-9A-Za-z][0-9A-Za-z._\-]*$");
        static readonly Regex Hex64 = new Regex("^[0-9a-f]{64}$");

        /// <summary>
        /// The release from the API's JSON, or null when it is a draft, a pre-release, or not
        /// readable. <paramref name="allowLocal"/> lets a test feed point the zip at a local file.
        /// </summary>
        public static ReleaseInfo Parse(string json, bool allowLocal = false)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json ?? ""))
                {
                    JsonElement r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object)
                        return null;
                    string tag = Text(r, "tag_name");
                    if (tag == null || True(r, "draft") || True(r, "prerelease"))
                        return null;
                    string version = tag.Trim().TrimStart('v', 'V');
                    if (Numbers(version) == null)
                        return null;

                    ReleaseInfo info = new ReleaseInfo { Tag = tag.Trim(), Version = version };
                    string page = Text(r, "html_url");
                    info.Page = page != null && page.StartsWith(Releases + "/", StringComparison.Ordinal) ? page : Releases;

                    DateTimeOffset published;
                    string when = Text(r, "published_at");
                    if (when != null && DateTimeOffset.TryParse(when, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal, out published))
                        info.Published = published;

                    List<string> arabic, english;
                    SplitNotes(Text(r, "body"), out arabic, out english);
                    info.NotesArabic = arabic;
                    info.NotesEnglish = english;

                    JsonElement assets;
                    if (r.TryGetProperty("assets", out assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement a in assets.EnumerateArray())
                        {
                            string name = Text(a, "name");
                            string url = Text(a, "browser_download_url");
                            if (name == null || url == null || !Trusted(url, name, allowLocal))
                                continue;
                            if (info.ZipName == null && ZipPattern.IsMatch(name))
                            {
                                info.ZipName = name;
                                info.ZipUrl = url;
                                JsonElement size;
                                info.ZipBytes = a.TryGetProperty("size", out size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0;
                                info.ZipSha256 = Digest(Text(a, "digest"));
                            }
                        }
                        // The checksum file belongs to the zip found above.
                        if (info.ZipName != null)
                        {
                            foreach (JsonElement a in assets.EnumerateArray())
                            {
                                string name = Text(a, "name");
                                string url = Text(a, "browser_download_url");
                                if (name == info.ZipName + ".sha256" && url != null && Trusted(url, name, allowLocal))
                                    info.Sha256Url = url;
                            }
                        }
                    }
                    return info;
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// The bullets of a release body: the Arabic ones before the "**English**" line, the
        /// English ones after it, up to the "---" that starts the download notes.
        /// </summary>
        public static void SplitNotes(string body, out List<string> arabic, out List<string> english)
        {
            arabic = new List<string>();
            english = new List<string>();
            if (string.IsNullOrEmpty(body))
                return;

            List<string> into = arabic;
            foreach (string raw in body.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line == "---")
                    break;
                if (line.Trim('*').Trim().Equals("English", StringComparison.OrdinalIgnoreCase))
                {
                    into = english;
                    continue;
                }
                if (!(line.StartsWith("- ") || line.StartsWith("* ")) || into.Count >= MaxNotes)
                    continue;
                string text = Plain(line.Substring(2));
                if (text.Length == 0)
                    continue;
                if (text.Length > MaxNoteChars)
                    text = text.Substring(0, MaxNoteChars).TrimEnd() + "…";
                into.Add(text);
            }
        }

        /// <summary>Markdown links, bold and code marks taken out: the dialog shows plain text.</summary>
        static string Plain(string text)
        {
            text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
            text = text.Replace("**", "").Replace("`", "");
            // Control characters (and bidi overrides) never reach the screen.
            text = Regex.Replace(text, @"[\u0000-\u001F\u007F‪-‮⁦-⁩]", "");
            return text.Trim();
        }

        /// <summary>The hash in a "&lt;zip&gt;.sha256" file ("hash  name" or just the hash), or null.</summary>
        public static string ParseSha256File(string text, string zipName)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string[] parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;
                string hash = parts[0].ToLowerInvariant();
                if (!Hex64.IsMatch(hash))
                    continue;
                if (parts.Length == 1 || parts[parts.Length - 1].TrimStart('*') == zipName)
                    return hash;
            }
            return null;
        }

        /// <summary>True when <paramref name="latest"/> ("2.6.10") is newer than <paramref name="installed"/> ("2.6.9").</summary>
        public static bool IsNewer(string latest, string installed)
        {
            int[] a = Numbers(latest), b = Numbers(installed);
            if (a == null || b == null)
                return false;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y)
                    return x > y;
            }
            return false;
        }

        /// <summary>A file the app may download: only from the project's own releases.</summary>
        static bool Trusted(string url, string name, bool allowLocal)
        {
            if (url.StartsWith(Downloads, StringComparison.Ordinal))
            {
                string rest = url.Substring(Downloads.Length);
                int slash = rest.IndexOf('/');
                if (slash <= 0)
                    return false;
                // The tag folder: plain characters only, so the link cannot climb out of the project.
                string folder = rest.Substring(0, slash);
                return TagFolder.IsMatch(folder) && !folder.Contains("..")
                    && rest.Substring(slash + 1) == Uri.EscapeDataString(name);
            }
            return allowLocal && !url.StartsWith(@"\\", StringComparison.Ordinal)
                && (System.IO.Path.IsPathRooted(url) || Regex.IsMatch(url, @"^[A-Za-z]:\\"));
        }

        /// <summary>"sha256:abcd…" as GitHub reports an asset's digest, as 64 lower-case hex digits.</summary>
        static string Digest(string digest)
        {
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return null;
            string hash = digest.Substring(7).Trim().ToLowerInvariant();
            return Hex64.IsMatch(hash) ? hash : null;
        }

        static string Text(JsonElement e, string name)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        static bool True(JsonElement e, string name)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.True;
        }

        static int[] Numbers(string version)
        {
            string core = (version ?? "").Trim().TrimStart('v', 'V').Split('-', '+', ' ')[0];
            string[] parts = core.Split('.');
            if (parts.Length == 0 || parts.Length > 4)
                return null;
            int[] n = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n[i]))
                    return null;
            return n;
        }
    }
}
