using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DF60A1
{
    internal enum IndependentDropKind
    {
        Gold,
        Consumable,
        Equipment,
        Recipe,
        Artifact
    }

    internal struct IndependentDropResult
    {
        public int ItemId;
        public uint Count;
        public IndependentDropKind Kind;
    }

    internal static class OfflineDropRules
    {
        // Offline test runs use a generous global loot-rate multiplier so
        // normal dungeon clears reliably exercise the drop/pickup flow.
        public const int MonsterDropChanceMultiplier = 10;
        public const ushort RyanCokesItemId = 3171;
        public const int RyanCokesMultiplier = 10;

        public static uint ScaleItemCount(int itemId, uint count)
        {
            if (count == 0 || itemId != RyanCokesItemId) return count;
            return (uint)Math.Min(uint.MaxValue, (ulong)count * RyanCokesMultiplier);
        }

        public static int ScaleDropChance(int itemId, int chance, int denominator)
        {
            if (chance <= 0 || denominator <= 0) return 0;
            var scaled = (long)chance * MonsterDropChanceMultiplier;
            return (int)Math.Min(denominator, Math.Max(0L, scaled));
        }

        public static long NextWeighted(Random random, long maxExclusive)
        {
            if (random == null) throw new ArgumentNullException("random");
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException("maxExclusive");
            return maxExclusive <= int.MaxValue
                ? random.Next((int)maxExclusive)
                : (long)(random.NextDouble() * maxExclusive);
        }
    }

    /// <summary>
    /// Implements etc/itemdropinfo_monseter.etc. This is the normal source of
    /// gold, equipment and consumable drops; monster/*.mob is only an extra corpse table.
    /// </summary>
    internal sealed class MonsterIndependentDropCatalog
    {
        private struct DropProbRow
        {
            public int LevelMin;
            public int LevelMax;
            public int Gold;
            public int Consumable;
            public int Equipment;
            public int Recipe;
            public int Artifact;
        }

        private static readonly Regex DropMetaRegex = new Regex(
            @"(?im)^\s*\[(grade|rarity|creation\s+rate|stackable\s+type)\]\s*(.+)$",
            RegexOptions.Compiled);

        private DropProbRow[] _dropProbabilities = new DropProbRow[0];
        private readonly int[,] _rarityThresholds = new int[4, 5];
        private readonly float[,] _partyBonus = IdentityBonus(5, 4);
        private readonly float[,] _difficultyBonus = IdentityBonus(5, 4);
        private readonly float[,] _monsterTypeBonus = IdentityBonus(5, 4);
        private readonly int[] _gradeDown = new int[200];
        private readonly int[] _gradeUp = new int[200];
        private readonly Dictionary<long, List<KeyValuePair<int, int>>> _equipmentPool =
            new Dictionary<long, List<KeyValuePair<int, int>>>();
        private readonly Dictionary<long, List<KeyValuePair<int, int>>> _consumablePool =
            new Dictionary<long, List<KeyValuePair<int, int>>>();
        private readonly Dictionary<long, List<KeyValuePair<int, int>>> _recipePool =
            new Dictionary<long, List<KeyValuePair<int, int>>>();
        private readonly Dictionary<long, List<KeyValuePair<int, int>>> _artifactPool =
            new Dictionary<long, List<KeyValuePair<int, int>>>();
        private readonly MonsterGoldCatalog _goldCatalog;
        private readonly Random _random = new Random();

        public int DropProbabilityCount { get { return _dropProbabilities.Length; } }
        public int EquipmentBucketCount { get { return _equipmentPool.Count; } }

        public MonsterIndependentDropCatalog(PvfArchiveReader archive, MonsterGoldCatalog goldCatalog)
        {
            _goldCatalog = goldCatalog;
            for (var index = 0; index < 200; index++)
            {
                _gradeDown[index] = 3;
                _gradeUp[index] = 3;
            }
            if (archive == null) return;
            try
            {
                uint fileNumber;
                var text = Decode(archive.ReadAllBytes("etc/itemdropinfo_monseter.etc", out fileNumber));
                _dropProbabilities = ParseDropProbabilities(text);
                ParseRarityThresholds(text, _rarityThresholds);
                ParseFloatTable(text, "party member drop bonusrate", _partyBonus);
                ParseFloatTable(text, "dungeon difficulty drop bonusrate", _difficultyBonus);
                ParseFloatTable(text, "monster type drop bonusrate", _monsterTypeBonus);
                ParseGradeRanges(text, _gradeDown, _gradeUp);
                BuildEquipmentPool(archive);
                BuildStackablePools(archive);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("无法构建 MonsterIndependentDropCatalog: " + exception.Message);
            }
        }

        public List<IndependentDropResult> Roll(int monsterLevel, int monsterType, int difficulty, int partySize)
        {
            lock (_random)
            {
                var result = new List<IndependentDropResult>();
                var level = Math.Max(1, Math.Min(200, monsterLevel));
                var type = Math.Max(0, Math.Min(3, monsterType));
                var difficultyIndex = Math.Max(0, Math.Min(3, difficulty));
                var partyIndex = Math.Max(1, Math.Min(4, partySize)) - 1;
                var rates = FindRates(level);
                TryRollCategory(result, IndependentDropKind.Gold,
                    ScaleRate(rates.Gold, 0, partyIndex, difficultyIndex, type), level);
                TryRollCategory(result, IndependentDropKind.Consumable,
                    ScaleRate(rates.Consumable, 1, partyIndex, difficultyIndex, type), level);
                TryRollCategory(result, IndependentDropKind.Equipment,
                    ScaleRate(rates.Equipment, 2, partyIndex, difficultyIndex, type), level);
                TryRollCategory(result, IndependentDropKind.Recipe,
                    ScaleRate(rates.Recipe, 3, partyIndex, difficultyIndex, type), level);
                TryRollCategory(result, IndependentDropKind.Artifact,
                    ScaleRate(rates.Artifact, 4, partyIndex, difficultyIndex, type), level);
                return result;
            }
        }

        private void TryRollCategory(ICollection<IndependentDropResult> output, IndependentDropKind kind,
            int rate, int level)
        {
            if (rate <= 0 || _random.Next(10000) >= Math.Min(10000, rate)) return;
            if (kind == IndependentDropKind.Gold)
            {
                var variance = 15;
                var amount = _goldCatalog == null
                    ? Math.Max(20, 20 + (level - 1) * 8)
                    : _goldCatalog.GetMonsterGold(level, out variance);
                var delta = variance > 0 ? _random.Next(variance * 2 + 1) - variance : 0;
                amount = Math.Max(1, amount + amount * delta / 100);
                output.Add(new IndependentDropResult
                {
                    ItemId = 0,
                    Count = (uint)amount,
                    Kind = kind
                });
                return;
            }

            var rarityRow = kind == IndependentDropKind.Consumable ? 0 :
                kind == IndependentDropKind.Equipment ? 1 :
                kind == IndependentDropKind.Recipe ? 2 : 3;
            var rarity = RollRarity(rarityRow);
            int itemId;
            if (!TryPickItem(kind, level, rarity, out itemId) && rarity > 0)
                TryPickItem(kind, level, 0, out itemId);
            if (itemId > 0)
                output.Add(new IndependentDropResult
                {
                    ItemId = itemId,
                    Count = OfflineDropRules.ScaleItemCount(itemId, 1),
                    Kind = kind
                });
        }

        private int ScaleRate(int rate, int category, int party, int difficulty, int monsterType)
        {
            var scaled = rate * _partyBonus[category, party] * _difficultyBonus[category, difficulty] *
                _monsterTypeBonus[category, monsterType];
            return OfflineDropRules.ScaleDropChance(0, (int)Math.Round(scaled), 10000);
        }

        private int RollRarity(int row)
        {
            var roll = _random.Next(10000) + 1;
            for (var rarity = 0; rarity < 5; rarity++)
                if (roll <= _rarityThresholds[row, rarity]) return rarity;
            return 0;
        }

        public bool TryPick(IndependentDropKind kind, int level, int rarity, Random random, out int itemId)
        {
            var normalized = Math.Max(1, Math.Min(200, level));
            return TryPick(kind, normalized, rarity, _gradeDown[normalized - 1],
                _gradeUp[normalized - 1], random, out itemId);
        }

        public bool TryPick(IndependentDropKind kind, int level, int rarity, int gradeDown, int gradeUp,
            Random random, out int itemId)
        {
            itemId = 0;
            if (random == null || kind == IndependentDropKind.Gold) return false;
            var pool = kind == IndependentDropKind.Equipment ? _equipmentPool :
                kind == IndependentDropKind.Consumable ? _consumablePool :
                kind == IndependentDropKind.Recipe ? _recipePool : _artifactPool;
            return TrySelectWeighted(CollectWeighted(pool, Math.Max(1, Math.Min(200, level)),
                rarity, gradeDown, gradeUp), random, out itemId);
        }

        private bool TryPickItem(IndependentDropKind kind, int level, int rarity, out int itemId)
        {
            return TryPick(kind, level, rarity, _random, out itemId);
        }

        private static List<KeyValuePair<int, int>> CollectWeighted(
            IDictionary<long, List<KeyValuePair<int, int>>> pool, int level, int rarity,
            int gradeDown, int gradeUp)
        {
            var result = new List<KeyValuePair<int, int>>();
            for (var offset = -gradeDown; offset < gradeUp; offset++)
            {
                var grade = level + offset;
                List<KeyValuePair<int, int>> bucket;
                if (grade >= 1 && grade <= 200 && pool.TryGetValue((long)grade * 10 + rarity, out bucket))
                    result.AddRange(bucket);
            }
            return result;
        }

        internal static bool TrySelectWeighted(IReadOnlyList<KeyValuePair<int, int>> candidates,
            Random random, out int itemId)
        {
            itemId = 0;
            if (candidates == null || candidates.Count == 0 || random == null) return false;
            long total = 0;
            for (var index = 0; index < candidates.Count; index++) total += Math.Max(0, candidates[index].Value);
            if (total <= 0) return false;
            var roll = OfflineDropRules.NextWeighted(random, total);
            long cursor = 0;
            for (var index = 0; index < candidates.Count; index++)
            {
                cursor += Math.Max(0, candidates[index].Value);
                if (roll < cursor)
                {
                    itemId = candidates[index].Key;
                    return itemId > 0;
                }
            }
            itemId = candidates[candidates.Count - 1].Key;
            return itemId > 0;
        }

        private DropProbRow FindRates(int level)
        {
            foreach (var row in _dropProbabilities)
                if (level >= row.LevelMin && level <= row.LevelMax) return row;
            return new DropProbRow
            {
                LevelMin = 1,
                LevelMax = 99,
                Gold = 875,
                Consumable = 10,
                Equipment = 32,
                Recipe = 3,
                Artifact = 5
            };
        }

        private void BuildEquipmentPool(PvfArchiveReader archive)
        {
            uint fileNumber;
            foreach (var entry in EquipmentCatalog.ParseListEntries(
                Decode(archive.ReadAllBytes("equipment/equipment.lst", out fileNumber))))
            {
                if (entry.Value.IndexOf("/avatar/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entry.Value.IndexOf("/cash/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try
                {
                    int grade;
                    int rarity;
                    int creationRate;
                    string stackType;
                    if (TryParseDropMeta(Decode(archive.ReadAllBytes(entry.Value, out fileNumber)),
                        out grade, out rarity, out creationRate, out stackType) && creationRate > 0 &&
                        grade > 0 && rarity >= 0 && rarity <= 4)
                        AddWeighted(_equipmentPool, grade, rarity, entry.Key, creationRate);
                }
                catch { }
            }
        }

        private void BuildStackablePools(PvfArchiveReader archive)
        {
            uint fileNumber;
            foreach (var entry in ItemCategoryCatalog.ParseStackableListEntries(
                Decode(archive.ReadAllBytes("stackable/stackable.lst", out fileNumber))))
            {
                try
                {
                    int grade;
                    int rarity;
                    int creationRate;
                    string stackType;
                    if (!TryParseDropMeta(Decode(archive.ReadAllBytes(entry.Value, out fileNumber)),
                        out grade, out rarity, out creationRate, out stackType) || creationRate <= 0 ||
                        grade <= 0 || rarity < 0 || rarity > 4) continue;
                    var path = entry.Value.Replace('\\', '/').ToLowerInvariant();
                    var isRecipe = path.IndexOf("/recipe/", StringComparison.Ordinal) >= 0 ||
                        string.Equals(stackType, "recipe", StringComparison.OrdinalIgnoreCase);
                    var isMaterial = string.Equals(stackType, "material", StringComparison.OrdinalIgnoreCase);
                    var isQuest = string.Equals(stackType, "quest", StringComparison.OrdinalIgnoreCase) ||
                        path.IndexOf("/quest/", StringComparison.Ordinal) >= 0;
                    var isCash = path.IndexOf("/cash/", StringComparison.Ordinal) >= 0 ||
                        path.IndexOf("cera", StringComparison.Ordinal) >= 0;
                    if (isQuest || isCash) continue;
                    var weight = OfflineDropRules.ScaleDropChance(entry.Key, creationRate, int.MaxValue);
                    AddWeighted(isRecipe ? _recipePool : isMaterial ? _artifactPool : _consumablePool,
                        grade, rarity, entry.Key, weight);
                }
                catch { }
            }
        }

        internal static bool TryParseDropMeta(string text, out int grade, out int rarity,
            out int creationRate, out string stackType)
        {
            grade = 0;
            rarity = 0;
            creationRate = 0;
            stackType = null;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (Match match in DropMetaRegex.Matches(text))
            {
                var tag = match.Groups[1].Value.Trim().ToLowerInvariant().Replace(" ", string.Empty);
                var body = match.Groups[2].Value.Trim();
                switch (tag)
                {
                    case "grade": int.TryParse(Token(body), NumberStyles.Integer, CultureInfo.InvariantCulture, out grade); break;
                    case "rarity": int.TryParse(Token(body), NumberStyles.Integer, CultureInfo.InvariantCulture, out rarity); break;
                    case "creationrate": int.TryParse(Token(body), NumberStyles.Integer, CultureInfo.InvariantCulture, out creationRate); break;
                    case "stackabletype":
                        if (stackType == null)
                        {
                            var value = body;
                            var open = value.IndexOf('[');
                            var close = value.IndexOf(']');
                            if (open >= 0 && close > open) value = value.Substring(open + 1, close - open - 1);
                            stackType = value.Trim().Trim('`');
                        }
                        break;
                }
            }
            return grade > 0;
        }

        private static string Token(string body)
        {
            if (string.IsNullOrEmpty(body)) return "0";
            var values = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return values.Length == 0 ? "0" : values[0].Trim('`');
        }

        private static void AddWeighted(IDictionary<long, List<KeyValuePair<int, int>>> pool,
            int grade, int rarity, int itemId, int weight)
        {
            var key = (long)grade * 10 + rarity;
            List<KeyValuePair<int, int>> bucket;
            if (!pool.TryGetValue(key, out bucket))
            {
                bucket = new List<KeyValuePair<int, int>>();
                pool[key] = bucket;
            }
            bucket.Add(new KeyValuePair<int, int>(itemId, weight));
        }

        private static DropProbRow[] ParseDropProbabilities(string text)
        {
            var values = ParseIntegers(ExtractSection(text, "drop prob"));
            var result = new DropProbRow[values.Length / 7];
            for (var index = 0; index < result.Length; index++)
            {
                var offset = index * 7;
                result[index] = new DropProbRow
                {
                    LevelMin = values[offset], LevelMax = values[offset + 1], Gold = values[offset + 2],
                    Consumable = values[offset + 3], Equipment = values[offset + 4],
                    Recipe = values[offset + 5], Artifact = values[offset + 6]
                };
            }
            return result;
        }

        private static void ParseRarityThresholds(string text, int[,] thresholds)
        {
            var values = ParseIntegers(ExtractSection(text, "basis of rarity dicision"));
            if (values.Length < 20) return;
            for (var row = 0; row < 4; row++)
                for (var column = 0; column < 5; column++) thresholds[row, column] = values[row * 5 + column];
        }

        private static void ParseFloatTable(string text, string section, float[,] target)
        {
            var values = ParseFloats(ExtractSection(text, section));
            if (values.Length < target.Length) return;
            for (var row = 0; row < target.GetLength(0); row++)
                for (var column = 0; column < target.GetLength(1); column++)
                    target[row, column] = values[row * target.GetLength(1) + column];
        }

        private static void ParseGradeRanges(string text, int[] down, int[] up)
        {
            var values = ParseIntegers(ExtractSection(text, "item drop ref table"));
            for (var offset = 0; offset + 2 < values.Length; offset += 3)
            {
                var level = values[offset];
                if (level < 1 || level > down.Length) continue;
                down[level - 1] = values[offset + 1];
                up[level - 1] = values[offset + 2];
            }
        }

        private static float[,] IdentityBonus(int rows, int columns)
        {
            var result = new float[rows, columns];
            for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++) result[row, column] = 1f;
            return result;
        }

        internal static string ExtractSection(string text, string name)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var tag = "[" + name + "]";
            var start = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += tag.Length;
            var end = text.IndexOf('[', start);
            if (end < 0) end = text.Length;
            return text.Substring(start, end - start);
        }

        internal static int[] ParseIntegers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new int[0];
            var result = new List<int>();
            foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int value;
                if (!token.StartsWith("//", StringComparison.Ordinal) &&
                    int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) result.Add(value);
            }
            return result.ToArray();
        }

        private static float[] ParseFloats(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new float[0];
            var result = new List<float>();
            foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                float value;
                if (!token.StartsWith("//", StringComparison.Ordinal) &&
                    float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) result.Add(value);
            }
            return result.ToArray();
        }

        internal static string Decode(byte[] bytes)
        {
            return Encoding.GetEncoding(949).GetString(bytes ?? new byte[0]).Replace("\0", string.Empty);
        }
    }

    internal sealed class MonsterGoldCatalog
    {
        private readonly Dictionary<int, int> _amounts = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _variance = new Dictionary<int, int>();

        public int Count { get { return _amounts.Count; } }

        public MonsterGoldCatalog(PvfArchiveReader archive)
        {
            if (archive == null) return;
            try
            {
                uint fileNumber;
                Parse(MonsterIndependentDropCatalog.Decode(
                    archive.ReadAllBytes("etc/itemdropinfo_common.etc", out fileNumber)));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("无法构建 MonsterGoldCatalog: " + exception.Message);
            }
        }

        public int GetMonsterGold(int level, out int variancePercent)
        {
            var normalized = Math.Max(1, level);
            int amount;
            if (_amounts.TryGetValue(normalized, out amount))
            {
                variancePercent = _variance.ContainsKey(normalized) ? _variance[normalized] : 15;
                return Math.Max(0, amount);
            }
            variancePercent = 15;
            return Math.Max(20, 20 + (normalized - 1) * 8);
        }

        private void Parse(string text)
        {
            var section = MonsterIndependentDropCatalog.ExtractSection(text, "gold drop ref table");
            foreach (var rawLine in section.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var values = MonsterIndependentDropCatalog.ParseIntegers(rawLine);
                if (values.Length < 2 || values[0] <= 0) continue;
                _amounts[values[0]] = values[1];
                if (values.Length >= 3) _variance[values[0]] = values[2];
            }
        }
    }

    /// <summary>Weighted rare-item table from etc/worlddrop.etc.</summary>
    internal sealed class WorldDropCatalog
    {
        private struct Row
        {
            public int Level;
            public long TotalWeight;
            public KeyValuePair<int, int>[] Items;
        }

        private Row[] _rows = new Row[0];
        public int RowCount { get { return _rows.Length; } }

        public WorldDropCatalog(PvfArchiveReader archive)
        {
            if (archive == null) return;
            try
            {
                uint fileNumber;
                _rows = Parse(MonsterIndependentDropCatalog.Decode(
                    archive.ReadAllBytes("etc/worlddrop.etc", out fileNumber)));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("无法构建 WorldDropCatalog: " + exception.Message);
            }
        }

        public bool TryRoll(int monsterLevel, Random random, out int itemId)
        {
            itemId = 0;
            if (random == null || _rows.Length == 0) return false;
            var level = Math.Max(1, monsterLevel);
            Row selected = default(Row);
            var found = false;
            foreach (var row in _rows)
            {
                if (row.Level == level)
                {
                    selected = row;
                    found = true;
                    break;
                }
                if (row.Level <= level)
                {
                    selected = row;
                    found = true;
                }
            }
            if (!found || selected.TotalWeight <= 0 || selected.Items == null || selected.Items.Length == 0 ||
                random.Next(100000) >= Math.Min(100000L, selected.TotalWeight)) return false;
            var roll = OfflineDropRules.NextWeighted(random, selected.TotalWeight);
            long cursor = 0;
            foreach (var item in selected.Items)
            {
                cursor += Math.Max(0, item.Value);
                if (roll < cursor)
                {
                    itemId = item.Key;
                    return itemId > 0;
                }
            }
            itemId = selected.Items[selected.Items.Length - 1].Key;
            return itemId > 0;
        }

        private static Row[] Parse(string text)
        {
            var result = new List<Row>();
            var start = (text ?? string.Empty).IndexOf("[world drop]", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return result.ToArray();
            foreach (var rawLine in text.Substring(start + "[world drop]".Length)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) ||
                    line.StartsWith("`", StringComparison.Ordinal)) continue;
                var values = new List<int>();
                foreach (var token in Regex.Split(line, @"\s+"))
                {
                    int value;
                    if (token.Length != 0 && !token.StartsWith("//", StringComparison.Ordinal) &&
                        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) values.Add(value);
                }
                if (values.Count < 3) continue;
                var items = new List<KeyValuePair<int, int>>();
                long total = 0;
                for (var offset = 2; offset + 1 < values.Count && values[offset] >= 0; offset += 2)
                {
                    var weight = OfflineDropRules.ScaleDropChance(values[offset], values[offset + 1], int.MaxValue);
                    if (values[offset] <= 0 || weight <= 0) continue;
                    items.Add(new KeyValuePair<int, int>(values[offset], weight));
                    total += weight;
                }
                if (items.Count != 0) result.Add(new Row
                {
                    Level = values[0],
                    TotalWeight = total,
                    Items = items.ToArray()
                });
            }
            return result.ToArray();
        }
    }
}
