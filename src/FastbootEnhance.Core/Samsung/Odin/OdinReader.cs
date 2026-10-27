using System;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>What a reading of a phone in Download mode found.</summary>
    public sealed class OdinReading
    {
        public int ProtocolVersion { get; internal set; }
        public bool TakesLz4 { get; internal set; }

        /// <summary>The PIT as the phone sent it, to be saved as a .pit file.</summary>
        public byte[] PitData { get; internal set; }
        public SamsungPit Pit { get; internal set; }

        /// <summary>Null when the bootloader does not offer it.</summary>
        public OdinDeviceInfo Info { get; internal set; }

        /// <summary>Why the device information is missing, when it is for a reason other than the phone saying no.</summary>
        public string InfoProblem { get; internal set; }

        /// <summary>False when the end of the session went wrong; the phone then needs restarting by hand.</summary>
        public bool Ended { get; internal set; }
    }

    /// <summary>
    /// One whole reading: handshake, session, PIT, device information, end of session. It only
    /// reads. The session is always ended, also after a failure, so the phone is not left waiting
    /// for packets; Samsung's bootloader takes no second session on the same connection, hence the
    /// restart choice.
    /// </summary>
    public static class OdinReader
    {
        public static OdinReading Read(IOdinTransport link, OdinReboot reboot, Action<string> log = null)
        {
            log = log ?? (_ => { });
            OdinClient client = new OdinClient(link, log);
            OdinReading reading = new OdinReading();

            client.Handshake();
            client.BeginSession();
            reading.ProtocolVersion = client.ProtocolVersion;
            reading.TakesLz4 = client.TakesLz4;
            try
            {
                reading.PitData = client.DumpPit();
                try
                {
                    reading.Pit = SamsungPit.Parse(reading.PitData);
                }
                catch (InvalidOperationException e)
                {
                    throw new OdinException("the PIT the phone sent is not readable: " + e.Message);
                }

                try
                {
                    reading.Info = client.DumpDeviceInfo();
                }
                catch (OdinException e)
                {
                    // The PIT is what matters; the link may be out of step now, so only the end is tried.
                    reading.InfoProblem = e.Message;
                    log("odin: " + e.Message);
                }
            }
            catch
            {
                tryEnd(client, reboot, log);
                throw;
            }
            reading.Ended = tryEnd(client, reboot, log);
            return reading;
        }

        static bool tryEnd(OdinClient client, OdinReboot reboot, Action<string> log)
        {
            try
            {
                client.EndSession(reboot);
                return true;
            }
            catch (Exception e) when (e is OdinException || e is System.IO.IOException || e is TimeoutException)
            {
                log("odin: ending the session failed: " + e.Message);
                return false;
            }
        }
    }
}
