using System;
using System.Collections.Generic;
using System.Linq;

namespace DF60A1
{
    internal sealed class DungeonCardReward
    {
        public ushort ItemId;
        public uint Count;

        public bool IsValid { get { return Count > 0; } }
        public bool IsGold { get { return ItemId == 0; } }
    }

    // Monotonic clear/card state for the ARAD result flow:
    // clear -> result -> score animation -> card rights -> selection -> leave.
    internal sealed class DungeonSettlementState
    {
        private readonly object _sync = new object();
        private sbyte _championRewardOrdinal;

        public bool ClearEnabled { get; private set; }
        public bool ResultSent { get; private set; }
        public bool CardLayoutSent { get; private set; }
        public bool CardSelectionEnabled { get; private set; }
        public bool FreeCardSelected { get; private set; }
        public bool PaidCardSelected { get; private set; }
        public byte FreeCardIndex { get; private set; }
        public byte PaidCardIndex { get; private set; }
        public bool CardSelected { get { return FreeCardSelected || PaidCardSelected; } }
        public DungeonCardReward[][] FreeCardRewards { get; private set; }
        public DungeonCardReward[][] PaidCardRewards { get; private set; }
        public uint PaidCardPrice { get; private set; }

        public uint[] FreeCardGold
        {
            get
            {
                return FreeCardRewards.Select(SumGold).ToArray();
            }
        }

        public DungeonSettlementState()
        {
            Reset();
        }

        public void Reset()
        {
            lock (_sync)
            {
                ClearEnabled = false;
                ResultSent = false;
                CardLayoutSent = false;
                CardSelectionEnabled = false;
                FreeCardSelected = false;
                PaidCardSelected = false;
                FreeCardIndex = byte.MaxValue;
                PaidCardIndex = byte.MaxValue;
                FreeCardRewards = CreateEmptyCardRows();
                PaidCardRewards = CreateEmptyCardRows();
                PaidCardPrice = 0;
                _championRewardOrdinal = 0;
            }
        }

        public sbyte AllocateChampionRewardOrdinal(bool isBoss, bool isChampion,
            bool isSuperChampion)
        {
            lock (_sync)
            {
                if (isBoss || (!isChampion && !isSuperChampion) ||
                    _championRewardOrdinal >= GamePackets.MaximumChampionRewardOrdinal)
                    return GamePackets.NoChampionRewardOrdinal;
                return ++_championRewardOrdinal;
            }
        }

        public void PrepareCardRewards(int dungeonLevel, int difficulty, Random random)
        {
            PrepareCardRewards(dungeonLevel, difficulty, random, null);
        }

        public void PrepareCardRewards(int dungeonLevel, int difficulty, Random random,
            MonsterIndependentDropCatalog dropCatalog)
        {
            if (random == null) throw new ArgumentNullException("random");
            var level = Math.Max(1, dungeonLevel);
            var multiplier = 1 + Math.Min(3, Math.Max(0, difficulty));
            var goldBase = Math.Max(50, level * 25 * multiplier);
            PaidCardPrice = (uint)Math.Max(100, level * 20 * multiplier);
            for (var card = 0; card < 4; card++)
            {
                var freeItem = PickCardItem(level, random, dropCatalog);
                var paidItem = PickCardItem(level, random, dropCatalog);
                FreeCardRewards[card] = new[]
                {
                    new DungeonCardReward
                    {
                        ItemId = 0,
                        Count = (uint)Math.Max(1, goldBase * random.Next(85, 126) / 100)
                    },
                    new DungeonCardReward
                    {
                        ItemId = freeItem,
                        Count = OfflineDropRules.ScaleItemCount(freeItem, 1)
                    }
                };
                PaidCardRewards[card] = new[]
                {
                    new DungeonCardReward
                    {
                        ItemId = 0,
                        Count = (uint)Math.Max(1, goldBase * random.Next(85, 126) / 100)
                    },
                    new DungeonCardReward
                    {
                        ItemId = paidItem,
                        Count = OfflineDropRules.ScaleItemCount(paidItem,
                            (uint)random.Next(1, 2 + Math.Min(3, Math.Max(0, difficulty))))
                    }
                };
            }
        }

