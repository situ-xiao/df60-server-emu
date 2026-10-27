using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DF60A1
{
    internal static class SelfTest
    {
        public static int RunSkillRegression()
        {
            try
            {
                SkillGrowthRegressionSelfTest();
                Console.WriteLine("SKILL SELF-TEST PASSED");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("SKILL SELF-TEST FAILED: " + exception);
                return 1;
            }
        }

        public static int Run()
        {
            // 自测按 Arad（7 字节下行包头）路径校验负载布局；
            // ARAD 的 6 字节包头由 GameServerPacket.Encode 另行验证。
            var previousLegacy = GameServerPacket.UseLegacyChannelHeader;
            var previousTutorial = GameServerPacket.TutorialEnabled;
            GameServerPacket.UseLegacyChannelHeader = false;
            GameServerPacket.TutorialEnabled = false;
            var root = Path.Combine(Path.GetTempPath(), "arad-emulator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                SkillGrowthRegressionSelfTest();
                var schema = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.sql");
                LocalCashMigrationSelfTest(root, schema);

                // Regression: returning from dungeon selection must not
                // restore the exact gate trigger coordinate, otherwise the
                // client immediately opens dungeon selection again.
                var dungeonReturn = TownSpawnCatalog.GetDungeonReturnSpawn(1, 1, 474, 234, 5);
                if (dungeonReturn.TownId != 1 || dungeonReturn.AreaId != 1 ||
                        dungeonReturn.X != 420 || dungeonReturn.Y != 336 || dungeonReturn.Direction != 5)
                    throw new InvalidOperationException("副本选择返回城镇未离开副本入口触发点");
                var ordinaryReturn = TownSpawnCatalog.GetDungeonReturnSpawn(1, 1, 450, 336, 5);
                if (ordinaryReturn.X != 450 || ordinaryReturn.Y != 336)
                    throw new InvalidOperationException("普通城镇返回位置被意外修改");
                var reportedGate = TownSpawnCatalog.GetReportedTownPosition(1, 1, 474, 234, 5);
                if (reportedGate.TownId != 1 || reportedGate.AreaId != 1 ||
                    reportedGate.X != 474 || reportedGate.Y != 250 || reportedGate.Direction != 5)
                    throw new InvalidOperationException("回城后入口位置上报未按参考服务端校正");
                var reportedOrdinary = TownSpawnCatalog.GetReportedTownPosition(1, 1, 450, 336, 5);
                if (reportedOrdinary.X != 450 || reportedOrdinary.Y != 336)
                    throw new InvalidOperationException("普通位置上报被意外校正");
                if (GamePackets.LeaveDungeonSelectionObserved != 146)
                    throw new InvalidOperationException("副本选择返回城镇命令号应为 146");

                using (var store = new SqliteGameStore(Path.Combine(root, "test.sqlite"), schema))
                {
                    var account = store.EnsureAccount("test", "test");
                    var created = store.CreateCharacter(account, "赛丽亚测试", 0);
                    if (!created.Success) throw new InvalidOperationException(created.Error);
                    if (created.Character.Cash != SqliteGameStore.InitialLocalCash ||
                        store.ListCharacters(account).Single().Cash != SqliteGameStore.InitialLocalCash)
                        throw new InvalidOperationException("new local character did not receive the one-time cash-shop test balance");
                    if (created.Character.AreaId != 1 || created.Character.X != 474 || created.Character.Y != 336)
                        throw new InvalidOperationException("新角色返回对象的赛丽亚房间位置失败");
                    var characters = store.ListCharacters(account);
                    if (characters.Count != 1 || characters[0].Name != "赛丽亚测试") throw new InvalidOperationException("SQLite 角色往返失败");
                    if (characters[0].TownId != 1 || characters[0].AreaId != 1 ||
                        characters[0].X != 474 || characters[0].Y != 336 || characters[0].Direction != 5)
                        throw new InvalidOperationException("国服新角色赛丽亚房间初始位置失败");
                    var createdSkills = store.LoadSkills(created.Character.Id);
                    if (created.Character.SkillPoints != 0 || characters[0].SkillPoints != 0)
                        throw new InvalidOperationException("新角色返回对象及数据库初始 SP 应为 0");
                    if (createdSkills.Count != 7 ||
                        !createdSkills.Any(value => value.Slot == 0 && value.SkillId == 5 && value.Level == 1) ||
                        !createdSkills.Any(value => value.Slot == 1 && value.SkillId == 46 && value.Level == 1) ||
                        !createdSkills.Any(value => value.Slot == 174 && value.SkillId == 181 && value.Level == 2) ||
                        !createdSkills.Any(value => value.Slot == 177 && value.SkillId == 173 && value.Level == 1) ||
                        !createdSkills.Any(value => value.Slot == 178 && value.SkillId == 179 && value.Level == 5))
                        throw new InvalidOperationException("SQLite 新角色主动/被动出生技能初始化失败");

                    var movedInitialWithOccupiedDefault = GamePackets.CurrentSkills(0, new[]
                    {
                        new CharacterSkillRecord { Slot = 158, SkillId = 5, Level = 1 },
                        new CharacterSkillRecord { Slot = 67, SkillId = 46, Level = 1 },
                        new CharacterSkillRecord { Slot = 0, SkillId = 169, Level = 1 }
                    });
                    if (movedInitialWithOccupiedDefault.GroupBy(value => value.Slot).Any(group => group.Count() != 1) ||
                        !movedInitialWithOccupiedDefault.Any(value => value.SkillId == 169 && value.Slot == 0) ||
                        !movedInitialWithOccupiedDefault.Any(value => value.SkillId == 5 && value.Slot != 0))
                        throw new InvalidOperationException("Skill normalization produced duplicate persisted slots");

                    var savedInventory = store.LoadInventory(created.Character.Id);
                    if (savedInventory.Count != 0)
                        throw new InvalidOperationException("New character inventory must be empty");
                    savedInventory.Add(new InventoryItemRecord { Slot = 42, ItemId = 8, Count = 99 });
                    store.SaveInventory(created.Character.Id, savedInventory);
                    var reloadedInventory = store.LoadInventory(created.Character.Id);
                    if (reloadedInventory.Count != 1 || reloadedInventory[0].Slot != 42 || reloadedInventory[0].Count != 99)
                        throw new InvalidOperationException("SQLite 背包槽位/数量持久化失败");
                    var savedStorage = new[]
                    {
                        new InventoryItemRecord { Slot = 119, ItemId = 8, Count = 12, State = 1, Durability = 2, SealState = 3 }
                    };
                    store.SaveInventoryAndStorage(created.Character.Id, reloadedInventory, savedStorage);
                    var reloadedStorage = store.LoadStorage(created.Character.Id);
                    if (reloadedStorage.Count != 1 || reloadedStorage[0].Slot != 119 ||
                        reloadedStorage[0].ItemId != 8 || reloadedStorage[0].Count != 12 ||
                        reloadedStorage[0].State != 1 || reloadedStorage[0].Durability != 2 || reloadedStorage[0].SealState != 3)
                        throw new InvalidOperationException("SQLite storage persistence failed");
                    store.SaveGoldAndInventory(created.Character.Id, 98765, reloadedInventory);
                    if (store.ListCharacters(account)[0].Gold != 98765)
                        throw new InvalidOperationException("SQLite 金币与背包事务持久化失败");

                    var savedEquipment = store.LoadEquipment(created.Character.Id, created.Character.Job);
                    if (savedEquipment.Count != 1 || savedEquipment[0].Slot != 9 || savedEquipment[0].ItemId != 27000)
                        throw new InvalidOperationException("SQLite 初始武器/装备槽初始化失败");
                    savedEquipment[0].ItemId = 27001;
                    store.SaveEquipment(created.Character.Id, savedEquipment);
                    if (store.LoadEquipment(created.Character.Id, created.Character.Job).Single().ItemId != 27001)
                        throw new InvalidOperationException("SQLite 装备持久化失败");

                    var atomicInventory = new[]
                    {
                        new InventoryItemRecord { Slot = 12, ItemId = 27001, Count = 1, Durability = 35 }
                    };
                    var atomicEquipment = new[]
                    {
                        new InventoryItemRecord { Slot = 9, ItemId = 27002, Count = 1, Durability = 35 }
                    };
                    store.SaveInventoryAndEquipment(created.Character.Id, atomicInventory, atomicEquipment);
                    if (store.LoadInventory(created.Character.Id).Single().ItemId != 27001 ||
                        store.LoadEquipment(created.Character.Id, created.Character.Job).Single().ItemId != 27002)
                        throw new InvalidOperationException("SQLite inventory/equipment atomic persistence failed");

                    var persistedSkills = new[] { new CharacterSkillRecord { Slot = 2, SkillId = 9, Level = 1 } };
                    store.SaveSkills(created.Character.Id, 9989, persistedSkills);
                    var reloadedSkills = store.LoadSkills(created.Character.Id);
                    if (reloadedSkills.Count != 1 || reloadedSkills[0].Slot != 2 || reloadedSkills[0].SkillId != 9 ||
                        store.ListCharacters(account)[0].SkillPoints != 9989)
                        throw new InvalidOperationException("SQLite 技能/SP 持久化失败");

                    store.SaveSkills(created.Character.Id, 9989, new[]
                    {
                        new CharacterSkillRecord { Slot = 48, SkillId = 27, Level = 1 },
                        new CharacterSkillRecord { Slot = 48, SkillId = 33, Level = 1 }
                    });
                    var repairedDuplicateSlots = store.LoadSkills(created.Character.Id);
                    if (repairedDuplicateSlots.Count != 2 ||
                        repairedDuplicateSlots.Select(value => value.Slot).Distinct().Count() != 2)
                        throw new InvalidOperationException("SQLite skill persistence did not repair duplicate slot indexes");

                    store.SaveActiveQuest(created.Character.Id, new CharacterQuestRecord { QuestId = 1000, Trigger = 2 });
                    store.SaveActiveQuest(created.Character.Id, new CharacterQuestRecord { QuestId = 1001, Trigger = 4 });
                    var activeQuestRecords = store.LoadActiveQuests(created.Character.Id);
                    var activeQuest = activeQuestRecords.Single(value => value.QuestId == 1000);
                    if (activeQuestRecords.Count != 2 || activeQuest.Trigger != 2)
                        throw new InvalidOperationException("SQLite 任务接受/进度持久化失败");
                    store.GiveupQuest(created.Character.Id, 1001);
                    store.CompleteQuest(created.Character.Id, 1000);
                    if (store.LoadActiveQuests(created.Character.Id).Count != 0 ||
                        !store.LoadCompletedQuests(created.Character.Id).Contains(1000))
                        throw new InvalidOperationException("SQLite 任务完成持久化失败");

                    store.SaveAcceptedQuestAndInventory(created.Character.Id,
                        new CharacterQuestRecord { QuestId = 4564, Trigger = 1 }, new[]
                        {
                            new InventoryItemRecord { Slot = 105, ItemId = 4291, Count = 1 }
                        });
                    if (!store.LoadActiveQuests(created.Character.Id).Any(value => value.QuestId == 4564 && value.Trigger == 1) ||
                        !store.LoadInventory(created.Character.Id).Any(value => value.Slot == 105 && value.ItemId == 4291 && value.Count == 1))
                        throw new InvalidOperationException("SQLite quest-accept item transaction failed");
                    store.GiveupQuest(created.Character.Id, 4564);

                    var packet = GamePackets.CharacterList(characters).Encode();
                    if (packet.Length < 7 || packet[0] != 0 || packet[1] != GamePackets.UserInfo) throw new InvalidOperationException("USERINFO 编码失败");

                    var keyConfigSaved = GamePackets.ClientConfigurationSaved(217);
                    var volumeConfigSaved = GamePackets.ClientConfigurationSaved(212);
                    if (keyConfigSaved.Type != GamePackets.Command || keyConfigSaved.ProtocolId != 217 ||
                        keyConfigSaved.Payload.Length != 1 || keyConfigSaved.Payload[0] != 1 ||
                        volumeConfigSaved.Type != GamePackets.Command || volumeConfigSaved.ProtocolId != 212 ||
                        volumeConfigSaved.Payload.Length != 1 || volumeConfigSaved.Payload[0] != 1)
                        throw new InvalidOperationException("ARAD client configuration save acknowledgement is malformed");

                    var restoredQuestPacket = GamePackets.AradSelectOk(created.Character, 1, new[]
                    {
                        new CharacterQuestRecord { QuestId = 151, Trigger = 2 },
                        new CharacterQuestRecord { QuestId = 142, Trigger = 1 }
                    });
                    if (restoredQuestPacket.Payload.Length != 88 ||
                        LittleEndian.ReadUInt16(restoredQuestPacket.Payload, 22) != 151 ||
                        LittleEndian.ReadUInt32(restoredQuestPacket.Payload, 24) != 2 ||
                        LittleEndian.ReadUInt16(restoredQuestPacket.Payload, 28) != 142 ||
                        LittleEndian.ReadUInt32(restoredQuestPacket.Payload, 30) != 1)
                        throw new InvalidOperationException("ARAD select-character response did not restore active quests");

                    if (QuestCatalog.MaxActiveQuests != 10)
                        throw new InvalidOperationException("ARAD active-quest capacity exceeds its ten login restore slots");

                    var loginPacket = GamePackets.LoginOk();
                    if (loginPacket.Payload == null || loginPacket.Payload.Length != 19 || loginPacket.Payload[0] != 1)
                        throw new InvalidOperationException("?? LOGIN 成功响应布局失败");

                    var payload = packet.Skip(GameServerPacket.HeaderLength).ToArray(); GamePayloadCipher.Decrypt(payload, 0, payload.Length);
                    if (payload[0] != 2 || LittleEndian.ReadUInt16(payload, 1) != 1) throw new InvalidOperationException("服务端负载加密往返失败");
                    var characterNameBytes = System.Text.Encoding.GetEncoding(936).GetBytes("赛丽亚测试");
                    var characterJobOffset = 9 + characterNameBytes.Length;
                    if (payload.Length != 24 + characterNameBytes.Length || payload[payload.Length - 1] != 1)
                        throw new InvalidOperationException("?? mode 2 角色预览记录布局失败");
                    if (LittleEndian.ReadUInt16(payload, 3) != 0 || LittleEndian.ReadUInt32(payload, 5) != (uint)characterNameBytes.Length ||
                        !payload.Skip(9).Take(characterNameBytes.Length).SequenceEqual(characterNameBytes) ||
                        payload[characterJobOffset] != 0 || payload[characterJobOffset + 1] != 0 || payload[characterJobOffset + 2] != 1)
                        throw new InvalidOperationException("?? 角色卡 slot/job/grow/level 字段布局失败");
                    if (payload.Skip(characterJobOffset + 3).Take(11).Any(value => value != 0))
                        throw new InvalidOperationException("?? 角色卡状态与公会字段默认值失败");

                    var awakened = new CharacterRecord
                    {
                        Slot = 4, Name = "Awakened", Job = 0, GrowType = 0x13, Level = 60
                    };
                    var awakenedList = GamePackets.CharacterList(new[] { awakened }).Payload;
                    var awakenedNameLength = System.Text.Encoding.GetEncoding(936).GetByteCount(awakened.Name);
                    var awakenedJobOffset = 9 + awakenedNameLength;
                    if (awakenedList[awakenedJobOffset + 1] != 3 ||
                        awakenedList[awakenedJobOffset + 3] != 1)
                        throw new InvalidOperationException("?? awakened character-list growth fields failed");

                    var awakenedTown = GamePackets.TownCharacter(awakened, 1).Payload;
                    var awakenedTownJobOffset = 9 + awakenedNameLength;
                    if (awakenedTown[awakenedTownJobOffset + 1] != 3 ||
                        awakenedTown[awakenedTownJobOffset + 3] != 1)
                        throw new InvalidOperationException("?? awakened town-character growth fields failed");

                    var emptyPacket = GamePackets.CharacterList(new CharacterRecord[0]).Encode();
                    if (emptyPacket.Length != 10 || emptyPacket[7] != 0xDE || emptyPacket[8] != 0xD6 || emptyPacket[9] != 0xD6)
                        throw new InvalidOperationException("?? 服务端下行固定向量失败");
                    var emptyPayload = emptyPacket.Skip(GameServerPacket.HeaderLength).ToArray();
                    GamePayloadCipher.Decrypt(emptyPayload, 0, emptyPayload.Length);
                    if (emptyPayload.Length != 3 || emptyPayload[0] != 2 || LittleEndian.ReadUInt16(emptyPayload, 1) != 0)
                        throw new InvalidOperationException("?? 空角色列表布局失败");

                    var channelPacket = GamePackets.Channel(new byte[16]).Encode();
                    if (channelPacket.Length != 33 || channelPacket.Skip(GameServerPacket.HeaderLength).Any(value => value != 0xD6))
                        throw new InvalidOperationException("?? CHANNEL_INFO 固定向量失败");
                    var channelPayload = channelPacket.Skip(GameServerPacket.HeaderLength).ToArray();
                    GamePayloadCipher.Decrypt(channelPayload, 0, channelPayload.Length);
                    if (channelPayload.Length != 26 || channelPayload.Any(value => value != 0))
                        throw new InvalidOperationException("?? CHANNEL_INFO 最小配置布局失败");

                    byte parsedJob; string parsedName;
                    var createBody = ParseHex("03000204000000B2E2CAD4");
                    if (!GamePackets.TryReadCreateCharacter(createBody, out parsedJob, out parsedName) ||
                        parsedJob != 2 || parsedName != "测试")
                        throw new InvalidOperationException("?? CREATE_CHARACTER 请求解析失败");
                    createBody[2] = 5;
                    if (GamePackets.TryReadCreateCharacter(createBody, out parsedJob, out parsedName))
                        throw new InvalidOperationException("?? CREATE_CHARACTER 接受了无效职业");

                    ushort buyItemId; int buyCount;
                    if (!GamePackets.TryReadBuyItem(ParseHex("1300E02E0000000F000000"), out buyItemId, out buyCount) ||
                        buyItemId != 12000 || buyCount != 15)
                        throw new InvalidOperationException("?? BUY_ITEM 请求解析失败");
                    var buyReply = GamePackets.BuyItemReply(0, 12);
                    if (buyReply.ProtocolId != GamePackets.BuyItem || buyReply.Payload.Length != 3 ||
                        buyReply.Payload[0] != 1 || buyReply.Payload[1] != 0 || buyReply.Payload[2] != 12)
                        throw new InvalidOperationException("?? BUY_ITEM 响应布局失败");

                    var shopProducts = new Dictionary<uint, CeraShopProduct>();
                    CeraShopCatalog.Parse("[item]\n100053 37 10 0 0 620 `test`", shopProducts);
                    CeraShopProduct shopProduct;
                    if (!shopProducts.TryGetValue(100053, out shopProduct) || shopProduct.ItemId != 37 ||
                        shopProduct.Quantity != 10 || shopProduct.CashPrice != 620)
                        throw new InvalidOperationException("?? cash-shop catalog parsing failed");
                    byte parsedShopCategory; uint[] parsedCommodities; byte[] parsedOptions;
                    if (!GameServer.TryReadCeraShopPurchase(ParseHex("00000201020000D5860100"),
                            out parsedShopCategory, out parsedCommodities, out parsedOptions) ||
                        parsedShopCategory != 2 || parsedCommodities.Length != 1 ||
                        parsedCommodities[0] != 100053 || parsedOptions[0] != 2 ||
                        !GameServer.TryReadCeraShopPurchase(ParseHex("0000020102D5860100"),
                            out parsedShopCategory, out parsedCommodities, out parsedOptions) ||
                        parsedCommodities[0] != 100053 || parsedOptions[0] != 2)
                        throw new InvalidOperationException("cash-shop purchase request parsing failed");
                    if (GameServer.ResolveAvatarPurchaseOption(byte.MaxValue, 1) != 1 ||
                        GameServer.ResolveAvatarPurchaseOption(3, 1) != 3 ||
                        GameServer.ResolveAvatarPurchaseOption(2, 1) != 2)
                        throw new InvalidOperationException("cash-shop avatar option selection failed");
                    var ceraReply = GamePackets.CeraShopBuyReply(2, 100053, 37, 10);
                    if (ceraReply.ProtocolId != GamePackets.CeraShopBuy || ceraReply.Payload.Length != 28 ||
                        ceraReply.Payload[0] != 1 || ceraReply.Payload[1] != 2 ||
                        LittleEndian.ReadUInt32(ceraReply.Payload, 2) != 2 ||
                        LittleEndian.ReadUInt32(ceraReply.Payload, 6) != 100053 ||
                        LittleEndian.ReadUInt32(ceraReply.Payload, 10) != 0 ||
                        LittleEndian.ReadUInt32(ceraReply.Payload, 14) != 0 ||
                        LittleEndian.ReadUInt16(ceraReply.Payload, 18) != 37 ||
                        LittleEndian.ReadUInt16(ceraReply.Payload, 20) != 0 ||
                        LittleEndian.ReadUInt16(ceraReply.Payload, 22) != ushort.MaxValue ||
                        LittleEndian.ReadUInt32(ceraReply.Payload, 24) != 0)
                        throw new InvalidOperationException("?? cash-shop purchase reply layout failed");
                    var ceraBalance = GamePackets.CeraBalance((uint)SqliteGameStore.InitialLocalCash);
                    if (ceraBalance.Type != GamePackets.Notification ||
                        ceraBalance.ProtocolId != GamePackets.CeraUpdate || ceraBalance.Payload.Length != 5 ||
                        ceraBalance.Payload[0] != 1 ||
                        LittleEndian.ReadUInt32(ceraBalance.Payload, 1) != SqliteGameStore.InitialLocalCash)
                        throw new InvalidOperationException("cash-shop initial balance packet layout failed");
                    var shopAccount = store.EnsureAccount("shop-test", "test");
                    var shopCharacter = store.CreateCharacter(shopAccount, "商城事务测试", 1);
                    if (!shopCharacter.Success)
                        throw new InvalidOperationException("cash-shop transaction character creation failed");
                    store.SaveCashInventoryAndAvatars(shopCharacter.Character.Id, 380,
                        new[]
                        {
                            new InventoryItemRecord { Slot = 41, ItemId = 8, Count = 10 }
                        },
                        new[]
                        {
                            new InventoryItemRecord
                            {
                                Slot = 9, ItemId = 20001, Count = 0, Durability = 2
                            }
                        });
                    var persistedShopCharacter = store.ListCharacters(shopAccount).Single();
                    var persistedShopItem = store.LoadInventory(persistedShopCharacter.Id).Single();
                    var persistedShopAvatar = store.LoadAvatars(persistedShopCharacter.Id).Single();
                    if (persistedShopCharacter.Cash != 380 || persistedShopItem.ItemId != 8 ||
                        persistedShopItem.Count != 10 || persistedShopAvatar.ItemId != 20001 ||
                        persistedShopAvatar.Count != 0 || persistedShopAvatar.Durability != 2)
                        throw new InvalidOperationException("cash-shop atomic persistence failed");

                    byte sellList; ushort sellSlot; int sellCount;
                    if (!GamePackets.TryReadSellItem(ParseHex("1400000C000300"), out sellList, out sellSlot, out sellCount) ||
                        sellList != 0 || sellSlot != 12 || sellCount != 3)
                        throw new InvalidOperationException("?? SELL_ITEM request layout failed");
                    var sellReply = GamePackets.SellItemReply(12345);
                    if (sellReply.ProtocolId != GamePackets.SellItem || sellReply.Payload.Length != 14 ||
                        sellReply.Payload[0] != 1 || LittleEndian.ReadUInt32(sellReply.Payload, 1) != 12345 ||
                        sellReply.Payload.Skip(5).Any(value => value != 0))
                        throw new InvalidOperationException("?? SELL_ITEM reply layout failed");
                    byte tradeList; ushort tradeSlot; ushort tradeCount;
                    if (!GamePackets.TryReadSetItemTradeState(ParseHex("1C00000A000100"), out tradeList, out tradeSlot, out tradeCount) ||
                        tradeList != 0 || tradeSlot != 10 || tradeCount != 1)
                        throw new InvalidOperationException("?? SET_ITEM_TRADE_STATE request layout failed");
                    var tradeReply = GamePackets.SetItemTradeStateReply(54321, 0, 10, 0);
                    if (tradeReply.ProtocolId != GamePackets.SetItemTradeState || tradeReply.Payload.Length != 10 ||
                        tradeReply.Payload[0] != 1 || LittleEndian.ReadUInt32(tradeReply.Payload, 1) != 54321 ||
                        tradeReply.Payload[5] != 0 || LittleEndian.ReadUInt16(tradeReply.Payload, 6) != 10 ||
                        LittleEndian.ReadUInt16(tradeReply.Payload, 8) != 0)
                        throw new InvalidOperationException("?? SET_ITEM_TRADE_STATE reply layout failed");

                    characters[0].TutorialStarted = true;
                    if (GameServerPacket.TutorialEnabled)
                        throw new InvalidOperationException("新手教程已按要求关闭");
                    var expectedGateAreas = new byte[] { 1, 5, 2, 1, 1, 4 };
                    for (byte townId = 1; townId <= expectedGateAreas.Length; townId++)
                    {
                        var gate = TownSpawnCatalog.GetGate(townId);
                        if (gate.TownId != townId || gate.AreaId != expectedGateAreas[townId - 1] ||
                            gate.X != 474 || gate.Y != 234 || gate.Direction != 5)
                            throw new InvalidOperationException("城镇 Gate.map 出生点映射失败: town=" + townId);
                    }
                    var persistedTown = new CharacterRecord
                    {
                        TownId = 2,
                        AreaId = 5,
                        X = 474,
                        Y = 234,
                        Direction = 5
                    };
                    TownSpawnCatalog.ApplySeriaSpawn(persistedTown);
                    if (persistedTown.TownId != 2 || persistedTown.AreaId != 5)
                        throw new InvalidOperationException("鐧诲綍鍑虹敓鐐逛笉搴旇鐩栦繚瀛樼殑鍩庨晣");
                    var gentTown = new CharacterRecord
                    {
                        TownId = 6,
                        AreaId = 4,
                        X = 474,
                        Y = 234,
                        Direction = 5
                    };
                    TownSpawnCatalog.ApplySeriaSpawn(gentTown);
                    if (gentTown.TownId != 6 || gentTown.AreaId != 4 ||
                        gentTown.X != 474 || gentTown.Y != 336 || gentTown.Direction != 5)
                        throw new InvalidOperationException("Gent Gate.map login spawn mapping failed");
                    var selectPacket = GamePackets.SelectOk(characters[0], 1).Encode();
                    var selectPayload = selectPacket.Skip(GameServerPacket.HeaderLength).ToArray();
                    GamePayloadCipher.Decrypt(selectPayload, 0, selectPayload.Length);
                    if (selectPayload.Length != 42 || selectPayload[0] != 1)
                        throw new InvalidOperationException("?? SELECT_CHARACTER 成功包布局失败");
                    if (LittleEndian.ReadUInt32(selectPayload, 1) != (uint)characters[0].Id ||
                        LittleEndian.ReadUInt16(selectPayload, 5) != 1 ||
                        selectPayload[37] != characters[0].TownId)
                        throw new InvalidOperationException("?? SELECT_CHARACTER 角色ID/服务器ID/城镇字段失败");
                    // Regression: globally disabling the tutorial must complete
                    // every client tutorial module. Completing only module 30
                    // enters town but leaves the hidden tutorial input controller
                    // able to capture all keyboard actions.
                    if (LittleEndian.ReadUInt32(selectPayload, 38) != uint.MaxValue)
                        throw new InvalidOperationException("?? SELECT_CHARACTER tutorial module mask is incomplete");
                    var citrisSelect = GamePackets.CitrisSelectOk(characters[0], 1).Payload;
                    if (citrisSelect.Length != 150 || citrisSelect[0] != 1 ||
                        LittleEndian.ReadUInt32(citrisSelect, 1) != (uint)characters[0].Id ||
                        LittleEndian.ReadUInt16(citrisSelect, 5) != 1 ||
                        citrisSelect[79] != characters[0].TownId ||
                        citrisSelect[80] != 0 ||
                        citrisSelect[81] != 0x42 ||
                        citrisSelect[82] != 0 ||
                        citrisSelect[82 + 0x1e] != 0x1e ||
                        citrisSelect[82 + 0x41] != 0x41)
                        throw new InvalidOperationException("Arad SELECT_CHARACTER layout failed");
                    var selectWithQuest = GamePackets.SelectOk(characters[0], 1, new[]
                    {
                        new CharacterQuestRecord { QuestId = 1016, Trigger = 2 }
                    }).Payload;
                    if (selectWithQuest.Length != 42 || LittleEndian.ReadUInt16(selectWithQuest, 18) != 1016 ||
                        LittleEndian.ReadUInt32(selectWithQuest, 20) != 2 ||
                        LittleEndian.ReadUInt16(selectWithQuest, 24) != ushort.MaxValue)
                        throw new InvalidOperationException("?? SELECT_CHARACTER 进行中任务恢复布局失败");

                    var townCharacter = GamePackets.TownCharacter(characters[0], 1).Payload;
                    var townNameOffset = 9 + characterNameBytes.Length;
                    if (townCharacter.Length != 53 + characterNameBytes.Length ||
                        LittleEndian.ReadUInt32(townCharacter, 5) != (uint)characterNameBytes.Length ||
                        !townCharacter.Skip(9).Take(characterNameBytes.Length).SequenceEqual(characterNameBytes) ||
                        townCharacter[townNameOffset] != characters[0].Job ||
                        townCharacter[townNameOffset + 1] != characters[0].GrowType ||
                        townCharacter[townNameOffset + 2] != characters[0].Level ||
                        townCharacter[townCharacter.Length - 13] != 1 ||
                        townCharacter.Skip(townCharacter.Length - 12).Any(value => value != 0))
                        throw new InvalidOperationException("?? USERINFO mode 0 完整实体尾布局失败");

                    // ARAD's longer mode-0 layout passes this tail byte to the
                    // local town actor state method.  Zero produces a valid
                    // entity with coordinates but suppresses its sprite.
                    var aradTownCharacter = GamePackets.AradTownCharacter(characters[0], 1).Payload;
                    var aradNameOffset = 9 + characterNameBytes.Length;
                    var aradAppearanceCount = aradTownCharacter[aradNameOffset + 6];
                    var aradTailOffset = aradNameOffset + 7 + aradAppearanceCount * 14;
                    if (aradAppearanceCount == 0 || aradTownCharacter.Length < aradTailOffset + 52 ||
                        aradTownCharacter[aradTailOffset + 35] != 1)
                        throw new InvalidOperationException("ARAD USERINFO mode 0 local actor visibility flag failed");

                    var townWeapon = GamePackets.TownCharacter(characters[0], 1, new[]
                    {
                        new InventoryItemRecord { Slot = 9, ItemId = 31000, Count = 1, Durability = 35 }
                    }).Payload;
                    if (townWeapon.Length != townCharacter.Length + 4 ||
                        townWeapon[townNameOffset + 6] != 1 || townWeapon[townNameOffset + 7] != 9 ||
                        LittleEndian.ReadUInt16(townWeapon, townNameOffset + 8) != 31000 || townWeapon[townNameOffset + 10] != 0)
                        throw new InvalidOperationException("?? USERINFO mode 0 城镇武器外观布局失败");

                    var townAvatar = GamePackets.TownCharacter(characters[0], 1, null, new[]
                    {
                        new InventoryItemRecord { Slot = 0, ItemId = 39000, Count = 1 },
                        new InventoryItemRecord { Slot = 9, ItemId = 39400, Count = 1 }
                    }).Payload;
                    if (townAvatar.Length != townCharacter.Length + 4 || townAvatar[townNameOffset + 6] != 0 ||
                        townAvatar[townNameOffset + 7] != 1 || townAvatar[townNameOffset + 8] != 0 ||
                        LittleEndian.ReadUInt16(townAvatar, townNameOffset + 9) != 39000 || townAvatar[townNameOffset + 11] != 0)
                        throw new InvalidOperationException("?? USERINFO mode 0 avatar appearance layout failed");

                    var userArea = GamePackets.Area(characters[0], 1).Payload;
                    var areaUsers = GamePackets.AreaRoster(characters[0], 1).Payload;
                    if (userArea.Length != 10 || userArea[9] != 3 ||
                        areaUsers.Length != 12 || areaUsers[11] != 3)
                        throw new InvalidOperationException("?? USER_AREA/AREA_USERS 城镇状态布局失败");

                    // USERINFO carries only the two base-job active skills;
                    // class-4 common passives are created by SKILL_INFO.
                    var detailsPayload = GamePackets.CharacterDetails(1, 0, 0).Payload;
                    if (detailsPayload.Length != 123 || LittleEndian.ReadUInt32(detailsPayload, 9) != 80 ||
                        LittleEndian.ReadUInt32(detailsPayload, 13) != 1800 ||
                        LittleEndian.ReadUInt32(detailsPayload, 17) != 1400 ||
                        LittleEndian.ReadInt16(detailsPayload, 21) != 70 ||
                        LittleEndian.ReadUInt32(detailsPayload, 71) != 400000 ||
                        LittleEndian.ReadUInt32(detailsPayload, 89) != 680000 ||
                        detailsPayload[93] != 1 || detailsPayload[94] != 9 || LittleEndian.ReadUInt32(detailsPayload, 95) != 27000 ||
                        LittleEndian.ReadUInt32(detailsPayload, 106) != uint.MaxValue ||
                        detailsPayload[117] != 2 || detailsPayload[118] != 5 || detailsPayload[120] != 46 ||
                        detailsPayload[122] != 0)
                        throw new InvalidOperationException("?? USERINFO 属性/负重/装备/技能列表布局失败");

                    var multiEquipmentDetails = GamePackets.CharacterDetails(1, 0, 0, new[]
                    {
                        new InventoryItemRecord { Slot = 9, ItemId = 27098, Count = 1, State = 10, Durability = 35 },
                        new InventoryItemRecord { Slot = 15, ItemId = 16879, Count = 1, State = 10, Durability = 24 },
                        new InventoryItemRecord { Slot = 17, ItemId = 22078, Count = 1, State = 10, Durability = 24 }
                    }).Payload;
                    if (multiEquipmentDetails[93] != 3 || multiEquipmentDetails[94] != 9 ||
                        multiEquipmentDetails[117] != 15 || multiEquipmentDetails[140] != 17 ||
                        LittleEndian.ReadUInt32(multiEquipmentDetails, 118) != 16879 ||
                        LittleEndian.ReadUInt32(multiEquipmentDetails, 141) != 22078)
                        throw new InvalidOperationException("USERINFO multi-equipment row alignment failed");
                    var avatarDetails = GamePackets.CharacterDetails(1, 0, 0, new[]
                    {
                        new InventoryItemRecord { Slot = 9, ItemId = 27000, Count = 1, Durability = 35 }
                    }, null, null, null, new[]
                    {
                        new InventoryItemRecord { Slot = 0, ItemId = 39000, Count = 0, Durability = 2 }
                    }).Payload;
                    if (avatarDetails.Length != 150 || avatarDetails[93] != 2 || avatarDetails[94] != 0 ||
                        LittleEndian.ReadUInt32(avatarDetails, 95) != 39000 ||
                        LittleEndian.ReadUInt16(avatarDetails, 104) != 2 ||
                        LittleEndian.ReadUInt32(avatarDetails, 117) != 0 ||
                        avatarDetails[121] != 9 || LittleEndian.ReadUInt32(avatarDetails, 122) != 27000 ||
                        avatarDetails[144] != 2 || avatarDetails[149] != 0)
                        throw new InvalidOperationException("USERINFO worn-avatar row layout failed");

                    var levelNineGunnerStats = new CharacterStatCatalog(null).Get(2, 0, 9);
                    var levelNineGunner = GamePackets.CharacterDetails(1, 0, 2, null, null, levelNineGunnerStats).Payload;
                    if (LittleEndian.ReadUInt32(levelNineGunner, 13) != 4800 ||
                        LittleEndian.ReadUInt32(levelNineGunner, 17) != 4000 ||
                        LittleEndian.ReadInt16(levelNineGunner, 21) != 300 ||
                        LittleEndian.ReadInt16(levelNineGunner, 23) != 300 ||
                        LittleEndian.ReadInt16(levelNineGunner, 25) != 290 ||
                        LittleEndian.ReadInt16(levelNineGunner, 27) != 290 ||
                        LittleEndian.ReadUInt32(levelNineGunner, 71) != 354000 ||
                        LittleEndian.ReadUInt32(levelNineGunner, 89) != 600000)
                        throw new InvalidOperationException("?? USERINFO character stat growth failed");

                    var consumeBody = ParseHex("010000010700580003000000");
                    byte consumeListType;
                    ConsumeItemRequestRecord[] consumeEntries;
                    if (!GamePackets.TryReadConsumeItems(consumeBody, out consumeListType, out consumeEntries) ||
                        consumeListType != 0 || consumeEntries.Length != 1 || consumeEntries[0].Operation != 7 ||
                        consumeEntries[0].Slot != 88 || consumeEntries[0].ItemId != 0 || consumeEntries[0].RequestedCount != 3)
                        throw new InvalidOperationException("?? consume-items operation/slot layout failed");
                    var aradConsumeBody = ParseHex("9800000102004900DD0B0000010000002E4A0E000000000000000000000000000000");
                    if (!GamePackets.TryReadConsumeItems(aradConsumeBody, out consumeListType, out consumeEntries) ||
                        consumeListType != 0 || consumeEntries.Length != 1 || consumeEntries[0].Operation != 2 ||
                        consumeEntries[0].Slot != 73 || consumeEntries[0].ItemId != 3037 ||
                        consumeEntries[0].RequestedCount != 1)
                        throw new InvalidOperationException("ARAD consume-items Q16.16 layout failed");
                    var consumableInventory = new Dictionary<ushort, InventoryItemRecord>
                    {
                        [41] = new InventoryItemRecord { Slot = 41, ItemId = 8, Count = 3 }
                    };
                    InventoryItemRecord consumedStack;
                    uint consumedCount;
                    uint remainingCount;
                    if (!GameServer.TryConsumeInventoryItem(consumableInventory,
                            new ConsumeItemRequestRecord { Operation = 2, Slot = 41, ItemId = 8, RequestedCount = 1 },
                            out consumedStack, out consumedCount, out remainingCount) ||
                        consumedCount != 1 || remainingCount != 2 || consumableInventory[41].Count != 2)
                        throw new InvalidOperationException("consume-items inventory decrement failed");
                    if (!GameServer.TryConsumeInventoryItem(consumableInventory,
                            new ConsumeItemRequestRecord { Operation = 2, Slot = 41, ItemId = 8, RequestedCount = 2 },
                            out consumedStack, out consumedCount, out remainingCount) ||
                        consumedCount != 2 || remainingCount != 0 || consumableInventory.ContainsKey(41))
                        throw new InvalidOperationException("Remy's aid final stack was not removed");
                    var consumeReply = GamePackets.ConsumeItemsReply(0, new[]
                    {
                        new InventoryConsumptionRecord { Slot = 88, ConsumedCount = 3 }
                    }).Payload;
                    if (consumeReply.Length != 9 || consumeReply[0] != 1 || consumeReply[2] != 1 ||
                        LittleEndian.ReadUInt16(consumeReply, 3) != 88 || LittleEndian.ReadUInt32(consumeReply, 5) != 3)
                        throw new InvalidOperationException("?? consume-items reply layout failed");
                    var townItemReply = GamePackets.UseTownItemReply(62, 1, 1000, 31).Payload;
                    if (townItemReply.Length != 10 || townItemReply[0] != 1 ||
                        LittleEndian.ReadUInt16(townItemReply, 1) != 62 || townItemReply[3] != 1 ||
                        LittleEndian.ReadUInt32(townItemReply, 4) != 1000 || LittleEndian.ReadUInt16(townItemReply, 8) != 31)
                        throw new InvalidOperationException("?? command 32 town-item reply layout failed");
                    TownConsumableEffect townEffect;
                    if (!TownConsumableCatalog.TryGet(1031, out townEffect) || townEffect.Type != TownConsumableEffectType.SkillPoints || townEffect.Amount != 5 ||
                        !TownConsumableCatalog.TryGet(1037, out townEffect) || townEffect.Type != TownConsumableEffectType.Experience || townEffect.Amount != 100000 ||
                        !TownConsumableCatalog.TryGet(1046, out townEffect) || townEffect.Type != TownConsumableEffectType.AllResistance || townEffect.Amount != 1 ||
                        TownConsumableCatalog.TryGet(3049, out townEffect))
                        throw new InvalidOperationException("?? permanent town consumable catalog failed");
                    TownConsumableCatalog.TryGet(1034, out townEffect);
                    if (TownConsumableCatalog.GetProtocolType(townEffect.Type) != 1 || TownConsumableCatalog.GetWireAmount(townEffect) != 100 ||
                        !TownConsumableCatalog.TryGet(1039, out townEffect) || TownConsumableCatalog.GetProtocolType(townEffect.Type) != 4 ||
                        TownConsumableCatalog.GetWireAmount(townEffect) != 50)
                        throw new InvalidOperationException("?? command 32 town-item effect encoding failed");

                    var permanent = new CharacterRecord
                    {
                        Job = 2, GrowType = 0, Level = 9, BonusStrength = 5, BonusIntelligence = 10,
                        BonusVitality = 15, BonusSpirit = 20, BonusMaximumHp = 25, BonusMaximumMp = 50,
                        BonusMovementSpeed = 1, BonusAllResistance = 2
                    };
                    var permanentStats = new CharacterStatCatalog(null).Get(permanent);
                    if (permanentStats.MaxHp != 5050 || permanentStats.MaxMp != 4500 || permanentStats.Strength != 350 ||
                        permanentStats.Intelligence != 390 || permanentStats.Vitality != 450 || permanentStats.Spirit != 490 ||
                        permanentStats.MoveSpeed != 8210 || permanentStats.FireResistance != 20 || permanentStats.LightResistance != 20)
                        throw new InvalidOperationException("?? permanent attribute-stone stat projection failed");

                    var consumeAccount = store.EnsureAccount("consume-test", "test");
                    var consumeCharacter = store.CreateCharacter(consumeAccount, "道具存档测试", 2);
                    if (!consumeCharacter.Success) throw new InvalidOperationException("consume persistence character creation failed");
                    consumeCharacter.Character.SkillPoints = 525;
                    consumeCharacter.Character.BonusStrength = 5;
                    consumeCharacter.Character.BonusMaximumHp = 25;
                    store.SaveTownConsumableState(consumeCharacter.Character, new[]
                    {
                        new InventoryItemRecord { Slot = 39, ItemId = 1031, Count = 2 }
                    });
                    var persistedConsume = store.ListCharacters(consumeAccount).Single();
                    if (persistedConsume.SkillPoints != 525 || persistedConsume.BonusStrength != 5 || persistedConsume.BonusMaximumHp != 25 ||
                        store.LoadInventory(persistedConsume.Id).Single(value => value.Slot == 39).Count != 2)
                        throw new InvalidOperationException("?? permanent town consumable transaction persistence failed");
                    store.SaveInventory(persistedConsume.Id, new[]
                    {
                        new InventoryItemRecord { Slot = 3, ItemId = 8, Count = 4 },
                        new InventoryItemRecord { Slot = 4, ItemId = 3037, Count = 2 },
                        new InventoryItemRecord { Slot = 5, ItemId = 10050, Count = 1, Durability = 35 }
                    });
                    var persistedQuickBar = store.LoadInventory(persistedConsume.Id);
                    if (persistedQuickBar.Count != 3 ||
                        !persistedQuickBar.Any(value => value.Slot == 3 && value.ItemId == 8 && value.Count == 4) ||
                        !persistedQuickBar.Any(value => value.Slot == 4 && value.ItemId == 3037 && value.Count == 2) ||
                        !persistedQuickBar.Any(value => value.Slot == 5 && value.ItemId == 10050 && value.Count == 1))
                        throw new InvalidOperationException("multi-category quick-bar database persistence failed");
                    var persistedConsumableInventory = persistedQuickBar.ToDictionary(value => value.Slot);
                    if (!GameServer.TryConsumeInventoryItem(persistedConsumableInventory,
                            new ConsumeItemRequestRecord
                            {
                                Operation = 2, Slot = 3, ItemId = 8, RequestedCount = 1
                            }, out consumedStack, out consumedCount, out remainingCount) ||
                        remainingCount != 3)
                        throw new InvalidOperationException("dungeon consumable transaction failed");
                    store.SaveInventory(persistedConsume.Id, persistedConsumableInventory.Values);
                    var persistedRemy = store.LoadInventory(persistedConsume.Id)
                        .SingleOrDefault(value => value.Slot == 3 && value.ItemId == 8);
                    if (persistedRemy == null || persistedRemy.Count != 3)
                        throw new InvalidOperationException("dungeon consumable decrement was not persisted");

                    var matchingPvf = Path.Combine(Environment.CurrentDirectory, "data", "Script.pvf");
                    if (File.Exists(matchingPvf))
                    {
                        var pvfDungeons = new DungeonCatalog(new PvfArchiveReader(matchingPvf));
                        var dungeonDefinitions = pvfDungeons.Definitions.ToArray();
                        var allPermissions = GameServer.BuildEffectiveDungeonPermissions(pvfDungeons,
                            new[] { new KeyValuePair<ushort, byte>(ushort.MaxValue, 2) });
                        if (dungeonDefinitions.Length == 0 ||
                            dungeonDefinitions.Any(definition => !allPermissions.Any(permissionRow =>
                                permissionRow.Key == definition.Key && permissionRow.Value == 3)) ||
                            !allPermissions.Any(permissionRow => permissionRow.Key == ushort.MaxValue && permissionRow.Value == 2) ||
                            allPermissions.Length != dungeonDefinitions.Length + 1)
                            throw new InvalidOperationException("all PVF dungeon permissions were not exposed");

                        var pvfStats = new CharacterStatCatalog(new PvfArchiveReader(matchingPvf)).Get(2, 0, 9);
                        if (pvfStats.MaxHp != 4800 || pvfStats.MaxMp != 4000 ||
                            pvfStats.Strength != 300 || pvfStats.Vitality != 300 ||
                            pvfStats.Intelligence != 290 || pvfStats.Spirit != 290 ||
                            pvfStats.InventoryLimit != 354000 || pvfStats.MoveSpeed != 8200 ||
                            pvfStats.AttackSpeed != 9500 || pvfStats.Weight != 600000)
                            throw new InvalidOperationException("?? PVF character stat parsing failed");

                        var pvfItems = new ItemCategoryCatalog(new PvfArchiveReader(matchingPvf));
                        var quickBarItems = new Dictionary<ushort, InventoryItemRecord>
                        {
                            [3] = new InventoryItemRecord { Slot = 3, ItemId = 8, Count = 4 },
                            [4] = new InventoryItemRecord { Slot = 4, ItemId = 3037, Count = 2 },
                            [5] = new InventoryItemRecord { Slot = 5, ItemId = 10050, Count = 1, Durability = 35 }
                        };
                        if (pvfItems.RetabInventory(quickBarItems) != 0 ||
                            !quickBarItems.ContainsKey(3) || quickBarItems[3].ItemId != 8 ||
                            !quickBarItems.ContainsKey(4) || quickBarItems[4].ItemId != 3037 ||
                            !quickBarItems.ContainsKey(5) || quickBarItems[5].ItemId != 10050)
                            throw new InvalidOperationException("multi-category quick-bar persistence normalization failed");
                        if (pvfItems.Count != pvfItems.EquipmentCount + pvfItems.StackableCount ||
                            pvfItems.EquipmentCount == 0 || pvfItems.StackableCount == 0 ||
                            pvfItems.UnclassifiedStackableCount != 0 ||
                            pvfItems.ConsumableCount + pvfItems.MaterialCount + pvfItems.QuestCount !=
                                pvfItems.StackableCount ||
                            pvfItems.GetCategory(8) != InventoryItemCategory.Consumable ||
                            pvfItems.GetCategory(5001) != InventoryItemCategory.Consumable ||
                            pvfItems.GetCategory(7000) != InventoryItemCategory.Consumable ||
                            pvfItems.GetCategory(2000) != InventoryItemCategory.Material ||
                            pvfItems.GetCategory(3072) != InventoryItemCategory.Quest ||
                            pvfItems.GetCategory(10050) != InventoryItemCategory.Equipment)
                            throw new InvalidOperationException("?? PVF full item category audit failed");
                    }

                    var expectedInitialSkills = new[]
                    {
                        new int[] { 0,5,1, 1,46,1, 174,181,2, 175,182,2, 176,184,1, 177,173,1, 178,179,5 },
                        new int[] { 0,5,1, 1,46,1, 174,181,3, 175,182,3, 176,184,1, 177,172,1, 178,179,5 },
                        new int[] { 0,4,1, 1,47,1, 174,181,2, 175,184,1, 176,185,1, 177,179,5 },
                        new int[] { 0,11,1, 1,12,1, 174,181,3, 175,182,3, 176,184,2, 177,187,1, 178,179,5 },
                        new int[] { 0,1,1, 1,51,1, 174,181,1, 175,182,1, 176,184,1, 177,179,5, 178,173,1 }
                    };
                    for (byte job = 0; job < expectedInitialSkills.Length; job++)
                    {
                        var expected = expectedInitialSkills[job];
                        var expectedCount = expected.Length / 3;
                        var jobDetails = GamePackets.CharacterDetails(1, 0, job).Payload;
                        var skillInfo = GamePackets.TownInitialization(job)
                            .Single(value => value.ProtocolId == GamePackets.SkillInfo).Payload;
                        const int characterDetailSkillCount = 2;
                        const int characterDetailSkillCountOffset = 117;
                        const int characterDetailSkillRowsOffset = 118;
                        if (jobDetails.Length != 119 + characterDetailSkillCount * 2 ||
                            jobDetails[characterDetailSkillCountOffset] != characterDetailSkillCount ||
                            skillInfo.Length != 4 + expectedCount * 3 || LittleEndian.ReadUInt16(skillInfo, 0) != 0 ||
                            skillInfo[2] != expectedCount || skillInfo[3 + expectedCount * 3] != 0)
                            throw new InvalidOperationException("?? 职业 " + job + " 初始技能布局失败: detailsLen=" +
                                jobDetails.Length + ", detailsCount=" + jobDetails[characterDetailSkillCountOffset] + ", skillInfoLen=" +
                                skillInfo.Length + ", skillCount=" + skillInfo[2]);
                        for (var index = 0; index < expectedCount; index++)
                        {
                            var expectedSlot = expected[index * 3];
                            var expectedId = expected[index * 3 + 1];
                            var expectedLevel = expected[index * 3 + 2];
                            var skillOffset = 3 + index * 3;
                            var expectedWireSlot = expectedSlot;
                            if ((index < characterDetailSkillCount &&
                                    (jobDetails[characterDetailSkillRowsOffset + index * 2] != expectedId ||
                                     jobDetails[characterDetailSkillRowsOffset + index * 2 + 1] != expectedLevel)) ||
                                skillInfo[skillOffset] != expectedWireSlot || skillInfo[skillOffset + 1] != expectedId ||
                                skillInfo[skillOffset + 2] != expectedLevel)
                                throw new InvalidOperationException("?? 职业 " + job + " 初始主动/被动技能明细失败");
                        }
                    }

                    // 物品列表：单 listType=0，金币 + 雷米（slot 12 背包格子）。
                    // 客户端根据物品类型自动分类到消耗品页签底部网格。
                    // Old characters can still have default common skills saved in
                    // the former 102+ range. SKILL_INFO must normalize the slot to
                    // the Arad client's common tab while preserving the saved level.
                    var legacySkillInfo = GamePackets.TownInitialization(2, 500, new[]
                    {
                        new CharacterSkillRecord { Slot = 102, SkillId = 181, Level = 4 }
                    }).Single(value => value.ProtocolId == GamePackets.SkillInfo).Payload;
                    var legacySkillOffset = Enumerable.Range(0, legacySkillInfo[2])
                        .Select(index => 3 + index * 3)
                        .Single(offset => legacySkillInfo[offset + 1] == 181);
                    if (legacySkillInfo[legacySkillOffset] != 174 || legacySkillInfo[legacySkillOffset + 2] != 4)
                        throw new InvalidOperationException("?? 旧角色通用技能槽位归一化失败");

                    var itemLists = GamePackets.TownInitialization()
                        .Where(value => value.ProtocolId == GamePackets.ItemList)
                        .Select(value => value.Payload).ToList();
                    if (itemLists.Count != 1)
                        throw new InvalidOperationException("?? ItemList 应发送 1 个列表（55 风格）");
                    var equipList = itemLists[0];
                    if (equipList.Length != 3 || equipList[0] != 0 || LittleEndian.ReadUInt16(equipList, 1) != 0)
                        throw new InvalidOperationException("?? ItemList（金币+雷米首个背包格 slot9）布局失败");
                    var persistedItemList = GameServer.BuildItemListPacket(
                        new Dictionary<ushort, InventoryItemRecord>
                        {
                            { 9, new InventoryItemRecord { Slot = 9, ItemId = 27000, Count = 1, Durability = 35 } }
                        }, 99999).Single().Payload;
                    if (persistedItemList.Length != 45 || persistedItemList[0] != 0 ||
                        LittleEndian.ReadUInt16(persistedItemList, 1) != 2 ||
                        LittleEndian.ReadUInt16(persistedItemList, 3) != 0 ||
                        LittleEndian.ReadUInt32(persistedItemList, 5) != 0 ||
                        LittleEndian.ReadUInt32(persistedItemList, 9) != 99999 ||
                        LittleEndian.ReadUInt16(persistedItemList, 24) != 9 ||
                        LittleEndian.ReadUInt32(persistedItemList, 26) != 27000 ||
                        LittleEndian.ReadUInt32(persistedItemList, 30) != 1)
                        throw new InvalidOperationException("Persisted ItemList must use 21-byte ARAD rows with a currency row");
                    var acceptableQuests = GamePackets.AcceptableQuests(new ushort[] { 1016 }).Payload;
                    if (acceptableQuests.Length != 3 || acceptableQuests[0] != 1 ||
                        LittleEndian.ReadUInt16(acceptableQuests, 1) != 1016)
                        throw new InvalidOperationException("?? 可接受任务列表布局失败");
                    var offerCatalog = new QuestCatalog(new[]
                    {
                        QuestCatalog.ParseDefinition(1, "quest/intro.qst", "[grade] `[epic]`\n[level] 1 99\n[type] `[clear map]`\n"),
                        QuestCatalog.ParseDefinition(2, "quest/next.qst", "[grade] `[epic]`\n[level] 1 99\n[type] `[clear map]`\n")
                    });
                    var offerCharacter = new CharacterRecord { Level = 1, Job = 0, GrowType = 0 };
                    var offerActive = new[] { new CharacterQuestRecord { QuestId = 1, Trigger = 1 } };
                    var offerIds = offerCatalog.GetAcceptableQuestIds(offerCharacter, offerActive, new HashSet<ushort>());
                    if (Array.IndexOf(offerIds, (ushort)1) >= 0 || Array.IndexOf(offerIds, (ushort)2) < 0 ||
                        !offerCatalog.CanAcceptQuest(offerCharacter, 2, offerActive, new HashSet<ushort>()))
                        throw new InvalidOperationException("进行中任务不应出现在可接任务通知中");
                    var goldSeekingQuest = QuestCatalog.ParseDefinition(1002, "quest/linus/2.qst",
                        "[type] `[seeking]`\n[int data]\n0 200\n[/int data]\n" +
                        "[reward int data]\n1000 2\n[/reward int data]\n[reward type] `[item]`\n");
                    if (QuestCatalog.IsSeekingConditionSatisfied(goldSeekingQuest, new InventoryItemRecord[0], 199) ||
                        !QuestCatalog.IsSeekingConditionSatisfied(goldSeekingQuest, new InventoryItemRecord[0], 200) ||
                        QuestCatalog.GetSeekingGoldRequirement(goldSeekingQuest) != 200 ||
                        goldSeekingQuest.RewardRows.Length != 1 || goldSeekingQuest.RewardRows[0][0] != 1000 ||
                        goldSeekingQuest.RewardRows[0][1] != 2)
                        throw new InvalidOperationException("健忘症任务金币条件/草莓奖励解析失败");
                    var decisionQuest = QuestCatalog.ParseDefinition(4564, "quest/epic/scasaepic3.qst",
                        "[depend give item]\n4291 1\n[/depend give item]\n" +
                        "[type] `[seeking]`\n[int data]\n4291 1\n[/int data]\n");
                    var decisionAcceptItems = QuestCatalog.GetAcceptItemGrants(decisionQuest);
                    if (decisionAcceptItems.Count != 1 || decisionAcceptItems[0].ItemId != 4291 ||
                        decisionAcceptItems[0].Count != 1)
                        throw new InvalidOperationException("Quest accept item parsing failed");
                    if (QuestCatalog.GetInitialTrigger(decisionQuest, new[]
                        { new InventoryItemRecord { ItemId = 4291, Count = 1 } }, 0) != 0)
                        throw new InvalidOperationException("Quest accept item must satisfy its seeking condition");
                    var liveQuestCatalog = new QuestCatalog(new PvfArchiveReader(GetSelfTestScriptPvfPath()));
                    QuestDefinition liveDecisionQuest;
                    var liveDecisionAcceptItems = liveQuestCatalog.TryGet(4564, out liveDecisionQuest)
                        ? QuestCatalog.GetAcceptItemGrants(liveDecisionQuest) : new QuestItemDrop[0];
                    if (liveDecisionAcceptItems.Count != 1 || liveDecisionAcceptItems[0].ItemId != 4291 ||
                        liveDecisionAcceptItems[0].Count != 1)
                        throw new InvalidOperationException("Script.pvf decision quest must grant item 4291 when accepted");
                    var huntQuestDefinition = QuestCatalog.ParseDefinition(103, "quest/epic/new-3.qst",
                        "[type] `[hunt monster]`\n[int data]\n2 -1 1 12\n[/int data]\n");
                    if (QuestCatalog.GetInitialTrigger(huntQuestDefinition, new InventoryItemRecord[0], 0) != 12 ||
                        !QuestCatalog.MatchesDungeonClear(huntQuestDefinition, 2) ||
                        !QuestCatalog.MatchesMonsterKill(huntQuestDefinition, 2, 0, 1) ||
                        QuestCatalog.MatchesMonsterKill(huntQuestDefinition, 2, 0, 2))
                        throw new InvalidOperationException("杀怪任务初始剩余数量解析失败");
                    var seriaRingQuest = QuestCatalog.ParseDefinition(1014, "quest/seria/4.qst",
                        "[clear reward item]\n2 -1 3087 1 100 1\n[/clear reward item]\n" +
                        "[type] `[seeking]`\n[int data]\n3087 1\n[/int data]\n");
                    var seriaDrops = QuestDropPlanner.Plan(seriaRingQuest, 2, 0,
                        new InventoryItemRecord[0], chance => true);
                    var cappedDrops = QuestDropPlanner.Plan(seriaRingQuest, 2, 0,
                        new[] { new InventoryItemRecord { ItemId = 3087, Count = 1 } }, chance => true);
                    if (seriaRingQuest.ClearRewardItems.Length != 1 ||
                        seriaRingQuest.ClearRewardItems[0].ItemId != 3087 ||
                        seriaRingQuest.ClearRewardItems[0].MaximumCount != 1 ||
                        seriaDrops.Count != 1 || seriaDrops[0].ItemId != 3087 || seriaDrops[0].Count != 1 ||
                        cappedDrops.Count != 0 || QuestDropPlanner.Plan(seriaRingQuest, 1, 0,
                            new InventoryItemRecord[0], chance => true).Count != 0)
                        throw new InvalidOperationException("Seria ring quest drop planning failed");
                    var monsterRewardQuest = QuestCatalog.ParseDefinition(1015, "quest/seria/5.qst",
                        "[monster reward item]\n1 2 -1 3090 2 100 3\n[/monster reward item]\n");
                    var monsterQuestDrops = QuestDropPlanner.PlanMonster(monsterRewardQuest, 2, 0, 1,
                        new[] { new InventoryItemRecord { ItemId = 3090, Count = 2 } }, chance => true);
                    if (monsterRewardQuest.MonsterRewardItems.Length != 1 ||
                        monsterQuestDrops.Count != 1 || monsterQuestDrops[0].ItemId != 3090 ||
                        monsterQuestDrops[0].Count != 1 ||
                        QuestDropPlanner.PlanMonster(monsterRewardQuest, 2, 0, 2,
                            new InventoryItemRecord[0], chance => true).Count != 0)
                        throw new InvalidOperationException("monster reward quest drop planning failed");
                    var acceptQuest = GamePackets.AcceptQuestReply(1000, 2);
                    var giveupQuest = GamePackets.GiveupQuestReply(1000);
                    var triggerQuest = GamePackets.SetQuestTriggerReply(1000, 3);
                    var finishQuest = GamePackets.FinishQuestReply(1000);
                    if (acceptQuest.Payload.Length != 8 || acceptQuest.Payload[0] != 1 ||
                        LittleEndian.ReadUInt16(acceptQuest.Payload, 1) != 1000 ||
                        LittleEndian.ReadUInt32(acceptQuest.Payload, 3) != 2 || acceptQuest.Payload[7] != 0 ||
                        giveupQuest.Payload.Length != 3 || triggerQuest.Payload.Length != 7 ||
                        LittleEndian.ReadUInt16(triggerQuest.Payload, 1) != 1000 ||
                        LittleEndian.ReadUInt32(triggerQuest.Payload, 3) != 3 ||
                        finishQuest.Payload.Length != 7)
                        throw new InvalidOperationException("?? 任务接受/放弃/进度/完成响应布局失败");
                    var moveReply = GamePackets.MoveItemSpaceReply(0, 9, 0, 0, 12);
                    if (moveReply.Type != GamePackets.Command || moveReply.ProtocolId != GamePackets.MoveItemSpace ||
                        moveReply.Payload.Length != 11 || moveReply.Payload[0] != 1 || moveReply.Payload[1] != 0 ||
                        LittleEndian.ReadUInt16(moveReply.Payload, 2) != 9 || LittleEndian.ReadUInt32(moveReply.Payload, 4) != 0 ||
                        moveReply.Payload[8] != 0 || LittleEndian.ReadUInt16(moveReply.Payload, 9) != 12)
                        throw new InvalidOperationException("?? MoveItemSpace 响应布局失败");
                    var moveError = GamePackets.MoveItemSpaceError(4, 1, 3);
                    if (!moveError.Payload.SequenceEqual(new byte[] { 0, 4, 1, 3 }))
                        throw new InvalidOperationException("MoveItemSpace failure layout must include both list identifiers");
                    var storageList = GamePackets.StorageItemList(new[]
                    {
                        new InventoryItemRecord { Slot = 119, ItemId = 8, Count = 12, State = 1, Durability = 2, SealState = 3 }
                    });
                    if (storageList.Type != GamePackets.Notification || storageList.ProtocolId != GamePackets.ItemList ||
                        storageList.Payload.Length != 26 || storageList.Payload[0] != 2 ||
                        LittleEndian.ReadUInt16(storageList.Payload, 1) != 120 ||
                        LittleEndian.ReadUInt16(storageList.Payload, 3) != 1 ||
                        LittleEndian.ReadUInt16(storageList.Payload, 5) != 119 ||
                        LittleEndian.ReadUInt32(storageList.Payload, 7) != 8 ||
                        LittleEndian.ReadUInt32(storageList.Payload, 11) != 12)
                        throw new InvalidOperationException("?? maximum storage item-list layout failed");
                    var updateItem = GamePackets.UpdateItem(3, 9, new InventoryItemRecord
                    {
                        Slot = 9, ItemId = 31001, Count = 1, Durability = 35
                    });
                    if (updateItem.ProtocolId != GamePackets.UpdateItemList || updateItem.Payload.Length != 24 ||
                        updateItem.Payload[0] != 3 || LittleEndian.ReadUInt16(updateItem.Payload, 1) != 1 ||
                        LittleEndian.ReadUInt16(updateItem.Payload, 3) != 9 || LittleEndian.ReadUInt32(updateItem.Payload, 5) != 31001)
                        throw new InvalidOperationException("?? UpdateItemList 增量装备布局失败");
                    var avatarUpdateItem = GamePackets.UpdateItem(1, 0, new InventoryItemRecord
                    {
                        Slot = 9, ItemId = 39000, Count = 0
                    });
                    if (avatarUpdateItem.Payload.Length != 28 ||
                        LittleEndian.ReadUInt32(avatarUpdateItem.Payload, 24) != 0)
                        throw new InvalidOperationException("avatar UpdateItemList must end with an empty DSTR");
                    var clearItem = GamePackets.UpdateItem(0, 9, null);
                    if (clearItem.Payload.Length != 24 || LittleEndian.ReadUInt32(clearItem.Payload, 5) != uint.MaxValue)
                        throw new InvalidOperationException("UpdateItemList empty slot must use 0xFFFF, not gold itemId 0");
                    var equipmentPaths = EquipmentCatalog.ParseListEntries(
                        "10400 `character/common/jacket/leather/vest_crleather.equ`\n" +
                        "39000 `character/swordman/avatar/cap/acap_hgear.equ`\n");
                    if (!equipmentPaths.ContainsKey(10400) ||
                        equipmentPaths[10400] != "equipment/character/common/jacket/leather/vest_crleather.equ" ||
                        equipmentPaths[39000] != "equipment/character/swordman/avatar/cap/acap_hgear.equ" ||
                        GamePackets.InitialAvatarIds(2).Count != 9 || GamePackets.InitialAvatarIds(2)[0] != 47000 ||
                        GamePackets.InitialAvatarIds(2)[8] != 50200 ||
                        EquipmentCatalog.ParseMaximumDurability("[durability] 33\n") != 33 ||
                        EquipmentCatalog.ParseAvatarOptionCount(
                            "[avatar select ability]\n`[a]` 1 2\n`[b]` 3 4 // x\n[/avatar select ability]\n") != 2)
                        throw new InvalidOperationException("?? PVF 装备耐久解析失败");

                    if (GameServer.AvatarBagDbToWire(9) != 0 || GameServer.AvatarBagDbToWire(254) != 245 ||
                        GameServer.AvatarBagDbToWire(8) != ushort.MaxValue || GameServer.AvatarBagWireToDb(0) != 9 ||
                        GameServer.AvatarBagWireToDb(245) != 254 || GameServer.AvatarBagWireToDb(246) != ushort.MaxValue ||
                        GameServer.ResolveAvatarContainerSlot(1, 7) != 16 ||
                        GameServer.ResolveAvatarContainerSlot(3, 7) != 7)
                        throw new InvalidOperationException("avatar bag wire/database slot mapping failed");
                    var avatarContainers = new Dictionary<ushort, InventoryItemRecord>
                    {
                        [0] = new InventoryItemRecord { Slot = 0, ItemId = 39000, Count = 0 },
                        [9] = new InventoryItemRecord { Slot = 9, ItemId = 39400, Count = 0, Durability = 2 },
                        [254] = new InventoryItemRecord { Slot = 254, ItemId = 39800, Count = 0 }
                    };
                    var avatarList = GameServer.BuildAvatarListPacket(avatarContainers);
                    if (avatarList.ProtocolId != GamePackets.ItemList || avatarList.Payload[0] != 1 ||
                        LittleEndian.ReadUInt16(avatarList.Payload, 1) != 2 || avatarList.Payload.Length != 53 ||
                        LittleEndian.ReadUInt16(avatarList.Payload, 3) != 0 ||
                        LittleEndian.ReadUInt32(avatarList.Payload, 5) != 39400 ||
                        LittleEndian.ReadUInt32(avatarList.Payload, 24) != 0 ||
                        LittleEndian.ReadUInt16(avatarList.Payload, 28) != 245 ||
                        LittleEndian.ReadUInt32(avatarList.Payload, 30) != 39800 ||
                        LittleEndian.ReadUInt32(avatarList.Payload, 49) != 0)
                        throw new InvalidOperationException("avatar inventory wire-list mapping failed");
                    var normalizedAvatars = new Dictionary<ushort, InventoryItemRecord>();
                    if (!GameServer.NormalizeAvatarSlots(null, new[]
                        {
                            new InventoryItemRecord { Slot = 9, ItemId = 39400, Count = 123 },
                            new InventoryItemRecord { Slot = 0, ItemId = 39000, Count = 0 }
                        }, normalizedAvatars) || normalizedAvatars.Count != 2 ||
                        normalizedAvatars[9].Count != 0 || normalizedAvatars[10].ItemId != 39000)
                        throw new InvalidOperationException("avatar persistence slot normalization failed");
                    var liveEquipmentCatalog = new EquipmentCatalog(new PvfArchiveReader(GetSelfTestScriptPvfPath()));
                    if (liveEquipmentCatalog.GetAvatarSlot(39000) != 0 ||
                        liveEquipmentCatalog.GetAvatarSlot(39400) != 1)
                        throw new InvalidOperationException("matching-PVF avatar wear-slot lookup failed");
                    normalizedAvatars.Clear();
                    if (GameServer.NormalizeAvatarSlots(liveEquipmentCatalog, new[]
                        {
                            new InventoryItemRecord { Slot = 0, ItemId = 39000, Count = 0 },
                            new InventoryItemRecord { Slot = 9, ItemId = 39400, Count = 0 }
                        }, normalizedAvatars) || !normalizedAvatars.ContainsKey(0) ||
                        !normalizedAvatars.ContainsKey(9))
                        throw new InvalidOperationException("valid worn-avatar slots were not preserved");

                    var stackablePaths = ItemCategoryCatalog.ParseStackableListEntries(
                        "8 `cash/hands_remy.stk`\n3087 `material/seria_ring.stk`\n");
                    if (stackablePaths[8] != "stackable/cash/hands_remy.stk" ||
                        ItemCategoryCatalog.ParseStackableType("[stackable type] `[quest]` 0") != InventoryItemCategory.Quest ||
                        ItemCategoryCatalog.ParseStackableType("[stackable type] `[waste]` 0") != InventoryItemCategory.Consumable ||
                        ItemCategoryCatalog.ParseStackableType("[stackable type] `[material]` 0") != InventoryItemCategory.Material ||
                        ItemCategoryCatalog.ParseStackableType("[stackable type] `[recipe]` 0") != InventoryItemCategory.Consumable ||
                        ItemCategoryCatalog.ParseStackableType("[stackable type] `[legacy]` 0") != InventoryItemCategory.Consumable ||
                        ItemCategoryCatalog.ParseStackableType("[stackable type] `[upgradable legacy]` 0") != InventoryItemCategory.Consumable ||
                        ItemCategoryCatalog.ParseStackableType("[stackable type] `[etc]` 0") != InventoryItemCategory.Consumable ||
                        ItemCategoryCatalog.ParseStackableType("[stackable type] `[set]` 0") != InventoryItemCategory.Consumable)
                        throw new InvalidOperationException("?? PVF item category parsing failed");
                    var occupiedMainSlots = new Dictionary<ushort, InventoryItemRecord>
                    {
                        [9] = new InventoryItemRecord { Slot = 9, ItemId = 10050 },
                        [10] = new InventoryItemRecord { Slot = 10, ItemId = 8 },
                        [39] = new InventoryItemRecord { Slot = 39, ItemId = 2000 },
                        [69] = new InventoryItemRecord { Slot = 69, ItemId = 3072 },
                        [99] = new InventoryItemRecord { Slot = 99, ItemId = 5001 }
                    };
                    if (new ItemCategoryCatalog(null).FindFreeSlot(occupiedMainSlots, 5001) != 11)
                        throw new InvalidOperationException("?? shared main inventory slot allocation failed");

                    var enableClear = GamePackets.EnableClearDungeon();
                    if (enableClear.Type != GamePackets.Notification || enableClear.ProtocolId != GamePackets.EnableClearDungeonNotification ||
                        enableClear.Payload.Length != 0)
                        throw new InvalidOperationException("?? EnableClearDungeon 通知布局失败");

                    var playResult = GamePackets.PlayResult(55);
                    if (playResult.Type != GamePackets.Notification || playResult.ProtocolId != GamePackets.PlayResultNotification ||
                        playResult.Payload.Length != 5 || playResult.Payload[0] != 55 ||
                        playResult.Payload.Skip(1).Any(value => value != 0))
                        throw new InvalidOperationException("?? PlayResult(34) 五字节布局失败");
                    var clearReward = GamePackets.ClearDungeonReward(1, 1234,
                        new uint[] { 100, 110, 120, 130 },
                        new[] { new DungeonCardReward { ItemId = 1004, Count = 2 },
                                new DungeonCardReward { ItemId = 1047, Count = 3 },
                                new DungeonCardReward { ItemId = 8,   Count = 1 },
                                new DungeonCardReward { ItemId = 1000, Count = 5 } },
                        150);
                    if (clearReward.Type != GamePackets.Notification || clearReward.ProtocolId != GamePackets.ClearDungeonRewardNotification ||
                        clearReward.Payload.Length != 102 || clearReward.Payload[0] != 1 ||
                        LittleEndian.ReadUInt32(clearReward.Payload, 1) != 1234 || clearReward.Payload[37] != 0 ||
                        clearReward.Payload[38] != 1 || LittleEndian.ReadUInt16(clearReward.Payload, 39) != 0 ||
                        LittleEndian.ReadUInt32(clearReward.Payload, 41) != 100 ||
                        clearReward.Payload[59] != 1 || LittleEndian.ReadUInt32(clearReward.Payload, 62) != 130 ||
                        LittleEndian.ReadUInt32(clearReward.Payload, 66) != 150 ||
                        // golden-card list 0
                        clearReward.Payload[70] != 1 || LittleEndian.ReadUInt16(clearReward.Payload, 71) != 1004 ||
                        LittleEndian.ReadUInt32(clearReward.Payload, 73) != 2 ||
                        // golden-card list 1
                        clearReward.Payload[77] != 1 || LittleEndian.ReadUInt16(clearReward.Payload, 78) != 1047 ||
                        LittleEndian.ReadUInt32(clearReward.Payload, 80) != 3 ||
                        // golden-card list 2
                        clearReward.Payload[84] != 1 || LittleEndian.ReadUInt16(clearReward.Payload, 85) != 8 ||
                        LittleEndian.ReadUInt32(clearReward.Payload, 87) != 1 ||
                        // golden-card list 3
                        clearReward.Payload[91] != 1 || LittleEndian.ReadUInt16(clearReward.Payload, 92) != 1000 ||
                        LittleEndian.ReadUInt32(clearReward.Payload, 94) != 5 ||
                        // membership-only lists stay empty
                        clearReward.Payload.Skip(98).Any(value => value != 0))
                        throw new InvalidOperationException("?? ClearDungeonReward(35) 完整奖励布局失败");

                    var dungeonItemReply = GamePackets.DungeonItemActionReply(3, 0);
                    if (dungeonItemReply.Type != GamePackets.Command || dungeonItemReply.ProtocolId != GamePackets.DungeonItemAction ||
                        dungeonItemReply.Payload.Length != 4 || dungeonItemReply.Payload[0] != 1 ||
                        LittleEndian.ReadUInt16(dungeonItemReply.Payload, 1) != 3 || dungeonItemReply.Payload[3] != 0)
                        throw new InvalidOperationException("?? DungeonItemAction(47) ACK 布局失败");
                    var buySkill = GamePackets.BuySkillReply(9989, 2, 9, 1);
                    if (buySkill.ProtocolId != GamePackets.BuySkill || buySkill.Payload.Length != 6 ||
                        buySkill.Payload[0] != 1 || LittleEndian.ReadUInt16(buySkill.Payload, 1) != 9989 ||
                        buySkill.Payload[3] != 2 || buySkill.Payload[4] != 9 || buySkill.Payload[5] != 1)
                        throw new InvalidOperationException("?? BuySkill(31) 响应布局失败");
                    var buySkillFailure = GamePackets.BuySkillFailureReply();
                    if (buySkillFailure.ProtocolId != GamePackets.BuySkill || buySkillFailure.Payload.Length != 1 ||
                        buySkillFailure.Payload[0] != 0)
                        throw new InvalidOperationException("?? BuySkill(31) 失败响应布局失败");
                    if (SkillCatalog.ParsePurchaseCost("[purchase cost]\r\n35\r\n[/purchase cost]") != 35 ||
                        SkillCatalog.ParsePurchaseCost("[purchase cost] 20") != 20)
                        throw new InvalidOperationException("技能 PVF SP 消耗解析失败");
                    SkillDefinitionCatalogSelfTest();
                    var movedSkills = new List<CharacterSkillRecord>
                    {
                        new CharacterSkillRecord { Slot = 0, SkillId = 4, Level = 1 },
                        new CharacterSkillRecord { Slot = 6, SkillId = 3, Level = 1 }
                    };
                    if (!GameServer.TryMoveSkillSlot(movedSkills, 6, 0) ||
                        movedSkills.Single(value => value.SkillId == 3).Slot != 0 ||
                        movedSkills.Single(value => value.SkillId == 4).Slot != 6)
                        throw new InvalidOperationException("技能快捷栏交换持久化逻辑失败");

                    // 结算翻牌流程（55 协议）：Command 72/73/74/75 布局。
                    var removedQuickSlotSkills = new List<CharacterSkillRecord>
                    {
                        new CharacterSkillRecord { Slot = 2, SkillId = 7, Level = 1 },
                        new CharacterSkillRecord { Slot = 6, SkillId = 8, Level = 1 }
                    };
                    if (!GameServer.TryRemoveSkillQuickSlot(removedQuickSlotSkills, 2) ||
                        removedQuickSlotSkills.Single(value => value.SkillId == 7).Slot != 7)
                        throw new InvalidOperationException("技能快捷栏移出后的槽位未正确持久化");

                    var scoreScroll = GamePackets.ScoreScrollStateReply();
                    if (scoreScroll.Type != GamePackets.Command || scoreScroll.ProtocolId != GamePackets.ScoreScrollState ||
                        scoreScroll.Payload.Length != 1 || scoreScroll.Payload[0] != 1)
                        throw new InvalidOperationException("?? ScoreScroll(72) 响应布局失败");
                    var cardRight = GamePackets.CardSelectRightState();
                    if (cardRight.Type != GamePackets.Command || cardRight.ProtocolId != 73 ||
                        cardRight.Payload.Length != 9 || cardRight.Payload[0] != 1 ||
                        Enumerable.Range(0, 4).Any(index => LittleEndian.ReadUInt16(cardRight.Payload, 1 + index * 2) != 1))
                        throw new InvalidOperationException("?? CardSelectRight(73) 响应布局失败");
                    var selectCard = GamePackets.SelectCardReply(1, 2, byte.MaxValue);
                    if (selectCard.Type != GamePackets.Command || selectCard.ProtocolId != GamePackets.SelectCard ||
                        selectCard.Payload.Length != 13 || selectCard.Payload[0] != 1 ||
                        selectCard.Payload[1] != byte.MaxValue || selectCard.Payload[2] != byte.MaxValue || selectCard.Payload[3] != 0 ||
                        selectCard.Payload[7] != 1 || selectCard.Payload[8] != byte.MaxValue || selectCard.Payload[9] != 0 ||
                        selectCard.Payload[10] != byte.MaxValue || selectCard.Payload[11] != byte.MaxValue || selectCard.Payload[12] != 0)
                        throw new InvalidOperationException("?? SelectCard(74) 响应布局失败");
                    var selectPaidCard = GamePackets.SelectCardReply(1, 2, 3, 1004, 2);
                    if (selectPaidCard.Payload.Length != 13 || selectPaidCard.Payload[11] != 1 ||
                        selectPaidCard.Payload[12] != 0)
                        throw new InvalidOperationException("?? 黄金卡奖励响应布局失败");
                    var eplp = GamePackets.EplpReply(1, 0);
                    if (eplp.Type != GamePackets.Command || eplp.ProtocolId != GamePackets.Eplp ||
                        eplp.Payload.Length != 3 || eplp.Payload[0] != 1 || eplp.Payload[1] != 1 || eplp.Payload[2] != 0)
                        throw new InvalidOperationException("?? Eplp(75) 响应布局失败");

                    // 洛兰（dungeon 1）副本包：DUNGEON_INFO + START_MAP。
                    var lorienInfo = GamePackets.LorienDungeonInfo(0);
                    if (lorienInfo.ProtocolId != GamePackets.DungeonInfo ||
                        lorienInfo.Payload.Length != 8 || LittleEndian.ReadUInt16(lorienInfo.Payload, 0) != 1 ||
                        lorienInfo.Payload[4] != 2 || lorienInfo.Payload[5] != 1)
                        throw new InvalidOperationException("?? 洛兰 DUNGEON_INFO 布局失败");
                    // Keep the real high-id regression used by latest_ref_server:
                    // Vilmark is dungeon 1500, whose little-endian wire id is DC 05.
                    var vilmarkInfo = GamePackets.LorienDungeonInfo(0, 1500, 5, 0);
                    if (vilmarkInfo.Payload.Length != 8 ||
                        LittleEndian.ReadUInt16(vilmarkInfo.Payload, 0) != 1500 ||
                        vilmarkInfo.Payload[0] != 0xDC || vilmarkInfo.Payload[1] != 0x05 ||
                        vilmarkInfo.Payload[4] != 5 || vilmarkInfo.Payload[5] != 0)
                        throw new InvalidOperationException("Vilmark DUNGEON_INFO dungeon id was truncated");
                    var lorienMap = GamePackets.LorienStartMap(0, 0);
                    // 39 = 10 头 + 3×9 怪物行 + 2 尾。怪物行 uid 必须是 u16：
                    // 写 byte 会让客户端解析错位导致副本加载崩溃。
                    if (lorienMap.ProtocolId != GamePackets.StartMap || lorienMap.Payload.Length != 39 ||
                        lorienMap.Payload[0] != 0 || lorienMap.Payload[1] != 0 ||
                        LittleEndian.ReadUInt16(lorienMap.Payload, 7) != 1 || lorienMap.Payload[9] != 3 ||
                        LittleEndian.ReadUInt16(lorienMap.Payload, 11) != 0 || LittleEndian.ReadUInt16(lorienMap.Payload, 13) != 1)
                        throw new InvalidOperationException("?? 洛兰 START_MAP 首房间布局失败");
                    var selectedEntry = GamePackets.DungeonEntry(7, 2, 1, 3, 4, 5, 6, 1003,
                        new KeyValuePair<ushort, DungeonCatalog.MonsterRow>[0], 12);
                    if (selectedEntry.Length != 2 || selectedEntry[0].ProtocolId != GamePackets.DungeonInfo ||
                        LittleEndian.ReadUInt16(selectedEntry[0].Payload, 0) != 7 ||
                        selectedEntry[0].Payload[2] != 2 || selectedEntry[0].Payload[3] != 1 ||
                        selectedEntry[1].ProtocolId != GamePackets.StartMap || selectedEntry[1].Payload[0] != 5 ||
                        selectedEntry[1].Payload[1] != 6 || LittleEndian.ReadUInt16(selectedEntry[1].Payload, 7) != 1003)
                        throw new InvalidOperationException("副本选择的 dungeon/map 启动包不一致");
                    // 洛兰深处（dungeon 2）：PVF 第一迷宫 start=(0,2)、boss=(0,1)。
                    var lorienInside = GamePackets.LorienStartMap(0, 2, 2);
                    if (lorienInside.Payload.Length != 57 || LittleEndian.ReadUInt16(lorienInside.Payload, 7) != 101 ||
                        lorienInside.Payload[9] != 5 || lorienInside.Payload[15] != 2 || lorienInside.Payload[52] != 0)
                        throw new InvalidOperationException("?? 洛兰深处 START_MAP 布局失败");
                    var lorienInsideSecond = GamePackets.LorienStartMap(1, 2, 2);
                    if (lorienInsideSecond.Payload.Length != 57 || lorienInsideSecond.Payload[0] != 1 || lorienInsideSecond.Payload[1] != 2 ||
                        LittleEndian.ReadUInt16(lorienInsideSecond.Payload, 7) != 111 || lorienInsideSecond.Payload[52] != 0)
                        throw new InvalidOperationException("?? 洛兰深处第二房普通图布局失败");
                    var lorienInsideBoss = GamePackets.LorienStartMap(0, 1, 2);
                    if (lorienInsideBoss.Payload.Length != 66 || LittleEndian.ReadUInt16(lorienInsideBoss.Payload, 7) != 119 ||
                        LittleEndian.ReadUInt16(lorienInsideBoss.Payload, 40) != 11 || lorienInsideBoss.Payload[43] != 3)
                        throw new InvalidOperationException("?? 洛兰深处真正 BOSS 房布局失败");
                    // 洛兰 boss 房 (2,1) map 13、7 只怪，row 4 为投掷哥布林 boss。
                    var lorienBoss = GamePackets.LorienStartMap(2, 1);
                    if (lorienBoss.Payload.Length != 75 || LittleEndian.ReadUInt16(lorienBoss.Payload, 7) != 13 ||
                        lorienBoss.Payload[9] != 7 || LittleEndian.ReadUInt16(lorienBoss.Payload, 49) != 2 || lorienBoss.Payload[52] != 3)
                        throw new InvalidOperationException("?? 洛兰 boss 房 START_MAP 布局失败");

                    var townInit82 = GamePackets.TownInit82Empty();
                    if (townInit82.ProtocolId != GamePackets.TownInit82 ||
                        townInit82.Payload.Length != 18 || townInit82.Payload[0] != 1 ||
                        townInit82.Payload.Skip(1).Any(value => value != 0))
                        throw new InvalidOperationException("?? CMD 82 空响应布局失败");

                    var finishLoaded = GamePackets.FinishLoaded();
                    if (finishLoaded.Type != GamePackets.Notification ||
                        finishLoaded.ProtocolId != GamePackets.FinishLoadingNotification ||
                        finishLoaded.Payload.Length != 0)
                        throw new InvalidOperationException("?? FINISH_LOADING 通知布局失败");

                    var tutorialBootstrap = GamePackets.TutorialBootstrapPacket();
                    if (tutorialBootstrap.Type != GamePackets.Notification ||
                        tutorialBootstrap.ProtocolId != GamePackets.TutorialBootstrap ||
                        tutorialBootstrap.Payload.Length != 0)
                        throw new InvalidOperationException("?? 教程启动通知布局失败");

                    var userState = GamePackets.UserStateChanged(1, 1);
                    if (userState.ProtocolId != GamePackets.UserState || userState.Payload.Length != 3 ||
                        LittleEndian.ReadUInt16(userState.Payload, 0) != 1 || userState.Payload[2] != 1)
                        throw new InvalidOperationException("?? 地下城用户状态通知布局失败");

                    // Regression: initial town activation must keep the local actor
                    // visible. USER_STATE=1 removes it in the Arad client.
                    var initialTownActivation = GamePackets.InitialTownActivation(characters[0], 1);
                    if (initialTownActivation.Length != 3 ||
                        initialTownActivation[0].Type != GamePackets.Notification ||
                        initialTownActivation[0].ProtocolId != GamePackets.UserState ||
                        initialTownActivation[0].Payload.Length != 3 ||
                        LittleEndian.ReadUInt16(initialTownActivation[0].Payload, 0) != 1 ||
                        initialTownActivation[0].Payload[2] != 0 ||
                        initialTownActivation[1].Type != GamePackets.Notification ||
                        initialTownActivation[1].ProtocolId != GamePackets.UserArea ||
                        initialTownActivation[2].Type != GamePackets.Notification ||
                        initialTownActivation[2].ProtocolId != GamePackets.AreaUsers)
                        throw new InvalidOperationException("?? 首次进入城镇的角色解锁顺序失败: " +
                            string.Join(",", initialTownActivation.Select(value =>
                                value.ProtocolId + ":" + BitConverter.ToString(value.Payload ?? new byte[0]))));
                    var enterTutorial = GamePackets.EnterTutorialSelection();
                    if (enterTutorial.ProtocolId != GamePackets.EnterSelectDungeonNotification ||
                        enterTutorial.Payload.Length != 2 || enterTutorial.Payload[0] != 1 || enterTutorial.Payload[1] != 0)
                        throw new InvalidOperationException("?? 进入地下城选择通知布局失败");
                    var enterDungeonSelectionPackets = GameServer.BuildEnterDungeonSelectionPackets(1,
                        new[] { new KeyValuePair<ushort, byte>(1500, 0) });
                    if (enterDungeonSelectionPackets.Length != 4 ||
                        enterDungeonSelectionPackets[0].ProtocolId != GamePackets.UserState ||
                        enterDungeonSelectionPackets[1].ProtocolId != GamePackets.UdpHost ||
                        enterDungeonSelectionPackets[2].ProtocolId != GamePackets.DungeonPermission ||
                        enterDungeonSelectionPackets[3].ProtocolId != GamePackets.EnterSelectDungeonNotification ||
                        enterDungeonSelectionPackets[2].Payload.Length != 5 ||
                        LittleEndian.ReadUInt16(enterDungeonSelectionPackets[2].Payload, 0) != 1 ||
                        LittleEndian.ReadUInt16(enterDungeonSelectionPackets[2].Payload, 2) != 1500 ||
                        enterDungeonSelectionPackets[2].Payload[4] != 0)
                        throw new InvalidOperationException("Enter dungeon selection truncated a high dungeon id");

                    ushort selectedDungeon; byte selectedDifficulty; byte selectedOption;
                    if (!GamePackets.TryReadSelectDungeon(ParseHex("080010270000"), out selectedDungeon, out selectedDifficulty, out selectedOption) ||
                        selectedDungeon != GamePackets.TutorialDungeonId || selectedDifficulty != 0 || selectedOption != 0)
                        throw new InvalidOperationException("?? SELECT_DUNGEON 请求解析失败");
                    var tutorialInfo = GamePackets.TutorialDungeonInfo(0);
                    if (tutorialInfo.ProtocolId != GamePackets.DungeonInfo || tutorialInfo.Payload.Length != 8 ||
                        LittleEndian.ReadUInt16(tutorialInfo.Payload, 0) != GamePackets.TutorialDungeonId ||
                        tutorialInfo.Payload[4] != byte.MaxValue || tutorialInfo.Payload[5] != byte.MaxValue)
                        throw new InvalidOperationException("?? 教程地下城信息通知布局失败");
                    var tutorialMap = GamePackets.TutorialStartMap();
                    if (tutorialMap.ProtocolId != GamePackets.StartMap || tutorialMap.Payload.Length != 48 ||
                        tutorialMap.Payload[6] != 1 || LittleEndian.ReadUInt16(tutorialMap.Payload, 7) != GamePackets.TutorialMapId ||
                        tutorialMap.Payload[9] != 4 || LittleEndian.ReadUInt16(tutorialMap.Payload, 13) != 1)
                        throw new InvalidOperationException("?? 教程 START_MAP 通知布局失败");

                    ushort monsterId;
                    if (!GamePackets.TryReadMonsterDeath(ParseHex("1900010001000000000000000000340000000500000000"), out monsterId) || monsterId != 1 ||
                        GamePackets.TryReadMonsterDeath(new byte[21], out monsterId))
                        throw new InvalidOperationException("?? MONSTER_DEATH 请求解析失败");
                    var monsterDie = GamePackets.MonsterDie(1);
                    if (monsterDie.Type != GamePackets.Notification || monsterDie.ProtocolId != GamePackets.MonsterDieNotification ||
                        !monsterDie.Payload.SequenceEqual(ParseHex("01000000FF00")))
                        throw new InvalidOperationException("?? MONSTER_DIE 通知布局失败");

                    ushort groundRuntimeId;
                    if (!GamePackets.TryReadPickupGroundItem(ParseHex("2A000140"), out groundRuntimeId) ||
                        groundRuntimeId != 0x4001 ||
                        GamePackets.TryReadPickupGroundItem(new byte[3], out groundRuntimeId))
                        throw new InvalidOperationException("?? PICKUP_GROUND_ITEM request layout failed");

                    var packetDrop = new GroundDropRecord
                    {
                        RuntimeId = 0x4001, ItemId = 8, Count = 1, X = 360, Y = 300,
                        ItemFlags = 0, Durability = 0
                    };
                    var spawned = GamePackets.GroundItemSpawned(1, packetDrop);
                    if (spawned.Type != GamePackets.Notification ||
                        spawned.ProtocolId != GamePackets.GroundItemSpawnedNotification ||
                        !spawned.Payload.SequenceEqual(ParseHex("010068012C010140080000010000000000")))
                        throw new InvalidOperationException("?? GROUND_ITEM_SPAWNED notification layout failed");

                    var monsterDrop = GamePackets.MonsterDie(1, new[] { packetDrop });
                    if (!monsterDrop.Payload.SequenceEqual(
                        ParseHex("01000101400800010000000000FFFF00FF00")))
                        throw new InvalidOperationException("MONSTER_DIE embedded drop layout failed");
                    var championMonsterDie = GamePackets.MonsterDie(2, null, 1);
                    if (!championMonsterDie.Payload.SequenceEqual(ParseHex("020000000100")))
                        throw new InvalidOperationException("MONSTER_DIE champion ordinal layout failed");
                    var championSettlement = new DungeonSettlementState();
                    if (championSettlement.AllocateChampionRewardOrdinal(false, false, false) != -1 ||
                        championSettlement.AllocateChampionRewardOrdinal(false, true, false) != 1 ||
                        championSettlement.AllocateChampionRewardOrdinal(false, false, true) != 2 ||
                        championSettlement.AllocateChampionRewardOrdinal(true, true, false) != -1)
                        throw new InvalidOperationException("champion reward ordinal allocation failed");

                    var picked = GamePackets.GroundItemPickedUp(1, 0x4001, 39);
                    if (picked.Type != GamePackets.Notification ||
                        picked.ProtocolId != GamePackets.GroundItemPickedUpNotification ||
                        !picked.Payload.SequenceEqual(ParseHex("014001000000000001002700")))
                        throw new InvalidOperationException("?? GROUND_ITEM_PICKED_UP notification layout failed");

                    var pickedGold = GamePackets.GroundItemPickedUpGold(1, 0x4001, 123);
                    if (!pickedGold.Payload.SequenceEqual(
                        ParseHex("014001007B00000000000000000000000000000000000000")))
                        throw new InvalidOperationException("GROUND_ITEM_PICKED_UP gold layout failed");

                    var pickupCategorySamples = new[]
                    {
                        new InventoryItemRecord { Slot = 11, ItemId = 10050, Count = 1, Durability = 28 }, // equipment
                        new InventoryItemRecord { Slot = 12, ItemId = 8, Count = 3 },                       // consumable
                        new InventoryItemRecord { Slot = 13, ItemId = 2000, Count = 4 },                    // material
                        new InventoryItemRecord { Slot = 14, ItemId = 3072, Count = 1 }                     // quest
                    };
                    foreach (var pickupItem in pickupCategorySamples)
                    {
                        var refresh = GamePackets.UpdateItem(0, pickupItem.Slot, pickupItem);
                        if (refresh.Type != GamePackets.Notification || refresh.ProtocolId != GamePackets.UpdateItemList ||
                            refresh.Payload.Length != 24 || refresh.Payload[0] != 0 ||
                            LittleEndian.ReadUInt16(refresh.Payload, 3) != pickupItem.Slot ||
                            LittleEndian.ReadUInt32(refresh.Payload, 5) != pickupItem.ItemId ||
                            LittleEndian.ReadUInt32(refresh.Payload, 9) != pickupItem.Count ||
                            LittleEndian.ReadUInt16(refresh.Payload, 14) != pickupItem.Durability)
                            throw new InvalidOperationException("?? four-category pickup refresh layout failed");
                    }

                    DungeonCatalog.MonsterRow parsedMapMonster;
                    if (!DungeonCatalog.TryParseMonsterLine(
                            "1 1 0 535 180 0 1 1 `[fixed]` `[normal]`", out parsedMapMonster) ||
                        parsedMapMonster.MonsterIndex != 1 || parsedMapMonster.X != 535 ||
                        parsedMapMonster.Y != 180 || parsedMapMonster.IsBoss)
                        throw new InvalidOperationException("PVF 地图怪物出生坐标解析失败");

                    var groundDrops = new GroundDropState();
                    var firstDrop = groundDrops.SpawnForMonster(0);
                    var secondDrop = groundDrops.SpawnForMonster(1);
                    var edgeDrop = groundDrops.SpawnForMonster(2, 8, 1, 0, 0, 0);
                    GroundDropRecord claimedDrop = null;
                    ushort claimedSlot = ushort.MaxValue;
                    if (firstDrop.RuntimeId != 0x4000 || secondDrop.RuntimeId != 0x4001 ||
                        firstDrop.ItemId != 8 || firstDrop.Count != 1 ||
                        firstDrop.X < 300 || firstDrop.X > 700 || firstDrop.Y < 200 || firstDrop.Y > 400 ||
                        edgeDrop.X != 0 || edgeDrop.Y != 0 ||
                        groundDrops.Count != 3 ||
                        !groundDrops.TryGet(firstDrop.RuntimeId, out claimedDrop) || claimedDrop.ItemId != 8 ||
                        groundDrops.TryClaim(firstDrop.RuntimeId, drop => ushort.MaxValue,
                            out claimedDrop, out claimedSlot) || groundDrops.Count != 3 ||
                        !groundDrops.TryClaim(firstDrop.RuntimeId, drop => 39,
                            out claimedDrop, out claimedSlot) || claimedSlot != 39 ||
                        groundDrops.TryClaim(firstDrop.RuntimeId, drop => 39,
                            out claimedDrop, out claimedSlot) || groundDrops.Count != 2)
                        throw new InvalidOperationException("ground drop one-shot state failed");
                    groundDrops.Clear();
                    if (groundDrops.Count != 0 || groundDrops.TryGet(secondDrop.RuntimeId, out claimedDrop))
                        throw new InvalidOperationException("ground drop room reset failed");

                    var roomDrops = new GroundDropState();
                    var persistedDrop = roomDrops.SpawnForMonster(4, 2000, 3, 0, 420, 260);
                    var roomLedger = new DungeonRoomDropLedger();
                    roomLedger.Save(1, 2, roomDrops);
                    roomDrops.Clear();
                    if (!roomLedger.TryRestore(1, 2, roomDrops) || roomDrops.Count != 1 ||
                        !roomDrops.TryGet(persistedDrop.RuntimeId, out claimedDrop) ||
                        claimedDrop.ItemId != 2000 || claimedDrop.Count != 3)
                        throw new InvalidOperationException("room drop restore failed");
                    roomDrops.Remove(persistedDrop.RuntimeId);
                    roomLedger.Remove(persistedDrop.RuntimeId);
                    roomDrops.Clear();
                    if (!roomLedger.TryRestore(1, 2, roomDrops) || roomDrops.Count != 0 ||
                        roomDrops.SpawnForMonster(5, 8).RuntimeId == persistedDrop.RuntimeId)
                        throw new InvalidOperationException("room drop pickup resurrected or rewound runtime id");

                    // Breakable-object rewards use START_MAP's separate 11-byte
                    // row. They must keep their object binding across a room
                    // revisit and disappear permanently once collected.
                    var passivePacketDrop = new GroundDropRecord
                    {
                        RuntimeId = 0x4002, ItemId = 2000, Count = 3,
                        Durability = 47
                    };
                    var passiveStartMap = GamePackets.DungeonStartMap(1, 2, 5601,
                        new KeyValuePair<ushort, DungeonCatalog.MonsterRow>[0], 40,
                        new[] { new PassiveObjectDropRecord { ObjectIndex = 7, Drop = passivePacketDrop } });
                    if (passiveStartMap.Payload.Length != 23 || passiveStartMap.Payload[9] != 0 ||
                        passiveStartMap.Payload[10] != 1 || passiveStartMap.Payload[11] != 7 ||
                        LittleEndian.ReadUInt16(passiveStartMap.Payload, 12) != 0x4002 ||
                        LittleEndian.ReadUInt16(passiveStartMap.Payload, 14) != 2000 ||
                        LittleEndian.ReadUInt32(passiveStartMap.Payload, 16) != 3 ||
                        LittleEndian.ReadUInt16(passiveStartMap.Payload, 20) != 47 ||
                        passiveStartMap.Payload[22] != 0)
                        throw new InvalidOperationException("passive-object START_MAP record layout failed");

                    // State 0 is the only START_MAP form that preserves an
                    // existing ARAD room scene.  It deliberately ends after
                    // x/y, seed and state: a map definition would make the
                    // client rebuild the room and respawn static actors.
                    var cachedStartMap = GamePackets.DungeonStartMap(1, 2, 5601,
                        new KeyValuePair<ushort, DungeonCatalog.MonsterRow>[0], 40,
                        GamePackets.StartMapCachedRoomState,
                        new[] { new PassiveObjectDropRecord { ObjectIndex = 7, Drop = passivePacketDrop } });
                    if (cachedStartMap.ProtocolId != GamePackets.StartMap ||
                        cachedStartMap.Payload.Length != 7 || cachedStartMap.Payload[0] != 1 ||
                        cachedStartMap.Payload[1] != 2 || cachedStartMap.Payload[6] !=
                        GamePackets.StartMapCachedRoomState)
                        throw new InvalidOperationException("cached-room START_MAP layout failed");

                    var passiveRoomDrops = new GroundDropState();
                    var ordinaryRoomDrop = passiveRoomDrops.SpawnForMonster(3, 8, 1);
                    var passiveRoomDrop = passiveRoomDrops.SpawnForPassiveObject(9, 2000, 2, 31);
                    var passiveRoomLedger = new DungeonRoomDropLedger();
                    passiveRoomLedger.Save(3, 4, passiveRoomDrops, new[] { passiveRoomDrop });
                    passiveRoomDrops.Clear();
                    List<PassiveObjectDropRecord> restoredPassiveDrops;
                    if (!passiveRoomLedger.TryRestore(3, 4, passiveRoomDrops, out restoredPassiveDrops) ||
                        passiveRoomDrops.Count != 2 || restoredPassiveDrops.Count != 1 ||
                        restoredPassiveDrops[0].ObjectIndex != 9 ||
                        restoredPassiveDrops[0].Drop.RuntimeId != passiveRoomDrop.Drop.RuntimeId ||
                        !passiveRoomLedger.GetPassiveRuntimeIds(3, 4)
                            .SequenceEqual(new[] { passiveRoomDrop.Drop.RuntimeId }))
                        throw new InvalidOperationException("passive-object room revisit binding failed");

                    // A normal ground item is rebuilt after FinishLoading;
                    // a passive-object reward is already sent in START_MAP
                    // and must not be emitted a second time as a free item.
                    var restoredGroundPackets = GameServer.BuildRestoredGroundDropPackets(1,
                        passiveRoomDrops, restoredPassiveDrops);
                    if (restoredGroundPackets.Length != 1 ||
                        restoredGroundPackets[0].ProtocolId != GamePackets.GroundItemSpawnedNotification ||
                        LittleEndian.ReadUInt16(restoredGroundPackets[0].Payload, 6) != ordinaryRoomDrop.RuntimeId ||
                        LittleEndian.ReadUInt16(restoredGroundPackets[0].Payload, 8) != ordinaryRoomDrop.ItemId)
                        throw new InvalidOperationException("ordinary ground drop revisit packet failed");

                    passiveRoomDrops.Remove(passiveRoomDrop.Drop.RuntimeId);
                    passiveRoomLedger.Remove(passiveRoomDrop.Drop.RuntimeId);
                    passiveRoomDrops.Clear();
                    if (!passiveRoomLedger.TryRestore(3, 4, passiveRoomDrops, out restoredPassiveDrops) ||
                        passiveRoomDrops.Count != 1 || restoredPassiveDrops.Count != 0 ||
                        !passiveRoomDrops.TryGet(ordinaryRoomDrop.RuntimeId, out claimedDrop) ||
                        passiveRoomLedger.GetPassiveRuntimeIds(3, 4).Length != 0)
                        throw new InvalidOperationException("picked passive-object drop was restored on room revisit");

                    passiveRoomDrops.Remove(ordinaryRoomDrop.RuntimeId);
                    passiveRoomLedger.Remove(ordinaryRoomDrop.RuntimeId);
                    passiveRoomDrops.Clear();
                    if (!passiveRoomLedger.TryRestore(3, 4, passiveRoomDrops, out restoredPassiveDrops) ||
                        passiveRoomDrops.Count != 0 || restoredPassiveDrops.Count != 0 ||
                        GameServer.BuildRestoredGroundDropPackets(1, passiveRoomDrops,
                            restoredPassiveDrops).Length != 0)
                        throw new InvalidOperationException("picked ordinary ground drop was restored on room revisit");

                    var expGained = GamePackets.ExperienceGained(1, 30, 0, 0, 490);
                    if (expGained.Type != GamePackets.Notification || expGained.ProtocolId != GamePackets.ExperienceGainedNotification ||
                        expGained.Payload.Length != 15 || expGained.Payload[0] != 1 ||
                        LittleEndian.ReadUInt32(expGained.Payload, 1) != 30 ||
                        LittleEndian.ReadUInt32(expGained.Payload, 5) != 0 ||
                        LittleEndian.ReadUInt32(expGained.Payload, 9) != 0 ||
                        LittleEndian.ReadUInt16(expGained.Payload, 13) != 490)
                        throw new InvalidOperationException("?? GET_EXP 通知布局失败");
                    if (ExperienceTable.ExpRequiredWithinLevel(29) != 593079 ||
                        ExperienceTable.ExpRequiredWithinLevel(59) != 27789097 ||
                        ExperienceTable.SkillPointProgress(1, 14) != 0 ||
                        ExperienceTable.SkillPointProgress(1, 15) != 1 ||
                        ExperienceTable.SkillPointProgress(1, 450) != 30 ||
                        ExperienceTable.MonsterExperience(12, 1, false) != 1 ||
                        ExperienceTable.MonsterExperience(12, 6, false) != 4 ||
                        ExperienceTable.MonsterExperience(1, 12, false) != 7 ||
                        ExperienceTable.MonsterExperience(12, 12, false) != 140 ||
                        ExperienceTable.MonsterExperience(12, 17, false) != 228 ||
                        ExperienceTable.MonsterExperience(12, 7, false) != 45 ||
                        ExperienceTable.MonsterExperience(4, 2, true) !=
                            ExperienceTable.MonsterExperience(4, 2, false) * 4 ||
                        ExperienceTable.DungeonClearExperience(12, 12, 0, 55) <= 0 ||
                        ExperienceTable.DungeonClearExperience(12, 12, 3, 95) <=
                            ExperienceTable.DungeonClearExperience(12, 12, 0, 55) ||
                        ExperienceTable.DungeonClearExperience(12, 1, 0, 55) >=
                            ExperienceTable.DungeonClearExperience(12, 12, 0, 55))
                        throw new InvalidOperationException("?? 经验/SP/怪物类型曲线失败");

                    var permission = GamePackets.DungeonPermissions(new[]
                    {
                        new KeyValuePair<ushort, byte>(1, 2),
                        new KeyValuePair<ushort, byte>(2, 1)
                    });
                    if (permission.ProtocolId != GamePackets.DungeonPermission || permission.Payload.Length != 8 ||
                        LittleEndian.ReadUInt16(permission.Payload, 0) != 2 || LittleEndian.ReadUInt16(permission.Payload, 2) != 1 ||
                        permission.Payload[4] != 2 || LittleEndian.ReadUInt16(permission.Payload, 5) != 2 || permission.Payload[7] != 1)
                        throw new InvalidOperationException("?? 副本难度权限布局失败");

                    var grant = store.GrantExperience(created.Character.Id, 30);
                    if (grant == null || grant.Item2 != 1 || grant.Item3 != 30 || grant.Item4 != 450 || grant.Item1 ||
                        grant.Item5 != 9991)
                        throw new InvalidOperationException("经验结算/升级阈值失败");
                    var grantLevelUp = store.GrantExperience(created.Character.Id, 420);
                    if (grantLevelUp == null || !grantLevelUp.Item1 || grantLevelUp.Item2 != 2 ||
                        grantLevelUp.Item3 != 0 || grantLevelUp.Item4 != 1014 || grantLevelUp.Item5 != 10019)
                        throw new InvalidOperationException("经验升级推进失败");
                    var grantSecondLevel = store.GrantExperience(created.Character.Id, 1014);
                    if (grantSecondLevel == null || !grantSecondLevel.Item1 || grantSecondLevel.Item2 != 3 ||
                        grantSecondLevel.Item3 != 0 || grantSecondLevel.Item4 != 2046 || grantSecondLevel.Item5 != 10049)
                        throw new InvalidOperationException("累计经验表的级内差值推进失败");
                    var charactersAfterExp = store.ListCharacters(account);
                    if (charactersAfterExp[0].Level != 3 || charactersAfterExp[0].Exp != 0)
                        throw new InvalidOperationException("SQLite 经验持久化失败");
                    if (store.UnlockNextDungeonDifficulty(created.Character.Id, 1, 0) != 1 ||
                        store.UnlockNextDungeonDifficulty(created.Character.Id, 1, 1) != 2 ||
                        store.UnlockNextDungeonDifficulty(created.Character.Id, 1, 0) != 2 ||
                        store.LoadDungeonProgress(created.Character.Id)[1] != 2)
                        throw new InvalidOperationException("副本难度解锁/持久化失败");

                    var tutorialRun = new TutorialDungeonRun();
                    if (tutorialRun.ReportMonsterDeath(0) != MonsterDeathResult.NotInDungeon) throw new InvalidOperationException("副本外死亡报告未被拒绝");
                    tutorialRun.Start();
                    if (tutorialRun.ReportMonsterDeath(1) != MonsterDeathResult.Accepted || tutorialRun.AliveMonsterCount != 3 ||
                        tutorialRun.ReportMonsterDeath(1) != MonsterDeathResult.UnknownOrAlreadyDead ||
                        tutorialRun.ReportMonsterDeath(2) != MonsterDeathResult.Accepted ||
                        tutorialRun.ReportMonsterDeath(0) != MonsterDeathResult.Accepted ||
                        tutorialRun.ReportMonsterDeath(3) != MonsterDeathResult.RoomCleared || !tutorialRun.IsCleared)
                        throw new InvalidOperationException("教程怪物死亡/房间清空状态机失败");

                    var lorienInsideRun = new LorienDungeonRun(null);
                    if (!lorienInsideRun.Start(2, 0) || lorienInsideRun.RoomX != 0 || lorienInsideRun.RoomY != 2 || lorienInsideRun.IsBossRoom ||
                        !lorienInsideRun.EnterRoom(1, 2) || lorienInsideRun.IsBossRoom || lorienInsideRun.AliveMonsterCount != 5 ||
                        !lorienInsideRun.EnterRoom(0, 1) || !lorienInsideRun.IsBossRoom || lorienInsideRun.AliveMonsterCount != 6 ||
                        lorienInsideRun.EnterRoom(2, 2))
                        throw new InvalidOperationException("洛兰深处完整迷宫/真正 BOSS 房状态机失败");
                    if (lorienInsideRun.ReportMonsterDeath(3) != MonsterDeathResult.RoomCleared ||
                        lorienInsideRun.AliveMonsterCount != 0 || lorienInsideRun.TakeAutoDefeatedMonsterIds().Length != 5)
                        throw new InvalidOperationException("BOSS 死亡自动清除小怪状态机失败");

                    var settlement = new DungeonSettlementState();
                    if (settlement.TryBeginResult() || settlement.TryBeginCardLayout() ||
                        settlement.TryEnableCardSelection() || settlement.TrySelectCard(0, 0))
                        throw new InvalidOperationException("结算状态机接受了提前请求");
                    settlement.PrepareCardRewards(12, 1, new Random(12345));
                    if (settlement.FreeCardGold.Length != 4 || settlement.PaidCardRewards.Length != 4 ||
                        settlement.PaidCardPrice == 0 || settlement.FreeCardGold.Any(value => value == 0) ||
                        settlement.FreeCardRewards.Any(row => row == null ||
                            !row.Any(value => value.IsGold) || !row.Any(value => !value.IsGold)) ||
                        settlement.PaidCardRewards.Any(row => row == null ||
                            !row.Any(value => value.IsGold) || !row.Any(value => !value.IsGold)))
                        throw new InvalidOperationException("结算卡牌奖励生成失败");
                    if (!settlement.TryEnableClear() || settlement.TryEnableClear() ||
                        !settlement.TryBeginResult() || settlement.TryBeginResult() ||
                        !settlement.TryBeginCardLayout() || settlement.TryBeginCardLayout() ||
                        settlement.TrySelectCard(0, 2) ||
                        !settlement.TryEnableCardSelection() || settlement.TryEnableCardSelection() ||
                        !settlement.TrySelectCard(1, 3) || settlement.TrySelectCard(1, 0) ||
                        !settlement.TrySelectCard(0, 2) || settlement.TrySelectCard(0, 1))
                        throw new InvalidOperationException("结算状态机顺序/幂等性失败");
                    if (settlement.SelectedFreeGold != settlement.FreeCardGold[2] ||
                        !object.ReferenceEquals(settlement.SelectedPaidReward,
                            settlement.PaidCardRewards[3].First(value => !value.IsGold)))
                        throw new InvalidOperationException("结算卡牌选择奖励不一致");
                    settlement.Reset();
                    if (settlement.ClearEnabled || settlement.ResultSent || settlement.CardLayoutSent ||
                        settlement.CardSelectionEnabled || settlement.CardSelected ||
                        settlement.FreeCardIndex != byte.MaxValue || settlement.PaidCardIndex != byte.MaxValue ||
                        settlement.PaidCardPrice != 0 || settlement.FreeCardGold.Any(value => value != 0) ||
                        settlement.PaidCardRewards.Any(value => value == null || value.Length != 0))
                        throw new InvalidOperationException("结算状态机重置失败");

                    var cipher = new GameClientCipherState();
                    PacketFrame decoded;
                    var check = new PacketFrame
                    {
                        Type = 1, ProtocolId = 0, DeclaredCrc32 = 0xE38A6876,
                        Body = ParseHex("00007575757575757575")
                    };
                    if (!cipher.TryDecode(check, out decoded) || decoded.Body.Skip(2).Any(value => value != 0))
                        throw new InvalidOperationException("频道首包解密失败");
                    var login = new PacketFrame
                    {
                        Type = 1, ProtocolId = 1, DeclaredCrc32 = 0xFD14BAC5,
                        Body = ParseHex("0100838282829FDB5E9F838282829FDB5E9F82808282824E5E61C37807B61B0A5B0EC2")
                    };
                    if (!cipher.TryDecode(login, out decoded) || LittleEndian.ReadUInt32(decoded.Body, 2) != 4 ||
                        System.Text.Encoding.ASCII.GetString(decoded.Body, 6, 4) != "test")
                        throw new InvalidOperationException("?? 独立种子登录包解密失败");

                    var legacyAccount = store.EnsureAccount("create_channel_2008", "test");
                    var legacyCharacter = store.CreateCharacter(legacyAccount, "迁移测试", 2);
                    if (!legacyCharacter.Success || store.MergeLocalAccountCharacters("create_channel_2008", account) != 1)
                        throw new InvalidOperationException("旧本地账号角色迁移失败");
                    var mergedRoster = store.ListCharacters(account);
                    if (mergedRoster.Count != 2 || mergedRoster[0].Slot != 0 || mergedRoster[1].Slot != 1 ||
                        mergedRoster[0].Job != 0 || mergedRoster[1].Job != 2)
                        throw new InvalidOperationException("多角色账号合并/槽位重排失败");
                    var mergedRosterPayload = GamePackets.CharacterList(mergedRoster).Payload;
                    if (mergedRosterPayload.Length < 3 || mergedRosterPayload[0] != 2 ||
                        LittleEndian.ReadUInt16(mergedRosterPayload, 1) != 2)
                        throw new InvalidOperationException("多角色列表封包数量失败");
                }
                // Reopening the store must not clamp SP earned from experience
                // or books back to the zero-point character baseline.
                using (var reopened = new SqliteGameStore(Path.Combine(root, "test.sqlite"), schema))
                {
                    var reopenedAccount = reopened.EnsureAccount("test", "test");
                    if (reopened.ListCharacters(reopenedAccount).First().SkillPoints != 10049)
                        throw new InvalidOperationException("SQLite 重启后经验/SP 书技能点被错误重置");
                }
                Console.WriteLine("SELF-TEST OK: SQLite、角色列表/实体、城镇区域、创建请求、选角响应与Arad客户端包解密均通过。");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine("SELF-TEST FAILED: " + exception); return 1; }
            finally
            {
                GameServerPacket.UseLegacyChannelHeader = previousLegacy;
                GameServerPacket.TutorialEnabled = previousTutorial;
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static byte[] ParseHex(string value)
        {
            var result = new byte[value.Length / 2];
            for (var index = 0; index < result.Length; index++)
                result[index] = Convert.ToByte(value.Substring(index * 2, 2), 16);
            return result;
        }

        private static void SkillDefinitionCatalogSelfTest()
        {
            object definition;
            var armorText =
@"[purchase cost]
0
[/purchase cost]
[required level] 1
[type] `[passive]`
[skill class] 4
[maximum level] 1
[growtype maximum level] 0 1 0 0 0
[skill fitness growtype]
0
1
2
3
4
[/skill fitness growtype]";
            if (!TryParseSkillDefinition(172, "skill/Swordman/ArmorMasteryLight.skl", armorText, out definition))
                throw new InvalidOperationException("SkillCatalog.TryParseDefinition did not parse a valid passive skill");
            if (ReadMember<byte>(definition, "SkillId") != 172 ||
                !string.Equals(ReadMember<string>(definition, "Path"), "skill/Swordman/ArmorMasteryLight.skl", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ReadMember<object>(definition, "Type").ToString(), "Passive", StringComparison.OrdinalIgnoreCase) ||
                ReadMember<byte>(definition, "SkillClass") != 4 ||
                ReadMember<int>(definition, "PurchaseCost") != 0 ||
                ReadMember<int>(definition, "RequiredLevel") != 1 ||
                ReadMember<int>(definition, "MaximumLevel") != 1 ||
                !ReadGrowTypeMaximumLevels(definition).SequenceEqual(new byte[] { 0, 1, 0, 0, 0 }) ||
                !ReadFitnessGrowTypes(definition).SequenceEqual(new byte[] { 0, 1, 2, 3, 4 }))
                throw new InvalidOperationException("SkillCatalog.TryParseDefinition returned the wrong skill fields");

            if (TryParseSkillDefinition(181, "skill/Swordman/CompoundCraft.skl",
                    "[purchase cost]\r\nfree\r\n[/purchase cost]\r\n[required level] 1\r\n[type] `[passive]`\r\n[skill class] 4\r\n[maximum level] 10\r\n[skill fitness growtype]\r\n0\r\n[/skill fitness growtype]",
                    out definition))
                throw new InvalidOperationException("SkillCatalog.TryParseDefinition accepted malformed skill text");

            var pvfPath = GetSelfTestScriptPvfPath();
            var catalog = new SkillCatalog(new PvfArchiveReader(pvfPath));
            var swordmanArmor = GetSkillDefinition(catalog, 0, 172);
            if (swordmanArmor == null ||
                ReadMember<int>(swordmanArmor, "PurchaseCost") != 0 ||
                ReadMember<int>(swordmanArmor, "RequiredLevel") != 1 ||
                !string.Equals(ReadMember<object>(swordmanArmor, "Type").ToString(), "Passive", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SkillCatalog.Get failed to load swordman armor mastery from Script.pvf");

            var swordmanCompoundCraft = GetSkillDefinition(catalog, 0, 181);
            if (swordmanCompoundCraft == null || ReadMember<int>(swordmanCompoundCraft, "PurchaseCost") != 10)
                throw new InvalidOperationException("SkillCatalog.Get failed to load swordman CompoundCraft from Script.pvf");

            var birthPassives = GetBirthPassiveSkills(catalog, 0).Select(value => ReadMember<byte>(value, "SkillId")).ToArray();
            if (!birthPassives.Contains((byte)172) || birthPassives.Contains((byte)181))
                throw new InvalidOperationException("SkillCatalog birth-passive filtering did not include free armor mastery and exclude CompoundCraft");

            var tempArchive = Path.Combine(Path.GetTempPath(), "skill-catalog-selftest-" + Guid.NewGuid().ToString("N") + ".pvf");
            try
            {
                File.WriteAllBytes(tempArchive, PvfArchiveReader.CreateSingleFile("misc/test.txt", new byte[] { 1, 2, 3 }, 1));
                if (new SkillCatalog(new PvfArchiveReader(tempArchive)).Count != 0)
                    throw new InvalidOperationException("SkillCatalog should ignore missing job skill lists");
            }
            finally
            {
                try { File.Delete(tempArchive); } catch { }
            }
        }

        private static void SkillGrowthRegressionSelfTest()
        {
            var catalog = new SkillCatalog(new PvfArchiveReader(GetSelfTestScriptPvfPath()));
            var automaticIds = catalog.GetAutomaticPassiveSkills(0, 1, 60)
                .Select(value => value.SkillId).ToArray();
            foreach (var expected in new byte[] { 27, 33, 37, 172 })
                if (!automaticIds.Contains(expected))
                    throw new InvalidOperationException("Weapon Master automatic passive is missing: " + expected);
            if (automaticIds.Contains((byte)174) || automaticIds.Contains((byte)185) || automaticIds.Contains((byte)187))
                throw new InvalidOperationException("Weapon Master received another subclass's armor mastery");

            byte first, last;
            var expectedRanges = new[]
            {
                new byte[] { 6, 47 }, new byte[] { 48, 89 }, new byte[] { 90, 131 },
                new byte[] { 132, 173 }, new byte[] { 174, 215 }
            };
            for (byte skillClass = 0; skillClass < expectedRanges.Length; skillClass++)
            {
                if (!GamePackets.TryGetSkillClassRange(skillClass, out first, out last) ||
                    first != expectedRanges[skillClass][0] || last != expectedRanges[skillClass][1])
                    throw new InvalidOperationException("Skill class slot range is wrong for class " + skillClass);
            }

            var normalized = GamePackets.CurrentSkills(0, 1, 60, new[]
            {
                new CharacterSkillRecord { Slot = 0, SkillId = 5, Level = 1 },
                new CharacterSkillRecord { Slot = 1, SkillId = 46, Level = 1 },
                new CharacterSkillRecord { Slot = 42, SkillId = 4, Level = 20 },
                new CharacterSkillRecord { Slot = 174, SkillId = 174, Level = 1 },
                new CharacterSkillRecord { Slot = 175, SkillId = 185, Level = 1 }
            }, catalog);
            if (normalized.GroupBy(value => value.Slot).Any(group => group.Count() != 1) ||
                !normalized.Any(value => value.SkillId == 5 && value.Slot == 0) ||
                !normalized.Any(value => value.SkillId == 46 && value.Slot == 1) ||
                !normalized.Any(value => value.SkillId == 4 && value.Slot >= 48 && value.Slot <= 89) ||
                !normalized.Any(value => value.SkillId == 172 && value.Slot >= 174 && value.Slot <= 215) ||
                normalized.Any(value => value.SkillId == 174 || value.SkillId == 185))
                throw new InvalidOperationException("Weapon Master skill normalization is incorrect: " +
                    string.Join(",", normalized.Select(value => value.Slot + ":" + value.SkillId + ":" + value.Level)));

            // Regression for the live crash: skill 68 was learned twice in
            // slot 48, then skill 33 was incorrectly assigned to slot 48 too.
            var purchaseCharacter = new CharacterRecord { Job = 0, Level = 60, SkillPoints = 10000 };
            var purchasedSkills = new List<CharacterSkillRecord>
            {
                new CharacterSkillRecord { Slot = 48, SkillId = 68, Level = 1 }
            };
            byte purchasedSlot, purchasedLevel;
            int purchasedCost;
            if (!GameServer.TryPurchaseSkill(catalog, purchaseCharacter, purchasedSkills, 68,
                    out purchasedSlot, out purchasedLevel, out purchasedCost) ||
                purchasedSlot != 48 || purchasedLevel != 2)
                throw new InvalidOperationException("Purchased skill upgrade did not retain its existing slot");
            if (!GameServer.TryPurchaseSkill(catalog, purchaseCharacter, purchasedSkills, 33,
                    out purchasedSlot, out purchasedLevel, out purchasedCost) ||
                purchasedSlot == 48 || purchasedSkills.Select(value => value.Slot).Distinct().Count() != purchasedSkills.Count)
                throw new InvalidOperationException("Consecutive skill purchases reused an occupied panel slot");
        }

        private static string GetSelfTestScriptPvfPath()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(directory.FullName, "data", "Script.pvf");
                if (File.Exists(path)) return path;
                directory = directory.Parent;
            }
            throw new InvalidOperationException("SelfTest requires data\\Script.pvf in the project directory tree.");
        }

        private static void LocalCashMigrationSelfTest(string root, string currentSchemaPath)
        {
            var legacyDatabasePath = Path.Combine(root, "cash-migration.sqlite");
            var legacySchema = File.ReadAllText(currentSchemaPath, System.Text.Encoding.UTF8)
                .Replace("    cash_initialized INTEGER NOT NULL DEFAULT 1,\r\n", string.Empty)
                .Replace("    cash_initialized INTEGER NOT NULL DEFAULT 1,\n", string.Empty);
            using (var connection = new System.Data.SQLite.SQLiteConnection(
                "Data Source=" + legacyDatabasePath + ";Version=3;Foreign Keys=True;"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = legacySchema;
                    command.ExecuteNonQuery();
                    command.CommandText = "INSERT INTO accounts(account_name,password) VALUES('cash-migration','test');" +
                        "INSERT INTO characters(account_id,slot_index,name,job,cash) VALUES(1,0,'cash-migration',0,0);";
                    command.ExecuteNonQuery();
                }
            }

            using (var migrated = new SqliteGameStore(legacyDatabasePath, currentSchemaPath))
            {
                var character = migrated.ListCharacters(1).Single();
                if (character.Cash != SqliteGameStore.InitialLocalCash)
                    throw new InvalidOperationException("cash-shop migration did not initialize an old zero-cash character");
                migrated.SaveCash(character.Id, 0);
            }
            using (var reloaded = new SqliteGameStore(legacyDatabasePath, currentSchemaPath))
            {
                if (reloaded.ListCharacters(1).Single().Cash != 0)
                    throw new InvalidOperationException("cash-shop migration refilled a spent local balance");
            }
        }

        private static bool TryParseSkillDefinition(byte skillId, string path, string text, out object definition)
        {
            definition = null;
            var method = typeof(SkillCatalog).GetMethod("TryParseDefinition", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("SkillCatalog.TryParseDefinition is missing");
            var arguments = new object[] { skillId, path, text, null };
            var parsed = (bool)method.Invoke(null, arguments);
            definition = arguments[3];
            return parsed;
        }

        private static object GetSkillDefinition(SkillCatalog catalog, byte job, byte skillId)
        {
            var method = typeof(SkillCatalog).GetMethod("Get", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("SkillCatalog.Get is missing");
            return method.Invoke(catalog, new object[] { job, skillId });
        }

        private static object[] GetBirthPassiveSkills(SkillCatalog catalog, byte job)
        {
            var method = typeof(SkillCatalog).GetMethod("GetBirthPassiveSkills", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("SkillCatalog.GetBirthPassiveSkills is missing");
            var result = method.Invoke(catalog, new object[] { job }) as System.Collections.IEnumerable;
            if (result == null) return new object[0];
            return result.Cast<object>().ToArray();
        }

        private static T ReadMember<T>(object value, string name)
        {
            var type = value.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return (T)property.GetValue(value, null);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return (T)field.GetValue(value);
            throw new InvalidOperationException(type.FullName + "." + name + " is missing");
        }

        private static byte[] ReadFitnessGrowTypes(object definition)
        {
            var values = ReadMember<System.Collections.IEnumerable>(definition, "FitnessGrowTypes");
            return values == null ? new byte[0] : values.Cast<object>().Select(Convert.ToByte).ToArray();
        }

        private static byte[] ReadGrowTypeMaximumLevels(object definition)
        {
            var values = ReadMember<System.Collections.IEnumerable>(definition, "GrowTypeMaximumLevels");
            return values == null ? new byte[0] : values.Cast<object>().Select(Convert.ToByte).ToArray();
        }
    }
}
