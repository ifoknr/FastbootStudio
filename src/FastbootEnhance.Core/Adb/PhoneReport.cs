using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using FastbootEnhance.Core.Fastboot;
using FastbootEnhance.Core.Images;

namespace FastbootEnhance.Core.Adb
{
    /// <summary>What the Phone page could learn from the Fastboot Studio Companion module.</summary>
    public enum CompanionState
    {
        /// <summary>The phone has no su, so the module (under /data/adb) cannot be reached.</summary>
        NoRoot,

        /// <summary>su said no, or nobody answered its prompt in time.</summary>
        Refused,

        /// <summary>Root works, and the module is not installed.</summary>
        NotInstalled,

        Ready,
    }

    /// <summary>Which partitions a backup holds, guessed from its file names.</summary>
    public enum BackupSet
    {
        /// <summary>Boot images only: boot, init_boot, vendor_boot, dtbo, vbmeta, recovery.</summary>
        Boot,

        /// <summary>The partitions that can brick the phone or lose its identity (PartitionSafety).</summary>
        Critical,

        /// <summary>Everything else as well.</summary>
        Full,
    }

    public enum ConflictKind
    {
        /// <summary>Two or more modules put the same file in place; only one copy is used.</summary>
        File,

        /// <summary>A module replaces a whole folder, hiding what other modules add there.</summary>
        Replace,

        /// <summary>Modules set the same system property to different values; the last one wins.</summary>
        Prop,
    }

    /// <summary>One conflict between root modules, as "fbs modules" reports it.</summary>
    public sealed class ModuleConflict
    {
        public ConflictKind Kind { get; internal set; }

        /// <summary>The modules involved, by name (their id when they have no name).</summary>
        public List<string> Modules { get; } = new List<string>();

        /// <summary>File: the first shared paths. Replace: the replaced folder.</summary>
        public List<string> Paths { get; } = new List<string>();

        /// <summary>File: how many paths are shared in all (Paths holds at most 12).</summary>
        public int Count { get; internal set; }

        /// <summary>Replace: the module that replaces the folder.</summary>
        public string By { get; internal set; }

        /// <summary>Prop: the property.</summary>
        public string Key { get; internal set; }

        /// <summary>Prop: each module's value, in the module's name.</summary>
        public List<KeyValuePair<string, string>> Values { get; } = new List<KeyValuePair<string, string>>();
    }

    /// <summary>A backup the Companion made, in /sdcard/FastbootStudio/Backups.</summary>
    public sealed class PhoneBackup
    {
        /// <summary>The folder name, the same as the Backup page uses: model_serial_yyyyMMdd-HHmmss.</summary>
        public string Name { get; internal set; }

        /// <summary>Lines in SHA256SUMS: the images that were saved.</summary>
        public int Files { get; internal set; }

        public long? Kb { get; internal set; }

        /// <summary>From the end of the name, in the phone's local time.</summary>
        public DateTime? Taken { get; internal set; }

        public List<string> Images { get; } = new List<string>();

        public BackupSet Set { get; internal set; }

        public string Path => PhoneReport.BackupsFolder + "/" + Name;
    }

    /// <summary>
    /// The phone as the Phone page shows it. A first adb shell command needs no root and reads
    /// the properties, the kernel, the battery and the backups folder; the Companion module,
    /// reached through su, adds the rest (the bootloader's own AVB state, the root manager,
    /// storage wear and the conflicts between modules).
    /// </summary>
    public sealed class PhoneReport
    {
        public const string BackupsFolder = "/sdcard/FastbootStudio/Backups";
        public const string CompanionPath = "/data/adb/modules/fastboot_studio_companion/bin/fbs";

