using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FastbootEnhance.Core.Usb
{
    /// <summary>
    /// Samsung's USB driver for Windows, which Download (Odin) mode needs: where it is
    /// downloaded from, how an installed copy is recognised, and whose signature it must carry.
    /// </summary>
    public static class SamsungDriver
    {
        /// <summary>Samsung's page for the driver; opened when the download cannot be made.</summary>
        public const string PageUrl = "https://developer.samsung.com/android-usb-driver";

        /// <summary>The page's own download link as of v1.9.5.0, used when the page cannot be read.</summary>
        public const string KnownFileUrl = "https://developer.samsung.com/SDP/file/a2fcaf64-7aa3-49f7-b06f-9e646590d528";

        /// <summary>The installer is about 37 MB; anything far bigger is not it.</summary>
        public const long MaxDownloadBytes = 200L * 1024 * 1024;

        /// <summary>The organisation in the certificate Samsung signs the installer with.</summary>
        const string Signer = "Samsung Electronics CO., LTD.";

        static readonly Regex FileLink = new Regex(@"href=""(/SDP/file/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})""",
            RegexOptions.CultureInvariant);

        /// <summary>The download link on Samsung's driver page, made absolute; null when there is none.</summary>
        public static string FindDownloadLink(string html)
        {
            Match m = FileLink.Match(html ?? "");
            return m.Success ? "https://developer.samsung.com" + m.Groups[1].Value : null;
        }

        /// <summary>
        /// A folder in Windows' driver store (DriverStore\FileRepository) left by the driver for
        /// Download mode: ssudbus.inf takes the phone, ssudmdm.inf gives Odin its port.
        /// </summary>
        public static bool IsDriverStoreFolder(string folderName)
        {
            string name = folderName ?? "";
            return name.StartsWith("ssudbus.inf_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ssudmdm.inf_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The installer's entry in Windows' list of programs: "SAMSUNG USB Drivers for Mobile Phones".</summary>
        public static bool IsProgramName(string displayName)
        {
            return (displayName ?? "").IndexOf("SAMSUNG USB Driver", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// True when a certificate subject ("CN=…, O=…, C=KR" as Windows writes it) names
        /// Samsung Electronics in Korea. Only meaningful for a signature Windows has verified.
        /// </summary>
        public static bool IsSamsungSubject(string subject)
        {
            Dictionary<string, string> parts = SubjectParts(subject);
            return parts.TryGetValue("O", out string org) && string.Equals(org, Signer, StringComparison.OrdinalIgnoreCase)
                && parts.TryGetValue("C", out string country) && string.Equals(country, "KR", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The attributes of a distinguished name; values in quotes may hold commas.</summary>
        static Dictionary<string, string> SubjectParts(string subject)
        {
            Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string s = subject ?? "";
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == ',' || s[i] == ';'))
                    i++;
                int eq = s.IndexOf('=', i);
                if (eq < 0)
                    break;
                string key = s.Substring(i, eq - i).Trim();
                i = eq + 1;
                StringBuilder value = new StringBuilder();
                if (i < s.Length && s[i] == '"')
                {
                    i++;
                    while (i < s.Length)
                    {
                        if (s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"')
                        {
                            value.Append('"');
                            i += 2;
                            continue;
                        }
                        if (s[i] == '"')
                        {
                            i++;
                            break;
                        }
                        value.Append(s[i++]);
                    }
                    while (i < s.Length && s[i] != ',' && s[i] != ';')
                        i++;
                }
                else
                {
                    while (i < s.Length && s[i] != ',' && s[i] != ';')
                    {
                        // RFC 4514 style "\," keeps a comma in the value.
                        if (s[i] == '\\' && i + 1 < s.Length)
                            i++;
                        value.Append(s[i++]);
                    }
                }
                // The first of a repeated attribute wins; a forged later one cannot replace it.
                if (key.Length > 0 && !parts.ContainsKey(key))
                    parts[key] = value.ToString().Trim();
            }
            return parts;
        }
    }
}
