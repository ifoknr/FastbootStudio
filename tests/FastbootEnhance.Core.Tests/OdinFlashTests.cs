using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastbootEnhance.Core.Samsung;
using FastbootEnhance.Core.Samsung.Odin;

namespace FastbootEnhance.Core.Tests
{
    public class OdinFlashTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "odinflash-" + Guid.NewGuid().ToString("N"));

        public OdinFlashTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            Directory.Delete(dir, true);
        }

        static byte[] Bytes(int length, int seed)
        {
            byte[] data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        string File(string name, byte[] data)
        {
            string path = Path.Combine(dir, name);
            System.IO.File.WriteAllBytes(path, data);
            return path;
        }

        string Tar(string name, bool md5, params (string name, byte[] data)[] entries)
        {
            string path = Path.Combine(dir, name);
            using (FileStream file = new FileStream(path, FileMode.Create))
            using (OdinTarWriter tar = new OdinTarWriter(file, md5 ? name.Substring(0, name.Length - 4) : null))
            {
                foreach ((string entry, byte[] data) in entries)
                    tar.Add(entry, new MemoryStream(data), data.Length);
                tar.Finish();
            }
            return path;
        }

        static readonly byte[] Pit = FakeOdinDevice.SamplePit();

        static OdinFlashPlan Plan(IEnumerable<string> paths, string model = "SM-S911B") =>
            OdinFlashPlan.Build(OdinSources.Read(paths), Pit, model);

        // ---------------------------------------------------------------- the client

        [Fact]
        public void One_image_is_written_whole_and_the_phone_restarts()
        {
            byte[] boot = Bytes(3 * 1024 * 1024 + 123, 1);
            OdinFlashPlan plan = Plan(new[] { File("boot.img", boot) });
            Assert.False(plan.Blocked);
            Assert.Equal("BOOT", plan.Items.Single().Entry.Partition);

            FakeOdinDevice phone = new FakeOdinDevice();
            List<long> progress = new List<long>();
            OdinFlashResult result = OdinFlasher.Flash(phone, plan, OdinReboot.Normal, (p, done, total) => progress.Add(done));

            Assert.Null(result.Error);
            Assert.Equal(new[] { "BOOT" }, result.Written);
            Assert.Equal(boot, phone.Written["BOOT"]);
            Assert.Equal(boot.Length, phone.TotalBytes);
            Assert.Equal(OdinPacket.LargePartSize, phone.PartSize);
            Assert.Equal("reboot", phone.Restarted);
            Assert.Equal(OdinReboot.Normal, result.Restart);
            Assert.Equal(boot.Length, progress.Last());
            Assert.True(progress.SequenceEqual(progress.OrderBy(x => x)));
        }

        [Fact]
        public void A_file_longer_than_a_sequence_goes_in_several()
        {
            // 30 parts of 1 MiB make a sequence: this takes two, the last one short.
            byte[] super = Bytes(31 * 1024 * 1024 + 5, 2);
            OdinFlashPlan plan = Plan(new[] { File("super.img", super) });
            FakeOdinDevice phone = new FakeOdinDevice();
            OdinFlashResult result = OdinFlasher.Flash(phone, plan, OdinReboot.None);
            Assert.Null(result.Error);
            Assert.Equal(super, phone.Written["SUPER"]);
            Assert.Equal(2, phone.Received.Count(r => r == "0x66/2"));
            Assert.Null(phone.Restarted);
        }

        [Fact]
        public void A_bootloader_that_keeps_the_default_gets_parts_of_128_KiB()
        {
            byte[] dtbo = Bytes(300 * 1024 + 7, 3);
            FakeOdinDevice phone = new FakeOdinDevice { SessionAnswer = 0 };
            OdinFlashResult result = OdinFlasher.Flash(phone, Plan(new[] { File("dtbo.img", dtbo) }), OdinReboot.None);
            Assert.Null(result.Error);
            Assert.Equal(OdinPacket.DefaultPartSize, phone.PartSize);
            Assert.DoesNotContain("0x64/5", phone.Received);
            Assert.Equal(dtbo, phone.Written["DTBO"]);
        }

        [Fact]
        public void A_modem_goes_to_the_modem()
        {
            byte[] modem = Bytes(70000, 4);
            OdinFlashPlan plan = Plan(new[] { File("modem.bin", modem) });
            Assert.Contains(plan.Checks, c => c.Code == OdinCheckCode.Modem && c.Level == OdinCheckLevel.Warn);
            FakeOdinDevice phone = new FakeOdinDevice();
            Assert.Null(OdinFlasher.Flash(phone, plan, OdinReboot.None).Error);
            Assert.Equal(modem, phone.Written["RADIO"]);
        }

        [Fact]
        public void LZ4_is_decoded_on_the_way()
        {
            byte[] dtbo = Bytes(200000, 5);
            OdinFlashPlan plan = Plan(new[] { File("dtbo.img.lz4", SamsungTests.StoredFrame(dtbo)) });
            OdinFlashSource source = plan.Items.Single().Source;
            Assert.Equal("dtbo.img", source.FileName);
            Assert.Equal(dtbo.Length, source.Size);
            FakeOdinDevice phone = new FakeOdinDevice();
            Assert.Null(OdinFlasher.Flash(phone, plan, OdinReboot.None).Error);
            Assert.Equal(dtbo, phone.Written["DTBO"]);
        }

        [Fact]
        public void Samsungs_own_AP_is_flashed_entry_by_entry()
        {
            string ap = File(SamsungFixtures.ApName, Convert.FromBase64String(SamsungFixtures.Ap));
            OdinFlashPlan plan = Plan(new[] { ap }, "SM-S918B");
            Assert.False(plan.Blocked);
            Assert.Equal(new[] { "INIT_BOOT", "VBMETA" }, plan.Items.Select(i => i.Entry.Partition));
            FakeOdinDevice phone = new FakeOdinDevice();
            Assert.Null(OdinFlasher.Flash(phone, plan, OdinReboot.Download).Error);
            Assert.Equal(new[] { "INIT_BOOT", "VBMETA" }, phone.Flashed);
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                Assert.Equal(SamsungFixtures.InitBootSha256, OdinTar.Hex(sha.ComputeHash(phone.Written["INIT_BOOT"])));
                Assert.Equal(SamsungFixtures.VbmetaSha256, OdinTar.Hex(sha.ComputeHash(phone.Written["VBMETA"])));
            }
            Assert.Equal("download", phone.Restarted);
        }

        [Fact]
        public void A_whole_firmware_goes_BL_AP_CP_CSC_and_leaves_out_what_the_PIT_lacks()
        {
            string bl = Tar("BL_S911BXXU3BXA1_CL1_REV00_user_low_ship.tar.md5", true, ("sboot.bin", Bytes(5000, 6)), ("param.bin", Bytes(4000, 7)));
            string ap = Tar("AP_S911BXXU3BXA1_CL1_REV00_user_low_ship_meta_OS14.tar.md5", true,
                ("boot.img.lz4", SamsungTests.StoredFrame(Bytes(9000, 8))), ("fota.zip", Bytes(100, 9)));
            string cp = Tar("CP_S911BXXU3BXA1_CP2_CL1_REV00_user_low_ship.tar.md5", true, ("modem.bin", Bytes(6000, 10)));
            string csc = Tar("HOME_CSC_OXM_S911BOXM3BXA1_CL1_REV00_user_low_ship.tar.md5", true,
                ("SM8550_EUR_OPEN.pit", Pit), ("cache.img", Bytes(3000, 11)));

            // Picked in any order, flashed in Odin's: BL, AP, CP, CSC.
            OdinFlashPlan plan = Plan(new[] { csc, cp, ap, bl });
            Assert.False(plan.Blocked);
            Assert.Equal(new[] { "BOOTLOADER", "PARAM", "BOOT", "RADIO", "CACHE" }, plan.Items.Select(i => i.Entry.Partition));
            Assert.Contains(plan.Checks, c => c.Code == OdinCheckCode.Skipped && c.Args[0] == "fota.zip");
            Assert.Contains(plan.Checks, c => c.Code == OdinCheckCode.PitSkipped);
            // Firmware parts are expected to hold a bootloader and a modem: no warning for those.
            Assert.DoesNotContain(plan.Checks, c => c.Code == OdinCheckCode.Bootloader || c.Code == OdinCheckCode.Modem);

            FakeOdinDevice phone = new FakeOdinDevice();
            OdinFlashResult result = OdinFlasher.Flash(phone, plan, OdinReboot.Normal);
            Assert.Null(result.Error);
            Assert.Equal(plan.Items.Select(i => i.Entry.Partition), phone.Flashed);
            Assert.Equal(plan.TotalBytes, phone.TotalBytes);
        }

        // ---------------------------------------------------------------- the checks

        [Fact]
        public void An_image_the_PIT_does_not_know_is_refused_until_a_partition_is_chosen()
        {
            List<OdinFlashSource> sources = OdinSources.Read(new[] { File("magisk_patched-28100_aB3dE.img", Bytes(4096, 12)) });
            OdinFlashPlan plan = OdinFlashPlan.Build(sources, Pit, "SM-S911B");
            Assert.True(plan.Blocked);
            Assert.Contains(plan.Checks, c => c.Code == OdinCheckCode.NotInPit);

            sources[0].Target = "boot";
            plan = OdinFlashPlan.Build(sources, Pit, "SM-S911B");
            Assert.False(plan.Blocked);
            Assert.Equal("BOOT", plan.Items.Single().Entry.Partition);
        }

        [Theory]
        [InlineData("efs.img")]
        [InlineData("sec_efs.img")]
        public void The_phones_identity_is_never_written(string name)
        {
            OdinFlashPlan plan = Plan(new[] { File(name, Bytes(1000, 13)) });
            Assert.True(plan.Blocked);
            Assert.Contains(plan.Checks, c => c.Code == OdinCheckCode.Protected);
            Assert.Throws<InvalidOperationException>(() => OdinFlasher.Flash(new FakeOdinDevice(), plan, OdinReboot.None));
        }

        [Fact]
        public void An_image_bigger_than_its_partition_is_refused()
        {
            // VBMETA is 16 blocks on UFS (device type 8): 64 KiB at most.
            OdinFlashPlan plan = Plan(new[] { File("vbmeta.img", Bytes(70000, 14)) });
            OdinCheck check = Assert.Single(plan.Checks, c => c.Code == OdinCheckCode.TooBig);
            Assert.Equal(OdinCheckLevel.Block, check.Level);
            Assert.Equal("VBMETA", check.Args[1]);
            Assert.Equal("65536", check.Args[3]);
        }

        [Fact]
        public void The_size_limit_follows_the_PIT_device_type()
        {
            // The sample PIT's BOOTLOADER is on eMMC (device type 2): 8192 blocks of 512 bytes, 4 MiB.
            OdinFlashPlan mmc = Plan(new[] { File("sboot.bin", Bytes(4 * 1024 * 1024 + 1, 14)) });
            OdinCheck tooBig = Assert.Single(mmc.Checks, c => c.Code == OdinCheckCode.TooBig);
            Assert.Equal(OdinCheckLevel.Block, tooBig.Level);
            Assert.Equal((8192L * 512).ToString(), tooBig.Args[3]);
            Assert.DoesNotContain(Plan(new[] { File("sboot.bin", Bytes(4 * 1024 * 1024, 14)) }).Checks, c => c.Code == OdinCheckCode.TooBig);

            // A PIT that names neither storage: the UFS size blocks, the eMMC size only warns.
            byte[] pit = SamsungPit.Build("COM_TAR2", "", new[]
            {
                SamsungPit.Entry(1, "BOOT", "boot.img", 1000, 16, deviceType: 0),
                SamsungPit.Entry(2, "DTBO", "dtbo.img", 2000, 16, deviceType: 0),
            });
            OdinFlashPlan warned = OdinFlashPlan.Build(OdinSources.Read(new[] { File("boot.img", Bytes(16 * 512 + 1, 15)) }), pit, null);
            OdinCheck mayNot = Assert.Single(warned.Checks, c => c.Code == OdinCheckCode.MayNotFit);
            Assert.Equal(OdinCheckLevel.Warn, mayNot.Level);
            Assert.Equal("8192", mayNot.Args[3]);
            Assert.False(warned.Blocked);
            OdinFlashPlan blocked = OdinFlashPlan.Build(OdinSources.Read(new[] { File("boot.img", Bytes(16 * 4096 + 1, 16)) }), pit, null);
            Assert.Contains(blocked.Checks, c => c.Code == OdinCheckCode.TooBig && c.Level == OdinCheckLevel.Block);
            Assert.DoesNotContain(blocked.Checks, c => c.Code == OdinCheckCode.MayNotFit);

            // PIT v1 records the block size itself.
            byte[] v1 = SamsungPit.Build("COM_TAR2", "", new[]
            {
                SamsungPit.Entry(1, "BOOT", "boot.img", 2048, 16),
                SamsungPit.Entry(2, "DTBO", "dtbo.img", 2048, 16),
            });
            OdinFlashPlan old = OdinFlashPlan.Build(OdinSources.Read(new[] { File("boot.img", Bytes(16 * 2048 + 1, 17)) }), v1, null);
            Assert.Equal("32768", Assert.Single(old.Checks, c => c.Code == OdinCheckCode.TooBig).Args[3]);
        }

        [Fact]
        public void Two_files_for_one_partition_are_refused()
        {
            string a = File("boot.img", Bytes(100, 15));
            string b = File("boot.img.lz4", SamsungTests.StoredFrame(Bytes(100, 16)));
            Assert.Contains(Plan(new[] { a, b }).Checks, c => c.Code == OdinCheckCode.Twice && c.Level == OdinCheckLevel.Block);
        }

        [Fact]
        public void A_bootloader_on_its_own_and_userdata_are_warned_about()
        {
            OdinFlashPlan plan = Plan(new[] { File("sboot.bin", Bytes(100, 17)), File("userdata.img", Bytes(100, 18)) });
            Assert.False(plan.Blocked);
            Assert.Contains(plan.Checks, c => c.Code == OdinCheckCode.Bootloader && c.Args[0] == "BOOTLOADER");
            Assert.Contains(plan.Checks, c => c.Code == OdinCheckCode.WipesData);
        }

        [Fact]
        public void Firmware_for_another_model_is_refused_and_an_unknown_phone_is_warned_about()
        {
            string ap = File(SamsungFixtures.ApName, Convert.FromBase64String(SamsungFixtures.Ap));
            OdinFlashPlan other = Plan(new[] { ap }, "SM-S911B");
            OdinCheck check = Assert.Single(other.Checks, c => c.Code == OdinCheckCode.OtherModel);
            Assert.Equal(new[] { "SM-S918B", "SM-S911B" }, check.Args);
            Assert.True(other.Blocked);

            OdinFlashPlan unknown = Plan(new[] { ap }, null);
            Assert.False(unknown.Blocked);
            Assert.Contains(unknown.Checks, c => c.Code == OdinCheckCode.ModelUnknown && c.Level == OdinCheckLevel.Warn);
        }

        [Fact]
        public void A_bootloader_of_another_model_next_to_the_right_AP_is_refused()
        {
            string ap = File(SamsungFixtures.ApName, Convert.FromBase64String(SamsungFixtures.Ap));
            string bl = Tar("BL_S911BXXU3BWK5_CL1_REV00_user_low_ship.tar.md5", true, ("sboot.bin", Bytes(100, 31)));
            OdinFlashPlan plan = Plan(new[] { ap, bl }, "SM-S918B");
            Assert.True(plan.Blocked);
            OdinCheck check = Assert.Single(plan.Checks, c => c.Code == OdinCheckCode.OtherModel);
            Assert.Equal(new[] { "SM-S911B", "SM-S918B" }, check.Args);
        }

        [Fact]
        public void A_tar_whose_name_is_not_Samsungs_is_warned_about_and_an_empty_file_is_refused()
        {
            string tar = Tar("AP_patched.tar", false, ("boot.img", Bytes(100, 32)));
            OdinFlashPlan plan = Plan(new[] { tar });
            Assert.False(plan.Blocked);
            OdinCheck warn = Assert.Single(plan.Checks, c => c.Code == OdinCheckCode.TarUnknown);
            Assert.Equal(OdinCheckLevel.Warn, warn.Level);
            Assert.Equal("AP_patched.tar", warn.Args[0]);

            OdinFlashPlan empty = Plan(new[] { File("vbmeta.img", new byte[0]) });
            Assert.True(empty.Blocked);
            Assert.Contains(empty.Checks, c => c.Code == OdinCheckCode.EmptyFile && c.Args[0] == "vbmeta.img");
            Assert.True(OdinFlashPlan.IsProtected("PGPT"));
            Assert.True(OdinFlashPlan.IsProtected("efs"));
            Assert.False(OdinFlashPlan.IsProtected("BOOT"));
        }

        [Fact]
        public void CSC_wipes_the_phone_and_CSC_with_HOME_CSC_is_refused()
        {
            string csc = Tar("CSC_OXM_S911BOXM3BXA1_CL1_REV00_user_low_ship.tar.md5", true, ("cache.img", Bytes(100, 19)));
            string home = Tar("HOME_CSC_OXM_S911BOXM3BXA1_CL1_REV00_user_low_ship.tar.md5", true, ("cache.img", Bytes(100, 20)));
            Assert.Contains(Plan(new[] { csc }).Checks, c => c.Code == OdinCheckCode.WipesData && c.Level == OdinCheckLevel.Warn);
            Assert.Contains(Plan(new[] { csc, home }).Checks, c => c.Code == OdinCheckCode.BothCsc && c.Level == OdinCheckLevel.Block);
        }

        [Fact]
        public void Blocks_come_first_and_an_empty_choice_is_refused()
        {
            OdinFlashPlan plan = Plan(new[] { File("efs.img", Bytes(10, 21)), File("sboot.bin", Bytes(10, 22)) });
            Assert.Equal(OdinCheckLevel.Block, plan.Checks[0].Level);
            Assert.Contains(Plan(new string[0]).Checks, c => c.Code == OdinCheckCode.Empty);
        }

        [Fact]
        public void A_damaged_file_is_found_before_anything_is_sent()
        {
            byte[] frame = SamsungTests.StoredFrame(Bytes(5000, 23));
            frame[frame.Length - 1] ^= 0xFF;
            Assert.Throws<InvalidDataException>(() => OdinSources.Read(new[] { File("boot.img.lz4", frame) }));

            string ap = Tar("AP_S911BXXU3BXA1_CL1_REV00_user_low_ship_meta_OS14.tar.md5", true, ("boot.img", Bytes(2000, 24)));
            byte[] bytes = System.IO.File.ReadAllBytes(ap);
            bytes[700] ^= 0xFF;
            System.IO.File.WriteAllBytes(ap, bytes);
            InvalidDataException e = Assert.Throws<InvalidDataException>(() => OdinSources.Read(new[] { ap }));
            Assert.Contains("MD5", e.Message);
        }

        // ---------------------------------------------------------------- failures

        [Fact]
        public void A_phone_with_another_PIT_gets_nothing_written()
        {
            OdinFlashPlan plan = Plan(new[] { File("boot.img", Bytes(5000, 25)) });
            byte[] otherPit = SamsungPit.Build("COM_TAR2", "SM8550", new[] { SamsungPit.Entry(12, "BOOT", "boot.img", 0, 100) });
            FakeOdinDevice phone = new FakeOdinDevice(otherPit);
            OdinFlashResult result = OdinFlasher.Flash(phone, plan, OdinReboot.Normal);
            Assert.True(result.PitChanged);
            Assert.NotNull(result.Error);
            Assert.Empty(result.Written);
            Assert.Empty(phone.Written);
            Assert.DoesNotContain(phone.Received, r => r.StartsWith("0x66/"));
            // Left in Download mode, ready for another try.
            Assert.Null(phone.Restarted);
            Assert.True(result.Ended);
        }

        [Fact]
        public void A_refused_image_stops_the_flash_and_keeps_the_phone_in_Download_mode()
        {
            OdinFlashPlan plan = Plan(new[] { File("boot.img", Bytes(5000, 26)), File("dtbo.img", Bytes(5000, 27)), File("recovery.img", Bytes(5000, 28)) });
            FakeOdinDevice phone = new FakeOdinDevice { RefusePartition = "DTBO" };
            OdinFlashResult result = OdinFlasher.Flash(phone, plan, OdinReboot.Normal);
            Assert.NotNull(result.Error);
            Assert.Equal(new[] { "BOOT" }, result.Written);
            Assert.Equal("DTBO", result.FailedAt);
            Assert.DoesNotContain("RECOVERY", phone.Flashed);
            Assert.Null(phone.Restarted);
        }

        [Fact]
        public void A_pulled_cable_mid_flash_is_reported_with_the_partition()
        {
            OdinFlashPlan plan = Plan(new[] { File("boot.img", Bytes(3 * 1024 * 1024, 29)) });
            FakeOdinDevice phone = new FakeOdinDevice { SilentAfterParts = 1 };
            OdinFlashResult result = OdinFlasher.Flash(phone, plan, OdinReboot.Normal);
            Assert.IsType<OdinException>(result.Error);
            Assert.Equal("BOOT", result.FailedAt);
            Assert.Empty(result.Written);
        }
    }
}
