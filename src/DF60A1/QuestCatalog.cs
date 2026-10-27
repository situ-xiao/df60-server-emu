using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DF60A1
{
    internal enum QuestGrade
    {
        Epic,
        CommonUnique,
        NormalRepeat,
        Unknown
    }

    internal enum QuestConditionType
    {
        Unknown,
        Seeking,
        ConditionUnderClear,
        HuntMonster,
        ClearMap,
        MeetNpc
    }

    internal sealed class QuestDefinition
    {
        public ushort Id;
        public string ScriptPath;
        public QuestGrade Grade;
        public int NpcIndex = -1;
        public int CompleteNpcIndex = -1;
        public string Job = "all";
        public int GrowType = -1;
        public int MinimumLevel = 1;
        public int MaximumLevel = 99;
        public ushort[] PrerequisiteQuestIds = new ushort[0];
        public ushort[] CollisionQuestIds = new ushort[0];
        public QuestConditionType ConditionType;
        public int ConditionSubType = -1;
        public int[][] ConditionRows = new int[0][];
        public string RewardType = "item";
        public int[][] RewardRows = new int[0][];
        public int[][] RewardSelectionRows = new int[0][];
        public int[][] DependGiveItemRows = new int[0][];
        public QuestClearRewardItemDefinition[] ClearRewardItems = new QuestClearRewardItemDefinition[0];
        public QuestMonsterRewardItemDefinition[] MonsterRewardItems = new QuestMonsterRewardItemDefinition[0];

        public bool IsRepeatable { get { return Grade == QuestGrade.NormalRepeat; } }
    }

    /// <summary>
    /// Loads the authoritative quest list and every referenced QST directly
    /// from the Arad client's Script.pvf. The client already owns localized
    /// names and dialog text; the server needs the IDs and rules which decide
    /// what can be accepted, progressed and rewarded.
    /// </summary>
    internal sealed class QuestCatalog
    {
        /// <summary>
        /// 可接任务通知(notification 21)的上限。旧版客户端在可接任务过多时
        /// 会出现随机卡死（实测 26 个长期正常、56 个必卡），因此服务端默认
        /// 只发送前 N 个。可用 --max-quests 调整。
        /// </summary>
        public static int MaxAcceptableQuests = 255;

        // This is the active-quest capacity, separate from the size of
        // notification 21. ARAD command 4 has exactly ten persisted quest
        // restore slots, so allowing more would leave hidden database rows
        // that appear to rotate into the list after another quest is removed.
        public static int MaxActiveQuests = 10;

        private static readonly Regex ListEntryRegex = new Regex(
            @"(?m)^\s*(\d+)\s+`([^`]+\.qst)`", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IntegerRegex = new Regex(@"-?\d+", RegexOptions.Compiled);
        private readonly Dictionary<ushort, QuestDefinition> _definitions;

        public QuestCatalog(string scriptPvfPath)
            : this(new PvfArchiveReader(scriptPvfPath))
        {
        }

        internal QuestCatalog(PvfArchiveReader archive)
        {
            uint ignored;
            var listText = Decode(archive.ReadAllBytes("quest/quest.lst", out ignored));
            _definitions = new Dictionary<ushort, QuestDefinition>();
            foreach (var entry in ParseListEntries(listText))
            {
                try
                {
                    var path = "quest/" + entry.Value.Replace('\\', '/').TrimStart('/');
                    var definitionText = Decode(archive.ReadAllBytes(path, out ignored));
                    _definitions[entry.Key] = ParseDefinition(entry.Key, path, definitionText);
                }
                catch (FileNotFoundException)
                {
                    // quest.lst contains only enabled (non-commented) rows,
                    // but keeping one malformed row from disabling the other
                    // eight hundred definitions makes startup recoverable.
                }
            }
        }

        internal QuestCatalog(IEnumerable<QuestDefinition> definitions)
        {
            _definitions = (definitions ?? Enumerable.Empty<QuestDefinition>())
                .Where(value => value != null).ToDictionary(value => value.Id);
        }

        public int Count { get { return _definitions.Count; } }
        public IEnumerable<QuestDefinition> Definitions { get { return _definitions.Values; } }

        public bool TryGet(ushort questId, out QuestDefinition definition)
        {
            return _definitions.TryGetValue(questId, out definition);
        }

        public ushort[] GetAcceptableQuestIds(CharacterRecord character,
            IEnumerable<CharacterQuestRecord> activeQuests, ISet<ushort> completedQuestIds)
        {
            var activeIds = new HashSet<ushort>((activeQuests ?? Enumerable.Empty<CharacterQuestRecord>())
                .Select(value => value.QuestId));
            var completed = completedQuestIds ?? new HashSet<ushort>();
            var result = new List<ushort>();

            // Notification 21 is an offer list, not an active-quest restore
            // list.  Active quests are restored by SELECT_CHARACTER.  If an
            // active ID is included here, this Arad client treats it as a new
            // offer and sends ACCEPT_QUEST again, which produces the apparent
            // "some quests cannot be accepted" error.
            result.AddRange(_definitions.Values
                .Where(value => !activeIds.Contains(value.Id) && IsAvailable(value, character, activeIds, completed))
                .OrderBy(value => GradeOrder(value.Grade))
                .ThenBy(value => value.Id)
                .Select(value => value.Id));
            var distinct = result.Distinct().ToArray();
            if (distinct.Length > MaxAcceptableQuests)
                return distinct.Take(MaxAcceptableQuests).ToArray();
            return distinct;
        }

        public bool CanAcceptQuest(CharacterRecord character, ushort questId,
            IEnumerable<CharacterQuestRecord> activeQuests, ISet<ushort> completedQuestIds)
        {
            QuestDefinition definition;
            if (!_definitions.TryGetValue(questId, out definition)) return false;

            var activeIds = new HashSet<ushort>((activeQuests ?? Enumerable.Empty<CharacterQuestRecord>())
                .Select(value => value.QuestId));
            var completed = completedQuestIds ?? new HashSet<ushort>();
            if (activeIds.Contains(questId) || completed.Contains(questId) || character == null)
                return false;

            // "冒险的开始" (quest 1) is the Arad client's introductory
            // quest.  Its PVF entry carries a growth-type value that is a
            // display/tutorial marker, not an acceptance restriction.  The
            // normal IsAvailable check incorrectly rejects it for characters
            // whose growth state has already been initialized by the server.
            if (questId == 1)
                return character.Level >= definition.MinimumLevel &&
                    character.Level <= definition.MaximumLevel &&
                    MatchesJob(definition.Job, character.Job);

            return IsAvailable(definition, character, activeIds, completed);
        }

        public static bool IsSeekingConditionSatisfied(QuestDefinition definition,
            IEnumerable<InventoryItemRecord> inventory, int gold)
        {
            if (definition == null || definition.ConditionType != QuestConditionType.Seeking) return false;
            var counts = (inventory ?? Enumerable.Empty<InventoryItemRecord>())
                .GroupBy(value => value.ItemId)
                .ToDictionary(group => (int)group.Key, group => group.Sum(value => (long)value.Count));
            return definition.ConditionRows.Length > 0 && definition.ConditionRows.All(row =>
                row.Length >= 2 && row[1] >= 0 &&
                (row[0] == 0 ? gold >= row[1] : row[0] > 0 && counts.ContainsKey(row[0]) && counts[row[0]] >= row[1]));
        }

        public static int GetSeekingGoldRequirement(QuestDefinition definition)
        {
            if (definition == null || definition.ConditionType != QuestConditionType.Seeking) return 0;
            var total = definition.ConditionRows.Where(row => row.Length >= 2 && row[0] == 0 && row[1] > 0)
                .Sum(row => (long)row[1]);
            return (int)Math.Min(int.MaxValue, total);
        }

        public static byte GetInitialTrigger(QuestDefinition definition,
            IEnumerable<InventoryItemRecord> inventory, int gold)
        {
            if (definition == null) return 1;
            if (definition.ConditionType == QuestConditionType.Seeking)
                return (byte)(IsSeekingConditionSatisfied(definition, inventory, gold) ? 0 : 1);
            if (definition.ConditionType == QuestConditionType.HuntMonster)
            {
                // Hunt rows end in the required kill count. The wire trigger
                // stores the remaining count; the client renders progress as
                // target minus trigger (12 with trigger=1 became 11/12).
                var required = definition.ConditionRows.Where(row => row.Length > 0)
                    .Sum(row => (long)Math.Max(0, row[row.Length - 1]));
                return (byte)Math.Max(1, Math.Min(byte.MaxValue, required));
            }
            return 1;
        }

        public static int GetHuntRequiredCount(QuestDefinition definition)
        {
            if (definition == null || definition.ConditionType != QuestConditionType.HuntMonster) return 0;
            var required = definition.ConditionRows.Where(row => row.Length >= 4)
                .Sum(row => (long)Math.Max(0, row[row.Length - 1]));
            return (int)Math.Max(0, Math.Min(byte.MaxValue, required));
        }

        // [depend give item] is issued when the quest is accepted. It is not
        // part of [reward int data], which is granted only when the quest is
        // completed.
        public static IReadOnlyList<QuestItemDrop> GetAcceptItemGrants(QuestDefinition definition)
        {
            if (definition == null) return new QuestItemDrop[0];
            return (definition.DependGiveItemRows ?? new int[0][])
                .Where(row => row != null && row.Length >= 2 && row[0] > 0 &&
                    row[0] <= ushort.MaxValue && row[1] > 0)
                .Select(row => new QuestItemDrop
                {
                    ItemId = (ushort)row[0],
                    Count = (uint)row[1]
                }).ToArray();
        }

        // Arad [hunt monster] 行：dungeon, difficulty(-1=任意), monster, count。
        // 只有实际死亡的怪物种类符合该行时才推进，不能把投掷哥布林等
        // 同图怪物也算作普通哥布林。
        public static bool MatchesMonsterKill(QuestDefinition definition, int dungeonId,
            int difficulty, int monsterIndex)
        {
            if (definition == null || definition.ConditionType != QuestConditionType.HuntMonster) return false;
            return definition.ConditionRows.Any(row => row.Length >= 4 && row[0] == dungeonId &&
                (row[1] < 0 || row[1] == difficulty) && (row[2] < 0 || row[2] == monsterIndex));
        }

        public static bool MatchesDungeonClear(QuestDefinition definition, int dungeonId)
        {
            if (definition == null) return false;
            if (definition.ConditionType == QuestConditionType.ClearMap)
                return definition.ConditionRows.Length == 0 || definition.ConditionRows.Any(row => row.Length > 0 && row[0] == dungeonId);
            if (definition.ConditionType != QuestConditionType.ConditionUnderClear &&
                definition.ConditionType != QuestConditionType.HuntMonster) return false;
            return definition.ConditionRows.Any(row => row.Length > 0 && row[0] == dungeonId);
        }

        private static bool IsAvailable(QuestDefinition definition, CharacterRecord character,
            ISet<ushort> active, ISet<ushort> completed)
        {
            if (character == null || character.Level < definition.MinimumLevel || character.Level > definition.MaximumLevel)
                return false;
            if (!MatchesJob(definition.Job, character.Job)) return false;
            if (definition.GrowType >= 0 && definition.GrowType != character.PrimaryGrowType) return false;
            if (!definition.IsRepeatable && completed.Contains(definition.Id)) return false;
            if (definition.PrerequisiteQuestIds.Any(value => !completed.Contains(value))) return false;
            if (definition.CollisionQuestIds.Any(value => completed.Contains(value) || active.Contains(value))) return false;
            return true;
        }

        private static bool MatchesJob(string job, byte characterJob)
        {
            var normalized = NormalizeToken(job);
            if (string.IsNullOrEmpty(normalized) || normalized == "all") return true;
            switch (normalized)
            {
                case "swordman": return characterJob == 0;
                case "fighter": return characterJob == 1;
                case "gunner": return characterJob == 2;
                case "mage": return characterJob == 3;
                case "priest": return characterJob == 4;
                default: return true;
            }
        }

        private static int GradeOrder(QuestGrade grade)
        {
            if (grade == QuestGrade.Epic) return 0;
            if (grade == QuestGrade.CommonUnique) return 1;
            if (grade == QuestGrade.NormalRepeat) return 2;
            return 3;
        }

        internal static Dictionary<ushort, string> ParseListEntries(string text)
        {
            var result = new Dictionary<ushort, string>();
            foreach (Match match in ListEntryRegex.Matches(text ?? string.Empty))
            {
                ushort id;
                if (ushort.TryParse(match.Groups[1].Value, out id)) result[id] = match.Groups[2].Value;
            }
            return result;
        }

        internal static QuestDefinition ParseDefinition(ushort id, string path, string text)
        {
            var definition = new QuestDefinition { Id = id, ScriptPath = path };
            definition.Grade = ParseGrade(ReadScalar(text, "grade"));
            definition.NpcIndex = ReadFirstInteger(text, "npc index", -1);
            definition.CompleteNpcIndex = ReadFirstInteger(text, "complete npc index", definition.NpcIndex);
            if (definition.CompleteNpcIndex < 0) definition.CompleteNpcIndex = definition.NpcIndex;
            definition.Job = NormalizeToken(ReadScalar(text, "job"));
            definition.GrowType = ReadFirstInteger(text, "grow type", -1);
            var levels = ReadScalarIntegers(text, "level");
            if (levels.Length > 0) definition.MinimumLevel = levels[0];
            if (levels.Length > 1) definition.MaximumLevel = levels[1];
            definition.PrerequisiteQuestIds = ReadSectionIntegers(text, "pre required quest")
                .Where(value => value >= 0 && value <= ushort.MaxValue).Select(value => (ushort)value).Distinct().ToArray();
            definition.CollisionQuestIds = ReadSectionIntegers(text, "collision quest")
                .Where(value => value >= 0 && value <= ushort.MaxValue).Select(value => (ushort)value).Distinct().ToArray();
            definition.ConditionType = ParseConditionType(ReadScalar(text, "type"));
            definition.ConditionSubType = ReadFirstInteger(text, "sub type", -1);
            definition.ConditionRows = ReadIntegerRows(text, "int data");
            definition.RewardType = NormalizeToken(ReadScalar(text, "reward type"));
            definition.RewardRows = ReadIntegerRows(text, "reward int data");
            definition.RewardSelectionRows = ReadIntegerRows(text, "reward selection int data");
            definition.DependGiveItemRows = ReadIntegerRows(text, "depend give item");
            definition.ClearRewardItems = ReadIntegerRows(text, "clear reward item")
                .Where(row => row.Length >= 6 && row[0] >= -1 && row[2] > 0 &&
                    row[2] <= ushort.MaxValue && row[3] > 0)
                .Select(row => new QuestClearRewardItemDefinition
                {
                    DungeonId = row[0],
                    Difficulty = row[1],
                    ItemId = (ushort)row[2],
                    Count = (uint)row[3],
                    ChancePercent = Math.Max(0, Math.Min(100, row[4])),
                    MaximumCount = ParseMaximumCount(row[5])
                }).ToArray();
            definition.MonsterRewardItems = ReadIntegerRows(text, "monster reward item")
                .Where(row => row.Length >= 7 && row[0] >= 0 && row[1] >= -1 &&
                    row[3] > 0 && row[3] <= ushort.MaxValue && row[4] > 0)
                .Select(row => new QuestMonsterRewardItemDefinition
                {
                    MonsterIndex = row[0],
                    DungeonId = row[1],
                    Difficulty = row[2],
                    ItemId = (ushort)row[3],
                    Count = (uint)Math.Max(1, row[4]),
                    ChancePercent = Math.Max(0, Math.Min(100, row[5])),
                    MaximumCount = ParseMaximumCount(row[6])
                }).ToArray();
            return definition;
        }

        private static uint ParseMaximumCount(int value)
        {
            return value < 0 ? uint.MaxValue : (uint)value;
        }

        private static string Decode(byte[] bytes)
        {
            return Encoding.GetEncoding(949).GetString(bytes ?? new byte[0]).Replace("\0", string.Empty);
        }

        private static QuestGrade ParseGrade(string value)
        {
            switch (NormalizeToken(value))
            {
                case "epic": return QuestGrade.Epic;
                case "common unique": return QuestGrade.CommonUnique;
                case "normaly repeat": return QuestGrade.NormalRepeat;
                default: return QuestGrade.Unknown;
            }
        }

        private static QuestConditionType ParseConditionType(string value)
        {
            switch (NormalizeToken(value))
            {
                case "seeking": return QuestConditionType.Seeking;
                case "condition under clear": return QuestConditionType.ConditionUnderClear;
                case "hunt monster": return QuestConditionType.HuntMonster;
                case "clear map": return QuestConditionType.ClearMap;
                case "meet npc": return QuestConditionType.MeetNpc;
                default: return QuestConditionType.Unknown;
            }
        }

        private static string NormalizeToken(string value)
        {
            return (value ?? string.Empty).Replace("`", string.Empty).Replace("[", string.Empty)
                .Replace("]", string.Empty).Trim().ToLowerInvariant();
        }

        private static string ReadScalar(string text, string key)
        {
            var match = Regex.Match(text ?? string.Empty, @"(?im)^\s*\[" + Regex.Escape(key) + @"\]\s*([^\r\n/]*)");
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static int ReadFirstInteger(string text, string key, int fallback)
        {
            var values = ReadScalarIntegers(text, key);
            return values.Length == 0 ? fallback : values[0];
        }

        private static int[] ReadScalarIntegers(string text, string key)
        {
            return ParseIntegers(ReadScalar(text, key));
        }

        private static int[] ReadSectionIntegers(string text, string key)
        {
            return ReadIntegerRows(text, key).SelectMany(value => value).ToArray();
        }

        private static int[][] ReadIntegerRows(string text, string key)
        {
            var match = Regex.Match(text ?? string.Empty,
                @"(?ims)^\s*\[" + Regex.Escape(key) + @"\][^\r\n]*[\r\n]+(.*?)^\s*\[/" + Regex.Escape(key) + @"\]");
            if (!match.Success) return new int[0][];
            var rows = new List<int[]>();
            foreach (var sourceLine in match.Groups[1].Value.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                var commentIndex = sourceLine.IndexOf("//", StringComparison.Ordinal);
                var line = commentIndex < 0 ? sourceLine : sourceLine.Substring(0, commentIndex);
                var values = ParseIntegers(line);
                if (values.Length > 0) rows.Add(values);
            }
            return rows.ToArray();
        }

        private static int[] ParseIntegers(string value)
        {
            return IntegerRegex.Matches(value ?? string.Empty).Cast<Match>()
                .Select(match => { int parsed; return int.TryParse(match.Value, out parsed) ? (int?)parsed : null; })
                .Where(parsed => parsed.HasValue).Select(parsed => parsed.Value).ToArray();
        }
    }
}
