using System;
using System.Text;
using System.Text.RegularExpressions;

namespace FastbootEnhance.Core.Adb
{
    /// <summary>How the app gets root on the device, which decides how shell commands are wrapped.</summary>
    public enum RootAccess
    {
        /// <summary>No root: files in shared storage only.</summary>
        None,

        /// <summary>adb already runs as root, as in TWRP, OrangeFox or "adb root".</summary>
        Direct,

        /// <summary>Through su, from Magisk, KernelSU or APatch.</summary>
        Su
    }

    /// <summary>
    /// Builds adb command lines. A command crosses two parsers on its way to the device: the
    /// Windows command line (adb.exe's argv) and the device's shell. Both are quoted here so
    /// that a path holding spaces, quotes or "$" reaches the device unchanged.
    /// </summary>
    public static class AdbCommand
    {
        static readonly Regex SafeName = new Regex("^[A-Za-z0-9._-]+$");
        static readonly Regex SafePath = new Regex("^/[A-Za-z0-9._/-]+$");

        /// <summary>A partition name that can go into a command without quoting.</summary>
        public static bool IsSafePartitionName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Length <= 64 && SafeName.IsMatch(name) && name != "." && name != "..";
        }

        /// <summary>A device directory made only of letters, digits and . _ - /.</summary>
        public static bool IsSafeDevicePath(string path)
        {
            return !string.IsNullOrEmpty(path) && SafePath.IsMatch(path) && !path.Contains("/../");
        }

        /// <summary>Quotes a string for the device shell: 'it'\''s' for it's.</summary>
        public static string ShellQuote(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\\''") + "'";
        }

        /// <summary>
        /// Quotes one argument for the Windows command line, following the rules
        /// CommandLineToArgvW and the C runtime use: backslashes are literal unless they come
        /// before a quote, and quotes are escaped with a backslash.
        /// </summary>
        public static string WindowsArgument(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return value;

            StringBuilder quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                }
                else
                {
                    quoted.Append('\\', backslashes);
                    quoted.Append(c);
                }
                backslashes = 0;
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        /// <summary>
        /// Wraps a device command so it runs with the given root access. With su the command
        /// is single-quoted once more, so it must not carry its own single quotes; root
        /// commands here only ever hold checked partition names and paths.
        /// </summary>
        public static string AsRoot(string command, RootAccess access)
        {
            if (access != RootAccess.Su)
                return command;
            if (command.IndexOf('\'') >= 0)
                throw new ArgumentException("a command run through su cannot contain single quotes", nameof(command));
            return "su -c '" + command + "'";
        }

        /// <summary>Arguments for "adb -s SERIAL shell COMMAND".</summary>
        public static string Shell(string serial, string deviceCommand)
        {
            return "-s " + WindowsArgument(serial) + " shell " + WindowsArgument(deviceCommand);
        }

        /// <summary>Arguments for "adb -s SERIAL exec-out COMMAND": raw, binary-safe output.</summary>
        public static string ExecOut(string serial, string deviceCommand)
        {
            return "-s " + WindowsArgument(serial) + " exec-out " + WindowsArgument(deviceCommand);
        }

        /// <summary>Arguments for "adb -s SERIAL push LOCAL REMOTE".</summary>
        public static string Push(string serial, string local, string remote)
        {
            return "-s " + WindowsArgument(serial) + " push " + WindowsArgument(local) + " " + WindowsArgument(remote);
        }

        /// <summary>Arguments for "adb -s SERIAL reboot TARGET" (bootloader, recovery, or "" for the system).</summary>
        public static string Reboot(string serial, string target)
        {
            return "-s " + WindowsArgument(serial) + " reboot" + (string.IsNullOrEmpty(target) ? "" : " " + target);
        }

        /// <summary>Arguments for "adb -s SERIAL pull -a REMOTE LOCAL".</summary>
        public static string Pull(string serial, string remote, string local)
        {
            return "-s " + WindowsArgument(serial) + " pull -a " + WindowsArgument(remote) + " " + WindowsArgument(local);
        }

