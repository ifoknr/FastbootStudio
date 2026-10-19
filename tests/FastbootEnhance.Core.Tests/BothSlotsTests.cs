using System.Collections.Generic;
using FastbootEnhance.Core.Fastboot;

namespace FastbootEnhance.Core.Tests
{
    public class BothSlotsTests
    {
        const string Phone =
            "(bootloader) current-slot:a\n"
            + "(bootloader) is-userspace:yes\n"
            + "(bootloader) partition-size:modem_a:0xC800000\n"
            + "(bootloader) partition-size:modem_b:0xC800000\n"
            + "(bootloader) partition-size:boot_a:0x6000000\n"
            + "(bootloader) partition-size:boot_b:0x6000000\n"
            + "(bootloader) partition-size:system_a:0x5DC00000\n"
            + "(bootloader) partition-size:system_b:0x0\n"
            + "(bootloader) partition-size:persist:0x2000000\n"
            + "(bootloader) is-logical:modem_a:no\n"
            + "(bootloader) is-logical:modem_b:no\n"
            + "(bootloader) is-logical:boot_a:no\n"
            + "(bootloader) is-logical:system_a:yes\n"
            + "(bootloader) is-logical:system_b:yes\n";

        [Fact]
        public void Physical_ab_partitions_also_go_to_the_other_slot()
        {
            FastbootData data = new FastbootData(Phone);
            IReadOnlyDictionary<string, string> targets = BothSlots.OtherSlotTargets(data,
                new[] { "modem", "boot", "system", "persist", "dtbo" }, new HashSet<string> { "system" });

            Assert.Equal(2, targets.Count);
            Assert.Equal("modem_b", targets["modem"]);
            Assert.Equal("boot_b", targets["boot"]);
        }

        [Fact]
        public void A_partition_the_phone_reports_as_logical_stays_out_even_when_the_payload_does_not_say_so()
        {
            FastbootData data = new FastbootData(Phone);
            IReadOnlyDictionary<string, string> targets = BothSlots.OtherSlotTargets(data,
                new[] { "system" }, new HashSet<string>());
            Assert.Empty(targets);
        }

        [Fact]
        public void From_slot_b_the_other_slot_is_a()
        {
            FastbootData data = new FastbootData(Phone.Replace("current-slot:a", "current-slot:b"));
            IReadOnlyDictionary<string, string> targets = BothSlots.OtherSlotTargets(data,
                new[] { "modem" }, new HashSet<string>());
            Assert.Equal("modem_a", targets["modem"]);
        }

        [Fact]
        public void An_older_bootloader_that_says_underscore_a_works_too()
        {
            FastbootData data = new FastbootData(Phone.Replace("current-slot:a", "current-slot:_a"));
            Assert.Equal("modem_b", BothSlots.OtherSlotTargets(data, new[] { "modem" }, new HashSet<string>())["modem"]);
        }

        [Fact]
        public void A_phone_without_slots_gets_nothing()
        {
            FastbootData data = new FastbootData(Phone.Replace("(bootloader) current-slot:a\n", ""));
            Assert.Empty(BothSlots.OtherSlotTargets(data, new[] { "modem", "boot" }, new HashSet<string>()));
        }
    }
}
