using System;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>
    /// The USB link to a Samsung phone in Download mode: its bulk-out and bulk-in endpoints, as
    /// a serial port of Samsung's driver on Windows or as USB host transfers on Android.
    /// </summary>
    public interface IOdinTransport : IDisposable
    {
        /// <summary>Sends all of <paramref name="count"/> bytes, or throws.</summary>
        void Write(byte[] buffer, int offset, int count, int timeoutMs);

        /// <summary>
        /// Called after each packet. A USB host link sends a zero-length transfer here, as
        /// Heimdall does; a serial port of Samsung's driver has nothing to send.
        /// </summary>
        void EndPacket();

        /// <summary>
        /// Receives at most <paramref name="count"/> bytes, waiting up to
        /// <paramref name="timeoutMs"/> for the first; returns how many arrived, 0 on timeout.
        /// </summary>
        int Read(byte[] buffer, int offset, int count, int timeoutMs);
    }
}
