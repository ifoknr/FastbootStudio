using System;
using System.Collections.Generic;
using System.Linq;

namespace FastbootEnhance.Core.Fastboot
{
    /// <summary>
    /// "Flash both slots": an OTA is written to the slot in use, and with this option each
    /// physical A/B partition (modem, boot, vbmeta…) is also written to the other slot, so a
    /// phone that falls back to that slot finds the same firmware there. Logical partitions in
    /// super are left out: a copy of system or vendor in the other slot would take super space
    /// the update needs, and Android does not boot it from there anyway.
    /// </summary>
    public static class BothSlots
    {
        /// <summary>
        /// The other-slot name ("modem_b" when slot a is in use) of each partition that has
        /// one on this phone, in the order given. Empty when the phone is not A/B.
        /// </summary>
        public static IReadOnlyDictionary<string, string> OtherSlotTargets(FastbootData data,
            IEnumerable<string> partitions, ISet<string> logical)
        {
            Dictionary<string, string> targets = new Dictionary<string, string>(StringComparer.Ordinal);
            // Some older bootloaders say "_a".
            string current = data == null || data.current_slot == null ? null : data.current_slot.TrimStart('_');
            string other = Other(current);
            if (other == null)
                return targets;

            foreach (string name in partitions)
            {
                if (string.IsNullOrEmpty(name) || targets.ContainsKey(name))
                    continue;
                if (logical != null && logical.Contains(name))
                    continue;
                string target = name + "_" + other;
                // The phone must have that slot of the partition, and it must not be in super.
                if (!data.partition_size.ContainsKey(target))
                    continue;
                bool? isLogical;
                if (data.partition_is_logical.TryGetValue(target, out isLogical) && isLogical == true)
                    continue;
                bool? currentLogical;
                if (data.partition_is_logical.TryGetValue(name + "_" + current, out currentLogical) && currentLogical == true)
                    continue;
                targets[name] = target;
            }
            return targets;
        }

        /// <summary>"b" for "a", "a" for "b", null for anything else.</summary>
        public static string Other(string slot) =>
            slot == "a" ? "b" : slot == "b" ? "a" : null;
    }
}
