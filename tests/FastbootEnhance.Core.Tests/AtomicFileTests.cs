using System;
using System.IO;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class AtomicFileTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "fbe-atomic-" + Guid.NewGuid().ToString("N"));

        public AtomicFileTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void A_committed_file_takes_its_name_and_replaces_the_old_one()
        {
            string target = Path.Combine(dir, "boot.img");
            File.WriteAllBytes(target, new byte[] { 9, 9, 9 });
            using (AtomicFile part = AtomicFile.Create(target))
            {
                Assert.Equal(target + ".part", part.PartPath);
                Assert.True(File.Exists(part.PartPath));
                part.Stream.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
                part.Commit();
            }
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target));
            Assert.False(File.Exists(target + ".part"));
        }

        [Fact]
        public void A_run_that_stops_half_way_leaves_no_file_under_the_final_name()
        {
            string target = Path.Combine(dir, "system.img");
            Action write = () =>
            {
                using (AtomicFile part = AtomicFile.Create(target))
                {
                    part.Stream.Write(new byte[1000], 0, 1000);
                    throw new InvalidDataException("damaged half way");
                }
            };
            Assert.Throws<InvalidDataException>(write);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }

        [Fact]
        public void An_older_file_stays_until_the_new_one_is_complete()
        {
            string target = Path.Combine(dir, "vbmeta.img");
            File.WriteAllBytes(target, new byte[] { 7 });
            using (AtomicFile part = AtomicFile.Create(target))
            {
                part.Stream.WriteByte(1);
                Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(target));
            }
            // Disposed without a commit: the old file is untouched.
            Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(target));
        }
    }
}
