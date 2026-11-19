using System;
using System.Collections.Generic;
using System.Globalization;

namespace FastbootEnhance.Core.Fastboot
{
    /// <summary>
    /// Parsed output of "fastboot getvar all". Lines look like
    /// "(bootloader) partition-size:system_a: 0x5DC00000"; anything malformed is skipped,
    /// because vendor bootloaders print all sorts of extra lines.
    /// </summary>
    public class FastbootData
    {
        public Dictionary<string, long> partition_size;
        public Dictionary<string, bool?> partition_is_logical;
        public string product;
        public bool secure;
        public bool? unlocked;
        public string current_slot;
        public bool fastbootd;
        public long max_download_size;
        public string snapshot_update_status;

        /// <summary>The name of the super partition ("super-partition-name"); null when not reported.</summary>
        public string super_partition_name;

        /// <summary>Per slot ("a", "b"): has it booted successfully, is it marked unbootable, tries left.</summary>
        public Dictionary<string, bool> slot_successful;
        public Dictionary<string, bool> slot_unbootable;
        public Dictionary<string, int> slot_retry_count;

        public FastbootData(string real_raw_data)
        {
            partition_size = new Dictionary<string, long>();
            partition_is_logical = new Dictionary<string, bool?>();
            product = null;
            secure = false;
            unlocked = null;
            current_slot = null;
            fastbootd = false;
            max_download_size = -1;
            snapshot_update_status = null;
            slot_successful = new Dictionary<string, bool>();
            slot_unbootable = new Dictionary<string, bool>();
            slot_retry_count = new Dictionary<string, int>();

            if (real_raw_data == null)
                return;

            foreach (string line in real_raw_data.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] tmp = line.Split(new char[] { ' ', ':', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                // "(bootloader) key: value" at minimum.
                if (tmp.Length < 3 || !tmp[0].Contains("bootloader"))
                    continue;

                string key = tmp[1];

                switch (key)
                {
                    case "partition-size":
                        if (tmp.Length < 4)
                            break;
                        partition_size[tmp[2]] = parseHex(tmp[3]);
                        break;

                    case "is-logical":
                        if (tmp.Length < 4)
                        {
                            partition_is_logical[tmp[2]] = null;
                            break;
                        }
                        partition_is_logical[tmp[2]] = tmp[3] == "yes";
                        break;

                    case "product":
                        product = tmp[2];
                        break;

                    case "secure":
                        secure = tmp[2] == "yes";
                        break;

                    case "unlocked":
                        unlocked = tmp[2] == "yes";
                        break;

                    case "current-slot":
                        // Some older bootloaders say "_a"; fastboot itself drops the underscore.
                        current_slot = tmp[2].TrimStart('_');
                        break;

                    case "is-userspace":
                        fastbootd = tmp[2] == "yes";
                        break;

                    case "max-download-size":
                        max_download_size = parseSize(tmp[2]);
                        break;

                    case "snapshot-update-status":
                        snapshot_update_status = tmp[2];
                        break;

                    case "super-partition-name":
                        super_partition_name = tmp[2];
                        break;

                    // "(bootloader) slot-successful:a: yes"
                    case "slot-successful":
                        if (tmp.Length >= 4)
                            slot_successful[tmp[2]] = tmp[3] == "yes";
                        break;

                    case "slot-unbootable":
                        if (tmp.Length >= 4)
                            slot_unbootable[tmp[2]] = tmp[3] == "yes";
                        break;

                    case "slot-retry-count":
                        int retries;
                        if (tmp.Length >= 4 && int.TryParse(tmp[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out retries))
                            slot_retry_count[tmp[2]] = retries;
                        break;
                }
            }
        }

        /// <summary>True when a Virtual A/B update is staged or merging.</summary>
        public bool HasPendingUpdate =>
            snapshot_update_status != null && snapshot_update_status != "none";

        /// <summary>True when copy-on-write partitions from an earlier update are still present.</summary>
        public bool HasCowPartitions
        {
            get
            {
                foreach (string name in partition_size.Keys)
                {
                    if (name.EndsWith("cow", StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
        }

        /// <summary>Reads "0x1A2B" or a bare hex/decimal number; -1 when it is neither.</summary>
        static long parseHex(string raw)
        {
            string value = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw.Substring(2) : raw;
            // Unsigned: "0xFFFFFFFFFFFFFFFF" is a very large size (some bootloaders say it for
            // "unknown"), not -1 or another negative number; it is held at the largest long.
            ulong parsed;
            if (ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
                return parsed > long.MaxValue ? long.MaxValue : (long)parsed;
            return -1;
        }

        /// <summary>
        /// A size as fastboot itself reads max-download-size: hex with "0x", decimal without
        /// (older bootloaders print "268435456"); a bare number with hex letters is still hex.
        /// </summary>
        static long parseSize(string raw)
        {
            long parsed;
            if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return parseHex(raw);
        }
    }
}
