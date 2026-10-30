using System;
using System.Text;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>A failure the phone or the link reported, written for the log.</summary>
    public sealed class OdinException : Exception
    {
        public OdinException(string message, int? phoneCode = null) : base(message)
        {
            PhoneCode = phoneCode;
        }

        /// <summary>The error code the bootloader sent with a 0xFF answer, when it sent one.</summary>
        public int? PhoneCode { get; }
    }

    /// <summary>
    /// The Odin protocol's packets, as Heimdall (MIT) and the Samsung-Loki documentation
    /// describe them. Every command is 1024 bytes of little-endian 32-bit integers, padded with
    /// zeros: type, command, then arguments. Every answer is 8 bytes: the type again, or 0xFF on
    /// failure, then a value.
    /// </summary>
    public static class OdinPacket
    {
        public const int CommandSize = 1024;
        public const int ResponseSize = 8;

        /// <summary>The size of one block of a PIT or device-information dump.</summary>
        public const int DumpBlockSize = 500;

        public const int TypeSession = 0x64;
        public const int TypePit = 0x65;
        public const int TypeFile = 0x66;
        public const int TypeEndSession = 0x67;
        public const int TypeDeviceInfo = 0x69;

        public const int SessionBegin = 0x00;
        public const int PitDump = 0x01;
        public const int PitDumpBlock = 0x02;
        public const int PitDumpEnd = 0x03;
        public const int DeviceInfoDump = 0x00;
        public const int DeviceInfoBlock = 0x01;
        public const int DeviceInfoEnd = 0x02;
        public const int EndSessionPlain = 0x00;
        public const int EndSessionReboot = 0x01;
        public const int EndSessionRebootToDownload = 0x02;

        /// <summary>The protocol version this app asks for; the phone answers with the lower of it and its own.</summary>
        public const int RequestedProtocol = 4;

        /// <summary>Set in the Begin Session answer when the bootloader takes LZ4-compressed data.</summary>
        public const int Lz4Flag = 0x8000;

        public static byte[] Command(int type, int command, params int[] arguments)
        {
            byte[] packet = new byte[CommandSize];
            PutInt(packet, 0, type);
            PutInt(packet, 4, command);
            for (int i = 0; i < arguments.Length; i++)
                PutInt(packet, 8 + 4 * i, arguments[i]);
            return packet;
        }

        public static void PutInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        public static int GetInt(byte[] buffer, int offset)
        {
            return buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24;
        }

        /// <summary>
        /// The value of an 8-byte answer to a packet of <paramref name="type"/>. A 0xFF answer,
        /// or an answer for another type, throws: the phone refused, or the link is out of step.
        /// </summary>
        public static int ResponseValue(byte[] response, int type, string what)
        {
            if (response == null || response.Length < ResponseSize)
                throw new OdinException(what + ": the answer is too short");
            if (response[0] == 0xFF)
            {
                int code = GetInt(response, 4);
                throw new OdinException(what + ": the phone refused (code " + code + ")", code);
            }
            int got = GetInt(response, 0);
            if (got != type)
                throw new OdinException(what + ": an answer of type 0x" + got.ToString("X") + " came instead of 0x" + type.ToString("X"));
            return GetInt(response, 4);
        }

        /// <summary>Text from a fixed-size field: up to the first zero byte, ASCII.</summary>
        public static string Text(byte[] buffer, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && end < buffer.Length && buffer[end] != 0)
                end++;
            StringBuilder text = new StringBuilder(end - offset);
            for (int i = offset; i < end; i++)
            {
                byte b = buffer[i];
                // Anything outside printable ASCII is not shown as it is.
                text.Append(b >= 0x20 && b < 0x7F ? (char)b : '?');
            }
            return text.ToString();
        }
    }
}
