using System;
using System.IO;
using ChromeosUpdateEngine;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace FastbootEnhance.Core.Payload
{
    /// <summary>
    /// Decodes a single install operation's attached data blob. Every codec here is
    /// fully managed, so the app ships no native decompression libraries.
    /// </summary>
    internal static class OperationCodec
    {
        /// <summary>
        /// Decompresses <paramref name="srcLength"/> bytes of <paramref name="src"/> into
        /// <paramref name="dest"/>, which is reset first and left rewound.
        /// </summary>
        internal static void DecodeInto(
            InstallOperation.Types.Type type, byte[] src, int srcLength, MemoryStream dest, long limit = long.MaxValue)
        {
            dest.SetLength(0);

            switch (type)
            {
                case InstallOperation.Types.Type.Replace:
                    if (srcLength > limit)
                        throw new InvalidDataException("the data is larger than the blocks it is for");
                    dest.Write(src, 0, srcLength);
                    break;

                case InstallOperation.Types.Type.ReplaceBz:
                    using (MemoryStream raw = new MemoryStream(src, 0, srcLength, false))
                    using (BZip2Stream bz = new BZip2Stream(raw, CompressionMode.Decompress, false))
                    {
                        CopyBounded(bz, dest, limit);
                    }
                    break;

                case InstallOperation.Types.Type.ReplaceXz:
                    using (MemoryStream raw = new MemoryStream(src, 0, srcLength, false))
                    using (XZStream xz = new XZStream(raw))
                    {
                        CopyBounded(xz, dest, limit);
                    }
                    break;

                case InstallOperation.Types.Type.Zstd:
                    using (MemoryStream raw = new MemoryStream(src, 0, srcLength, false))
                    using (ZstdSharp.DecompressionStream zstd = new ZstdSharp.DecompressionStream(raw))
                    {
                        CopyBounded(zstd, dest, limit);
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(type), type, "not a data-carrying replace operation");
            }

            dest.Position = 0;
        }

        /// <summary>
        /// Copies until the end of <paramref name="from"/>, but gives up as soon as more than
        /// <paramref name="limit"/> bytes come out: a damaged or crafted blob would otherwise
        /// be inflated whole into memory before anyone compared it with its blocks.
        /// </summary>
        static void CopyBounded(Stream from, MemoryStream to, long limit)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > limit)
                    throw new InvalidDataException("the data decodes to more than the blocks it is for");
                to.Write(buffer, 0, n);
            }
        }
    }
}
