using System.Collections.Generic;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>
    /// What the bootloader says about itself in Download mode (packet 0x69): a header with the
    /// magic 0x12345678 and an item count, the items' locations (type, offset, size), and the
    /// items, each a type, a size and its bytes. Types: 0 model, 1 serial, 2 region (OMC sales
    /// code), 3 carrier.
    /// </summary>
    public sealed class OdinDeviceInfo
    {
        public const int Magic = 0x12345678;

        public string Model { get; private set; }
        public string Serial { get; private set; }
        public string SalesCode { get; private set; }
        public string CarrierId { get; private set; }

        /// <summary>The items as read, by type, including types this app does not name.</summary>
        public Dictionary<int, string> Items { get; } = new Dictionary<int, string>();

        /// <summary>Reads the information; null when it is not in this format.</summary>
        public static OdinDeviceInfo Parse(byte[] data)
        {
            if (data == null || data.Length < 8 || OdinPacket.GetInt(data, 0) != Magic)
                return null;
            int count = OdinPacket.GetInt(data, 4);
            if (count < 0 || count > 64 || 8 + count * 12 > data.Length)
                return null;
            OdinDeviceInfo info = new OdinDeviceInfo();
            for (int i = 0; i < count; i++)
            {
                int at = 8 + i * 12;
                int type = OdinPacket.GetInt(data, at);
                int offset = OdinPacket.GetInt(data, at + 4);
                int size = OdinPacket.GetInt(data, at + 8);
                if (offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset)
                    continue;
                // The location may point at the item's own header (type and size) or at its bytes.
                string value;
                if (size >= 8 && OdinPacket.GetInt(data, offset) == type && OdinPacket.GetInt(data, offset + 4) <= size - 8
                    && OdinPacket.GetInt(data, offset + 4) >= 0)
                    value = OdinPacket.Text(data, offset + 8, OdinPacket.GetInt(data, offset + 4));
                else
                    value = OdinPacket.Text(data, offset, size);
                value = value.Trim();
                if (value.Length == 0)
                    continue;
                info.Items[type] = value;
            }
            info.Model = Get(info.Items, 0);
            info.Serial = Get(info.Items, 1);
            info.SalesCode = Get(info.Items, 2);
            info.CarrierId = Get(info.Items, 3);
            return info.Items.Count > 0 ? info : null;
        }

        static string Get(Dictionary<int, string> items, int type)
        {
            return items.TryGetValue(type, out string v) ? v : null;
        }

        /// <summary>Builds the information in this format; used by the stand-in phone and tests.</summary>
        public static byte[] Build(IList<KeyValuePair<int, string>> items)
        {
            int headers = 8 + items.Count * 12;
            List<byte> data = new List<byte>(new byte[headers]);
            byte[] head = new byte[headers];
            OdinPacket.PutInt(head, 0, Magic);
            OdinPacket.PutInt(head, 4, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                byte[] text = System.Text.Encoding.ASCII.GetBytes(items[i].Value ?? "");
                int offset = data.Count;
                byte[] item = new byte[8 + text.Length];
                OdinPacket.PutInt(item, 0, items[i].Key);
                OdinPacket.PutInt(item, 4, text.Length);
                text.CopyTo(item, 8);
                data.AddRange(item);
                OdinPacket.PutInt(head, 8 + i * 12, items[i].Key);
                OdinPacket.PutInt(head, 8 + i * 12 + 4, offset);
                OdinPacket.PutInt(head, 8 + i * 12 + 8, item.Length);
            }
            byte[] result = data.ToArray();
            System.Array.Copy(head, result, headers);
            return result;
        }
    }
}
