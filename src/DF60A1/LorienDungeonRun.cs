using System;
using System.Collections.Generic;
using System.Linq;

namespace DF60A1
{
    // Per-connection state for a dungeon run. Generic since 2026-08-02:
    // room map ids and monster rows come from the PVF DungeonCatalog
    // (dungeon/*.dgn + map/*.map) instead of the old Lorien-only hard-coded
    // layout. When no catalog is supplied (offline self-test) the legacy
    // Lorien/LorienInside hard-coded layout is used as a fallback.
    // The client owns combat simulation; the server stays authoritative for
    // the spawned runtime monster IDs, room progression and boss-room clear.
    internal sealed class LorienDungeonRun
    {
        // Legacy Lorien room layout rows: (0,0),(1,0),(2,0),(0,1),(1,1),(2,1) boss.
        private static readonly byte[] LegacyRoomMonsterCounts = { 3, 5, 6, 4, 5, 7 };
        private static readonly ushort[] LegacyRoomMapIds = { 1, 5, 6, 8, 7, 13 };

        private readonly DungeonCatalog _catalog;
        private HashSet<ushort> _aliveMonsterIds = new HashSet<ushort>();
        private readonly List<ushort> _autoDefeatedMonsterIds = new List<ushort>();
        private Dictionary<ushort, DungeonCatalog.MonsterRow> _monsterRows = new Dictionary<ushort, DungeonCatalog.MonsterRow>();
        private readonly Dictionary<int, RoomState> _roomStates = new Dictionary<int, RoomState>();
        private DungeonCatalog.RunLayout _layout;
        private RoomState _currentRoom;
        private DungeonCatalog.SpawnPosition _spawnPosition;
        private byte _clientBossX;
        private byte _clientBossY;

        private sealed class RoomState
        {
            public readonly byte ClientX;
            public readonly byte ClientY;
            public readonly ushort MapId;
            public readonly bool IsBoss;
            public readonly DungeonCatalog.Room Room;
            public readonly HashSet<ushort> AliveMonsterIds = new HashSet<ushort>();
            public readonly Dictionary<ushort, DungeonCatalog.MonsterRow> MonsterRows =
                new Dictionary<ushort, DungeonCatalog.MonsterRow>();
            public bool IsCleared;

            public RoomState(byte clientX, byte clientY, DungeonCatalog.Room room, bool isBoss)
            {
                ClientX = clientX;
                ClientY = clientY;
                MapId = room.MapId;
                IsBoss = isBoss;
                Room = room;
                var roomAlreadyMarksBoss = room.Monsters.Any(monster => monster.IsBoss);
                for (ushort runtimeId = 0; runtimeId < room.Monsters.Length; runtimeId++)
                {
                    var monster = room.Monsters[runtimeId];
                    if (isBoss && !roomAlreadyMarksBoss && runtimeId == room.Monsters.Length - 1)
                        monster = new DungeonCatalog.MonsterRow(monster.MonsterIndex, true, monster.X, monster.Y,
                            monster.IsChampion, monster.IsSuperChampion, monster.Lv, monster.AutoLv, monster.Level);
                    AliveMonsterIds.Add(runtimeId);
                    MonsterRows[runtimeId] = monster;
                }
            }

            public void MarkCleared()
            {
                AliveMonsterIds.Clear();
                IsCleared = true;
            }
        }

        public LorienDungeonRun(DungeonCatalog catalog)
        {
            _catalog = catalog;
        }

        public bool IsActive { get; private set; }
        public ushort DungeonId { get; private set; }
        public byte Difficulty { get; private set; }
        public byte RoomX { get; private set; }
        public byte RoomY { get; private set; }
        public int RoomSlot
        {
            get
            {
                var width = _layout != null ? _layout.Width : (byte)3;
                return RoomX + RoomY * width;
            }
        }
        public bool IsBossRoom { get; private set; }
        public ushort MapId { get; private set; }
        public byte MazeIndex { get { return _layout != null ? _layout.MazeIndex : (byte)0; } }
        public byte StartX { get { return _layout != null ? _layout.StartX : (byte)0; } }
        public byte StartY { get { return _layout != null ? _layout.StartY : (byte)0; } }
        public byte BossX { get { return _layout != null ? _clientBossX : (byte)(DungeonId == 2 ? 0 : 2); } }
        public byte BossY { get { return _layout != null ? _clientBossY : (byte)(DungeonId == 2 ? 1 : 1); } }
        public int AliveMonsterCount { get { return _aliveMonsterIds.Count; } }
        public bool LastRoomWasRevisit { get; private set; }
        public bool HasSpawnPosition { get { return _spawnPosition.IsDefined; } }
        public short SpawnX { get { return _spawnPosition.X; } }
        public short SpawnY { get { return _spawnPosition.Y; } }
        public byte SpawnDirection { get { return _spawnPosition.Direction; } }

