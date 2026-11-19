using FastbootEnhance.Core.Fastboot;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class FastbootDataTests
    {
        // Shaped like real "fastboot getvar all" output from fastbootd, which goes to stderr.
        const string Fastbootd = @"(bootloader) is-userspace:yes
(bootloader) product:sample_a64
(bootloader) secure:yes
(bootloader) unlocked:yes
(bootloader) current-slot:a
(bootloader) max-download-size:0x10000000
(bootloader) snapshot-update-status:none
(bootloader) partition-size:system_a:0x5DC00000
(bootloader) partition-size:boot_a:0x4000000
(bootloader) partition-size:super:0x240000000
(bootloader) is-logical:system_a:yes
(bootloader) is-logical:boot_a:no
(bootloader) is-logical:super:no
all:
Finished. Total time: 0.050s
";

        [Fact]
        public void Reads_the_common_variables()
        {
            FastbootData data = new FastbootData(Fastbootd);

            Assert.True(data.fastbootd);
            Assert.Equal("sample_a64", data.product);
            Assert.True(data.secure);
            Assert.True(data.unlocked);
            Assert.Equal("a", data.current_slot);
            Assert.Equal(0x10000000, data.max_download_size);
            Assert.Equal("none", data.snapshot_update_status);
        }

        [Fact]
        public void Reads_partition_sizes_and_logical_flags()
        {
            FastbootData data = new FastbootData(Fastbootd);

            Assert.Equal(0x5DC00000, data.partition_size["system_a"]);
            Assert.Equal(0x4000000, data.partition_size["boot_a"]);
            Assert.Equal(0x240000000, data.partition_size["super"]);
            Assert.True(data.partition_is_logical["system_a"]);
            Assert.False(data.partition_is_logical["boot_a"]);
        }

        [Fact]
        public void Handles_bootloader_style_spacing_and_crlf()
        {
            FastbootData data = new FastbootData(
                "(bootloader) partition-size:userdata: 0x1BC4000000\r\n" +
                "(bootloader) is-userspace: no\r\n" +
                "(bootloader) unlocked: no\r\n");

            Assert.Equal(0x1BC4000000, data.partition_size["userdata"]);
            Assert.False(data.fastbootd);
            Assert.False(data.unlocked);
        }

        [Theory]
        [InlineData("(bootloader) product:")]
        [InlineData("(bootloader) partition-size:system_a:")]
        [InlineData("(bootloader) is-logical:system_a")]
        [InlineData("(bootloader)")]
        [InlineData("(bootloader) partition-size:system_a: not-a-number")]
        [InlineData("garbage without a prefix")]
        public void Malformed_lines_are_skipped_instead_of_throwing(string line)
        {
            FastbootData data = new FastbootData(line + "\n(bootloader) product:still_read\n");

            Assert.Equal("still_read", data.product);
        }

        [Fact]
        public void An_unparseable_size_is_kept_as_unknown()
        {
            FastbootData data = new FastbootData("(bootloader) partition-size:odd_a: zzz\n");

            Assert.Equal(-1, data.partition_size["odd_a"]);
        }

        [Fact]
        public void A_repeated_partition_keeps_the_last_value()
        {
            FastbootData data = new FastbootData(
                "(bootloader) partition-size:boot_a:0x1000\n(bootloader) partition-size:boot_a:0x2000\n");

            Assert.Equal(0x2000, data.partition_size["boot_a"]);
        }

        [Fact]
        public void Lock_state_is_unknown_when_the_device_does_not_say()
        {
            FastbootData data = new FastbootData("(bootloader) product:x\n");

            Assert.Null(data.unlocked);
        }

        [Fact]
        public void Empty_output_gives_empty_data()
        {
            FastbootData data = new FastbootData("");

            Assert.Empty(data.partition_size);
            Assert.Null(data.product);
            Assert.Null(new FastbootData(null).product);
        }

        [Theory]
        [InlineData("none", false)]
        [InlineData("snapshotted", true)]
        [InlineData("merging", true)]
        public void Pending_update_follows_the_snapshot_status(string status, bool pending)
        {
            FastbootData data = new FastbootData("(bootloader) snapshot-update-status:" + status + "\n");

            Assert.Equal(pending, data.HasPendingUpdate);
        }

        [Fact]
        public void A_slot_said_with_an_underscore_is_the_same_slot()
        {
            // Older bootloaders answer "_a"; every check here builds "boot" + "_" + slot from it.
            FastbootData data = new FastbootData(
                "(bootloader) current-slot:_b\n(bootloader) partition-size:boot_b:0x4000000\n");

            Assert.Equal("b", data.current_slot);
            Assert.True(MissingPartitions.Find(data, new[] { "boot" }, new string[0]).None);
        }

        [Theory]
        [InlineData("0x10000000", 0x10000000L)]
        [InlineData("268435456", 268435456L)]
        [InlineData("1000000A", 0x1000000AL)]
        [InlineData("soon", -1L)]
        public void The_download_size_is_decimal_without_0x_as_fastboot_reads_it(string said, long size)
        {
            Assert.Equal(size, new FastbootData("(bootloader) max-download-size: " + said + "\n").max_download_size);
        }

        [Theory]
        [InlineData("0xFFFFFFFFFFFFFFFF", long.MaxValue)]
        [InlineData("0x8000000000000000", long.MaxValue)]
        [InlineData("FFFFFFFFFFFFFFFF", long.MaxValue)]
        [InlineData("0x7FFFFFFFFFFFFFFF", long.MaxValue)]
        [InlineData("0x1FFFFFFFFFFFFFFFF", -1L)]
        [InlineData("0x-1", -1L)]
        public void A_size_with_its_top_bit_set_is_large_not_negative(string said, long size)
        {
            FastbootData data = new FastbootData(
                "(bootloader) partition-size:userdata: " + said + "\n(bootloader) max-download-size: " + said + "\n");
            Assert.Equal(size, data.partition_size["userdata"]);
            Assert.Equal(size, data.max_download_size);
        }

        [Fact]
        public void No_snapshot_status_means_nothing_pending()
        {
            Assert.False(new FastbootData("(bootloader) product:x\n").HasPendingUpdate);
        }

        [Fact]
        public void Leftover_cow_partitions_are_noticed()
        {
            Assert.True(new FastbootData("(bootloader) partition-size:system_a-cow:0x1000\n").HasCowPartitions);
            Assert.False(new FastbootData(Fastbootd).HasCowPartitions);
        }
    }
}
