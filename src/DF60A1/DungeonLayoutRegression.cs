using System;
using System.Collections.Generic;
using System.Linq;

namespace DF60A1
{
    internal static class DungeonLayoutRegression
    {
        public static int Run(string scriptPvfPath)
        {
            var catalog = new DungeonCatalog(new PvfArchiveReader(scriptPvfPath));
            DungeonCatalog.Definition definition;
            if (!catalog.TryGetDefinition(3, out definition))
                return Failure("dungeon 3 was not loaded");

            var layouts = definition.MazeLayouts;
            if (layouts == null || layouts.Length < 2)
                return Failure("dungeon 3 did not retain all [maze info] layouts");

            var seenMazeIndexes = new HashSet<byte>();
            var seenStartMaps = new HashSet<ushort>();
            for (var seed = 0; seed < 32; seed++)
            {
                DungeonCatalog.RunLayout layout;
                if (!catalog.TrySelectRunLayout(3, new Random(seed), out layout))
                    return Failure("dungeon 3 run layout selection failed");
                seenMazeIndexes.Add(layout.MazeIndex);
                if (layout.Maze.RoomTopology.Length != 0 &&
                    layout.Maze.RoomTopology.Length != layout.Width * layout.Height)
                    return Failure("[greed] topology was not collapsed to one symbol per room");
                DungeonCatalog.Room room;
                if (!catalog.TryCreateRoom(layout, layout.StartX, layout.StartY, out room))
                    return Failure("selected dungeon 3 first room could not be created");
                seenStartMaps.Add(room.MapId);
            }
            if (seenMazeIndexes.Count < 2)
                return Failure("dungeon 3 always selects the same first-room layout");

            var firstRoomMaps = new Dictionary<ushort, ushort>();
            foreach (var dungeonId in new ushort[] { 1, 3, 7, 8 })
            {
                DungeonCatalog.RunLayout layout;
                DungeonCatalog.Room room;
                if (!catalog.TrySelectRunLayout(dungeonId, null, out layout) ||
                    !catalog.TryCreateRoom(layout, layout.StartX, layout.StartY, out room))
                    return Failure("first room could not be resolved for dungeon " + dungeonId);
                firstRoomMaps[dungeonId] = room.MapId;
            }
            if (firstRoomMaps.Values.Distinct().Count() != firstRoomMaps.Count)
                return Failure("different dungeons resolved to the same first-room map id");

            // Dungeon 40 (Charnak Ridge) is the client-facing regression for
            // the in-map spawn bug.  Its PVF start area is 58,402,146,189,
            // whose centre is (131,496), and its first map is 5601.
            var ridgeRun = new LorienDungeonRun(catalog);
            if (!ridgeRun.Start(40, 0, new Random(0)) || ridgeRun.MapId != 5601 ||
                !ridgeRun.HasSpawnPosition || ridgeRun.SpawnX != 131 || ridgeRun.SpawnY != 496)
                return Failure("dungeon 40 did not retain the PVF start-area spawn (131,496)");
            var ridgePositionPacket = GamePackets.DungeonUserPosition(7,
                ridgeRun.SpawnX, ridgeRun.SpawnY, ridgeRun.SpawnDirection).Payload;
            if (ridgePositionPacket.Length != 9 || LittleEndian.ReadUInt16(ridgePositionPacket, 0) != 7 ||
                LittleEndian.ReadInt16(ridgePositionPacket, 2) != 131 ||
                LittleEndian.ReadInt16(ridgePositionPacket, 4) != 496 ||
                LittleEndian.ReadUInt16(ridgePositionPacket, 7) != 100)
                return Failure("USER_POSITION spawn packet layout failed");

            DungeonCatalog.RunLayout packetLayout;
            if (!catalog.TrySelectRunLayout(3, new Random(0), out packetLayout))
                return Failure("packet layout selection failed");
            var dungeonInfo = GamePackets.LorienDungeonInfo(2, 3, packetLayout.MazeIndex,
                packetLayout.BossX, packetLayout.BossY).Payload;
            if (dungeonInfo.Length != 8 || dungeonInfo[0] != 3 || dungeonInfo[1] != 0 ||
                dungeonInfo[2] != 2 || dungeonInfo[3] != packetLayout.MazeIndex ||
                dungeonInfo[4] != packetLayout.BossX || dungeonInfo[5] != packetLayout.BossY)
                return Failure("DUNGEON_INFO does not describe the selected run layout");

            // ARAD PVF OuterWall.dgn records two equally valid [start map]
            // coordinates: (0,0) and (0,2). Keep both; the old regression that
            // forcibly rejected (0,0) contradicted the actual route data. The
            // important invariant is that either coordinate resolves to an
            // OuterWall start map, never a stale map from another dungeon.
            var outerWallStarts = new HashSet<string>();
            for (var seed = 0; seed < 32; seed++)
            {
                DungeonCatalog.RunLayout outerWallLayout;
                DungeonCatalog.Room outerWallStartRoom;
                if (!catalog.TrySelectRunLayout(21, new Random(seed), out outerWallLayout) ||
                    outerWallLayout.StartX != 0 ||
                    (outerWallLayout.StartY != 0 && outerWallLayout.StartY != 2) ||
                    !catalog.TryCreateRoom(outerWallLayout, outerWallLayout.StartX,
                        outerWallLayout.StartY, out outerWallStartRoom) ||
                    (outerWallStartRoom.MapId != 3001 && outerWallStartRoom.MapId != 3002))
                    return Failure("Outer Wall did not use an ARAD PVF start coordinate/map");
                outerWallStarts.Add(outerWallLayout.StartX + ":" + outerWallLayout.StartY);
            }
            if (!outerWallStarts.SetEquals(new[] { "0:0", "0:2" }))
                return Failure("Outer Wall did not retain every ARAD PVF [start map] candidate");

            // The reported White Land repro has one authoritative PVF start:
            // (1,3) -> map 9200. This guards the exact room-table coordinate
            // used by the supplied client, not a coordinate inferred from
            // visual minimap orientation.
            DungeonCatalog.RunLayout whiteLandLayout;
            DungeonCatalog.Room whiteLandStartRoom;
            var whiteLandRun = new LorienDungeonRun(catalog);
            if (!catalog.TrySelectRunLayout(43, new Random(0), out whiteLandLayout) ||
                !catalog.TryCreateRoom(whiteLandLayout, whiteLandLayout.StartX, whiteLandLayout.StartY,
                    out whiteLandStartRoom) ||
                !whiteLandRun.Start(43, 0, new Random(0)) ||
                whiteLandLayout.StartX != 1 || whiteLandLayout.StartY != 3 ||
                whiteLandRun.RoomX != 1 || whiteLandRun.RoomY != 3 ||
                whiteLandStartRoom.MapId != 9200 || whiteLandRun.MapId != 9200)
                return Failure("White Land did not start at PVF room (1,3) with map 9200");

            // Ice Palace exposes the slot-zero conflict directly: its PVF
            // start is (0,1), while the next room is the physical slot-zero
            // coordinate (0,0). Both cells must remain distinct across the
            // round trip, otherwise the client rebuilds 9103 and leaves doors
            // from a duplicate scene in the room.
            DungeonCatalog.RunLayout icePalaceLayout;
            DungeonCatalog.Room icePalaceStart;
            DungeonCatalog.Room icePalaceUpperLeft;
            var icePalaceRun = new LorienDungeonRun(catalog);
            if (!catalog.TrySelectRunLayout(44, new Random(0), out icePalaceLayout) ||
                icePalaceLayout.Width != 3 || icePalaceLayout.Height != 3 ||
                icePalaceLayout.StartX != 0 || icePalaceLayout.StartY != 1 ||
                icePalaceLayout.BossX != 1 || icePalaceLayout.BossY != 1 ||
                !catalog.TryCreateRoom(icePalaceLayout, 0, 1, out icePalaceStart) ||
                !catalog.TryCreateRoom(icePalaceLayout, 0, 0, out icePalaceUpperLeft) ||
                icePalaceStart.MapId != 9100 || icePalaceUpperLeft.MapId != 9103 ||
                icePalaceStart.Topology != 'K' || icePalaceUpperLeft.Topology != 'J' ||
                !catalog.AreRoomsConnected(icePalaceLayout, 0, 1, 0, 0) ||
                !catalog.AreRoomsConnected(icePalaceLayout, 0, 0, 0, 1) ||
                !icePalaceRun.Start(44, 0, new Random(0)) ||
                !icePalaceRun.TryMoveRoom(0, 0) || icePalaceRun.LastRoomWasRevisit ||
                icePalaceRun.MapId != 9103 ||
                !icePalaceRun.TryMoveRoom(0, 1) || !icePalaceRun.LastRoomWasRevisit ||
                icePalaceRun.MapId != 9100 ||
                !icePalaceRun.TryMoveRoom(0, 0) || !icePalaceRun.LastRoomWasRevisit ||
                icePalaceRun.MapId != 9103)
                return Failure("Ice Palace (0,1)<->(0,0) slot-zero round trip was not PVF-stable");

            var cachedIcePalacePacket = GamePackets.DungeonStartMap(0, 0,
                icePalaceRun.MapId, icePalaceRun.SpawnMonsterRows,
                icePalaceRun.CurrentLevel(), GamePackets.StartMapCachedRoomState, null).Payload;
            if (cachedIcePalacePacket.Length != 7 || cachedIcePalacePacket[0] != 0 ||
                cachedIcePalacePacket[1] != 0 ||
                cachedIcePalacePacket[6] != GamePackets.StartMapCachedRoomState)
                return Failure("Ice Palace cached START_MAP rebuilt the physical slot-zero room");

            // latest_ref_server uses real dungeon 1500 (Vilmark) to guard the
            // complete u16 path, not only packet serialization.  It must keep
            // 1500 in run state and resolve the PVF start room to map 5201.
            DungeonCatalog.Definition vilmarkDefinition;
            var vilmarkRun = new LorienDungeonRun(catalog);
            if (!catalog.TryGetDefinition(1500, out vilmarkDefinition) ||
                !vilmarkRun.Start(1500, 0, new Random(0)) ||
                vilmarkRun.DungeonId != 1500 ||
                vilmarkRun.RoomX != 0 || vilmarkRun.RoomY != 0 ||
                vilmarkRun.MapId != 5201)
                return Failure("Vilmark run truncated dungeon 1500 or selected the wrong start map");
            var vilmarkInfo = GamePackets.LorienDungeonInfo(0, vilmarkRun.DungeonId,
                vilmarkRun.MazeIndex, vilmarkRun.BossX, vilmarkRun.BossY).Payload;
            if (vilmarkInfo.Length != 8 ||
                LittleEndian.ReadUInt16(vilmarkInfo, 0) != 1500 ||
                vilmarkInfo[0] != 0xDC || vilmarkInfo[1] != 0x05)
                return Failure("Vilmark DUNGEON_INFO truncated dungeon 1500");

            // START_MAP must use the selected maze's real [start map]
            // coordinates.  Rebinding it to (0,0) was the cause of every
            // dungeon opening in the upper-left cell with a mismatched map.
            var layoutRandom = new Random(0);
            DungeonCatalog.RunLayout revisitLayout;
            if (!catalog.TrySelectRunLayout(50, layoutRandom, out revisitLayout))
                return Failure("dungeon 50 layout setup failed");
            DungeonCatalog.Room expectedStartRoom;
            if (!catalog.TryCreateRoom(revisitLayout, revisitLayout.StartX, revisitLayout.StartY,
                    out expectedStartRoom))
                return Failure("dungeon 50 selected start room could not be created");
            var revisitRun = new LorienDungeonRun(catalog);
            if (!revisitRun.Start(50, 0, new Random(0)) ||
                revisitRun.RoomX != revisitLayout.StartX || revisitRun.RoomY != revisitLayout.StartY ||
                revisitRun.StartX != revisitLayout.StartX || revisitRun.StartY != revisitLayout.StartY ||
                revisitRun.MapId != expectedStartRoom.MapId)
                return Failure("START_MAP coordinates/map do not match the selected PVF start room");

            byte neighborX;
            byte neighborY;
            if (!TryFindConnectedNeighbor(catalog, revisitLayout, revisitRun.RoomX, revisitRun.RoomY,
                    out neighborX, out neighborY))
                return Failure("dungeon 50 start room has no connected regression-test neighbor");
            var startX = revisitRun.RoomX;
            var startY = revisitRun.RoomY;
            var startMapId = revisitRun.MapId;
            if (!revisitRun.TryMoveRoom(neighborX, neighborY) || revisitRun.LastRoomWasRevisit ||
                !revisitRun.TryMoveRoom(startX, startY) || !revisitRun.LastRoomWasRevisit ||
                revisitRun.MapId != startMapId || revisitRun.AliveMonsterCount != 0)
                return Failure("returning to a cleared start room recreated monsters or changed its map");
            var clearedStartPacket = GamePackets.DungeonStartMap(revisitRun.RoomX, revisitRun.RoomY,
                revisitRun.MapId, revisitRun.SpawnMonsterRows, revisitRun.CurrentLevel()).Payload;
            if (clearedStartPacket[9] != 0)
                return Failure("cleared start-room START_MAP packet still contains monster rows");

            // Preserve the legacy clear-and-transition helper for direct run
            // state coverage. Live ARAD clients now report each death through
            // command 131 before the path-gate request.
            var stagedRun = new LorienDungeonRun(catalog);
            if (!stagedRun.Start(50, 0, new Random(0)) ||
                !stagedRun.CanMoveRoom(neighborX, neighborY) ||
                !stagedRun.MarkCurrentRoomCleared() ||
                stagedRun.RoomX != revisitLayout.StartX || stagedRun.RoomY != revisitLayout.StartY ||
                stagedRun.MapId != expectedStartRoom.MapId || stagedRun.AliveMonsterCount != 0 ||
                !stagedRun.TryMoveRoom(neighborX, neighborY))
                return Failure("legacy clear-and-transition path did not preserve room state");

            var invalidMoveRun = new LorienDungeonRun(catalog);
            if (!invalidMoveRun.Start(50, 0, new Random(0))) return Failure("invalid-move setup failed");
            var invalidMoveAlive = invalidMoveRun.AliveMonsterCount;
            var invalidMoveRuntimeIds = invalidMoveRun.SpawnMonsterRows.Select(row => row.Key).ToArray();
            if (invalidMoveRun.TryMoveRoom(byte.MaxValue, byte.MaxValue) ||
                invalidMoveRun.RoomX != revisitLayout.StartX || invalidMoveRun.RoomY != revisitLayout.StartY ||
                invalidMoveRun.MapId != expectedStartRoom.MapId || invalidMoveRun.AliveMonsterCount != invalidMoveAlive ||
                invalidMoveRun.LastRoomWasRevisit ||
                !invalidMoveRuntimeIds.SequenceEqual(invalidMoveRun.SpawnMonsterRows.Select(row => row.Key)))
                return Failure("closed orthogonal path changed the current room state");

            var movementRun = new LorienDungeonRun(catalog);
            if (!movementRun.Start(50, 0, new Random(0)) ||
                !movementRun.TryMoveRoom(neighborX, neighborY) || movementRun.LastRoomWasRevisit ||
                !movementRun.TryMoveRoom(startX, startY) || movementRun.MapId != startMapId ||
                movementRun.AliveMonsterCount != 0 || !movementRun.LastRoomWasRevisit ||
                !movementRun.TryMoveRoom(neighborX, neighborY) || movementRun.AliveMonsterCount != 0 ||
                !movementRun.LastRoomWasRevisit)
                return Failure("MoveRoom did not preserve cleared rooms or reject a closed path");
            var clearedPacket = GamePackets.DungeonStartMap(movementRun.RoomX, movementRun.RoomY,
                movementRun.MapId, movementRun.SpawnMonsterRows, movementRun.CurrentLevel()).Payload;
            if (clearedPacket.Length != 12 || clearedPacket[0] != neighborX || clearedPacket[1] != neighborY ||
                clearedPacket[6] != 1 || BitConverter.ToUInt16(clearedPacket, 7) != movementRun.MapId ||
                clearedPacket[9] != 0)
                return Failure("cleared-room START_MAP packet still contains monster rows");

            var partialRun = new LorienDungeonRun(catalog);
            if (!partialRun.Start(50, 0, new Random(0)) || !partialRun.EnterRoom(neighborX, neighborY) ||
                partialRun.ReportMonsterDeath(1) != MonsterDeathResult.Accepted)
                return Failure("partial room setup failed");
            var partialPacket = GamePackets.DungeonStartMap(partialRun.RoomX, partialRun.RoomY,
                partialRun.MapId, partialRun.SpawnMonsterRows, partialRun.CurrentLevel()).Payload;
            var packetRuntimeIds = new List<ushort>();
            for (var row = 0; row < partialPacket[9]; row++)
                packetRuntimeIds.Add(BitConverter.ToUInt16(partialPacket, 11 + row * 9));
            if (packetRuntimeIds.Contains(1) || !packetRuntimeIds.Contains(0) || !packetRuntimeIds.Contains(2))
                return Failure("START_MAP renumbered surviving runtime monster ids");

            string exhaustiveFailure;
            int auditedLayouts;
            int auditedRuns;
            int auditedRooms;
            int auditedEdges;
            if (!AuditAllPvfLayouts(catalog, out exhaustiveFailure, out auditedLayouts,
                    out auditedRuns, out auditedRooms, out auditedEdges))
                return Failure(exhaustiveFailure);

            Console.WriteLine("DUNGEON-LAYOUT-REGRESSION OK: dungeons={0} layouts={1} runs={2} rooms={3} edges={4}; dungeon=3 layouts={5} selected={6} startMaps={7}; crossDungeon={8}",
                catalog.DungeonCount, auditedLayouts, auditedRuns, auditedRooms, auditedEdges,
                layouts.Length, string.Join(",", seenMazeIndexes), string.Join(",", seenStartMaps),
                string.Join(",", firstRoomMaps.Select(pair => pair.Key + "->" + pair.Value)));
            return 0;
        }

