using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DF60A1
{
    // 通用副本目录：从 Arad PVF 解析 dungeon/dungeon.lst、map/map.lst、
    // dungeon/*.dgn（迷宫布局）与 map/*.map（每房间地图与怪物行），
    // 为任意 dungeon 解析起始房间 mapId 与怪物列表。
    // Route selection is derived only from the local ARAD Script.pvf.
    internal sealed class DungeonCatalog
    {
        internal struct RoomPosition
        {
            public readonly byte X;
            public readonly byte Y;
            public RoomPosition(byte x, byte y) { X = x; Y = y; }
        }

        // World-space position used by the client after a START_MAP load.
        // Map coordinates use signed 16-bit values on the ARAD wire.
        internal struct SpawnPosition
        {
            public readonly short X;
            public readonly short Y;
            public readonly byte Direction;
            public readonly bool IsDefined;

            public SpawnPosition(short x, short y, byte direction)
            {
                X = x;
                Y = y;
                Direction = direction;
                IsDefined = true;
            }

            private SpawnPosition(bool _) { X = 0; Y = 0; Direction = 0; IsDefined = false; }

            public static SpawnPosition Undefined { get { return new SpawnPosition(false); } }
        }

        internal sealed class MazeDefinition
        {
            public readonly byte Index;
            public readonly byte Width;
            public readonly byte Height;
            public readonly RoomPosition[] StartRoomPositions;
            public readonly RoomPosition[] BossRoomPositions;
            public readonly string RoomTopology;
            internal readonly Dictionary<RoomPoint, ushort[]> MapSpecifications;

            public MazeDefinition(byte index, byte width, byte height,
                RoomPosition[] startRoomPositions, RoomPosition[] bossRoomPositions,
                string roomTopology, Dictionary<RoomPoint, ushort[]> mapSpecifications)
            {
                Index = index; Width = width; Height = height;
                StartRoomPositions = startRoomPositions ?? new RoomPosition[0];
                BossRoomPositions = bossRoomPositions ?? new RoomPosition[0];
                RoomTopology = roomTopology ?? string.Empty;
                MapSpecifications = mapSpecifications ?? new Dictionary<RoomPoint, ushort[]>();
            }
        }

        internal sealed class RunLayout
        {
            public readonly Definition Dungeon;
            public readonly MazeDefinition Maze;
            public readonly byte MazeIndex;
            public readonly byte Width;
            public readonly byte Height;
            public readonly byte StartX;
            public readonly byte StartY;
            public readonly byte BossX;
            public readonly byte BossY;

            public RunLayout(Definition dungeon, MazeDefinition maze, RoomPosition start, RoomPosition boss)
            {
                Dungeon = dungeon; Maze = maze; MazeIndex = maze.Index;
                Width = maze.Width; Height = maze.Height;
                StartX = start.X; StartY = start.Y; BossX = boss.X; BossY = boss.Y;
            }
        }

        internal sealed class Definition
        {
            public readonly ushort DungeonId;
            public readonly byte MinimumLevel;
            public readonly byte BasisLevel;
            public readonly byte Width;
            public readonly byte Height;
            public readonly byte StartX;
            public readonly byte StartY;
            public readonly byte BossX;
            public readonly byte BossY;
            public readonly string RoomTopology;
            public readonly MazeDefinition[] MazeLayouts;
            public readonly int[] ChampionCounts;
            public readonly PassiveObjectDropRule[] PassiveObjectDropRules;
            public Definition(ushort dungeonId, byte minimumLevel, byte basisLevel, byte width, byte height,
                byte startX, byte startY, byte bossX, byte bossY, string roomTopology,
                MazeDefinition[] mazeLayouts = null, int[] championCounts = null,
                PassiveObjectDropRule[] passiveObjectDropRules = null)
            {
                DungeonId = dungeonId; MinimumLevel = minimumLevel; BasisLevel = basisLevel;
                Width = width; Height = height; StartX = startX; StartY = startY;
                BossX = bossX; BossY = bossY; RoomTopology = roomTopology;
                MazeLayouts = mazeLayouts ?? new MazeDefinition[0];
                ChampionCounts = NormalizeChampionCounts(championCounts);
                PassiveObjectDropRules = passiveObjectDropRules ?? new PassiveObjectDropRule[0];
            }

            private static int[] NormalizeChampionCounts(int[] values)
            {
                var result = new int[4];
                if (values == null) return result;
                for (var index = 0; index < result.Length && index < values.Length; index++)
                    result[index] = Math.Max(0, values[index]);
                return result;
            }
        }

        // Entries from [special passive object item] bind a rolled reward to
        // the matching breakable-object row in START_MAP.
        internal sealed class PassiveObjectDropRule
        {
            public readonly byte ObjectIndex;
            public readonly int LevelOverride;
            public readonly ushort ItemId;
            public readonly int DropRate;

            public PassiveObjectDropRule(byte objectIndex, int levelOverride,
                ushort itemId, int dropRate)
            {
                ObjectIndex = objectIndex;
                LevelOverride = levelOverride;
                ItemId = itemId;
                DropRate = dropRate;
            }
        }

        internal sealed class MonsterRow
        {
            public readonly ushort MonsterIndex; // monster.lst id
            public readonly bool IsBoss;
            public readonly bool IsChampion;
            public readonly bool IsSuperChampion;
            public readonly ushort X;
            public readonly ushort Y;
            public readonly int Lv;
            public readonly int AutoLv;
            public readonly byte Level;

            public MonsterRow(ushort monsterIndex, bool isBoss, ushort x = 0, ushort y = 0,
                bool isChampion = false, bool isSuperChampion = false, int lv = 1,
                int autoLv = 0, byte level = 0)
            {
                MonsterIndex = monsterIndex;
                IsBoss = isBoss;
                IsChampion = isChampion;
                IsSuperChampion = isSuperChampion;
                X = x;
                Y = y;
                Lv = lv;
                AutoLv = autoLv;
                Level = level;
            }

            public byte ResolveLevel(int dungeonBasisLevel)
            {
                if (Level != 0) return Level;
                var basis = Math.Max(1, dungeonBasisLevel);
                var resolved = Lv != 0 ? basis + AutoLv : AutoLv;
                if (resolved <= 0) resolved = basis;
                return (byte)Math.Max(1, Math.Min(byte.MaxValue, resolved));
            }

            public MonsterRow AsChampion()
            {
                if (IsBoss || IsChampion || IsSuperChampion) return this;
                return new MonsterRow(MonsterIndex, false, X, Y, true, false,
                    Lv, AutoLv, Level);
            }
        }

        internal sealed class Room
        {
            public readonly ushort MapId;
            public readonly MonsterRow[] Monsters;
            public readonly char Topology;
            public readonly bool HasScriptBossActor;
            public readonly SpawnPosition DefaultSpawn;
            private readonly SpawnPosition[] _pathGatePositions;
            public SpawnPosition[] PathGatePositions { get { return _pathGatePositions; } }

            public Room(ushort mapId, MonsterRow[] monsters, char topology, bool hasScriptBossActor,
                SpawnPosition defaultSpawn, SpawnPosition[] pathGatePositions)
            {
                MapId = mapId; Monsters = monsters; Topology = topology; HasScriptBossActor = hasScriptBossActor;
                DefaultSpawn = defaultSpawn;
                _pathGatePositions = pathGatePositions ?? new SpawnPosition[0];
            }

            public SpawnPosition GetEntrySpawn(int fromX, int fromY, int toX, int toY)
            {
                // [pathgate pos] records gates in left, right, top, bottom order.
                // Entering a room from the left therefore uses its left gate.
                var deltaX = toX - fromX;
                var deltaY = toY - fromY;
                var gateIndex = deltaX > 0 ? 0 : deltaX < 0 ? 1 : deltaY > 0 ? 2 : deltaY < 0 ? 3 : -1;
                if (gateIndex >= 0 && gateIndex < _pathGatePositions.Length &&
                    _pathGatePositions[gateIndex].IsDefined)
                    return _pathGatePositions[gateIndex];
                return DefaultSpawn;
            }
        }

        private sealed class MapDef
        {
            public readonly ushort MapId;
            public readonly ushort[] DungeonIds;
            public readonly MapKind Kind;
            public readonly int? CoordinateX;
            public readonly int? CoordinateY;
            public readonly char[] TopologySymbols;
            public readonly MonsterRow[] Monsters;
            public readonly SpawnPosition DefaultSpawn;
            public readonly SpawnPosition[] PathGatePositions;
            public MapDef(ushort mapId, ushort[] dungeonIds, MapKind kind, int? coordinateX, int? coordinateY,
                char[] topologySymbols, MonsterRow[] monsters, SpawnPosition defaultSpawn,
                SpawnPosition[] pathGatePositions)
            {
                MapId = mapId; DungeonIds = dungeonIds; Kind = kind; CoordinateX = coordinateX;
                CoordinateY = coordinateY; TopologySymbols = topologySymbols; Monsters = monsters;
                DefaultSpawn = defaultSpawn;
                PathGatePositions = pathGatePositions ?? new SpawnPosition[0];
            }
        }

        private enum MapKind : byte { Normal, Start, Boss }

        internal struct RoomPoint : IEquatable<RoomPoint>
        {
            public readonly int X, Y;
            public RoomPoint(int x, int y) { X = x; Y = y; }
            public bool Equals(RoomPoint other) { return X == other.X && Y == other.Y; }
            public override bool Equals(object obj) { return obj is RoomPoint && Equals((RoomPoint)obj); }
            public override int GetHashCode() { return X * 397 ^ Y; }
        }

        private static readonly Encoding ScriptEncoding = CreateScriptEncoding();
        private static readonly Regex ListEntryPattern = new Regex("(?m)^\\s*(\\d+)\\s+`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex IntegerPattern = new Regex("-?\\d+", RegexOptions.Compiled);
        private static readonly Regex CoordinatePattern = new Regex("\\((-?\\d+)\\s*[,\\.]\\s*(-?\\d+)\\)", RegexOptions.Compiled);

        private readonly PvfArchiveReader _archive;
        private readonly Dictionary<ushort, Definition> _definitions = new Dictionary<ushort, Definition>();
        private readonly Dictionary<ushort, MapDef> _maps = new Dictionary<ushort, MapDef>();
        private readonly Dictionary<ushort, Dictionary<RoomPoint, ushort[]>> _mapSpecifications = new Dictionary<ushort, Dictionary<RoomPoint, ushort[]>>();

        public DungeonCatalog(PvfArchiveReader archive)
        {
            _archive = archive;
            if (archive == null) return;
            try
            {
                Load();
                LogInfo("Arad 副本目录已加载: {0} 个副本, {1} 张地图", _definitions.Count, _maps.Count);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("ARAD PVF 副本目录加载失败", exception);
            }
        }

        public int DungeonCount { get { return _definitions.Count; } }
        public int MapCount { get { return _maps.Count; } }
        internal IEnumerable<KeyValuePair<ushort, Definition>> Definitions
        {
            get { return _definitions.OrderBy(pair => pair.Key); }
        }

        public bool TryGetDefinition(ushort dungeonId, out Definition definition)
        {
            return _definitions.TryGetValue(dungeonId, out definition);
        }

        public bool TrySelectRunLayout(ushort dungeonId, Random random, out RunLayout layout)
        {
            layout = null;
            Definition definition;
            if (!_definitions.TryGetValue(dungeonId, out definition) ||
                definition.MazeLayouts == null || definition.MazeLayouts.Length == 0)
                return false;

            var maze = definition.MazeLayouts[random != null ? random.Next(definition.MazeLayouts.Length) : 0];
            if (maze.StartRoomPositions.Length == 0 || maze.BossRoomPositions.Length == 0) return false;
            // Every [start map] coordinate recorded by this ARAD PVF is valid.
            // Do not suppress (0,0) or inject a preferred coordinate.
            var start = maze.StartRoomPositions[random != null ? random.Next(maze.StartRoomPositions.Length) : 0];
            var bossCandidates = maze.BossRoomPositions
                .Where(value => value.X != start.X || value.Y != start.Y).ToArray();
            if (bossCandidates.Length == 0) bossCandidates = maze.BossRoomPositions;
            var boss = bossCandidates[random != null ? random.Next(bossCandidates.Length) : 0];
            layout = new RunLayout(definition, maze, start, boss);
            return true;
        }

        public bool IsBossRoom(RunLayout layout, byte roomX, byte roomY)
        {
            return layout != null && layout.BossX == roomX && layout.BossY == roomY;
        }

        public int GetChampionTargetCount(RunLayout layout, byte difficulty,
            byte roomX, byte roomY)
        {
            var definition = layout == null ? null : layout.Dungeon;
            if (definition == null || definition.ChampionCounts == null ||
                definition.ChampionCounts.Length == 0) return 0;
            var difficultyIndex = Math.Max(0, Math.Min(definition.ChampionCounts.Length - 1,
                (int)difficulty));
            var dungeonTarget = Math.Max(0, definition.ChampionCounts[difficultyIndex]);
            if (dungeonTarget == 0) return 0;

            var rooms = new List<Tuple<RoomPosition, int>>();
            for (var y = 0; y < layout.Height; y++)
            for (var x = 0; x < layout.Width; x++)
            {
                Room room;
                if (!TryCreateRoom(layout, (byte)x, (byte)y, out room)) continue;
                var capacity = (room.Monsters ?? new MonsterRow[0]).Count(IsChampionCandidate);
                if (capacity > 0)
                    rooms.Add(Tuple.Create(new RoomPosition((byte)x, (byte)y), capacity));
            }
            if (rooms.Count == 0) return 0;
            var ordinal = rooms.FindIndex(value => value.Item1.X == roomX && value.Item1.Y == roomY);
            if (ordinal < 0) return 0;
            var remaining = Math.Min(dungeonTarget, rooms.Sum(value => value.Item2));
            var allocation = new int[rooms.Count];
            while (remaining > 0)
            {
                var progressed = false;
                for (var index = 0; index < rooms.Count && remaining > 0; index++)
                {
                    if (allocation[index] >= rooms[index].Item2) continue;
                    allocation[index]++;
                    remaining--;
                    progressed = true;
                }
                if (!progressed) break;
            }
            return allocation[ordinal];
        }

        internal static MonsterRow[] PromoteChampions(IEnumerable<MonsterRow> source,
            int count, uint seed)
        {
            var rows = (source ?? Enumerable.Empty<MonsterRow>()).ToArray();
            if (count <= 0 || rows.Length == 0) return rows;
            var candidates = Enumerable.Range(0, rows.Length)
                .Where(index => IsChampionCandidate(rows[index]))
                .OrderBy(index => ChampionOrder(seed, index))
                .ThenBy(index => index)
                .Take(count)
                .ToArray();
            foreach (var index in candidates) rows[index] = rows[index].AsChampion();
            return rows;
        }

        private static bool IsChampionCandidate(MonsterRow row)
        {
            return row != null && !row.IsBoss && !row.IsChampion && !row.IsSuperChampion;
        }

        private static uint ChampionOrder(uint seed, int index)
        {
            unchecked
            {
                var value = seed ^ ((uint)index + 1u) * 0x9E3779B9u;
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                return value ^ value >> 16;
            }
        }

        public bool AreRoomsConnected(RunLayout layout, byte fromX, byte fromY, byte toX, byte toY)
        {
            if (layout == null || fromX >= layout.Width || fromY >= layout.Height ||
                toX >= layout.Width || toY >= layout.Height) return false;
            var deltaX = (int)toX - fromX;
            var deltaY = (int)toY - fromY;
            if (Math.Abs(deltaX) + Math.Abs(deltaY) != 1) return false;

            char fromTopology;
            char toTopology;
            if (!TryGetRoomTopology(layout, fromX, fromY, out fromTopology) ||
                !TryGetRoomTopology(layout, toX, toY, out toTopology)) return false;
            var fromMask = TopologyMask(fromTopology);
            var toMask = TopologyMask(toTopology);
            if (deltaX == 1) return (fromMask & 1) != 0 && (toMask & 4) != 0;
            if (deltaX == -1) return (fromMask & 4) != 0 && (toMask & 1) != 0;
            if (deltaY == 1) return (fromMask & 8) != 0 && (toMask & 2) != 0;
            return (fromMask & 2) != 0 && (toMask & 8) != 0;
        }

        private static int TopologyMask(char topology)
        {
            var normalized = char.ToUpperInvariant(topology);
            return normalized >= 'A' && normalized <= 'P' ? normalized - 'A' : 0;
        }

        // Diagnostic hook for the standalone PVF audit command. It audits the
        // catalog's default (maze 0) layout only. Live runs must retain and use
        // their selected RunLayout for every room so they cannot fall back to
        // a different maze after DUNGEON_INFO has been sent.
        internal bool TryResolveDefaultLayoutMapId(ushort dungeonId, byte roomX, byte roomY, out ushort mapId)
        {
            mapId = 0;
            Definition definition;
            if (!_definitions.TryGetValue(dungeonId, out definition) || roomX >= definition.Width || roomY >= definition.Height)
                return false;
            char topology;
            var hasTopology = TryGetRoomTopology(definition, roomX, roomY, out topology);
            mapId = ResolveMapId(dungeonId, definition, roomX, roomY, topology);
            return mapId != 0;
        }

        public bool TryCreateRoom(RunLayout layout, byte roomX, byte roomY, out Room room)
        {
            room = null;
            if (layout == null || roomX >= layout.Width || roomY >= layout.Height) return false;
            var dungeonId = layout.Dungeon.DungeonId;
            var isStart = layout.StartX == roomX && layout.StartY == roomY;
            var isBossRoom = layout.BossX == roomX && layout.BossY == roomY;
            var hasSpecification = layout.Maze.MapSpecifications.ContainsKey(new RoomPoint(roomX, roomY));
            char topology;
            var hasTopology = TryGetRoomTopology(layout, roomX, roomY, out topology);
            if (hasTopology && !IsOpenRoomTopology(topology) && !isStart && !isBossRoom && !hasSpecification) return false;
            var mapId = ResolveMapId(layout, roomX, roomY, topology);
            MapDef mapDef;
            if (mapId == 0 || !_maps.TryGetValue(mapId, out mapDef)) return false;
            var monsterRows = mapDef.Monsters.Take(255).ToArray();
            var mapMarksBoss = monsterRows.Any(monster => monster.IsBoss);
            var monsters = new MonsterRow[monsterRows.Length];
            for (var index = 0; index < monsterRows.Length; index++)
            {
                var isBoss = monsterRows[index].IsBoss ||
                    (isBossRoom && !mapMarksBoss && index == monsterRows.Length - 1);
                monsters[index] = new MonsterRow(monsterRows[index].MonsterIndex, isBoss,
                    monsterRows[index].X, monsterRows[index].Y,
                    monsterRows[index].IsChampion, monsterRows[index].IsSuperChampion,
                    monsterRows[index].Lv, monsterRows[index].AutoLv, monsterRows[index].Level);
            }
            room = new Room(mapId, monsters, hasTopology ? topology : '\0', mapMarksBoss,
                mapDef.DefaultSpawn, mapDef.PathGatePositions);
            return true;
        }

        private void Load()
        {
            LoadMaps("map/map.lst");
            uint ignored;
            var dungeonList = Decode(_archive.ReadAllBytes("dungeon/dungeon.lst", out ignored));
            foreach (Match match in ListEntryPattern.Matches(dungeonList))
            {
                ushort dungeonId;
                if (!ushort.TryParse(match.Groups[1].Value, out dungeonId)) continue;
                var relativePath = match.Groups[2].Value.Replace('\\', '/').TrimStart('/');
                Definition definition;
                Dictionary<RoomPoint, ushort[]> specifications;
                byte[] bytes;
                try { bytes = _archive.ReadAllBytes("dungeon/" + relativePath, out ignored); }
                catch { continue; }
                if (!TryParseDungeon(dungeonId, Decode(bytes), out definition, out specifications)) continue;
                _definitions[dungeonId] = definition;
                _mapSpecifications[dungeonId] = specifications;
            }

        }

        private void LoadMaps(string mapListPath)
        {
            uint ignored;
            var mapList = Decode(_archive.ReadAllBytes(mapListPath, out ignored));
            foreach (Match match in ListEntryPattern.Matches(mapList))
            {
                ushort mapId;
                if (!ushort.TryParse(match.Groups[1].Value, out mapId)) continue;
                var relativePath = match.Groups[2].Value.Replace('\\', '/').TrimStart('/');
                byte[] bytes;
                try { bytes = _archive.ReadAllBytes("map/" + relativePath, out ignored); }
                catch { continue; }
                var text = Decode(bytes);
                var dungeonIds = ParseAllBlockIntegers(text, "dungeon")
                    .Where(value => value >= 0 && value <= ushort.MaxValue)
                    .Select(value => (ushort)value).Distinct().ToArray();
                var monsters = ParseMonsterRows(text);
                var fileName = relativePath.Split('/').Last();
                var coordinateMatch = CoordinatePattern.Match(fileName);
                int? coordinateX = coordinateMatch.Success ? (int?)int.Parse(coordinateMatch.Groups[1].Value) : null;
                int? coordinateY = coordinateMatch.Success ? (int?)int.Parse(coordinateMatch.Groups[2].Value) : null;
                var mapKind = ClassifyMap(text);
                var pathGatePositions = ParsePathGatePositions(text);
                var defaultSpawn = ParseDungeonStartSpawn(text);
                if (!defaultSpawn.IsDefined && mapKind == MapKind.Start)
                {
                    // A few older PVFs omit the rectangle on a start map but
                    // still provide path gates. Keep those maps away from the
                    // client's (0,0) fallback as well.
                    foreach (var pathGate in pathGatePositions)
                        if (pathGate.IsDefined) { defaultSpawn = pathGate; break; }
                }
                _maps[mapId] = new MapDef(mapId, dungeonIds, mapKind, coordinateX, coordinateY,
                    ParseTopologySymbols(text), monsters, defaultSpawn, pathGatePositions);
            }
        }

        private static bool TryParseDungeon(ushort dungeonId, string text, out Definition definition, out Dictionary<RoomPoint, ushort[]> specifications)
        {
            definition = null;
            specifications = new Dictionary<RoomPoint, ushort[]>();
            var layouts = ParseMazeDefinitions(text);
            if (layouts.Length == 0) return false;
            var first = layouts[0];
            var start = first.StartRoomPositions[0];
            var boss = first.BossRoomPositions[0];
            var minimumLevel = ParseInlineIntegers(text, "minimum required level").FirstOrDefault();
            if (minimumLevel < 1) minimumLevel = 1;
            var basisLevel = ParseInlineIntegers(text, "basis level").FirstOrDefault();
            if (basisLevel < Math.Max(1, minimumLevel)) basisLevel = Math.Max(1, minimumLevel);
            var championCounts = ParseInlineIntegers(text, "champion");
            var passiveObjectDropRules = ParsePassiveObjectDropRules(text);
            definition = new Definition(dungeonId, (byte)Math.Min(255, minimumLevel), (byte)Math.Min(255, basisLevel),
                first.Width, first.Height, start.X, start.Y, boss.X, boss.Y, first.RoomTopology,
                layouts, championCounts, passiveObjectDropRules);
            specifications = first.MapSpecifications;
            return true;
        }

        internal static MazeDefinition[] ParseMazeDefinitions(string text)
        {
            var source = text ?? string.Empty;
            var markers = Regex.Matches(source, "(?im)^\\s*\\[maze info\\]\\s*$").Cast<Match>().ToArray();
            var result = new List<MazeDefinition>();
            for (var index = 0; index < markers.Length && index <= byte.MaxValue; index++)
            {
                var startOffset = markers[index].Index;
                var endOffset = index + 1 < markers.Length ? markers[index + 1].Index : source.Length;
                var mazeText = source.Substring(startOffset, endOffset - startOffset);
                var size = ParseInlineIntegers(mazeText, "size");
                if (size.Length < 2 || size[0] <= 0 || size[1] <= 0 || size[0] > 255 || size[1] > 255) continue;

                var width = size[0];
                var height = size[1];
                var starts = ParseRoomPositions(ParseFirstBlockIntegers(mazeText, "start map"))
                    .Where(value => value.X < width && value.Y < height).ToArray();
                var bosses = ParseRoomPositions(ParseFirstBlockIntegers(mazeText, "boss map"))
                    .Where(value => value.X < width && value.Y < height).ToArray();
                // Missing/invalid endpoints mean the PVF does not define a
                // generic traversable dungeon (the tutorial is handled by its
                // dedicated protocol path). Never fabricate route coordinates.
                if (starts.Length == 0 || bosses.Length == 0) continue;

                var topology = CollapseGreedTopology(ParseTopologySymbols(mazeText), width, height);
                if (topology.Length != width * height) continue;
                result.Add(new MazeDefinition((byte)index, (byte)width, (byte)height, starts, bosses,
                    new string(topology), ParseMapSpecifications(mazeText, width, height)));
            }
            return result.ToArray();
        }

        private static RoomPosition[] ParseRoomPositions(int[] values)
        {
            var result = new List<RoomPosition>();
            for (var index = 0; values != null && index + 1 < values.Length; index += 2)
            {
                if (values[index] < 0 || values[index] > 255 || values[index + 1] < 0 || values[index + 1] > 255) continue;
                var point = new RoomPosition((byte)values[index], (byte)values[index + 1]);
                if (!result.Any(value => value.X == point.X && value.Y == point.Y)) result.Add(point);
            }
            return result.ToArray();
        }

        private static char[] CollapseGreedTopology(char[] raw, int width, int height)
        {
            if (raw == null || width <= 0 || height <= 0) return new char[0];
            var count = width * height;
            if (raw.Length == count) return raw;
            if (raw.Length != count * 2) return new char[0];
            var result = new char[count];
            for (var index = 0; index < count; index++) result[index] = raw[index * 2];
            return result;
        }

        private static Dictionary<RoomPoint, ushort[]> ParseMapSpecifications(string maze, int width, int height)
        {
            var result = new Dictionary<RoomPoint, ushort[]>();
            foreach (var block in ParseAllBlocks(maze, "map specification"))
            {
                var values = ParseIntegers(block);
                if (values.Length < 3 || values[0] < 0 || values[0] >= width ||
                    values[1] < 0 || values[1] >= height) continue;
                var mapIds = values.Skip(2).Where(value => value > 0 && value <= ushort.MaxValue)
                    .Select(value => (ushort)value).Distinct().ToArray();
                if (mapIds.Length == 0) continue;
                result[new RoomPoint(values[0], values[1])] = mapIds;
            }
            return result;
        }

        private ushort ResolveMapId(ushort dungeonId, Definition dungeon, byte roomX, byte roomY, char topology)
        {
            Dictionary<RoomPoint, ushort[]> specifications;
            if (!_mapSpecifications.TryGetValue(dungeonId, out specifications))
                specifications = new Dictionary<RoomPoint, ushort[]>();
            return ResolveMapId(dungeon, specifications, dungeon.StartX, dungeon.StartY,
                dungeon.BossX, dungeon.BossY, roomX, roomY, topology);
        }

        private ushort ResolveMapId(RunLayout layout, byte roomX, byte roomY, char topology)
        {
            return ResolveMapId(layout.Dungeon, layout.Maze.MapSpecifications, layout.StartX, layout.StartY,
                layout.BossX, layout.BossY, roomX, roomY, topology);
        }

        private ushort ResolveMapId(Definition dungeon, Dictionary<RoomPoint, ushort[]> specifications,
            byte startX, byte startY, byte bossX, byte bossY, byte roomX, byte roomY, char topology)
        {
            var dungeonId = dungeon.DungeonId;
            var point = new RoomPoint(roomX, roomY);
            var isStart = startX == roomX && startY == roomY;
            var isBoss = bossX == roomX && bossY == roomY;
            var role = isStart ? MapKind.Start : (isBoss ? MapKind.Boss : MapKind.Normal);
            var candidateMaps = _maps.Values
                .Where(map => map.DungeonIds.Contains(dungeonId) && map.Kind == role &&
                    MapSupportsTopology(map, topology))
                .OrderBy(map => map.MapId)
                .ToArray();

            // A [map specification] is authoritative only when its map also
            // matches the selected room role and [greed] topology. Boss/start
            // candidate coordinates often list their normal-room replacement;
            // selecting that coordinate as the actual endpoint must use the
            // PVF's [type] or [dungeon start area] map pool instead.
            ushort specifiedMap;
            if (TryResolveSpecifiedMap(specifications, point, topology, role, out specifiedMap))
                return specifiedMap;

            var coordinateMatches = candidateMaps
                .Where(map => map.CoordinateX == roomX && map.CoordinateY == roomY)
                .ToArray();
            if (coordinateMatches.Length != 0)
                return PickStable(coordinateMatches, dungeon, roomX, roomY).MapId;
            if (candidateMaps.Length != 0)
                return PickStable(candidateMaps, dungeon, roomX, roomY).MapId;

            if (role != MapKind.Normal) return 0;

            // An unselected [start map] or [boss map] candidate becomes an
            // ordinary traversable cell. Some ARAD PVF layouts provide no
            // separate normal map for that topology, so use any map explicitly
            // associated with this dungeon and [greed] symbol. The candidate
            // still comes entirely from PVF; no topology-mismatched fallback is
            // permitted.
            ushort[] specifiedCandidates;
            if (specifications != null && specifications.TryGetValue(point, out specifiedCandidates))
            {
                foreach (var candidate in specifiedCandidates)
                {
                    MapDef specified;
                    if (_maps.TryGetValue(candidate, out specified) &&
                        specified.DungeonIds.Contains(dungeonId) && MapSupportsTopology(specified, topology))
                        return candidate;
                }
            }
            var topologyCandidates = _maps.Values
                .Where(map => map.DungeonIds.Contains(dungeonId) && MapSupportsTopology(map, topology))
                .OrderBy(map => map.MapId)
                .ToArray();
            return topologyCandidates.Length == 0
                ? (ushort)0
                : PickStable(topologyCandidates, dungeon, roomX, roomY).MapId;
        }

        private bool TryResolveSpecifiedMap(Dictionary<RoomPoint, ushort[]> specifications, RoomPoint point,
            char topology, MapKind requiredKind, out ushort mapId)
        {
            mapId = 0;
            ushort[] candidates;
            if (specifications == null || !specifications.TryGetValue(point, out candidates)) return false;
            foreach (var candidate in candidates)
            {
                MapDef mapDef;
                if (_maps.TryGetValue(candidate, out mapDef) && mapDef.Kind == requiredKind &&
                    MapSupportsTopology(mapDef, topology)) { mapId = candidate; break; }
            }
            return mapId != 0;
        }

        private static MapDef PickStable(IReadOnlyList<MapDef> candidates, Definition dungeon, byte roomX, byte roomY)
        {
            var index = (dungeon.DungeonId + roomX + roomY * dungeon.Width) % candidates.Count;
            return candidates[index];
        }

        private static MonsterRow[] ParseMonsterRows(string text)
        {
            var result = new List<MonsterRow>();
            foreach (var block in ParseAllBlocks(text, "monster"))
            {
                foreach (var rawLine in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim();
                    MonsterRow monster;
                    if (TryParseMonsterLine(line, out monster)) result.Add(monster);
                }
            }
            return result.Take(255).ToArray();
        }

        // Arad .map 的 [monster] 行：monsterId ... x y ...。
        // 例如：1 1 0 535 180 0 1 1 `[fixed]` `[normal]`。
        internal static bool TryParseMonsterLine(string line, out MonsterRow monster)
        {
            monster = null;
            var values = ParseIntegers(line ?? string.Empty);
            if (values.Length < 5 || values[0] <= 0 || values[0] > ushort.MaxValue ||
                values[3] < 0 || values[3] > ushort.MaxValue ||
                values[4] < 0 || values[4] > ushort.MaxValue) return false;
            var isSuperChampion = line.IndexOf("[super champion]", StringComparison.OrdinalIgnoreCase) >= 0;
            var isChampion = !isSuperChampion &&
                line.IndexOf("[champion]", StringComparison.OrdinalIgnoreCase) >= 0;
            monster = new MonsterRow((ushort)values[0],
                line.IndexOf("[boss]", StringComparison.OrdinalIgnoreCase) >= 0,
                (ushort)values[3], (ushort)values[4], isChampion, isSuperChampion,
                values[1], values[2]);
            return true;
        }

        private static IEnumerable<int> ParseAllBlockIntegers(string text, string tag)
        {
            return ParseAllBlocks(text, tag).SelectMany(ParseIntegers);
        }

        private static int[] ParseFirstBlockIntegers(string text, string tag)
        {
            var blocks = ParseAllBlocks(text, tag).ToList();
            return blocks.Count == 0 ? new int[0] : ParseIntegers(blocks[0]);
        }

        private static IEnumerable<string> ParseAllBlocks(string text, string tag)
        {
            var pattern = "\\[" + Regex.Escape(tag) + "\\]\\s*(.*?)\\s*\\[/" + Regex.Escape(tag) + "\\]";
            foreach (Match match in Regex.Matches(text ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
                yield return match.Groups[1].Value;
        }

        internal static PassiveObjectDropRule[] ParsePassiveObjectDropRules(string text)
        {
            var result = new List<PassiveObjectDropRule>();
            foreach (var block in ParseAllBlocks(text, "special passive object item"))
            {
                var values = ParseIntegers(block);
                var position = 0;
                while (position + 2 < values.Length)
                {
                    var objectIndex = values[position++];
                    var levelOverride = values[position++];
                    var itemCount = values[position++];
                    if (objectIndex < 0 || objectIndex > byte.MaxValue || itemCount < 0)
                        break;
                    for (var item = 0; item < itemCount && position + 1 < values.Length; item++)
                    {
                        var itemId = values[position++];
                        var dropRate = values[position++];
                        if (itemId <= 0 || itemId > ushort.MaxValue || dropRate <= 0) continue;
                        result.Add(new PassiveObjectDropRule((byte)objectIndex, levelOverride,
                            (ushort)itemId, Math.Min(10000, dropRate)));
                    }
                }
            }
            return result.Take(byte.MaxValue).ToArray();
        }

        private static char[] ParseTopologySymbols(string text)
        {
            var source = text ?? string.Empty;
            var start = source.IndexOf("[greed]", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return new char[0];
            start += "[greed]".Length;
            var end = source.Length;
            for (var offset = start; offset < source.Length; offset++)
            {
                if ((offset == 0 || source[offset - 1] == '\n') && source[offset] == '[')
                {
                    end = offset;
                    break;
                }
            }
            return source.Substring(start, Math.Max(0, end - start))
                .Where(character => !char.IsWhiteSpace(character) && character != '`' && character != ',')
                .ToArray();
        }

        private static bool TryGetRoomTopology(Definition dungeon, byte roomX, byte roomY, out char topology)
        {
            topology = '\0';
            if (roomX >= dungeon.Width || roomY >= dungeon.Height || dungeon.RoomTopology.Length != dungeon.Width * dungeon.Height) return false;
            topology = dungeon.RoomTopology[roomY * dungeon.Width + roomX];
            return true;
        }

        private static bool TryGetRoomTopology(RunLayout layout, byte roomX, byte roomY, out char topology)
        {
            topology = '\0';
            if (layout == null || roomX >= layout.Width || roomY >= layout.Height ||
                layout.Maze.RoomTopology.Length != layout.Width * layout.Height) return false;
            topology = layout.Maze.RoomTopology[roomY * layout.Width + roomX];
            return true;
        }

        private static bool TryGetRoomTopology(MazeDefinition maze, byte roomX, byte roomY, out char topology)
        {
            topology = '\0';
            if (maze == null || roomX >= maze.Width || roomY >= maze.Height ||
                maze.RoomTopology.Length != maze.Width * maze.Height) return false;
            topology = maze.RoomTopology[roomY * maze.Width + roomX];
            return true;
        }

        private static bool IsOpenRoomTopology(char topology)
        {
            switch (topology)
            {
                case '\0': case '.': case '0': case 'A': case 'a': case 'X': case 'x': case '┼':
                    return false;
                default:
                    return true;
            }
        }

        private static bool MapSupportsTopology(MapDef map, char topology)
        {
            if (topology != '\0') return map.TopologySymbols.Contains(topology);
            return true;
        }

        private static int[] ParseInlineIntegers(string text, string tag)
        {
            var match = Regex.Match(text ?? string.Empty, "\\[" + Regex.Escape(tag) + "\\]\\s*([^\\r\\n]*)", RegexOptions.IgnoreCase);
            return match.Success ? ParseIntegers(match.Groups[1].Value) : new int[0];
        }

        private static int[] ParseTagValues(string text, string tag)
        {
            // Most map tags are blocks without a closing tag. Stop at the next
            // bracketed tag so the numeric values from the following section do
            // not become part of this one.
            var pattern = "^\\s*\\[" + Regex.Escape(tag) + "\\]\\s*(.*?)(?=^\\s*\\[|\\z)";
            var match = Regex.Match(text ?? string.Empty, pattern,
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
            return match.Success ? ParseIntegers(match.Groups[1].Value) : new int[0];
        }

        private static SpawnPosition ParseDungeonStartSpawn(string mapText)
        {
            var values = ParseInlineIntegers(mapText, "dungeon start area");
            if (values.Length < 4) return SpawnPosition.Undefined;

            // ARAD stores this rectangle as left, top, width, height (the same
            // convention as [town movable area]). Its centre is a stable,
            // walkable first-room spawn and avoids the client's (0,0) default.
            var x0 = values[0];
            var y0 = values[1];
            var x1 = x0 + values[2];
            var y1 = y0 + values[3];
            var left = Math.Min(x0, x1);
            var right = Math.Max(x0, x1);
            var top = Math.Min(y0, y1);
            var bottom = Math.Max(y0, y1);
            return new SpawnPosition(ClampCoordinate(left + (right - left) / 2),
                ClampCoordinate(top + (bottom - top) / 2), 5);
        }

        private static SpawnPosition[] ParsePathGatePositions(string mapText)
        {
            var values = ParseTagValues(mapText, "pathgate pos");
            var result = new SpawnPosition[4];
            for (var index = 0; index < result.Length; index++)
            {
                var offset = index * 2;
                result[index] = values.Length >= offset + 2
                    ? new SpawnPosition(ClampCoordinate(values[offset]), ClampCoordinate(values[offset + 1]), 5)
                    : SpawnPosition.Undefined;
            }
            return result;
        }

        private static short ClampCoordinate(int value)
        {
            return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
        }

        private static int[] ParseIntegers(string text)
        {
            var result = new List<int>();
            foreach (Match match in IntegerPattern.Matches(text ?? string.Empty))
            {
                int value;
                if (int.TryParse(match.Value, out value)) result.Add(value);
            }
            return result.ToArray();
        }

        private static MapKind ClassifyMap(string mapText)
        {
            var source = mapText ?? string.Empty;
            if (Regex.IsMatch(source, "(?im)^\\s*\\[type\\]\\s*`\\[boss\\]`")) return MapKind.Boss;
            if (Regex.IsMatch(source, "(?im)^\\s*\\[dungeon start area\\]")) return MapKind.Start;
            return MapKind.Normal;
        }

        private static string Decode(byte[] bytes)
        {
            return ScriptEncoding.GetString(bytes ?? new byte[0]).Replace("\0", string.Empty);
        }

        private static Encoding CreateScriptEncoding()
        {
            return Encoding.GetEncoding(949);
        }

        private void LogInfo(string format, params object[] args)
        {
            Console.WriteLine("[INFO] " + string.Format(format, args));
        }
    }
}
