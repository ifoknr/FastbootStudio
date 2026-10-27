using FastbootEnhance.Core.Usb;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

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

        // ------------------------------------------------------------------ Authenticode

        /// <summary>
        /// Asks Windows whether the file carries a valid Authenticode signature from a trusted
        /// publisher, and returns the signer's certificate subject; null when it does not.
        /// Revocation is not checked online, so this works without reaching the CA.
        /// </summary>
        public static string VerifiedSigner(string path)
        {
            if (WinTrust.Verify(path) != 0)
                return null;
            try
            {
#pragma warning disable SYSLIB0057
                using (X509Certificate signer = X509Certificate.CreateFromSignedFile(path))
#pragma warning restore SYSLIB0057
                using (X509Certificate2 cert = new X509Certificate2(signer))
                    return cert.Subject;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return null;
            }
        }

        static class WinTrust
        {
            static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            const uint WTD_UI_NONE = 2;
            const uint WTD_REVOKE_NONE = 0;
            const uint WTD_CHOICE_FILE = 1;
            const uint WTD_STATEACTION_VERIFY = 1;
            const uint WTD_STATEACTION_CLOSE = 2;
            const uint WTD_REVOCATION_CHECK_NONE = 0x10;
            const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct WINTRUST_FILE_INFO
            {
                public uint cbStruct;
                [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct WINTRUST_DATA
            {
                public uint cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint dwUIChoice;
                public uint fdwRevocationChecks;
                public uint dwUnionChoice;
                public IntPtr pFile;
                public uint dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint dwProvFlags;
                public uint dwUIContext;
                public IntPtr pSignatureSettings;
            }

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
            static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

            /// <summary>0 when the signature is valid and trusted; an HRESULT otherwise.</summary>
            public static int Verify(string path)
            {
                WINTRUST_FILE_INFO file = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = path,
                };
                IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                try
                {
                    Marshal.StructureToPtr(file, filePtr, false);
                    WINTRUST_DATA data = new WINTRUST_DATA
                    {
                        cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                        dwUIChoice = WTD_UI_NONE,
                        fdwRevocationChecks = WTD_REVOKE_NONE,
                        dwUnionChoice = WTD_CHOICE_FILE,
                        pFile = filePtr,
                        dwStateAction = WTD_STATEACTION_VERIFY,
                        dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
                    };
                    int result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                    // The state Windows kept for the check is let go in every case.
                    data.dwStateAction = WTD_STATEACTION_CLOSE;
                    WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                    return result;
                }
                finally
                {
                    Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
                    Marshal.FreeHGlobal(filePtr);
                }
            }
        }
    }
}
