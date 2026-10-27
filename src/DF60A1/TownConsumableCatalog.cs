using System.Collections.Generic;

namespace DF60A1
{
    internal enum TownConsumableEffectType
    {
        SkillPoints, Experience, Strength, Intelligence, Vitality, Spirit,
        MaximumHp, MaximumMp, MovementSpeed, AllResistance
    }

    internal sealed class TownConsumableEffect
    {
        public TownConsumableEffectType Type;
        public int Amount;
    }

    /// <summary>Permanent town-use effects supplied by the retail game server.</summary>
    internal static class TownConsumableCatalog
    {
        private static readonly IReadOnlyDictionary<ushort, TownConsumableEffect> Effects =
            new Dictionary<ushort, TownConsumableEffect>
            {
                [1031] = Effect(TownConsumableEffectType.SkillPoints, 5),
                [1038] = Effect(TownConsumableEffectType.SkillPoints, 20),
                [1034] = Effect(TownConsumableEffectType.Experience, 100),
                [1035] = Effect(TownConsumableEffectType.Experience, 1000),
                [1036] = Effect(TownConsumableEffectType.Experience, 10000),
                [1037] = Effect(TownConsumableEffectType.Experience, 100000),
                [1039] = Effect(TownConsumableEffectType.Strength, 5),
                [1040] = Effect(TownConsumableEffectType.Intelligence, 5),
                [1041] = Effect(TownConsumableEffectType.Vitality, 5),
                [1042] = Effect(TownConsumableEffectType.Spirit, 5),
                [1043] = Effect(TownConsumableEffectType.MaximumHp, 25),
                [1044] = Effect(TownConsumableEffectType.MaximumMp, 25),
                [1045] = Effect(TownConsumableEffectType.MovementSpeed, 1),
                [1046] = Effect(TownConsumableEffectType.AllResistance, 1)
            };

        public static bool TryGet(ushort itemId, out TownConsumableEffect effect)
        {
            return Effects.TryGetValue(itemId, out effect);
        }

        public static byte GetProtocolType(TownConsumableEffectType type)
        {
            switch (type)
            {
                case TownConsumableEffectType.SkillPoints: return 0;
                case TownConsumableEffectType.Experience: return 1;
                case TownConsumableEffectType.MaximumHp: return 2;
                case TownConsumableEffectType.MaximumMp: return 3;
                case TownConsumableEffectType.Strength: return 4;
                case TownConsumableEffectType.Vitality: return 5;
                case TownConsumableEffectType.Intelligence: return 6;
                case TownConsumableEffectType.Spirit: return 7;
                case TownConsumableEffectType.MovementSpeed: return 8;
                case TownConsumableEffectType.AllResistance: return 9;
                default: return byte.MaxValue;
            }
        }

        public static uint GetWireAmount(TownConsumableEffect effect)
        {
            if (effect == null) return 0;
            var scale = effect.Type == TownConsumableEffectType.SkillPoints ||
                effect.Type == TownConsumableEffectType.Experience ? 1L : 10L;
            return (uint)System.Math.Min(uint.MaxValue, System.Math.Max(0L, effect.Amount * scale));
        }

        private static TownConsumableEffect Effect(TownConsumableEffectType type, int amount)
        {
            return new TownConsumableEffect { Type = type, Amount = amount };
        }
    }
}