        public bool Start(ushort dungeonId, byte difficulty)
        {
            // The client launcher resolves one PVF layout/start-room record
            // before the run begins.  Several older dungeons, including
            // Scasa's Lair, declare multiple valid [start map] positions.
            // Choosing one randomly here leaves the client and server in
            // different rooms because the selection protocol carries no seed.
            // Use the first PVF entry, matching the launcher's lookup.
            return Start(dungeonId, difficulty, null);
        }

        internal bool Start(ushort dungeonId, byte difficulty, Random random)
        {
            ResetRoomState();
            DungeonId = dungeonId;
            Difficulty = difficulty;
            if (_catalog == null) return EnterRoomLegacy(dungeonId == 2 ? (byte)0 : (byte)0, dungeonId == 2 ? (byte)2 : (byte)0);
            DungeonCatalog.Definition definition;
            if (!_catalog.TryGetDefinition(dungeonId, out definition) ||
                !_catalog.TrySelectRunLayout(dungeonId, random, out _layout)) return false;
            DungeonCatalog.Room startRoom;
            if (!_catalog.TryCreateRoom(_layout, _layout.StartX, _layout.StartY, out startRoom)) return false;
            startRoom = ApplyChampionProjection(startRoom, _layout.StartX, _layout.StartY);

            _clientBossX = _layout.BossX;
            _clientBossY = _layout.BossY;
            var startIsBoss = _layout.BossX == _layout.StartX && _layout.BossY == _layout.StartY;
            if (startIsBoss)
            {
                _clientBossX = _layout.StartX;
                _clientBossY = _layout.StartY;
            }
            // START_MAP coordinates and map id must identify the exact same
            // PVF-derived client maze cell. Room persistence is keyed by these coordinates.
            var state = new RoomState(_layout.StartX, _layout.StartY, startRoom, startIsBoss);
            _roomStates[RoomKey(_layout.StartX, _layout.StartY)] = state;
            return LoadRoom(state, false, startRoom.DefaultSpawn);
        }

        public bool EnterRoom(byte roomX, byte roomY)
        {
            if (_catalog == null) return EnterRoomLegacy(roomX, roomY);
            RoomState existing;
            if (_roomStates.TryGetValue(RoomKey(roomX, roomY), out existing))
                return LoadRoom(existing, true, existing.Room.DefaultSpawn);

            DungeonCatalog.Room room;
            if (_layout == null || !_catalog.TryCreateRoom(_layout, roomX, roomY, out room)) return false;
            room = ApplyChampionProjection(room, roomX, roomY);
            var state = new RoomState(roomX, roomY, room,
                IsClientBossRoom(roomX, roomY));
            _roomStates[RoomKey(roomX, roomY)] = state;
            return LoadRoom(state, false, room.DefaultSpawn);
        }

        public bool TryMoveRoom(byte roomX, byte roomY)
        {
            if (_catalog == null) return EnterRoomLegacy(roomX, roomY);
            if (!CanMoveRoom(roomX, roomY)) return false;

            RoomState target;
            var revisit = _roomStates.TryGetValue(RoomKey(roomX, roomY), out target);
            if (!revisit)
            {
                DungeonCatalog.Room room;
                if (!_catalog.TryCreateRoom(_layout, roomX, roomY, out room)) return false;
                room = ApplyChampionProjection(room, roomX, roomY);
                target = new RoomState(roomX, roomY, room,
                    IsClientBossRoom(roomX, roomY));
                _roomStates[RoomKey(roomX, roomY)] = target;
            }

            // Retain this compatibility clear for the legacy path-transition
            // flow. Current ARAD clients report each ordinary monster death
            // with command 131 before requesting the next room.
            _currentRoom.MarkCleared();
            var entrySpawn = target.Room.GetEntrySpawn(RoomX, RoomY, roomX, roomY);
            return LoadRoom(target, revisit, entrySpawn);
        }

        // The path gate is valid only between connected, non-boss rooms.
        // MarkCurrentRoomCleared remains available for legacy transition tests.
        public bool CanMoveRoom(byte roomX, byte roomY)
        {
            if (_catalog == null) return IsActive && !IsBossRoom &&
                (RoomX != roomX || RoomY != roomY);
            return IsActive && _layout != null && _currentRoom != null && !IsBossRoom &&
                (RoomX != roomX || RoomY != roomY) &&
                _catalog.AreRoomsConnected(_layout, RoomX, RoomY, roomX, roomY);
        }

