using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DF60A1
{
    internal sealed class CharacterStatBlock
    {
        public uint MaxHp, MaxMp, InventoryLimit, Weight;
        public short Strength, Vitality, Intelligence, Spirit;
        public short FireResistance, WaterResistance, DarkResistance, LightResistance;
        public ushort HpRegen, MpRegen, MoveSpeed, AttackSpeed, CastSpeed, HitRecovery, JumpPower;
    }

    /// <summary>
    /// Builds the USERINFO attribute snapshot from the matching client's .chr files.
    /// PVF character values are decimals while the Arad wire format uses x10 fixed point.
    /// </summary>
    internal sealed class CharacterStatCatalog
    {
        private static readonly string[] JobPaths =
        {
            "character/swordman/swordman.chr",
            "character/fighter/fighter.chr",
            "character/gunner/gunner.chr",
            "character/mage/mage.chr",
            "character/priest/priest.chr"
        };

        private readonly Dictionary<byte, JobGrowth> _jobs = new Dictionary<byte, JobGrowth>();

        public CharacterStatCatalog(PvfArchiveReader archive)
        {
            if (archive == null) return;
            for (byte job = 0; job < JobPaths.Length; job++)
            {
                try
                {
                    uint ignored;
                    var text = Encoding.GetEncoding(949).GetString(archive.ReadAllBytes(JobPaths[job], out ignored))
                        .Replace("\0", string.Empty);
                    _jobs[job] = Parse(text);
                }
                catch
                {
                    // A missing class file must not prevent the server from starting.
                }
            }
        }

        public int Count { get { return _jobs.Count; } }

        public CharacterStatBlock Get(byte job, byte growType, byte level)
        {
            JobGrowth values;
            if (!_jobs.TryGetValue(job, out values)) values = BuiltInBaseJob(job);
            StatValues growth;
            if (!values.Growth.TryGetValue(growType + 1, out growth))
                values.Growth.TryGetValue(1, out growth);
            if (growth == null) growth = new StatValues();

            var levelsGained = Math.Max(0, (int)level - 1);
            return ToWire(Add(values.Initial, growth, levelsGained));
        }

        public CharacterStatBlock Get(CharacterRecord character)
        {
            if (character == null) return Get(0, 0, 1);
            var result = Get(character.Job, character.PrimaryGrowType, character.Level);
            result.MaxHp = AddU32(result.MaxHp, character.BonusMaximumHp);
            result.MaxMp = AddU32(result.MaxMp, character.BonusMaximumMp);
            result.Strength = AddS16(result.Strength, character.BonusStrength);
            result.Intelligence = AddS16(result.Intelligence, character.BonusIntelligence);
            result.Vitality = AddS16(result.Vitality, character.BonusVitality);
            result.Spirit = AddS16(result.Spirit, character.BonusSpirit);
            result.MoveSpeed = AddU16(result.MoveSpeed, character.BonusMovementSpeed);
            result.FireResistance = AddS16(result.FireResistance, character.BonusAllResistance);
            result.WaterResistance = AddS16(result.WaterResistance, character.BonusAllResistance);
            result.DarkResistance = AddS16(result.DarkResistance, character.BonusAllResistance);
            result.LightResistance = AddS16(result.LightResistance, character.BonusAllResistance);
            return result;
        }

        internal static JobGrowth Parse(string text)
        {
            var result = new JobGrowth();
            var initial = Regex.Match(text ?? string.Empty,
                @"(?ims)^\s*\[initial value\].*?(?=^\s*\[skill\])");
            result.Initial = ParseValues(initial.Success ? initial.Value : string.Empty);
            foreach (Match header in Regex.Matches(text ?? string.Empty,
                @"(?im)^\s*\[growtype\s+(\d+)\][^\r\n]*"))
            {
                int number;
                if (!int.TryParse(header.Groups[1].Value, out number)) continue;
                var start = header.Index + header.Length;
                var tail = (text ?? string.Empty).Substring(start);
                var end = Regex.Match(tail, @"(?im)^\s*\[(?:growtype\s+\d+|awakening(?:\s+name|\s+\d+)?)\]");
                result.Growth[number] = ParseValues(end.Success ? tail.Substring(0, end.Index) : tail);
            }
            return result;
        }

        private static StatValues ParseValues(string section)
        {
            var value = new StatValues();
            value.MaxHp = Read(section, "HP MAX");
            value.MaxMp = Read(section, "MP MAX");
            // The historical PVF names describe the stat's role. On the wire these
            // four fields become STR/VIT/INT/SPR, from which the client derives attack/defence.
            value.Strength = Read(section, "physical attack");
            value.Vitality = Read(section, "physical defense");
            value.Intelligence = Read(section, "magical attack");
            value.Spirit = Read(section, "magical defense");
            value.FireResistance = Read(section, "fire resistance");
            value.WaterResistance = Read(section, "water resistance");
            value.DarkResistance = Read(section, "dark resistance");
            value.LightResistance = Read(section, "light resistance");
            value.InventoryLimit = Read(section, "inventory limit");
            value.HpRegen = Read(section, "HP regen speed");
            value.MpRegen = Read(section, "MP regen speed");
            value.MoveSpeed = Read(section, "move speed");
            value.AttackSpeed = Read(section, "attack speed");
            value.CastSpeed = Read(section, "cast speed");
            value.HitRecovery = Read(section, "hit recovery");
            value.JumpPower = Read(section, "jump power");
            value.Weight = Read(section, "weight");
            return value;
        }

        private static decimal Read(string section, string tag)
        {
            var match = Regex.Match(section ?? string.Empty,
                @"(?im)^\s*\[" + Regex.Escape(tag) + @"\]\s*(-?\d+(?:\.\d+)?)");
            decimal value;
            return match.Success && decimal.TryParse(match.Groups[1].Value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out value) ? value : 0m;
        }

        private static StatValues Add(StatValues initial, StatValues growth, int levels)
        {
            var result = new StatValues();
            foreach (var field in typeof(StatValues).GetFields())
            {
                var start = (decimal)field.GetValue(initial);
                var perLevel = (decimal)field.GetValue(growth);
                field.SetValue(result, start + perLevel * levels);
            }
            return result;
        }

        private static CharacterStatBlock ToWire(StatValues value)
        {
            return new CharacterStatBlock
            {
                MaxHp = U32(value.MaxHp), MaxMp = U32(value.MaxMp),
                Strength = S16(value.Strength), Vitality = S16(value.Vitality),
                Intelligence = S16(value.Intelligence), Spirit = S16(value.Spirit),
                FireResistance = S16(value.FireResistance), WaterResistance = S16(value.WaterResistance),
                DarkResistance = S16(value.DarkResistance), LightResistance = S16(value.LightResistance),
                InventoryLimit = U32(value.InventoryLimit), HpRegen = U16(value.HpRegen),
                MpRegen = U16(value.MpRegen), MoveSpeed = U16(value.MoveSpeed),
                AttackSpeed = U16(value.AttackSpeed), CastSpeed = U16(value.CastSpeed),
                HitRecovery = U16(value.HitRecovery), JumpPower = U16(value.JumpPower), Weight = U32(value.Weight)
            };
        }

        private static uint U32(decimal value) { return (uint)Math.Max(0m, Math.Min(uint.MaxValue, Math.Round(value * 10m, MidpointRounding.AwayFromZero))); }
        private static ushort U16(decimal value) { return (ushort)Math.Max(0m, Math.Min(ushort.MaxValue, Math.Round(value * 10m, MidpointRounding.AwayFromZero))); }
        private static short S16(decimal value) { return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(value * 10m, MidpointRounding.AwayFromZero))); }
        private static uint AddU32(uint value, int displayedBonus) { return (uint)Math.Min(uint.MaxValue, value + Math.Max(0L, displayedBonus * 10L)); }
        private static ushort AddU16(ushort value, int displayedBonus) { return (ushort)Math.Min(ushort.MaxValue, value + Math.Max(0L, displayedBonus * 10L)); }
        private static short AddS16(short value, int displayedBonus) { return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value + displayedBonus * 10L)); }

        private static JobGrowth BuiltInBaseJob(byte job)
        {
            var initial = new[]
            {
                new decimal[] {180,140,7,7,4,4,40000,50,850,850,700,600,430,68000},
                new decimal[] {200,120,7,7,4,4,36000,40,910,950,1000,600,470,50000},
                new decimal[] {160,160,6,6,5,5,33000,50,820,950,800,600,490,60000},
                new decimal[] {120,200,4,4,7,7,28000,80,800,1000,1000,500,350,40000},
                new decimal[] {220,100,6,6,4,6,40000,50,750,950,1000,500,500,85000}
            }[Math.Min(job, (byte)4)];
            var growth = new[]
            {
                new decimal[] {45,25,3.3m,3.3m,2.7m,2.7m,300,2.5m},
                new decimal[] {50,20,3.5m,3.5m,2.5m,2.5m,270,2},
                new decimal[] {40,30,3,3,3,3,300,2.5m},
                new decimal[] {35,35,2.5m,2.5m,3.5m,3.5m,250,4},
                new decimal[] {50,20,3.2m,3.3m,2.5m,3,300,2.5m}
            }[Math.Min(job, (byte)4)];
            var result = new JobGrowth { Initial = FromArrays(initial, false) };
            result.Growth[1] = FromArrays(growth, true);
            return result;
        }

        private static StatValues FromArrays(decimal[] values, bool growth)
        {
            var result = new StatValues
            {
                MaxHp = values[0], MaxMp = values[1], Strength = values[2], Vitality = values[3],
                Intelligence = values[4], Spirit = values[5], InventoryLimit = values[6]
            };
            if (growth) result.MpRegen = values[7];
            else
            {
                result.MpRegen = values[7]; result.MoveSpeed = values[8]; result.AttackSpeed = values[9];
                result.CastSpeed = values[10]; result.HitRecovery = values[11]; result.JumpPower = values[12]; result.Weight = values[13];
            }
            return result;
        }

        internal sealed class JobGrowth
        {
            public StatValues Initial = new StatValues();
            public readonly Dictionary<int, StatValues> Growth = new Dictionary<int, StatValues>();
        }

        internal sealed class StatValues
        {
            public decimal MaxHp, MaxMp, Strength, Vitality, Intelligence, Spirit;
            public decimal FireResistance, WaterResistance, DarkResistance, LightResistance;
            public decimal InventoryLimit, HpRegen, MpRegen, MoveSpeed, AttackSpeed, CastSpeed, HitRecovery, JumpPower, Weight;
        }
    }
}
