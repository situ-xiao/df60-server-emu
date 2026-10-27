using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DF60A1
{
    internal static class GamePackets
    {
        private const byte CommonSkillSlotStart = 174;
        private const byte CommonSkillSlotEnd = 215;
        private static readonly byte[][] SkillClassSlotRanges =
        {
            // Slots 0-5 are the quick bar.  Each learned-skill class owns a
            // 42-slot panel beginning at slot 6.
            new byte[] { 6, 47 }, new byte[] { 48, 89 }, new byte[] { 90, 131 },
            new byte[] { 132, 173 }, new byte[] { CommonSkillSlotStart, CommonSkillSlotEnd }
        };

        // The Arad client renders 42 skill slots per category. The fifth
        // category (common) therefore owns slots 174-215. Slots 0-1 keep the
        // base-job active skills used by the existing character layout.
        // The same numeric ID has a different meaning in each job list.
        private static readonly CharacterSkillRecord[][] InitialSkillsByJob =
        {
            new[] // Swordman
            {
                new CharacterSkillRecord { Slot = 0, SkillId = 5, Level = 1 },
                new CharacterSkillRecord { Slot = 1, SkillId = 46, Level = 1 },
                new CharacterSkillRecord { Slot = 174, SkillId = 181, Level = 2 },
                new CharacterSkillRecord { Slot = 175, SkillId = 182, Level = 2 },
                new CharacterSkillRecord { Slot = 176, SkillId = 184, Level = 1 },
                new CharacterSkillRecord { Slot = 177, SkillId = 173, Level = 1 },
                new CharacterSkillRecord { Slot = 178, SkillId = 179, Level = 5 }
            },
            new[] // Fighter
            {
                new CharacterSkillRecord { Slot = 0, SkillId = 5, Level = 1 },
                new CharacterSkillRecord { Slot = 1, SkillId = 46, Level = 1 },
                new CharacterSkillRecord { Slot = 174, SkillId = 181, Level = 3 },
                new CharacterSkillRecord { Slot = 175, SkillId = 182, Level = 3 },
                new CharacterSkillRecord { Slot = 176, SkillId = 184, Level = 1 },
                new CharacterSkillRecord { Slot = 177, SkillId = 172, Level = 1 },
                new CharacterSkillRecord { Slot = 178, SkillId = 179, Level = 5 }
            },
            new[] // Gunner
            {
                new CharacterSkillRecord { Slot = 0, SkillId = 4, Level = 1 },
                new CharacterSkillRecord { Slot = 1, SkillId = 47, Level = 1 },
                new CharacterSkillRecord { Slot = 174, SkillId = 181, Level = 2 },
                new CharacterSkillRecord { Slot = 175, SkillId = 184, Level = 1 },
                new CharacterSkillRecord { Slot = 176, SkillId = 185, Level = 1 },
                new CharacterSkillRecord { Slot = 177, SkillId = 179, Level = 5 }
            },
            new[] // Mage
            {
                new CharacterSkillRecord { Slot = 0, SkillId = 11, Level = 1 },
                new CharacterSkillRecord { Slot = 1, SkillId = 12, Level = 1 },
                new CharacterSkillRecord { Slot = 174, SkillId = 181, Level = 3 },
                new CharacterSkillRecord { Slot = 175, SkillId = 182, Level = 3 },
                new CharacterSkillRecord { Slot = 176, SkillId = 184, Level = 2 },
                new CharacterSkillRecord { Slot = 177, SkillId = 187, Level = 1 },
                new CharacterSkillRecord { Slot = 178, SkillId = 179, Level = 5 }
            },
            new[] // Priest
            {
                new CharacterSkillRecord { Slot = 0, SkillId = 1, Level = 1 },
                new CharacterSkillRecord { Slot = 1, SkillId = 51, Level = 1 },
                new CharacterSkillRecord { Slot = 174, SkillId = 181, Level = 1 },
                new CharacterSkillRecord { Slot = 175, SkillId = 182, Level = 1 },
                new CharacterSkillRecord { Slot = 176, SkillId = 184, Level = 1 },
                new CharacterSkillRecord { Slot = 177, SkillId = 179, Level = 5 },
                new CharacterSkillRecord { Slot = 178, SkillId = 173, Level = 1 }
            }
        };

        public const byte Notification = 0;
        public const byte Command = 1;
        public const byte CheckConnection = 0;
        public const byte ChannelInfo = 1;
        public const byte UserInfo = 2;
        public const byte UserState = 3;
        public const byte Stamina = 4;
        public const byte DungeonPermission = 5;
        public const byte PartyInfo = 9;
        public const byte ItemList = 13;
        public const byte UpdateItemList = 14;
        public const byte SkillInfo = 19;
        public const byte AcceptableQuestList = 21;
        public const byte UserPosition = 22;
        public const byte UserArea = 23;
        public const byte AreaUsers = 24;
        public const byte UdpHost = 26;
        public const byte EnterSelectDungeonNotification = 27;
        public const byte DungeonInfo = 28;
        public const byte StartMap = 29;
        public const byte FinishLoadingNotification = 30;
        public const byte EnableClearDungeonNotification = 31; // 副本可结算/通关许可（55 协议确认）
        public const byte PlayResultNotification = 34;         // 通关结算（55 协议确认）
        public const byte ClearDungeonRewardNotification = 35; // 结算奖励（55 协议确认）
        public const byte MonsterDieNotification = 38; // 怪物死亡通知（55 协议确认，与 Arad 通知表 [38]=0x4217CE 吻合）
        public const sbyte NoChampionRewardOrdinal = -1;
        public const sbyte MaximumChampionRewardOrdinal = 11;
        public const byte AllDieMonsterNotification = 127; // 全怪秒杀/房间清场（reference server）
        public const byte GroundItemPickedUpNotification = 39;
        public const byte GroundItemSpawnedNotification = 40;
        public const byte MonsterDefeatedNotification = 42; // 通知 42 为逻辑实体通知（0xb5dcbc），非怪物死亡通知
        public const byte ExperienceGainedNotification = 37;
        public const byte FatigueNotification = 36;              // 疲劳值通知（Arad 参考服务端布局）
        public const byte ClientFeatureFlagsNotification = 120;  // 普通疲劳 HUD 模式（Arad 参考服务端布局）
        public const byte TutorialBootstrap = 136;
        public const byte Login = 1;
        public const byte Exit = 3;
        public const byte SelectCharacter = 4;
        public const byte CreateCharacter = 5;
        public const byte DeleteCharacter = 6;
        public const byte ReturnSelectCharacter = 7;
        public const byte GetUserInfo = 8;
        public const byte EnterSelectDungeon = 15;
        public const byte SelectDungeon = 16;
        public const byte SetUserPosition = 37;
        public const byte SetUserArea = 38;
        public const byte FinishLoading = 40;
        public const byte MonsterDeath = 42;
        public const byte PickupGroundItem = 46; // 拾取地面物品（Arad 客户端实测：body=seq u16 + runtimeId u16 + 4B；55 无此命令）
        public const byte UseSkill = 41;      // 使用技能（韩服 55 UseSkillCommand）
        public const byte GiveupGame = 45; // 放弃副本/返回城镇（韩服 55 协议确认；旧名 DungeonRoomClear 有误）
        public const byte DungeonItemAction = 47; // 副本内快捷使用物品：u16 slot + byte listType
        public const byte LeaveDungeonSelection = 142; // 地图选择界面“返回城镇”（Arad 客户端实测）
        public const byte SetPlayResult = 49; // 副本通关结算请求（韩服 55 协议确认）
        public const byte BuySkill = 31;      // 学习/升级技能（韩服 55 BuySkillCommand）
        public const byte AcceptQuest = 33;
        public const byte LeaveDungeonSelectionObserved = 146;
        public const byte UseTownItem = 32;   // Arad town item: request = seq:u16 + slot:u16
        public const byte GiveupQuest = 34;
        public const byte SetQuestTrigger = 35;
        public const byte FinishQuest = 36;
        public const byte ConsumeItems = 18;  // 使用消耗品（韩服 55 ConsumeItemsCommand）
        public const byte MoveItemSpace = 19; // 拖动物品/切换槽位（55 MoveItemSpaceCommand）
        public const byte ChangeSkillSlot = 28;    // 技能槽位调整（55 ChangeSkillSlotCommand）
        public const byte ChangeSkillQuickSlot = 30; // 技能快捷栏调整（55 ChangeSkillQuickSlotCommand）
        public const byte MoveRoom = 48;      // 换房（韩服 55 协议确认：body[2]=新房间X, body[3]=新房间Y）
        public const byte ScoreScrollState = 72; // 结算计分翻页请求（翻牌前，55 确认）
        public const byte CardSelectRightStateCommand = 73;
        public const byte SelectCard = 74;    // 翻牌选择（55 确认）
        public const byte Eplp = 75;          // 结算完成/离开（55 确认，state=1 时回城）
        public const byte BuyItem = 21;
        public const byte SellItem = 22;
        public const byte SetItemTradeState = 24;
        public const byte CeraShopBuy = 67;
        public const byte CeraUpdate = 53;
        public const byte TownInit82 = 82;
        public const byte TownInit130 = 130;
        public const byte ConfirmActiveModule = 153;
        public const byte SecurityCard = 161;
        public const ushort TutorialDungeonId = 10000;
        public const ushort TutorialMapId = 61000;
        public const ushort TutorialMonsterCount = 4;
        public const ushort MaximumFatigue = 188;

        public static GameServerPacket Check(byte[] token) { return new GameServerPacket(Notification, CheckConnection, (byte[])token.Clone()); }
        public static GameServerPacket Channel(byte[] token, string host = null, int port = 0)
        {
            // DNF.exe 1.0.1.9, sub_419420 NOTIPACKET_CHANNEL_INFO case 1,
            // consumes a 26-byte configuration record:
            //   uint32, uint32, byte, byte, uint32, uint32 count,
            //   [count * 0x104-byte address records], uint32, uint32.
            // The 55 client instead places its 16-byte challenge at offset 10;
            // doing that here turns the four trailing fields into arbitrary
            // ASCII integers and prevents the Arad client from sending LOGIN.
            // A zero-address local configuration is the smallest safe record.
            if (GameServerPacket.UseLegacyChannelHeader)
                return new GameServerPacket(Notification, ChannelInfo, BuildAradChannelInfo(host, port));
            return new GameServerPacket(Notification, ChannelInfo, new byte[26]);
        }

        private static byte[] BuildAradChannelInfo(string host, int port)
        {
            // ARAD 需要带一条 0x104 字节地址记录的频道配置（共 286 字节）。
            // 模板来自实测抓包：count=1，记录内含频道名、频道号、IP、端口。
            var payload = new byte[286];
            LittleEndian.WriteUInt32(payload, 14, 1); // address record count

            const int record = 18;
            WriteFixedAscii(payload, record + 0x00, "ch.11", 0x20);
            LittleEndian.WriteUInt32(payload, record + 0x26, 11); // channel number
            var address = string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" ? "127.0.0.1" : host;
            WriteFixedAscii(payload, record + 0x30, address, 0x10);
            var actualPort = port <= 0 ? 7001 : port;
            LittleEndian.WriteUInt32(payload, record + 0x40, (uint)actualPort);
            LittleEndian.WriteUInt32(payload, record + 0x44, (uint)actualPort);
            LittleEndian.WriteUInt32(payload, record + 0x48, 100); // max users
            return payload;
        }

        private static void WriteFixedAscii(byte[] buffer, int offset, string value, int width)
        {
            var bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            var count = Math.Min(bytes.Length, width);
            Buffer.BlockCopy(bytes, 0, buffer, offset, count);
        }
        public static GameServerPacket LoginOk()
        {
            // DNF.exe 1.0.1.9, sub_40F290 command-response case 1.
            // The success flag is followed by four bytes, three uint32 values
            // and two final bytes. A shorter body makes the client reader run
            // beyond the declared payload and leaves login state uninitialised.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); // command succeeded
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0); // channel/region mode
                writer.Write((byte)0);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write((byte)0);
                writer.Write((byte)0);
                return new GameServerPacket(Command, Login, stream.ToArray());
            }
        }
        public static GameServerPacket CommandResult(byte id, bool success) { return new GameServerPacket(Command, id, new byte[] { success ? (byte)1 : (byte)0 }); }
        public static GameServerPacket ClientConfigurationSaved(byte commandId)
        {
            return CommandResult(commandId, true);
        }
        public static GameServerPacket CommandError(byte id, byte error) { return new GameServerPacket(Command, id, new byte[] { 0, error }); }
        public static GameServerPacket TownInit82Empty()
        {
            // sub_40F290 command case 82:
            // success:u8, field1:u8, field2:u8, name:DSTR,
            // field3:u8, field4:u8, value1:u32, value2:u32, count:u8.
            // Even an empty name consumes its uint32 length, so the smallest
            // valid successful response is 18 bytes rather than 14.
            return new GameServerPacket(Command, TownInit82, new byte[18]
            {
                1, 0, 0,
                0, 0, 0, 0,
                0, 0,
                0, 0, 0, 0,
                0, 0, 0, 0,
                0
            });
        }
        public static GameServerPacket TownInit130Empty()
        {
            // sub_40F290 command case 130: success plus an empty entry count.
            return new GameServerPacket(Command, TownInit130, new byte[] { 1, 0 });
        }

        public static bool TryReadCreateCharacter(byte[] body, out byte job, out string name)
        {
            job = 0;
            name = null;

            // DNF.exe 1.0.1.9, sub_898E90 at 0x8995AC:
            //   BeginCommand(5)
            //   WriteBytes(&selectedJob, 1)
            //   WriteString(name) => uint32 byte length + raw bytes
            // PacketFrame.Body retains the client's leading ushort sequence,
            // so the command payload begins at body[2].
            if (body == null || body.Length < 7) return false;
            job = body[2];
            if (job > 4) return false;

            var length = LittleEndian.ReadUInt32(body, 3);
            if (length == 0 || length > 30 || body.Length - 7 < (int)length) return false;
            name = Encoding.GetEncoding(936).GetString(body, 7, (int)length);
            return true;
        }

        public static bool TryReadSelectDungeon(byte[] body, out ushort dungeonId, out byte difficulty, out byte option)
        {
            dungeonId = 0;
            difficulty = 0;
            option = 0;

            // Command 16 is sequence:u16, dungeonId:u16, difficulty:u8,
            // option:u8 in DNF.exe 1.0.1.9 (sub_8A1DD0).
            if (body == null || body.Length < 6) return false;
            dungeonId = LittleEndian.ReadUInt16(body, 2);
            difficulty = body[4];
            option = body[5];
            return true;
        }

        public static bool TryReadMonsterDeath(byte[] body, out ushort monsterId)
        {
            monsterId = 0;
            // Captured 1.0.1.9 command 42 packets are 22 bytes:
            // sequence:u16, monsterRuntimeId:u16, killerUserId:u32,
            // opaque death/combat fields.  Only the runtime ID is required
            // for this single-player tutorial room, but require the complete
            // known minimum layout before accepting the report.
            if (body == null || body.Length < 22) return false;
            monsterId = LittleEndian.ReadUInt16(body, 2);
            return true;
        }

        public static bool TryReadPickupGroundItem(byte[] body, out ushort runtimeId)
        {
            runtimeId = 0;
            if (body == null || body.Length < 4) return false;
            runtimeId = LittleEndian.ReadUInt16(body, 2);
            return runtimeId != 0 && runtimeId != ushort.MaxValue;
        }

        public static bool TryReadBuyItem(byte[] body, out ushort itemId, out int count)
        {
            itemId = 0;
            count = 1;
            if (body == null || body.Length < 4) return false;

            // Captured 1.0.1.9 packets use sequence:u16 + itemId:i32 + count:i32.
            // Keep the shorter u16 form for shop windows using the legacy writer.
            if (body.Length >= 6)
            {
                var encodedItemId = (int)LittleEndian.ReadUInt32(body, 2);
                if (encodedItemId > 0 && encodedItemId <= ushort.MaxValue)
                {
                    itemId = (ushort)encodedItemId;
                    // 1.0.1.9 inserts listType:u8 before count; the 55 writer
                    // omits it. Captured example: 1300 E02E0000 00 0F000000.
                    if (body.Length >= 11) count = (int)LittleEndian.ReadUInt32(body, 7);
                    else if (body.Length >= 10) count = (int)LittleEndian.ReadUInt32(body, 6);
                    return true;
                }
            }
            itemId = LittleEndian.ReadUInt16(body, 2);
            if (body.Length >= 6) count = LittleEndian.ReadUInt16(body, 4);
            else if (body.Length >= 5) count = body[4];
            return itemId != 0;
        }

        public static bool TryReadSellItem(byte[] body, out byte listType, out ushort slot, out int count)
        {
            listType = 0;
            slot = 0;
            count = 0;
            if (body == null) return false;
            if (body.Length >= 7 && (body[2] == 0 || body[2] == 3))
            {
                listType = body[2];
                var signedSlot = (short)LittleEndian.ReadUInt16(body, 3);
                count = (short)LittleEndian.ReadUInt16(body, 5);
                if (signedSlot < 0) return false;
                slot = (ushort)signedSlot;
                return count > 0;
            }
            if (body.Length == 5 && (body[2] == 0 || body[2] == 3))
            {
                listType = body[2];
                slot = body[3];
                count = body[4];
                return count > 0;
            }
            if (body.Length < 6) return false;
            var fallbackSlot = (short)LittleEndian.ReadUInt16(body, 2);
            count = (short)LittleEndian.ReadUInt16(body, 4);
            if (fallbackSlot < 0) return false;
            slot = (ushort)fallbackSlot;
            return count > 0;
        }

        public static bool TryReadSetItemTradeState(byte[] body, out byte listType, out ushort slot, out ushort count)
        {
            listType = 0;
            slot = 0;
            count = 0;
            if (body == null || body.Length < 7 || (body[2] != 0 && body[2] != 3)) return false;
            listType = body[2];
            slot = LittleEndian.ReadUInt16(body, 3);
            count = LittleEndian.ReadUInt16(body, 5);
            return true;
        }

        public static GameServerPacket CharacterList(IList<CharacterRecord> characters,
            Func<int, IEnumerable<InventoryItemRecord>> avatarProvider = null)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)2); writer.Write((ushort)characters.Count);
                foreach (var character in characters)
                {
                    // sub_419420 -> sub_8B3250 first consumes a DSTR here.
                    // dnf.str[0x26F] ("没有名字") is only the fallback used
                    // when the wire length is zero or invalid; it is not a
                    // replacement for the network field.
                    writer.Write((ushort)character.Slot);
                    var nameBytes = Encoding.GetEncoding(936).GetBytes(character.Name ?? string.Empty);
                    writer.Write((uint)nameBytes.Length);
                    writer.Write(nameBytes);
                    writer.Write(character.Job);
                    writer.Write(character.PrimaryGrowType);
                    writer.Write(character.Level);
                    writer.Write(character.SecondaryGrowState);
                    writer.Write((byte)0); // character state
                    writer.Write((byte)0); // equipped preview pair count
                    var avatarPreview = (avatarProvider == null
                            ? Enumerable.Empty<InventoryItemRecord>()
                            : avatarProvider(character.Id) ?? Enumerable.Empty<InventoryItemRecord>())
                        .Where(value => value != null && value.Slot < 9 && value.ItemId != 0)
                        .OrderBy(value => value.Slot).Take(byte.MaxValue).ToArray();
                    writer.Write((byte)avatarPreview.Length);
                    foreach (var avatar in avatarPreview)
                    {
                        writer.Write((byte)avatar.Slot);
                        writer.Write(avatar.ItemId);
                    }
                    writer.Write(0u);      // guild id
                    writer.Write((byte)0); // guild/member flag
                    writer.Write((byte)0); // restricted state
                    writer.Write((byte)0); // trailing client state
                    // sub_419420 initializes this preview-object state to 1
                    // before reading the wire value (sub_417D60).  Zero keeps
                    // the newly-created preview object out of its normal state.
                    writer.Write((byte)1);
                }
                return new GameServerPacket(Notification, UserInfo, stream.ToArray());
            }
        }

        // ARAD（日服 1.0.44.1 客户端）的 USERINFO mode 2 逐条解析器与
        // Arad 国服不同：它在 state 之后依次消费
        //   u8 pairCount + pair×14B + u8 guild + u32 guildFlag + u8 isOne
        //   + u8 nonzero + u8 lastFlag + u32 lastValue。
        // 旧布局只补到 previewState(1B)，导致 lastValue 吞掉下一条目的
        // slot/nameLen，从第 2 个角色起名字全部错位（界面显示“名前なし”）。
        public static GameServerPacket AradCharacterList(IList<CharacterRecord> characters)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)2);
                writer.Write((ushort)characters.Count);
                foreach (var character in characters)
                {
                    writer.Write((ushort)character.Slot);
                    var nameBytes = Encoding.GetEncoding(936).GetBytes(character.Name ?? string.Empty);
                    writer.Write((uint)nameBytes.Length);
                    writer.Write(nameBytes);
                    writer.Write(character.Job);
                    writer.Write(character.PrimaryGrowType);
                    writer.Write(character.Level);
                    writer.Write(character.SecondaryGrowState);
                    writer.Write((byte)0); // state
                    writer.Write((byte)0); // pair count（暂无外观预览）
                    writer.Write((byte)0); // guild
                    writer.Write(0u);      // guild flag
                    writer.Write((byte)0); // is-one flag
                    writer.Write((byte)0); // nonzero flag
                    writer.Write((byte)0); // last flag
                    writer.Write(0u);      // last value
                }
                return new GameServerPacket(Notification, UserInfo, stream.ToArray());
            }
        }

        public static GameServerPacket SelectOk(CharacterRecord character, ushort userId,
            IEnumerable<CharacterQuestRecord> activeQuests = null)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); // command succeeded

                // DNF.exe 1.0.1.9, sub_40F290 command-response case 4.
                // Keep this layout byte-exact: the three entries below are
                // ushort + uint, not the ushort + byte used by later guesses.
                writer.Write((uint)character.Id);
                writer.Write(userId);
                writer.Write((ushort)0);
                writer.Write((ushort)188);
                writer.Write((ushort)0);

                writer.Write((byte)0); // account/currency state
                writer.Write((uint)Math.Max(0, character.Cash));

                var quests = (activeQuests ?? Enumerable.Empty<CharacterQuestRecord>()).Take(3).ToArray();
                for (var index = 0; index < 3; index++)
                {
                    if (index < quests.Length)
                    {
                        writer.Write(quests[index].QuestId);
                        writer.Write((uint)quests[index].Trigger);
                    }
                    else
                    {
                        writer.Write(ushort.MaxValue);
                        writer.Write(0u);
                    }
                }

                writer.Write((byte)0); // variable byte + string entry count
                writer.Write(character.TownId);
                // Tutorials are globally disabled, so mark every client tutorial
                // module complete. Setting only bit 30 enters town mode but leaves
                // earlier invisible tutorial controllers able to capture keyboard
                // input even though mouse click movement still works.
                writer.Write(uint.MaxValue);
                return new GameServerPacket(Command, SelectCharacter, stream.ToArray());
            }
        }

        public static GameServerPacket CitrisSelectOk(CharacterRecord character, ushort userId)
        {
            // DNF.exe 1.0.0.75 command-response case 4 (0x4101D8).
            // The four ushort values become the client's local identity/state
            // globals.  It then consumes ten ushort+uint restore entries before
            // the final town and state fields; this is not compatible with the
            // three-entry Arad response above.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write((uint)character.Id);
                writer.Write(userId);
                writer.Write((ushort)0);
                writer.Write((ushort)188);
                writer.Write((ushort)0);
                writer.Write((byte)0); // variable byte+uint entry count
                writer.Write(0u);
                for (var index = 0; index < 10; index++)
                {
                    writer.Write((ushort)0);
                    writer.Write(uint.MaxValue);
                }
                writer.Write((byte)0); // variable byte+string entry count
                writer.Write(character.TownId);
                // Arad consumes the trailing byte-count list as completed
                // client modules (sub_4106B0 -> sub_9FCE10).  Mark every
                // module complete, matching the Arad SELECT_CHARACTER
                // uint.MaxValue mask, so notification 136 still activates the
                // town movement module without starting the tutorial.
                // The handler reads four fixed bytes after the ten restore
                // entries: count, id, flag, then the module count.  The flag
                // byte must stay zero; the module list follows it.
                writer.Write((byte)0);
                var completedModules = Enumerable.Range(0, 0x42).Select(value => (byte)value).ToArray();
                writer.Write((byte)completedModules.Length);
                foreach (var module in completedModules) writer.Write(module);
                writer.Write((ushort)0);
                return new GameServerPacket(Command, SelectCharacter, stream.ToArray());
            }
        }

        // ARAD（1.0.44.1 修改端）的 SELECT_CHARACTER 响应布局由命令 4
        // 处理器 0x40F931 反汇编确认：
        //   success:u8, charId:u32, localUserId:u16, val2:u16, val3:u16,
        //   val4:u16, countA:u8, valueA:u32, valueB:u32,
        //   10 × (u16,u32) 恢复条目（u32=0xFFFF 时跳过），
        //   countB:u8, townId:u8, moduleMask:u32。
        // moduleMask 必须按“教程是否完成”决定：新角色为 0，通知 136 才会
        // 启动教程（事件/漫画/地图）；完成后为 uint.MaxValue 直接进城镇。
        public static GameServerPacket AradSelectOk(CharacterRecord character, ushort userId,
            IEnumerable<CharacterQuestRecord> activeQuests = null)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write((uint)character.Id);
                writer.Write(userId);
                writer.Write((ushort)0);
                writer.Write((ushort)188);
                writer.Write((ushort)0);
                writer.Write((byte)0); // countA
                writer.Write(0u);      // valueA
                writer.Write(0u);      // valueB
                var quests = (activeQuests ?? Enumerable.Empty<CharacterQuestRecord>()).Take(10).ToArray();
                for (var index = 0; index < 10; index++)
                {
                    if (index < quests.Length)
                    {
                        writer.Write(quests[index].QuestId);
                        writer.Write((uint)quests[index].Trigger);
                    }
                    else
                    {
                        writer.Write((ushort)0);
                        writer.Write(uint.MaxValue);
                    }
                }
                // This legacy field is not command-217 client configuration.
                // Nonzero guessed records make the ARAD client over-read this
                // response and crash before town initialization completes.
                writer.Write((byte)0);
                writer.Write(character.TownId);
                writer.Write(GameServerPacket.TutorialEnabled && !character.TutorialStarted ? 0u : uint.MaxValue);
                return new GameServerPacket(Command, SelectCharacter, stream.ToArray());
            }
        }

        public static GameServerPacket TownCharacter(CharacterRecord character, ushort userId,
            IEnumerable<InventoryItemRecord> equipment = null,
            IEnumerable<InventoryItemRecord> avatars = null)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)0); writer.Write((ushort)1); writer.Write(userId);
                var nameBytes = Encoding.GetEncoding(936).GetBytes(character.Name ?? string.Empty);
                writer.Write((uint)nameBytes.Length);
                writer.Write(nameBytes);
                // Mode 0 uses the same leading DSTR as mode 2.  Once it has
                // been consumed, the following byte is passed to
                // sub_654E50(job) to construct the locally controlled actor.
                writer.Write(character.Job); writer.Write(character.PrimaryGrowType); writer.Write(character.Level);
                // DNF.exe 1.0.1.9, sub_419420 USERINFO mode 0.  This
                // record is longer than the otherwise similar DNF55 layout.
                // In particular the Arad reader consumes both appearance-list
                // counts and a 20-byte entity tail.  Ending at the first uint
                // makes it read bytes beyond the packet and eventually passes
                // that garbage to sub_417D60 as the town actor state.
                writer.Write(character.SecondaryGrowState);
                writer.Write((byte)0); // PvP/rating state (mode-0 only)
                writer.Write((byte)0); // user state
                // Mode-0 town appearance uses persistent equipment slot 9 for the
                // weapon (slot:u8 + itemId:u16 + state:u8).  The initial selection
                // packet may omit it, then the server replays this packet once the
                // town actor exists and whenever the weapon changes.
                var weaponAppearance = (equipment ?? Enumerable.Empty<InventoryItemRecord>())
                    .Where(value => value != null && value.Slot == 9 && value.ItemId != 0)
                    .OrderBy(value => value.Slot).Take(byte.MaxValue).ToArray();
                writer.Write((byte)weaponAppearance.Length);
                foreach (var equipped in weaponAppearance)
                {
                    writer.Write((byte)equipped.Slot);
                    writer.Write(equipped.ItemId);
                    writer.Write(equipped.State);
                }
                var avatarAppearance = (avatars ?? Enumerable.Empty<InventoryItemRecord>())
                    .Where(value => value != null && value.Slot < 9 && value.ItemId != 0)
                    .OrderBy(value => value.Slot).Take(byte.MaxValue).ToArray();
                writer.Write((byte)avatarAppearance.Length);
                foreach (var avatar in avatarAppearance)
                {
                    writer.Write((byte)avatar.Slot);
                    writer.Write(avatar.ItemId);
                    writer.Write(avatar.State);
                }
                writer.Write(0u);      // guild id
                writer.Write((byte)0); // guild/member flag
                writer.Write((ushort)0); // guild/title index
                writer.Write(0u);        // display name DSTR length
                writer.Write((byte)0);   // display-name flag
                writer.Write((byte)0);   // restricted state
                writer.Write((byte)0);   // trailing client state
                writer.Write(0u);        // local-player state
                writer.Write((byte)0);   // secondary display type
                writer.Write(0u);        // secondary display DSTR length
                writer.Write((byte)1);   // initial entity state
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
                return new GameServerPacket(Notification, UserInfo, stream.ToArray());
            }
        }

        // ARAD（日服 1.0.44.1）的 USERINFO mode 0（城镇角色实体）解析器
        // 比 Arad 国服长：名字之后依次消费 job、grow、level、secondary、
        // mode0 附加字节、state、pairCount、pair×14B、guild、guildFlag、
        // isOne、guildId、guildName DSTR、g1、nonzero、value1、flag2、
        // string2 DSTR、value2、flag3、flag4、value3、fA、vA、vB、vC、fB、
        // vD、fC。字段语义未全部确认，先按全零下发，保证解析器不越界。
        public static GameServerPacket AradTownCharacter(CharacterRecord character, ushort userId,
            IEnumerable<InventoryItemRecord> equipment = null,
            IEnumerable<InventoryItemRecord> avatars = null)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write(userId);
                var nameBytes = Encoding.GetEncoding(936).GetBytes(character.Name ?? string.Empty);
                writer.Write((uint)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(character.Job);
                writer.Write(character.PrimaryGrowType);
                writer.Write(character.Level);
                writer.Write(character.SecondaryGrowState);
                writer.Write((byte)0); // mode-0 附加字段
                writer.Write((byte)0); // state
                // ARAD mode 0 的 pair 列表用于构建城镇角色外观/装备：
                //   byte slot + u32 itemId + byte state + u32 耐久 + u32 seal。
                // 武器沿用持久化装备槽 9；不带任何 pair 时客户端角色部件为空，
                // 进教程/城镇后动作状态可能无法初始化。
                var appearancePairs = new List<InventoryItemRecord>();
                foreach (var equipped in (equipment ?? Enumerable.Empty<InventoryItemRecord>())
                    .Where(value => value != null && value.ItemId != 0 && value.Slot == 9)
                    .OrderBy(value => value.Slot))
                    appearancePairs.Add(equipped);
                if (appearancePairs.Count == 0)
                {
                    appearancePairs.Add(new InventoryItemRecord
                    {
                        Slot = 9,
                        ItemId = InitialWeaponId(character.Job),
                        Count = 1,
                        State = 0,
                        Durability = 35,
                        SealState = 0
                    });
                }
                foreach (var avatar in (avatars ?? Enumerable.Empty<InventoryItemRecord>())
                    .Where(value => value != null && value.ItemId != 0 && value.Slot < 9)
                    .OrderBy(value => value.Slot))
                    appearancePairs.Add(avatar);
                writer.Write((byte)appearancePairs.Count);
                foreach (var pair in appearancePairs)
                {
                    writer.Write((byte)pair.Slot);
                    writer.Write((uint)pair.ItemId);
                    writer.Write((byte)pair.State);
                    // Avatar rows use this field for the selected appearance
                    // option.  Option zero is valid and must not be rewritten
                    // to the equipment durability fallback.
                    writer.Write((uint)(pair.Slot < 9 ? pair.Durability :
                        (pair.Durability != 0 ? pair.Durability : 35)));
                    writer.Write(0u);
                }
                writer.Write((byte)0); // guild
                writer.Write(0u);      // guild flag
                writer.Write((byte)0); // is-one flag
                writer.Write(0u);      // guild id
                writer.Write(0u);      // guild name DSTR 长度
                writer.Write((byte)0); // g1
                writer.Write((byte)0); // nonzero
                writer.Write(0u);      // value1
                writer.Write((byte)0); // flag2
                writer.Write(0u);      // string2 DSTR 长度
                writer.Write(0u);      // value2
                writer.Write((byte)0); // flag3
                writer.Write((byte)0); // flag4
                writer.Write(0u);      // value3
                // ARAD initializes this parser field to 1 and passes it to the
                // town actor's state method (client 0x437B50).  Sending 0 keeps
                // a fully-created local actor out of the visible scene: the map
                // and NPCs render, but the player sprite does not.  The
                // reference server also sends 1 at the equivalent position.
                writer.Write((byte)1); // local town actor active/visible
                writer.Write(0u);      // vA
                writer.Write(0u);      // vB
                writer.Write(0u);      // vC
                writer.Write((byte)0); // fB
                writer.Write((ushort)0); // vD
                writer.Write((byte)0); // fC
                return new GameServerPacket(Notification, UserInfo, stream.ToArray());
            }
        }

        public static GameServerPacket AradCharacterDetails(ushort userId, CharacterRecord character)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write((ushort)1);
                writer.Write(userId);
                writer.Write(0u);
                var nameBytes = Encoding.GetEncoding(936).GetBytes(character.Name ?? string.Empty);
                writer.Write((uint)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write((byte)0); // item count
                writer.Write((byte)0); // skill count
                writer.Write((byte)0); // flag
                writer.Write((byte)0); // stat1
                writer.Write((byte)0); // stat2
                writer.Write((byte)0); // stat3
                return new GameServerPacket(Notification, UserInfo, stream.ToArray());
            }
        }

        public static IReadOnlyList<CharacterSkillRecord> InitialSkills(byte job)
        {
            if (job >= InitialSkillsByJob.Length) return Array.Empty<CharacterSkillRecord>();
            return InitialSkillsByJob[job].Select(value => new CharacterSkillRecord
            {
                Slot = value.Slot, SkillId = value.SkillId, Level = value.Level
            }).ToArray();
        }

        public static IReadOnlyList<CharacterSkillRecord> InitialSkills(byte job, SkillCatalog catalog)
        {
            if (catalog == null) return InitialSkills(job);
            var baseSkillIds = new HashSet<byte>(InitialSkills(job).Select(value => value.SkillId));
            var templates = InitialSkills(job).Where(value =>
            {
                var definition = catalog.Get(job, value.SkillId);
                return definition == null || (definition.FitnessGrowTypes.Contains(job) &&
                    (definition.Type != SkillType.Passive || definition.PurchaseCost == 0));
            }).ToList();
            foreach (var passive in catalog.GetBirthPassiveSkills(job))
                if (!templates.Any(value => value.SkillId == passive.SkillId))
                    templates.Add(new CharacterSkillRecord { SkillId = passive.SkillId, Level = 1 });
            var occupied = new HashSet<byte>();
            var result = new List<CharacterSkillRecord>();
            foreach (var template in templates)
            {
                var definition = catalog.Get(job, template.SkillId);
                byte first, last;
                var slot = template.Slot;
                var fixedQuickSlot = slot <= 5 && baseSkillIds.Contains(template.SkillId);
                if (!fixedQuickSlot && definition != null && TryGetSkillClassRange(definition.SkillClass, out first, out last) &&
                    (slot < first || slot > last || occupied.Contains(slot)))
                    slot = AllocateSkillSlot(occupied, first, last);
                if (occupied.Contains(slot)) continue;
                result.Add(new CharacterSkillRecord { Slot = slot, SkillId = template.SkillId, Level = template.Level == 0 ? (byte)1 : template.Level });
                occupied.Add(slot);
            }
            return result.OrderBy(value => value.Slot).ToArray();
        }

        public static ushort InitialWeaponId(byte job)
        {
            return new ushort[] { 27000, 29000, 31000, 33300, 35000 }[Math.Min(job, (byte)4)];
        }

        public static IReadOnlyList<ushort> InitialAvatarIds(byte job)
        {
            var baseId = new ushort[] { 39000, 43000, 47000, 51000, 55000 }[Math.Min(job, (byte)4)];
            return Enumerable.Range(0, 9).Select(index => checked((ushort)(baseId + index * 400))).ToArray();
        }

        public static List<CharacterSkillRecord> CurrentSkills(byte job, IEnumerable<CharacterSkillRecord> savedSkills)
        {
            var saved = (savedSkills ?? Enumerable.Empty<CharacterSkillRecord>()).ToList();
            var result = new List<CharacterSkillRecord>();
            var initial = InitialSkills(job);
            var initialIds = new HashSet<byte>(initial.Select(value => value.SkillId));
            var additional = saved
                .Where(value => !initialIds.Contains(value.SkillId))
                .GroupBy(value => value.SkillId)
                .Select(group => group.OrderByDescending(value => value.Level).First())
                .OrderBy(value => value.Slot)
                .ToList();
            // Learned skills already own their persisted panel slots.  A base
            // skill dragged to a quick slot is restored to its skill panel on
            // login, but its historical default slot may now be occupied by a
            // learned skill. Allocate another free panel slot instead of
            // producing duplicate slot_index rows and aborting character select.
            var occupied = new HashSet<byte>(additional.Select(value => value.Slot));
            foreach (var template in initial)
            {
                var persisted = saved.FirstOrDefault(value => value.SkillId == template.SkillId);
                var templateIsCommon = template.Slot >= CommonSkillSlotStart && template.Slot <= CommonSkillSlotEnd;
                var persistedInSamePanel = persisted != null && (templateIsCommon
                    ? persisted.Slot >= CommonSkillSlotStart && persisted.Slot <= CommonSkillSlotEnd
                    : persisted.Slot < 102);
                var slot = persistedInSamePanel ? persisted.Slot : template.Slot;
                if (occupied.Contains(slot))
                {
                    var first = templateIsCommon ? CommonSkillSlotStart : (byte)0;
                    var last = templateIsCommon ? CommonSkillSlotEnd : (byte)101;
                    var candidate = first;
                    while (candidate <= last && occupied.Contains(candidate)) candidate++;
                    if (candidate > last) continue;
                    slot = candidate;
                }
                result.Add(new CharacterSkillRecord
                {
                    Slot = slot,
                    SkillId = template.SkillId,
                    Level = persisted == null ? template.Level : Math.Max(template.Level, persisted.Level)
                });
                occupied.Add(slot);
            }
            foreach (var skill in additional)
                result.Add(new CharacterSkillRecord { Slot = skill.Slot, SkillId = skill.SkillId, Level = skill.Level == 0 ? (byte)1 : skill.Level });
            return result.OrderBy(value => value.Slot).ToList();
        }

        public static List<CharacterSkillRecord> CurrentSkills(byte job, IEnumerable<CharacterSkillRecord> savedSkills, SkillCatalog catalog)
        {
            if (catalog == null) return CurrentSkills(job, savedSkills);
            var saved = (savedSkills ?? Enumerable.Empty<CharacterSkillRecord>()).ToList();
            var initial = InitialSkills(job, catalog).ToList();
            // A persisted skill was already accepted for this character.  The
            // PVF fitness values describe grow types, not the base job number;
            // filtering them with job used to discard newly purchased subclass
            // skills.  That made the next purchase reuse an occupied panel slot.
            var known = saved.Where(value => catalog.Get(job, value.SkillId) != null)
                .GroupBy(value => value.SkillId)
                .Select(group => group.OrderByDescending(value => value.Level).First()).ToList();
            var unknown = saved.Where(value => catalog.Get(job, value.SkillId) == null)
                .GroupBy(value => value.SkillId)
                .Select(group => group.OrderByDescending(value => value.Level).First()).ToList();
            var result = new List<CharacterSkillRecord>();
            var occupied = new HashSet<byte>();
            foreach (var skill in unknown)
            {
                if (occupied.Contains(skill.Slot)) continue;
                result.Add(new CharacterSkillRecord { Slot = skill.Slot, SkillId = skill.SkillId, Level = skill.Level == 0 ? (byte)1 : skill.Level });
                occupied.Add(skill.Slot);
            }
            foreach (var skill in known.Where(value => !initial.Any(template => template.SkillId == value.SkillId)))
                AddCatalogSkill(result, occupied, catalog.Get(job, skill.SkillId), skill.SkillId, skill.Level, skill.Slot);
            foreach (var template in initial)
            {
                var persisted = known.FirstOrDefault(value => value.SkillId == template.SkillId);
                var level = persisted == null ? template.Level : (byte)Math.Max(template.Level, persisted.Level);
                AddCatalogSkill(result, occupied, catalog.Get(job, template.SkillId), template.SkillId, level,
                    persisted == null ? template.Slot : persisted.Slot);
            }
            return result.OrderBy(value => value.Slot).ToList();
        }

        // Keep source compatibility with the older reference-server helper.
        // Level and growType are used when resolving growth rewards, but do
        // not alter the panel normalization performed by this packet layer.
        public static List<CharacterSkillRecord> CurrentSkills(byte job, byte growType, byte level,
            IEnumerable<CharacterSkillRecord> savedSkills, SkillCatalog catalog)
        {
            if (catalog == null) return CurrentSkills(job, savedSkills);
            var compatible = (savedSkills ?? Enumerable.Empty<CharacterSkillRecord>())
                .Where(value =>
                {
                    var definition = catalog.Get(job, value.SkillId);
                    return definition == null || definition.FitnessGrowTypes.Contains(growType);
                });
            return CurrentSkills(job, compatible, catalog).Where(value =>
            {
                var definition = catalog.Get(job, value.SkillId);
                if (definition == null || definition.Type != SkillType.Passive || definition.PurchaseCost != 0)
                    return true;
                return growType < definition.GrowTypeMaximumLevels.Count &&
                    definition.GrowTypeMaximumLevels[growType] > 0;
            }).ToList();
        }

        private static void AddCatalogSkill(List<CharacterSkillRecord> result, HashSet<byte> occupied,
            SkillDefinition definition, byte skillId, byte level, byte preferredSlot)
        {
            var slot = preferredSlot;
            byte first, last;
            if (definition != null && TryGetSkillClassRange(definition.SkillClass, out first, out last) &&
                ((slot > 5 && (slot < first || slot > last)) || occupied.Contains(slot)))
                slot = AllocateSkillSlot(occupied, first, last);
            if (occupied.Contains(slot)) return;
            result.Add(new CharacterSkillRecord { Slot = slot, SkillId = skillId, Level = level == 0 ? (byte)1 : level });
            occupied.Add(slot);
        }

        internal static bool TryGetSkillClassRange(byte skillClass, out byte first, out byte last)
        {
            if (skillClass >= SkillClassSlotRanges.Length) { first = last = 0; return false; }
            first = SkillClassSlotRanges[skillClass][0];
            last = SkillClassSlotRanges[skillClass][1];
            return true;
        }

        private static byte AllocateSkillSlot(HashSet<byte> occupied, byte first, byte last)
        {
            for (var slot = first; slot <= last; slot++)
                if (!occupied.Contains((byte)slot)) return (byte)slot;
            return first;
        }

        public static GameServerPacket CharacterDetails(ushort userId, uint experience = 0u, byte job = 0,
            IEnumerable<InventoryItemRecord> equipment = null, IEnumerable<CharacterSkillRecord> savedSkills = null,
            CharacterStatBlock characterStats = null, SkillCatalog skillCatalog = null,
            IEnumerable<InventoryItemRecord> avatars = null)
        {
            if (characterStats == null) characterStats = new CharacterStatCatalog(null).Get(job, 0, 1);
            using (var statsStream = new MemoryStream())
            using (var stats = new BinaryWriter(statsStream))
            {
                // x10 fixed point: STR/VIT/INT/SPR are combined with equipment by
                // the client to produce both the displayed and combat attributes.
                stats.Write(characterStats.MaxHp); stats.Write(characterStats.MaxMp);
                stats.Write(characterStats.Strength); stats.Write(characterStats.Vitality);
                stats.Write(characterStats.Intelligence); stats.Write(characterStats.Spirit);
                // Arad 国服的角色属性块是 80 字节，不是韩服 55 的 78 字节。
                // 客户端角色虚函数 0x495D40 依次读取 +0x38:u16、+0x3A:u32，
                // 以及 +0x4C:u32；+0x3A 和 +0x4C 分别参与背包负重上限计算。
                // Arad 在 16 个保留 short 后比 55 多一个 u16 保留字段。
                stats.Write(characterStats.FireResistance); stats.Write(characterStats.WaterResistance);
                stats.Write(characterStats.DarkResistance); stats.Write(characterStats.LightResistance);
                for (var index = 0; index < 16; index++) stats.Write((short)0);
                stats.Write((ushort)0); // Arad 独有的保留字段（+0x38）
                stats.Write(characterStats.InventoryLimit); stats.Write(characterStats.HpRegen);
                stats.Write(characterStats.MpRegen); stats.Write(characterStats.MoveSpeed);
                stats.Write(characterStats.AttackSpeed); stats.Write(characterStats.CastSpeed);
                stats.Write(characterStats.HitRecovery); stats.Write(characterStats.JumpPower);
                stats.Write(characterStats.Weight);
                var statBytes = statsStream.ToArray();
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    // Arad CreateCurrentCharacterDetails 完整布局：
                    //   byte 1 + u16 1 + u16 userId + u32 experience
                    //   + u32 属性长度 + 属性块(78B)
                    //   + byte 装备数 + [byte slot + u16 itemId + u32 value + byte state + u16 dur]×N
                    //   + byte 技能数 + [byte skillId + byte level]×N
                    //   + byte 0
                    // 装备和技能必须随 USERINFO 下发：客户端据此初始化装备槽
                    // （含负重）和技能列表。此前装备数=技能数=0 导致负重 0、
                    // 技能无法拖动、装备进快捷栏。
                    writer.Write((byte)1); writer.Write((ushort)1); writer.Write(userId); writer.Write(experience);
                    writer.Write((uint)statBytes.Length); writer.Write(statBytes);
                    var equipmentItems = (equipment ?? new[] { new InventoryItemRecord
                    {
                        Slot = 9, ItemId = InitialWeaponId(job), Count = 1, Durability = 35
                    } }).Where(value => value != null && value.ItemId != 0);
                    var equippedItems = (avatars ?? Enumerable.Empty<InventoryItemRecord>())
                        .Where(value => value != null && value.ItemId != 0 && value.Slot < 9)
                        .Concat(equipmentItems)
                        .GroupBy(value => value.Slot)
                        .Select(group => group.First())
                        .OrderBy(value => value.Slot)
                        .Take(byte.MaxValue)
                        .ToArray();
                    writer.Write((byte)equippedItems.Length);
                    foreach (var equipped in equippedItems)
                    {
                        // USERINFO 角色详情的装备槽沿用持久化容器(list 3)
                        // 的槽号（武器=9、防具=10..17）。23:25 实测会话中
                        // 客户端以槽 9 正常进入新手教程并存活 6 分钟以上；
                        // 曾尝试映射为外观槽 0..8，反而使客户端在
                        // FinishLoaded 后无法继续进入教程场景。保持槽 9 原样
                        // 下发以与已验证会话一致。
                        writer.Write((byte)equipped.Slot);
                        // USERINFO mode 1 uses a 23-byte equipment row
                        // (client handler 0x4208F2..0x420976), not the compact
                        // inventory row. A short row shifts the next slot byte
                        // and makes the client's 0x17-entry slot array abort.
                        writer.Write((uint)equipped.ItemId);
                        writer.Write(equipped.Count);
                        writer.Write(equipped.State);
                        writer.Write(equipped.Durability);
                        writer.Write(uint.MaxValue); // no random-option record
                        writer.Write(0u);            // enchant
                        writer.Write((byte)0);       // amplify type
                        writer.Write((ushort)0);     // amplify value
                        // Client 0x420989 calls the list-3 item constructor.
                        // Avatar definitions take its conditional DSTR branch;
                        // normal equipment does not.
                        WriteAradItemExtension(writer, 3, equipped.Slot < 9);
                    }
                    // USERINFO has no slot/class field.  Creating class-4
                    // common skills here attaches them to the base-job page
                    // before SKILL_INFO can place them.  Let the slot-aware
                    // notification create common skills instead.
                    var characterSkills = CurrentSkills(job, savedSkills, skillCatalog)
                        .Where(value => value.SkillId < 169).ToArray();
                    writer.Write((byte)characterSkills.Length);
                    foreach (var skill in characterSkills)
                    {
                        writer.Write(skill.SkillId);
                        writer.Write(skill.Level);
                    }
                    writer.Write((byte)0); // 尾部
                    return new GameServerPacket(Notification, UserInfo, stream.ToArray());
                }
            }
        }

        public static IEnumerable<GameServerPacket> TownInitialization(byte job = 0, ushort sp = 0,
            IEnumerable<CharacterSkillRecord> savedSkills = null, IEnumerable<ushort> acceptableQuestIds = null,
            IEnumerable<KeyValuePair<ushort, byte>> dungeonPermissions = null, SkillCatalog skillCatalog = null)
        {
            yield return DungeonPermissions(dungeonPermissions);
            // 物品列表（通知 13）：单 listType=0 列表（金币 slot 0 + 物品）。
            // Arad 客户端条目为 21 字节（handler 0x4240C4）：
            //   slot u16 + itemId u32 + data u32 + extData u8
            //   + durability u16 + isSealed u8 + enchant u32
            //   + amplifyType u8 + amplifyValue u16
            using (var item = new MemoryStream())
            using (var writer = new BinaryWriter(item))
            {
                writer.Write((byte)0);          // listType 0
                // Placeholder only; GameServer replaces it with persisted items.
                // Never synthesize item id 0 because Arad renders it as gold.
                writer.Write((ushort)0);
                yield return new GameServerPacket(Notification, ItemList, item.ToArray());
            }
            // Arad 国服 SKILL_INFO（通知 19，handler 0x41E3C8）：
            //   u16 技能点 + byte 主技能数 + [byte 槽位, byte 技能id, byte 等级]×N
            //   + byte 辅助技能数（空响应即 {0,0,0,0}）。
            yield return DefaultSkills(job, sp, savedSkills, skillCatalog);
            yield return AcceptableQuests(acceptableQuestIds);
        }

        public static GameServerPacket DungeonPermissions(IEnumerable<KeyValuePair<ushort, byte>> permissions)
        {
            var rows = (permissions ?? Enumerable.Empty<KeyValuePair<ushort, byte>>())
                .Where(value => value.Key != 0)
                .OrderBy(value => value.Key)
                .Take(ushort.MaxValue)
                .ToArray();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)rows.Length);
                foreach (var row in rows)
                {
                    writer.Write(row.Key);
                    writer.Write((byte)Math.Min(3, (int)row.Value));
                }
                return new GameServerPacket(Notification, DungeonPermission, stream.ToArray());
            }
        }

        public static GameServerPacket AcceptableQuests(IEnumerable<ushort> questIds)
        {
            var ids = (questIds ?? Enumerable.Empty<ushort>()).Distinct().Take(byte.MaxValue).ToArray();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // ARAD 客户端的通知 21 handler（0x425F0F）先读一个保留字节，
                // 再读 u8 数量，随后才是 u16 任务 ID 列表；Arad 只读
                // u8 数量。这里按全局客户端类型自动选择布局。
                if (GameServerPacket.UseLegacyChannelHeader)
                    writer.Write((byte)0);
                writer.Write((byte)ids.Length);
                foreach (var questId in ids) writer.Write(questId);
                return new GameServerPacket(Notification, AcceptableQuestList, stream.ToArray());
            }
        }

        public static GameServerPacket AcceptQuestReply(ushort questId, byte trigger)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // DNF.exe 1.0.1.9 command 33 reads questId:u16,
                // trigger:u32 and then a byte-sized immediate-reward count.
                // Writing the later-client u8 trigger layout makes the reader
                // consume bytes beyond this packet and leaves the quest dialog
                // controller in an indeterminate state.
                writer.Write((byte)1);
                writer.Write(questId);
                writer.Write((uint)trigger);
                writer.Write((byte)0); // no immediate reward rows
                return new GameServerPacket(Command, AcceptQuest, stream.ToArray());
            }
        }

        public static GameServerPacket GiveupQuestReply(ushort questId)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write(questId);
                return new GameServerPacket(Command, GiveupQuest, stream.ToArray());
            }
        }

        public static GameServerPacket SetQuestTriggerReply(ushort questId, byte trigger)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Command 35 in the Arad executable also consumes a u32
                // trigger.  This is intentionally different from DNF55.
                writer.Write((byte)1);
                writer.Write(questId);
                writer.Write((uint)trigger);
                return new GameServerPacket(Command, SetQuestTrigger, stream.ToArray());
            }
        }

        public static GameServerPacket FinishQuestReply(ushort questId)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write(questId); writer.Write(0u);
                return new GameServerPacket(Command, FinishQuest, stream.ToArray());
            }
        }

        public static GameServerPacket DefaultSkills(byte job, ushort sp = 0, IEnumerable<CharacterSkillRecord> savedSkills = null,
            SkillCatalog skillCatalog = null)
        {
            var skills = CurrentSkills(job, savedSkills, skillCatalog);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(sp);
                // DNF.exe 1.0.1.9 only completes its character-loading
                // controller when all learned skills are in this primary
                // array and the secondary array is empty. Populating the
                // secondary array leaves the client permanently at Loading.
                writer.Write((byte)skills.Count);
                foreach (var skill in skills)
                {
                    writer.Write(skill.Slot); writer.Write(skill.SkillId); writer.Write(skill.Level);
                }
                writer.Write((byte)0);
                return new GameServerPacket(Notification, SkillInfo, stream.ToArray());
            }
        }

        public static GameServerPacket EnableClearDungeon()
        {
            // 通知 31（EnableClearDungeon）：空载荷。副本怪物全灭后下发，
            // 客户端据此完成教程剧情（救 NPC）并进入结算/返回城镇流程。
            return new GameServerPacket(Notification, EnableClearDungeonNotification, new byte[0]);
        }

        public static GameServerPacket SoloPartyInfo(ushort userId, ushort partyId = 1)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)1);
                writer.Write(partyId);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                for (var slot = 0; slot < 4; slot++)
                {
                    writer.Write(slot == 0 ? userId : ushort.MaxValue);
                    writer.Write((byte)0);
                }
                writer.Write((byte)0);
                return new GameServerPacket(Notification, PartyInfo, stream.ToArray());
            }
        }

        // DNF.exe 1.0.1.9 notification 34 handler @0x420B0F reads exactly
        // five bytes: the locally calculated result/rank code followed by four
        // party-leave flags.  The first byte is NOT a user id: the handler
        // compares it with DungeonResult::GetResultCode (@0x4D2410) and aborts
        // the whole result-model initialization when it does not match.
        public static GameServerPacket PlayResult(byte resultCode)
        {
            var payload = new byte[5];
            payload[0] = resultCode;
            // These are "left party" flags, not participant-presence flags.
            // A solo player has not left the result party, so all four flags
            // must remain zero.  Setting the local slot to one makes the card
            // screen render the bogus "character who left the party" entry.
            return new GameServerPacket(Notification, PlayResultNotification, payload);
        }

        public static byte ResultCodeFromScore(int score)
        {
            if (score >= 105) return 105;
            if (score >= 95) return 95;
            if (score >= 85) return 85;
            if (score >= 70) return 70;
            if (score >= 55) return 55;
            if (score >= 45) return 45;
            if (score >= 40) return 40;
            if (score >= 35) return 35;
            return 0;
        }

        public static bool IsValidResultCode(byte code)
        {
            return code == 0 || code == 35 || code == 40 || code == 45 || code == 55 ||
                code == 70 || code == 85 || code == 95 || code == 105;
        }

        // DNF.exe 1.0.1.9 notification 35 handler @0x420C23 reads:
        //   byte user + 9*u32 result fields
        //   byte inventoryUpdateCount + update rows (u16,u16,u32,u16)
        //   4 normal-card lists + u32 paid-card price
        //   4 optional-extra lists + 4 membership-extra lists
        // A card list is byte count followed by (u16 itemId,u32 count) rows.
        // Item id 0 is gold, matching the main inventory's currency record.
        //
        // The four "optional-extra" lists are the golden-card previews.
        // Handler 0x420C23 routes each item through call 0x87A7E0 (cardType=1),
        // which at the bottom sets `result_menu[slot].0x2b4 = 1` (this is the
        // "golden card unlocked" flag the UI consults).  Handlers fall back to
        // 0x417FF0 whenever the inner count is zero, and that branch only
        // stamps the unassigned `-1` sentinel; the unlock flag never flips,
        // the UI keeps the X-locked state, and the player can never click
        // any golden card.  We MUST populate each golden list with exactly
        // one preview row whose item id is not a currency category (that
        // would otherwise pop a numeric input dialog mid-settlement).
        public static GameServerPacket ClearDungeonReward(byte userId, uint clearExperience,
            uint[] freeCardGold, DungeonCardReward[] paidCardRewards, uint paidCardPrice)
        {
            if (freeCardGold == null || freeCardGold.Length != 4)
                throw new ArgumentException("Exactly four normal-card rewards are required.", "freeCardGold");
            if (paidCardRewards == null || paidCardRewards.Length != 4)
                throw new ArgumentException("Exactly four paid-card previews are required.", "paidCardRewards");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(userId);
                // Result field 0 is total clear EXP. With field 2 left at zero,
                // handler @0x420D17 stores this value as the base/visible clear
                // experience (field0 - field2) in the result model.
                writer.Write(clearExperience);
                for (var field = 1; field < 9; field++) writer.Write(0u);
                writer.Write((byte)0); // no immediate inventory update rows

                // Four free cards. Each contains one sane gold reward instead
                // of leaving the client to read uninitialized memory.
                for (var card = 0; card < 4; card++)
                {
                    writer.Write((byte)1);
                    writer.Write((ushort)0);
                    writer.Write(freeCardGold[card]);
                }

                // Paid-card price (rendered as "100 gold" on the four bottom
                // buttons) and one preview item per golden card.  The preview
                // only unlocks the button; the actual reward delivery still
                // happens in command 74 when the player confirms a slot and
                // pays.  Item id 0 would be parsed as currency and trigger a
                // rogue numeric prompt, so we exclude that case explicitly.
                writer.Write(paidCardPrice);
                for (var card = 0; card < 4; card++)
                {
                    var reward = paidCardRewards[card];
                    var itemId = reward != null && reward.ItemId != 0 ? reward.ItemId : (ushort)1004;
                    var count = reward != null && reward.ItemId != 0 ? reward.Count : 1u;
                    writer.Write((byte)1);
                    writer.Write(itemId);
                    writer.Write(count);
                }
                // Membership-only cards remain disabled for ordinary accounts.
                for (var card = 0; card < 4; card++) writer.Write((byte)0);
                return new GameServerPacket(Notification, ClearDungeonRewardNotification, stream.ToArray());
            }
        }

        public static GameServerPacket ClearDungeonReward(byte resultOwnerId, uint clearExperience,
            DungeonCardReward[][] freeCardRewards, DungeonCardReward[][] paidCardRewards,
            uint paidCardPrice)
        {
            if (freeCardRewards == null || freeCardRewards.Length != 4)
                throw new ArgumentException("Exactly four normal-card reward lists are required.", "freeCardRewards");
            if (paidCardRewards == null || paidCardRewards.Length != 4)
                throw new ArgumentException("Exactly four paid-card reward lists are required.", "paidCardRewards");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // This is the local result actor index, not the network user id.
                writer.Write(resultOwnerId);
                writer.Write(clearExperience);
                for (var field = 1; field < 9; field++) writer.Write(0u);
                writer.Write((byte)0);
                WriteCardRewardLists(writer, freeCardRewards);
                writer.Write(paidCardPrice);
                WriteCardRewardLists(writer, paidCardRewards);
                for (var card = 0; card < 4; card++) writer.Write((byte)0);
                return new GameServerPacket(Notification, ClearDungeonRewardNotification, stream.ToArray());
            }
        }

        private static void WriteCardRewardLists(BinaryWriter writer,
            DungeonCardReward[][] cardRewards)
        {
            for (var card = 0; card < 4; card++)
            {
                var rewards = (cardRewards[card] ?? new DungeonCardReward[0])
                    .Where(value => value != null && value.Count > 0).Take(byte.MaxValue).ToArray();
                writer.Write((byte)rewards.Length);
                foreach (var reward in rewards)
                {
                    writer.Write(reward.ItemId);
                    writer.Write(reward.Count);
                }
            }
        }

        // ---- 结算翻牌流程（韩服 55 协议确认）----
        // 命令 49 结算后客户端依次请求：
        //   72 ScoreScroll -> 回 Command 72 {1} + Command 73 {1}（翻牌资格）
        //   74 SelectCard  -> 回 Command 74 {1, 选中索引}
        //   75 Eplp        -> 回 Command 75 {1, state, option}，state=1 后回城
        public static GameServerPacket ScoreScrollStateReply() { return new GameServerPacket(Command, ScoreScrollState, new byte[] { 1 }); }
        public static GameServerPacket CardSelectRightState()
        {
            // Command 73 handler @0x41585D reads four u16 rights after success.
            // Handler computes one canContinue flag and clears it if ANY value
            // is zero. Result-menu code @0x8777B5 then blocks retry/another
            // dungeon and displays the fatigue-zero warning. These are four
            // prerequisites, not party-member slots.
            return new GameServerPacket(Command, CardSelectRightStateCommand,
                new byte[] { 1, 1, 0, 1, 0, 1, 0, 1, 0 });
        }
        public static GameServerPacket SelectCardReply(byte resultOwnerId, byte freeCardIndex, byte paidCardIndex,
            ushort paidRewardItemId = 0, uint paidRewardCount = 0)
        {
            // Command 74 handler @0x4158B5 reads four card rows after success:
            // freeOwner:i8, paidOwner:i8, bonusCount:u8,
            // bonuses:(u16,u32)*N.  0xFF is the unassigned sentinel.  Writing
            // zero for an unassigned owner makes the client manufacture party
            // members; writing zero as paidOwner for a free selection also
            // duplicates the chosen card in the golden-card row.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                for (byte card = 0; card < 4; card++)
                {
                    writer.Write((byte)(card == freeCardIndex ? resultOwnerId : byte.MaxValue));
                    writer.Write((byte)(card == paidCardIndex ? resultOwnerId : byte.MaxValue));
                    // Notification 35 already populated the selected face.
                    // Repeating rewards here creates a third floating card.
                    writer.Write((byte)0);
                }
                return new GameServerPacket(Command, SelectCard, stream.ToArray());
            }
        }
        // 使用消耗品响应（韩服 55 CreateConsumeItemsReply）：
        //   success + listType + count + [u16 slot + u32 consumedCount]×N
        public static GameServerPacket ConsumeItemsReply(byte listType, IEnumerable<InventoryConsumptionRecord> consumptions)
        {
            var entries = (consumptions ?? Enumerable.Empty<InventoryConsumptionRecord>()).Take(byte.MaxValue).ToArray();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);       // success
                writer.Write(listType);
                writer.Write((byte)entries.Length);
                foreach (var entry in entries)
                {
                    writer.Write(entry.Slot);
                    writer.Write(entry.ConsumedCount);
                }
                return new GameServerPacket(Command, ConsumeItems, stream.ToArray());
            }
        }

        public static GameServerPacket UseTownItemReply(ushort slot, byte effectType, uint amount, ushort levelUpSkillPoints)
        {
            // DNF.exe command 32 handler @0x412C87 reads, after success:
            // slot:u16, effectType:u8, amount:u32, levelUpSkillPoints:u16.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write(slot);
                writer.Write(effectType);
                writer.Write(amount);
                writer.Write(levelUpSkillPoints);
                return new GameServerPacket(Command, UseTownItem, stream.ToArray());
            }
        }

        public static bool TryReadConsumeItems(byte[] body, out byte listType, out ConsumeItemRequestRecord[] entries)
        {
            listType = byte.MaxValue;
            entries = new ConsumeItemRequestRecord[0];
            if (body == null || body.Length < 4) return false;
            listType = body[2];
            var count = body[3];
            if (count == 0 || count > 32) return false;

            // ARAD 1.0.1.9 sends 10-byte rows:
            //   operation:u16, slot:u16, itemId:u16, count:q16.16
            // followed by an opaque action-context tail.  The older parser
            // treated itemId as a raw count, so ordinary consumables and cube
            // costs were never decremented correctly.  Keep the historical
            // 8-byte layout as a fallback for the other supported client.
            var aradRows = body.Length >= 4 + count * 10;
            var rowSize = aradRows ? 10 : 8;
            if (body.Length < 4 + count * rowSize) return false;
            entries = new ConsumeItemRequestRecord[count];
            for (var index = 0; index < count; index++)
            {
                var offset = 4 + index * rowSize;
                var first = LittleEndian.ReadUInt16(body, offset);
                var second = LittleEndian.ReadUInt16(body, offset + 2);
                // Some Arad builds use slot,operation while the 55 layout
                // uses operation,slot. A slot is never a small operation.
                var operation = first;
                var slot = second;
                if (first >= 3 && second <= 3)
                {
                    operation = second;
                    slot = first;
                }
                entries[index] = new ConsumeItemRequestRecord
                {
                    Operation = operation,
                    Slot = slot,
                    ItemId = aradRows ? LittleEndian.ReadUInt16(body, offset + 4) : (ushort)0,
                    RequestedCount = aradRows
                        ? DecodeAradItemCount(LittleEndian.ReadUInt32(body, offset + 6))
                        : LittleEndian.ReadUInt32(body, offset + 4)
                };
            }
            return true;
        }

        private static uint DecodeAradItemCount(uint value)
        {
            // The legacy ARAD client represents integral item counts as Q16.16
            // (1 == 0x00010000).  Accept plain integers too so this parser can
            // still consume packets produced by the reference-version client.
            if (value != 0 && (value & 0xFFFFu) == 0 && (value >> 16) != 0)
                return value >> 16;
            return value;
        }

        public static GameServerPacket BuyItemReply(byte listType, ushort slot)
        {
            // Command 21 success layout: success, list type, destination slot.
            return new GameServerPacket(Command, BuyItem,
                new byte[] { 1, listType, checked((byte)slot) });
        }

        public static GameServerPacket CeraShopBuyReply(int shopCategory, uint commodityNo,
            ushort itemId, uint quantity)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                // DNF.exe command 67 @0x415060 reads a one-byte purchase
                // state before the 32-bit shop category.  State 2 is an
                // ordinary self-purchase; state 1 enters the gift-result path
                // and leaves the normal cash-shop input lock in place.
                writer.Write((byte)2);
                writer.Write(shopCategory);
                writer.Write(commodityNo);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(itemId);
                writer.Write((ushort)0);
                // This is a separate optional activity reward, not the item
                // being purchased.  The inventory/avatars are refreshed by
                // their normal notifications after this command response.
                writer.Write(ushort.MaxValue);
                writer.Write(0u);
                return new GameServerPacket(Command, CeraShopBuy, stream.ToArray());
            }
        }

        public static GameServerPacket CeraShopBuyError(byte errorCode, int shopCategory = 0, uint commodityNo = 0)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Failure has its own state byte.  Omitting it shifts every
                // following field in the 2008 client command-67 handler.
                writer.Write((byte)0); writer.Write((byte)0); writer.Write(errorCode);
                writer.Write(shopCategory); writer.Write(commodityNo);
                writer.Write(0u); writer.Write(0u); writer.Write((ushort)0);
                return new GameServerPacket(Command, CeraShopBuy, stream.ToArray());
            }
        }

        public static GameServerPacket CeraBalance(uint cash)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write(cash);
                return new GameServerPacket(Notification, CeraUpdate, stream.ToArray());
            }
        }

        public static GameServerPacket SellItemReply(uint gold, uint victoryPoints = 0, uint cash = 0)
        {
            // Command 22 reads three u32 balances and an item-effect count.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write(gold);
                writer.Write(victoryPoints);
                writer.Write(cash);
                writer.Write((byte)0);
                return new GameServerPacket(Command, SellItem, stream.ToArray());
            }
        }

        public static GameServerPacket SetItemTradeStateReply(uint gold, byte listType, ushort slot, ushort remainingCount)
        {
            // DNF.exe command 24 @0x4119B7 consumes updatedGold:u32,
            // listType:u8, slot:u16 and remainingCount:u16 after success.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write(gold);
                writer.Write(listType);
                writer.Write(slot);
                writer.Write(remainingCount);
                return new GameServerPacket(Command, SetItemTradeState, stream.ToArray());
            }
        }

        // Arad command 47 handler @0x4141FE reads success 后的 u16 slot 与
        // byte listType。短响应会使读取器越过包体，导致副本内卡死/闪退。
        public static GameServerPacket DungeonItemActionReply(ushort slot, byte listType)
        {
            return new GameServerPacket(Command, DungeonItemAction,
                new byte[] { 1, (byte)slot, (byte)(slot >> 8), listType });
        }

        public static GameServerPacket UpdateItem(byte listType, ushort slot, InventoryItemRecord item)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(listType);
                writer.Write((ushort)1);
                // Notification 14 handler 0x424420 consumes the same full
                // 21-byte row as notification 13. A compact row shifts the
                // count and every following field.
                WriteAradItemRow(writer, slot,
                    item == null ? uint.MaxValue : item.ItemId,
                    item == null ? 0u : item.Count,
                    item == null ? (byte)0 : item.State,
                    item == null ? (ushort)0 : item.Durability,
                    item == null ? (byte)0 : item.SealState);
                WriteAradItemExtension(writer, listType);
                return new GameServerPacket(Notification, UpdateItemList, stream.ToArray());
            }
        }

        internal static void WriteAradItemRow(BinaryWriter writer, ushort slot, uint itemId,
            uint count, byte state, ushort durability, byte sealState)
        {
            writer.Write(slot);
            writer.Write(itemId);
            writer.Write(count);
            writer.Write(state);
            writer.Write(durability);
            writer.Write(sealState);
            writer.Write(0u);        // enchant
            writer.Write((byte)0);   // amplify type
            writer.Write((ushort)0); // amplify value
        }

        // ARAD.exe routes list type 1 through its avatar-item decoder after
        // consuming the common row. USERINFO mode 1 also calls that decoder
        // conditionally for avatar items in list type 3. In both cases it reads
        // one additional DSTR (u32 byte length followed by the bytes). An empty
        // DSTR is sufficient, but omitting its length makes the next row's
        // slot/item id look like a huge string and crashes the client.
        internal static void WriteAradItemExtension(BinaryWriter writer, byte listType,
            bool isAvatar = false)
        {
            if (listType == 1 || (listType == 3 && isAvatar)) writer.Write(0u);
        }

        // Arad UPDATE_ITEM_LIST entry layout (handler 0x425C6E):
        //   listType:u8, count:u16,
        //   slot:u16, itemId:u32, data:u32, extData:u8,
        //   durability:u16, isSealed:u8, enchant:u32,
        //   amplifyType:u8, amplifyValue:u16
        public static GameServerPacket CitrisUpdateItem(byte listType, ushort slot, InventoryItemRecord item)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(listType);
                writer.Write((ushort)1);
                // Arad uses equipment type 0 for the weapon, unlike the
                // Arad persistent list slot 9.
                writer.Write(slot == 9 ? (ushort)0 : slot);
                writer.Write(item == null ? 0u : (uint)item.ItemId);
                writer.Write(item == null ? 0u : item.Count);
                writer.Write(item == null ? (byte)0 : item.State);
                writer.Write(item == null ? (ushort)0 : item.Durability);
                writer.Write(item == null ? (byte)0 : item.SealState);
                writer.Write(0u); // enchant
                writer.Write((byte)0); // amplify type
                writer.Write((ushort)0); // amplify value
                WriteAradItemExtension(writer, listType);
                return new GameServerPacket(Notification, UpdateItemList, stream.ToArray());
            }
        }

        public static GameServerPacket EplpReply(byte state, byte option) { return new GameServerPacket(Command, Eplp, new byte[] { 1, state, option }); }

        // Arad command 19 handler is at 0x410D15. After the success byte it
        // reads sourceList:u8, sourceSlot:u16, moveCount:u32,
        // destinationList:u8 and destinationSlot:u16 (11 bytes total).
        public static GameServerPacket MoveItemSpaceReply(byte sourceList, ushort sourceSlot, uint moveCount, byte destinationList, ushort destinationSlot)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write(sourceList);
                writer.Write(sourceSlot);
                writer.Write(moveCount);
                writer.Write(destinationList);
                writer.Write(destinationSlot);
                return new GameServerPacket(Command, MoveItemSpace, stream.ToArray());
            }
        }

        // On failure the same command handler still consumes the error code
        // and both list identifiers. A generic two-byte CommandError leaves
        // the client reader beyond the packet body.
        public static GameServerPacket MoveItemSpaceError(byte error, byte sourceList, byte destinationList)
        {
            return new GameServerPacket(Command, MoveItemSpace,
                new byte[] { 0, error, sourceList, destinationList });
        }

        // 以下为 ref server 的背包/道具命令（整理 20、丢弃 50）解析与回复。
        public static bool TryReadDropItem(byte[] body, out ushort x, out ushort y, out byte listType, out ushort slot, out uint count)
        {
            x = 0; y = 0; listType = byte.MaxValue; slot = 0; count = 0u;
            if (body == null || body.Length != 13) return false;
            x = LittleEndian.ReadUInt16(body, 2);
            y = LittleEndian.ReadUInt16(body, 4);
            listType = body[6];
            slot = LittleEndian.ReadUInt16(body, 7);
            count = LittleEndian.ReadUInt32(body, 9);
            return (listType == 0 || listType == 3) && count != 0;
        }

        public static GameServerPacket DropItemReply(byte listType, ushort slot, uint remainingCount)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write(listType);
                writer.Write(slot);
                writer.Write(remainingCount);
                return new GameServerPacket(Command, 50, stream.ToArray());
            }
        }

        public static GameServerPacket DropItemError(byte error, byte listType)
        {
            return new GameServerPacket(Command, 50, new byte[] { 0, error, listType });
        }

        public static bool TryReadSortItem(byte[] body, out byte listType)
        {
            listType = byte.MaxValue;
            if (body == null || body.Length < 3) return false;
            listType = body[2];
            return listType == 0 || listType == 1 || listType == 2 || listType == 7;
        }

        public static GameServerPacket SortItemReply(byte listType)
        {
            return new GameServerPacket(Command, 20, new byte[] { 1, listType });
        }

        public static GameServerPacket SortItemError(byte listType, byte error)
        {
            return new GameServerPacket(Command, 20, new byte[] { 0, error, listType });
        }

        public static GameServerPacket StorageItemList(IEnumerable<InventoryItemRecord> items)
        {
            var snapshot = (items ?? Enumerable.Empty<InventoryItemRecord>())
                .Where(value => value != null && value.Slot < 120 && value.ItemId != 0 && value.Count != 0)
                .OrderBy(value => value.Slot).ToList();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)2);               // warehouse item-list type
                writer.Write((ushort)120);           // level 7 (diamond safe), the client maximum
                writer.Write((ushort)snapshot.Count);
                foreach (var value in snapshot)
                    WriteAradItemRow(writer, value.Slot, value.ItemId, value.Count,
                        value.State, value.Durability, value.SealState);
                return new GameServerPacket(Notification, ItemList, stream.ToArray());
            }
        }

        // 技能槽位调整响应（55 CreateChangeSkillSlotReply）：{1, u16 skillOrSlotIndex, byte destSlot}
        public static GameServerPacket ChangeSkillSlotReply(ushort skillOrSlotIndex, byte destinationSlot)
        {
            return new GameServerPacket(Command, ChangeSkillSlot, new byte[] { 1, (byte)skillOrSlotIndex, (byte)(skillOrSlotIndex >> 8), destinationSlot });
        }

        // 技能快捷栏调整响应（55 CreateChangeSkillQuickSlotReply）：{1, sourceSlot, destSlot}
        public static GameServerPacket ChangeSkillQuickSlotReply(byte sourceSlot, byte destinationSlot)
        {
            return new GameServerPacket(Command, ChangeSkillQuickSlot, new byte[] { 1, sourceSlot, destinationSlot });
        }

        // Arad command 31 handler @0x412A47：success 后读取
        // u16 剩余SP、byte 技能槽、byte 技能ID、byte 新等级。
        public static GameServerPacket BuySkillReply(ushort remainingSp, byte slot, byte skillId, byte level)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);       // success
                writer.Write(remainingSp);   // u16 剩余 SP
                writer.Write(slot);
                writer.Write(skillId);
                writer.Write(level);
                return new GameServerPacket(Command, BuySkill, stream.ToArray());
            }
        }

        public static GameServerPacket BuySkillFailureReply()
        {
            return new GameServerPacket(Command, BuySkill, new byte[] { 0 });
        }

        // 使用技能响应（韩服 55 CreateUseSkillReply）：Command 41 {1}。
        public static GameServerPacket UseSkillReply() { return new GameServerPacket(Command, UseSkill, new byte[] { 1 }); }

        // ---- 洛兰（dungeon 1）副本包构造 ----
        // 迷宫 3x2：start (0,0)、boss (2,1)；地图与怪物数见 map/map.lst
        // （Lorien/s0.map=map1 3怪、Lorien/4.map=map5 5怪、Lorien/6.map=map6 6怪、
        //  Lorien/8.map=map8 4怪、Lorien/7.map=map7 5怪、Lorien/b1.map=map13 7怪）。
        public static GameServerPacket LorienDungeonInfo(byte difficulty, ushort dungeonId = 1, byte bossX = 2, byte bossY = 1)
        {
            return DungeonIndexInfo(difficulty, dungeonId, 0, bossX, bossY);
        }

        public static GameServerPacket LorienDungeonInfo(byte difficulty, ushort dungeonId, byte mazeIndex, byte bossX, byte bossY)
        {
            return DungeonIndexInfo(difficulty, dungeonId, mazeIndex, bossX, bossY);
        }

        // DUNGEON_INFO assigns the active dungeon index in the client. Keep
        // this generic API separate from the legacy Lorien-named wrappers.
        public static GameServerPacket DungeonIndexInfo(byte difficulty, ushort dungeonId, byte mazeIndex, byte bossX, byte bossY)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(dungeonId);   // Full protocol u16 dungeon id; do not truncate ids above 255.
                writer.Write(difficulty);
                writer.Write(mazeIndex);   // selected [maze info] layout
                writer.Write(bossX);       // boss room X
                writer.Write(bossY);       // boss room Y
                writer.Write((byte)0);
                writer.Write((byte)0);
                return new GameServerPacket(Notification, DungeonInfo, stream.ToArray());
            }
        }

        // Entering a dungeon must use one selected run for both client-side
        // assignments: DUNGEON_INFO for dungeon index, then START_MAP for map.
        public static GameServerPacket[] DungeonEntry(ushort dungeonId, byte difficulty, byte mazeIndex,
            byte bossX, byte bossY, byte roomX, byte roomY, ushort mapId,
            IReadOnlyList<KeyValuePair<ushort, DungeonCatalog.MonsterRow>> monsters, int level)
        {
            return new[]
            {
                DungeonIndexInfo(difficulty, dungeonId, mazeIndex, bossX, bossY),
                DungeonStartMap(roomX, roomY, mapId, monsters, level)
            };
        }

        // 洛兰系副本 START_MAP（dungeon 1 = Lorien 3x2 迷宫、dungeon 2 =
        // LorienInside 两房间）。洛兰 boss 房（map 13）最后一只为
        // monster.lst 2（GoblinThrower 投掷哥布林，b1.map 实测），
        // 其余房间均为 monster.lst 1（Goblin）。
        public static GameServerPacket LorienStartMap(byte roomX, byte roomY, byte dungeonId = 1, byte difficulty = 0)
        {
            ushort mapId;
            byte count;
            byte level;
            bool isBoss;
            ushort[] monsterIds;
            var bossMonsterRow = -1;
            if (dungeonId == 2)
            {
                // Script.pvf dungeon/act1/lorieninside.dgn, first maze:
                //   BB MM AA
                //   BB PP EE    boss=(0,1)
                //   BB GG AA    start=(0,2)
                // AA cells are closed. Map ids and monster rows below come
                // directly from map/lorien/*.map. Thrower goblins in normal
                // rooms remain normal; only monster 11 in b103 is the boss.
                level = checked((byte)(2 + Math.Min(3, (int)difficulty)));
                isBoss = roomX == 0 && roomY == 1;
                if (roomX == 0 && roomY == 2) { mapId = 101; monsterIds = new ushort[] { 1, 1, 2, 1, 2 }; }
                else if (roomX == 1 && roomY == 2) { mapId = 111; monsterIds = new ushort[] { 1, 1, 1, 2, 2 }; }
                else if (roomX == 1 && roomY == 1) { mapId = 107; monsterIds = new ushort[] { 1, 1, 2, 1, 1, 2 }; }
                else if (roomX == 2 && roomY == 1) { mapId = 102; monsterIds = new ushort[] { 1, 2, 2, 1 }; }
                else if (roomX == 1 && roomY == 0) { mapId = 109; monsterIds = new ushort[] { 2, 2, 2, 2 }; }
                else if (roomX == 0 && roomY == 0) { mapId = 116; monsterIds = new ushort[] { 2, 1, 1, 2, 2 }; }
                else if (isBoss) { mapId = 119; monsterIds = new ushort[] { 2, 1, 2, 11, 2, 2 }; bossMonsterRow = 3; }
                else throw new ArgumentOutOfRangeException("roomX/roomY", "LorienInside room is closed");
                count = checked((byte)monsterIds.Length);
            }
            else
            {
                var index = roomY == 0 ? roomX : roomX + 3;
                mapId = new ushort[] { 1, 5, 6, 8, 7, 13 }[index];
                count = new byte[] { 3, 5, 6, 4, 5, 7 }[index];
                level = checked((byte)(1 + Math.Min(3, (int)difficulty)));
                isBoss = index == 5;
                monsterIds = new ushort[count];
                for (var row = 0; row < count; row++) monsterIds[row] = 1;
                // map/lorien/b1.map marks row 4 (not the last row) as the boss.
                if (isBoss) { monsterIds[4] = 2; bossMonsterRow = 4; }
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(roomX);
                writer.Write(roomY);
                writer.Write(0u);          // random seed
                writer.Write((byte)1);     // full first-visit room state follows
                writer.Write(mapId);
                writer.Write(count);
                for (byte row = 0; row < count; row++)
                {
                    var isBossMonster = isBoss && row == bossMonsterRow;
                    writer.Write(row);          // map [monster] row
                    // 注意：runtime unique ID 必须是 u16！教程实现用
                    // (ushort)index。此处若写成 byte（writer.Write(row)），
                    // 每行 8 字节导致客户端按 9 字节/行解析时整体错位，
                    // 尾部标志读到错位数据 -> 客户端崩溃（洛兰卡死闪退）。
                    writer.Write((ushort)row);  // runtime unique ID
                    writer.Write(monsterIds[row]);
                    writer.Write(level);        // basis level
                    // type：0=普通、1=Champion（精英）、2=不灭精英、3=Boss
                    // （用户实测 type=1 显示"精英"、type=2 显示"不灭"，BOSS 用 3）。
                    writer.Write((byte)(isBossMonster ? 3 : 0));
                    writer.Write((byte)0);      // not a box monster
                    writer.Write((byte)0);      // box index
                }
                writer.Write((byte)0);      // no passive-object records
                writer.Write((byte)0);      // final room-state flag
                return new GameServerPacket(Notification, StartMap, stream.ToArray());
            }
        }

        // 通用 START_MAP：房间 mapId 与怪物行来自 DungeonCatalog（PVF 数据）。
        // 与 LorienStartMap 的 Arad 布局完全一致：每行 9 字节
        // （byte row + u16 uid + u16 monsterId + byte level + byte type +
        //  byte box + byte boxidx），type=3 表示 Boss。
        // ARAD's START_MAP handler treats the byte after the random seed as a
        // room operation, rather than a cosmetic flag.  State 0 enters an
        // already cached coordinate without parsing a replacement map; states
        // 1 and 2 parse a complete room definition and rebuild the map.
        public const byte StartMapCachedRoomState = 0;
        public const byte StartMapFirstVisitRoomState = 1;
        public const byte StartMapReplaceRoomState = 2;

        public static GameServerPacket DungeonStartMap(byte roomX, byte roomY, ushort mapId,
            IReadOnlyList<KeyValuePair<ushort, DungeonCatalog.MonsterRow>> monsters, int level)
        {
            return DungeonStartMap(roomX, roomY, mapId, monsters, level,
                StartMapFirstVisitRoomState, null);
        }

        // Passive-object rewards are serialized in START_MAP, not as ordinary
        // ground spawns. The client binds each reward to its map object index
        // and releases the item when that object is destroyed.
        public static GameServerPacket DungeonStartMap(byte roomX, byte roomY, ushort mapId,
            IReadOnlyList<KeyValuePair<ushort, DungeonCatalog.MonsterRow>> monsters, int level,
            IReadOnlyList<PassiveObjectDropRecord> passiveObjectDrops)
        {
            return DungeonStartMap(roomX, roomY, mapId, monsters, level,
                StartMapFirstVisitRoomState, passiveObjectDrops);
        }

        // A cached re-entry must not carry a new map definition.  ARAD leaves
        // the unconsumed tail unread for state 0, and sending it caused the
        // server state to imply a room rebuild that never happened locally.
        public static GameServerPacket DungeonStartMap(byte roomX, byte roomY, ushort mapId,
            IReadOnlyList<KeyValuePair<ushort, DungeonCatalog.MonsterRow>> monsters, int level,
            byte roomState, IReadOnlyList<PassiveObjectDropRecord> passiveObjectDrops)
        {
            if (roomState != StartMapCachedRoomState &&
                roomState != StartMapFirstVisitRoomState &&
                roomState != StartMapReplaceRoomState)
                throw new ArgumentOutOfRangeException("roomState", "START_MAP room state must be 0, 1, or 2");
            if (monsters == null || monsters.Count > byte.MaxValue ||
                monsters.Any(monster => monster.Key > byte.MaxValue))
                throw new ArgumentOutOfRangeException("monsters", "START_MAP supports at most 255 one-byte monster row ids");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(roomX);
                writer.Write(roomY);
                writer.Write(0u);          // random seed
                writer.Write(roomState);
                if (roomState == StartMapCachedRoomState)
                    return new GameServerPacket(Notification, StartMap, stream.ToArray());
                writer.Write(mapId);
                writer.Write((byte)monsters.Count);
                for (byte row = 0; row < monsters.Count; row++)
                {
                    var runtimeId = monsters[row].Key;
                    var monster = monsters[row].Value;
                    writer.Write((byte)runtimeId); // original map [monster] row
                    writer.Write(runtimeId);       // runtime unique ID (must be u16)
                    writer.Write(monster.MonsterIndex);
                    writer.Write(monster.ResolveLevel(level));
                    // 0=normal, 1=champion, 2=super champion, 3=boss.
                    writer.Write((byte)(monster.IsBoss ? 3 : monster.IsSuperChampion ? 2 :
                        monster.IsChampion ? 1 : 0));
                    writer.Write((byte)0);      // not a box monster
                    writer.Write((byte)0);      // box index
                }
                var passiveDropCount = passiveObjectDrops == null ? 0 :
                    Math.Min(byte.MaxValue, passiveObjectDrops.Count);
                writer.Write((byte)passiveDropCount);
                for (var index = 0; index < passiveDropCount; index++)
                {
                    var passiveDrop = passiveObjectDrops[index];
                    if (passiveDrop == null || passiveDrop.Drop == null)
                        throw new ArgumentException("Passive drop records must contain an item.", "passiveObjectDrops");
                    var drop = passiveDrop.Drop;
                    // objectIndex:u8, runtimeId:u16, itemId:u16, count:u32,
                    // durability:u16. This is the ARAD START_MAP layout.
                    writer.Write(passiveDrop.ObjectIndex);
                    writer.Write(drop.RuntimeId);
                    writer.Write(drop.ItemId);
                    writer.Write(drop.Count);
                    writer.Write(drop.Durability);
                }
                writer.Write((byte)0);      // final room-state flag
                return new GameServerPacket(Notification, StartMap, stream.ToArray());
            }
        }

        public static GameServerPacket Area(CharacterRecord character, ushort userId)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                // Arad case 23 reads uid, town, area, x, y, direction and a
                // final actor-state byte.
                writer.Write(userId); writer.Write(character.TownId); writer.Write(character.AreaId);
                writer.Write(character.X); writer.Write(character.Y); writer.Write(character.Direction);
                writer.Write((byte)3);
                return new GameServerPacket(Notification, UserArea, stream.ToArray());
            }
        }

        // Notification 22 (USER_POSITION) updates an already-created local
        // dungeon actor.  It is separate from USER_AREA: START_MAP creates the
        // map, while this packet places the player inside that map.
        public static GameServerPacket DungeonUserPosition(ushort userId, short x, short y,
            byte direction = 5, ushort movementParameter = 100)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(userId);
                writer.Write(x);
                writer.Write(y);
                writer.Write(direction);
                writer.Write(movementParameter);
                return new GameServerPacket(Notification, UserPosition, stream.ToArray());
            }
        }

        public static GameServerPacket AreaRoster(CharacterRecord character, ushort userId)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(character.TownId); writer.Write(character.AreaId); writer.Write((ushort)1);
                writer.Write(userId); writer.Write(character.X); writer.Write(character.Y); writer.Write(character.Direction);
                writer.Write((byte)3);
                return new GameServerPacket(Notification, AreaUsers, stream.ToArray());
            }
        }
        public static GameServerPacket[] InitialTownActivation(CharacterRecord character, ushort userId)
        {
            // USER_STATE=1 removes the local actor from the town scene in this
            // client. Keep the established visible-character sequence while the
            // keyboard-only restriction is diagnosed separately.
            return new[]
            {
                UserStateChanged(userId, 0),
                Area(character, userId),
                AreaRoster(character, userId)
            };
        }
        public static GameServerPacket StaminaFull() { return new GameServerPacket(Notification, Stamina, new byte[] { 100 }); }
        // STAMINA = 100 会触发客户端的 STAMINA_RECOVER 恢复效果/音效
        // （handler 0x41B1CE 比较 0x64 后调用 0x433890）。传非 100 值
        // 仅设置体力而不播放恢复音效，用于进区域流程。
        public static GameServerPacket StaminaUpdate(byte value) { return new GameServerPacket(Notification, Stamina, new byte[] { value }); }
        public static GameServerPacket FatigueUpdate(ushort consumed, ushort maximum, ushort eventFatigue)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(consumed);
                writer.Write(maximum);
                writer.Write(eventFatigue);
                return new GameServerPacket(Notification, FatigueNotification, stream.ToArray());
            }
        }
        public static GameServerPacket InfiniteFatigueUpdate()
        {
            return FatigueUpdate(0, MaximumFatigue, 0);
        }
        public static GameServerPacket NormalFatigueHudState()
        {
            return new GameServerPacket(Notification, ClientFeatureFlagsNotification, new byte[4]);
        }
        public static GameServerPacket TutorialBootstrapPacket()
        {
            // DNF.exe 1.0.1.9 notification case 136 starts the first-login
            // tutorial. It sends command 15 and command 16 for dungeon 10000,
            // enables module bit 30, then confirms that module with command 153.
            return new GameServerPacket(Notification, TutorialBootstrap, new byte[0]);
        }
        public static GameServerPacket UserStateChanged(ushort userId, byte state)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Notification case 3 at 0x41B0D3: userId:u16, state:u8.
                writer.Write(userId);
                writer.Write(state);
                return new GameServerPacket(Notification, UserState, stream.ToArray());
            }
        }
        public static GameServerPacket UnavailableUdpHost()
        {
            // Notification case 26 consumes one host-state byte.
            return new GameServerPacket(Notification, UdpHost, new byte[] { 0 });
        }
        public static GameServerPacket EnterTutorialSelection()
        {
            // Notification case 27: entry mode followed by a byte-counted list
            // of blocked party member IDs. Mode 1 with an empty list is solo.
            return new GameServerPacket(Notification, EnterSelectDungeonNotification, new byte[] { 1, 0 });
        }
        public static GameServerPacket TutorialDungeonInfo(byte difficulty)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Notification case 28 at 0x41F550 consumes exactly:
                // dungeon:u16, difficulty, maze, bossX, bossY, flagA, flagB.
                writer.Write(TutorialDungeonId);
                writer.Write(difficulty);
                writer.Write((byte)0); // first maze
                writer.Write(byte.MaxValue); // tutorial has no boss room
                writer.Write(byte.MaxValue);
                writer.Write((byte)0);
                writer.Write((byte)0);
                return new GameServerPacket(Notification, DungeonInfo, stream.ToArray());
            }
        }
        public static GameServerPacket TutorialStartMap()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Script.pvf dungeon 10000 is Tutorial/Tutorial.dgn. Its first
                // room is (0,0), map 61000 (Tutorial/tutorial.map), containing
                // four level-1 monster.lst entry 1 rows.
                writer.Write((byte)0); // room X
                writer.Write((byte)0); // room Y
                writer.Write(0u);      // random seed
                writer.Write((byte)1); // full first-visit room state follows
                writer.Write(TutorialMapId);
                writer.Write((byte)TutorialMonsterCount);
                for (byte index = 0; index < TutorialMonsterCount; index++)
                {
                    writer.Write(index);          // map [monster] row
                    writer.Write((ushort)index); // runtime unique ID
                    writer.Write((ushort)1);     // monster.lst ID
                    writer.Write((byte)1);       // level
                    writer.Write((byte)0);       // normal monster
                    writer.Write((byte)0);       // not a box monster
                    writer.Write((byte)0);       // box index
                }
                writer.Write((byte)0); // no runtime passive-object records
                writer.Write((byte)0); // final room-state flag
                return new GameServerPacket(Notification, StartMap, stream.ToArray());
            }
        }
        public static GameServerPacket FinishLoaded()
        {
            // DNF.exe 1.0.1.9 case 30 consumes no body.  Later China clients
            // carry five status bytes here, but that layout is not compatible
            // with this Arad packet dispatcher.
            return new GameServerPacket(Notification, FinishLoadingNotification, new byte[0]);
        }

        // 怪物死亡通知（Arad 国服 notification 38，跳转表 0x42A080 项 38
        // -> 0x4217CE）。布局经韩服 55 模拟端 Dnf55.Protocol 确认：
        //   u16 怪物唯一ID + byte 0 + byte 0 + byte 0xFF（共 6 字节）。
        // Arad handler 同样初始化 0xFF 字节并读取 u16 + byte，逐字段吻合。
        // 注意：命令 42（DieMonsterCommand）是客户端上报，55/Arad 均无
        // 命令响应分支；怪物死亡完全由本通知驱动，与通知 42（逻辑实体
        // 通知，handler 0x423491）无关。
        public static GameServerPacket MonsterDie(ushort uniqueId)
        {
            return MonsterDie(uniqueId, null, NoChampionRewardOrdinal);
        }

        public static GameServerPacket MonsterDie(ushort uniqueId, IList<GroundDropRecord> drops)
        {
            return MonsterDie(uniqueId, drops, NoChampionRewardOrdinal);
        }

        public static GameServerPacket MonsterDie(ushort uniqueId, IList<GroundDropRecord> drops,
            sbyte championRewardOrdinal)
        {
            if (championRewardOrdinal != NoChampionRewardOrdinal &&
                (championRewardOrdinal < 1 || championRewardOrdinal > MaximumChampionRewardOrdinal))
                throw new ArgumentOutOfRangeException("championRewardOrdinal");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(uniqueId);
                var count = drops == null ? 0 : Math.Min(drops.Count, byte.MaxValue);
                writer.Write((byte)count);
                for (var index = 0; index < count; index++)
                {
                    var drop = drops[index];
                    writer.Write(drop.RuntimeId);
                    writer.Write(drop.ItemId);
                    writer.Write(drop.Count);
                    writer.Write(drop.Durability);
                    writer.Write(ushort.MaxValue);
                }
                writer.Write((byte)0);
                writer.Write(unchecked((byte)championRewardOrdinal));
                writer.Write((byte)0);
                return new GameServerPacket(Notification, MonsterDieNotification, stream.ToArray());
            }
        }

        public static GameServerPacket AllDieMonster(ushort killerUserId)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1);
                writer.Write((byte)1);
                writer.Write(killerUserId);
                return new GameServerPacket(Notification, AllDieMonsterNotification, stream.ToArray());
            }
        }

        // 角色死亡状态通知（ref server 命令 43 应答）：actorId:u16 + state
        // + 保留字节。state=0 进入死亡表现，state=1 原地复活/结束死亡表现。
        public static GameServerPacket DieState(ushort actorId, byte state)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(actorId);
                writer.Write(state);
                writer.Write((byte)0);
                return new GameServerPacket(Notification, 32, stream.ToArray());
            }
        }

        public static GameServerPacket GroundItemSpawned(ushort ownerUserId, GroundDropRecord drop)
        {
            if (drop == null) throw new ArgumentNullException("drop");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ownerUserId);
                writer.Write(drop.X);
                writer.Write(drop.Y);
                writer.Write(drop.RuntimeId);
                writer.Write(drop.ItemId);
                writer.Write(drop.ItemFlags);
                writer.Write(drop.Count);
                writer.Write(drop.Durability);
                return new GameServerPacket(Notification, GroundItemSpawnedNotification, stream.ToArray());
            }
        }

        public static GameServerPacket GroundItemPickedUp(ushort pickerUserId, ushort runtimeId, ushort inventorySlot)
        {
            // Arad 客户端通知 39 handler (0x421EA7) 反汇编确认：
            //   读 u16[0] -> 0x4c5aa0 查找地面物品（runtimeId）
            //   读 u16[1] -> 与当前用户比较（userId）
            // 普通物品分支随后依次读取 4 个队伍分配状态字节、
            // recipientUserId、inventorySlot；这个顺序不能交换。
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(runtimeId);
                writer.Write(pickerUserId);
                // 客户端先读取 4 个队伍成员的掷点字节，之后才读取
                // recipientUserId 和 inventorySlot。单人副本无需掷点。
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write(pickerUserId); // 单人副本：拾取者也是获奖者
                writer.Write(inventorySlot);
                return new GameServerPacket(Notification, GroundItemPickedUpNotification, stream.ToArray());
            }
        }

        public static GameServerPacket GroundItemPickedUpGold(ushort pickerUserId, ushort runtimeId,
            uint goldAmount, byte partySlot = 0)
        {
            if (partySlot > 3) throw new ArgumentOutOfRangeException("partySlot");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(runtimeId);
                writer.Write(pickerUserId);
                for (byte slot = 0; slot < 4; slot++)
                {
                    writer.Write(slot == partySlot ? goldAmount : 0u);
                    writer.Write((byte)0);
                }
                return new GameServerPacket(Notification, GroundItemPickedUpNotification, stream.ToArray());
            }
        }

        // 获得经验通知（Arad 国服 notification 37，跳转表 0x42A080 项 37
        // -> 0x42127C）。handler 按顺序读取 byte + u32 + u32 + u32 + u16：
        //   byte 等级(0x1929) + u32 本次获得经验(0x1938) + u32 分项A(0x1948)
        //   + u32 分项B(0x194c) + u16 当前SP(0x193c)。
        // 字段语义由 handler 末端确认：角色对象 vtable[0x490] 被调用，
        // 实参为 (u32 本次经验, byte 等级)，客户端据此累加经验并刷新等级；
        // 等级字段与角色当前等级不一致时（即升级）还会刷新等级 UI。
        // 因此 byte 必须填当前等级（不是槽位），否则客户端会把等级写成 0。
        // 最后的 u16 会直接写回客户端 SP；发 0 会在每次获得经验时把
        // 客户端显示清零，所以必须携带服务端当前 SP。
        public static GameServerPacket ExperienceGained(byte level, uint gained, uint extraA, uint extraB, ushort skillPoints)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(level);
                writer.Write(gained);
                writer.Write(extraA);
                writer.Write(extraB);
                writer.Write(skillPoints);
                return new GameServerPacket(Notification, ExperienceGainedNotification, stream.ToArray());
            }
        }

        private static void WriteBytes(BinaryWriter writer, byte[] value) { writer.Write((uint)value.Length); writer.Write(value); }
    }
}
