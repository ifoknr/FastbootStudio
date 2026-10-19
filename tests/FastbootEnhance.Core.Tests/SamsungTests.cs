using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FastbootEnhance.Core.Images;
using FastbootEnhance.Core.Samsung;

namespace FastbootEnhance.Core.Tests
{
    public class SamsungTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "fbs-samsung-" + Guid.NewGuid().ToString("N"));

        public SamsungTests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }

        string WriteAp()
        {
            string path = Path.Combine(dir, SamsungFixtures.ApName);
            File.WriteAllBytes(path, Convert.FromBase64String(SamsungFixtures.Ap));
            return path;
        }

        static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
                return OdinTar.Hex(sha.ComputeHash(f));
        }

        /// <summary>An LZ4 frame of stored (uncompressed) blocks: valid LZ4 without a compressor.</summary>
        static byte[] StoredFrame(byte[] data, bool checksum = true)
        {
            MemoryStream ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(Lz4.FrameMagic), 0, 4);
            byte[] descriptor = { (byte)(0x40 | 0x20 | (checksum ? 0x04 : 0)), 0x40 };
            ms.Write(descriptor, 0, 2);
            ms.WriteByte((byte)(XxHash32.Hash(descriptor, 0, 2) >> 8));
            for (int at = 0; at < data.Length; at += 65536)
            {
                int n = Math.Min(65536, data.Length - at);
                ms.Write(BitConverter.GetBytes(0x80000000u | (uint)n), 0, 4);
                ms.Write(data, at, n);
            }
            ms.Write(new byte[4], 0, 4);
            if (checksum)
                ms.Write(BitConverter.GetBytes(XxHash32.Hash(data, 0, data.Length)), 0, 4);
            return ms.ToArray();
        }

        // ---------------------------------------------------------------- xxHash32 and LZ4

        [Theory]
        [InlineData("", 0x02CC5D05u)]
        [InlineData("abc", 0x32D153FFu)]
        [InlineData("Nobody inspects the spammish repetition", 0xE2293B2Fu)]
        public void XxHash32_matches_the_reference(string text, uint expected)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            Assert.Equal(expected, XxHash32.Hash(b, 0, b.Length));
            // The same in pieces, as a stream feeds it.
            XxHash32 x = new XxHash32();
            for (int i = 0; i < b.Length; i += 3)
                x.Append(b, i, Math.Min(3, b.Length - i));
            Assert.Equal(expected, x.Digest());
        }

        [Theory]
        [InlineData(nameof(CompressedFixtures.Lz4Frame))]
        [InlineData(nameof(CompressedFixtures.Lz4FrameChecksums))]
        [InlineData(nameof(CompressedFixtures.Lz4FrameLinked))]
        public void The_streaming_decoder_gives_what_the_whole_buffer_decoder_gives(string fixture)
        {
            byte[] packed = Convert.FromBase64String((string)typeof(CompressedFixtures).GetField(fixture).GetValue(null));
            byte[] expected = Lz4.Decompress(packed, 0, packed.Length, 64 << 20);
            MemoryStream output = new MemoryStream();
            long written = Lz4Stream.Decode(new MemoryStream(packed), output);
            Assert.Equal(expected.Length, written);
            Assert.Equal(expected, output.ToArray());
        }

        [Fact]
        public void Stored_blocks_and_the_content_checksum_are_read()
        {
            byte[] data = Enumerable.Range(0, 200000).Select(i => (byte)(i * 7)).ToArray();
            MemoryStream output = new MemoryStream();
            Lz4Stream.Decode(new MemoryStream(StoredFrame(data)), output);
            Assert.Equal(data, output.ToArray());
        }

        [Fact]
        public void A_damaged_frame_is_refused_not_written_as_if_whole()
        {
            byte[] data = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
            byte[] frame = StoredFrame(data);
            frame[100] ^= 0xFF; // inside the stored block: only the content checksum can tell
            Assert.Throws<InvalidDataException>(() => Lz4Stream.Decode(new MemoryStream(frame), new MemoryStream()));

            byte[] cut = StoredFrame(data).Take(3000).ToArray();
            Assert.Throws<InvalidDataException>(() => Lz4Stream.Decode(new MemoryStream(cut), new MemoryStream()));
            Assert.Throws<InvalidDataException>(() => Lz4Stream.Decode(new MemoryStream(data), new MemoryStream()));
        }

        // ---------------------------------------------------------------- tar.md5

        [Fact]
        public void A_samsung_ap_is_checked_listed_and_unpacked()
        {
            string ap = WriteAp();
            Assert.Null(OdinTar.VerifyMd5(ap));

            List<TarEntry> entries;
            using (FileStream f = File.OpenRead(ap))
                entries = OdinTar.List(f);
            Assert.Equal(new[] { "init_boot.img.lz4", "vbmeta.img.lz4" }, entries.Select(e => e.Name));
            Assert.All(entries, e => Assert.True(e.Lz4));

            string outDir = Path.Combine(dir, "out");
            string initBoot = FirmwareTools.Extract(ap, entries[0], outDir);
            string vbmeta = FirmwareTools.Extract(ap, entries[1], outDir);
            Assert.Equal("init_boot.img", Path.GetFileName(initBoot));
            Assert.Equal(SamsungFixtures.InitBootSha256, Sha256(initBoot));
            Assert.Equal(SamsungFixtures.VbmetaSha256, Sha256(vbmeta));
            Assert.Empty(Directory.GetFiles(outDir, "*.part"));
        }

        [Fact]
        public void A_damaged_tar_md5_fails_its_md5()
        {
            string ap = WriteAp();
            byte[] b = File.ReadAllBytes(ap);
            b[700] ^= 1;
            File.WriteAllBytes(ap, b);
            Assert.NotNull(OdinTar.VerifyMd5(ap));
        }

        [Fact]
        public void A_plain_tar_has_no_md5_line()
        {
            string path = Path.Combine(dir, "x.tar");
            using (FileStream f = File.Create(path))
            using (OdinTarWriter w = new OdinTarWriter(f))
            {
                w.Add("recovery.img", new MemoryStream(new byte[1000]), 1000);
                w.Finish();
            }
            using (FileStream f = File.OpenRead(path))
                Assert.Null(OdinTar.ReadTrailer(f));
            Assert.Equal("no MD5 line at the end of the file", OdinTar.VerifyMd5(path));
        }

        [Fact]
        public void The_writer_makes_a_tar_md5_the_reader_and_its_md5_accept()
        {
            string path = Path.Combine(dir, "twrp.tar.md5");
            byte[] recovery = Enumerable.Range(0, 70000).Select(i => (byte)(i % 251)).ToArray();
            File.WriteAllBytes(Path.Combine(dir, "recovery.img"), recovery);
            FirmwareTools.BuildTar(new[] { Path.Combine(dir, "recovery.img") }, path, withMd5: true);

            Assert.Null(OdinTar.VerifyMd5(path));
            using (FileStream f = File.OpenRead(path))
            {
                Assert.Equal("twrp.tar", OdinTar.ReadTrailer(f).Name);
                TarEntry e = Assert.Single(OdinTar.List(f));
                Assert.Equal("recovery.img", e.Name);
                MemoryStream copy = new MemoryStream();
                OdinTar.Open(f, e).CopyTo(copy);
                Assert.Equal(recovery, copy.ToArray());
            }
        }

        [Fact]
        public void An_entry_of_more_than_8_GiB_is_read_from_its_base_256_size()
        {
            // A sparse file: a header for a 9 GiB super.img.lz4, then nothing written.
            long size = 9L << 30;
            string path = Path.Combine(dir, "big.tar");
            byte[] header = new byte[512];
            Encoding.ASCII.GetBytes("super.img.lz4").CopyTo(header, 0);
            Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
            header[124] = 0x80;
            long v = size;
            for (int i = 135; i > 124; i--, v >>= 8)
                header[i] = (byte)(v & 0xFF);
            header[156] = (byte)'0';
            Encoding.ASCII.GetBytes("ustar\000").CopyTo(header, 257);
            for (int i = 148; i < 156; i++)
                header[i] = (byte)' ';
            int sum = header.Sum(b => (int)b);
            Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
            using (FileStream f = File.Create(path))
            {
                f.Write(header, 0, 512);
                f.SetLength(512 + size + 1024);
            }
            using (FileStream f = File.OpenRead(path))
            {
                TarEntry e = Assert.Single(OdinTar.List(f));
                Assert.Equal(size, e.Size);
                Assert.Equal("super.img", e.PlainName);
            }
        }

        [Fact]
        public void Something_that_is_not_a_tar_is_refused()
        {
            Assert.Throws<InvalidDataException>(() => OdinTar.List(new MemoryStream(Enumerable.Repeat((byte)'A', 2048).ToArray())));
        }

        // ---------------------------------------------------------------- names and builds

        [Fact]
        public void A_build_number_tells_model_bootloader_binary_and_date()
        {
            SamsungBuild b = SamsungBuild.Parse("S918BXXU3BWK5");
            Assert.Equal("S918B", b.Model);
            Assert.Equal(3, b.Binary);
            Assert.Equal(2023, b.Year);
            Assert.Equal(11, b.Month);

            Assert.Equal(16, SamsungBuild.Parse("G973FXXSGHWC2").Binary);  // G = 16
            Assert.Equal("S918B", SamsungBuild.Parse("S918BOXM3BWK5").Model); // a CSC build
            Assert.Null(SamsungBuild.Parse("latest"));
            Assert.Null(SamsungBuild.Parse("S918BXXU3BW!5"));
        }

        [Fact]
        public void Firmware_names_are_read()
        {
            FirmwareSet set = FirmwareSet.FromPaths(new[]
            {
                @"D:\fw\" + SamsungFixtures.ApName,
                @"D:\fw\BL_S918BXXU3BWK5_S918BXXU3BWK5_MQB71230440_REV00_user_low_ship_MULTI_CERT.tar.md5",
                @"D:\fw\CP_S918BXXU3BWK5_CP25103453_MQB71230440_REV00_user_low_ship_MULTI_CERT.tar.md5",
                @"D:\fw\CSC_OXM_S918BOXM3BWK5_MQB71230440_REV00_user_low_ship_MULTI_CERT.tar.md5",
                @"D:\fw\HOME_CSC_OXM_S918BOXM3BWK5_MQB71230440_REV00_user_low_ship_MULTI_CERT.tar.md5",
                @"D:\fw\notes.txt",
            });
            Assert.Equal(new[] { FirmwarePart.AP, FirmwarePart.BL, FirmwarePart.CP, FirmwarePart.CSC, FirmwarePart.HomeCsc },
                set.Files.Select(f => f.Part));
            Assert.Equal("SM-S918B", set.Model);
            Assert.Equal(3, set.Build.Binary);
            Assert.Equal("OXM", set.CscCode);
            Assert.Equal(14, set.Android);
            Assert.Empty(set.Mixed);
            Assert.Single(set.Unknown);
        }

        [Fact]
        public void Parts_of_two_firmwares_are_called_mixed()
        {
            FirmwareSet set = FirmwareSet.FromPaths(new[]
            {
                SamsungFixtures.ApName,
                "BL_S918BXXU2BWF1_S918BXXU2BWF1_MQB0_REV00_user_low_ship_MULTI_CERT.tar.md5",
                "CSC_OXM_S911BOXM3BWK5_MQB0_REV00_user_low_ship_MULTI_CERT.tar.md5",
            });
            Assert.Equal(2, set.Mixed.Count);
        }

        // ---------------------------------------------------------------- firmware against phone

        static FirmwareSet S918B() => FirmwareSet.FromPaths(new[]
        {
            SamsungFixtures.ApName,
            "BL_S918BXXU3BWK5_S918BXXU3BWK5_MQB71230440_REV00_user_low_ship_MULTI_CERT.tar.md5",
            "HOME_CSC_OXM_S918BOXM3BWK5_MQB71230440_REV00_user_low_ship_MULTI_CERT.tar.md5",
        });

        static SamsungPhone Phone(string model = "SM-S918B", string bootloader = "S918BXXU3BWJ1", string android = "14",
            string csc = "S918BOXM3BWJ1", string maker = "samsung") =>
            SamsungPhone.Parse("R5CW", "maker=" + maker + "\nmodel=" + model + "\nbootloader=" + bootloader + "\npda=\nandroid=" + android
                + "\nsales=KSA\ncscver=" + csc + "\n");

        [Fact]
        public void The_right_firmware_for_the_phone_passes()
        {
            List<MatchFinding> f = FirmwareMatch.Check(S918B(), Phone());
            Assert.Equal(MatchLevel.Ok, FirmwareMatch.Overall(f));
            Assert.Contains(f, x => x.Code == MatchCode.SameModel);
            Assert.Contains(f, x => x.Code == MatchCode.SameBinary);
            Assert.Contains(f, x => x.Code == MatchCode.SameCsc && x.Phone == "KSA (OXM)");
        }

        [Fact]
        public void An_older_bootloader_binary_is_refused_as_the_phone_would()
        {
            List<MatchFinding> f = FirmwareMatch.Check(S918B(), Phone(bootloader: "S918BXXU4CXA1"));
            MatchFinding older = Assert.Single(f, x => x.Code == MatchCode.OlderBinary);
            Assert.Equal(MatchLevel.Block, older.Level);
            Assert.Equal("3", older.Firmware);
            Assert.Equal("4", older.Phone);
        }

        [Fact]
        public void Another_model_or_maker_is_refused()
        {
            Assert.Equal(MatchLevel.Block, FirmwareMatch.Overall(FirmwareMatch.Check(S918B(), Phone(model: "SM-S918U"))));
            Assert.Contains(FirmwareMatch.Check(S918B(), Phone(maker: "INFINIX", model: "X6878")), x => x.Code == MatchCode.NotSamsung);
        }

        [Fact]
        public void A_newer_binary_is_noted_and_an_older_android_warned()
        {
            List<MatchFinding> f = FirmwareMatch.Check(S918B(), Phone(bootloader: "S918BXXU2BWF1", android: "15", csc: "S918BXSG2BWF1"));
            Assert.Equal(MatchLevel.Notice, f.Single(x => x.Code == MatchCode.NewerBinary).Level);
            Assert.Equal(MatchLevel.Warn, f.Single(x => x.Code == MatchCode.OlderAndroid).Level);
            Assert.Equal(MatchLevel.Notice, f.Single(x => x.Code == MatchCode.OtherCsc).Level);
        }

        [Fact]
        public void Without_a_phone_only_the_firmware_is_judged()
        {
            List<MatchFinding> f = FirmwareMatch.Check(S918B(), null);
            Assert.Contains(f, x => x.Code == MatchCode.PhoneUnknown);
            Assert.DoesNotContain(f, x => x.Level == MatchLevel.Block);
        }

        [Fact]
        public void The_full_csc_is_said_to_wipe_the_phone()
        {
            FirmwareSet set = FirmwareSet.FromPaths(new[] { SamsungFixtures.ApName, "CSC_OXM_S918BOXM3BWK5_MQB0_REV00_user_low_ship_MULTI_CERT.tar.md5" });
            Assert.Contains(FirmwareMatch.Check(set, Phone()), x => x.Code == MatchCode.CscWipes);
        }

        // ---------------------------------------------------------------- Magisk

        [Fact]
        public void The_magisk_tar_holds_init_boot_and_vbmeta_still_compressed()
        {
            string ap = WriteAp();
            List<TarEntry> entries;
            using (FileStream f = File.OpenRead(ap))
                entries = OdinTar.List(f);
            List<TarEntry> picked = FirmwareTools.MagiskEntries(entries);
            Assert.Equal(new[] { "init_boot.img.lz4", "vbmeta.img.lz4" }, picked.Select(e => e.Name));

            string small = Path.Combine(dir, "magisk-input.tar");
            FirmwareTools.CopyEntries(ap, picked, small);
            using (FileStream f = File.OpenRead(small))
                Assert.Equal(entries.Select(e => e.Size), OdinTar.List(f).Select(e => e.Size));
            string outDir = Path.Combine(dir, "again");
            using (FileStream f = File.OpenRead(small))
                foreach (TarEntry e in OdinTar.List(f))
                    FirmwareTools.Extract(small, e, outDir);
            Assert.Equal(SamsungFixtures.InitBootSha256, Sha256(Path.Combine(outDir, "init_boot.img")));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void A_patched_tar_is_checked_for_magisk(bool magisk, bool lz4)
        {
            byte[] kernel = Convert.FromBase64String(CompressedFixtures.Lz4Legacy);
            byte[] ramdisk = BootImageTests.Gzip(magisk
                ? BootImageTests.Cpio("init", ".backup/.magisk", ".backup/init", "overlay.d/sbin/magisk64.xz")
                : BootImageTests.Cpio("init", "system/bin/init"));
            byte[] image = BootImageTests.BootImage(2, kernel, ramdisk);
            byte[] content = lz4 ? StoredFrame(image) : image;
            string name = lz4 ? "boot.img.lz4" : "boot.img";
            File.WriteAllBytes(Path.Combine(dir, name), content);
            File.WriteAllBytes(Path.Combine(dir, "vbmeta.img"), new byte[4096]);
            string tar = Path.Combine(dir, "magisk_patched-28100_abcde.tar");
            FirmwareTools.BuildTar(new[] { Path.Combine(dir, name), Path.Combine(dir, "vbmeta.img") }, tar, withMd5: false);

            FirmwareTools.PatchedCheck check = FirmwareTools.CheckPatched(tar);
            Assert.Equal("boot.img", check.Image);
            Assert.Equal(magisk, check.Patched);
            Assert.True(check.HasVbmeta);
        }
    }
}
