using System;
using System.IO;
using System.Threading;
using FastbootEnhance.Core.Images;

namespace FastbootEnhance.Core.Samsung
{
    /// <summary>
    /// Decodes LZ4 frames from a stream to a stream, block by block, so a 9 GB super.img.lz4
    /// never has to fit in memory. Checks what the frame itself records: the header checksum,
    /// block checksums, the content size and the content checksum, when present.
    /// </summary>
    public static class Lz4Stream
    {
        const int Window = 64 * 1024;

        /// <summary>
        /// Decodes every frame in <paramref name="input"/> (Samsung images are one frame; lz4
        /// can write several in a row) and returns the bytes written. <paramref name="progress"/>
        /// gets the compressed bytes read so far.
        /// </summary>
        public static long Decode(Stream input, Stream output, Action<long> progress = null, CancellationToken cancel = default)
        {
            long read = 0, written = 0;
            byte[] word = new byte[4];
            bool first = true;
            while (true)
            {
                int got = ReadUpTo(input, word, 4);
                read += got;
                if (got == 0 && !first)
                    return written;
                if (got < 4)
                    throw new InvalidDataException("Not an LZ4 frame.");
                uint magic = BitConverter.ToUInt32(word, 0);
                if ((magic & 0xFFFFFFF0u) == 0x184D2A50u)
                {
                    // A skippable frame: its length, then data to ignore.
                    ReadExactly(input, word, 4);
                    uint skip = BitConverter.ToUInt32(word, 0);
                    Skip(input, skip);
                    read += 4 + skip;
                    first = false;
                    continue;
                }
                if (magic != Lz4.FrameMagic)
                {
                    if (first)
                        throw new InvalidDataException("Not an LZ4 frame.");
                    throw new InvalidDataException("Unexpected data after the LZ4 frame.");
                }
                first = false;
                written += Frame(input, output, ref read, progress, cancel);
            }
        }

        static long Frame(Stream input, Stream output, ref long read, Action<long> progress, CancellationToken cancel)
        {
            byte[] head = new byte[2];
            ReadExactly(input, head, 2);
            int flags = head[0], bd = head[1];
            if ((flags >> 6) != 1)
                throw new InvalidDataException("Unsupported LZ4 frame version.");
            bool independent = (flags & 0x20) != 0;
            bool blockChecksum = (flags & 0x10) != 0;
            bool hasSize = (flags & 0x08) != 0;
            bool contentChecksum = (flags & 0x04) != 0;
            bool hasDict = (flags & 0x01) != 0;
            int blockMax = 1 << (8 + 2 * ((bd >> 4) & 7));
            if (((bd >> 4) & 7) < 4)
                throw new InvalidDataException("Unsupported LZ4 block size.");

            byte[] descriptor = new byte[2 + (hasSize ? 8 : 0) + (hasDict ? 4 : 0)];
            descriptor[0] = head[0];
            descriptor[1] = head[1];
            ReadExactly(input, descriptor, 2, descriptor.Length - 2);
            int hc = input.ReadByte();
            if (hc < 0)
                throw new InvalidDataException("LZ4 frame header is cut short.");
            if ((byte)(XxHash32.Hash(descriptor, 0, descriptor.Length) >> 8) != hc)
                throw new InvalidDataException("LZ4 frame header checksum does not match.");
            if (hasDict)
                throw new InvalidDataException("LZ4 frames with a dictionary are not supported.");
            long expected = hasSize ? BitConverter.ToInt64(descriptor, 2) : -1;
            read += descriptor.Length - 2 + 1;

            byte[] compressed = new byte[blockMax];
            byte[] window = new byte[Window + blockMax];
            int history = 0;
            byte[] word = new byte[4];
            XxHash32 content = contentChecksum ? new XxHash32() : null;
            long total = 0;

            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                ReadExactly(input, word, 4);
                read += 4;
                uint raw = BitConverter.ToUInt32(word, 0);
                if (raw == 0)
                    break;
                bool stored = (raw & 0x80000000u) != 0;
                int size = (int)(raw & 0x7FFFFFFF);
                if (size > blockMax)
                    throw new InvalidDataException("LZ4 block is larger than the frame allows.");
                ReadExactly(input, compressed, 0, size);
                read += size;
                if (blockChecksum)
                {
                    ReadExactly(input, word, 4);
                    read += 4;
                    if (BitConverter.ToUInt32(word, 0) != XxHash32.Hash(compressed, 0, size))
                        throw new InvalidDataException("LZ4 block checksum does not match.");
                }

                int start = history, end;
                if (stored)
                {
                    Buffer.BlockCopy(compressed, 0, window, start, size);
                    end = start + size;
                }
                else
                {
                    end = Lz4.DecodeBlock(compressed, 0, size, window, start, start + blockMax, independent ? start : 0);
                }
                int length = end - start;
                output.Write(window, start, length);
                content?.Append(window, start, length);
                total += length;
                progress?.Invoke(read);

                // Keep the last 64 KiB for the next block of a linked frame.
                if (!independent)
                {
                    int keep = Math.Min(Window, end);
                    Buffer.BlockCopy(window, end - keep, window, 0, keep);
                    history = keep;
                }
            }

            if (contentChecksum)
            {
                ReadExactly(input, word, 4);
                read += 4;
                if (BitConverter.ToUInt32(word, 0) != content.Digest())
                    throw new InvalidDataException("LZ4 content checksum does not match: the image is damaged.");
            }
            if (expected >= 0 && expected != total)
                throw new InvalidDataException("LZ4 frame holds " + total + " bytes, its header says " + expected + ".");
            return total;
        }

        static int ReadUpTo(Stream s, byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buffer, got, count - got);
                if (n <= 0)
                    break;
                got += n;
            }
            return got;
        }

        static void ReadExactly(Stream s, byte[] buffer, int count) => ReadExactly(s, buffer, 0, count);

        static void ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buffer, offset, count);
                if (n <= 0)
                    throw new InvalidDataException("The LZ4 data is cut short.");
                offset += n;
                count -= n;
            }
        }

        static void Skip(Stream s, long count)
        {
            byte[] buffer = new byte[81920];
            while (count > 0)
            {
                int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (n <= 0)
                    throw new InvalidDataException("The LZ4 data is cut short.");
                count -= n;
            }
        }
    }
}