        /// <summary>key=value lines, then one "backup=name|files|kb|image image …" line per backup.</summary>
        public const string Command =
            "g() { echo \"$1=$(getprop $2)\"; }; " +
            "g model ro.product.model; g brand ro.product.brand; g device ro.product.device; " +
            "g android ro.build.version.release; g sdk ro.build.version.sdk; g patch ro.build.version.security_patch; " +
            "g platform ro.board.platform; g hardware ro.hardware; g soc_maker ro.soc.manufacturer; g soc_model ro.soc.model; " +
            "g arch ro.product.cpu.abi; g slot ro.boot.slot_suffix; g dynamic ro.boot.dynamic_partitions; " +
            "g avb ro.boot.verifiedbootstate; g device_state ro.boot.vbmeta.device_state; g locked ro.boot.flash.locked; " +
            "echo \"kernel=$(uname -r)\"; echo \"selinux=$(getenforce 2>/dev/null)\"; " +
            "b=/sys/class/power_supply/battery; " +
            "for k in capacity cycle_count charge_full charge_full_design; do echo \"battery_$k=$(cat $b/$k 2>/dev/null)\"; done; " +
            "for d in " + BackupsFolder + "/*/; do [ -d \"$d\" ] || continue; n=${d%/}; n=${n##*/}; " +
            "f=$(cat \"${d}SHA256SUMS\" 2>/dev/null | wc -l); k=$(du -sk \"$d\" 2>/dev/null); " +
            "echo \"backup=$n|$f|${k%%[!0-9]*}|$(ls \"$d\" | tr '\\n' ' ')\"; done";

        /// <summary>
        /// Through su: the Companion's info and modules reports, separated by a line "@@", or
        /// "fbs-missing" without the module. No single quotes, so AdbCommand.AsRoot can wrap it.
        /// </summary>
        public const string CompanionCommand =
            "f=" + CompanionPath + "; if [ -f $f ]; then sh $f info; echo @@; sh $f modules; else echo fbs-missing; fi";

        public const string CompanionMissing = "fbs-missing";

        // ---------------------------------------------------------------- the phone

        public string Model { get; private set; }
        public string Brand { get; private set; }
        public string Device { get; private set; }
        public string Android { get; private set; }
        public string Sdk { get; private set; }
        public string Patch { get; private set; }
        public string Platform { get; private set; }

        /// <summary>MediaTek, Qualcomm, Unisoc, Samsung, Google, or null.</summary>
        public string Soc { get; private set; }

        public string Arch { get; private set; }

        /// <summary>"_a" or "_b"; null on a phone without A/B slots.</summary>
        public string Slot { get; private set; }

        public bool? Dynamic { get; private set; }

        // ---------------------------------------------------------------- kernel and security

        public string Kernel { get; private set; }
        public string Kmi { get; private set; }
        public string Selinux { get; private set; }

        /// <summary>green, yellow or orange. From the Companion, it is the bootloader's own word.</summary>
        public string Avb { get; private set; }

        /// <summary>locked or unlocked.</summary>
        public string DeviceState { get; private set; }

        /// <summary>The boot properties (ro.boot.*) say something else than the bootloader or root do.</summary>
        public bool Spoofed { get; private set; }

        /// <summary>KernelSU, Magisk or APatch, from the Companion.</summary>
        public string Root { get; private set; }

        // ---------------------------------------------------------------- health

        public int? BatteryLevel { get; private set; }

        /// <summary>Full charge now against the design capacity, in percent.</summary>
        public int? BatteryHealth { get; private set; }

        public int? BatteryCycles { get; private set; }

        /// <summary>ufs or emmc, from the Companion.</summary>
        public string StorageType { get; private set; }

        /// <summary>The worst of the two wear estimates, 1 (0-10% used) to 11 (past its rated life).</summary>
        public int? StorageWear { get; private set; }

        /// <summary>Pre-EOL: 1 normal, 2 warning (spare blocks running low), 3 urgent.</summary>
        public int? StorageEol { get; private set; }

        // ---------------------------------------------------------------- the Companion

        public CompanionState Companion { get; private set; } = CompanionState.NoRoot;

        /// <summary>"2.4.0".</summary>
        public string CompanionVersion { get; private set; }

        /// <summary>beta or stable.</summary>
        public string CompanionChannel { get; private set; }

