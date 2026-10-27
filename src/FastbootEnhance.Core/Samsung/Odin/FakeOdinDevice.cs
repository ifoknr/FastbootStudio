using System;
using System.Collections.Generic;
using System.Text;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>
    /// A Samsung phone in Download mode, made up: it answers the reading half of the protocol
    /// (handshake, session, PIT and device-information dumps, end of session) the way the
    /// bootloader does, so tests and the app's stand-in mode run without a phone. It refuses
    /// every packet it does not know with 0xFF, as a bootloader does.
    /// </summary>
    public sealed class FakeOdinDevice : IOdinTransport
    {
        readonly Queue<byte> outgoing = new Queue<byte>();
        readonly List<byte> incoming = new List<byte>();
        bool shookHands;
        bool inSession;

        public FakeOdinDevice(byte[] pit = null, byte[] deviceInfo = null)
        {
            Pit = pit ?? SamplePit();
            DeviceInfo = deviceInfo ?? SampleDeviceInfo();
        }

        public byte[] Pit { get; set; }

        /// <summary>The information packet 0x69 answers with; null for a bootloader that refuses it.</summary>
        public byte[] DeviceInfo { get; set; }

        /// <summary>What Begin Session answers: protocol 4 with LZ4 unless set otherwise.</summary>
        public int SessionAnswer { get; set; } = (4 << 16) | OdinPacket.Lz4Flag;

        /// <summary>What the handshake answers instead of "LOKE"; null for "LOKE".</summary>
        public string HandshakeAnswer { get; set; }

        /// <summary>Stays silent from this packet on (counted from 0, after the handshake): a pulled cable.</summary>
        public int? SilentFrom { get; set; }

        /// <summary>Sends each last dump block whole (500 bytes), as some bootloaders do.</summary>
        public bool PadLastBlock { get; set; }

        /// <summary>Refuses to restart into Download mode, as some bootloaders may.</summary>
        public bool RefusesDownloadRestart { get; set; }

        /// <summary>Restarts without answering the restart request, as phones often do.</summary>
        public bool RestartsSilently { get; set; }

        /// <summary>Every packet received after the handshake, as "type/command".</summary>
        public List<string> Received { get; } = new List<string>();

        /// <summary>How the session ended: "reboot", "download", or null while it has not.</summary>
        public string Restarted { get; private set; }

        public int EmptyPackets { get; private set; }
        public bool Disposed { get; private set; }

        public void Write(byte[] buffer, int offset, int count, int timeoutMs)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FakeOdinDevice));
            if (Restarted != null)
                throw new OdinException("the phone restarted; the link is gone");
            for (int i = 0; i < count; i++)
                incoming.Add(buffer[offset + i]);

            if (!shookHands)
            {
                if (incoming.Count < 4)
                    return;
                string hello = Encoding.ASCII.GetString(incoming.ToArray(), 0, 4);
                incoming.RemoveRange(0, 4);
                if (hello == "ODIN")
                {
                    shookHands = true;
                    Answer(Encoding.ASCII.GetBytes(HandshakeAnswer ?? "LOKE"));
                }
                return;
            }

            while (incoming.Count >= OdinPacket.CommandSize)
            {
                byte[] packet = incoming.GetRange(0, OdinPacket.CommandSize).ToArray();
                incoming.RemoveRange(0, OdinPacket.CommandSize);
                Handle(packet);
            }
        }

        public void EndPacket()
        {
            EmptyPackets++;
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            int n = 0;
            while (n < count && outgoing.Count > 0)
                buffer[offset + n++] = outgoing.Dequeue();
            return n;
        }

        public void Dispose()
        {
            Disposed = true;
        }

        void Handle(byte[] packet)
        {
            int type = OdinPacket.GetInt(packet, 0);
            int command = OdinPacket.GetInt(packet, 4);
            int argument = OdinPacket.GetInt(packet, 8);
            Received.Add("0x" + type.ToString("X") + "/" + command);
            if (SilentFrom != null && Received.Count > SilentFrom)
                return;

            switch (type)
            {
                case OdinPacket.TypeSession when command == OdinPacket.SessionBegin:
                    if (inSession)
                    {
                        Refuse(-1);
                        return;
                    }
                    inSession = true;
                    Reply(type, SessionAnswer);
                    return;

                case OdinPacket.TypePit when inSession:
                    Dump(Pit, type, command, OdinPacket.PitDump, OdinPacket.PitDumpBlock, OdinPacket.PitDumpEnd, argument);
                    return;

                case OdinPacket.TypeDeviceInfo when inSession && DeviceInfo != null:
                    Dump(DeviceInfo, type, command, OdinPacket.DeviceInfoDump, OdinPacket.DeviceInfoBlock, OdinPacket.DeviceInfoEnd, argument);
                    return;

                case OdinPacket.TypeEndSession when command == OdinPacket.EndSessionPlain && inSession:
                    inSession = false;
                    Reply(type, 0);
                    return;

                case OdinPacket.TypeEndSession when command == OdinPacket.EndSessionRebootToDownload && RefusesDownloadRestart:
                    Refuse(-1);
                    return;

                case OdinPacket.TypeEndSession when command == OdinPacket.EndSessionReboot || command == OdinPacket.EndSessionRebootToDownload:
                    if (!RestartsSilently)
                        Reply(type, 0);
                    Restarted = command == OdinPacket.EndSessionReboot ? "reboot" : "download";
                    return;
            }
            // Anything else, writing included, is refused: this phone takes no changes.
            Refuse(-1);
        }

        void Dump(byte[] data, int type, int command, int start, int block, int end, int index)
        {
            if (command == start)
            {
                Reply(type, data.Length);
            }
            else if (command == block)
            {
                int at = index * OdinPacket.DumpBlockSize;
                if (index < 0 || at >= data.Length)
                {
                    Refuse(-1);
                    return;
                }
                int length = Math.Min(OdinPacket.DumpBlockSize, data.Length - at);
                byte[] part = new byte[PadLastBlock ? OdinPacket.DumpBlockSize : length];
                Array.Copy(data, at, part, 0, length);
                Answer(part);
            }
            else if (command == end)
            {
                Reply(type, 0);
            }
            else
            {
                Refuse(-1);
            }
        }

        void Reply(int type, int value)
        {
            byte[] answer = new byte[OdinPacket.ResponseSize];
            OdinPacket.PutInt(answer, 0, type);
            OdinPacket.PutInt(answer, 4, value);
            Answer(answer);
        }

        void Refuse(int code)
        {
            byte[] answer = new byte[OdinPacket.ResponseSize];
            OdinPacket.PutInt(answer, 0, 0xFF);
            OdinPacket.PutInt(answer, 4, code);
            Answer(answer);
        }

        void Answer(byte[] bytes)
        {
            foreach (byte b in bytes)
                outgoing.Enqueue(b);
        }

        /// <summary>A PIT shaped like a recent Galaxy's (UFS, PIT v2), shortened.</summary>
        public static byte[] SamplePit()
        {
            var entries = new List<PitEntry>
            {
                SamsungPit.Entry(80, "BOOTLOADER", "sboot.bin", 0, 8192, deviceType: 2, type: 2),
                SamsungPit.Entry(1, "EFS", "efs.img", 8192, 40960, type: 5, fileSystem: 1),
                SamsungPit.Entry(2, "SEC_EFS", "sec_efs.img", 49152, 5120, type: 5),
                SamsungPit.Entry(10, "PARAM", "param.bin", 54272, 8192, type: 5),
                SamsungPit.Entry(11, "UP_PARAM", "up_param.bin", 62464, 4096, type: 5),
                SamsungPit.Entry(12, "BOOT", "boot.img", 66560, 32768, type: 5),
                SamsungPit.Entry(13, "INIT_BOOT", "init_boot.img", 99328, 2048, type: 5),
                SamsungPit.Entry(14, "VENDOR_BOOT", "vendor_boot.img", 101376, 25600, type: 5),
                SamsungPit.Entry(15, "RECOVERY", "recovery.img", 126976, 32768, type: 5),
                SamsungPit.Entry(16, "DTBO", "dtbo.img", 159744, 6144, type: 5),
                SamsungPit.Entry(17, "VBMETA", "vbmeta.img", 165888, 16, type: 5),
                SamsungPit.Entry(18, "VBMETA_SYSTEM", "vbmeta_system.img", 165904, 16, type: 5),
                SamsungPit.Entry(19, "RADIO", "modem.bin", 165920, 61440, binaryType: 1, type: 5),
                SamsungPit.Entry(20, "SUPER", "super.img", 227360, 3145728, type: 5, fileSystem: 1),
                SamsungPit.Entry(21, "PRISM", "prism.img", 3373088, 81920, type: 5, fileSystem: 5),
                SamsungPit.Entry(22, "OPTICS", "optics.img", 3455008, 10240, type: 5, fileSystem: 5),
                SamsungPit.Entry(23, "CACHE", "cache.img", 3465248, 51200, type: 5, fileSystem: 5),
                SamsungPit.Entry(24, "USERDATA", "userdata.img", 3516448, 0, type: 5, fileSystem: 5),
            };
            return SamsungPit.Build("COM_TAR2", "SM8550", entries);
        }

        public static byte[] SampleDeviceInfo()
        {
            return OdinDeviceInfo.Build(new[]
            {
                new KeyValuePair<int, string>(0, "SM-S911B"),
                new KeyValuePair<int, string>(1, "R5CW00EXAMPLE"),
                new KeyValuePair<int, string>(2, "EUX"),
                new KeyValuePair<int, string>(3, "EUX"),
            });
        }
    }
}
