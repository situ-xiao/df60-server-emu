using System;
using System.Collections.Generic;

namespace DF60A1
{
    // Per-connection state for the only currently implemented dungeon room.
    // The client owns combat simulation, but the server remains authoritative
    // for the set of spawned runtime monster IDs and room-clear progression.
    internal sealed class TutorialDungeonRun
    {
        private readonly HashSet<ushort> _aliveMonsterIds = new HashSet<ushort>();

        public bool IsActive { get; private set; }
        public bool IsCleared { get; private set; }
        public int AliveMonsterCount { get { return _aliveMonsterIds.Count; } }

        public void Start()
        {
            _aliveMonsterIds.Clear();
            for (ushort id = 0; id < GamePackets.TutorialMonsterCount; id++) _aliveMonsterIds.Add(id);
            IsActive = true;
            IsCleared = false;
        }

        public void Stop()
        {
            _aliveMonsterIds.Clear();
            IsActive = false;
            IsCleared = false;
        }

        public MonsterDeathResult ReportMonsterDeath(ushort monsterId)
        {
            if (!IsActive) return MonsterDeathResult.NotInDungeon;
            if (!_aliveMonsterIds.Remove(monsterId)) return MonsterDeathResult.UnknownOrAlreadyDead;
            if (_aliveMonsterIds.Count == 0)
            {
                IsCleared = true;
                return MonsterDeathResult.RoomCleared;
            }
            return MonsterDeathResult.Accepted;
        }
    }

    internal enum MonsterDeathResult
    {
        NotInDungeon,
        UnknownOrAlreadyDead,
        Accepted,
        RoomCleared
    }
}