        public bool MarkCurrentRoomCleared()
        {
            if (!IsActive || _currentRoom == null) return false;
            _currentRoom.MarkCleared();
            _aliveMonsterIds = _currentRoom.AliveMonsterIds;
            return true;
        }

        private bool LoadRoom(RoomState state, bool revisit, DungeonCatalog.SpawnPosition spawnPosition)
        {
            _currentRoom = state;
            if (state.IsCleared) state.AliveMonsterIds.Clear();
            _aliveMonsterIds = state.AliveMonsterIds;
            _monsterRows = state.MonsterRows;
            RoomX = state.ClientX;
            RoomY = state.ClientY;
            MapId = state.MapId;
            IsBossRoom = state.IsBoss;
            LastRoomWasRevisit = revisit;
            _spawnPosition = spawnPosition;
            IsActive = true;
            return true;
        }

        private DungeonCatalog.Room ApplyChampionProjection(DungeonCatalog.Room room,
            byte roomX, byte roomY)
        {
            if (room == null || _catalog == null || _layout == null) return room;
            var target = _catalog.GetChampionTargetCount(_layout, Difficulty, roomX, roomY);
            unchecked
            {
                var seed = (uint)DungeonId * 397u ^ (uint)room.MapId * 31u ^
                    (uint)Difficulty * 131u ^ (uint)roomX * 17u ^ roomY;
                var monsters = DungeonCatalog.PromoteChampions(room.Monsters, target, seed);
                return new DungeonCatalog.Room(room.MapId, monsters, room.Topology,
                    room.HasScriptBossActor, room.DefaultSpawn, room.PathGatePositions);
            }
        }

        // Legacy hard-coded Lorien / LorienInside layout for the offline
        // self-test only (catalog == null).
        private bool EnterRoomLegacy(byte roomX, byte roomY)
        {
            ushort[] monsters;
            if (DungeonId == 2)
            {
                if (roomX == 0 && roomY == 2) monsters = new ushort[] { 1, 1, 2, 1, 2 };
                else if (roomX == 1 && roomY == 2) monsters = new ushort[] { 1, 1, 1, 2, 2 };
                else if (roomX == 1 && roomY == 1) monsters = new ushort[] { 1, 1, 2, 1, 1, 2 };
                else if (roomX == 2 && roomY == 1) monsters = new ushort[] { 1, 2, 2, 1 };
                else if (roomX == 1 && roomY == 0) monsters = new ushort[] { 2, 2, 2, 2 };
                else if (roomX == 0 && roomY == 0) monsters = new ushort[] { 2, 1, 1, 2, 2 };
                else if (roomX == 0 && roomY == 1) monsters = new ushort[] { 2, 1, 2, 11, 2, 2 };
                else return false;
            }
            else
            {
                var index = roomY == 0 ? roomX : roomX + 3;
                if (roomY > 1 || roomX > 2) return false;
                var count = LegacyRoomMonsterCounts[index];
                var bossMonsterRow = index == 5 ? 4 : -1;
                monsters = new ushort[count];
                for (var row = 0; row < count; row++) monsters[row] = (ushort)(row == bossMonsterRow ? 2 : 1);
            }
            RoomX = roomX;
            RoomY = roomY;
            MapId = 0;
            _spawnPosition = DungeonCatalog.SpawnPosition.Undefined;
            IsBossRoom = DungeonId == 2 ? roomX == 0 && roomY == 1 : roomX == 2 && roomY == 1;
            _aliveMonsterIds.Clear();
            _monsterRows.Clear();
            for (ushort id = 0; id < monsters.Length; id++)
            {
                _aliveMonsterIds.Add(id);
                _monsterRows[id] = new DungeonCatalog.MonsterRow(monsters[id], IsBossRoom && (DungeonId == 2 ? id == 3 : id == 4));
            }
            IsActive = true;
            return true;
        }

        public void Stop()
        {
            ResetRoomState();
        }

        private void ResetRoomState()
        {
            _aliveMonsterIds = new HashSet<ushort>();
            _monsterRows = new Dictionary<ushort, DungeonCatalog.MonsterRow>();
            _autoDefeatedMonsterIds.Clear();
            _roomStates.Clear();
            _currentRoom = null;
            _layout = null;
            IsActive = false;
            IsBossRoom = false;
            LastRoomWasRevisit = false;
            MapId = 0;
            RoomX = 0;
            RoomY = 0;
            _clientBossX = 0;
            _clientBossY = 0;
        }

