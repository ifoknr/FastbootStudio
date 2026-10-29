using System.Diagnostics;
using FastbootEnhance.Core.Adb;

namespace FastbootEnhance.Core.Tests
{
    public class PhoneReportTests
    {
        // What PhoneReport.Command prints on a MediaTek phone with two Companion backups.
        const string Basic =
            "model=Demo Phone\nbrand=Demo\ndevice=demo\nandroid=14\nsdk=34\npatch=2026-08-05\n" +
            "platform=mt6789\nhardware=mt6789\nsoc_maker=Mediatek\nsoc_model=MT6789\narch=arm64-v8a\n" +
            "slot=_a\ndynamic=true\navb=green\ndevice_state=locked\nlocked=0\n" +
            "kernel=5.15.148-android13-8-00017-gabc123\nselinux=Enforcing\n" +
            "battery_capacity=78\nbattery_cycle_count=312\nbattery_charge_full=4550000\nbattery_charge_full_design=5000000\n" +
            "backup=Demo_Phone_DEMO0123456789_20260830-091233|4|114688|SHA256SUMS backup-info.txt boot_a.img init_boot_a.img vbmeta_a.img dtbo_a.img \n" +
            "backup=Demo_Phone_DEMO0123456789_20261010-184012|22|10066329|SHA256SUMS backup-info.txt boot_a.img super.img nvram.img \n" +
            "backup=Demo_Phone_DEMO0123456789_20260921-184012|11|352256|SHA256SUMS boot_a.img nvram.img seccfg.img persist.img \n";

        // "fbs info", "@@" and "fbs modules" from the Companion's own test phone (tests/fixture.sh):
        // a hiding module set ro.boot.verifiedbootstate to green while the bootloader said orange.
        const string Companion =
            "{\"fbs\":\"2.4.0\",\"channel\":\"beta\",\"model\":\"Demo Phone\",\"brand\":\"Demo\",\"device\":\"demo\",\"product\":\"demo_mt6789\"," +
            "\"android\":\"14\",\"sdk\":\"34\",\"patch\":\"2026-08-05\",\"build\":\"DEMO-14.0.1\",\"platform\":\"mt6789\",\"soc\":\"mediatek\"," +
            "\"arch\":\"arm64-v8a\",\"slot\":\"_a\",\"dynamic\":true,\"treble\":true,\"vndk\":\"34\"," +
            "\"boot\":{\"vbstate\":\"orange\",\"device_state\":\"unlocked\",\"locked\":false,\"spoofed\":true}," +
            "\"kernel\":\"5.15.148-android13-8-00017-gabc123\",\"kmi\":\"5.15-android13\",\"selinux\":\"Enforcing\",\"root\":\"KernelSU\"," +
            "\"boot_reason\":\"reboot,userrequested\",\"battery\":{\"level\":78,\"health\":91,\"cycles\":312,\"temp_dc\":334}," +
            "\"memory\":{\"total_kb\":7864320,\"avail_kb\":2725000},\"storage\":{\"type\":\"emmc\",\"life_a\":\"0x02\",\"life_b\":\"0x01\",\"eol\":\"0x01\"}}\n" +
            "@@\n" +
            "{\"root\":\"/data/adb/modules\",\"modules\":[" +
            "{\"id\":\"addon\",\"name\":\"Addon\",\"version\":\"v1.0\",\"enabled\":true}," +
            "{\"id\":\"debloat\",\"name\":\"Debloat\",\"version\":\"v1.0\",\"enabled\":true}," +
            "{\"id\":\"fonts_1\",\"name\":\"Fonts 1\",\"version\":\"v1.0\",\"enabled\":true}," +
            "{\"id\":\"fonts_2\",\"name\":\"Fonts 2\",\"version\":\"v1.0\",\"enabled\":true}," +
            "{\"id\":\"hosts_a\",\"name\":\"Hosts A\",\"version\":\"v1.0\",\"enabled\":true}," +
            "{\"id\":\"hosts_b\",\"name\":\"Hosts \\\"B\\\"\",\"version\":\"v1.0\",\"enabled\":true}," +
            "{\"id\":\"nomount\",\"name\":\"No mount\",\"version\":\"v1.0\",\"enabled\":true}]," +
            "\"conflicts\":[" +
            "{\"kind\":\"file\",\"modules\":[\"fonts_1\",\"fonts_2\"],\"count\":20,\"paths\":[\"/system/fonts/Demo1.ttf\",\"/system/fonts/Demo10.ttf\"]}," +
            "{\"kind\":\"file\",\"modules\":[\"hosts_a\",\"hosts_b\"],\"count\":2,\"paths\":[\"/system/etc/hosts\",\"/system/vendor/lib/libdemo.so\"]}," +
            "{\"kind\":\"replace\",\"path\":\"/system/app/Foo\",\"by\":\"debloat\",\"modules\":[\"addon\"]}," +
            "{\"kind\":\"prop\",\"key\":\"ro.demo.x\",\"values\":[{\"module\":\"hosts_a\",\"value\":\"1\"},{\"module\":\"debloat\",\"value\":\"2\"},{\"module\":\"nomount\",\"value\":\"1\"}]}]}\n";

