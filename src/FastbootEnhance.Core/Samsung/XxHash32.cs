using System;

namespace FastbootEnhance.Core.Samsung
{
    /// <summary>xxHash32, the checksum an LZ4 frame keeps of its whole content.</summary>
    public sealed class XxHash32
    {
        const uint P1 = 2654435761U, P2 = 2246822519U, P3 = 3266489917U, P4 = 668265263U, P5 = 374761393U;

        uint v1, v2, v3, v4;
        readonly uint seed;
        readonly byte[] pending = new byte[16];
        int pendingLength;
        long total;

        public XxHash32(uint seed = 0)
        {
            this.seed = seed;
            v1 = seed + P1 + P2;
            v2 = seed + P2;
            v3 = seed;
            v4 = seed - P1;
        }

        static uint Rotl(uint x, int r) => (x << r) | (x >> (32 - r));

        static uint Round(uint acc, uint lane) => Rotl(acc + lane * P2, 13) * P1;

        static uint Lane(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

        public void Append(byte[] data, int offset, int count)
        {
            total += count;
            if (pendingLength > 0)
            {
                int take = Math.Min(16 - pendingLength, count);
                Buffer.BlockCopy(data, offset, pending, pendingLength, take);
                pendingLength += take;
                offset += take;
                count -= take;
                if (pendingLength < 16)
                    return;
                v1 = Round(v1, Lane(pending, 0));
                v2 = Round(v2, Lane(pending, 4));
                v3 = Round(v3, Lane(pending, 8));
                v4 = Round(v4, Lane(pending, 12));
                pendingLength = 0;
            }
            while (count >= 16)
            {
                v1 = Round(v1, Lane(data, offset));
                v2 = Round(v2, Lane(data, offset + 4));
                v3 = Round(v3, Lane(data, offset + 8));
                v4 = Round(v4, Lane(data, offset + 12));
                offset += 16;
                count -= 16;
            }
            if (count > 0)
            {
                Buffer.BlockCopy(data, offset, pending, 0, count);
                pendingLength = count;
            }
        }

        public uint Digest()
        {
            uint h = total >= 16
                ? Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18)
                : seed + P5;
            h += (uint)total;
            int i = 0;
            for (; i + 4 <= pendingLength; i += 4)
                h = Rotl(h + Lane(pending, i) * P3, 17) * P4;
            for (; i < pendingLength; i++)
                h = Rotl(h + pending[i] * P5, 11) * P1;
            h ^= h >> 15;
            h *= P2;
            h ^= h >> 13;
            h *= P3;
            h ^= h >> 16;
            return h;
        }

        public static uint Hash(byte[] data, int offset, int count)
        {
            XxHash32 x = new XxHash32();
            x.Append(data, offset, count);
            return x.Digest();
        }
    }
}