        /// <summary>Installed modules, enabled or not; null when the Companion was not read.</summary>
        public int? ModuleCount { get; private set; }

        public List<ModuleConflict> Conflicts { get; } = new List<ModuleConflict>();

        public List<PhoneBackup> Backups { get; } = new List<PhoneBackup>();

        public long BackupsKb => Backups.Sum(b => b.Kb ?? 0);

        /// <summary>Reads the output of <see cref="Command"/>.</summary>
        public static PhoneReport Parse(string output)
        {
            PhoneReport report = new PhoneReport();
            Dictionary<string, string> values = new Dictionary<string, string>();
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key == "backup")
                {
                    PhoneBackup backup = ParseBackup(value);
                    if (backup != null)
                        report.Backups.Add(backup);
                }
                else if (value.Length > 0)
                {
                    values[key] = value;
                }
            }

            report.Model = Get(values, "model");
            report.Brand = Get(values, "brand");
            report.Device = Get(values, "device");
            report.Android = Get(values, "android");
            report.Sdk = Get(values, "sdk");
            report.Patch = Get(values, "patch");
            report.Platform = Get(values, "platform");
            report.Soc = SocVendor(Get(values, "soc_maker"), Get(values, "platform"), Get(values, "hardware"));
            report.Arch = Get(values, "arch");
            report.Slot = Get(values, "slot");
            report.Dynamic = Bool(Get(values, "dynamic"));
            report.Avb = Get(values, "avb");
            report.DeviceState = Get(values, "device_state");
            if (report.DeviceState == null)
            {
                bool? locked = Bool(Get(values, "locked"));
                if (locked != null)
                    report.DeviceState = locked.Value ? "locked" : "unlocked";
            }
            report.SetKernel(Get(values, "kernel"));
            report.Selinux = Get(values, "selinux");
            report.BatteryLevel = Int(Get(values, "battery_capacity"));
            report.BatteryCycles = Int(Get(values, "battery_cycle_count"));
            report.BatteryHealth = Health(Long(Get(values, "battery_charge_full")), Long(Get(values, "battery_charge_full_design")));

