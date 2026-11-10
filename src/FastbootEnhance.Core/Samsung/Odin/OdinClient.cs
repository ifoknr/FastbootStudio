using System;
using System.Text;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>What the phone does when the session ends.</summary>
    public enum OdinReboot
    {
        /// <summary>Stays in Download mode; Samsung's bootloader takes no second session on the same connection.</summary>
        None,

        /// <summary>Restarts into Android.</summary>
        Normal,

        /// <summary>Restarts into Download mode, ready for the next session (not every phone can).</summary>
        Download,
    }

    /// <summary>
    /// Talks to a Samsung phone in Download mode: the handshake, a session, the PIT and the
    /// device information, the end of the session, and writing a file to a partition the way
    /// Heimdall (MIT) does. Only <see cref="OdinFlasher"/> writes, after its checks.
    /// </summary>
    public sealed class OdinClient
    {
        public const int HandshakeTimeoutMs = 2000;
        public const int ResponseTimeoutMs = 5000;

        /// <summary>How long one file part may take to be sent and answered.</summary>
        public const int PartTimeoutMs = 30000;

        /// <summary>How long the phone may take to write a sequence (up to 100 MB) before it answers; Heimdall waits 2 minutes.</summary>
        public const int SequenceTimeoutMs = 120000;

        /// <summary>How long a leftover transfer is waited for after a dump, as Heimdall reads its empty packet.</summary>
        const int DrainTimeoutMs = 200;

        readonly IOdinTransport link;
        readonly Action<string> log;

        public OdinClient(IOdinTransport link, Action<string> log = null)
        {
            this.link = link;
            this.log = log ?? (_ => { });
        }

        /// <summary>The protocol version the bootloader agreed to: 1 for old ones that answer 0.</summary>
        public int ProtocolVersion { get; private set; }

        /// <summary>The bootloader takes LZ4-compressed data (it set 0x8000 in its answer).</summary>
        public bool TakesLz4 { get; private set; }

        public bool InSession { get; private set; }

        /// <summary>What Begin Session answered; not 0 when the bootloader takes a larger file part size.</summary>
        public int SessionAnswer { get; private set; }

        /// <summary>The size of each file part, and how many make a sequence: Heimdall's defaults until <see cref="PrepareTransfers"/>.</summary>
        public int PartSize { get; private set; } = OdinPacket.DefaultPartSize;
        public int SequenceParts { get; private set; } = OdinPacket.DefaultSequenceParts;

        /// <summary>"ODIN", answered by "LOKE" from a phone in Download mode.</summary>
        public void Handshake()
        {
            byte[] hello = Encoding.ASCII.GetBytes("ODIN");
            link.Write(hello, 0, hello.Length, HandshakeTimeoutMs);
            byte[] answer = ReadExact(4, HandshakeTimeoutMs, "the handshake");
            string text = Encoding.ASCII.GetString(answer);
            if (text != "LOKE")
                throw new OdinException("the handshake: \"" + OdinPacket.Text(answer, 0, 4) + "\" came instead of LOKE");
            log("odin: handshake LOKE");
        }

        /// <summary>
        /// Opens a session, asking for protocol <see cref="OdinPacket.RequestedProtocol"/>. The
        /// answer is the agreed version in its upper 16 bits, with 0x8000 set for LZ4.
        /// </summary>
        public void BeginSession()
        {
            int value = Command("begin session", OdinPacket.TypeSession, OdinPacket.SessionBegin, OdinPacket.RequestedProtocol);
            InSession = true;
            SessionAnswer = value;
            ProtocolVersion = value == 0 ? 1 : (value >> 16) & 0xFFFF;
            TakesLz4 = (value & OdinPacket.Lz4Flag) != 0;
            log("odin: session, protocol " + ProtocolVersion + (TakesLz4 ? ", LZ4" : "") + " (0x" + value.ToString("X") + ")");
        }

        /// <summary>The phone's PIT, block by block.</summary>
        public byte[] DumpPit()
        {
            RequireSession();
            int size = Command("PIT dump", OdinPacket.TypePit, OdinPacket.PitDump);
            if (size <= 0 || size > SamsungPit.MaxSize)
                throw new OdinException("PIT dump: the phone says its PIT is " + size + " bytes");
            byte[] pit = ReadBlocks(size, OdinPacket.TypePit, OdinPacket.PitDumpBlock, "PIT");
            Command("PIT dump end", OdinPacket.TypePit, OdinPacket.PitDumpEnd);
            log("odin: PIT, " + size + " bytes");
            return pit;
        }

        /// <summary>The device information (model, serial, region); null when the bootloader does not offer it.</summary>
        public OdinDeviceInfo DumpDeviceInfo()
        {
            RequireSession();
            int size;
            try
            {
                size = Command("device information", OdinPacket.TypeDeviceInfo, OdinPacket.DeviceInfoDump);
            }
            catch (OdinException e) when (e.PhoneCode != null)
            {
                // Older bootloaders refuse this packet; the session carries on.
                log("odin: no device information (" + e.Message + ")");
                return null;
            }
            if (size <= 0 || size > 64 * 1024)
            {
                log("odin: device information of " + size + " bytes skipped");
                return null;
            }
            byte[] data = ReadBlocks(size, OdinPacket.TypeDeviceInfo, OdinPacket.DeviceInfoBlock, "device information");
            Command("device information end", OdinPacket.TypeDeviceInfo, OdinPacket.DeviceInfoEnd);
            OdinDeviceInfo info = OdinDeviceInfo.Parse(data);
            log("odin: device information, " + size + " bytes" + (info == null ? ", not readable" : ""));
            return info;
        }

        /// <summary>
        /// Before a flash: a bootloader whose Begin Session answer is not 0 takes 1 MiB file
        /// parts, and is told so (Heimdall does the same); the others keep 128 KiB.
        /// </summary>
        public void PrepareTransfers()
        {
            RequireSession();
            if (SessionAnswer == 0)
                return;
            int result = Command("file part size", OdinPacket.TypeSession, OdinPacket.SessionFilePartSize, OdinPacket.LargePartSize);
            if (result != 0)
                throw new OdinException("file part size: the phone answered " + result + " instead of 0");
            PartSize = OdinPacket.LargePartSize;
            SequenceParts = OdinPacket.LargeSequenceParts;
            log("odin: file parts of " + PartSize / 1024 + " KiB, " + SequenceParts + " to a sequence");
        }

        /// <summary>
        /// The size of everything about to be flashed, which the bootloader wants before the
        /// first file. Sent as 64 bits (low word first); older bootloaders read the low word only.
        /// </summary>
        public void SendTotalBytes(long total)
        {
            RequireSession();
            int result = Command("total size", OdinPacket.TypeSession, OdinPacket.SessionTotalBytes,
                (int)(total & 0xFFFFFFFF), (int)(total >> 32));
            if (result != 0)
                throw new OdinException("total size: the phone answered " + result + " instead of 0", result);
            log("odin: total " + total + " bytes");
        }

        /// <summary>
        /// Writes <paramref name="size"/> bytes from <paramref name="data"/> to the partition of
        /// <paramref name="entry"/>, as Heimdall's SendFile: a flash request, then sequences of
        /// file parts (the last part padded with zeros), each part answered with its index, and
        /// each sequence closed with its real length, the partition and whether it is the last.
        /// A modem (CP) partition is closed with the modem's packet. <paramref name="sent"/> gets
        /// the bytes sent so far. There is no stopping half-way: a partition left half written is
        /// worse than one written whole.
        /// </summary>
        public void FlashFile(System.IO.Stream data, long size, PitEntry entry, Action<long> sent = null)
        {
            RequireSession();
            if (size <= 0)
                throw new ArgumentException("nothing to flash to " + entry.Partition);
            Command("flash " + entry.Partition, OdinPacket.TypeFile, OdinPacket.FileFlash);

            long sequenceBytes = (long)SequenceParts * PartSize;
            long done = 0;
            byte[] part = new byte[PartSize];
            byte[] answer = new byte[OdinPacket.ResponseSize];
            while (done < size)
            {
                long length = Math.Min(sequenceBytes, size - done);
                int parts = (int)((length + PartSize - 1) / PartSize);
                bool last = done + length >= size;
                Command("sequence of " + entry.Partition, OdinPacket.TypeFile, OdinPacket.FilePart, parts * PartSize);

                for (int i = 0; i < parts; i++)
                {
                    int want = (int)Math.Min(PartSize, length - (long)i * PartSize);
                    readFull(data, part, want, entry.Partition);
                    Array.Clear(part, want, PartSize - want);
                    // Heimdall sends an empty transfer before every part but the first of a sequence.
                    if (i > 0)
                        link.EndPacket();
                    link.Write(part, 0, PartSize, PartTimeoutMs);
                    if (ReadUpTo(answer, answer.Length, PartTimeoutMs) < answer.Length)
                        throw new OdinException(entry.Partition + ": part " + i + " was not answered");
                    int index = OdinPacket.ResponseValue(answer, OdinPacket.TypeFilePartAnswer, entry.Partition + " part " + i);
                    if (index != i)
                        throw new OdinException(entry.Partition + ": the phone answered part " + index + " for part " + i);
                    sent?.Invoke(done + Math.Min(length, (long)(i + 1) * PartSize));
                }

                byte[] end = entry.IsModem
                    ? OdinPacket.Command(OdinPacket.TypeFile, OdinPacket.FileEnd, OdinPacket.DestinationModem, (int)length, 0,
                        entry.DeviceType, last ? 1 : 0)
                    : OdinPacket.Command(OdinPacket.TypeFile, OdinPacket.FileEnd, OdinPacket.DestinationPhone, (int)length, 0,
                        entry.DeviceType, entry.PartitionId, last ? 1 : 0);
                // The end of a sequence has an empty transfer before and after it.
                link.EndPacket();
                Send(end);
                if (ReadUpTo(answer, answer.Length, SequenceTimeoutMs) < answer.Length)
                    throw new OdinException(entry.Partition + ": the phone did not confirm the written sequence");
                OdinPacket.ResponseValue(answer, OdinPacket.TypeFile, "end of a sequence of " + entry.Partition);
                done += length;
            }
            log("odin: " + entry.Partition + ", " + size + " bytes written");
        }

        static void readFull(System.IO.Stream data, byte[] buffer, int count, string what)
        {
            int got = 0;
            while (got < count)
            {
                int read = data.Read(buffer, got, count - got);
                if (read <= 0)
                    throw new System.IO.EndOfStreamException("the file for " + what + " ended early");
                got += read;
            }
        }

        /// <summary>
        /// Ends the session and, when asked, restarts the phone. A bootloader that refuses to
        /// restart into Download mode is asked for a normal restart instead. Returns the restart done.
        /// </summary>
        public OdinReboot EndSession(OdinReboot reboot)
        {
            Command("end session", OdinPacket.TypeEndSession, OdinPacket.EndSessionPlain);
            InSession = false;
            if (reboot == OdinReboot.None)
                return reboot;
            try
            {
                restart(reboot);
                return reboot;
            }
            catch (OdinException e) when (reboot == OdinReboot.Download && e.PhoneCode != null)
            {
                log("odin: no restart into Download mode (" + e.Message + "), restarting normally");
                restart(OdinReboot.Normal);
                return OdinReboot.Normal;
            }
        }

        void restart(OdinReboot reboot)
        {
            int request = reboot == OdinReboot.Download ? OdinPacket.EndSessionRebootToDownload : OdinPacket.EndSessionReboot;
            Send(OdinPacket.Command(OdinPacket.TypeEndSession, request));
            // The phone may restart before its answer gets out; the restart is what was asked for.
            byte[] answer = new byte[OdinPacket.ResponseSize];
            int got = ReadUpTo(answer, OdinPacket.ResponseSize, ResponseTimeoutMs);
            if (got == OdinPacket.ResponseSize)
                OdinPacket.ResponseValue(answer, OdinPacket.TypeEndSession, "restart");
            log("odin: restart " + reboot + (got == OdinPacket.ResponseSize ? "" : " (no answer, the phone restarted)"));
        }

        // ------------------------------------------------------------------ packets

        void RequireSession()
        {
            if (!InSession)
                throw new InvalidOperationException("no Odin session");
        }

        int Command(string what, int type, int command, params int[] arguments)
        {
            Send(OdinPacket.Command(type, command, arguments));
            byte[] answer = ReadExact(OdinPacket.ResponseSize, ResponseTimeoutMs, what);
            return OdinPacket.ResponseValue(answer, type, what);
        }

        void Send(byte[] packet)
        {
            link.Write(packet, 0, packet.Length, ResponseTimeoutMs);
            link.EndPacket();
        }

        /// <summary>A dump of <paramref name="size"/> bytes, asked for 500 bytes at a time.</summary>
        byte[] ReadBlocks(int size, int type, int command, string what)
        {
            byte[] data = new byte[size];
            int blocks = (size + OdinPacket.DumpBlockSize - 1) / OdinPacket.DumpBlockSize;
            for (int i = 0; i < blocks; i++)
            {
                Send(OdinPacket.Command(type, command, i));
                int length = Math.Min(OdinPacket.DumpBlockSize, size - i * OdinPacket.DumpBlockSize);
                byte[] block = ReadExact(length, ResponseTimeoutMs, what + " block " + i);
                Buffer.BlockCopy(block, 0, data, i * OdinPacket.DumpBlockSize, length);
            }
            // A last block sent whole, or the empty transfer that ends a dump: not part of the data.
            byte[] rest = new byte[OdinPacket.DumpBlockSize];
            int extra = ReadUpTo(rest, rest.Length, DrainTimeoutMs);
            if (extra > 0)
                log("odin: " + extra + " bytes after the " + what + " ignored");
            return data;
        }

        byte[] ReadExact(int count, int timeoutMs, string what)
        {
            byte[] buffer = new byte[count];
            int got = ReadUpTo(buffer, count, timeoutMs);
            if (got < count)
                throw new OdinException(what + ": " + got + " of " + count + " bytes arrived before the timeout");
            return buffer;
        }

        /// <summary>Reads until <paramref name="count"/> bytes arrived or the time ran out; returns how many.</summary>
        int ReadUpTo(byte[] buffer, int count, int timeoutMs)
        {
            int got = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (got < count)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0)
                    break;
                int read = link.Read(buffer, got, count - got, left);
                if (read <= 0)
                    break;
                got += read;
            }
            return got;
        }
    }
}
