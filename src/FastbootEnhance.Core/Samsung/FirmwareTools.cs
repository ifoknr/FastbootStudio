using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FastbootEnhance.Core.Images;

namespace FastbootEnhance.Core.Samsung
{
    /// <summary>
    /// Takes images out of Samsung firmware and builds the tars Odin and Magisk use. Nothing here
    /// talks to a phone.
    /// </summary>
    public static class FirmwareTools
    {
        /// <summary>Where the small tar goes on the phone, and where Magisk writes magisk_patched-….tar.</summary>
        public const string PhoneFolder = "/sdcard/Download";

        /// <summary>Lists Magisk's patched tars as "mtime|size|path" for RootHelper.FindNewest.</summary>
        public const string ListPatchedCommand = "stat -c %Y\\|%s\\|%n -- " + PhoneFolder + "/magisk_patched*.tar 2>/dev/null";

        /// <summary>
        /// Saves one entry as a plain file in <paramref name="folder"/>, undoing LZ4 on the way
        /// ("boot.img.lz4" becomes "boot.img"). Written next to its final name first, so a cut-short
        /// run leaves nothing that looks finished. Returns the file written.
        /// </summary>
        public static string Extract(string tarPath, TarEntry entry, string folder, Action<long> progress = null,
            CancellationToken cancel = default)
        {
            Directory.CreateDirectory(folder);
            string name = Path.GetFileName(entry.PlainName);
            if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                throw new InvalidDataException("An entry without a usable name: " + entry.Name);
            string target = Path.Combine(folder, name);
            string part = target + ".part";
            try
            {
                using (FileStream tar = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (Stream source = OdinTar.Open(tar, entry))
                using (FileStream output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    if (entry.Lz4)
                    {
                        Lz4Stream.Decode(new BufferedStream(source, 1 << 20), output, progress, cancel);
                    }
                    else
                    {
                        byte[] buffer = new byte[1 << 20];
                        long done = 0;
                        int n;
                        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, n);
                            done += n;
                            progress?.Invoke(done);
                        }
                    }
                }
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(part, target);
                return target;
            }
            catch
            {
                try
                {
                    File.Delete(part);
                }
                catch (IOException)
                {
                }
                throw;
            }
        }

        /// <summary>
        /// The images Magisk needs from an AP, in the order it reads them: init_boot (or boot on
        /// phones without one) and vbmeta. Null when the AP has neither boot image.
        /// </summary>
        public static List<TarEntry> MagiskEntries(IList<TarEntry> ap)
        {
            TarEntry find(string name) => ap.FirstOrDefault(e => string.Equals(e.PlainName, name, StringComparison.OrdinalIgnoreCase));
            TarEntry boot = find("init_boot.img") ?? find("boot.img");
            if (boot == null)
                return null;
            List<TarEntry> picked = new List<TarEntry> { boot };
            TarEntry vbmeta = find("vbmeta.img");
            if (vbmeta != null)
                picked.Add(vbmeta);
            return picked;
        }

        /// <summary>
        /// Copies <paramref name="entries"/> of a tar, as they are (still LZ4), into a new small tar:
        /// what Magisk patches instead of the whole 8 GB AP.
        /// </summary>
        public static void CopyEntries(string tarPath, IEnumerable<TarEntry> entries, string outPath, CancellationToken cancel = default)
        {
            string part = outPath + ".part";
            try
            {
                using (FileStream tar = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (FileStream output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (OdinTarWriter writer = new OdinTarWriter(output))
                {
                    foreach (TarEntry e in entries)
                        using (Stream s = OdinTar.Open(tar, e))
                            writer.Add(Path.GetFileName(e.Name), s, e.Size, cancel);
                    writer.Finish();
                }
                if (File.Exists(outPath))
                    File.Delete(outPath);
                File.Move(part, outPath);
            }
            catch
            {
                try
                {
                    File.Delete(part);
                }
                catch (IOException)
                {
                }
                throw;
            }
        }

        /// <summary>
        /// A tar Odin flashes in its AP slot, built from image files (recovery.img for TWRP, a
        /// patched boot…). Each file keeps its own name, which tells Odin the partition.
        /// </summary>
        public static void BuildTar(IEnumerable<string> files, string outPath, bool withMd5, CancellationToken cancel = default)
        {
            string part = outPath + ".part";
            try
            {
                using (FileStream output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (OdinTarWriter writer = new OdinTarWriter(output, withMd5 ? TarName(outPath) : null))
                {
                    foreach (string f in files)
                        using (FileStream s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                            writer.Add(Path.GetFileName(f), s, s.Length, cancel);
                    writer.Finish();
                }
                if (File.Exists(outPath))
                    File.Delete(outPath);
                File.Move(part, outPath);
            }
            catch
            {
                try
                {
                    File.Delete(part);
                }
                catch (IOException)
                {
                }
                throw;
            }
        }

        /// <summary>"x.tar" for "x.tar.md5": the name an MD5 line carries.</summary>
        static string TarName(string path)
        {
            string name = Path.GetFileName(path);
            return name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        public sealed class PatchedCheck
        {
            /// <summary>"init_boot.img" or "boot.img", as found in the tar.</summary>
            public string Image { get; internal set; }
            public RootKind Root { get; internal set; }
            public bool HasVbmeta { get; internal set; }
            public bool Patched => (Root & RootKind.Magisk) != 0;
        }

        /// <summary>
        /// Reads the tar Magisk wrote (magisk_patched-….tar) and says whether its boot image
        /// carries Magisk. Null when the tar has no boot image at all.
        /// </summary>
        public static PatchedCheck CheckPatched(string tarPath)
        {
            using (FileStream tar = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                List<TarEntry> entries = OdinTar.List(tar);
                TarEntry boot = entries.FirstOrDefault(e => string.Equals(e.PlainName, "init_boot.img", StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault(e => string.Equals(e.PlainName, "boot.img", StringComparison.OrdinalIgnoreCase));
                if (boot == null)
                    return null;
                using (Stream raw = OdinTar.Open(tar, boot))
                using (MemoryStream image = new MemoryStream())
                {
                    if (boot.Lz4)
                        Lz4Stream.Decode(raw, new Limited(image, BootImageAnalysis.MaxUnpacked));
                    else
                        raw.CopyTo(new Limited(image, BootImageAnalysis.MaxUnpacked));
                    image.Position = 0;
                    BootImageAnalysis analysis = BootImageAnalysis.Analyze(image);
                    return new PatchedCheck
                    {
                        Image = boot.PlainName,
                        Root = analysis.Root,
                        HasVbmeta = entries.Any(e => string.Equals(e.PlainName, "vbmeta.img", StringComparison.OrdinalIgnoreCase)),
                    };
                }
            }
        }

        /// <summary>Refuses to grow past a limit, so a damaged file cannot fill memory.</summary>
        sealed class Limited : Stream
        {
            readonly Stream inner;
            readonly long max;

            public Limited(Stream inner, long max)
            {
                this.inner = inner;
                this.max = max;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (inner.Length + count > max)
                    throw new InvalidDataException("The boot image is larger than any real one.");
                inner.Write(buffer, offset, count);
            }
        }
    }
}
