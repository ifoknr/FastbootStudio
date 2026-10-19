using System;
using System.Collections.Generic;
using System.Linq;

namespace FastbootEnhance.Core.Fastboot
{
    /// <summary>
    /// Some phones write a few firmware partitions from the bootloader only: in fastbootd a
    /// OnePlus 11 answers "flash modem" with FAILED (remote: 'Operation not permitted'). Such a
    /// partition is not a failed flash but one for later: the rest of the OTA is written in
    /// fastbootd, then the phone goes to the bootloader and that partition is written there,
    /// as the OEM flashing scripts do. Anything else that fails (a lost cable, a full super)
    /// still stops the flash.
    /// </summary>
    public static class FastbootdRefusal
    {
        // What fastbootd says when the partition has to come from the bootloader.
        static readonly string[] Phrases =
        {
            "operation not permitted",
            "not allowed in fastbootd",
            "flash from bootloader",
            "flashed in bootloader",
            "not supported in fastbootd",
            "use bootloader",
        };

        /// <summary>
        /// True when fastboot's output for a failed flash in fastbootd says the partition is
        /// refused there (rather than that the write itself failed).
        /// </summary>
        public static bool IsRefusal(string fastbootOutput)
        {
            if (string.IsNullOrEmpty(fastbootOutput))
                return false;
            foreach (string line in fastbootOutput.Split('\n'))
            {
                string l = line.ToLowerInvariant();
                if (l.Contains("failed (remote:") && Phrases.Any(l.Contains))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a partition that failed in fastbootd may wait for the bootloader: only when
        /// the phone was in fastbootd, the partition is not a logical one (those exist only in
        /// fastbootd), and the phone said it refuses it there.
        /// </summary>
        public static bool CanDefer(bool inFastbootd, bool logical, string fastbootOutput) =>
            inFastbootd && !logical && IsRefusal(fastbootOutput);
    }
}