        [Fact]
        public void Reads_the_phone_without_root()
        {
            PhoneReport phone = PhoneReport.Parse(Basic);
            Assert.Equal("Demo Phone", phone.Model);
            Assert.Equal("14", phone.Android);
            Assert.Equal("34", phone.Sdk);
            Assert.Equal("2026-08-05", phone.Patch);
            Assert.Equal("MediaTek", phone.Soc);
            Assert.Equal("mt6789", phone.Platform);
            Assert.Equal("arm64-v8a", phone.Arch);
            Assert.Equal("_a", phone.Slot);
            Assert.True(phone.Dynamic);
            Assert.Equal("android13-5.15", phone.Kmi);
            Assert.Equal("Enforcing", phone.Selinux);
            // Without the Companion, the properties are all there is.
            Assert.Equal("green", phone.Avb);
            Assert.Equal("locked", phone.DeviceState);
            Assert.False(phone.Spoofed);
            Assert.Null(phone.Root);
            Assert.Equal(78, phone.BatteryLevel);
            Assert.Equal(312, phone.BatteryCycles);
            Assert.Equal(91, phone.BatteryHealth);
            Assert.Equal(CompanionState.NoRoot, phone.Companion);
        }

        [Fact]
        public void Lists_backups_newest_first_and_tells_their_kind()
        {
            PhoneReport phone = PhoneReport.Parse(Basic);
            Assert.Equal(3, phone.Backups.Count);
            Assert.Equal(new[] { "20261010-184012", "20260921-184012", "20260830-091233" },
                phone.Backups.Select(b => b.Name.Substring(b.Name.LastIndexOf('_') + 1)).ToArray());

            PhoneBackup full = phone.Backups[0];
            Assert.Equal(BackupSet.Full, full.Set);
            Assert.Equal(22, full.Files);
            Assert.Equal(10066329, full.Kb);
            Assert.Equal(new DateTime(2026, 10, 10, 18, 40, 12), full.Taken);
            Assert.Equal(PhoneReport.BackupsFolder + "/Demo_Phone_DEMO0123456789_20261010-184012", full.Path);

            Assert.Equal(BackupSet.Critical, phone.Backups[1].Set);
            Assert.Equal(BackupSet.Boot, phone.Backups[2].Set);
            Assert.Equal(new[] { "boot_a", "init_boot_a", "vbmeta_a", "dtbo_a" }, phone.Backups[2].Images);
            Assert.Equal(10066329 + 352256 + 114688, phone.BackupsKb);
        }

        [Fact]
        public void Skips_backup_lines_that_could_leave_the_folder()
        {
            PhoneReport phone = PhoneReport.Parse("backup=..|1|4|a.img\nbackup=a/b|1|4|a.img\nbackup=|1|4|\nbackup=x_y|2\n");
            Assert.Empty(phone.Backups);
        }

        [Fact]
        public void The_companion_overrides_the_properties_a_hiding_module_changed()
        {
            PhoneReport phone = PhoneReport.Parse(Basic);
            phone.ApplyCompanion(true, Companion);

            Assert.Equal(CompanionState.Ready, phone.Companion);
            Assert.Equal("2.4.0", phone.CompanionVersion);
            Assert.Equal("beta", phone.CompanionChannel);
            Assert.Equal("orange", phone.Avb);
            Assert.Equal("unlocked", phone.DeviceState);
            Assert.True(phone.Spoofed);
            Assert.Equal("KernelSU", phone.Root);
            Assert.Equal(91, phone.BatteryHealth);
            Assert.Equal("emmc", phone.StorageType);
            Assert.Equal(2, phone.StorageWear);   // the worse of 0x02 and 0x01
            Assert.Equal(1, phone.StorageEol);
            Assert.Equal(7, phone.ModuleCount);
            // The backups came from the first command and stay.
            Assert.Equal(3, phone.Backups.Count);
        }

