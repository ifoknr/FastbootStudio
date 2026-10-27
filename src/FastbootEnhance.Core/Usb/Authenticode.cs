using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace FastbootEnhance.Core.Usb
{
    /// <summary>Windows' own check of a file's Authenticode signature (WinVerifyTrust). Windows only.</summary>
    public static class Authenticode
    {
        /// <summary>
        /// Asks Windows whether the file carries a valid Authenticode signature from a trusted
        /// publisher, and returns the signer's certificate subject, one attribute per line
        /// ("O=Samsung Electronics CO., LTD."), without quotes; null when it does not.
        /// The check reads the file through <paramref name="locked"/>, which the caller keeps open
        /// without write or delete sharing, so what was checked is what later runs.
        /// Revocation is not checked online, so this works without reaching the CA.
        /// </summary>
        public static string VerifiedSigner(FileStream locked)
        {
            if (WinTrust.Verify(locked.Name, locked.SafeFileHandle.DangerousGetHandle()) != 0)
                return null;
            try
            {
#pragma warning disable SYSLIB0057
                using (X509Certificate signer = X509Certificate.CreateFromSignedFile(locked.Name))
#pragma warning restore SYSLIB0057
                using (X509Certificate2 cert = new X509Certificate2(signer))
                    return cert.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines
                        | X500DistinguishedNameFlags.DoNotUseQuotes | X500DistinguishedNameFlags.DoNotUsePlusSign);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return null;
            }
        }

        /// <summary>The same, for a file nobody else could change (tests and tools).</summary>
        public static string VerifiedSigner(string path)
        {
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return VerifiedSigner(locked);
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
            public static int Verify(string path, IntPtr handle)
            {
                WINTRUST_FILE_INFO file = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = path,
                    hFile = handle,
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
