using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FastbootEnhance.Core.Usb;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>A phone in Download mode, as Windows lists it: the serial port Samsung's driver gave it.</summary>
    public sealed class OdinPortInfo
    {
        public OdinPortInfo(string port, string name, string deviceId)
        {
            Port = port;
            Name = name;
            DeviceId = deviceId;
        }

        /// <summary>"COM5".</summary>
        public string Port { get; }
        public string Name { get; }
        public string DeviceId { get; }
    }

    /// <summary>
    /// Finds Download-mode phones among Windows' devices. Samsung's driver lists the phone
    /// (USB VID 04E8, PID 685D) as "SAMSUNG Mobile USB Modem (COM5)" or similar; only that id
    /// counts, since a phone running Android shows a Samsung modem too, under another id.
    /// </summary>
    public static class OdinPortFinder
    {
        static readonly Regex PortInName = new Regex(@"\((COM[1-9][0-9]{0,2})\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex PortAlone = new Regex(@"^COM[1-9][0-9]{0,2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>True for the device id of a Samsung phone in Download mode, the whole device or one of its interfaces.</summary>
        public static bool IsDownloadMode(string deviceId)
        {
            return deviceId != null
                && deviceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                && deviceId.IndexOf("VID_04E8&PID_685D", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>"COM5" from "SAMSUNG Mobile USB Modem (COM5)"; null when the name carries none.</summary>
        public static string PortFromName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            Match match = PortInName.Match(name);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        /// <summary>A port name as the registry or a modem's "attached to" gives it, checked; null when it is not one.</summary>
        public static string CleanPort(string port)
        {
            port = port?.Trim();
            return port != null && PortAlone.IsMatch(port) ? port.ToUpperInvariant() : null;
        }

        /// <summary>
        /// The Download-mode phones in a device list, each once. <paramref name="portOf"/> gives a
        /// device's port from elsewhere (the registry) when its name has none.
        /// </summary>
        public static List<OdinPortInfo> Find(IEnumerable<PnpDevice> devices, Func<PnpDevice, string> portOf = null)
        {
            List<OdinPortInfo> found = new List<OdinPortInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PnpDevice device in devices)
            {
                if (!IsDownloadMode(device.DeviceId) || device.ErrorCode != 0)
                    continue;
                string port = PortFromName(device.Name) ?? CleanPort(portOf?.Invoke(device));
                if (port != null && seen.Add(port))
                    found.Add(new OdinPortInfo(port, device.Name, device.DeviceId));
            }
            return found;
        }
    }
}