        private static bool AuditAllPvfLayouts(DungeonCatalog catalog, out string failure,
            out int layoutCount, out int runCount, out int roomCount, out int edgeCount)
        {
            failure = string.Empty;
            layoutCount = 0;
            runCount = 0;
            roomCount = 0;
            edgeCount = 0;
            foreach (var definitionPair in catalog.Definitions)
            {
                var dungeonId = definitionPair.Key;
                var definition = definitionPair.Value;
                if (definition.MazeLayouts == null || definition.MazeLayouts.Length == 0)
                {
                    failure = "dungeon " + dungeonId + " has no PVF maze layout";
                    return false;
                }
                foreach (var maze in definition.MazeLayouts)
                {
                    layoutCount++;
                    if (maze.Width == 0 || maze.Height == 0 ||
                        maze.RoomTopology.Length != maze.Width * maze.Height ||
                        maze.StartRoomPositions.Length == 0 || maze.BossRoomPositions.Length == 0)
                    {
                        failure = "dungeon " + dungeonId + " maze " + maze.Index +
                            " has an incomplete PVF layout";
                        return false;
                    }
                    foreach (var start in maze.StartRoomPositions)
                    {
                        var bossCandidates = maze.BossRoomPositions
                            .Where(value => value.X != start.X || value.Y != start.Y).ToArray();
                        if (bossCandidates.Length == 0) bossCandidates = maze.BossRoomPositions;
                        foreach (var boss in bossCandidates)
                        {
                            runCount++;
                            var layout = new DungeonCatalog.RunLayout(definition, maze, start, boss);
                            DungeonCatalog.Room startRoom;
                            DungeonCatalog.Room bossRoom;
                            if (!catalog.TryCreateRoom(layout, start.X, start.Y, out startRoom))
                            {
                                failure = LayoutFailure(dungeonId, maze.Index, start, boss,
                                    "start room has no compatible map");
                                return false;
                            }
                            if (!catalog.TryCreateRoom(layout, boss.X, boss.Y, out bossRoom))
                            {
                                failure = LayoutFailure(dungeonId, maze.Index, start, boss,
                                    "boss room has no compatible map");
                                return false;
                            }
                            var endpointsDiffer = start.X != boss.X || start.Y != boss.Y;
                            if (endpointsDiffer && (startRoom.HasScriptBossActor ||
                                startRoom.Monsters.Any(monster => monster.IsBoss)))
                            {
                                failure = LayoutFailure(dungeonId, maze.Index, start, boss,
                                    "first room was resolved as a boss map");
                                return false;
                            }

                            var visited = new HashSet<int>();
                            var pending = new Queue<DungeonCatalog.RoomPosition>();
                            pending.Enqueue(start);
                            visited.Add(RoomKey(start.X, start.Y));
                            while (pending.Count != 0)
                            {
                                var current = pending.Dequeue();
                                roomCount++;
                                foreach (var offset in NeighborOffsets)
                                {
                                    var nextX = current.X + offset[0];
                                    var nextY = current.Y + offset[1];
                                    if (nextX < 0 || nextY < 0 || nextX >= maze.Width || nextY >= maze.Height)
                                        continue;
                                    var x = (byte)nextX;
                                    var y = (byte)nextY;
                                    if (!catalog.AreRoomsConnected(layout, current.X, current.Y, x, y))
                                        continue;
                                    DungeonCatalog.Room nextRoom;
                                    if (!catalog.TryCreateRoom(layout, x, y, out nextRoom))
                                    {
                                        failure = LayoutFailure(dungeonId, maze.Index, start, boss,
                                            "connected room (" + x + "," + y + ") has no compatible map");
                                        return false;
                                    }
                                    edgeCount++;
                                    if (visited.Add(RoomKey(x, y)))
                                        pending.Enqueue(new DungeonCatalog.RoomPosition(x, y));
                                }
                            }
                            if (!visited.Contains(RoomKey(boss.X, boss.Y)))
                            {
                                failure = LayoutFailure(dungeonId, maze.Index, start, boss,
                                    "boss room is not reachable from the start through mutual PVF doors");
                                return false;
                            }
                        }
                    }
                }
            }
            return true;
        }

