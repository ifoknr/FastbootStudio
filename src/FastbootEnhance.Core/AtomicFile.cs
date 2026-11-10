using System;
using System.IO;

namespace FastbootEnhance.Core
{
    /// <summary>
    /// A file written beside its final name (".part") and moved over it only when complete, so a
    /// run that stops half way (an error, a cancel, the computer going off) never leaves a file
    /// that looks finished. Dispose without <see cref="Commit"/> deletes the part.
    /// </summary>
    public sealed class AtomicFile : IDisposable
    {
        public const string Suffix = ".part";

        readonly string target;
        bool committed;

        /// <summary>The stream being written; it is closed by <see cref="Commit"/> or Dispose.</summary>
        public FileStream Stream { get; private set; }

        /// <summary>The name the part is written under until it is committed.</summary>
        public string PartPath => target + Suffix;

        AtomicFile(string target, FileStream stream)
        {
            this.target = target;
            Stream = stream;
        }

        /// <summary>Opens <paramref name="path"/>'s part for writing; the folder must exist.</summary>
        public static AtomicFile Create(string path, FileAccess access = FileAccess.Write, int bufferSize = 1 << 20)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("a file name is needed", nameof(path));
            if (access == FileAccess.Read)
                access = FileAccess.ReadWrite;
            string part = path + Suffix;
            FileStream stream = new FileStream(part, FileMode.Create, access, FileShare.None, bufferSize);
            return new AtomicFile(path, stream);
        }

        /// <summary>Closes the stream and puts the part in the target's place, replacing an older target.</summary>
        public void Commit()
        {
            if (committed)
                return;
            Stream.Dispose();
            if (File.Exists(target))
                File.Delete(target);
            File.Move(PartPath, target);
            committed = true;
        }

        public void Dispose()
        {
            if (committed)
                return;
            try
            {
                Stream.Dispose();
            }
            catch (IOException)
            {
            }
            TryDelete(PartPath);
        }

        /// <summary>Deletes a file if it is there, saying nothing when it cannot.</summary>
        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
