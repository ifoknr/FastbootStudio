using System.Text;
using System.Text.RegularExpressions;

namespace FastbootEnhance.FakeAdb
{
    /// <summary>
    /// Pretends to be adb talking to one phone booted into a custom recovery, where adb runs
    /// as root. It answers the commands the Backup page sends: devices, id -u, the partition
    /// listing script, exec-out cat of a partition (deterministic bytes, so hashes can be
    /// checked), stat folder listings and pull. Every call is logged to fake-adb.log.
    ///
    /// A fake-adb.mode file next to the executable changes the phone it plays, read on every
    /// call: "none" (no phone on adb, as when it sits in fastboot), "android" (a booted, rooted
    /// system where adb runs as the shell user and root comes through su) or "recovery".
    /// </summary>
    static class Program
    {
        const string Serial = "FBE0SAMPLE02";
        const long MiB = 1 << 20;

        static readonly (string name, long size)[] Partitions =
        {
            ("boot_a", 16 * MiB), ("boot_b", 16 * MiB),
            ("init_boot_a", 8 * MiB), ("init_boot_b", 8 * MiB),
            ("vendor_boot_a", 16 * MiB), ("vendor_boot_b", 16 * MiB),
            ("dtbo_a", 4 * MiB), ("dtbo_b", 4 * MiB),
            ("vbmeta_a", 64 * 1024), ("vbmeta_b", 64 * 1024),
            ("vbmeta_system_a", 8 * 1024), ("vbmeta_system_b", 8 * 1024),
            ("modem_a", 32 * MiB), ("modem_b", 32 * MiB),
            ("persist", 16 * MiB), ("modemst1", 2 * MiB), ("modemst2", 2 * MiB),
            ("fsg", 2 * MiB), ("fsc", 128 * 1024), ("devinfo", 4096),
            ("misc", MiB), ("metadata", 16 * MiB), ("frp", 512 * 1024),
            ("super", 8L << 30), ("userdata", 110L << 30),
        };

        // A small phone storage: folder path -> (name, size or -1 for a folder).
        static readonly Dictionary<string, (string name, long size)[]> Tree = new Dictionary<string, (string, long)[]>
        {
            ["/sdcard"] = new[]
            {
                ("Alarms", -1L), ("Android", -1L), ("DCIM", -1L), ("Documents", -1L), ("Download", -1L),
                ("Music", -1L), ("Pictures", -1L), (".nomedia", 0L), ("notes.txt", 1234L),
            },
            ["/sdcard/DCIM"] = new[] { ("Camera", -1L), ("Screenshots", -1L) },
            ["/sdcard/DCIM/Camera"] = new[]
            {
                ("IMG_20260901_101500.jpg", 2516582L), ("IMG_20260902_183000.jpg", 3250585L),
                ("VID_20260903_120000.mp4", 48 * MiB),
            },
            ["/sdcard/DCIM/Screenshots"] = new[] { ("Screenshot_20260904.png", 412000L) },
            ["/sdcard/Documents"] = new[] { ("invoice 2026.pdf", 214000L), ("Ahmed's notes.txt", 4096L) },
            ["/sdcard/Download"] = new[] { ("platform-tools.zip", 8 * MiB) },
            ["/sdcard/Pictures"] = new[] { ("wallpaper.png", 1800000L) },
            ["/sdcard/Alarms"] = new (string, long)[0],
            ["/sdcard/Android"] = new (string, long)[0],
            ["/sdcard/Music"] = new (string, long)[0],
        };

        static readonly string Mode = ReadMode();

        static bool Android => Mode == "android";