        private static int RoomKey(byte roomX, byte roomY)
        {
            return roomX | (roomY << 8);
        }

        private bool IsClientBossRoom(byte roomX, byte roomY)
        {
            return _layout != null && _clientBossX == roomX && _clientBossY == roomY;
        }

        public MonsterDeathResult ReportMonsterDeath(ushort monsterId)
        {
            if (!IsActive) return MonsterDeathResult.NotInDungeon;
            DungeonCatalog.MonsterRow row;
            if (!_monsterRows.TryGetValue(monsterId, out row) || !_aliveMonsterIds.Remove(monsterId))
                return MonsterDeathResult.UnknownOrAlreadyDead;
            if (IsBossRoom && row.IsBoss)
            {
                _autoDefeatedMonsterIds.Clear();
                _autoDefeatedMonsterIds.AddRange(_aliveMonsterIds);
                _autoDefeatedMonsterIds.Sort();
                _aliveMonsterIds.Clear();
                if (_currentRoom != null) _currentRoom.MarkCleared();
                return MonsterDeathResult.RoomCleared;
            }
            if (_aliveMonsterIds.Count == 0)
            {
                if (_currentRoom != null) _currentRoom.IsCleared = true;
                // Boss room cleared ends the dungeon; normal rooms simply
                // open the path gate and the client requests the next room
                // with command 48 (MoveRoom).
                return IsBossRoom ? MonsterDeathResult.RoomCleared : MonsterDeathResult.Accepted;
            }
            return MonsterDeathResult.Accepted;
        }

        // 当前房间怪物（runtime id -> monster.lst id / boss 标记），供 START_MAP 构造。
        public IReadOnlyList<DungeonCatalog.MonsterRow> MonsterRows
        {
            get
            {
                return _monsterRows.Where(pair => _aliveMonsterIds.Contains(pair.Key))
                    .OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
            }
        }

        public IReadOnlyList<KeyValuePair<ushort, DungeonCatalog.MonsterRow>> SpawnMonsterRows
        {
            get
            {
                return _monsterRows.Where(pair => _aliveMonsterIds.Contains(pair.Key))
                    .OrderBy(pair => pair.Key).ToList();
            }
        }

        public bool TryGetMonster(ushort runtimeId, out ushort monsterIndex, out int level, out bool isBoss)
        {
            bool isChampion;
            bool isSuperChampion;
            return TryGetMonster(runtimeId, out monsterIndex, out level, out isBoss,
                out isChampion, out isSuperChampion);
        }

        public bool TryGetMonster(ushort runtimeId, out ushort monsterIndex, out int level,
            out bool isBoss, out bool isChampion, out bool isSuperChampion)
        {
            DungeonCatalog.MonsterRow row;
            level = CurrentLevel();
            isBoss = IsBossRoom && _monsterRows.TryGetValue(runtimeId, out row) && row.IsBoss;
            if (_monsterRows.TryGetValue(runtimeId, out row))
            {
                monsterIndex = row.MonsterIndex;
                level = row.ResolveLevel(CurrentLevel());
                isChampion = row.IsChampion;
                isSuperChampion = row.IsSuperChampion;
                return true;
            }
            monsterIndex = 0;
            isChampion = false;
            isSuperChampion = false;
            return false;
        }

        public bool TryGetMonsterPosition(ushort runtimeId, out ushort x, out ushort y)
        {
            DungeonCatalog.MonsterRow row;
            if (_monsterRows.TryGetValue(runtimeId, out row) && (row.X != 0 || row.Y != 0))
            {
                x = row.X;
                y = row.Y;
                return true;
            }
            x = 0;
            y = 0;
            return false;
        }

        // START_MAP 与任务判定使用的怪物等级：dungeon 基础等级 + 难度修正，
        // 与洛兰（basis 1）/洛兰深处（basis 2）旧硬编码行为一致。
        public int CurrentLevel()
        {
            DungeonCatalog.Definition definition;
            var basis = _catalog != null && _catalog.TryGetDefinition(DungeonId, out definition)
                ? (int)definition.BasisLevel : (DungeonId == 2 ? 2 : 1);
            return System.Math.Max(1, basis + System.Math.Min(3, (int)Difficulty));
        }

        public ushort[] TakeAutoDefeatedMonsterIds()
        {
            var result = _autoDefeatedMonsterIds.ToArray();
            _autoDefeatedMonsterIds.Clear();
            return result;
        }
    }
}
