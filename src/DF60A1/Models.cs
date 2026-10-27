using System;

namespace DF60A1
{
    internal sealed class CharacterRecord
    {
        public int Id;
        public int AccountId;
        public byte Slot;
        public string Name;
        public byte Job;
        public byte GrowType;
        // Low nibble is the first-growth class; the high nibble carries the
        // awakening/secondary-growth state used by the Arad wire records.
        public byte PrimaryGrowType { get { return (byte)(GrowType & 0x0f); } }
        public byte SecondaryGrowState { get { return (byte)(GrowType >> 4); } }
        public byte Level;
        public int Exp;
        public int SkillPoints;
        public int Gold;
        public int Cash;
        public bool TutorialStarted;
        public byte TownId;
        public byte AreaId;
        public short X;
        public short Y;
        public byte Direction;
        public int BonusStrength;
        public int BonusIntelligence;
        public int BonusVitality;
        public int BonusSpirit;
        public int BonusMaximumHp;
        public int BonusMaximumMp;
        public int BonusMovementSpeed;
        public int BonusAllResistance;
    }

    internal sealed class CreateCharacterResult
    {
        public bool Success;
        public string Error;
        public CharacterRecord Character;

        public static CreateCharacterResult Fail(string error)
        {
            return new CreateCharacterResult { Success = false, Error = error };
        }
    }

    internal sealed class InventoryItemRecord
    {
        public ushort Slot;
        public ushort ItemId;
        public uint Count;
        public byte State;
        public ushort Durability;
        public byte SealState;
    }

    internal sealed class CharacterSkillRecord
    {
        public byte Slot;
        public byte SkillId;
        public byte Level;
    }

    internal sealed class ConsumeItemRequestRecord
    {
        public ushort Operation;
        public ushort Slot;
        public ushort ItemId;
        public uint RequestedCount;
    }

    internal sealed class InventoryConsumptionRecord
    {
        public ushort Slot;
        public uint ConsumedCount;
    }

    internal sealed class CharacterQuestRecord
    {
        public ushort QuestId;
        public byte Trigger;
    }
}
