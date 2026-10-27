using FastbootEnhance.Core.Usb;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;

namespace FastbootEnhance
{
    /// <summary>
    /// Samsung's USB driver: whether it is installed, and the window that downloads it from
    /// Samsung, checks Samsung signed it and runs its installer. Download (Odin) mode needs it;
    /// it is the same driver Odin itself uses, so installing it breaks nothing.
    /// FASTBOOT_STUDIO_FAKE_SAMSUNG_DRIVER ("installed" or "missing") answers for tests.
    /// </summary>
    static class DriverUI
    {
        const string StandInVariable = "FASTBOOT_STUDIO_FAKE_SAMSUNG_DRIVER";

        /// <summary>True when Windows' driver store holds the Download-mode driver, or the installer is listed in Programs.</summary>
        public static bool SamsungInstalled()
        {
            string standIn = Environment.GetEnvironmentVariable(StandInVariable);
            if (!string.IsNullOrEmpty(standIn))
                return string.Equals(standIn.Trim(), "installed", StringComparison.OrdinalIgnoreCase);

            try
            {
                string store = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
                if (Directory.Exists(store) && Directory.EnumerateDirectories(store)
                        .Any(d => SamsungDriver.IsDriverStoreFolder(Path.GetFileName(d))))
                    return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }

            const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey list = root.OpenSubKey(uninstall))
                    {
                        if (list == null)
                            continue;
                        foreach (string name in list.GetSubKeyNames())
                        {
                            using (RegistryKey entry = list.OpenSubKey(name))
                            {
                                if (SamsungDriver.IsProgramName(entry?.GetValue("DisplayName") as string))
                                    return true;
                            }
                        }
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException)
                {
                }
            }
            return false;
        }

        /// <summary>Opens the download-and-install window; <paramref name="after"/> runs when it closes.</summary>
        public static void InstallSamsung(Action after = null)
        {
            DriverWindow.Show(after);
        }
    }
}