        /// <summary>
        /// Finds the by-name directory and prints "DIR:path" then one "P:name:bytes" line per
        /// partition. blockdev is missing from some recoveries, so sysfs is the fallback.
        /// Kept free of quotes so it can be wrapped by AsRoot.
        /// </summary>
        public const string ListPartitionsScript =
            "for d in /dev/block/by-name /dev/block/bootdevice/by-name /dev/block/platform/*/by-name /dev/block/platform/*/*/by-name; do " +
            "if [ -d $d ]; then echo DIR:$d; cd $d; for p in *; do " +
            "s=$(blockdev --getsize64 $p 2>/dev/null); " +
            "if [ -z \"$s\" ]; then b=$(readlink -f $p); s=$(( $(cat /sys/class/block/${b##*/}/size 2>/dev/null || echo 0) * 512 )); fi; " +
            "echo P:$p:$s; done; break; fi; done";

        /// <summary>Streams one partition to stdout. Errors are dropped so they cannot end up in the image.</summary>
        public static string ReadPartitionCommand(string directory, string partition)
        {
            if (!IsSafeDevicePath(directory))
                throw new ArgumentException("unexpected partition directory: " + directory, nameof(directory));
            if (!IsSafePartitionName(partition))
                throw new ArgumentException("unexpected partition name: " + partition, nameof(partition));
            return "cat " + directory + "/" + partition + " 2>/dev/null";
        }

        /// <summary>
        /// The phone's own SHA-256 of a partition, to hold against the hash of the copy. The read
        /// above keeps errors out of the image's bytes, so a read that stops early looks like a
        /// shorter partition; when the listing gave no size to compare with, this is what tells.
        /// Its output goes through <see cref="ParseHash"/>.
        /// </summary>
        public static string HashPartitionCommand(string directory, string partition)
        {
            if (!IsSafeDevicePath(directory))
                throw new ArgumentException("unexpected partition directory: " + directory, nameof(directory));
            if (!IsSafePartitionName(partition))
                throw new ArgumentException("unexpected partition name: " + partition, nameof(partition));
            return "sha256sum " + directory + "/" + partition;
        }

        /// <summary>
        /// The hash in what sha256sum printed ("hash  /dev/block/by-name/boot_a"), lower case;
        /// null when it printed an error or nothing (no sha256sum, no permission, a read error).
        /// </summary>
        public static string ParseHash(string output)
        {
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 64 || (line.Length > 64 && line[64] != ' '))
                    continue;
                string hash = line.Substring(0, 64).ToLowerInvariant();
                bool hex = true;
                foreach (char c in hash)
                    hex &= (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (hex)
                    return hash;
            }
            return null;
        }

        /// <summary>
        /// Whether a partition's copy is whole. <paramref name="listedSize"/> is the size the
        /// listing gave (0 or less when it gave none), <paramref name="phoneHash"/> what
        /// <see cref="ParseHash"/> read (null when not asked or not answered). A size that
        /// differs or a hash that differs is a failed copy; with neither a size nor a hash to
        /// compare, nothing says the copy is whole, and that is a failure too.
        /// </summary>
        public static bool CopyIsWhole(long listedSize, long copiedBytes, string copiedHash, string phoneHash)
        {
            if (copiedBytes <= 0)
                return false;
            if (listedSize > 0 && copiedBytes != listedSize)
                return false;
            if (phoneHash != null)
                return string.Equals(phoneHash, copiedHash, StringComparison.OrdinalIgnoreCase);
            return listedSize > 0;
        }

        /// <summary>
        /// Lists a folder as "type|size|path" lines, hidden entries included. Runs as the
        /// shell user, so it needs no root and takes any path.
        /// </summary>
        public static string ListFolderCommand(string folder)
        {
            string quoted = ShellQuote(folder.TrimEnd('/').Length == 0 ? "/" : folder.TrimEnd('/'));
            return "stat -c %F\\|%s\\|%n -- " + quoted + "/* " + quoted + "/.* 2>/dev/null";
        }
    }
}
