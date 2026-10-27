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
    /// Talks to a Samsung phone in Download mode. This version only reads: the handshake, a
    /// session, the PIT and the device information, and the end of the session. It has no
    /// command that writes to the phone.
    /// </summary>
    public sealed class OdinClient
    {
        public const int HandshakeTimeoutMs = 2000;
        public const int ResponseTimeoutMs = 5000;

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

        /// <summary>Ends the session and, when asked, restarts the phone.</summary>
        public void EndSession(OdinReboot reboot)
        {
            Command("end session", OdinPacket.TypeEndSession, OdinPacket.EndSessionPlain);
            InSession = false;
            if (reboot == OdinReboot.None)
                return;
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
