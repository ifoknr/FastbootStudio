using FastbootEnhance.Core.Samsung.Odin;
using FastbootEnhance.Core.Usb;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FastbootEnhance
{
    /// <summary>
    /// Finds Samsung phones in Download mode and opens their port. FASTBOOT_STUDIO_FAKE_ODIN
    /// answers for tests instead of Windows: "phone", "noinfo" (no device information),
    /// "silent" (no answer to the handshake), "busy" (port taken), "nodriver" or "none".
    /// </summary>
    static class OdinUI
    {
        const string StandInVariable = "FASTBOOT_STUDIO_FAKE_ODIN";
        const string StandInPort = "COM9";

        public sealed class Scan
        {
            public List<OdinPortInfo> Ports = new List<OdinPortInfo>();

            /// <summary>A Download-mode phone is plugged in, but has no working port: the driver is missing or broken.</summary>
            public bool SeenWithoutPort;
        }

        static string standIn => Environment.GetEnvironmentVariable(StandInVariable)?.Trim().ToLowerInvariant();

        public static Scan Find()
        {
            Scan scan = new Scan();
            string fake = standIn;
            if (!string.IsNullOrEmpty(fake))
            {
                if (fake == "nodriver")
                    scan.SeenWithoutPort = true;
                else if (fake != "none")
                    scan.Ports.Add(new OdinPortInfo(StandInPort, "SAMSUNG Mobile USB Modem (" + StandInPort + ")", @"USB\VID_04E8&PID_685D&MI_01\STANDIN"));
                return scan;
            }

            IReadOnlyList<PnpDevice> devices = UsbScan.Read();
            scan.Ports = OdinPortFinder.Find(devices, registryPort);
            scan.SeenWithoutPort = scan.Ports.Count == 0 && devices.Any(d => OdinPortFinder.IsDownloadMode(d.DeviceId));
            return scan;
        }

        /// <summary>The port Windows gave a device, from its "Device Parameters" key.</summary>
        static string registryPort(PnpDevice device)
        {
            if (device.DeviceId.IndexOf("..", StringComparison.Ordinal) >= 0)
                return null;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + device.DeviceId + @"\Device Parameters"))
                    return key?.GetValue("PortName") as string;
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is System.IO.IOException)
            {
                return null;
            }
        }

        public static IOdinTransport Open(OdinPortInfo port, Action<string> log)
        {
            string fake = standIn;
            if (string.IsNullOrEmpty(fake))
                return new OdinSerialPort(port.Port, log);

            if (fake == "busy")
                throw new OdinPortException(port.Port, OdinPortException.InUse);
            FakeOdinDevice phone = new FakeOdinDevice();
            if (fake == "silent")
                phone.HandshakeAnswer = "";
            else if (fake == "noinfo")
                phone.DeviceInfo = null;
            return phone;
        }
    }
}
