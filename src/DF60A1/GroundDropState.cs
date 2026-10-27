using System;
using System.Collections.Generic;
using System.Linq;

namespace DF60A1
{
    internal sealed class GroundDropRecord
    {
        public ushort RuntimeId;
        public ushort ItemId;
        public uint Count;
        public ushort X;
        public ushort Y;
        public byte ItemFlags;
        public ushort Durability;
        public byte State;
        public byte SealState;
    }

    internal sealed class PassiveObjectDropRecord
    {
        public byte ObjectIndex;
        public GroundDropRecord Drop;
    }

    internal sealed class GroundDropState
    {
        private readonly Dictionary<ushort, GroundDropRecord> _drops =
            new Dictionary<ushort, GroundDropRecord>();
        private ushort _nextRuntimeId = 0x4000;

        public int Count { get { return _drops.Count; } }

        public GroundDropRecord SpawnForMonster(ushort monsterRuntimeId, int itemIdHint = -1, uint countHint = 1,
            ushort durabilityHint = 0, ushort? posX = null, ushort? posY = null,
            byte stateHint = 0, byte sealStateHint = 0)
        {
            if (countHint == 0) throw new ArgumentOutOfRangeException("countHint");
            var runtimeId = AllocateRuntimeId();
            var drop = new GroundDropRecord
            {
                RuntimeId = runtimeId,
                // itemIdHint > 0：调用方已根据怪物掉落表摇骰选出的真实 itemId。
                // 否则回退到雷米的援助（itemId 8），保持 Arad 至少有一个可视化掉落。
                ItemId = itemIdHint >= 0 ? checked((ushort)itemIdHint) : (ushort)8,
                Count = countHint,
                // 掉落位置 = 怪物死亡位置（调用方传入 posX/posY）。
                // 旧逻辑按 runtimeId 硬编码散布，掉落物与怪物尸体不在一起。
                // 0 是合法的房间边界坐标，不能把它当成“未提供”。
                X = posX.HasValue ? posX.Value : checked((ushort)(360 + monsterRuntimeId % 6 * 48)),
                Y = posY.HasValue ? posY.Value : checked((ushort)(280 + monsterRuntimeId % 2 * 32)),
                ItemFlags = 0,
                Durability = durabilityHint,
                State = stateHint,
                SealState = sealStateHint
            };
            _drops.Add(runtimeId, drop);
            return drop;
        }

        public PassiveObjectDropRecord SpawnForPassiveObject(byte objectIndex, ushort itemId,
            uint count = 1, ushort durability = 0)
        {
            return new PassiveObjectDropRecord
            {
                ObjectIndex = objectIndex,
                Drop = SpawnForMonster(objectIndex, itemId, count, durability)
            };
        }

        public bool TryGet(ushort runtimeId, out GroundDropRecord drop)
        {
            return _drops.TryGetValue(runtimeId, out drop);
        }

        public bool Remove(ushort runtimeId)
        {
            return _drops.Remove(runtimeId);
        }

        public GroundDropRecord[] Snapshot()
        {
            return _drops.Values.OrderBy(value => value.RuntimeId).Select(Copy).ToArray();
        }

        public bool TryClaim(ushort runtimeId, Func<GroundDropRecord, ushort> addToInventory,
            out GroundDropRecord drop, out ushort slot)
        {
            drop = null;
            slot = ushort.MaxValue;
            if (addToInventory == null || !_drops.TryGetValue(runtimeId, out drop)) return false;
            slot = addToInventory(drop);
            if (slot == ushort.MaxValue) return false;
            if (_drops.Remove(runtimeId)) return true;
            slot = ushort.MaxValue;
            return false;
        }

        public void Clear()
        {
            _drops.Clear();
        }

        public GroundDropState Clone()
        {
            var clone = new GroundDropState { _nextRuntimeId = _nextRuntimeId };
            foreach (var pair in _drops) clone._drops[pair.Key] = Copy(pair.Value);
            return clone;
        }

        public void ReplaceWith(GroundDropState source)
        {
            if (source == null) throw new ArgumentNullException("source");
            // Restoring a room must not rewind the run-wide runtime-id allocator.
            var nextRuntimeId = _nextRuntimeId;
            _drops.Clear();
            foreach (var pair in source._drops) _drops[pair.Key] = Copy(pair.Value);
            if (source._nextRuntimeId > nextRuntimeId) nextRuntimeId = source._nextRuntimeId;
            _nextRuntimeId = nextRuntimeId;
        }

        private static GroundDropRecord Copy(GroundDropRecord source)
        {
            return new GroundDropRecord
            {
                RuntimeId = source.RuntimeId,
                ItemId = source.ItemId,
                Count = source.Count,
                X = source.X,
                Y = source.Y,
                ItemFlags = source.ItemFlags,
                Durability = source.Durability,
                State = source.State,
                SealState = source.SealState
            };
        }

