using FastbootEnhance.Core.Fastboot;

namespace FastbootEnhance.Core.Tests
{
    public class FastbootdRefusalTests
    {
        // The OnePlus 11 log an XDA user posted (Oct 19, 2026).
        const string OnePlusModem =
            "Invalid sparse file format at header magic\n"
            + "Sending sparse 'modem_a' 1/1 (202578 KB)           OKAY [  5.237s]\n"
            + "Writing 'modem_a'                                  FAILED (remote: 'Operation not permitted')\n"
            + "fastboot: error: Command failed\n";

        [Fact]
        public void A_partition_fastbootd_refuses_waits_for_the_bootloader()
        {
            Assert.True(FastbootdRefusal.IsRefusal(OnePlusModem));
            Assert.True(FastbootdRefusal.CanDefer(true, false, OnePlusModem));
        }

        [Theory]
        // A logical partition only exists in fastbootd: nothing to wait for.
        [InlineData(true, true)]
        // Already in the bootloader: the refusal is final.
        [InlineData(false, false)]
        public void Logical_partitions_and_the_bootloader_never_defer(bool fastbootd, bool logical)
        {
            Assert.False(FastbootdRefusal.CanDefer(fastbootd, logical, OnePlusModem));
        }

        [Theory]
        [InlineData("Writing 'system_a'   FAILED (remote: 'Not enough space to resize partition')\n")]
        [InlineData("Sending 'boot_a' (196608 KB)   FAILED (Write to device failed (No such device))\nfastboot: error: Command failed\n")]
        [InlineData("fastboot: error: cannot load 'modem.img'\n")]
        [InlineData("Writing 'boot_a'   FAILED (remote: 'Flashing is not allowed in Lock State')\n")]
        [InlineData("")]
        public void Other_failures_still_stop_the_flash(string output)
        {
            Assert.False(FastbootdRefusal.CanDefer(true, false, output));
        }
    }
}