            report.Backups.Sort((x, y) => Nullable.Compare(y.Taken, x.Taken));
            return report;
        }

        /// <summary>
        /// Reads the output of <see cref="CompanionCommand"/>. hasSu is whether the phone has su
        /// at all; without it the command is not run and output is ignored.
        /// </summary>
        public void ApplyCompanion(bool hasSu, string output)
        {
            if (!hasSu)
            {
                Companion = CompanionState.NoRoot;
                return;
            }
            output = output ?? "";
            if (output.Split('\n').Any(l => l.Trim() == CompanionMissing))
            {
                Companion = CompanionState.NotInstalled;
                RootMeansUnlocked();
                return;
            }

            int split = output.IndexOf("\n@@", StringComparison.Ordinal);
            string info = split >= 0 ? output.Substring(0, split) : output;
            string modules = split >= 0 ? output.Substring(split + 3) : null;
            try
            {
                int start = info.IndexOf("{\"fbs\"", StringComparison.Ordinal);
                if (start < 0)
                {
                    Companion = CompanionState.Refused;
                    RootMeansUnlocked();
                    return;
                }
                using (JsonDocument doc = JsonDocument.Parse(info.Substring(start).Trim()))
                    ApplyInfo(doc.RootElement);
                Companion = CompanionState.Ready;
                RootMeansUnlocked();
            }
            catch (JsonException)
            {
                Companion = CompanionState.Refused;
                RootMeansUnlocked();
                return;
            }

            if (modules == null)
                return;
            try
            {
                int start = modules.IndexOf('{');
                if (start >= 0)
                {
                    using (JsonDocument doc = JsonDocument.Parse(modules.Substring(start).Trim()))
                        ApplyModules(doc.RootElement);
                }
            }
            catch (JsonException)
            {
                // A module list the app cannot read leaves the rest of the report as it is.
            }
        }

        /// <summary>
        /// su runs on the phone, so it has root, which needs a modified boot image that a locked
        /// bootloader does not start: the bootloader is unlocked whatever the properties say.
        /// ROMs and hiding modules set ro.boot.* to "locked" and "green" for Play Integrity, and
        /// older bootloaders (Qualcomm msm89xx and the like) give the Companion nothing better.
        /// </summary>
        void RootMeansUnlocked()
        {
            if (DeviceState == "unlocked")
                return;
            if (DeviceState == "locked" || Avb == "green")
                Spoofed = true;
            DeviceState = "unlocked";
            if (Avb == "green")
                Avb = "orange";
        }

        void ApplyInfo(JsonElement info)
        {
            CompanionVersion = Str(info, "fbs");
            CompanionChannel = Str(info, "channel");
            Model = Str(info, "model") ?? Model;
            Brand = Str(info, "brand") ?? Brand;
            Device = Str(info, "device") ?? Device;
            Android = Str(info, "android") ?? Android;
            Sdk = Str(info, "sdk") ?? Sdk;
            Patch = Str(info, "patch") ?? Patch;
            Platform = Str(info, "platform") ?? Platform;
            string soc = Str(info, "soc");
            if (soc != null && soc != "unknown")
                Soc = SocName(soc);
            Arch = Str(info, "arch") ?? Arch;
            Slot = Str(info, "slot") ?? Slot;
            Dynamic = BoolOf(info, "dynamic") ?? Dynamic;
            // The KMI comes from the kernel, written as the rest of the app does ("android13-5.15").
            SetKernel(Str(info, "kernel") ?? Kernel);
            Selinux = Str(info, "selinux") ?? Selinux;
            string root = Str(info, "root");
            Root = root == "unknown" ? null : root;

            JsonElement boot;
            if (info.TryGetProperty("boot", out boot) && boot.ValueKind == JsonValueKind.Object)
            {
                // The bootloader's own words: a hiding module may have changed the properties.
                Avb = Str(boot, "vbstate") ?? Avb;
                string state = Str(boot, "device_state");
                bool? locked = BoolOf(boot, "locked");
                if (state != null)
                    DeviceState = state;
                else if (locked != null)
                    DeviceState = locked.Value ? "locked" : "unlocked";
                Spoofed = BoolOf(boot, "spoofed") ?? false;
            }

            JsonElement battery;
            if (info.TryGetProperty("battery", out battery) && battery.ValueKind == JsonValueKind.Object)
            {
                BatteryLevel = IntOf(battery, "level") ?? BatteryLevel;
                BatteryHealth = IntOf(battery, "health") ?? BatteryHealth;
                BatteryCycles = IntOf(battery, "cycles") ?? BatteryCycles;
            }

            JsonElement storage;
            if (info.TryGetProperty("storage", out storage) && storage.ValueKind == JsonValueKind.Object)
            {
                StorageType = Str(storage, "type");
                int? a = Hex(Str(storage, "life_a")), b = Hex(Str(storage, "life_b"));
                // 0 means "not defined"; the worse of the two is what matters.
                int? wear = new[] { a, b }.Where(v => v > 0).Max();
                StorageWear = wear;
                int? eol = Hex(Str(storage, "eol"));
                StorageEol = eol > 0 ? eol : null;
            }
        }

        void ApplyModules(JsonElement root)
        {
            Dictionary<string, string> names = new Dictionary<string, string>();
            JsonElement modules;
            if (root.TryGetProperty("modules", out modules) && modules.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement module in modules.EnumerateArray())
                {
                    string id = Str(module, "id");
                    if (id != null)
                        names[id] = Str(module, "name") ?? id;
                }
                ModuleCount = names.Count;
            }
            string Name(string id) => id != null && names.TryGetValue(id, out string name) ? name : id;

            JsonElement conflicts;
            if (!root.TryGetProperty("conflicts", out conflicts) || conflicts.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement item in conflicts.EnumerateArray())
            {
                ModuleConflict conflict = new ModuleConflict();
                switch (Str(item, "kind"))
                {
                    case "file":
                        conflict.Kind = ConflictKind.File;
                        conflict.Modules.AddRange(Strings(item, "modules").Select(Name));
                        conflict.Paths.AddRange(Strings(item, "paths"));
                        conflict.Count = IntOf(item, "count") ?? conflict.Paths.Count;
                        break;
                    case "replace":
                        conflict.Kind = ConflictKind.Replace;
                        conflict.By = Name(Str(item, "by"));
                        conflict.Modules.AddRange(Strings(item, "modules").Select(Name));
                        string path = Str(item, "path");
                        if (path != null)
                            conflict.Paths.Add(path);
                        conflict.Count = conflict.Paths.Count;
                        break;
                    case "prop":
                        conflict.Kind = ConflictKind.Prop;
                        conflict.Key = Str(item, "key");
                        JsonElement values;
                        if (item.TryGetProperty("values", out values) && values.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement value in values.EnumerateArray())
                            {
                                string module = Name(Str(value, "module"));
                                conflict.Values.Add(new KeyValuePair<string, string>(module, Str(value, "value") ?? ""));
                                if (module != null && !conflict.Modules.Contains(module))
                                    conflict.Modules.Add(module);
                            }
                        }
                        break;
                    default:
                        continue;
                }
                Conflicts.Add(conflict);
            }
        }

        void SetKernel(string release)
        {
            Kernel = release;
            Kmi = release != null ? KernelInfo.FromRelease(release)?.Kmi : null;
        }

        // ---------------------------------------------------------------- backups

        /// <summary>"name|files|kb|boot_a.img SHA256SUMS backup-info.txt …".</summary>
        static PhoneBackup ParseBackup(string value)
        {
            string[] parts = value.Split(new[] { '|' }, 4);
            if (parts.Length < 3 || parts[0].Length == 0 || parts[0].IndexOf('/') >= 0 || parts[0].StartsWith(".", StringComparison.Ordinal))
                return null;
            PhoneBackup backup = new PhoneBackup
            {
                Name = parts[0],
                Files = Int(parts[1]) ?? 0,
                Kb = Long(parts[2]),
                Taken = DateFromName(parts[0]),
            };
            if (parts.Length > 3)
            {
                foreach (string file in parts[3].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (file.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                        backup.Images.Add(file.Substring(0, file.Length - 4));
                }
            }
            backup.Set = SetOf(backup.Images);
            return backup;
        }

        static readonly HashSet<string> BootSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "boot", "init_boot", "vendor_boot", "vendor_kernel_boot", "dtbo", "recovery", "vbmeta", "vbmeta_system", "vbmeta_vendor",
        };

        /// <summary>The Companion's three sets: boot images, the critical partitions, or everything.</summary>
        public static BackupSet SetOf(IEnumerable<string> partitions)
        {
            bool critical = false, any = false;
            foreach (string name in partitions)
            {
                any = true;
                if (BootSet.Contains(PartitionSafety.BaseName(name)))
                    continue;
                if (PartitionSafety.IsBrickRisk(name))
                    critical = true;
                else
                    return BackupSet.Full;
            }
            return !any ? BackupSet.Full : critical ? BackupSet.Critical : BackupSet.Boot;
        }

        /// <summary>The date at the end of "model_serial_yyyyMMdd-HHmmss".</summary>
        public static DateTime? DateFromName(string name)
        {
            int underscore = name?.LastIndexOf('_') ?? -1;
            DateTime taken;
            if (underscore >= 0 && DateTime.TryParseExact(name.Substring(underscore + 1), "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out taken))
                return taken;
            return null;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>The chip maker from ro.soc.manufacturer, or guessed from the platform name.</summary>
        public static string SocVendor(string maker, string platform, string hardware)
        {
            string m = (maker ?? "").ToLowerInvariant();
            if (m.StartsWith("mediatek", StringComparison.Ordinal) || m.StartsWith("mtk", StringComparison.Ordinal)) return "MediaTek";
            if (m.StartsWith("qti", StringComparison.Ordinal) || m.StartsWith("qualcomm", StringComparison.Ordinal)) return "Qualcomm";
            if (m.StartsWith("spreadtrum", StringComparison.Ordinal) || m.StartsWith("unisoc", StringComparison.Ordinal)) return "Unisoc";
            if (m.StartsWith("samsung", StringComparison.Ordinal)) return "Samsung";
            if (m.StartsWith("google", StringComparison.Ordinal)) return "Google";
            foreach (string raw in new[] { platform, hardware })
            {
                string p = (raw ?? "").ToLowerInvariant();
                if (p.Length == 0)
                    continue;
                if (p.StartsWith("mt", StringComparison.Ordinal) && p.Length > 2 && char.IsDigit(p[2])) return "MediaTek";
                if (p.StartsWith("msm", StringComparison.Ordinal) || p.StartsWith("sdm", StringComparison.Ordinal)
                    || (p.StartsWith("sm", StringComparison.Ordinal) && p.Length > 2 && char.IsDigit(p[2])) || QualcommCodenames.Contains(p))
                    return "Qualcomm";
                if (p.StartsWith("ums", StringComparison.Ordinal) || p.StartsWith("sp9", StringComparison.Ordinal) || p.StartsWith("s9863", StringComparison.Ordinal))
                    return "Unisoc";
                if (p.StartsWith("exynos", StringComparison.Ordinal) || p.StartsWith("universal", StringComparison.Ordinal) || p.StartsWith("s5e", StringComparison.Ordinal))
                    return "Samsung";
                if ((p.StartsWith("gs", StringComparison.Ordinal) && p.Length > 2 && char.IsDigit(p[2])) || p.StartsWith("zuma", StringComparison.Ordinal))
                    return "Google";
            }
            return null;
        }

        static readonly HashSet<string> QualcommCodenames = new HashSet<string>
        {
            "qcom", "lahaina", "kona", "taro", "kalama", "pineapple", "sun", "holi", "bengal", "lito", "trinket",
            "atoll", "parrot", "crow", "blair", "monaco", "niobe", "cliffs", "volcano", "pitti",
        };

        static string SocName(string companion)
        {
            switch (companion)
            {
                case "mediatek": return "MediaTek";
                case "qualcomm": return "Qualcomm";
                case "unisoc": return "Unisoc";
                case "samsung": return "Samsung";
                case "google": return "Google";
                default: return companion;
            }
        }

        static int? Health(long? full, long? design)
        {
            if (full == null || design == null || design <= 0 || full <= 0)
                return null;
            return (int)(full.Value * 100 / design.Value);
        }

        static string Get(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : null;
        }

        static int? Int(string value)
        {
            int parsed;
            return int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : (int?)null;
        }

        static long? Long(string value)
        {
            long parsed;
            return long.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : (long?)null;
        }

        static int? Hex(string value)
        {
            value = (value ?? "").Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(2);
            int parsed;
            return value.Length > 0 && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed) ? parsed : (int?)null;
        }

        static bool? Bool(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": return true;
                case "0": case "false": case "no": return false;
                default: return null;
            }
        }

        static string Str(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value))
                return null;
            string text = value.ValueKind == JsonValueKind.String ? value.GetString()
                : value.ValueKind == JsonValueKind.Number ? value.GetRawText()
                : null;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        static int? IntOf(JsonElement element, string name)
        {
            JsonElement value;
            int parsed;
            if (!element.TryGetProperty(name, out value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out parsed))
                return parsed;
            return value.ValueKind == JsonValueKind.String ? Int(value.GetString()) : null;
        }

        static bool? BoolOf(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value))
                return null;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            return value.ValueKind == JsonValueKind.String ? Bool(value.GetString()) : null;
        }

        static IEnumerable<string> Strings(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Array)
                yield break;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()))
                    yield return item.GetString();
            }
        }
    }
}
