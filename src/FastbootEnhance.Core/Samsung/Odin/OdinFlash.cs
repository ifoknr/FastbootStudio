using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>
    /// One file to flash: a plain image (boot.img, boot.img.lz4) or an entry of a firmware tar.
    /// LZ4 is decoded on the way to the phone, so <see cref="Size"/> is the decoded size.
    /// </summary>
    public sealed class OdinFlashSource
    {
        /// <summary>The file on disk: the image, or the tar holding it.</summary>
        public string Origin { get; internal set; }

        /// <summary>The name in the tar, or the file's name.</summary>
        public string Name { get; internal set; }

        public bool Lz4 { get; internal set; }

        /// <summary>The name the PIT knows it by: <see cref="Name"/> without a folder and without ".lz4".</summary>
        public string FileName
        {
            get
            {
                string name = Name.Substring(Name.LastIndexOfAny(new[] { '/', '\\' }) + 1);
                return Lz4 ? name.Substring(0, name.Length - 4) : name;
            }
        }

        /// <summary>The bytes the phone receives.</summary>
        public long Size { get; internal set; }

        /// <summary>The firmware part (BL, AP, CP, CSC, HOME_CSC) the tar is, when its name says.</summary>
        public FirmwarePart? Part { get; internal set; }

        /// <summary>
        /// The partition chosen by hand, for an image whose name the PIT does not know
        /// ("magisk_patched-28100_aB3dE.img" for BOOT); null to go by <see cref="FileName"/>.
        /// </summary>
        public string Target { get; set; }

        internal long DataOffset { get; set; } = -1;
        internal long StoredSize { get; set; }

        /// <summary>The bytes to send, decoded. The caller disposes it.</summary>
        public Stream Open()
        {
            FileStream file = new FileStream(Origin, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            Stream raw = DataOffset >= 0 ? OdinTar.Open(file, new TarEntry { Name = Name, Size = StoredSize, DataOffset = DataOffset }) : file;
            if (!Lz4)
                return new Owner(raw, file);
            return new DecodedStream(output => Lz4Stream.Decode(raw, output), file);
        }

        /// <summary>A stream that also closes the file under it.</summary>
        sealed class Owner : Stream
        {
            readonly Stream inner;
            readonly Stream file;
            public Owner(Stream inner, Stream file) { this.inner = inner; this.file = file; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    file.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// Bytes written by a decoder on its own thread, read here as they come: the decoder never
    /// gets more than a few blocks ahead, so a 9 GB super.img.lz4 is never held whole.
    /// </summary>
    sealed class DecodedStream : Stream
    {
        readonly BlockingCollection<byte[]> blocks = new BlockingCollection<byte[]>(8);
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly Thread thread;
        readonly Stream file;
        Exception failure;
        byte[] current;
        int position;

        public DecodedStream(Action<Stream> decode, Stream file)
        {
            this.file = file;
            thread = new Thread(() =>
            {
                try
                {
                    decode(new Writer(this));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    blocks.CompleteAdding();
                }
            }) { IsBackground = true, Name = "lz4 decode" };
            thread.Start();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (current == null || position >= current.Length)
            {
                if (!blocks.TryTake(out current, Timeout.Infinite))
                {
                    if (failure != null)
                        throw new InvalidDataException(failure.Message, failure);
                    return 0;
                }
                position = 0;
            }
            int n = Math.Min(count, current.Length - position);
            Buffer.BlockCopy(current, position, buffer, offset, n);
            position += n;
            return n;
        }

        sealed class Writer : Stream
        {
            readonly DecodedStream owner;
            public Writer(DecodedStream owner) { this.owner = owner; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count == 0)
                    return;
                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                owner.blocks.Add(copy, owner.stop.Token);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stop.Cancel();
                // Unblocks a decoder waiting for room.
                while (blocks.TryTake(out _)) { }
                thread.Join();
                file.Dispose();
                stop.Dispose();
                blocks.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Reads the chosen files before anything is sent: each .tar.md5 against its MD5 line, each
    /// tar's list of files, and each LZ4 image decoded once to learn its size and prove it
    /// whole. A damaged file is found here, never half-way through a flash.
    /// </summary>
    public static class OdinSources
    {
        /// <summary>
        /// The files to flash from <paramref name="paths"/> (.tar.md5, .tar, .img, .bin, .lz4…).
        /// <paramref name="progress"/> gets (what is being read, bytes done, bytes in all).
        /// Throws InvalidDataException naming the file that is damaged.
        /// </summary>
        public static List<OdinFlashSource> Read(IEnumerable<string> paths, Action<string, long, long> progress = null,
            CancellationToken cancel = default)
        {
            List<OdinFlashSource> sources = new List<OdinFlashSource>();
            foreach (string path in paths)
            {
                cancel.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                bool isTar = name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase);
                long length = new FileInfo(path).Length;
                if (!isTar)
                {
                    OdinFlashSource single = new OdinFlashSource
                    {
                        Origin = path,
                        Name = name,
                        Lz4 = name.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase),
                        StoredSize = length,
                        Size = length,
                    };
                    if (single.Lz4)
                        single.Size = decodedSize(single, (done) => progress?.Invoke(name, done, length), cancel);
                    sources.Add(single);
                    continue;
                }

                if (name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase))
                {
                    string wrong = OdinTar.VerifyMd5(path, done => progress?.Invoke(name, done, length), cancel);
                    if (wrong != null)
                        throw new InvalidDataException(name + ": " + wrong);
                }
                FirmwarePart? part = FirmwareFile.FromPath(path)?.Part;
                List<TarEntry> entries;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    entries = OdinTar.List(file);
                foreach (TarEntry entry in entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    OdinFlashSource source = new OdinFlashSource
                    {
                        Origin = path,
                        Name = entry.Name,
                        Lz4 = entry.Lz4,
                        StoredSize = entry.Size,
                        Size = entry.Size,
                        DataOffset = entry.DataOffset,
                        Part = part,
                    };
                    if (source.Lz4)
                    {
                        string what = name + " › " + entry.Name;
                        source.Size = decodedSize(source, done => progress?.Invoke(what, done, entry.Size), cancel);
                    }
                    sources.Add(source);
                }
            }
            return sources;
        }

        static long decodedSize(OdinFlashSource source, Action<long> progress, CancellationToken cancel)
        {
            using (FileStream file = new FileStream(source.Origin, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                Stream raw = source.DataOffset >= 0
                    ? OdinTar.Open(file, new TarEntry { Name = source.Name, Size = source.StoredSize, DataOffset = source.DataOffset })
                    : file;
                try
                {
                    return Lz4Stream.Decode(raw, Stream.Null, progress, cancel);
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException(source.Name + ": " + e.Message, e);
                }
            }
        }
    }

    public enum OdinCheckLevel
    {
        /// <summary>Said, nothing more.</summary>
        Info,

        /// <summary>Allowed after the person has read it.</summary>
        Warn,

        /// <summary>Nothing is flashed while this stands.</summary>
        Block,
    }

    public enum OdinCheckCode
    {
        /// <summary>Nothing to flash.</summary>
        Empty,

        /// <summary>A plain image the phone's PIT has no partition for (args: file).</summary>
        NotInPit,

        /// <summary>A firmware file the PIT has no partition for, left out as Odin leaves it out (args: file).</summary>
        Skipped,

        /// <summary>A PIT in the firmware: never flashed here, the partitions stay as they are (args: file).</summary>
        PitSkipped,

        /// <summary>Bigger than the partition can be (args: file, partition, size, most).</summary>
        TooBig,

        /// <summary>EFS and the like: the IMEI and calibration; never flashed here (args: partition).</summary>
        Protected,

        /// <summary>Two files for one partition (args: partition).</summary>
        Twice,

        /// <summary>CSC and HOME_CSC together (no args).</summary>
        BothCsc,

        /// <summary>The firmware is for another model (args: firmware model, phone model).</summary>
        OtherModel,

        /// <summary>The phone did not say its model, so the firmware cannot be matched to it (args: firmware model).</summary>
        ModelUnknown,

        /// <summary>Parts from different builds (args: the files).</summary>
        Mixed,

        /// <summary>A bootloader image chosen on its own (args: partition).</summary>
        Bootloader,

        /// <summary>A modem image chosen on its own (args: partition).</summary>
        Modem,

        /// <summary>CSC (not HOME_CSC) or a userdata image: the phone's data is erased (args: file).</summary>
        WipesData,
    }

    public sealed class OdinCheck
    {
        public OdinCheckLevel Level { get; internal set; }
        public OdinCheckCode Code { get; internal set; }
        public IReadOnlyList<string> Args { get; internal set; }

        public override string ToString() => Level + " " + Code + (Args.Count > 0 ? ": " + string.Join(", ", Args) : "");
    }

    public sealed class OdinFlashItem
    {
        public PitEntry Entry { get; internal set; }
        public OdinFlashSource Source { get; internal set; }
    }

    /// <summary>
    /// What a flash will write, checked against the phone's PIT and its model before anything
    /// is sent. Blocks stop it; warnings are shown to the person, who says yes or no.
    /// </summary>
    public sealed class OdinFlashPlan
    {
        /// <summary>Partitions that hold the phone's own identity (IMEI, keys, calibration): never written here.</summary>
        static readonly HashSet<string> ProtectedPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EFS", "SEC_EFS", "CPEFS", "STEADY", "PERSISTENT", "PERSIST", "KEYSTORAGE", "KEYREFUGE",
        };

        /// <summary>Partitions of the boot chain: a wrong one can leave the phone unable to start.</summary>
        static readonly HashSet<string> BootChain = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BOOTLOADER", "SBOOT", "ABL", "XBL", "XBL_CONFIG", "AOP", "TZ", "HYP", "UEFI", "KEYMASTER", "UP_PARAM", "PARAM",
        };

        /// <summary>The largest sector there is (UFS): no partition holds more than its block count times this.</summary>
        const long MaxSector = 4096;

        public IReadOnlyList<OdinFlashItem> Items { get; private set; }
        public IReadOnlyList<OdinCheck> Checks { get; private set; }

        /// <summary>The PIT the plan was checked against; the flash refuses a phone whose PIT differs.</summary>
        public byte[] PitData { get; private set; }

        public bool Blocked => Checks.Any(c => c.Level == OdinCheckLevel.Block);
        public long TotalBytes => Items.Sum(i => i.Source.Size);

        /// <summary>
        /// Checks <paramref name="sources"/> against the phone's PIT (<paramref name="pitData"/>,
        /// as a reading of the phone returned it) and its model (null when it did not say).
        /// Firmware files (BL, AP, CP, CSC) leave out entries the PIT does not know, as Odin
        /// does; a plain image the PIT does not know is refused unless a partition was chosen.
        /// </summary>
        public static OdinFlashPlan Build(IList<OdinFlashSource> sources, byte[] pitData, string phoneModel)
        {
            SamsungPit pit = SamsungPit.Parse(pitData);
            List<OdinCheck> checks = new List<OdinCheck>();
            List<OdinFlashItem> items = new List<OdinFlashItem>();
            void add(OdinCheckLevel level, OdinCheckCode code, params string[] args) =>
                checks.Add(new OdinCheck { Level = level, Code = code, Args = args });

            bool firmware = sources.Any(s => s.Part != null);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OdinFlashSource source in sources)
            {
                if (source.FileName.EndsWith(".pit", StringComparison.OrdinalIgnoreCase))
                {
                    add(OdinCheckLevel.Info, OdinCheckCode.PitSkipped, source.Name);
                    continue;
                }
                PitEntry entry = source.Target != null ? pit.ForPartition(source.Target) : pit.ForFile(source.FileName);
                if (entry == null)
                {
                    if (source.Part != null)
                        add(OdinCheckLevel.Warn, OdinCheckCode.Skipped, source.Name);
                    else
                        add(OdinCheckLevel.Block, OdinCheckCode.NotInPit, source.Target ?? source.Name);
                    continue;
                }
                if (ProtectedPartitions.Contains(entry.Partition))
                {
                    add(OdinCheckLevel.Block, OdinCheckCode.Protected, entry.Partition);
                    continue;
                }
                if (!seen.Add(entry.Partition))
                {
                    add(OdinCheckLevel.Block, OdinCheckCode.Twice, entry.Partition);
                    continue;
                }
                if (entry.BlockCount > 0 && source.Size > entry.BlockCount * MaxSector)
                    add(OdinCheckLevel.Block, OdinCheckCode.TooBig, source.Name, entry.Partition,
                        source.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        (entry.BlockCount * MaxSector).ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (source.Part == null && BootChain.Contains(entry.Partition))
                    add(OdinCheckLevel.Warn, OdinCheckCode.Bootloader, entry.Partition);
                if (source.Part == null && entry.IsModem)
                    add(OdinCheckLevel.Warn, OdinCheckCode.Modem, entry.Partition);
                if (string.Equals(entry.Partition, "USERDATA", StringComparison.OrdinalIgnoreCase))
                    add(OdinCheckLevel.Warn, OdinCheckCode.WipesData, source.Name);
                items.Add(new OdinFlashItem { Entry = entry, Source = source });
            }

            if (firmware)
            {
                List<string> tars = sources.Where(s => s.Part != null).Select(s => s.Origin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                FirmwareSet set = FirmwareSet.FromPaths(tars);
                bool csc = set.Get(FirmwarePart.CSC) != null, home = set.Get(FirmwarePart.HomeCsc) != null;
                if (csc && home)
                    add(OdinCheckLevel.Block, OdinCheckCode.BothCsc);
                else if (csc)
                    add(OdinCheckLevel.Warn, OdinCheckCode.WipesData, set.Get(FirmwarePart.CSC).FileName);
                if (set.Mixed.Count > 0)
                    add(OdinCheckLevel.Warn, OdinCheckCode.Mixed, set.Mixed.ToArray());
                string model = set.Model;
                if (model != null)
                {
                    if (string.IsNullOrEmpty(phoneModel))
                        add(OdinCheckLevel.Warn, OdinCheckCode.ModelUnknown, model);
                    else if (!sameModel(model, phoneModel))
                        add(OdinCheckLevel.Block, OdinCheckCode.OtherModel, model, phoneModel);
                }
            }

            if (items.Count == 0)
                add(OdinCheckLevel.Block, OdinCheckCode.Empty);

            // Blocks first, then warnings, then the rest.
            checks = checks.OrderByDescending(c => c.Level).ToList();
            return new OdinFlashPlan { Items = items, Checks = checks, PitData = pitData };
        }

        /// <summary>"SM-S911B" and "S911B" are one model; letters after it (a carrier's "U1") are not ignored.</summary>
        static bool sameModel(string a, string b)
        {
            string clean(string m) => m.Trim().ToUpperInvariant().Replace("SM-", "");
            return clean(a) == clean(b);
        }
    }

    /// <summary>How a flash went.</summary>
    public sealed class OdinFlashResult
    {
        /// <summary>The partitions written whole, in order.</summary>
        public List<string> Written { get; } = new List<string>();

        /// <summary>The partition being written when it failed; null when it failed before writing, or did not fail.</summary>
        public string FailedAt { get; internal set; }

        /// <summary>Null when every file was written.</summary>
        public Exception Error { get; internal set; }

        /// <summary>The phone's PIT was not the one the plan was checked against: nothing was written.</summary>
        public bool PitChanged { get; internal set; }

        /// <summary>False when ending the session went wrong; the phone then needs restarting by hand.</summary>
        public bool Ended { get; internal set; }

        public OdinReboot Restart { get; internal set; }
    }

    /// <summary>
    /// One whole flash, in Heimdall's order: handshake, session, file part size, total size,
    /// the PIT (which must be the one the plan was checked against, or nothing is written),
    /// then each file, then the end of the session and the restart asked for. After a failure
    /// the session is ended without a restart, so the phone stays in Download mode, ready to be
    /// flashed again.
    /// </summary>
    public static class OdinFlasher
    {
        public static OdinFlashResult Flash(IOdinTransport link, OdinFlashPlan plan, OdinReboot reboot,
            Action<string, long, long> progress = null, Action<string> log = null)
        {
            if (plan.Blocked)
                throw new InvalidOperationException("the plan has blocks; nothing may be flashed");
            log = log ?? (_ => { });
            OdinClient client = new OdinClient(link, log);
            OdinFlashResult result = new OdinFlashResult();

            client.Handshake();
            client.BeginSession();
            try
            {
                client.PrepareTransfers();
                client.SendTotalBytes(plan.TotalBytes);
                byte[] pit = client.DumpPit();
                if (!pit.SequenceEqual(plan.PitData))
                {
                    result.PitChanged = true;
                    throw new OdinException("the phone's PIT is not the one the files were checked against");
                }
                long total = plan.TotalBytes, before = 0;
                foreach (OdinFlashItem item in plan.Items)
                {
                    result.FailedAt = item.Entry.Partition;
                    log("odin: flashing " + item.Source.Name + " to " + item.Entry.Partition + " (" + item.Source.Size + " bytes)");
                    using (Stream data = item.Source.Open())
                    {
                        long start = before;
                        client.FlashFile(data, item.Source.Size, item.Entry, sent => progress?.Invoke(item.Entry.Partition, start + sent, total));
                    }
                    before += item.Source.Size;
                    result.Written.Add(item.Entry.Partition);
                }
                result.FailedAt = null;
            }
            catch (Exception e) when (e is OdinException || e is IOException || e is TimeoutException || e is InvalidDataException)
            {
                result.Error = e;
                log("odin: flash stopped: " + e.Message);
                result.Ended = tryEnd(client, OdinReboot.None, log, out OdinReboot none);
                result.Restart = none;
                return result;
            }
            result.Ended = tryEnd(client, reboot, log, out OdinReboot done);
            result.Restart = done;
            return result;
        }

        static bool tryEnd(OdinClient client, OdinReboot reboot, Action<string> log, out OdinReboot done)
        {
            done = OdinReboot.None;
            try
            {
                done = client.EndSession(reboot);
                return true;
            }
            catch (Exception e) when (e is OdinException || e is IOException || e is TimeoutException)
            {
                log("odin: ending the session failed: " + e.Message);
                return false;
            }
        }
    }
}
