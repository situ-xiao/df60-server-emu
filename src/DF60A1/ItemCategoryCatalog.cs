using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DF60A1
{
    internal enum InventoryItemCategory
    {
        Equipment,
        Consumable,
        Material,
        Quest
    }

    /// <summary>Maps PVF item definitions to the four visible Arad backpack tabs.</summary>
    internal sealed class ItemCategoryCatalog
    {
        // ref server 背包整理（SortItem 命令 20）使用的页签槽位区间。
        private const ushort QuickFirst = 3;
        private const ushort QuickLast = 8;
        private const ushort EquipmentFirst = 9;
        private const ushort EquipmentLast = 40;
        private const ushort ConsumableFirst = 41;
        private const ushort ConsumableLast = 72;
        private const ushort MaterialFirst = 73;
        private const ushort MaterialLast = 104;
        private const ushort QuestFirst = 105;
        private const ushort QuestLast = 136;

        private static readonly Regex StackableListEntryRegex = new Regex(
            @"(?m)^\s*(\d+)\s+`([^`]+\.stk)`", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StackableTypeRegex = new Regex(
            @"(?im)^\s*\[stackable\s+type\]\s*`?\[?([^\]`\r\n]+)", RegexOptions.Compiled);
        private static readonly HashSet<string> ConsumableStackableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "avatar jewel", "booster", "cera booster", "cera package", "creature",
            "etc", "feed", "legacy", "recipe", "set", "stackable legacy", "throw",
            "unlimited etc", "unlimited waste", "upgradable legacy", "waste"
        };

        private readonly PvfArchiveReader _archive;
        private readonly HashSet<ushort> _equipmentIds = new HashSet<ushort>();
        private readonly Dictionary<ushort, string> _stackablePaths = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, InventoryItemCategory> _cache = new Dictionary<ushort, InventoryItemCategory>();
        private readonly object _gate = new object();

        public ItemCategoryCatalog(PvfArchiveReader archive)
        {
            _archive = archive;
            if (archive == null) return;
            uint ignored;
            foreach (var id in EquipmentCatalog.ParseListEntries(Decode(
                archive.ReadAllBytes("equipment/equipment.lst", out ignored))).Keys)
                _equipmentIds.Add(id);
            foreach (var pair in ParseStackableListEntries(Decode(
                archive.ReadAllBytes("stackable/stackable.lst", out ignored))))
                _stackablePaths[pair.Key] = pair.Value;

            // Classify every PVF item at startup.  A lazy "unknown => material"
            // fallback allowed whole stackable families (recipes and legacy
            // items in particular) to enter the wrong client tab unnoticed.
            foreach (var pair in _stackablePaths)
            {
                try
                {
                    _cache[pair.Key] = ParseStackableType(Decode(archive.ReadAllBytes(pair.Value, out ignored)));
                }
                catch (FileNotFoundException)
                {
                    // Some client PVFs retain deleted event scripts in
                    // stackable.lst. They are still stackable item IDs and
                    // must remain classifiable if one is restored from an old
                    // character database or referenced by a reward table.
                    _cache[pair.Key] = InferMissingStackableCategory(pair.Value);
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException("无法分类 PVF 堆叠物品 " + pair.Key + " (" + pair.Value + ")", exception);
                }
            }
        }

        public int Count { get { return _equipmentIds.Count + _stackablePaths.Count; } }
        public int EquipmentCount { get { return _equipmentIds.Count; } }
        public int StackableCount { get { return _stackablePaths.Count; } }
        public int ConsumableCount { get { return _cache.Values.Count(value => value == InventoryItemCategory.Consumable); } }
        public int MaterialCount { get { return _cache.Values.Count(value => value == InventoryItemCategory.Material); } }
        public int QuestCount { get { return _cache.Values.Count(value => value == InventoryItemCategory.Quest); } }
        public int UnclassifiedStackableCount
        {
            get { return _stackablePaths.Keys.Count(itemId => !_cache.ContainsKey(itemId)); }
        }

        public InventoryItemCategory GetCategory(ushort itemId)
        {
            InventoryItemCategory category;
            if (TryGetCategory(itemId, out category)) return category;
            throw new KeyNotFoundException("物品 ID 不存在于 PVF equipment/stackable 列表: " + itemId);
        }

        public ushort FindFreeSlot(IDictionary<ushort, InventoryItemRecord> inventory, ushort itemId)
        {
            // 按 ref server 的页签槽位规则分配：装备 9..40、消耗品 41..72、
            // 材料 73..104、任务 105..136。消耗品不进快捷栏 3..8，
            // 保证物品显示在背包页里。
            InventoryItemCategory category;
            if (!TryGetCategory(itemId, out category)) category = InventoryItemCategory.Equipment;
            GetTabRange(category, out var first, out var last);
            for (var slotValue = first; slotValue <= last; slotValue++)
            {
                var slot = (ushort)slotValue;
                if (inventory == null || !inventory.ContainsKey(slot)) return slot;
            }
            return ushort.MaxValue;
        }

        /// <summary>把不在自己页签槽位区间内的物品移回正确区间（ref server 行为）。</summary>
        public int RetabInventory(IDictionary<ushort, InventoryItemRecord> inventory)
        {
            if (inventory == null || inventory.Count == 0) return 0;
            var misplaced = inventory.Values
                .Where(value => value != null && value.ItemId != 0 && value.ItemId != 1)
                .Where(value =>
                {
                    InventoryItemCategory category;
                    if (!TryGetCategory(value.ItemId, out category)) return false;
                    // Slots 3..8 are the persisted item quick bar. The client
                    // permits equipment, consumables and materials there, so
                    // login normalization must preserve every item category.
                    return !IsSlotInTab(value.Slot, category);
                })
                .OrderBy(value => value.Slot)
                .ToList();
            if (misplaced.Count == 0) return 0;
            foreach (var item in misplaced) inventory.Remove(item.Slot);
            var moved = 0;
            foreach (var item in misplaced)
            {
                InventoryItemCategory category;
                TryGetCategory(item.ItemId, out category);
                if (category != InventoryItemCategory.Equipment)
                {
                    var existing = inventory.Values.FirstOrDefault(value =>
                        value != null && value.ItemId == item.ItemId && value.Durability == 0 &&
                        IsSlotInTab(value.Slot, category));
                    if (existing != null)
                    {
                        existing.Count += item.Count;
                        moved++;
                        continue;
                    }
                }
                var slot = FindFreeSlot(inventory, item.ItemId);
                if (slot == ushort.MaxValue)
                {
                    inventory[item.Slot] = item;
                    continue;
                }
                item.Slot = slot;
                inventory[slot] = item;
                moved++;
            }
            return moved;
        }

        public bool TryGetCategory(ushort itemId, out InventoryItemCategory category)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(itemId, out category)) return true;
            }
            if (_equipmentIds.Contains(itemId))
            {
                category = InventoryItemCategory.Equipment;
                return true;
            }
            category = InventoryItemCategory.Equipment;
            return false;
        }

        private static void GetTabRange(InventoryItemCategory category, out ushort first, out ushort last)
        {
            switch (category)
            {
                case InventoryItemCategory.Consumable: first = ConsumableFirst; last = ConsumableLast; break;
                case InventoryItemCategory.Material: first = MaterialFirst; last = MaterialLast; break;
                case InventoryItemCategory.Quest: first = QuestFirst; last = QuestLast; break;
                default: first = EquipmentFirst; last = EquipmentLast; break;
            }
        }

        internal static bool IsSlotInTab(ushort slot, InventoryItemCategory category)
        {
            if (slot >= QuickFirst && slot <= QuickLast)
                return true;
            GetTabRange(category, out var first, out var last);
            return slot >= first && slot <= last;
        }

        /// <summary>按 ref server 规则整理主背包：物品按分类移入各自页签槽位区间。</summary>
        public bool TrySortMainInventory(IDictionary<ushort, InventoryItemRecord> inventory, out ushort[] changedSlots)
        {
            changedSlots = new ushort[0];
            if (inventory == null) return false;
            var list = inventory
                .Where(pair => pair.Key >= 9 && pair.Key <= 136 && pair.Value != null &&
                    pair.Value.ItemId != 0 && pair.Value.Count != 0)
                .OrderBy(pair => pair.Key)
                .ToList();
            var before = list.ToDictionary(pair => pair.Key, pair => CloneItem(pair.Value));
            var source = list.Select(pair => new SortableInventoryItem
            {
                OriginalSlot = pair.Key,
                Item = pair.Value,
                Category = GetCategoryOrEquipment(pair.Value.ItemId)
            }).ToList();
            foreach (var category in Enum.GetValues(typeof(InventoryItemCategory)))
            {
                GetTabRange((InventoryItemCategory)category, out var first, out var last);
                if (source.Count(value => value.Category == (InventoryItemCategory)category) > last - first + 1)
                    return false;
            }
            var sorted = new Dictionary<ushort, InventoryItemRecord>();
            foreach (var category in Enum.GetValues(typeof(InventoryItemCategory)))
            {
                GetTabRange((InventoryItemCategory)category, out var first, out var _);
                var next = first;
                foreach (var item in source
                    .Where(value => value.Category == (InventoryItemCategory)category)
                    .OrderBy(value => value.Item.ItemId)
                    .ThenBy(value => value.Item.SealState)
                    .ThenBy(value => value.Item.State)
                    .ThenBy(value => value.Item.Durability)
                    .ThenBy(value => value.OriginalSlot))
                {
                    sorted[(ushort)next++] = item.Item;
                }
            }
            foreach (var pair in list) inventory.Remove(pair.Key);
            foreach (var pair in sorted)
            {
                pair.Value.Slot = pair.Key;
                inventory[pair.Key] = pair.Value;
            }
            var allSlots = new SortedSet<ushort>(before.Keys);
            foreach (var key in sorted.Keys) allSlots.Add(key);
            changedSlots = allSlots.Where(slot =>
            {
                InventoryItemRecord beforeItem;
                var hadBefore = before.TryGetValue(slot, out beforeItem);
                InventoryItemRecord afterItem;
                var hasAfter = inventory.TryGetValue(slot, out afterItem);
                return hadBefore != hasAfter || (hadBefore && !SameWireItem(beforeItem, afterItem));
            }).ToArray();
            return true;
        }

        private InventoryItemCategory GetCategoryOrEquipment(ushort itemId)
        {
            InventoryItemCategory category;
            return TryGetCategory(itemId, out category) ? category : InventoryItemCategory.Equipment;
        }

        private static InventoryItemRecord CloneItem(InventoryItemRecord item)
        {
            return new InventoryItemRecord
            {
                Slot = item.Slot,
                ItemId = item.ItemId,
                Count = item.Count,
                State = item.State,
                Durability = item.Durability,
                SealState = item.SealState
            };
        }

        private static bool SameWireItem(InventoryItemRecord left, InventoryItemRecord right)
        {
            return left != null && right != null &&
                left.ItemId == right.ItemId && left.Count == right.Count &&
                left.State == right.State && left.Durability == right.Durability &&
                left.SealState == right.SealState;
        }

        private sealed class SortableInventoryItem
        {
            public ushort OriginalSlot;
            public InventoryItemRecord Item;
            public InventoryItemCategory Category;
        }

        internal static Dictionary<ushort, string> ParseStackableListEntries(string text)
        {
            var result = new Dictionary<ushort, string>();
            foreach (Match match in StackableListEntryRegex.Matches(text ?? string.Empty))
            {
                ushort id;
                if (ushort.TryParse(match.Groups[1].Value, out id))
                    result[id] = "stackable/" + match.Groups[2].Value.Replace('\\', '/').TrimStart('/');
            }
            return result;
        }

        internal static InventoryItemCategory ParseStackableType(string text)
        {
            var type = ParseStackableTypeName(text);
            if (string.Equals(type, "quest", StringComparison.OrdinalIgnoreCase)) return InventoryItemCategory.Quest;
            if (string.Equals(type, "quest receive", StringComparison.OrdinalIgnoreCase)) return InventoryItemCategory.Quest;
            if (string.Equals(type, "material", StringComparison.OrdinalIgnoreCase)) return InventoryItemCategory.Material;
            if (ConsumableStackableTypes.Contains(type)) return InventoryItemCategory.Consumable;
            throw new FormatException("不支持的 [stackable type]: " + (string.IsNullOrEmpty(type) ? "<missing>" : type));
        }

        private static InventoryItemCategory InferMissingStackableCategory(string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            if (normalized.Contains("/quest/") || normalized.Contains("quest_"))
                return InventoryItemCategory.Quest;
            if (normalized.Contains("/material/") || normalized.Contains("/cube"))
                return InventoryItemCategory.Material;
            return InventoryItemCategory.Consumable;
        }

        internal IDictionary<string, int> AuditStackableTypes()
        {
            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _stackablePaths.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                var type = "<read-error>";
                try
                {
                    uint ignored;
                    type = ParseStackableTypeName(Decode(_archive.ReadAllBytes(path, out ignored)));
                    if (string.IsNullOrEmpty(type)) type = "<missing>";
                }
                catch
                {
                    type = "<read-error>";
                }
                int count;
                counts.TryGetValue(type, out count);
                counts[type] = count + 1;
            }
            return counts;
        }

        internal IDictionary<string, IList<string>> AuditStackableTypeExamples(int maximumPerType)
        {
            var examples = new SortedDictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _stackablePaths.OrderBy(value => value.Key))
            {
                var type = "<read-error>";
                try
                {
                    uint ignored;
                    type = ParseStackableTypeName(Decode(_archive.ReadAllBytes(pair.Value, out ignored)));
                    if (string.IsNullOrEmpty(type)) type = "<missing>";
                }
                catch
                {
                    type = "<read-error>";
                }
                IList<string> values;
                if (!examples.TryGetValue(type, out values))
                {
                    values = new List<string>();
                    examples[type] = values;
                }
                if (values.Count < Math.Max(1, maximumPerType))
                    values.Add(pair.Key + "=" + pair.Value);
            }
            return examples;
        }

        internal static string ParseStackableTypeName(string text)
        {
            var match = StackableTypeRegex.Match(text ?? string.Empty);
            return match.Success ? match.Groups[1].Value.Trim().ToLowerInvariant() : string.Empty;
        }

        private static string Decode(byte[] bytes)
        {
            return Encoding.GetEncoding(949).GetString(bytes ?? new byte[0]).Replace("\0", string.Empty);
        }
    }
}
