using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DF60A1
{
    /// <summary>Reads the per-monster corpse drop tables from monster/*.mob.</summary>
    internal sealed class MonsterCatalog
    {
        public readonly struct DropEntry
        {
            public readonly int ItemId;
            public readonly int Chance;

            public DropEntry(int itemId, int chance)
            {
                ItemId = itemId;
                Chance = chance;
            }
        }

        public readonly struct SpecificDropEntry
        {
            public readonly int ItemId;
            public readonly int Chance;

            public SpecificDropEntry(int itemId, int chance)
            {
                ItemId = itemId;
                Chance = chance;
            }
        }

        public sealed class Definition
        {
            public int MonsterIndex;
            public string SourcePath;
            public readonly List<DropEntry> Drops = new List<DropEntry>();
            public readonly List<DropEntry> ChampionDrops = new List<DropEntry>();
            public int SpecificRate = 1000000;
            public readonly List<SpecificDropEntry> SpecificDrops = new List<SpecificDropEntry>();
        }

        private readonly Dictionary<int, Definition> _byIndex = new Dictionary<int, Definition>();
        private readonly Random _random;

        public int Count { get { return _byIndex.Count; } }
        public int SpecificDropCount { get { return _byIndex.Values.Sum(value => value.SpecificDrops.Count); } }

        public MonsterCatalog(PvfArchiveReader archive) : this(archive, null) { }

        internal MonsterCatalog(PvfArchiveReader archive, Random random)
        {
            _random = random ?? new Random();
            if (archive == null) return;
            try
            {
                LoadFromArchive(archive);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("无法构建 MonsterCatalog: " + exception.Message);
            }
        }

        private void LoadFromArchive(PvfArchiveReader archive)
        {
            uint fileNumber;
            var listText = Decode(archive.ReadAllBytes("monster/monster.lst", out fileNumber));
            var paths = new Dictionary<int, string>();
            foreach (var rawLine in listText.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                var separator = line.IndexOfAny(new[] { ' ', '\t' });
                int monsterIndex;
                if (separator <= 0 || !int.TryParse(line.Substring(0, separator).Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out monsterIndex)) continue;
                var remainder = line.Substring(separator).Trim();
                var pathStart = remainder.IndexOf('`');
                var pathEnd = pathStart < 0 ? -1 : remainder.IndexOf('`', pathStart + 1);
                if (pathStart < 0 || pathEnd < 0) continue;
                paths[monsterIndex] = remainder.Substring(pathStart + 1, pathEnd - pathStart - 1)
                    .Replace('\\', '/').ToLowerInvariant();
            }

            foreach (var pair in paths)
            {
                try
                {
                    var definition = ParseMob(Decode(archive.ReadAllBytes("monster/" + pair.Value, out fileNumber)));
                    if (definition == null) continue;
                    definition.MonsterIndex = pair.Key;
                    definition.SourcePath = pair.Value;
                    _byIndex[pair.Key] = definition;
                }
                catch
                {
                    // A corrupt monster entry must not prevent all other PVF drops from loading.
                }
            }
        }

        internal static Definition ParseMob(string text)
        {
            var definition = new Definition();
            ParseDropBlock(text, "[item]", definition.Drops);
            ParseDropBlock(text, "[common champion drop item]", definition.ChampionDrops);
            ParseSpecificDropBlock(text, definition);
            return definition.Drops.Count != 0 || definition.ChampionDrops.Count != 0 ||
                definition.SpecificDrops.Count != 0 ? definition : null;
        }

        private static void ParseSpecificDropBlock(string text, Definition definition)
        {
            const string openTag = "[specific gen rate item]";
            const string closeTag = "[/specific gen rate item]";
            var open = (text ?? string.Empty).IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            if (open < 0) return;
            var bodyStart = open + openTag.Length;
            var close = text.IndexOf(closeTag, bodyStart, StringComparison.OrdinalIgnoreCase);
            if (close < 0) close = text.Length;
            var firstValue = true;
            foreach (var rawLine in text.Substring(bodyStart, close - bodyStart).Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                var values = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                int itemId;
                if (values.Length == 0 || !int.TryParse(values[0], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out itemId)) continue;
                if (firstValue && values.Length == 1)
                {
                    definition.SpecificRate = Math.Max(0, Math.Min(1000000, itemId));
                    firstValue = false;
                    continue;
                }
                firstValue = false;
                int chance;
                if (values.Length >= 2 && itemId > 0 && itemId <= ushort.MaxValue &&
                    int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out chance) && chance > 0)
                    definition.SpecificDrops.Add(new SpecificDropEntry(itemId,
                        Math.Max(0, Math.Min(1000000, chance))));
            }
        }

        private static void ParseDropBlock(string text, string openTag, ICollection<DropEntry> output)
        {
            var open = (text ?? string.Empty).IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            if (open < 0) return;
            var bodyStart = open + openTag.Length;
            var close = text.IndexOf("[/" + openTag.Substring(1), bodyStart, StringComparison.OrdinalIgnoreCase);
            if (close < 0) close = text.Length;
            foreach (var rawLine in text.Substring(bodyStart, close - bodyStart).Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                var values = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                int itemId;
                int chance;
                if (values.Length >= 2 &&
                    int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId) &&
                    int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out chance) &&
                    itemId > 0 && itemId <= ushort.MaxValue && chance > 0)
                    output.Add(new DropEntry(itemId, chance));
            }
        }

        public bool TryGet(int monsterIndex, out Definition definition)
        {
            return _byIndex.TryGetValue(monsterIndex, out definition);
        }

        public List<int> RollDrops(int monsterIndex)
        {
            return RollTable(monsterIndex, false);
        }

        public List<int> RollChampionDrops(int monsterIndex)
        {
            return RollTable(monsterIndex, true);
        }

        public List<int> RollSpecificDrops(int monsterIndex)
        {
            var result = new List<int>();
            Definition definition;
            if (!_byIndex.TryGetValue(monsterIndex, out definition) || definition.SpecificDrops.Count == 0)
                return result;
            var gate = OfflineDropRules.ScaleDropChance(0, definition.SpecificRate, 1000000);
            if (gate <= 0 || _random.Next(1000000) >= gate) return result;
            foreach (var entry in definition.SpecificDrops)
            {
                // Specific tables use millionth-rate values and are scaled by
                // the common offline drop multiplier before the roll.
                var chance = OfflineDropRules.ScaleDropChance(0, entry.Chance, 1000000);
                if (chance > 0 && _random.Next(1000000) < chance) result.Add(entry.ItemId);
            }
            return result;
        }

        private List<int> RollTable(int monsterIndex, bool champion)
        {
            var result = new List<int>();
            Definition definition;
            if (!_byIndex.TryGetValue(monsterIndex, out definition)) return result;
            foreach (var entry in champion ? definition.ChampionDrops : definition.Drops)
            {
                // The PVF value is a ten-thousandths probability; the shared
                // offline multiplier is applied before the roll.
                var chance = OfflineDropRules.ScaleDropChance(entry.ItemId, entry.Chance, 10000);
                if (chance > 0 && _random.Next(10000) < chance) result.Add(entry.ItemId);
            }
            return result;
        }

        private static string Decode(byte[] bytes)
        {
            return Encoding.GetEncoding(949).GetString(bytes ?? new byte[0]).Replace("\0", string.Empty);
        }
    }
}
