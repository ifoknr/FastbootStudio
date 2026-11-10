using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace FastbootEnhance.Core.Usb
{
    /// <summary>
    /// Google's USB driver for Windows (the one Android Studio's SDK Manager installs), for
    /// fastboot and adb on Pixels and the many phones that use Google's USB ids. It comes as a
    /// zip from dl.google.com; Google's SDK catalogue names the current one with its size and
    /// SHA-1, and r13 is pinned by SHA-256 as well. Windows checks the driver's own signature
    /// (Microsoft's, on its catalog files) when pnputil installs it.
    /// </summary>
    public static class GoogleDriver
    {
        /// <summary>Google's SDK catalogue; its "extras;google;usb_driver" package names the zip.</summary>
        public const string CatalogUrl = "https://dl.google.com/android/repository/addon2-3.xml";

        public const string RepositoryUrl = "https://dl.google.com/android/repository/";

        /// <summary>Google's page for the driver; opened when the download cannot be made.</summary>
        public const string PageUrl = "https://developer.android.com/studio/run/win-usb";

        /// <summary>r13, the current one since 2014, used when the catalogue cannot be read.</summary>
        public static readonly DriverArchive Known = new DriverArchive
        {
            Revision = 13,
            Url = RepositoryUrl + "usb_driver_r13-windows.zip",
            Size = 8682039,
            Sha1 = "08a48c39084e9443f6146c239cbd3be6f91e681b",
            Sha256 = "360b01d3dfb6c41621a3a64ae570dfac2c9a40cca1b5a1f136ae90d02f5e9e0b",
        };

        /// <summary>The zip is about 8 MB; anything far bigger is not it.</summary>
        public const long MaxDownloadBytes = 64L * 1024 * 1024;

        /// <summary>The setup file inside the zip.</summary>
        public const string InfName = "android_winusb.inf";

        public sealed class DriverArchive
        {
            public int Revision { get; internal set; }
            public string Url { get; internal set; }
            public long Size { get; internal set; }
            public string Sha1 { get; internal set; }

            /// <summary>Set for the pinned revision only; the catalogue gives SHA-1.</summary>
            public string Sha256 { get; internal set; }
        }

        /// <summary>
        /// The Windows zip of the USB driver package in Google's catalogue; null when the text is
        /// not the catalogue or has no such archive. The url must be a plain file name on
        /// dl.google.com, as Google's catalogue writes them.
        /// </summary>
        public static DriverArchive FromCatalog(string xml)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml ?? "");
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
            foreach (XElement package in doc.Descendants())
            {
                if (package.Name.LocalName != "remotePackage" || (string)package.Attribute("path") != "extras;google;usb_driver")
                    continue;
                int revision = 0;
                foreach (XElement e in package.Descendants())
                {
                    if (e.Name.LocalName == "major")
                        int.TryParse(e.Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision);
                }
                foreach (XElement archive in package.Descendants())
                {
                    if (archive.Name.LocalName != "archive")
                        continue;
                    string os = child(archive, "host-os");
                    XElement complete = null;
                    foreach (XElement c in archive.Elements())
                    {
                        if (c.Name.LocalName == "complete")
                            complete = c;
                    }
                    if (complete == null || (os != null && os != "windows"))
                        continue;
                    string url = child(complete, "url");
                    string sha1 = null;
                    foreach (XElement c in complete.Elements())
                    {
                        if (c.Name.LocalName == "checksum" && (string)c.Attribute("type") == "sha1")
                            sha1 = c.Value.Trim().ToLowerInvariant();
                    }
                    if (!long.TryParse(child(complete, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out long size))
                        continue;
                    if (!isPlainZipName(url) || sha1 == null || sha1.Length != 40 || size <= 0 || size > MaxDownloadBytes)
                        continue;
                    return new DriverArchive
                    {
                        Revision = revision,
                        Url = RepositoryUrl + url,
                        Size = size,
                        Sha1 = sha1,
                        Sha256 = string.Equals(url, "usb_driver_r13-windows.zip", StringComparison.Ordinal) ? Known.Sha256 : null,
                    };
                }
            }
            return null;
        }

        static string child(XElement parent, string name)
        {
            foreach (XElement c in parent.Elements())
            {
                if (c.Name.LocalName == name)
                    return c.Value.Trim();
            }
            return null;
        }

        static bool isPlainZipName(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (char c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                    return false;
            }
            return !name.Contains("..");
        }

        /// <summary>
        /// Whether a downloaded file is the archive: its size, its SHA-1, and its SHA-256 when one
        /// is pinned. Returns null when it is, otherwise what differs.
        /// </summary>
        public static string Check(Stream file, DriverArchive archive)
        {
            if (file.Length != archive.Size)
                return "size " + file.Length + " instead of " + archive.Size;
            file.Position = 0;
            string sha1, sha256;
            using (SHA1 h1 = SHA1.Create())
            using (SHA256 h2 = SHA256.Create())
            {
                byte[] buffer = new byte[1 << 16];
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                {
                    h1.TransformBlock(buffer, 0, read, null, 0);
                    h2.TransformBlock(buffer, 0, read, null, 0);
                }
                h1.TransformFinalBlock(buffer, 0, 0);
                h2.TransformFinalBlock(buffer, 0, 0);
                sha1 = Samsung.OdinTar.Hex(h1.Hash);
                sha256 = Samsung.OdinTar.Hex(h2.Hash);
            }
            file.Position = 0;
            if (sha1 != archive.Sha1)
                return "SHA-1 " + sha1 + " instead of " + archive.Sha1;
            if (archive.Sha256 != null && sha256 != archive.Sha256)
                return "SHA-256 " + sha256 + " instead of " + archive.Sha256;
            return null;
        }

        /// <summary>
        /// Unpacks the driver's files into <paramref name="folder"/> and returns the path of its
        /// setup file. Only the "usb_driver/" folder is taken, and no entry may point outside it.
        /// </summary>
        public static string Extract(Stream zip, string folder)
        {
            string root = Path.GetFullPath(folder);
            string inf = null;
            using (ZipArchive archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith("usb_driver/", StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal))
                        continue;
                    string target = Path.GetFullPath(Path.Combine(root, name.Substring("usb_driver/".Length)));
                    if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("the zip has an entry outside its folder: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream from = entry.Open())
                    using (FileStream to = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        from.CopyTo(to);
                    if (string.Equals(name, "usb_driver/" + InfName, StringComparison.OrdinalIgnoreCase))
                        inf = target;
                }
            }
            if (inf == null)
                throw new InvalidDataException("the zip has no " + InfName);
            return inf;
        }

        /// <summary>pnputil's arguments: add the driver to Windows' store and install it on matching devices.</summary>
        public static string PnputilArguments(string infPath) => "/add-driver \"" + infPath + "\" /install";

        /// <summary>
        /// What pnputil's exit code means: 0 done; 3010 done, a restart finishes it; 259 the
        /// driver is in the store but no plugged-in device took it (normal with no phone attached).
        /// </summary>
        public static bool PnputilSucceeded(int exitCode) => exitCode == 0 || exitCode == 3010 || exitCode == 259;

        /// <summary>A folder in Windows' driver store (DriverStore\FileRepository) left by this driver.</summary>
        public static bool IsDriverStoreFolder(string folderName)
        {
            return (folderName ?? "").StartsWith("android_winusb.inf_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Phones whose driver trouble this driver can fix: Google's own vendor id. Other makers'
        /// phones need their maker's driver.
        /// </summary>
        public static bool Covers(string vid) => string.Equals(vid, "18D1", StringComparison.OrdinalIgnoreCase);
    }
}