        public DungeonCardReward[] GetCardRewards(byte cardTier, byte cardIndex)
        {
            if (cardIndex >= 4) return new DungeonCardReward[0];
            if (cardTier == 0) return ValidRewards(FreeCardRewards[cardIndex]);
            if (cardTier == 1) return ValidRewards(PaidCardRewards[cardIndex]);
            return new DungeonCardReward[0];
        }

        public DungeonCardReward[] SelectedFreeRewards
        {
            get { return FreeCardSelected && FreeCardIndex < 4 ? GetCardRewards(0, FreeCardIndex) : new DungeonCardReward[0]; }
        }

        public DungeonCardReward[] SelectedPaidRewards
        {
            get { return PaidCardSelected && PaidCardIndex < 4 ? GetCardRewards(1, PaidCardIndex) : new DungeonCardReward[0]; }
        }

        public uint SelectedFreeGold { get { return SumGold(SelectedFreeRewards); } }

        public DungeonCardReward SelectedPaidReward
        {
            get { return SelectedPaidRewards.FirstOrDefault(value => !value.IsGold); }
        }

        public bool TryEnableClear()
        {
            lock (_sync)
            {
                if (ClearEnabled) return false;
                ClearEnabled = true;
                return true;
            }
        }

        public bool TryBeginResult()
        {
            lock (_sync)
            {
                if (!ClearEnabled || ResultSent) return false;
                ResultSent = true;
                return true;
            }
        }

        public bool TryBeginCardLayout()
        {
            lock (_sync)
            {
                if (!ResultSent || CardLayoutSent) return false;
                CardLayoutSent = true;
                return true;
            }
        }

        public bool TryEnableCardSelection()
        {
            lock (_sync)
            {
                if (!CardLayoutSent || CardSelectionEnabled) return false;
                CardSelectionEnabled = true;
                return true;
            }
        }

        public bool TrySelectCard(byte cardTier, byte cardIndex)
        {
            lock (_sync)
            {
                if (!CardSelectionEnabled || cardIndex >= 4 ||
                    GetCardRewards(cardTier, cardIndex).Length == 0) return false;
                if (cardTier == 0)
                {
                    if (FreeCardSelected) return false;
                    FreeCardSelected = true;
                    FreeCardIndex = cardIndex;
                    return true;
                }
                if (cardTier == 1)
                {
                    if (PaidCardSelected) return false;
                    PaidCardSelected = true;
                    PaidCardIndex = cardIndex;
                    return true;
                }
                return false;
            }
        }

        private static ushort PickCardItem(int level, Random random,
            MonsterIndependentDropCatalog dropCatalog)
        {
            if (dropCatalog != null)
            {
                foreach (var kind in new[] { IndependentDropKind.Equipment,
                    IndependentDropKind.Consumable, IndependentDropKind.Recipe,
                    IndependentDropKind.Artifact })
                {
                    int itemId;
                    if (dropCatalog.TryPick(kind, level, 0, random, out itemId) &&
                        itemId > 0 && itemId <= ushort.MaxValue)
                        return (ushort)itemId;
                }
            }
            var fallback = new ushort[] { 8, 1000, 1004, 1047, 3015, 3029 };
            return fallback[random.Next(fallback.Length)];
        }

        private static DungeonCardReward[][] CreateEmptyCardRows()
        {
            return new[] { new DungeonCardReward[0], new DungeonCardReward[0],
                new DungeonCardReward[0], new DungeonCardReward[0] };
        }

        private static DungeonCardReward[] ValidRewards(IEnumerable<DungeonCardReward> rewards)
        {
            return (rewards ?? Enumerable.Empty<DungeonCardReward>())
                .Where(value => value != null && value.IsValid).ToArray();
        }

        private static uint SumGold(IEnumerable<DungeonCardReward> rewards)
        {
            ulong total = 0;
            foreach (var reward in rewards ?? Enumerable.Empty<DungeonCardReward>())
                if (reward != null && reward.IsGold)
                    total = Math.Min(uint.MaxValue, total + reward.Count);
            return (uint)total;
        }
    }
}
