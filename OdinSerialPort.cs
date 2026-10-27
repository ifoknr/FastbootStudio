using FastbootEnhance.Core.Samsung.Odin;
using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace FastbootEnhance
{
    /// <summary>The port could not be opened; <see cref="Code"/> is Windows' error.</summary>
    sealed class OdinPortException : IOException
    {
        public const int NotFound = 2;
        public const int InUse = 5;

        public OdinPortException(string port, int code)
            : base(port + ": " + new Win32Exception(code).Message + " (" + code + ")")
        {
            Port = port;
            Code = code;
        }

        public string Port { get; }
        public int Code { get; }
    }

    /// <summary>
    /// A phone in Download mode through the serial port Samsung's USB driver gives it, the way
    /// Odin talks to it: no other driver to install. The port is opened for this app alone, set
    /// to 8N1 with DTR and RTS on, and read with a time limit per call.
    /// </summary>
    sealed class OdinSerialPort : IOdinTransport
    {
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint OPEN_EXISTING = 3;
        const uint PURGE_TXCLEAR = 0x0004;
        const uint PURGE_RXCLEAR = 0x0008;
        const uint MAXDWORD = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        struct DCB
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public sbyte XonChar;
            public sbyte XoffChar;
            public sbyte ErrorChar;
            public sbyte EofChar;
            public sbyte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetCommState(SafeFileHandle file, ref DCB dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetCommState(SafeFileHandle file, ref DCB dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetCommTimeouts(SafeFileHandle file, ref COMMTIMEOUTS timeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetupComm(SafeFileHandle file, uint inQueue, uint outQueue);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool PurgeComm(SafeFileHandle file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, int count, out int written, IntPtr overlapped);

        readonly SafeFileHandle handle;
        readonly string port;
        readonly Action<string> log;
        uint readTimeout = uint.MaxValue;
        uint writeTimeout = uint.MaxValue;

        public OdinSerialPort(string port, Action<string> log = null)
        {
            if (OdinPortFinder.CleanPort(port) == null)
                throw new ArgumentException("not a serial port: " + port);
            this.port = port;
            this.log = log ?? (_ => { });

            // \\.\ reaches COM10 and above as well.
            handle = CreateFile(@"\\.\" + port, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new OdinPortException(port, code);
            }

            SetupComm(handle, 1 << 20, 1 << 20);
            DCB dcb = new DCB { DCBlength = (uint)Marshal.SizeOf(typeof(DCB)) };
            if (GetCommState(handle, ref dcb))
            {
                // The baud rate means nothing on USB, but the driver wants one; binary, DTR and RTS on, no flow control.
                dcb.BaudRate = 115200;
                dcb.ByteSize = 8;
                dcb.Parity = 0;
                dcb.StopBits = 0;
                dcb.Flags = 0x0001 | 0x0010 | 0x1000;
                if (!SetCommState(handle, ref dcb))
                    this.log("odin: " + port + " keeps its settings (" + Marshal.GetLastWin32Error() + ")");
            }
            PurgeComm(handle, PURGE_RXCLEAR | PURGE_TXCLEAR);
        }

        public void Write(byte[] buffer, int offset, int count, int timeoutMs)
        {
            checkRange(buffer, offset, count);
            setTimeouts(readTimeout, (uint)Math.Max(1, timeoutMs));
            int done = 0;
            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr start = pinned.AddrOfPinnedObject();
                while (done < count)
                {
                    if (!WriteFile(handle, start + offset + done, count - done, out int written, IntPtr.Zero))
                        throw broken("writing");
                    if (written <= 0)
                        throw new TimeoutException(port + ": the phone took no data for " + timeoutMs + " ms");
                    done += written;
                }
            }
            finally
            {
                pinned.Free();
            }
        }

        public void EndPacket()
        {
            // Samsung's driver ends each transfer itself.
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            checkRange(buffer, offset, count);
            if (count == 0)
                return 0;
            // Back as soon as anything arrived, else after the time limit with nothing.
            setTimeouts((uint)Math.Max(1, timeoutMs), writeTimeout);
            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                if (!ReadFile(handle, pinned.AddrOfPinnedObject() + offset, count, out int read, IntPtr.Zero))
                    throw broken("reading");
                return read;
            }
            finally
            {
                pinned.Free();
            }
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        void setTimeouts(uint read, uint write)
        {
            if (read == readTimeout && write == writeTimeout)
                return;
            COMMTIMEOUTS timeouts = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = MAXDWORD,
                ReadTotalTimeoutMultiplier = MAXDWORD,
                ReadTotalTimeoutConstant = Math.Min(read, MAXDWORD - 1),
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = write == uint.MaxValue ? 5000 : write,
            };
            if (!SetCommTimeouts(handle, ref timeouts))
                throw broken("setting the time limits");
            readTimeout = read;
            writeTimeout = write;
        }

        IOException broken(string what)
        {
            int code = Marshal.GetLastWin32Error();
            return new IOException(port + ", " + what + ": " + new Win32Exception(code).Message + " (" + code + ")");
        }

        static void checkRange(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
        }
    }
}