        static string ReadMode()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "fake-adb.mode");
                if (File.Exists(path))
                    return File.ReadAllText(path).Trim();
            }
            catch (IOException)
            {
            }
            return "recovery";
        }

        static int Main(string[] args)
        {
            Log("[" + Mode + "] " + string.Join(" | ", args));

            List<string> rest = new List<string>(args);
            if (rest.Count >= 2 && rest[0] == "-s")
            {
                if (rest[1] != Serial || Mode == "none")
                {
                    Console.Error.WriteLine("adb: device '" + rest[1] + "' not found");
                    return 1;
                }
                rest.RemoveRange(0, 2);
            }

            string command = rest.Count > 0 ? rest[0] : "";
            string tail = string.Join(" ", rest.Skip(1));
            switch (command)
            {
                case "devices":
                    Console.Out.Write("List of devices attached\n");
                    if (Mode != "none")
                        Console.Out.Write(Serial + "\t" + (Android ? "device" : "recovery")
                            + " product:sample_a64 model:Sample_A64 device:sample transport_id:3\n");
                    Console.Out.Write("\n");
                    return 0;
                case "kill-server":
                case "start-server":
                    return 0;
                case "shell":
                    return Shell(tail);
                case "exec-out":
                    return ExecOut(tail);
                case "pull":
                    return Pull(rest.Skip(1).Where(a => a != "-a").ToList());
                case "push":
                    return Push(rest.Skip(1).ToList());
                case "reboot":
                    // The phone leaves Android for the bootloader: the fake fastboot lists it
                    // (as a second device, next to the one already sitting in fastboot).
                    if (tail == "bootloader")
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fake-adb.in-fastboot"), Serial);
                    return 0;
                default:
                    Console.Error.WriteLine("fake adb: unsupported command " + command);
                    return 1;
            }
        }

        static int Shell(string command)
        {
            if (command == "date +%s")
            {
                Console.Out.Write(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "\n");
                return 0;
            }
            if (command.StartsWith("stat -c %Y", StringComparison.Ordinal) && command.Contains("magisk_patched*.tar"))
                return ListPatchedTar();
            if (command.StartsWith("stat -c %Y", StringComparison.Ordinal))
                return ListPatched();

            // SamsungPhone.Command: a Galaxy S23 Ultra when fake-adb.samsung exists, the sample phone otherwise.
            if (command.Contains("getprop ro.boot.bootloader"))
            {
                if (!Android)
                    return 1;
                Console.Out.Write(File.Exists(Path.Combine(AppContext.BaseDirectory, "fake-adb.samsung"))
                    ? "maker=samsung\nmodel=SM-S918B\nbootloader=S918BXXU3BWJ1\npda=S918BXXU3BWJ1\nandroid=14\nsales=KSA\ncscver=S918BOXM3BWJ1\nwarranty=0\nwarranty2=\nlocked=1\nvbstate=green\n"
                    : "maker=Sample\nmodel=Sample_A64\nbootloader=unknown\npda=\nandroid=14\nsales=\ncscver=\n");
                return 0;
            }

            // In Android, adb runs as the shell user (uid 2000) and root comes from su.
            if (command == "id -u")
            {
                Console.Out.Write(Android ? "2000\n" : "0\n");
                return 0;
            }
            if (command == "su -c 'id -u'")
            {
                Console.Out.Write("0\n");
                return 0;
            }

            // PhoneReport.Command: the Phone page's read without root, backups on /sdcard included.
            if (command.Contains("soc_maker ro.soc.manufacturer"))
            {
                if (!Android)
                    return 1;
                Console.Out.Write(PhoneBasic());
                return 0;
            }
            // PhoneReport.CompanionCommand through su: "fbs info", "@@", "fbs modules".
            if (command.StartsWith("su -c '", StringComparison.Ordinal) && command.Contains("fastboot_studio_companion/bin/fbs") && command.Contains(" info"))
            {
                switch (CompanionMode())
                {
                    case "missing":
                        Console.Out.Write("fbs-missing\n");
                        return 0;
                    case "refused":
                        Console.Error.Write("Permission denied\n");
                        return 1;
                }
                Console.Out.Write(CompanionInfo + "\n@@\n" + CompanionModules + "\n");
                return 0;
            }

            // DeviceFacts.Command: what any app can ask without root.
            if (command.Contains("echo kernel=$(uname -r)"))
            {
                Console.Out.Write("kernel=5.10.198-android12-9-00001-gabcdef\npatch=2024-09-05\nvendor_patch=2024-09-05\n"
                    + "model=Sample_A64\nandroid=14\n");
                return 0;
            }
            if (command == "command -v su")
            {
                Console.Out.Write("/system/bin/su\n");
                return 0;
            }
            // The Fastboot Studio Companion module's report (DeviceFacts.CompanionCommand through su).
            if (command == "su -c 'sh /data/adb/modules/fastboot_studio_companion/bin/fbs facts'")
            {
                if (!Android)
                    return 127;
                Console.Out.Write("kernel=5.10.198-android12-9-00001-gabcdef\npatch=2024-09-05\nvendor_patch=2024-09-05\n"
                    + "model=Sample_A64\nandroid=14\ncompanion=2.4.0 beta\nroot=KernelSU\navb=orange\ndevice_state=unlocked\n"
                    + "spoofed=false\nselinux=Enforcing\nkmi=5.10-android12\nconflicts=1\n");
                return 0;
            }

            if (command.Contains("/dev/block/by-name") && command.Contains("blockdev"))
            {
                if (Android && !command.StartsWith("su -c '", StringComparison.Ordinal))
                    return 0;   // the shell user cannot see the sizes; the app must use su

                StringBuilder listing = new StringBuilder("DIR:/dev/block/by-name\n");
                foreach (var (name, size) in Partitions)
                    listing.Append("P:").Append(name).Append(':').Append(size).Append('\n');
                Console.Out.Write(listing.ToString());
                return 0;
            }

            if (command.StartsWith("stat -c", StringComparison.Ordinal))
            {
                string folder = FirstShellQuoted(command);
                if (folder == null || !Tree.TryGetValue(folder, out var children))
                    return 1;
                StringBuilder listing = new StringBuilder();
                listing.Append("directory|4096|").Append(folder).Append("/.\n");
                listing.Append("directory|4096|").Append(folder).Append("/..\n");
                foreach (var (name, size) in children)
                {
                    listing.Append(size < 0 ? "directory|4096|" : size == 0 ? "regular empty file|0|" : "regular file|" + size + "|")
                        .Append(folder).Append('/').Append(name).Append('\n');
                }
                Console.Out.Write(listing.ToString());
                return 0;
            }

            return 0;
        }

        /// <summary>Undoes AdbCommand.ShellQuote on the first quoted word: 'it'\''s' is it's.</summary>
        static string FirstShellQuoted(string command)
        {
            int start = command.IndexOf('\'');
            if (start < 0)
                return null;
            StringBuilder value = new StringBuilder();
            int i = start + 1;
            while (i < command.Length)
            {
                if (command[i] == '\'')
                {
                    if (string.CompareOrdinal(command, i, "'\\''", 0, 4) == 0)
                    {
                        value.Append('\'');
                        i += 4;
                        continue;
                    }
                    return value.ToString();
                }
                value.Append(command[i]);
                i++;
            }
            return null;
        }

        static int ExecOut(string command)
        {
            Match match = Regex.Match(command, @"cat /dev/block/by-name/([A-Za-z0-9._-]+)");
            if (!match.Success)
                return 1;
            if (Android && !command.StartsWith("su -c '", StringComparison.Ordinal))
                return 1;   // permission denied without root
            string name = match.Groups[1].Value;
            long size = Partitions.Where(p => p.name == name).Select(p => p.size).FirstOrDefault();
            if (size <= 0)
                return 0;

            // Deterministic content, different per partition, so a mix-up would show in the hashes.
            using (Stream output = Console.OpenStandardOutput())
            {
                byte[] block = new byte[MiB];
                uint state = 2166136261;
                foreach (char c in name)
                    state = (state ^ c) * 16777619;
                long left = size;
                while (left > 0)
                {
                    for (int i = 0; i < block.Length; i += 4)
                    {
                        state ^= state << 13;
                        state ^= state >> 17;
                        state ^= state << 5;
                        BitConverter.TryWriteBytes(new Span<byte>(block, i, 4), state);
                    }
                    int chunk = (int)Math.Min(block.Length, left);
                    output.Write(block, 0, chunk);
                    left -= chunk;
                }
            }
            return 0;
        }

        // The root app's side of the Root page: an image pushed to Download is "patched" at
        // once, and the patched image is whatever fake-adb.patched (next to the executable)
        // names, so the page finds, pulls and checks a real Magisk-patched sample.
        static readonly string PhoneDir = Path.Combine(AppContext.BaseDirectory, "fake-phone");
        const string PatchedRemote = "/sdcard/Download/magisk_patched-28100_FAKE.img";

        static string PatchedSample()
        {
            string pointer = Path.Combine(AppContext.BaseDirectory, "fake-adb.patched");
            return File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : null;
        }

        static int Push(List<string> paths)
        {
            if (paths.Count != 2 || !File.Exists(paths[0]))
            {
                Console.Error.WriteLine("adb: error: cannot stat '" + (paths.Count > 0 ? paths[0] : "") + "': No such file or directory");
                return 1;
            }
            Directory.CreateDirectory(PhoneDir);
            File.Copy(paths[0], Path.Combine(PhoneDir, paths[1].Substring(paths[1].LastIndexOf('/') + 1)), true);
            Console.Out.WriteLine(paths[0] + ": 1 file pushed, 0 skipped. 40.0 MB/s (" + new FileInfo(paths[0]).Length + " bytes in 0.100s)");
            return 0;
        }

        static int ListPatched()
        {
            string sample = PatchedSample();
            if (!Directory.Exists(PhoneDir) || sample == null || !File.Exists(sample))
                return 1; // no match for the glob
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            StringBuilder listing = new StringBuilder();
            foreach (string pushed in Directory.GetFiles(PhoneDir))
                listing.Append(now).Append('|').Append(new FileInfo(pushed).Length).Append("|/sdcard/Download/").Append(Path.GetFileName(pushed)).Append('\n');
            listing.Append(now).Append('|').Append(new FileInfo(sample).Length).Append('|').Append(PatchedRemote).Append('\n');
            Console.Out.Write(listing.ToString());
            return 0;
        }

        // The Samsung page's Magisk step: once a magisk_input tar is in Download, "Magisk" has
        // written magisk_patched-28100_FAKE.tar, the file fake-adb.patched-tar names.
        const string PatchedTarRemote = "/sdcard/Download/magisk_patched-28100_FAKE.tar";

        static string PatchedTarSample()
        {
            string pointer = Path.Combine(AppContext.BaseDirectory, "fake-adb.patched-tar");
            return File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : null;
        }

        static int ListPatchedTar()
        {
            string sample = PatchedTarSample();
            bool sent = Directory.Exists(PhoneDir) && Directory.GetFiles(PhoneDir, "magisk_input_*.tar").Length > 0;
            if (!sent || sample == null || !File.Exists(sample))
                return 1; // no match for the glob
            Console.Out.Write(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "|" + new FileInfo(sample).Length + "|" + PatchedTarRemote + "\n");
            return 0;
        }

        static int Pull(List<string> paths)
        {
            if (paths.Count == 2 && paths[0] == PatchedTarRemote && PatchedTarSample() != null)
            {
                string target = Directory.Exists(paths[1]) ? Path.Combine(paths[1], "magisk_patched-28100_FAKE.tar") : paths[1];
                File.Copy(PatchedTarSample(), target, true);
                Console.Out.WriteLine(PatchedTarRemote + ": 1 file pulled, 0 skipped. 40.0 MB/s");
                return 0;
            }
            if (paths.Count == 2 && paths[0] == PatchedRemote && PatchedSample() != null)
            {
                string target = Directory.Exists(paths[1]) ? Path.Combine(paths[1], "magisk_patched-28100_FAKE.img") : paths[1];
                File.Copy(PatchedSample(), target, true);
                Console.Out.WriteLine(PatchedRemote + ": 1 file pulled, 0 skipped. 40.0 MB/s");
                return 0;
            }
            if (paths.Count != 2)
            {
                Console.Error.WriteLine("adb: usage: pull [-a] REMOTE LOCAL");
                return 1;
            }
            if (paths[0].StartsWith(BackupsFolder + "/", StringComparison.Ordinal))
                return PullBackup(paths[0].Substring(BackupsFolder.Length + 1).TrimEnd('/'), paths[1]);
            string remote = paths[0].TrimEnd('/');
            string local = paths[1];
            string parent = remote.Substring(0, Math.Max(1, remote.LastIndexOf('/')));
            string name = remote.Substring(remote.LastIndexOf('/') + 1);

            int files = 0;
            long bytes = 0;
            if (Tree.ContainsKey(remote))
            {
                Copy(remote, Path.Combine(local, name), ref files, ref bytes);
            }
            else if (Tree.TryGetValue(parent, out var siblings) && siblings.Any(s => s.name == name && s.size >= 0))
            {
                long size = siblings.First(s => s.name == name).size;
                Directory.CreateDirectory(local);
                WriteFile(Path.Combine(local, name), size);
                files = 1;
                bytes = size;
            }
            else
            {
                Console.Error.WriteLine("adb: error: failed to stat remote object '" + remote + "': No such file or directory");
                return 1;
            }

            Console.Out.WriteLine(remote + ": " + files + " file" + (files == 1 ? "" : "s") + " pulled, 0 skipped. 40.0 MB/s ("
                + bytes + " bytes in 0.100s)");
            return 0;
        }

        static void Copy(string remote, string local, ref int files, ref long bytes)
        {
            Directory.CreateDirectory(local);
            foreach (var (name, size) in Tree[remote])
            {
                if (size < 0)
                {
                    Copy(remote + "/" + name, Path.Combine(local, name), ref files, ref bytes);
                }
                else
                {
                    WriteFile(Path.Combine(local, name), size);
                    files++;
                    bytes += size;
                }
            }
        }

        static void WriteFile(string path, long size)
        {
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))
                file.SetLength(size);
        }

        // ---------------------------------------------------------------- the Phone page

        const string BackupsFolder = "/sdcard/FastbootStudio/Backups";

        // Three backups the Companion made, as on the mockup: full, critical and boot.
        static readonly (string name, long kb, string[] images)[] Backups =
        {
            ("Sample_A64_FBE0SAMPLE02_20261010-184012", 10066329, new[]
            {
                "boot_a", "init_boot_a", "vendor_boot_a", "dtbo_a", "vbmeta_a", "vbmeta_system_a", "modem_a", "persist",
                "modemst1", "modemst2", "fsg", "fsc", "devinfo", "misc", "metadata", "frp", "super", "abl_a", "xbl_a",
                "tz_a", "aop_a", "keymaster_a",
            }),
            ("Sample_A64_FBE0SAMPLE02_20260921-184012", 352256, new[]
            {
                "persist", "modemst1", "modemst2", "fsg", "fsc", "devinfo", "frp", "abl_a", "xbl_a", "tz_a", "modem_a",
            }),
            ("Sample_A64_FBE0SAMPLE02_20260830-091233", 114688, new[] { "boot_a", "init_boot_a", "vendor_boot_a", "vbmeta_a" }),
        };

        /// <summary>"companion" (default), "missing" or "refused", from fake-adb.companion next to the executable.</summary>
        static string CompanionMode()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fake-adb.companion");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "companion";
        }

        static string PhoneBasic()
        {
            StringBuilder text = new StringBuilder(
                "model=Sample_A64\nbrand=Sample\ndevice=sample\nandroid=14\nsdk=34\npatch=2024-09-05\n" +
                "platform=taro\nhardware=qcom\nsoc_maker=QTI\nsoc_model=SM8450\narch=arm64-v8a\n" +
                "slot=_a\ndynamic=true\navb=green\ndevice_state=locked\nlocked=1\n" +
                "kernel=5.10.198-android12-9-00001-gabcdef\nselinux=Enforcing\n" +
                "battery_capacity=78\nbattery_cycle_count=312\nbattery_charge_full=4550000\nbattery_charge_full_design=5000000\n");
            foreach (var (name, kb, images) in Backups)
                text.Append("backup=").Append(name).Append('|').Append(images.Length).Append('|').Append(kb).Append("|SHA256SUMS backup-info.txt ")
                    .Append(string.Join(" ", images.Select(i => i + ".img"))).Append(" \n");
            return text.ToString();
        }

        // A hiding module set ro.boot.verifiedbootstate to green while the bootloader said orange.
        const string CompanionInfo =
            "{\"fbs\":\"2.4.0\",\"channel\":\"beta\",\"model\":\"Sample_A64\",\"brand\":\"Sample\",\"device\":\"sample\",\"product\":\"sample_a64\"," +
            "\"android\":\"14\",\"sdk\":\"34\",\"patch\":\"2024-09-05\",\"build\":\"SAMPLE-14.0.1\",\"platform\":\"taro\",\"soc\":\"qualcomm\"," +
            "\"arch\":\"arm64-v8a\",\"slot\":\"_a\",\"dynamic\":true,\"treble\":true,\"vndk\":\"34\"," +
            "\"boot\":{\"vbstate\":\"orange\",\"device_state\":\"unlocked\",\"locked\":false,\"spoofed\":true}," +
            "\"kernel\":\"5.10.198-android12-9-00001-gabcdef\",\"kmi\":\"5.10-android12\",\"selinux\":\"Enforcing\",\"root\":\"KernelSU\"," +
            "\"boot_reason\":\"reboot\",\"battery\":{\"level\":78,\"health\":91,\"cycles\":312,\"temp_dc\":334}," +
            "\"memory\":{\"total_kb\":7864320,\"avail_kb\":2725000},\"storage\":{\"type\":\"ufs\",\"life_a\":\"0x02\",\"life_b\":\"0x01\",\"eol\":\"0x01\"}}";

        const string CompanionModules =
            "{\"root\":\"/data/adb/modules\",\"modules\":[" +
            "{\"id\":\"adblock\",\"name\":\"AdBlock Plus\",\"version\":\"v3.1\",\"enabled\":true}," +
            "{\"id\":\"hosts\",\"name\":\"Systemless Hosts\",\"version\":\"v1.0\",\"enabled\":true}," +
            "{\"id\":\"debloater\",\"name\":\"Debloater\",\"version\":\"v20.1\",\"enabled\":true}," +
            "{\"id\":\"youtube\",\"name\":\"YouTube Patched\",\"version\":\"19.16\",\"enabled\":true}," +
            "{\"id\":\"gpu\",\"name\":\"GPU Tweaks\",\"version\":\"v2\",\"enabled\":true}," +
            "{\"id\":\"fastboot_studio_companion\",\"name\":\"Fastboot Studio Companion (Beta)\",\"version\":\"v2.4.0\",\"enabled\":true}]," +
            "\"conflicts\":[" +
            "{\"kind\":\"file\",\"modules\":[\"adblock\",\"hosts\"],\"count\":1,\"paths\":[\"/system/etc/hosts\"]}," +
            "{\"kind\":\"replace\",\"path\":\"/system/app/YouTube\",\"by\":\"debloater\",\"modules\":[\"youtube\"]}," +
            "{\"kind\":\"prop\",\"key\":\"debug.hwui.renderer\",\"values\":[{\"module\":\"gpu\",\"value\":\"skiavk\"},{\"module\":\"debloater\",\"value\":\"skiagl\"}]}]}";

        /// <summary>Writes a backup folder as the Companion leaves it: small images, SHA256SUMS and backup-info.txt.</summary>
        static int PullBackup(string name, string local)
        {
            var backup = Backups.FirstOrDefault(b => b.name == name);
            if (backup.name == null)
            {
                Console.Error.WriteLine("adb: error: failed to stat remote object '" + BackupsFolder + "/" + name + "': No such file or directory");
                return 1;
            }
            string folder = Path.Combine(local, name);
            Directory.CreateDirectory(folder);
            StringBuilder sums = new StringBuilder();
            StringBuilder info = new StringBuilder("Fastboot Studio backup\ndate\t2026-10-10 18:40:12 +03:00\nserial\t" + Serial
                + "\nmodel\tSample_A64\nproduct\tsample_a64\nmode\tcompanion\nsource\t/dev/block/by-name\n\npartition\tbytes\tsha256\n");
            long bytes = 0;
            foreach (string image in backup.images)
            {
                byte[] content = new byte[64 * 1024];
                uint state = 2166136261;
                foreach (char c in image)
                    state = (state ^ c) * 16777619;
                for (int i = 0; i < content.Length; i += 4)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    BitConverter.TryWriteBytes(new Span<byte>(content, i, 4), state);
                }
                File.WriteAllBytes(Path.Combine(folder, image + ".img"), content);
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
                sums.Append(hash).Append("  ").Append(image).Append(".img\n");
                info.Append(image).Append('\t').Append(content.Length).Append('\t').Append(hash).Append('\n');
                bytes += content.Length;
            }
            File.WriteAllText(Path.Combine(folder, "SHA256SUMS"), sums.ToString());
            File.WriteAllText(Path.Combine(folder, "backup-info.txt"), info.ToString());
            Console.Out.WriteLine(BackupsFolder + "/" + name + "/: " + (backup.images.Length + 2) + " files pulled, 0 skipped. 40.0 MB/s ("
                + bytes + " bytes in 0.100s)");
            return 0;
        }

        /// <summary>
        /// Appends a line to fake-adb.log. The app runs several copies at once (a device poller next to a
        /// transfer), and CI counts these lines, so a write that finds the file in use waits
        /// and tries again rather than dropping its line.
        /// </summary>
        static void Log(string line)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fake-adb.log");
            string text = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine;
            for (int attempt = 0; attempt < 200; attempt++)
            {
                try
                {
                    File.AppendAllText(path, text);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(10);
                }
            }
        }
    }
}
