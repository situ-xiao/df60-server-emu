namespace DF60A1
{
    internal struct TownGateSpawn
    {
        public byte TownId;
        public byte AreaId;
        public short X;
        public short Y;
        public byte Direction;
    }

    /// <summary>Authoritative Gate.map (Seria room) spawn for each Arad town.</summary>
    internal static class TownSpawnCatalog
    {
        public static TownGateSpawn GetGate(byte townId)
        {
            // town/town.lst: Gent is town 6. Preserve it so a character that
            // enters Heaven is not silently sent back to Elvengard on login.
            var normalizedTown = townId >= 1 && townId <= 6 ? townId : (byte)1;
            byte area;
            switch (normalizedTown)
            {
                case 2: area = 5; break; // HendonMyre/Gate_Hendon.map
                case 3: area = 2; break; // HendonMyre/Gate_West.map
                case 6: area = 4; break; // Gent/Gate_Gent.map
                default: area = 1; break;
            }
            return new TownGateSpawn
            {
                TownId = normalizedTown,
                AreaId = area,
                X = 474,
                Y = 234,
                Direction = 5
            };
        }

        /// <summary>Login spawn inside Seria's room, away from any dungeon gate.</summary>
        public static TownGateSpawn GetSeriaSpawn()
        {
            return GetLobbySpawn(1);
        }

        /// <summary>Walkable login position for a town's Gate.map lobby.</summary>
        public static TownGateSpawn GetLobbySpawn(byte townId)
        {
            var gate = GetGate(townId);
            return new TownGateSpawn
            {
                TownId = gate.TownId,
                AreaId = gate.AreaId,
                X = gate.X,
                // Gate.map: [town movable area] 330 324 289 24.
                // Use its centre; 474,234 is the transition marker, not floor.
                Y = 336,
                Direction = 5
            };
        }

        /// <summary>
        /// Gets the position used when leaving dungeon selection.  The gate
        /// marker is an interaction trigger, not a walkable town floor, so
        /// restoring it would immediately reopen dungeon selection.
        /// </summary>
        public static TownGateSpawn GetDungeonReturnSpawn(byte townId, byte areaId,
            short x, short y, byte direction)
        {
            var result = new TownGateSpawn
            {
                TownId = townId,
                AreaId = areaId,
                X = x,
                Y = y,
                Direction = direction
            };
            var gate = GetGate(townId);
            if (areaId == gate.AreaId && x == gate.X && y == gate.Y)
            {
                result.TownId = gate.TownId;
                result.AreaId = gate.AreaId;
                // Move well to the side of the gate trigger.  The previous
                // centre point (474,336) was still inside this client's
                // interaction range.
                result.X = 420;
                result.Y = 336;
            }
            return result;
        }

        /// <summary>
        /// Normalizes the client's Gate marker report.  The Arad client sends
        /// (474,234) after a town transition even though that coordinate is a
        /// trigger marker.  The reference server accepts the report but
        /// stores the nearby Gate position (474,250) instead.
        /// </summary>
        public static TownGateSpawn GetReportedTownPosition(byte townId, byte areaId,
            short x, short y, byte direction)
        {
            var result = new TownGateSpawn
            {
                TownId = townId,
                AreaId = areaId,
                X = x,
                Y = y,
                Direction = direction
            };
            var gate = GetGate(townId);
            if (areaId == gate.AreaId && x == gate.X && y == gate.Y)
            {
                result.TownId = gate.TownId;
                result.AreaId = gate.AreaId;
                result.X = gate.X;
                result.Y = 250;
            }
            return result;
        }

        public static bool ApplySeriaSpawn(CharacterRecord character)
        {
            if (character == null) return false;
            // The client sends a common Gate return marker during login, but
            // the character's persisted town identifies which Seria room to
            // enter.  Do not replace that town with Elvengard (town 1).
            var gate = GetGate(character.TownId);
            var spawn = GetLobbySpawn(gate.TownId);
            var changed = character.TownId != spawn.TownId || character.AreaId != spawn.AreaId ||
                character.X != spawn.X || character.Y != spawn.Y || character.Direction != spawn.Direction;
            character.TownId = spawn.TownId;
            character.AreaId = spawn.AreaId;
            character.X = spawn.X;
            character.Y = spawn.Y;
            character.Direction = spawn.Direction;
            return changed;
        }

        public static bool ApplyGate(CharacterRecord character)
        {
            if (character == null) return false;
            var gate = GetGate(character.TownId);
            var changed = character.TownId != gate.TownId || character.AreaId != gate.AreaId ||
                character.X != gate.X || character.Y != gate.Y || character.Direction != gate.Direction;
            character.TownId = gate.TownId;
            character.AreaId = gate.AreaId;
            character.X = gate.X;
            character.Y = gate.Y;
            character.Direction = gate.Direction;
            return changed;
        }
    }
}
