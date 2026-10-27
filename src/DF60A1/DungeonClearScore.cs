using System;

namespace DF60A1
{
    internal sealed class DungeonClearScore
    {
        public ushort[] StyleComponents { get; private set; }
        public ushort[] TechniqueComponents { get; private set; }
        public int HitPenalty { get; private set; }
        public ushort Style { get; private set; }
        public ushort Technique { get; private set; }
        public byte ResultCode { get; private set; }

        public int CalculatedScore
        {
            get { return Math.Max(0, Math.Min(ushort.MaxValue, (int)Style + Technique + HitPenalty)); }
        }

        public byte CalculatedResultCode
        {
            get { return GamePackets.ResultCodeFromScore(CalculatedScore); }
        }

        public static bool TryParse(byte[] body, out DungeonClearScore score)
        {
            score = null;
            if (body == null || body.Length < 24) return false;
            var rawResultCode = LittleEndian.ReadUInt16(body, 22);
            if (rawResultCode > byte.MaxValue) return false;
            score = new DungeonClearScore
            {
                StyleComponents = new[] { LittleEndian.ReadUInt16(body, 2),
                    LittleEndian.ReadUInt16(body, 4), LittleEndian.ReadUInt16(body, 6) },
                TechniqueComponents = new[] { LittleEndian.ReadUInt16(body, 8),
                    LittleEndian.ReadUInt16(body, 10), LittleEndian.ReadUInt16(body, 12) },
                HitPenalty = unchecked((int)LittleEndian.ReadUInt32(body, 14)),
                Style = LittleEndian.ReadUInt16(body, 18),
                Technique = LittleEndian.ReadUInt16(body, 20),
                ResultCode = (byte)rawResultCode
            };
            return true;
        }
    }
}
