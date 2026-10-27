using System;
using System.Collections.Generic;

namespace FastbootEnhance.Core.Samsung.Odin
{
    /// <summary>One partition in a Samsung PIT: what the bootloader flashes a file of the firmware to.</summary>
    public sealed class PitEntry
    {
        /// <summary>0 for the AP (phone), 1 for the CP (modem).</summary>
        public int BinaryType { get; internal set; }
        public int DeviceType { get; internal set; }

        /// <summary>The identifier a flash names the partition by.</summary>
        public int PartitionId { get; internal set; }

        /// <summary>Attributes (PIT v1), or the partition type (v2).</summary>
        public int Attributes { get; internal set; }

        /// <summary>Update attributes (v1), or the file system (v2).</summary>
        public int UpdateAttributes { get; internal set; }

        /// <summary>The block size (v1), or the first block (v2).</summary>
        public int BlockSizeOrStart { get; internal set; }
        public int BlockCount { get; internal set; }
        public int FileOffset { get; internal set; }
        public int FileSize { get; internal set; }

        /// <summary>"BOOT", "SUPER", "USERDATA"…</summary>
        public string Partition { get; internal set; }

        /// <summary>The file the firmware carries for it: "boot.img", "super.img"…; empty when none.</summary>
        public string FileName { get; internal set; }
        public string DeltaName { get; internal set; }

        public bool IsModem => BinaryType == 1;
    }

    /// <summary>
    /// A Samsung PIT (partition information table), as the bootloader sends it in Download mode
    /// or as a CSC carries it: a 28-byte header (magic 0x12349876, entry count, the "COM_TAR2"
    /// gang name, the project name) and 132-byte entries of nine integers and three 32-byte names.
    /// </summary>
    public sealed class SamsungPit
    {
        public const int Magic = 0x12349876;
        public const int HeaderSize = 28;
        public const int EntrySize = 132;

        /// <summary>A PIT larger than this is not one; the dump refuses to read it.</summary>
        public const int MaxSize = 1024 * 1024;

        public string Gang { get; private set; }

        /// <summary>The chip project, "SM8550" or similar; empty on old PITs.</summary>
        public string Project { get; private set; }

        public List<PitEntry> Entries { get; } = new List<PitEntry>();

        /// <summary>
        /// PIT v2 (eMMC and UFS phones) stores a start block where v1 stored a block size: told
        /// apart by those values differing between entries.
        /// </summary>
        public bool IsVersion2 { get; private set; }

        public static bool LooksLikePit(byte[] data)
        {
            return data != null && data.Length >= HeaderSize && OdinPacket.GetInt(data, 0) == Magic;
        }

        /// <summary>Reads a PIT; throws InvalidOperationException when it is not one or is cut short.</summary>
        public static SamsungPit Parse(byte[] data)
        {
            if (!LooksLikePit(data))
                throw new InvalidOperationException("not a PIT (the magic number is missing)");
            int count = OdinPacket.GetInt(data, 4);
            if (count < 0 || count > (data.Length - HeaderSize) / EntrySize)
                throw new InvalidOperationException("the PIT says it has " + count + " partitions, but holds room for "
                    + Math.Max(0, (data.Length - HeaderSize) / EntrySize));

            SamsungPit pit = new SamsungPit
            {
                Gang = OdinPacket.Text(data, 8, 8),
                Project = OdinPacket.Text(data, 16, 8),
            };
            int? firstBlock = null;
            for (int i = 0; i < count; i++)
            {
                int at = HeaderSize + i * EntrySize;
                PitEntry entry = new PitEntry
                {
                    BinaryType = OdinPacket.GetInt(data, at),
                    DeviceType = OdinPacket.GetInt(data, at + 4),
                    PartitionId = OdinPacket.GetInt(data, at + 8),
                    Attributes = OdinPacket.GetInt(data, at + 12),
                    UpdateAttributes = OdinPacket.GetInt(data, at + 16),
                    BlockSizeOrStart = OdinPacket.GetInt(data, at + 20),
                    BlockCount = OdinPacket.GetInt(data, at + 24),
                    FileOffset = OdinPacket.GetInt(data, at + 28),
                    FileSize = OdinPacket.GetInt(data, at + 32),
                    Partition = OdinPacket.Text(data, at + 36, 32),
                    FileName = OdinPacket.Text(data, at + 68, 32),
                    DeltaName = OdinPacket.Text(data, at + 100, 32),
                };
                if (firstBlock == null)
                    firstBlock = entry.BlockSizeOrStart;
                else if (entry.BlockSizeOrStart != firstBlock)
                    pit.IsVersion2 = true;
                pit.Entries.Add(entry);
            }
            return pit;
        }

        /// <summary>The entry a firmware file goes to, matched on its file name without case; null when none.</summary>
        public PitEntry ForFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;
            foreach (PitEntry e in Entries)
            {
                if (string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        /// <summary>The entry named <paramref name="partition"/> ("BOOT"), without case; null when none.</summary>
        public PitEntry ForPartition(string partition)
        {
            foreach (PitEntry e in Entries)
            {
                if (string.Equals(e.Partition, partition, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        /// <summary>Writes a PIT; used to make the stand-in phone's table and in tests.</summary>
        public static byte[] Build(string gang, string project, IList<PitEntry> entries)
        {
            byte[] data = new byte[HeaderSize + entries.Count * EntrySize];
            OdinPacket.PutInt(data, 0, Magic);
            OdinPacket.PutInt(data, 4, entries.Count);
            // The header's names fill their 8 bytes ("COM_TAR2"); an entry's names end with a zero.
            PutText(data, 8, 8, gang, 8);
            PutText(data, 16, 8, project, 8);
            for (int i = 0; i < entries.Count; i++)
            {
                PitEntry e = entries[i];
                int at = HeaderSize + i * EntrySize;
                OdinPacket.PutInt(data, at, e.BinaryType);
                OdinPacket.PutInt(data, at + 4, e.DeviceType);
                OdinPacket.PutInt(data, at + 8, e.PartitionId);
                OdinPacket.PutInt(data, at + 12, e.Attributes);
                OdinPacket.PutInt(data, at + 16, e.UpdateAttributes);
                OdinPacket.PutInt(data, at + 20, e.BlockSizeOrStart);
                OdinPacket.PutInt(data, at + 24, e.BlockCount);
                OdinPacket.PutInt(data, at + 28, e.FileOffset);
                OdinPacket.PutInt(data, at + 32, e.FileSize);
                PutText(data, at + 36, 32, e.Partition, 31);
                PutText(data, at + 68, 32, e.FileName, 31);
                PutText(data, at + 100, 32, e.DeltaName, 31);
            }
            return data;
        }

        /// <summary>An entry for <see cref="Build"/>.</summary>
        public static PitEntry Entry(int id, string partition, string fileName, int start, int blocks,
            int binaryType = 0, int deviceType = 8, int type = 5, int fileSystem = 0)
        {
            return new PitEntry
            {
                BinaryType = binaryType,
                DeviceType = deviceType,
                PartitionId = id,
                Attributes = type,
                UpdateAttributes = fileSystem,
                BlockSizeOrStart = start,
                BlockCount = blocks,
                Partition = partition,
                FileName = fileName ?? "",
                DeltaName = "",
            };
        }

        static void PutText(byte[] data, int offset, int length, string text, int most)
        {
            text = text ?? "";
            for (int i = 0; i < most && i < length && i < text.Length; i++)
                data[offset + i] = (byte)text[i];
        }
    }
}
