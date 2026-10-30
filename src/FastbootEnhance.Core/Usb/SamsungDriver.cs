using System;
using System.Collections.Generic;
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

        /// <summary>The ProductName in the installer's version resource, which its signature covers.</summary>
        public const string ProductName = "Samsung USB Driver for Mobile Phones";

        /// <summary>
        /// True when a certificate subject, one attribute per line as <see cref="Authenticode"/>
        /// gives it, names Samsung Electronics in Korea. Only meaningful for a signature Windows
        /// has verified. Values are not parsed for quotes or escapes: each line is one attribute.
        /// </summary>
        public static bool IsSamsungSubject(string subject)
        {
            string org = null, country = null;
            foreach (string raw in (subject ?? "").Split('\r', '\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = raw.Substring(0, eq).Trim(), value = raw.Substring(eq + 1).Trim();
                // A repeated attribute makes the subject unclear: it is not taken as Samsung's.
                if (key == "O")
                {
                    if (org != null)
                        return false;
                    org = value;
                }
                else if (key == "C")
                {
                    if (country != null)
                        return false;
                    country = value;
                }
            }
            return string.Equals(org, Signer, StringComparison.OrdinalIgnoreCase)
                && string.Equals(country, "KR", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The installer's own name for itself, from its version resource.</summary>
        public static bool IsInstallerProduct(string productName)
        {
            return string.Equals((productName ?? "").Trim(), ProductName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
