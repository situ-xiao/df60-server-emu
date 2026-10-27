using System;
using System.Collections.Generic;

namespace DF60A1
{
    internal enum SkillType
    {
        Active,
        Passive
    }

    internal sealed class SkillDefinition
    {
        public byte SkillId { get; private set; }
        public string Path { get; private set; }
        public SkillType Type { get; private set; }
        public byte SkillClass { get; private set; }
        public IReadOnlyList<byte> FitnessGrowTypes { get; private set; }
        public IReadOnlyList<byte> GrowTypeMaximumLevels { get; private set; }
        public int PurchaseCost { get; private set; }
        public int RequiredLevel { get; private set; }
        public int MaximumLevel { get; private set; }

        public SkillDefinition(byte skillId, string path, SkillType type, byte skillClass,
            IReadOnlyList<byte> fitnessGrowTypes, IReadOnlyList<byte> growTypeMaximumLevels,
            int purchaseCost, int requiredLevel, int maximumLevel)
        {
            SkillId = skillId;
            Path = path ?? string.Empty;
            Type = type;
            SkillClass = skillClass;
            FitnessGrowTypes = fitnessGrowTypes ?? new byte[0];
            GrowTypeMaximumLevels = growTypeMaximumLevels ?? new byte[0];
            PurchaseCost = purchaseCost;
            RequiredLevel = requiredLevel;
            MaximumLevel = maximumLevel;
        }
    }
}
