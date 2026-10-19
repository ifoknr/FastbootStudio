using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FastbootEnhance.Core.Samsung
{
    /// <summary>One file inside a firmware tar.</summary>
    public sealed class TarEntry
    {
        public string Name { get; internal set; }
        public long Size { get; internal set; }
        /// <summary>Where its data starts in the tar file.</summary>
        public long DataOffset { get; internal set; }

        /// <summary>"boot.img.lz4" is boot.img compressed with LZ4.</summary>
        public bool Lz4 => Name.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase);

        /// <summary>The name once LZ4 is undone: "boot.img".</summary>
        public string PlainName => Lz4 ? Name.Substring(0, Name.Length - 4) : Name;
    }

    /// <summary>
    /// The tar files Samsung firmware comes in (AP, BL, CP, CSC, HOME_CSC). A ".tar.md5" is a
    /// plain tar with one more line at its end, "md5  name.tar", the MD5 of everything before
    /// that line; Odin checks it before flashing, and so does this app before reading.
    /// </summary>
    public static class OdinTar
    {
        const int Block = 512;

        /// <summary>The MD5 line at the end of a .tar.md5: the hash, and how many bytes it covers.</summary>
        public sealed class Md5Trailer
        {
            public string Md5 { get; internal set; }
            public string Name { get; internal set; }
            public long Covers { get; internal set; }
        }

        static readonly Regex TrailerLine = new Regex(@"([0-9a-fA-F]{32})[ \t]+\*?([^\r\n]+?)\s*$");

        /// <summary>The MD5 line at the end of the file, or null when it has none (a plain .tar).</summary>
        public static Md5Trailer ReadTrailer(Stream file)
        {
            long length = file.Length;
            int tail = (int)Math.Min(length, 1024);
            byte[] end = new byte[tail];
            file.Seek(length - tail, SeekOrigin.Begin);
            ReadExactly(file, end, 0, tail);
            // The line starts after the last zero byte of the tar's padding.
            int start = tail;
            while (start > 0 && end[start - 1] != 0)
                start--;
            string text = Encoding.ASCII.GetString(end, start, tail - start);
            Match m = TrailerLine.Match(text);
            if (!m.Success || m.Index != text.Length - text.TrimStart().Length)
                return null;
            return new Md5Trailer
            {
                Md5 = m.Groups[1].Value.ToLowerInvariant(),
                Name = m.Groups[2].Value.Trim(),
                Covers = length - (tail - start),
            };
        }

        /// <summary>
        /// Checks a .tar.md5 against its own MD5 line. Returns null when it matches, or what is
        /// wrong. <paramref name="progress"/> gets the bytes read so far.
        /// </summary>
        public static string VerifyMd5(string path, Action<long> progress = null, CancellationToken cancel = default)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                Md5Trailer trailer = ReadTrailer(file);
                if (trailer == null)
                    return "no MD5 line at the end of the file";
                file.Seek(0, SeekOrigin.Begin);
                using (MD5 md5 = MD5.Create())
                {
                    byte[] buffer = new byte[1 << 20];
                    long left = trailer.Covers, done = 0;
                    while (left > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (n <= 0)
                            return "the file ends early";
                        md5.TransformBlock(buffer, 0, n, null, 0);
                        left -= n;
                        done += n;
                        progress?.Invoke(done);
                    }
                    md5.TransformFinalBlock(buffer, 0, 0);
                    string actual = Hex(md5.Hash);
                    return actual == trailer.Md5 ? null : "MD5 is " + actual + ", the file says " + trailer.Md5;
                }
            }
        }

        /// <summary>The files in a tar (ustar or GNU, with long names and sizes over 8 GiB).</summary>
        public static List<TarEntry> List(Stream tar)
        {
            List<TarEntry> entries = new List<TarEntry>();
            byte[] header = new byte[Block];
            long pos = 0;
            string longName = null;
            long limit = tar.Length;
            while (pos + Block <= limit)
            {
                tar.Seek(pos, SeekOrigin.Begin);
                ReadExactly(tar, header, 0, Block);
                if (IsZero(header))
                    break;
                if (!ChecksumOk(header))
                {
                    if (entries.Count == 0)
                        throw new InvalidDataException("Not a tar file.");
                    break; // the MD5 line, or other trailing bytes
                }
                char type = (char)header[156];
                long size = Number(header, 124, 12);
                long data = pos + Block;
                long next = data + (size + Block - 1) / Block * Block;
                if (size < 0 || data + size > limit)
                    throw new InvalidDataException("The tar is cut short.");

                if (type == 'L')
                {
                    // GNU long name: the next entry's name is this entry's data.
                    byte[] name = new byte[size];
                    ReadExactly(tar, name, 0, (int)size);
                    longName = Text(name, 0, name.Length);
                }
                else if (type == 'x' || type == 'g')
                {
                    // pax headers: only "path" matters here.
                    byte[] pax = new byte[size];
                    ReadExactly(tar, pax, 0, (int)size);
                    Match p = Regex.Match(Encoding.UTF8.GetString(pax), @"\d+ path=([^\n]*)\n");
                    if (p.Success && type == 'x')
                        longName = p.Groups[1].Value;
                }
                else
                {
                    string name = longName ?? FullName(header);
                    longName = null;
                    if (type == '0' || type == '\0' || type == '7')
                        entries.Add(new TarEntry { Name = name, Size = size, DataOffset = data });
                }
                pos = next;
            }
            return entries;
        }

        /// <summary>A read-only view of one entry's bytes.</summary>
        public static Stream Open(Stream tar, TarEntry entry) => new Slice(tar, entry.DataOffset, entry.Size);

        static string FullName(byte[] h)
        {
            string name = Text(h, 0, 100);
            bool ustar = Encoding.ASCII.GetString(h, 257, 5) == "ustar";
            string prefix = ustar ? Text(h, 345, 155) : "";
            return prefix.Length > 0 ? prefix + "/" + name : name;
        }

        static string Text(byte[] b, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && b[end] != 0)
                end++;
            return Encoding.UTF8.GetString(b, offset, end - offset);
        }

        /// <summary>Octal, or GNU base-256 (high bit set) for sizes octal cannot hold.</summary>
        static long Number(byte[] h, int offset, int length)
        {
            if ((h[offset] & 0x80) != 0)
            {
                long v = h[offset] & 0x7F;
                for (int k = 1; k < length; k++)
                    v = (v << 8) | h[offset + k];
                return v;
            }
            int i = offset, end = offset + length;
            while (i < end && h[i] == ' ')
                i++;
            long n = 0;
            for (; i < end && h[i] != 0 && h[i] != ' '; i++)
            {
                if (h[i] < '0' || h[i] > '7')
                    throw new InvalidDataException("Not a tar number.");
                n = n * 8 + (h[i] - '0');
            }
            return n;
        }

        static bool ChecksumOk(byte[] h)
        {
            long stored;
            try
            {
                stored = Number(h, 148, 8);
            }
            catch (InvalidDataException)
            {
                return false;
            }
            long sum = 0;
            for (int i = 0; i < Block; i++)
                sum += i >= 148 && i < 156 ? ' ' : h[i];
            return sum == stored;
        }

        static bool IsZero(byte[] b)
        {
            foreach (byte x in b)
                if (x != 0)
                    return false;
            return true;
        }

        public static string Hex(byte[] hash)
        {
            StringBuilder s = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                s.Append(b.ToString("x2"));
            return s.ToString();
        }

        internal static void ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buffer, offset, count);
                if (n <= 0)
                    throw new InvalidDataException("The file ends early.");
                offset += n;
                count -= n;
            }
        }

        sealed class Slice : Stream
        {
            readonly Stream inner;
            readonly long start, length;
            long position;

            public Slice(Stream inner, long start, long length)
            {
                this.inner = inner;
                this.start = start;
                this.length = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => position; set => position = Math.Max(0, Math.Min(length, value)); }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                long left = length - position;
                if (left <= 0)
                    return 0;
                inner.Seek(start + position, SeekOrigin.Begin);
                int n = inner.Read(buffer, offset, (int)Math.Min(count, left));
                position += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                Position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : length + offset;
                return position;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Writes a tar Odin accepts: ustar headers, files in the order given, two empty blocks at
    /// the end. With <c>md5</c>, the result is a .tar.md5 with its MD5 line, like Samsung's.
    /// </summary>
    public sealed class OdinTarWriter : IDisposable
    {
        const int Block = 512;
        readonly Stream output;
        readonly MD5 md5 = MD5.Create();
        readonly string md5Name;
        bool finished;

        /// <param name="md5Name">The tar's name for the MD5 line ("x.tar"); null writes a plain .tar.</param>
        public OdinTarWriter(Stream output, string md5Name = null)
        {
            this.output = output;
            this.md5Name = md5Name;
        }

        public void Add(string name, Stream content, long size, CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(name) || Encoding.UTF8.GetByteCount(name) > 99 || name.Contains("/") || name.Contains("\\"))
                throw new ArgumentException("A tar entry name must be a plain file name of up to 99 bytes: " + name);
            byte[] header = new byte[Block];
            Encoding.UTF8.GetBytes(name, 0, name.Length, header, 0);
            Octal(header, 100, 8, Convert.ToInt64("644", 8));
            Octal(header, 108, 8, 0);
            Octal(header, 116, 8, 0);
            if (size < 077777777777L)
            {
                Octal(header, 124, 12, size);
            }
            else
            {
                // GNU base-256 for files of 8 GiB and more.
                header[124] = 0x80;
                long v = size;
                for (int i = 135; i > 124; i--, v >>= 8)
                    header[i] = (byte)(v & 0xFF);
            }
            Octal(header, 136, 12, 0);
            header[156] = (byte)'0';
            Encoding.ASCII.GetBytes("ustar\0", 0, 6, header, 257);
            header[263] = (byte)'0';
            header[264] = (byte)'0';
            for (int i = 148; i < 156; i++)
                header[i] = (byte)' ';
            long sum = 0;
            foreach (byte b in header)
                sum += b;
            Octal(header, 148, 7, sum);
            header[155] = (byte)' ';
            Write(header, 0, Block);

            byte[] buffer = new byte[1 << 20];
            long left = size;
            while (left > 0)
            {
                cancel.ThrowIfCancellationRequested();
                int n = content.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0)
                    throw new IOException("The file for " + name + " ended early.");
                Write(buffer, 0, n);
                left -= n;
            }
            int pad = (int)((Block - size % Block) % Block);
            if (pad > 0)
                Write(new byte[pad], 0, pad);
        }

        /// <summary>Ends the tar, and writes its MD5 line when asked to.</summary>
        public void Finish()
        {
            if (finished)
                return;
            finished = true;
            Write(new byte[Block * 2], 0, Block * 2);
            md5.TransformFinalBlock(new byte[0], 0, 0);
            if (md5Name != null)
            {
                byte[] line = Encoding.ASCII.GetBytes(OdinTar.Hex(md5.Hash) + "  " + md5Name + "\n");
                output.Write(line, 0, line.Length);
            }
            output.Flush();
        }

        void Write(byte[] b, int offset, int count)
        {
            output.Write(b, offset, count);
            md5.TransformBlock(b, offset, count, null, 0);
        }

        static void Octal(byte[] h, int offset, int length, long value)
        {
            string s = Convert.ToString(value, 8).PadLeft(length - 1, '0');
            Encoding.ASCII.GetBytes(s, 0, s.Length, h, offset);
            h[offset + length - 1] = 0;
        }

        public void Dispose()
        {
            md5.Dispose();
        }
    }
}