        private static readonly int[][] NeighborOffsets =
        {
            new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 }
        };

        private static int RoomKey(byte x, byte y)
        {
            return x | y << 8;
        }

        private static string LayoutFailure(ushort dungeonId, byte mazeIndex,
            DungeonCatalog.RoomPosition start, DungeonCatalog.RoomPosition boss, string message)
        {
            return "dungeon " + dungeonId + " maze " + mazeIndex + " start=(" + start.X + "," +
                start.Y + ") boss=(" + boss.X + "," + boss.Y + "): " + message;
        }

        private static bool TryFindConnectedNeighbor(DungeonCatalog catalog, DungeonCatalog.RunLayout layout,
            byte roomX, byte roomY, out byte neighborX, out byte neighborY)
        {
            var offsets = new[] { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
            foreach (var offset in offsets)
            {
                var x = (int)roomX + offset[0];
                var y = (int)roomY + offset[1];
                DungeonCatalog.Room room;
                if (x >= 0 && x <= byte.MaxValue && y >= 0 && y <= byte.MaxValue &&
                    catalog.AreRoomsConnected(layout, roomX, roomY, (byte)x, (byte)y) &&
                    catalog.TryCreateRoom(layout, (byte)x, (byte)y, out room))
                {
                    neighborX = (byte)x;
                    neighborY = (byte)y;
                    return true;
                }
            }
            neighborX = 0;
            neighborY = 0;
            return false;
        }

        private static int Failure(string message)
        {
            Console.Error.WriteLine("DUNGEON-LAYOUT-REGRESSION FAILED: " + message);
            return 1;
        }
    }
}