        private ushort AllocateRuntimeId()
        {
            for (var attempt = 0; attempt < ushort.MaxValue; attempt++)
            {
                var candidate = _nextRuntimeId;
                _nextRuntimeId = unchecked((ushort)(_nextRuntimeId + 1));
                if (_nextRuntimeId == 0 || _nextRuntimeId == ushort.MaxValue) _nextRuntimeId = 0x4000;
                if (candidate != 0 && candidate != ushort.MaxValue && !_drops.ContainsKey(candidate))
                    return candidate;
            }
            throw new InvalidOperationException("No ground-item runtime IDs are available.");
        }
    }

    /// <summary>Preserves unclaimed drops per room so revisiting a map cannot delete or duplicate loot.</summary>
    internal sealed class DungeonRoomDropLedger
    {
        private sealed class RoomSnapshot
        {
            public GroundDropState Drops;
            public readonly Dictionary<byte, ushort> PassiveRuntimeIds =
                new Dictionary<byte, ushort>();
        }

        private readonly Dictionary<int, RoomSnapshot> _rooms =
            new Dictionary<int, RoomSnapshot>();

        public int Count { get { return _rooms.Count; } }

        public bool Contains(byte roomX, byte roomY)
        {
            return _rooms.ContainsKey(Key(roomX, roomY));
        }

        public void Clear()
        {
            _rooms.Clear();
        }

        public void Save(byte roomX, byte roomY, GroundDropState drops,
            IEnumerable<PassiveObjectDropRecord> passiveDrops = null)
        {
            if (drops == null) throw new ArgumentNullException("drops");
            var key = Key(roomX, roomY);
            RoomSnapshot snapshot;
            if (!_rooms.TryGetValue(key, out snapshot))
            {
                snapshot = new RoomSnapshot();
                _rooms[key] = snapshot;
            }
            snapshot.Drops = drops.Clone();
            if (passiveDrops == null) return;
            snapshot.PassiveRuntimeIds.Clear();
            foreach (var passive in passiveDrops)
            {
                if (passive == null || passive.Drop == null) continue;
                snapshot.PassiveRuntimeIds[passive.ObjectIndex] = passive.Drop.RuntimeId;
            }
        }

        public bool TryRestore(byte roomX, byte roomY, GroundDropState destination,
            out List<PassiveObjectDropRecord> passiveDrops)
        {
            passiveDrops = new List<PassiveObjectDropRecord>();
            if (destination == null) throw new ArgumentNullException("destination");
            RoomSnapshot snapshot;
            if (!_rooms.TryGetValue(Key(roomX, roomY), out snapshot) || snapshot.Drops == null) return false;
            destination.ReplaceWith(snapshot.Drops);
            foreach (var pair in snapshot.PassiveRuntimeIds.OrderBy(value => value.Key))
            {
                GroundDropRecord drop;
                if (!destination.TryGet(pair.Value, out drop)) continue;
                passiveDrops.Add(new PassiveObjectDropRecord { ObjectIndex = pair.Key, Drop = drop });
            }
            return true;
        }

        public bool TryRestore(byte roomX, byte roomY, GroundDropState destination)
        {
            List<PassiveObjectDropRecord> ignored;
            return TryRestore(roomX, roomY, destination, out ignored);
        }

        public int DetachPassiveObjectDrops(byte roomX, byte roomY)
        {
            RoomSnapshot snapshot;
            if (!_rooms.TryGetValue(Key(roomX, roomY), out snapshot)) return 0;
            var count = snapshot.PassiveRuntimeIds.Count;
            snapshot.PassiveRuntimeIds.Clear();
            return count;
        }

        public ushort[] GetPassiveRuntimeIds(byte roomX, byte roomY)
        {
            RoomSnapshot snapshot;
            if (!_rooms.TryGetValue(Key(roomX, roomY), out snapshot)) return new ushort[0];
            return snapshot.PassiveRuntimeIds.Values.Distinct().ToArray();
        }

        public bool Remove(ushort runtimeId)
        {
            var removed = false;
            foreach (var snapshot in _rooms.Values)
            {
                if (snapshot.Drops != null && snapshot.Drops.Remove(runtimeId)) removed = true;
                foreach (var objectIndex in snapshot.PassiveRuntimeIds
                    .Where(value => value.Value == runtimeId).Select(value => value.Key).ToArray())
                {
                    snapshot.PassiveRuntimeIds.Remove(objectIndex);
                    removed = true;
                }
            }
            return removed;
        }

        private static int Key(byte roomX, byte roomY)
        {
            return roomX | roomY << 8;
        }
    }
}
