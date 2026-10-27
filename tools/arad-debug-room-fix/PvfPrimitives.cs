using System;

namespace DF60A1
{
    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(byte[] bytes)
        {
            return Compute(bytes, 0);
        }

        public static uint Compute(byte[] bytes, uint seed)
        {
            var crc = ~seed;
            for (var index = 0; index < bytes.Length; index++)
                crc = Table[(crc ^ bytes[index]) & 0xff] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    internal static class LittleEndian
    {
        public static uint ReadUInt32(byte[] value, int offset)
        {
            return (uint)(value[offset] | value[offset + 1] << 8 |
                value[offset + 2] << 16 | value[offset + 3] << 24);
        }

        public static void WriteUInt32(byte[] value, int offset, uint number)
        {
            value[offset] = (byte)number;
            value[offset + 1] = (byte)(number >> 8);
            value[offset + 2] = (byte)(number >> 16);
            value[offset + 3] = (byte)(number >> 24);
        }
    }
}