        [Fact]
        public void Reads_every_kind_of_module_conflict_by_module_name()
        {
            PhoneReport phone = PhoneReport.Parse("");
            phone.ApplyCompanion(true, Companion);
            Assert.Equal(4, phone.Conflicts.Count);

            ModuleConflict fonts = phone.Conflicts[0];
            Assert.Equal(ConflictKind.File, fonts.Kind);
            Assert.Equal(new[] { "Fonts 1", "Fonts 2" }, fonts.Modules);
            Assert.Equal(20, fonts.Count);
            Assert.Equal("/system/fonts/Demo1.ttf", fonts.Paths[0]);

            Assert.Equal(new[] { "Hosts A", "Hosts \"B\"" }, phone.Conflicts[1].Modules);

            ModuleConflict replace = phone.Conflicts[2];
            Assert.Equal(ConflictKind.Replace, replace.Kind);
            Assert.Equal("Debloat", replace.By);
            Assert.Equal(new[] { "Addon" }, replace.Modules);
            Assert.Equal("/system/app/Foo", replace.Paths.Single());

            ModuleConflict prop = phone.Conflicts[3];
            Assert.Equal(ConflictKind.Prop, prop.Kind);
            Assert.Equal("ro.demo.x", prop.Key);
            Assert.Equal(new[] { "Hosts A", "Debloat", "No mount" }, prop.Modules);
            Assert.Equal("2", prop.Values[1].Value);
        }

        [Fact]
        public void Root_means_an_unlocked_bootloader_whatever_the_properties_say()
        {
            // A Xiaomi msm8937 with KernelSU: the ROM sets ro.boot.* to locked and green, and
            // the Companion is not installed, so the properties are all the report reads.
            PhoneReport phone = PhoneReport.Parse(Basic);
            Assert.Equal("locked", phone.DeviceState);
            phone.ApplyCompanion(true, "fbs-missing\n");
            Assert.Equal(CompanionState.NotInstalled, phone.Companion);
            Assert.Equal("unlocked", phone.DeviceState);
            Assert.Equal("orange", phone.Avb);
            Assert.True(phone.Spoofed);

            // An older Companion that still believed the properties.
            PhoneReport old = PhoneReport.Parse(Basic);
            old.ApplyCompanion(true, "{\"fbs\":\"2.5.1\",\"channel\":\"stable\",\"root\":\"KernelSU\"," +
                "\"boot\":{\"vbstate\":\"orange\",\"device_state\":\"locked\",\"locked\":true,\"spoofed\":false}}\n");
            Assert.Equal(CompanionState.Ready, old.Companion);
            Assert.Equal("unlocked", old.DeviceState);
            Assert.Equal("orange", old.Avb);
            Assert.True(old.Spoofed);

            // Without su nothing changes: the properties are all there is.
            PhoneReport noRoot = PhoneReport.Parse(Basic);
            noRoot.ApplyCompanion(false, "");
            Assert.Equal("locked", noRoot.DeviceState);
            Assert.False(noRoot.Spoofed);
        }

        [Fact]
        public void Tells_a_missing_module_from_a_refused_root()
        {
            PhoneReport none = PhoneReport.Parse(Basic);
            none.ApplyCompanion(false, "");
            Assert.Equal(CompanionState.NoRoot, none.Companion);

            PhoneReport missing = PhoneReport.Parse(Basic);
            missing.ApplyCompanion(true, "fbs-missing\n");
            Assert.Equal(CompanionState.NotInstalled, missing.Companion);

            PhoneReport refused = PhoneReport.Parse(Basic);
            refused.ApplyCompanion(true, "Permission denied\n");
            Assert.Equal(CompanionState.Refused, refused.Companion);
            // su is there, so root is: the "green" and "locked" properties were set for Play Integrity.
            Assert.Equal("orange", refused.Avb);
            Assert.Equal("unlocked", refused.DeviceState);

            PhoneReport broken = PhoneReport.Parse(Basic);
            broken.ApplyCompanion(true, "{\"fbs\":\"2.4.0\",\"model\":");
            Assert.Equal(CompanionState.Refused, broken.Companion);
        }

