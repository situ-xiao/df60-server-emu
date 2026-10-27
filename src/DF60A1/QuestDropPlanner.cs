using System;
using System.Collections.Generic;
using System.Linq;

namespace DF60A1
{
    internal sealed class QuestClearRewardItemDefinition
    {
        public int DungeonId;
        public int Difficulty;
        public ushort ItemId;
        public uint MaximumCount;
        public int ChancePercent;
        public uint Count;
    }

    internal sealed class QuestMonsterRewardItemDefinition
    {
        public int MonsterIndex;
        public int DungeonId;
        public int Difficulty;
        public ushort ItemId;
        public uint MaximumCount;
        public int ChancePercent;
        public uint Count;
    }

    internal sealed class QuestItemDrop
    {
        public ushort ItemId;
        public uint Count;
    }

    internal static class QuestDropPlanner
    {
        public static IReadOnlyList<QuestItemDrop> Plan(QuestDefinition quest, int dungeonId, int difficulty,
            IEnumerable<InventoryItemRecord> inventory, Func<int, bool> chanceRoll, bool guaranteed = false)
        {
            var result = new List<QuestItemDrop>();
            if (quest == null) return result;
            var owned = (inventory ?? Enumerable.Empty<InventoryItemRecord>())
                .GroupBy(item => item.ItemId)
                .ToDictionary(group => group.Key, group => group.Aggregate(0UL, (total, item) => total + item.Count));
            foreach (var reward in quest.ClearRewardItems ?? new QuestClearRewardItemDefinition[0])
            {
                if ((reward.DungeonId >= 0 && reward.DungeonId != dungeonId) ||
                    (reward.Difficulty >= 0 && reward.Difficulty != difficulty) ||
                    reward.MaximumCount == 0 || reward.Count == 0) continue;
                ulong current;
                if (!owned.TryGetValue(reward.ItemId, out current)) current = 0;
                if (current >= reward.MaximumCount) continue;
                if (!guaranteed && (reward.ChancePercent <= 0 || (reward.ChancePercent < 100 &&
                    (chanceRoll == null || !chanceRoll(reward.ChancePercent))))) continue;
                var count = (uint)Math.Min((ulong)reward.Count, (ulong)reward.MaximumCount - current);
                if (count == 0) continue;
                result.Add(new QuestItemDrop { ItemId = reward.ItemId, Count = count });
                owned[reward.ItemId] = current + count;
            }
            return result;
        }

        public static IReadOnlyList<QuestItemDrop> PlanMonster(QuestDefinition quest, int dungeonId,
            int difficulty, int monsterIndex, IEnumerable<InventoryItemRecord> inventory,
            Func<int, bool> chanceRoll, bool guaranteed = false)
        {
            var result = new List<QuestItemDrop>();
            if (quest == null) return result;
            var owned = (inventory ?? Enumerable.Empty<InventoryItemRecord>())
                .GroupBy(item => item.ItemId)
                .ToDictionary(group => group.Key,
                    group => group.Aggregate(0UL, (total, item) => total + item.Count));
            foreach (var reward in quest.MonsterRewardItems ?? new QuestMonsterRewardItemDefinition[0])
            {
                if ((reward.DungeonId >= 0 && reward.DungeonId != dungeonId) ||
                    (reward.MonsterIndex >= 0 && reward.MonsterIndex != monsterIndex) ||
                    (reward.Difficulty >= 0 && reward.Difficulty != difficulty) ||
                    reward.MaximumCount == 0 || reward.Count == 0) continue;
                ulong current;
                if (!owned.TryGetValue(reward.ItemId, out current)) current = 0;
                if (current >= reward.MaximumCount) continue;
                if (!guaranteed && (reward.ChancePercent <= 0 || (reward.ChancePercent < 100 &&
                    (chanceRoll == null || !chanceRoll(reward.ChancePercent))))) continue;
                var count = (uint)Math.Min((ulong)reward.Count,
                    (ulong)reward.MaximumCount - current);
                if (count == 0) continue;
                result.Add(new QuestItemDrop { ItemId = reward.ItemId, Count = count });
                owned[reward.ItemId] = current + count;
            }
            return result;
        }
    }
}