        [Fact]
        public void A_module_list_it_cannot_read_keeps_the_rest()
        {
            PhoneReport phone = PhoneReport.Parse(Basic);
            phone.ApplyCompanion(true, Companion.Substring(0, Companion.IndexOf("@@", StringComparison.Ordinal) + 3) + "{\"modules\":[");
            Assert.Equal(CompanionState.Ready, phone.Companion);
            Assert.Equal("KernelSU", phone.Root);
            Assert.Empty(phone.Conflicts);
        }

        [Theory]
        [InlineData("Mediatek", "", "", "MediaTek")]
        [InlineData("QTI", "", "", "Qualcomm")]
        [InlineData("", "kalama", "qcom", "Qualcomm")]
        [InlineData("", "sm8550", "", "Qualcomm")]
        [InlineData("", "mt6878", "", "MediaTek")]
        [InlineData("", "ums9230", "", "Unisoc")]
        [InlineData("", "exynos2100", "", "Samsung")]
        [InlineData("", "zuma", "", "Google")]
        [InlineData("", "", "", null)]
        public void Names_the_chip_maker(string maker, string platform, string hardware, string expected)
        {
            Assert.Equal(expected, PhoneReport.SocVendor(maker, platform, hardware));
        }

        [Fact]
        public void Kinds_of_backup()
        {
            Assert.Equal(BackupSet.Boot, PhoneReport.SetOf(new[] { "boot_a", "init_boot_a", "vendor_boot_a", "vbmeta_system_a" }));
            Assert.Equal(BackupSet.Critical, PhoneReport.SetOf(new[] { "nvram", "persist", "seccfg", "boot_a" }));
            Assert.Equal(BackupSet.Full, PhoneReport.SetOf(new[] { "nvram", "super" }));
            Assert.Equal(BackupSet.Full, PhoneReport.SetOf(new string[0]));
        }

        [Fact]
        public void Commands_fit_where_they_go()
        {
            // The Companion's command goes through su -c '…', which cannot carry single quotes.
            Assert.Equal("su -c '" + PhoneReport.CompanionCommand + "'", AdbCommand.AsRoot(PhoneReport.CompanionCommand, RootAccess.Su));
            Assert.DoesNotContain("\n", PhoneReport.Command);
        }

        /// <summary>Runs the real command in sh, with getprop answered by a stub and a backups folder made here.</summary>
        [Fact]
        public void The_command_runs_in_a_posix_shell()
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
                return;
            string root = Path.Combine(Path.GetTempPath(), "fbs-phone-" + Guid.NewGuid().ToString("N"));
            string backup = Path.Combine(root, "Demo_Phone_SER1_20260921-184012");
            Directory.CreateDirectory(backup);
            try
            {
                File.WriteAllText(Path.Combine(backup, "SHA256SUMS"), new string('a', 64) + "  boot_a.img\n" + new string('b', 64) + "  nvram.img\n");
                File.WriteAllBytes(Path.Combine(backup, "boot_a.img"), new byte[8192]);
                File.WriteAllBytes(Path.Combine(backup, "nvram.img"), new byte[4096]);
                string command = "getprop() { case $1 in ro.product.model) echo 'Demo Phone';; ro.board.platform) echo mt6789;; " +
                    "ro.boot.slot_suffix) echo _b;; esac; }; getenforce() { echo Permissive; }; " +
                    PhoneReport.Command.Replace(PhoneReport.BackupsFolder, root);

                ProcessStartInfo start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(command);
                using Process sh = Process.Start(start)!;
                string output = sh.StandardOutput.ReadToEnd();
                sh.WaitForExit();

                PhoneReport phone = PhoneReport.Parse(output);
                Assert.Equal("Demo Phone", phone.Model);
                Assert.Equal("MediaTek", phone.Soc);
                Assert.Equal("_b", phone.Slot);
                Assert.Equal("Permissive", phone.Selinux);
                Assert.NotNull(phone.Kernel);
                PhoneBackup found = Assert.Single(phone.Backups);
                Assert.Equal("Demo_Phone_SER1_20260921-184012", found.Name);
                Assert.Equal(2, found.Files);
                Assert.True(found.Kb > 0);
                Assert.Equal(new[] { "boot_a", "nvram" }, found.Images.OrderBy(n => n).ToArray());
                Assert.Equal(BackupSet.Critical, found.Set);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
