using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DF60A1
{
    internal sealed class GameServer : IDisposable
    {
        private const byte SeriaTownId = 1;
        private const byte SeriaAreaId = 1;
        private const short SeriaSpawnX = 474;
        private const short SeriaSpawnY = 336;
        private const int ChannelLoginSettleMilliseconds = 500;
        private const ushort AvatarBagDbBase = 9;
        private const ushort AvatarWearSlotCount = 9;
        private const ushort AvatarBagDbLast = 254;
        // Arad 60 (1.0.44.1) 客户端连上 TCP 后大约 200ms 内收不到服务端
        // CHECK_CONNECTION 就会主动断开。入口客户端（Arad 国服）会在几十毫秒
        // 内先发协议 11，因此 100ms 的竞态窗口仍能区分两种连接。
        private const int EntranceReadRaceMilliseconds = 100;
        // WriteProcessMemory requires both VM_WRITE and VM_OPERATION.  Asking
        // for only VM_WRITE can open the process but still reject the write on
        // current Windows builds, leaving the one-shot first-room hook unarmed.
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ClientRoomTablePointerGlobal = 0x00ED041C;
        private const int ClientRoomCellSize = 0x28;
        private const int ClientRoomCellStateOffset = 0x24;
        private const int ClientRoomWatchCellLimit = 64;

        private readonly ServerOptions _options;
        private readonly SqliteGameStore _store;
        private readonly TcpListener _tcp;
        private readonly UdpCharacterService _udp;
        private readonly QuestCatalog _questCatalog;
        private readonly EquipmentCatalog _equipmentCatalog;
        private readonly CharacterStatCatalog _characterStatCatalog;
        private readonly SkillCatalog _skillCatalog;
        private readonly ItemCategoryCatalog _itemCategoryCatalog;
        private readonly MonsterCatalog _monsterCatalog;
        private readonly MonsterGoldCatalog _monsterGoldCatalog;
        private readonly MonsterIndependentDropCatalog _independentDropCatalog;
        private readonly WorldDropCatalog _worldDropCatalog;
        private readonly DungeonCatalog _dungeonCatalog;
        private readonly CeraShopCatalog _ceraShopCatalog;
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
        private readonly byte[] _channelScript;
        private volatile bool _running;

        public GameServer(ServerOptions options, SqliteGameStore store)
        {
            _options = options; _store = store;
            var address = IPAddress.Parse(options.Host);
            _tcp = new TcpListener(address, options.Port);
            _udp = new UdpCharacterService(new IPEndPoint(address, options.Port), Log);
            try
            {
                uint fileNumber;
                var archive = new PvfArchiveReader(options.ScriptPvfPath);
                var channelInfo = archive.ReadAllBytes("etc/channel_info.etc", out fileNumber);
                // DNF.exe 1.0.1.9 passes the downloaded buffer through strlen
                // and feeds it directly to the channel-info lexer.  Unlike the
                // later 1.0.44.1 client, protocol 9 therefore expects the raw
                // channel_info.etc text, not a binary ChannelScript.pvf archive.
                _channelScript = channelInfo;
                _questCatalog = new QuestCatalog(archive);
                _equipmentCatalog = new EquipmentCatalog(archive);
                _characterStatCatalog = new CharacterStatCatalog(archive);
                _skillCatalog = new SkillCatalog(archive);
                _itemCategoryCatalog = new ItemCategoryCatalog(archive);
                _dungeonCatalog = new DungeonCatalog(archive);
                _monsterCatalog = new MonsterCatalog(archive);
                _monsterGoldCatalog = new MonsterGoldCatalog(archive);
                _independentDropCatalog = new MonsterIndependentDropCatalog(archive, _monsterGoldCatalog);
                _worldDropCatalog = new WorldDropCatalog(archive);
                _ceraShopCatalog = new CeraShopCatalog(archive);
            }
            catch (Exception exception)
            {
                _channelScript = null;
                _questCatalog = new QuestCatalog(new QuestDefinition[0]);
                _equipmentCatalog = new EquipmentCatalog(null);
                _characterStatCatalog = new CharacterStatCatalog(null);
                _skillCatalog = new SkillCatalog(null);
                _itemCategoryCatalog = new ItemCategoryCatalog(null);
                _dungeonCatalog = new DungeonCatalog(null);
                _monsterCatalog = new MonsterCatalog(null);
                _monsterGoldCatalog = new MonsterGoldCatalog(null);
                _independentDropCatalog = new MonsterIndependentDropCatalog(null, _monsterGoldCatalog);
                _worldDropCatalog = new WorldDropCatalog(null);
                _ceraShopCatalog = new CeraShopCatalog(null);
                Console.Error.WriteLine("无法从 Script.pvf 构建 ChannelScript.pvf: " + exception.Message);
            }
        }

        public void Run()
        {
            _running = true; _tcp.Start(); _udp.Start();
            Log("INFO", "DNF Arad 模拟端已启动，TCP/UDP {0}:{1}", _options.Host, _options.Port);
            Log("INFO", "数据库: {0}", _options.DatabasePath);
            Log("INFO", "频道脚本: {0}", _channelScript == null ? "不可用" : _channelScript.Length + " bytes");
            Log("INFO", "按 Ctrl+C 停止；另开终端执行程序 --launch 可按 13 段参数启动客户端。");
            Log("INFO", "Arad 任务表已加载: {0} 条（主线、支线与重复任务）", _questCatalog.Count);
            Log("INFO", "Arad 装备耐久表已加载: {0} 条", _equipmentCatalog.Count);
            Log("INFO", "Arad 技能 SP 消耗表已加载: {0} 条", _skillCatalog.Count);
            Log("INFO", "Arad 物品分类表已全量校验: equipment={0}, consumable={1}, material={2}, quest={3}, total={4}",
                _itemCategoryCatalog.EquipmentCount, _itemCategoryCatalog.ConsumableCount,
                _itemCategoryCatalog.MaterialCount, _itemCategoryCatalog.QuestCount, _itemCategoryCatalog.Count);
            Log("INFO", "Arad 副本目录已加载: {0} 个副本, {1} 张地图", _dungeonCatalog.DungeonCount, _dungeonCatalog.MapCount);
            Log("INFO", "Arad 怪物掉落表已加载: {0} 只怪物", _monsterCatalog.Count);
            Log("INFO", "Arad 完整掉落表已加载: specific={0}, independentRows={1}, equipmentBuckets={2}, worldRows={3}, goldRows={4}",
                _monsterCatalog.SpecificDropCount, _independentDropCatalog.DropProbabilityCount,
                _independentDropCatalog.EquipmentBucketCount, _worldDropCatalog.RowCount, _monsterGoldCatalog.Count);
            Log("INFO", "Arad 商城商品表已加载: {0} 个商品", _ceraShopCatalog.Count);
            Task.Run((Func<Task>)AcceptLoopAsync);
            _stopped.Wait();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _tcp.Stop(); } catch { }
            _udp.Stop();
            _stopped.Set();
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                try
                {
                    var client = await _tcp.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (!_running) break; throw; }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var remote = client.Client.RemoteEndPoint == null ? "unknown" : client.Client.RemoteEndPoint.ToString();
            Log("INFO", "TCP 客户端连接: {0}", remote);
            var token = new byte[16]; Encoding.ASCII.GetBytes("dnf2008-local").CopyTo(token, 0);
            var cipher = new GameClientCipherState();
            var citrisCipher = new Citris60ClientCipher();
            var entrance = new EntranceSession();
            var accountName = _options.Account;
            var accountId = 0;
            var loginResponseUtc = DateTime.MinValue;
            var migratedCharacters = 0;
            if (migratedCharacters > 0)
                Log("INFO", "已合并旧本地账号 create_channel_2008 -> {0}，迁移角色 {1} 个", accountName, migratedCharacters);
            CharacterRecord active = null;
            // ARAD 客户端创建角色后会上报命令 122（配置/列表上传）。收到后
            // 先确认，再自动把刚创建的角色带入城镇；若客户端自行发送
            // SELECT_CHARACTER，则此标记会被 SelectCharacter 分支清空。
            CharacterRecord pendingAutoEnter = null;
            const ushort userId = 1;
            var userInfoSent = false;
            var areaRosterInitialized = false;
            var tutorialRun = new TutorialDungeonRun();
            var lorienRun = new LorienDungeonRun(_dungeonCatalog);
            // DungeonCatalog retains the PVF room-table coordinates selected
            // for this client. A first room is data-driven rather than
            // universally (0,0), and command 48 uses those coordinates
            // directly.
            // Diagnostic only: DebugConfig is read by the client at startup,
            // so retain the scene index that this server has actually driven.
            // -1 = character/channel selection; 0 = town or the dungeon selector.
            // A running dungeon uses its actual index: Lorien is 1, while
            // Mountain Ridge is 40.
            var tracedDungeonIndex = -1;
            var dungeonTraceCts = new CancellationTokenSource();
            var dungeonTraceTask = Task.Run(async delegate
            {
                try
                {
                    while (!dungeonTraceCts.IsCancellationRequested)
                    {
                        await Task.Delay(3000, dungeonTraceCts.Token).ConfigureAwait(false);
                        if (dungeonTraceCts.IsCancellationRequested) break;

                        string clientMemory;
                        if (TryReadClientRoomMemory(out clientMemory))
                            Log("TRACE", "客户端内存副本状态 remote={0}: {1}", remote, clientMemory);
                        else
                            Log("TRACE", "客户端内存副本状态 remote={0}: unavailable={1}", remote, clientMemory);
                        continue;
                    }
                }

 #if false // Legacy server-state logging retained only as historical reference.
                        var selectedDungeonId = lorienRun.IsActive ? lorienRun.DungeonId :
                            (tutorialRun.IsActive ? GamePackets.TutorialDungeonId : (ushort)0);
                        var selectedMapId = lorienRun.IsActive ? lorienRun.MapId :
                            (tutorialRun.IsActive ? GamePackets.TutorialMapId : (ushort)0);
                        var roomSlot = lorienRun.IsActive ? lorienRun.RoomSlot : (tutorialRun.IsActive ? 0 : -1);
                        var roomX = lorienRun.IsActive ? lorienRun.RoomX : (byte)0;
                        var roomY = lorienRun.IsActive ? lorienRun.RoomY : (byte)0;
                        Log("TRACE", "副本状态 remote={0}: dungeon index={1}, active dungeon={2}, map-id={3}, room-slot={4}, room=({5},{6})",
                            remote, Volatile.Read(ref tracedDungeonIndex), selectedDungeonId, selectedMapId,
                            roomSlot, roomX, roomY);
                    }
                }
 #endif
                catch (OperationCanceledException)
                {
                    // The client disconnected and the session is being released.
                }
            });
            var settlement = new DungeonSettlementState();
            var groundDrops = new GroundDropState();
            var roomDropLedger = new DungeonRoomDropLedger();
            GameServerPacket[] pendingGroundRestorePackets = null;
            // 进副本前的位置（命令 15 记录），返回城镇时恢复到副本门口。
            // 赛丽亚房间是 town 1 / area 1（Gate.map）。
            byte returnTownId = SeriaTownId, returnAreaId = SeriaAreaId;
            short returnX = SeriaSpawnX, returnY = SeriaSpawnY;
            byte returnDirection = 5;
            // 初始技能由职业表派生；这里保存学习/升级后的槽位与等级。
            var learnedSkills = new List<CharacterSkillRecord>();
            // Main inventory is keyed by its protocol slot. Slots 3..8 are the
            // item quick bar; the visible backpack grid starts at slot 9.
            var inventory = new Dictionary<ushort, InventoryItemRecord>();
            var storage = new Dictionary<ushort, InventoryItemRecord>();
            var equipment = new Dictionary<ushort, InventoryItemRecord>();
            var avatars = new Dictionary<ushort, InventoryItemRecord>();
            var activeQuests = new List<CharacterQuestRecord>();
            var completedQuestIds = new HashSet<ushort>();
            var questDropRandom = new Random();
            // 角色死亡倒计时（命令 43）：10 秒后原地复活（教程）或回城。
            var deathReturnEpoch = 0;
            CancellationTokenSource deathReturnCts = null;
            void CancelDeathReturnTimer()
            {
                var old = deathReturnCts;
                deathReturnCts = null;
                if (old != null)
                {
                    deathReturnEpoch++;
                    try { old.Cancel(); } catch { }
                    old.Dispose();
                }
            }

            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                // The entrance socket is client-first, while the channel
                // socket waits for a server CHECK_CONNECTION notification.
                // A short first-read race distinguishes both connections even
                // though the local emulator intentionally shares one port.
                var firstRead = PacketFrame.ReadAsync(stream);
                var firstCompleted = await Task.WhenAny(firstRead, Task.Delay(EntranceReadRaceMilliseconds)).ConfigureAwait(false);
                PacketFrame queued = null;
                if (ReferenceEquals(firstCompleted, firstRead))
                {
                    queued = await firstRead.ConfigureAwait(false);
                }
                else
                {
                    // Arad 老客户端在频道连接上先等 CHANNELINFO；
                    // Arad 60 则先等 CHECK_CONNECTION。
                    if (GameServerPacket.UseLegacyChannelHeader)
                    {
                        await SendAsync(stream, GamePackets.Check(token), remote).ConfigureAwait(false);
                        await SendAsync(stream, GamePackets.Channel(token, _options.Host, _options.Port), remote).ConfigureAwait(false);
                        // ARAD 客户端不会主动发送 LOGIN / GET_USER_INFO，
                        // 账号与角色列表必须由服务端在频道握手后主动下发，
                        // 否则客户端一直停留在登录/频道界面。
                        if (accountId == 0)
                            accountId = _store.EnsureAccount(accountName, _options.Password);
                        await Task.Delay(300).ConfigureAwait(false);
                        await SendRosterAsync(stream, remote, accountId).ConfigureAwait(false);
                        userInfoSent = true;
                        Log("INFO", "ARAD 频道连接已自动下发角色列表（{0} 个角色）", _store.ListCharacters(accountId).Count);
                    }
                    else
                    {
                        await SendAsync(stream, GamePackets.Check(token), remote).ConfigureAwait(false);
                    }
                    Log("INFO", "连接 {0} 识别为频道连接，已发送 {1}", remote,
                        GameServerPacket.UseLegacyChannelHeader ? "CHANNELINFO" : "CHECK_CONNECTION");
                }

                while (_running)
                {
                    var raw = queued;
                    queued = null;
                    raw = raw ?? await firstRead.ConfigureAwait(false);
                    firstRead = PacketFrame.ReadAsync(stream);
                    if (raw == null) break;
                    LogPacket("RX", remote, raw, raw.Body);
                    if (raw.Type == 0 && EntranceProtocol.IsEntrance(raw.ProtocolId))
                    {
                        var entranceReply = EntranceProtocol.Handle(raw, entrance, _options.Host, _options.Port, _channelScript);
                        if (entranceReply != null) await SendAsync(stream, entranceReply, remote).ConfigureAwait(false);
                        continue;
                    }
                    PacketFrame request;
                    var decodedOk = (raw.IsCitrisCommand || GameServerPacket.UseLegacyChannelHeader)
                        ? citrisCipher.TryDecode(raw, out request)
                        : cipher.TryDecode(raw, out request);
                    if (!decodedOk)
                    {
                        // Some Arad builds send early handshake bodies in plaintext.
                        request = raw;
                        Log("WARN", "无法恢复滚动密钥，暂按明文处理 type={0} id={1} crc={2:X8}", raw.Type, raw.ProtocolId, raw.DeclaredCrc32);
                    }
                    else if (!ReferenceEquals(raw, request))
                    {
                        LogPacket("RX-DEC", remote, request, request.Body);
                    }

                    if (request.Type != GamePackets.Command)
                    {
                        Log("WARN", "未处理包 type={0} id={1}", request.Type, request.ProtocolId);
                        continue;
                    }

                    switch (request.ProtocolId)
                    {
                        case GamePackets.CheckConnection:
                            await SendAsync(stream, GamePackets.Channel(token), remote).ConfigureAwait(false);
                            break;
                        case GamePackets.Login:
                            accountName = NormalizeLoginName(ReadLoginName(request.Body), _options.Account);
                            accountId = _store.EnsureAccount(accountName, _options.Password);
                            migratedCharacters = _store.MergeLocalAccountCharacters("create_channel_2008", accountId);
                            if (migratedCharacters > 0)
                                Log("INFO", "已合并旧本地账号 create_channel_2008 -> {0}，迁移角色 {1} 个", accountName, migratedCharacters);
                            await SendAsync(stream, GamePackets.LoginOk(), remote).ConfigureAwait(false);
                            loginResponseUtc = DateTime.UtcNow;
                            Log("INFO", "账号登录: {0} (id={1})", accountName, accountId);
                            break;
                        case GamePackets.Exit:
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.Exit, true), remote).ConfigureAwait(false);
                            break;
                        case GamePackets.SecurityCard:
                            // CNAntiBotSystem::vftable -> sub_8C74F0 uploads a
                            // uint32 length followed by opaque anti-bot data.
                            // Neither sub_40F290 nor the notification dispatcher
                            // has an id-161 consumer, so this is fire-and-forget.
                            break;
                        case 181:
                            // Arad client capability/build report.
                            break;
                        case 213:
                            // ARAD FRAME_LAG_STATISTICS（帧延迟统计）属于单向上报。
                            break;
                        case 210:
                        case 242:
                            // Arad town/tutorial state telemetry. These are
                            // fire-and-forget and have no command response.
                            break;
                        case 211:
                            // Arad 角色创建/控制配置上报（新建角色后客户端
                            // 上传键位/控制表）。与 181/210/242 相同，属于
                            // 单向上报，没有命令响应，忽略即可。
                            break;
                        case 212:
                            // ARAD 音量/客户端偏好保存通知。该命令通常只
                            // 携带客户端序号，实际配置数据由 217 一并上传。
                            await SendAsync(stream, GamePackets.ClientConfigurationSaved(212), remote).ConfigureAwait(false);
                            Log("INFO", "ARAD 客户端音量配置通知已接收 body={0}", Hex(request.Body));
                            break;
                        case 217:
                            // body[0..1] 是客户端请求序号；其后的数据是
                            // 客户端配置记录（键位、音量及未来可能新增的
                            // UI 设置）。原样保存，避免未知字段丢失。
                            if (accountId <= 0)
                                accountId = _store.EnsureAccount(accountName, _options.Password);
                            if (request.Body != null && request.Body.Length > 2)
                            {
                                var configPayload = new byte[request.Body.Length - 2];
                                Buffer.BlockCopy(request.Body, 2, configPayload, 0, configPayload.Length);
                                _store.SaveClientConfig(accountId, configPayload);
                                await SendAsync(stream, GamePackets.ClientConfigurationSaved(217), remote).ConfigureAwait(false);
                                Log("INFO", "ARAD 客户端配置已保存 account={0}, bytes={1}", accountId, configPayload.Length);
                            }
                            break;
                        case 160:
                            // Tutorial combat sends compressed client state at
                            // a high frequency.  It has no server response in
                            // the Arad client dispatcher; do not treat it as an
                            // unknown command merely because we do not consume
                            // its opaque compressed payload yet.
                            Log("INFO", "收到教程状态上报 160，length={0}", request.Body == null ? 0 : request.Body.Length);
                            break;
                        case GamePackets.GetUserInfo:
                            if (accountId == 0)
                            {
                                accountId = _store.EnsureAccount(accountName, _options.Password);
                                _store.MergeLocalAccountCharacters("create_channel_2008", accountId);
                            }
                            await SendRosterAsync(stream, remote, accountId).ConfigureAwait(false);
                            userInfoSent = true;
                            if (_options.AutoSelect)
                            {
                                active = _store.ListCharacters(accountId).FirstOrDefault();
                                if (active != null)
                                {
                                    LoadAndNormalizeSkills(active, learnedSkills);
                                    LoadInventory(active.Id, inventory);
                                    LoadStorage(active.Id, storage);
                                    LoadEquipment(active.Id, active.Job, equipment);
                                    LoadAvatars(active.Id, active.Job, avatars);
                                    LoadQuests(active.Id, activeQuests, completedQuestIds);
                                    CompleteTutorialForDirectTown(active);
                                    await Task.Delay(500).ConfigureAwait(false);
                                    if (TownSpawnCatalog.ApplySeriaSpawn(active))
                                    {
                                        _store.UpdatePosition(active.Id, active.TownId, active.AreaId, active.X, active.Y, active.Direction);
                                        Log("INFO", "角色 {0} 已迁移到当前城镇 Gate: town={1}, area={2}, ({3},{4})", active.Name, active.TownId, active.AreaId, active.X, active.Y);
                                    }
                                    if (request.IsCitrisCommand)
                                    {
                                        await SendAsync(stream, GamePackets.CitrisSelectOk(active, userId), remote).ConfigureAwait(false);
                                        await SendCitrisCharacterInitializationAsync(stream, remote, active, userId, inventory, storage, equipment, avatars, learnedSkills, activeQuests, completedQuestIds).ConfigureAwait(false);
                                    }
                                    else if (GameServerPacket.UseLegacyChannelHeader)
                                    {
                                        await SendAsync(stream, GamePackets.AradSelectOk(active, userId, activeQuests), remote).ConfigureAwait(false);
                                        await SendCharacterInitializationAsync(stream, remote, active, userId, inventory, storage, equipment, avatars, learnedSkills, activeQuests, completedQuestIds).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await SendAsync(stream, GamePackets.SelectOk(active, userId, activeQuests), remote).ConfigureAwait(false);
                                        await SendCharacterInitializationAsync(stream, remote, active, userId, inventory, storage, equipment, avatars, learnedSkills, activeQuests, completedQuestIds).ConfigureAwait(false);
                                    }
                                    Log("INFO", "联调自动选择角色 {0}：已发送 SELECT_CHARACTER 与城镇初始化流", active.Name);
                                }
                            }
                            break;
                        case GamePackets.CeraShopBuy:
                            byte shopCategory; uint[] commodityNumbers; byte[] purchaseOptions;
                            if (active == null || !TryReadCeraShopPurchase(request.Body, out shopCategory,
                                out commodityNumbers, out purchaseOptions))
                            {
                                await SendAsync(stream, GamePackets.CeraShopBuyError(4), remote).ConfigureAwait(false);
                                if (active != null)
                                    await SendAsync(stream, GamePackets.CeraBalance((uint)Math.Max(0, active.Cash)), remote).ConfigureAwait(false);
                                Log("WARN", "命令 67：无效商城购买请求 body={0}", Hex(request.Body));
                                break;
                            }
                            var plannedInventory = CloneItems(inventory);
                            var plannedAvatars = CloneItems(avatars);
                            var purchasedProducts = new List<CeraShopProduct>();
                            var purchasedAvatarSlots = new List<ushort>();
                            long totalCashPrice = 0;
                            var shopPurchaseValid = true;
                            for (var productIndex = 0; productIndex < commodityNumbers.Length; productIndex++)
                            {
                                var commodityNo = commodityNumbers[productIndex];
                                CeraShopProduct product;
                                if (!_ceraShopCatalog.TryGet(commodityNo, out product)) { shopPurchaseValid = false; break; }
                                totalCashPrice += product.CashPrice;
                                if (totalCashPrice > active.Cash) { shopPurchaseValid = false; break; }
                                if (product.IsAvatar)
                                {
                                    var avatarSlot = FindFreeAvatarSlot(plannedAvatars);
                                    if (avatarSlot == ushort.MaxValue) { shopPurchaseValid = false; break; }
                                    var requestedOption = purchaseOptions[productIndex];
                                    // Command 67 carries the client-side compact option index.
                                    // It is not a PVF skill id and must not be clamped to the
                                    // local PVF option count: the client applies that compact
                                    // value when its provisional shop-avatar is refreshed.
                                    var selectedOption = ResolveAvatarPurchaseOption(requestedOption,
                                        product.DefaultAvatarOption);
                                    plannedAvatars[avatarSlot] = new InventoryItemRecord
                                    {
                                        Slot = avatarSlot, ItemId = product.ItemId,
                                        // The matching client treats zero as a permanent avatar.
                                        // The durability field carries the selected avatar option.
                                        Count = 0, State = 0, Durability = selectedOption, SealState = 0
                                    };
                                    purchasedAvatarSlots.Add(avatarSlot);
                                }
                                else if (AddInventoryReward(plannedInventory, product.ItemId, product.Quantity) == ushort.MaxValue)
                                {
                                    shopPurchaseValid = false; break;
                                }
                                purchasedProducts.Add(product);
                            }
                            if (!shopPurchaseValid)
                            {
                                var failedCommodity = commodityNumbers.Length == 0 ? uint.MaxValue : commodityNumbers[0];
                                await SendAsync(stream, GamePackets.CeraShopBuyError(4, shopCategory, failedCommodity), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.CeraBalance((uint)Math.Max(0, active.Cash)), remote).ConfigureAwait(false);
                                Log("WARN", "命令 67：商城购买失败 products={0} cash={1} price={2}", commodityNumbers.Length, active.Cash, totalCashPrice);
                                break;
                            }
                            active.Cash -= (int)totalCashPrice;
                            inventory.Clear(); foreach (var pair in plannedInventory) inventory[pair.Key] = pair.Value;
                            avatars.Clear(); foreach (var pair in plannedAvatars) avatars[pair.Key] = pair.Value;
                            _store.SaveCashInventoryAndAvatars(active.Id, active.Cash,
                                inventory.Values, avatars.Values);
                            foreach (var product in purchasedProducts)
                                await SendAsync(stream, GamePackets.CeraShopBuyReply(product.ClientCategory,
                                    product.CommodityNo, product.ItemId, product.Quantity), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.CeraBalance((uint)Math.Max(0, active.Cash)), remote).ConfigureAwait(false);
                            if (purchasedProducts.Any(product => !product.IsAvatar))
                                await SendItemListPacketsAsync(stream, remote, inventory, active.Gold).ConfigureAwait(false);
                            if (purchasedAvatarSlots.Count != 0)
                            {
                                // The reference flow brackets the full avatar-list refresh with
                                // incremental updates.  This releases both the shop modal and the
                                // avatar inventory's per-slot cache in this client family.
                                foreach (var avatarSlot in purchasedAvatarSlots)
                                    await SendAsync(stream, GamePackets.UpdateItem(1, AvatarBagDbToWire(avatarSlot),
                                        avatars[avatarSlot]), remote).ConfigureAwait(false);
                                await SendAsync(stream, BuildAvatarListPacket(avatars), remote).ConfigureAwait(false);
                                foreach (var avatarSlot in purchasedAvatarSlots)
                                    await SendAsync(stream, GamePackets.UpdateItem(1, AvatarBagDbToWire(avatarSlot),
                                        avatars[avatarSlot]), remote).ConfigureAwait(false);
                            }
                            Log("INFO", "命令 67：商城购买完成 products={0} price={1} cash={2}", purchasedProducts.Count, totalCashPrice, active.Cash);
                            break;
                        case GamePackets.BuyItem:
                            ushort purchasedItemId;
                            int requestedPurchaseCount;
                            if (active == null || !GamePackets.TryReadBuyItem(request.Body, out purchasedItemId, out requestedPurchaseCount))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.BuyItem, 4), remote).ConfigureAwait(false);
                                Log("WARN", "命令 21：无效购买请求 body={0}", Hex(request.Body));
                                break;
                            }

                            if (_equipmentCatalog.IsAvatar(purchasedItemId))
                            {
                                var avatarSlot = FindFreeAvatarSlot(avatars);
                                var avatarCost = GetItemPurchasePrice(purchasedItemId, true);
                                if (avatarSlot == ushort.MaxValue || avatarCost > active.Gold)
                                {
                                    await SendAsync(stream, GamePackets.CommandError(GamePackets.BuyItem, 4), remote).ConfigureAwait(false);
                                    Log("WARN", "命令 21：装扮购买失败 itemId={0} gold={1} slot={2}", purchasedItemId, active.Gold, avatarSlot);
                                    break;
                                }
                                avatars[avatarSlot] = new InventoryItemRecord
                                {
                                    Slot = avatarSlot, ItemId = purchasedItemId, Count = 0,
                                    State = 0, Durability = 0, SealState = 0
                                };
                                active.Gold -= avatarCost;
                                _store.SaveGoldAndInventory(active.Id, active.Gold, inventory.Values);
                                _store.SaveAvatars(active.Id, avatars.Values);
                                await SendAsync(stream, GamePackets.BuyItemReply(1, AvatarBagDbToWire(avatarSlot)), remote).ConfigureAwait(false);
                                await SendAsync(stream, BuildAvatarListPacket(avatars), remote).ConfigureAwait(false);
                                await SendItemListPacketsAsync(stream, remote, inventory, active.Gold).ConfigureAwait(false);
                                Log("INFO", "命令 21：装扮购买完成 itemId={0} slot={1} cost={2}", purchasedItemId, avatarSlot, avatarCost);
                                break;
                            }

                            var purchasedEquipment = IsEquipmentItem(purchasedItemId);
                            var purchaseCount = purchasedEquipment ? 1u : (uint)Math.Max(1, Math.Min(999, requestedPurchaseCount));
                            var purchaseCost = (long)GetItemPurchasePrice(purchasedItemId, purchasedEquipment) * purchaseCount;
                            InventoryItemRecord purchaseTarget = purchasedEquipment
                                ? null
                                : inventory.Values.FirstOrDefault(value => value.ItemId == purchasedItemId && value.Durability == 0);
                            var purchaseSlot = purchaseTarget == null ? FindFreeInventorySlot(inventory, purchasedItemId) : purchaseTarget.Slot;
                            if (purchaseSlot == ushort.MaxValue || purchaseCost > active.Gold)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.BuyItem, 4), remote).ConfigureAwait(false);
                                Log("WARN", "命令 21：购买失败 itemId={0} count={1} gold={2} slot={3}", purchasedItemId, purchaseCount, active.Gold, purchaseSlot);
                                break;
                            }

                            if (purchaseTarget == null)
                            {
                                purchaseTarget = new InventoryItemRecord
                                {
                                    Slot = purchaseSlot,
                                    ItemId = purchasedItemId,
                                    Count = purchaseCount,
                                    State = 0,
                                    Durability = purchasedEquipment ? _equipmentCatalog.GetMaximumDurability(purchasedItemId) : (ushort)0,
                                    SealState = 0
                                };
                                inventory[purchaseSlot] = purchaseTarget;
                            }
                            else
                            {
                                var stackedCount = (ulong)purchaseTarget.Count + purchaseCount;
                                purchaseTarget.Count = stackedCount > uint.MaxValue ? uint.MaxValue : (uint)stackedCount;
                            }
                            active.Gold -= (int)purchaseCost;
                            _store.SaveGoldAndInventory(active.Id, active.Gold, inventory.Values);
                            await SendAsync(stream, GamePackets.BuyItemReply(0, purchaseSlot), remote).ConfigureAwait(false);
                            await SendItemListPacketsAsync(stream, remote, inventory, active.Gold, purchaseSlot).ConfigureAwait(false);
                            Log("INFO", "命令 21：购买完成 itemId={0} count={1} slot={2} cost={3} gold={4}", purchasedItemId, purchaseCount, purchaseSlot, purchaseCost, active.Gold);
                            break;
                        case GamePackets.SellItem:
                            byte sellListType; ushort sellSlot; int requestedSellCount;
                            InventoryItemRecord sellItem;
                            if (active == null ||
                                !GamePackets.TryReadSellItem(request.Body, out sellListType, out sellSlot, out requestedSellCount) ||
                                sellListType != 0 || !inventory.TryGetValue(sellSlot, out sellItem) || sellItem.Count == 0)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.SellItem, 17), remote).ConfigureAwait(false);
                                break;
                            }
                            var soldCount = Math.Min((uint)requestedSellCount, sellItem.Count);
                            var sellRemainder = sellItem.Count - soldCount;
                            if (sellRemainder == 0) inventory.Remove(sellSlot);
                            else sellItem.Count = sellRemainder;
                            var sellGain = (long)Math.Max(1, GetItemPurchasePrice(sellItem.ItemId, IsEquipmentItem(sellItem.ItemId)) / 2) * soldCount;
                            active.Gold = (int)Math.Min(int.MaxValue, (long)Math.Max(0, active.Gold) + sellGain);
                            _store.SaveGoldAndInventory(active.Id, active.Gold, inventory.Values);
                            await SendAsync(stream, GamePackets.SellItemReply((uint)active.Gold), remote).ConfigureAwait(false);
                            InventoryItemRecord sellRemaining;
                            inventory.TryGetValue(sellSlot, out sellRemaining);
                            await SendAsync(stream, GamePackets.UpdateItem(0, sellSlot, sellRemaining), remote).ConfigureAwait(false);
                            Log("INFO", "命令 22：出售 itemId={0} count={1} slot={2}, +{3} 金币, gold={4}",
                                sellItem.ItemId, soldCount, sellSlot, sellGain, active.Gold);
                            break;
                        case GamePackets.SetItemTradeState:
                            byte tradeListType; ushort tradeSlot; ushort requestedTradeCount;
                            InventoryItemRecord tradeItem;
                            if (active == null ||
                                !GamePackets.TryReadSetItemTradeState(request.Body, out tradeListType, out tradeSlot, out requestedTradeCount) ||
                                tradeListType != 0 || !inventory.TryGetValue(tradeSlot, out tradeItem) || tradeItem.Count == 0)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.SetItemTradeState, 17), remote).ConfigureAwait(false);
                                break;
                            }
                            var tradeSoldCount = Math.Min(requestedTradeCount == 0 ? 1u : requestedTradeCount, tradeItem.Count);
                            var tradeRemainder = tradeItem.Count - tradeSoldCount;
                            if (tradeRemainder == 0) inventory.Remove(tradeSlot);
                            else tradeItem.Count = tradeRemainder;
                            var tradeGain = (long)Math.Max(1, GetItemPurchasePrice(tradeItem.ItemId, IsEquipmentItem(tradeItem.ItemId)) / 2) * tradeSoldCount;
                            active.Gold = (int)Math.Min(int.MaxValue, (long)Math.Max(0, active.Gold) + tradeGain);
                            _store.SaveGoldAndInventory(active.Id, active.Gold, inventory.Values);
                            await SendAsync(stream, GamePackets.SetItemTradeStateReply((uint)active.Gold, tradeListType, tradeSlot,
                                (ushort)Math.Min(ushort.MaxValue, tradeRemainder)), remote).ConfigureAwait(false);
                            // Command 24 updates the balance/count fields, but
                            // a zero count does not destroy the client's slot
                            // object. Notification 14 performs the actual icon
                            // removal (or refreshes the remaining stack).
                            InventoryItemRecord tradeRemaining;
                            inventory.TryGetValue(tradeSlot, out tradeRemaining);
                            await SendAsync(stream, GamePackets.UpdateItem(tradeListType, tradeSlot, tradeRemaining), remote).ConfigureAwait(false);
                            Log("INFO", "命令 24：出售装备/物品 itemId={0} count={1} slot={2}, +{3} 金币, gold={4}",
                                tradeItem.ItemId, tradeSoldCount, tradeSlot, tradeGain, active.Gold);
                            break;
                        case GamePackets.AcceptQuest:
                            if (active == null || request.Body.Length < 4)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.AcceptQuest, 23), remote).ConfigureAwait(false);
                                break;
                            }
                            var acceptedQuestId = LittleEndian.ReadUInt16(request.Body, 2);
                            QuestDefinition acceptedDefinition;
                            if (!_questCatalog.TryGet(acceptedQuestId, out acceptedDefinition) ||
                                activeQuests.Count >= QuestCatalog.MaxActiveQuests ||
                                !_questCatalog.CanAcceptQuest(active, acceptedQuestId, activeQuests, completedQuestIds))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.AcceptQuest, 18), remote).ConfigureAwait(false);
                                break;
                            }
                            // Zero means "all conditions satisfied" in the
                            // Arad quest manager. Most new quests start
                            // unfinished, but a [depend give item] grant can
                            // satisfy a Seeking condition at accept time.
                            var acceptedQuest = new CharacterQuestRecord
                            {
                                QuestId = acceptedQuestId,
                                Trigger = QuestCatalog.GetInitialTrigger(acceptedDefinition, inventory.Values, active.Gold)
                            };
                            var acceptedInventory = CloneItems(inventory);
                            var acceptedItemSlots = new HashSet<ushort>();
                            var acceptItemsValid = true;
                            foreach (var grant in QuestCatalog.GetAcceptItemGrants(acceptedDefinition))
                            {
                                var itemSlot = AddInventoryReward(acceptedInventory, grant.ItemId, grant.Count);
                                if (itemSlot == ushort.MaxValue)
                                {
                                    acceptItemsValid = false;
                                    break;
                                }
                                acceptedItemSlots.Add(itemSlot);
                            }
                            if (!acceptItemsValid)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.AcceptQuest, 18), remote).ConfigureAwait(false);
                                Log("WARN", "任务接取失败，背包没有足够空间存放任务道具 quest={0}", acceptedQuestId);
                                break;
                            }
                            acceptedQuest.Trigger = QuestCatalog.GetInitialTrigger(acceptedDefinition,
                                acceptedInventory.Values, active.Gold);
                            try
                            {
                                _store.SaveAcceptedQuestAndInventory(active.Id, acceptedQuest, acceptedInventory.Values);
                            }
                            catch (Exception acceptException)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.AcceptQuest, 23), remote).ConfigureAwait(false);
                                Log("WARN", "任务接取持久化失败 quest={0}: {1}", acceptedQuestId, acceptException.Message);
                                break;
                            }
                            inventory.Clear();
                            foreach (var pair in acceptedInventory) inventory[pair.Key] = pair.Value;
                            activeQuests.Add(acceptedQuest);
                            await SendAsync(stream, GamePackets.AcceptQuestReply(acceptedQuestId, acceptedQuest.Trigger), remote).ConfigureAwait(false);
                            if (acceptedItemSlots.Count > 0)
                                await SendItemListPacketsAsync(stream, remote, inventory, active.Gold,
                                    acceptedItemSlots.OrderBy(value => value).Last()).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                            Log("INFO", "任务已接受 quest={0}, acceptedItems={1}", acceptedQuestId, acceptedItemSlots.Count);
                            break;
                        case GamePackets.GiveupQuest:
                            if (active == null || request.Body.Length < 4)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.GiveupQuest, 19), remote).ConfigureAwait(false);
                                break;
                            }
                            var giveupQuestId = LittleEndian.ReadUInt16(request.Body, 2);
                            var giveupQuest = activeQuests.FirstOrDefault(value => value.QuestId == giveupQuestId);
                            if (giveupQuest == null)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.GiveupQuest, 19), remote).ConfigureAwait(false);
                                break;
                            }
                            activeQuests.Remove(giveupQuest);
                            _store.GiveupQuest(active.Id, giveupQuestId);
                            await SendAsync(stream, GamePackets.GiveupQuestReply(giveupQuestId), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                            Log("INFO", "任务已放弃 quest={0}", giveupQuestId);
                            break;
                        case GamePackets.SetQuestTrigger:
                            if (active == null || request.Body.Length < 5)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.SetQuestTrigger, 22), remote).ConfigureAwait(false);
                                break;
                            }
                            var triggerQuestId = LittleEndian.ReadUInt16(request.Body, 2);
                            var triggerQuest = activeQuests.FirstOrDefault(value => value.QuestId == triggerQuestId);
                            if (triggerQuest == null)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.SetQuestTrigger, 22), remote).ConfigureAwait(false);
                                break;
                            }
                            QuestDefinition triggerDefinition;
                            if (!_questCatalog.TryGet(triggerQuestId, out triggerDefinition))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.SetQuestTrigger, 22), remote).ConfigureAwait(false);
                                break;
                            }
                            // Byte 4 is the condition value calculated by the
                            // Arad client from its matching QST.  This is vital
                            // for NPC, PvP and map-specific conditions which the
                            // emulator cannot infer from command 35 alone. Item
                            // collection remains server-authoritative.
                            var reportedTrigger = request.Body[4];
                            if (triggerDefinition.ConditionType == QuestConditionType.Seeking)
                                triggerQuest.Trigger = (byte)(QuestCatalog.IsSeekingConditionSatisfied(triggerDefinition, inventory.Values, active.Gold) ? 0 : 1);
                            else if (triggerDefinition.ConditionType == QuestConditionType.HuntMonster)
                            {
                                // 杀怪进度由服务端死亡事件维护。客户端会依据本地
                                // QST 反复上报目标总数（噪音公害为 16），若采信
                                // 就会把 12 只目标显示成 -4/12 并覆盖真实进度。
                                var required = QuestCatalog.GetHuntRequiredCount(triggerDefinition);
                                if (required > 0 && triggerQuest.Trigger > required)
                                    triggerQuest.Trigger = (byte)required;
                            }
                            else
                                triggerQuest.Trigger = reportedTrigger;
                            _store.SaveActiveQuest(active.Id, triggerQuest);
                            await SendAsync(stream, GamePackets.SetQuestTriggerReply(triggerQuestId, triggerQuest.Trigger), remote).ConfigureAwait(false);
                            Log("INFO", "任务进度 quest={0}, trigger={1}", triggerQuestId, triggerQuest.Trigger);
                            break;
                        case GamePackets.FinishQuest:
                            if (active == null || request.Body.Length < 4)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.FinishQuest, 22), remote).ConfigureAwait(false);
                                break;
                            }
                            var finishedQuestId = LittleEndian.ReadUInt16(request.Body, 2);
                            var finishedQuest = activeQuests.FirstOrDefault(value => value.QuestId == finishedQuestId);
                            QuestDefinition finishedDefinition;
                            if (finishedQuest == null || finishedQuest.Trigger != 0 ||
                                !_questCatalog.TryGet(finishedQuestId, out finishedDefinition))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.FinishQuest, 22), remote).ConfigureAwait(false);
                                Log("WARN", "拒绝未达成任务交付 quest={0}, trigger={1}", finishedQuestId,
                                    finishedQuest == null ? -1 : finishedQuest.Trigger);
                                break;
                            }
                            if (finishedDefinition.ConditionType == QuestConditionType.Seeking &&
                                !QuestCatalog.IsSeekingConditionSatisfied(finishedDefinition, inventory.Values, active.Gold))
                            {
                                finishedQuest.Trigger = 1;
                                _store.SaveActiveQuest(active.Id, finishedQuest);
                                await SendAsync(stream, GamePackets.SetQuestTriggerReply(finishedQuestId, 1), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.FinishQuest, 22), remote).ConfigureAwait(false);
                                Log("WARN", "拒绝收集条件不足的任务交付 quest={0}, gold={1}", finishedQuestId, active.Gold);
                                break;
                            }
                            activeQuests.Remove(finishedQuest);
                            completedQuestIds.Add(finishedQuestId);
                            _store.CompleteQuest(active.Id, finishedQuestId);
                            var questGoldCost = ConsumeQuestItems(finishedDefinition, inventory);
                            var questGoldReward = ApplyQuestRewards(finishedDefinition, request.Body, active, inventory);
                            active.Gold = (int)Math.Max(0, Math.Min(int.MaxValue,
                                (long)active.Gold - questGoldCost + questGoldReward));
                            _store.SaveGoldAndInventory(active.Id, active.Gold, inventory.Values);
                            await SendAsync(stream, GamePackets.FinishQuestReply(finishedQuestId), remote).ConfigureAwait(false);
                            await SendItemListPacketsAsync(stream, remote, inventory, active.Gold).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                            Log("INFO", "任务已完成 quest={0}, gold=-{1}/+{2}, rewards={3}+{4}", finishedQuestId,
                                questGoldCost, questGoldReward, finishedDefinition.RewardRows.Length,
                                finishedDefinition.RewardSelectionRows.Length);
                            break;
                        case GamePackets.ChangeSkillSlot:
                            // 命令 28（ChangeSkillSlot）：技能在技能面板内调整槽位。
                            // body[2-3]=skillOrSlotIndex(u16), body[4]=destinationSlot。
                            if (active != null && request.Body.Length >= 5)
                            {
                                ushort skillOrSlot = LittleEndian.ReadUInt16(request.Body, 2);
                                byte destSlot = request.Body[4];
                                var changedSkillPanel = skillOrSlot <= byte.MaxValue &&
                                    TryMoveSkillSlot(learnedSkills, (byte)skillOrSlot, destSlot);
                                if (changedSkillPanel)
                                    _store.SaveSkills(active.Id, active.SkillPoints, learnedSkills);
                                await SendAsync(stream, GamePackets.ChangeSkillSlotReply(skillOrSlot, destSlot), remote).ConfigureAwait(false);
                                Log("INFO", "命令 28：技能槽调整 skillOrSlot={0} -> slot {1}，changed={2}，已保存", skillOrSlot, destSlot, changedSkillPanel);
                            }
                            break;
                        case GamePackets.ChangeSkillQuickSlot:
                            // 命令 30（ChangeSkillQuickSlot）：技能拖入/拖出快捷栏。
                            // body[2]=sourceSlot, body[3]=destinationSlot。
                            if (active != null && request.Body.Length >= 4)
                            {
                                byte srcSlot = request.Body[2];
                                byte dstSlot = request.Body[3];
                                var changedQuickSlot = dstSlot == byte.MaxValue
                                    ? TryRemoveSkillQuickSlot(learnedSkills, srcSlot)
                                    : TryMoveSkillSlot(learnedSkills, srcSlot, dstSlot);
                                if (changedQuickSlot)
                                    _store.SaveSkills(active.Id, active.SkillPoints, learnedSkills);
                                await SendAsync(stream, GamePackets.ChangeSkillQuickSlotReply(srcSlot, dstSlot), remote).ConfigureAwait(false);
                                Log("INFO", "命令 30：技能快捷栏调整 slot {0} -> {1}，changed={2}，已保存", srcSlot, dstSlot, changedQuickSlot);
                            }
                            break;
                        case GamePackets.MoveItemSpace:
                            // 命令 19（MoveItemSpace）：拖动物品。Arad 使用 u32 itemId：
                            // sequence:u16 + source(list:u8,slot:u16,item:u32,count:u32)
                            // + destination(list:u8,slot:u16,item:u32) + trailing state。
                            // 客户端把物品拖向空格时会把空格放在“来源”字段，因此下面会还原真实移动方向。
                            // 响应中的 u16 slot 应为**目标槽位**（客户端据此完成移动）。
                            if (active != null && request.Body.Length >= 20)
                            {
                                byte srcListType = request.Body[2];
                                ushort srcSlot = LittleEndian.ReadUInt16(request.Body, 3);
                                var encodedSrcItemId = LittleEndian.ReadUInt32(request.Body, 5);
                                uint moveCount = LittleEndian.ReadUInt32(request.Body, 9);
                                byte dstListType = request.Body[13];
                                ushort dstSlot = LittleEndian.ReadUInt16(request.Body, 14);
                                var encodedDstItemId = LittleEndian.ReadUInt32(request.Body, 16);
                                ushort srcItemId = encodedSrcItemId <= ushort.MaxValue ? (ushort)encodedSrcItemId : (ushort)0;
                                ushort dstItemId = encodedDstItemId <= ushort.MaxValue ? (ushort)encodedDstItemId : (ushort)0;

                                // The Arad client addresses an avatar in the bag as list 1,
                                // but addresses that same avatar as list 3 while it is worn.
                                // Normal equipment also uses list 3, so select the backing
                                // container from the item ids before resolving either slot.
                                var requestContainsAvatar = _equipmentCatalog.IsAvatar(srcItemId) ||
                                    _equipmentCatalog.IsAvatar(dstItemId);
                                var sourceAvatarSide = srcListType == 1 ||
                                    (srcListType == 3 && srcSlot < AvatarWearSlotCount && requestContainsAvatar);
                                var destinationAvatarSide = dstListType == 1 ||
                                    (dstListType == 3 && dstSlot < AvatarWearSlotCount && requestContainsAvatar);
                                var avatarMove = (sourceAvatarSide || destinationAvatarSide) &&
                                    (requestContainsAvatar || srcListType == 1 || dstListType == 1);
                                Dictionary<ushort, InventoryItemRecord> sourceContainer = avatarMove && (srcListType == 1 || srcListType == 3)
                                    ? avatars : srcListType == 0 ? inventory : srcListType == 1 ? avatars : srcListType == 2 ? storage : srcListType == 3 ? equipment : null;
                                Dictionary<ushort, InventoryItemRecord> destinationContainer = avatarMove && (dstListType == 1 || dstListType == 3)
                                    ? avatars : dstListType == 0 ? inventory : dstListType == 1 ? avatars : dstListType == 2 ? storage : dstListType == 3 ? equipment : null;
                                var sourceContainerSlot = sourceContainer == avatars
                                    ? ResolveAvatarContainerSlot(srcListType, srcSlot) : srcSlot;
                                var destinationContainerSlot = destinationContainer == avatars
                                    ? ResolveAvatarContainerSlot(dstListType, dstSlot) : dstSlot;
                                InventoryItemRecord sourceItem = null;
                                InventoryItemRecord destinationItem = null;
                                if (sourceContainer != null) sourceContainer.TryGetValue(sourceContainerSlot, out sourceItem);
                                if (destinationContainer != null) destinationContainer.TryGetValue(destinationContainerSlot, out destinationItem);
                                var actualSourceSlot = sourceContainerSlot;
                                var actualDestinationSlot = destinationContainerSlot;
                                var actualSourceList = srcListType;
                                var actualDestinationList = dstListType;
                                var expectedSourceItemId = srcItemId;
                                var expectedDestinationItemId = dstItemId;

                                // The client puts the empty slot first when an
                                // occupied slot is dropped onto it.
                                if (sourceItem == null && srcItemId == 0 && destinationItem != null &&
                                    destinationItem.ItemId == dstItemId)
                                {
                                    sourceItem = destinationItem;
                                    destinationItem = null;
                                    actualSourceSlot = destinationContainerSlot;
                                    actualDestinationSlot = sourceContainerSlot;
                                    actualSourceList = dstListType;
                                    actualDestinationList = srcListType;
                                    expectedSourceItemId = dstItemId;
                                    expectedDestinationItemId = srcItemId;
                                    var swappedContainer = sourceContainer;
                                    sourceContainer = destinationContainer;
                                    destinationContainer = swappedContainer;
                                }

                                var sourceAvatarSlotValid = actualSourceList == 1 ? IsAvatarBagDbSlot(actualSourceSlot) :
                                    actualSourceList == 3 && IsAvatarWearDbSlot(actualSourceSlot) &&
                                    _equipmentCatalog.GetAvatarSlot(sourceItem == null ? (ushort)0 : sourceItem.ItemId) == actualSourceSlot;
                                var destinationAvatarSlotValid = actualDestinationList == 1 ? IsAvatarBagDbSlot(actualDestinationSlot) :
                                    actualDestinationList == 3 && IsAvatarWearDbSlot(actualDestinationSlot) &&
                                    _equipmentCatalog.GetAvatarSlot(sourceItem == null ? (ushort)0 : sourceItem.ItemId) == actualDestinationSlot;
                                var avatarMoveValid = avatarMove && sourceAvatarSlotValid && destinationAvatarSlotValid;
                                var equipmentMove = !avatarMove && sourceItem != null &&
                                    IsEquipmentItem(sourceItem.ItemId) &&
                                    (actualSourceList == 3 || actualDestinationList == 3);
                                var normalMoveValid = !avatarMove &&
                                    (equipmentMove
                                        ? IsValidEquipmentMoveSlot(actualSourceList, actualSourceSlot) &&
                                          IsValidEquipmentMoveSlot(actualDestinationList, actualDestinationSlot)
                                        : IsValidContainerSlot(actualSourceList, actualSourceSlot) &&
                                          IsValidContainerSlot(actualDestinationList, actualDestinationSlot)) &&
                                    ((equipmentMove && actualSourceList != 1 && actualDestinationList != 1) ||
                                     (!equipmentMove &&
                                      ((actualSourceList != 2 && actualDestinationList != 2) ||
                                       ((actualSourceList == 0 || actualSourceList == 2) &&
                                        (actualDestinationList == 0 || actualDestinationList == 2)))));
                                var valid = sourceContainer != null && destinationContainer != null &&
                                    (avatarMoveValid || normalMoveValid) &&
                                    !(actualSourceList == actualDestinationList && actualSourceSlot == actualDestinationSlot) &&
                                    sourceItem != null && sourceItem.ItemId != 0 &&
                                    (expectedSourceItemId == 0 || sourceItem.ItemId == expectedSourceItemId) &&
                                    (destinationItem == null || expectedDestinationItemId == 0 ||
                                        destinationItem.ItemId == expectedDestinationItemId) &&
                                    (avatarMoveValid || ((actualSourceList != 1 && actualDestinationList != 1) ||
                                        (actualSourceList == 1 && actualDestinationList == 1 && _equipmentCatalog.IsAvatar(sourceItem.ItemId)))) &&
                                    (avatarMoveValid || actualDestinationList != 3 || sourceItem.Count == 1) &&
                                    (avatarMoveValid || actualDestinationList != 1 || IsAvatarBagDbSlot(actualDestinationSlot) ||
                                        _equipmentCatalog.GetAvatarSlot(sourceItem.ItemId) == actualDestinationSlot);
                                if (!valid)
                                {
                                    await SendAsync(stream, GamePackets.MoveItemSpaceError(4, srcListType, dstListType), remote).ConfigureAwait(false);
                                    Log("WARN", "命令 19：拒绝无效物品移动 src={0}:{1}/{2} dst={3}:{4}/{5} body={6}", srcListType, srcSlot, srcItemId, dstListType, dstSlot, dstItemId, Hex(request.Body));
                                    break;
                                }

                                sourceContainer.Remove(actualSourceSlot);
                                destinationContainer.Remove(actualDestinationSlot);
                                if (moveCount > 0 && moveCount < sourceItem.Count && destinationItem == null)
                                {
                                    sourceItem.Count -= moveCount;
                                    sourceItem.Slot = actualSourceSlot;
                                    sourceContainer[actualSourceSlot] = sourceItem;
                                    destinationContainer[actualDestinationSlot] = new InventoryItemRecord
                                    {
                                        Slot = actualDestinationSlot, ItemId = sourceItem.ItemId,
                                        Count = moveCount, State = sourceItem.State,
                                        Durability = sourceItem.Durability, SealState = sourceItem.SealState
                                    };
                                }
                                else
                                {
                                    sourceItem.Slot = actualDestinationSlot;
                                    destinationContainer[actualDestinationSlot] = sourceItem;
                                    if (destinationItem != null)
                                    {
                                        destinationItem.Slot = actualSourceSlot;
                                        sourceContainer[actualSourceSlot] = destinationItem;
                                    }
                                }

                                if (avatarMove)
                                {
                                    _store.SaveAvatars(active.Id, avatars.Values);
                                }
                                else if (actualSourceList == 2 || actualDestinationList == 2)
                                {
                                    _store.SaveInventoryAndStorage(active.Id, inventory.Values, storage.Values);
                                    _store.SaveEquipment(active.Id, equipment.Values);
                                }
                                else if (actualSourceList == 3 || actualDestinationList == 3)
                                {
                                    // Wearing/replacing equipment is one logical move.  Persist both
                                    // containers atomically so a restart cannot duplicate or lose an item.
                                    _store.SaveInventoryAndEquipment(active.Id, inventory.Values, equipment.Values);
                                }
                                else
                                {
                                    _store.SaveInventory(active.Id, inventory.Values);
                                }
                                await SendAsync(stream, GamePackets.MoveItemSpaceReply(srcListType, srcSlot, moveCount, dstListType, dstSlot), remote).ConfigureAwait(false);

                                if (actualSourceList == 2 || actualDestinationList == 2)
                                {
                                    await SendItemListPacketsAsync(stream, remote, inventory, active.Gold).ConfigureAwait(false);
                                    await SendAsync(stream, GamePackets.StorageItemList(storage.Values), remote).ConfigureAwait(false);
                                }
                                else
                                {
                                    // MoveItemSpaceReply is the complete client-side move operation for
                                    // bag, equipment and avatar containers. Replaying USERINFO mode 0 to
                                    // the same actor after wearing/removing a weapon is not an appearance
                                    // update in this client: it is re-entered as qualification/GM-event
                                    // data and produces the bogus 808464432-minute message. Do not emit
                                    // any follow-up notification for an ordinary successful move.
                                }
                                Log("INFO", "命令 19：已移动物品 itemId={0} {1}:{2} -> {3}:{4}，背包与装备已保存", sourceItem.ItemId, actualSourceList, actualSourceSlot, actualDestinationList, actualDestinationSlot);
                            }
                            else
                            {
                                var invalidSourceList = request.Body != null && request.Body.Length > 2
                                    ? request.Body[2] : (byte)0;
                                var invalidDestinationList = request.Body != null && request.Body.Length > 13
                                    ? request.Body[13] : (byte)0;
                                await SendAsync(stream, GamePackets.MoveItemSpaceError(4,
                                    invalidSourceList, invalidDestinationList), remote).ConfigureAwait(false);
                                Log("WARN", "命令 19：拒绝格式不完整的物品移动 body={0}", Hex(request.Body));
                            }
                            break;
                        case GamePackets.UseTownItem:
                            if (active == null || request.Body.Length < 4)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.UseTownItem, 1), remote).ConfigureAwait(false);
                                break;
                            }
                            var townItemSlot = LittleEndian.ReadUInt16(request.Body, 2);
                            InventoryItemRecord townItem;
                            TownConsumableEffect townEffect;
                            if (!inventory.TryGetValue(townItemSlot, out townItem) || townItem.Count == 0 ||
                                !TownConsumableCatalog.TryGet(townItem.ItemId, out townEffect))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.UseTownItem, 2), remote).ConfigureAwait(false);
                                Log("WARN", "命令 32：拒绝未知或缺失的城镇道具 slot={0}", townItemSlot);
                                break;
                            }
                            var skillPointsBeforeTownItem = active.SkillPoints;
                            ApplyTownConsumable(active, townEffect, 1);
                            var levelUpSkillPoints = townEffect.Type == TownConsumableEffectType.Experience
                                ? Math.Max(0, active.SkillPoints - skillPointsBeforeTownItem) : 0;
                            townItem.Count--;
                            if (townItem.Count == 0) inventory.Remove(townItemSlot);
                            _store.SaveTownConsumableState(active, inventory.Values);
                            await SendAsync(stream, GamePackets.UseTownItemReply(townItemSlot,
                                TownConsumableCatalog.GetProtocolType(townEffect.Type),
                                TownConsumableCatalog.GetWireAmount(townEffect),
                                (ushort)Math.Min(ushort.MaxValue, levelUpSkillPoints)), remote).ConfigureAwait(false);
                            InventoryItemRecord remainingTownItem;
                            inventory.TryGetValue(townItemSlot, out remainingTownItem);
                            await SendAsync(stream, GamePackets.UpdateItem(0, townItemSlot,
                                remainingTownItem), remote).ConfigureAwait(false);
                            // Command 32's native client handler consumes the stack and applies the
                            // returned EXP/SP/stat delta itself.  Sending Notification 14 or a skill
                            // snapshot here repeats that refresh and makes the Arad client tear down
                            // the currently open inventory window.
                            Log("INFO", "命令 32：城镇道具生效 itemId={0} slot={1}, effect={2}, amount={3}, remaining={4}",
                                townItem.ItemId, townItemSlot, townEffect.Type, townEffect.Amount, townItem.Count);
                            break;
                        case GamePackets.ConsumeItems:
                            // 命令 18（ConsumeItems）：使用消耗品/扣除技能材料。
                            // ARAD body[2]=listType(0=主背包), body[3]=count，每条 10B：
                            //   u16 operation + u16 slot + u16 itemId + q16.16 requestedCount
                            // 响应：{1, listType, count, [u16 slot + u32 consumed]×N}
                            if (active != null && request.Body.Length >= 4)
                            {
                                byte listType;
                                ConsumeItemRequestRecord[] requests;
                                if (active != null && GamePackets.TryReadConsumeItems(request.Body, out listType, out requests) && listType == 0)
                                {
                                    var consumptions = new List<InventoryConsumptionRecord>(requests.Length);
                                    var progressionChanged = false;
                                    var statsChanged = false;
                                    var leveled = false;
                                    var characterStateChanged = false;
                                    foreach (var itemRequest in requests)
                                    {
                                        InventoryItemRecord candidate;
                                        InventoryItemCategory category;
                                        if (itemRequest.Operation != 2 ||
                                            !inventory.TryGetValue(itemRequest.Slot, out candidate) ||
                                            candidate == null || candidate.Count == 0 ||
                                            !_itemCategoryCatalog.TryGetCategory(candidate.ItemId, out category) ||
                                            category == InventoryItemCategory.Equipment)
                                            continue;

                                        InventoryItemRecord consumedItem;
                                        uint consumed;
                                        uint remaining;
                                        if (!TryConsumeInventoryItem(inventory, itemRequest, out consumedItem,
                                            out consumed, out remaining))
                                            continue;

                                        TownConsumableEffect effect;
                                        if (TownConsumableCatalog.TryGet(consumedItem.ItemId, out effect))
                                        {
                                            var oldLevel = active.Level;
                                            ApplyTownConsumable(active, effect, consumed);
                                            leveled |= active.Level != oldLevel;
                                            progressionChanged |= effect.Type == TownConsumableEffectType.SkillPoints || effect.Type == TownConsumableEffectType.Experience;
                                            statsChanged |= effect.Type != TownConsumableEffectType.SkillPoints && effect.Type != TownConsumableEffectType.Experience;
                                            characterStateChanged = true;
                                        }
                                        consumptions.Add(new InventoryConsumptionRecord { Slot = itemRequest.Slot, ConsumedCount = consumed });
                                        Log("INFO", "命令 18：使用物品 itemId={0} slot={1}, 消耗 {2}, 剩余 {3}",
                                            consumedItem.ItemId, itemRequest.Slot, consumed, remaining);
                                    }
                                    if (characterStateChanged)
                                        _store.SaveTownConsumableState(active, inventory.Values);
                                    else if (consumptions.Count > 0)
                                        _store.SaveInventory(active.Id, inventory.Values);
                                    await SendAsync(stream, GamePackets.ConsumeItemsReply(listType, consumptions), remote).ConfigureAwait(false);
                                    foreach (var consumption in consumptions)
                                    {
                                        InventoryItemRecord refreshed;
                                        inventory.TryGetValue(consumption.Slot, out refreshed);
                                        await SendAsync(stream, GamePackets.UpdateItem(listType, consumption.Slot, refreshed), remote).ConfigureAwait(false);
                                    }
                                    if (progressionChanged)
                                        await SendAsync(stream, GamePackets.ExperienceGained(active.Level, CumulativeExperience(active), 0, 0,
                                            (ushort)Math.Min(active.SkillPoints, ushort.MaxValue)), remote).ConfigureAwait(false);
                                    if (progressionChanged)
                                        await SendAsync(stream, GamePackets.DefaultSkills(active.Job, (ushort)Math.Min(active.SkillPoints, ushort.MaxValue), learnedSkills, _skillCatalog), remote).ConfigureAwait(false);
                                    if (statsChanged || leveled)
                                        await SendAsync(stream, GamePackets.CharacterDetails(userId, CumulativeExperience(active), active.Job,
                                            equipment.Values, learnedSkills, _characterStatCatalog.Get(active), _skillCatalog,
                                            avatars.Values), remote).ConfigureAwait(false);
                                }
                            }
                            break;
                        case GamePackets.BuySkill:
                            // 命令 31：body[2]=技能 ID。客户端本身负责职业、
                            // 等级与前置技能校验；服务端保存等级/槽位并返回
                            // Arad handler @0x412A47 的五字段成功布局。
                            if (active != null && request.Body.Length >= 3)
                            {
                                byte skillId = request.Body[2];
                                byte slotForSkill;
                                byte newLevel;
                                int skillPurchaseCost;
                                if (!TryPurchaseSkill(_skillCatalog, active, learnedSkills, skillId,
                                    out slotForSkill, out newLevel, out skillPurchaseCost))
                                {
                                    await SendAsync(stream, GamePackets.BuySkillFailureReply(), remote).ConfigureAwait(false);
                                    Log("WARN", "命令 31：拒绝学习技能 id={0}, cost={1}, SP={2}", skillId, skillPurchaseCost, active.SkillPoints);
                                    break;
                                }
                                _store.SaveSkills(active.Id, active.SkillPoints, learnedSkills);
                                await SendAsync(stream, GamePackets.BuySkillReply((ushort)Math.Min(active.SkillPoints, ushort.MaxValue), slotForSkill, skillId, newLevel), remote).ConfigureAwait(false);
                                Log("INFO", "命令 31：已学习技能 id={0} level={1} slot={2}, cost={3}, SP={4}", skillId, newLevel, slotForSkill, skillPurchaseCost, active.SkillPoints);
                            }
                            else
                            {
                                Log("WARN", "命令 31：无效学技能请求");
                            }
                            break;
                        case GamePackets.EnterSelectDungeon:
                            if (active != null)
                            {
                                // 记录进副本前位置，返回城镇时恢复到副本门口。
                                returnTownId = active.TownId; returnAreaId = active.AreaId;
                                returnX = active.X; returnY = active.Y; returnDirection = active.Direction;
                                // Refresh the persisted u16 dungeon list immediately before
                                // opening the selector.  This follows latest_ref_server and
                                // keeps high-id unlocks such as Vilmark (1500) visible.
                                Interlocked.Exchange(ref tracedDungeonIndex, 0);
                                foreach (var packet in BuildEnterDungeonSelectionPackets(userId,
                                    BuildEffectiveDungeonPermissions(_dungeonCatalog,
                                        _store.LoadDungeonProgress(active.Id))))
                                    await SendAsync(stream, packet, remote).ConfigureAwait(false);
                                Log("INFO", "进入地下城选择流程：角色={0}（副本门口 {1},{2}）", active.Name, active.X, active.Y);
                            }
                            break;
                        case GamePackets.SelectDungeon:
                            ushort dungeonId; byte difficulty; byte dungeonOption;
                            if (active == null || !GamePackets.TryReadSelectDungeon(request.Body, out dungeonId, out difficulty, out dungeonOption))
                            {
                                Log("WARN", "SELECT_DUNGEON 请求无效: {0}", Hex(request.Body));
                                break;
                            }
                            if (dungeonId == GamePackets.TutorialDungeonId && GameServerPacket.TutorialEnabled)
                            {
                                CancelDeathReturnTimer();
                                // The tutorial sends START_MAP directly, so arm the same
                                // one-shot initializer before its first map packet as well.
                                ArmClientRoomPatch(remote, dungeonId, 0, 0);
                                await SendAsync(stream, GamePackets.TutorialDungeonInfo(difficulty), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.TutorialStartMap(), remote).ConfigureAwait(false);
                                groundDrops.Clear();
                                roomDropLedger.Clear();
                                pendingGroundRestorePackets = null;
                                lorienRun.Stop();
                                tutorialRun.Start();
                                settlement.Reset();
                                Interlocked.Exchange(ref tracedDungeonIndex, dungeonId);
                                Log("INFO", "已启动新手教程副本 dungeon={0}, map={1}, difficulty={2}, option={3}", dungeonId, GamePackets.TutorialMapId, difficulty, dungeonOption);
                            }
                            else if (dungeonId == GamePackets.TutorialDungeonId)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.SelectDungeon, 4), remote).ConfigureAwait(false);
                                Log("INFO", "已忽略已关闭的新手教程副本请求 dungeon={0}", dungeonId);
                            }
                            else if (dungeonId == 1 || dungeonId == 2)
                            {
                                // 洛兰（dungeon 1，Act1/Lorien.dgn）：3x2 迷宫。
                                // 洛兰深处（dungeon 2，LorienInside.dgn）。
                                // 起始/首领房、房间 mapId 与怪物行全部由
                                // DungeonCatalog（PVF dungeon/*.dgn + map/*.map）
                                // 解析，不再依赖硬编码布局。
                                pendingGroundRestorePackets = null;
                                await StartDungeonAsync(stream, remote, active, userId, dungeonId, difficulty, dungeonOption, tutorialRun, lorienRun, groundDrops, roomDropLedger, settlement).ConfigureAwait(false);
                                if (lorienRun.IsActive)
                                {
                                    Interlocked.Exchange(ref tracedDungeonIndex, lorienRun.DungeonId);
                                    // The initial room is constructed by the local ARAD path.
                                    // Queue visuals only after its first FINISH_LOADING request.
                                    pendingGroundRestorePackets = null;
                                }
                            }
                            else
                            {
                                // 其余副本（格兰之森 dungeon 7 等）：同样走通用
                                // DungeonCatalog 流程，保证客户端能正常加载地图。
                                pendingGroundRestorePackets = null;
                                await StartDungeonAsync(stream, remote, active, userId, dungeonId, difficulty, dungeonOption, tutorialRun, lorienRun, groundDrops, roomDropLedger, settlement).ConfigureAwait(false);
                                if (lorienRun.IsActive)
                                {
                                    Interlocked.Exchange(ref tracedDungeonIndex, lorienRun.DungeonId);
                                    // See the first branch above: the first room has no server
                                    // START_MAP packet, so its drops wait for FINISH_LOADING.
                                    pendingGroundRestorePackets = null;
                                }
                            }
                            break;
                        case GamePackets.CreateCharacter:
                            byte job; string name;
                            if (!GamePackets.TryReadCreateCharacter(request.Body, out job, out name))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.CreateCharacter, 2), remote).ConfigureAwait(false);
                                break;
                            }
                            // ARAD 客户端在 13 段参数里已带账号，但可能不先发 LOGIN；
                            // 创建角色前必须确保账号存在，否则外键约束会被误判为“角色名已存在”。
                            if (accountId == 0)
                                accountId = _store.EnsureAccount(accountName, _options.Password);
                            var created = _store.CreateCharacter(accountId, name, job);
                            await SendAsync(stream, created.Success ? GamePackets.CommandResult(GamePackets.CreateCharacter, true) : GamePackets.CommandError(GamePackets.CreateCharacter, 2), remote).ConfigureAwait(false);
                            Log(created.Success ? "INFO" : "WARN", created.Success ? "已创建角色: {0}, job={1}" : "创建角色失败: {0}", created.Success ? name : created.Error, job);
                            if (created.Success)
                            {
                                // Keep the China-server order used by the 86
                                // implementation: ACK first, refreshed roster
                                // second, after the create UI releases its
                                // pending command state.
                                await SendRosterAsync(stream, remote, accountId).ConfigureAwait(false);
                                if (GameServerPacket.UseLegacyChannelHeader)
                                    pendingAutoEnter = created.Character;
                            }
                            break;
                        case 122:
                            // ARAD 客户端创建角色后上传配置/列表（u16 8 + u32
                            // 数量等）。客户端只按成功字节消费响应，回复 01 即可，
                            // 否则客户端在创建完成后立即断开连接。
                            await SendAsync(stream, GamePackets.CommandResult(122, true), remote).ConfigureAwait(false);
                            Log("INFO", "ARAD 命令 122 已确认");
                            if (GameServerPacket.UseLegacyChannelHeader && pendingAutoEnter != null)
                            {
                                var autoCharacter = pendingAutoEnter;
                                pendingAutoEnter = null;
                                active = await EnterTownAfterCreateAsync(stream, remote, autoCharacter, userId,
                                    inventory, storage, equipment, avatars, learnedSkills, activeQuests, completedQuestIds).ConfigureAwait(false);
                                areaRosterInitialized = false;
                            }
                            break;
                        case GamePackets.DeleteCharacter:
                            await SendAsync(stream, GamePackets.CommandError(GamePackets.DeleteCharacter, 2), remote).ConfigureAwait(false);
                            break;
                        case 134:
                            // ARAD 客户端的城镇用户列表请求（等价于 Arad 的
                            // TownInit82/130）。响应 = 成功字节 + 记录数 + 每条
                            // DSTR 名字 + 3×u8。随后补发 FinishLoaded 结束加载。
                            await SendAsync(stream, BuildAradTownUserList(active), remote).ConfigureAwait(false);
                            Log("INFO", "ARAD 命令 134 已返回城镇用户列表（active={0}）", active == null ? "null" : active.Name);
                            // The ARAD town-user-list callback is still inside its
                            // connection-transition handler here.  Pushing the quest
                            // list before that callback has completed makes this
                            // client report a generic connection fault (command 122)
                            // and close the live game socket.  The same list is sent
                            // by the normal town-initialisation/quest paths instead.
                            if (active != null && !GameServerPacket.UseLegacyChannelHeader)
                                await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.FinishLoaded(), remote).ConfigureAwait(false);
                            break;
                        case 66:
                            // ARAD 客户端选角后上报的加载完成请求；无专用响应
                            // handler，补发 FinishLoaded 让客户端结束加载等待。
                            await SendAsync(stream, GamePackets.CommandResult(66, true), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.FinishLoaded(), remote).ConfigureAwait(false);
                            if (active != null)
                            {
                                await SendAsync(stream, GamePackets.NormalFatigueHudState(), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.InfiniteFatigueUpdate(), remote).ConfigureAwait(false);
                            }
                            Log("INFO", "ARAD 命令 66 已确认并补发 FinishLoaded");
                            break;
                        case 17:
                            await SendAsync(stream, GamePackets.CommandResult(17, true), remote).ConfigureAwait(false);
                            Log("INFO", "ARAD 命令 17 已确认");
                            break;
                        case 43:
                            // 角色死亡上报（ref server 命令 43）：发送 DieState
                            // 通知，10 秒后教程原地复活、普通副本回城。
                            {
                                var rewardCount = 0u;
                                InventoryItemRecord goldItem;
                                if (inventory.TryGetValue(1, out goldItem) && goldItem != null && goldItem.ItemId == 1)
                                    rewardCount = goldItem.Count;
                                await SendAsync(stream, GamePackets.DieState(userId, 0), remote).ConfigureAwait(false);
                                CancelDeathReturnTimer();
                                var epoch = deathReturnEpoch;
                                var cts = new CancellationTokenSource();
                                deathReturnCts = cts;
                                var deathToken = cts.Token;
                                var deathRemote = remote;
                                var deathStream = stream;
                                _ = Task.Run(async delegate
                                {
                                    try
                                    {
                                        await Task.Delay(10000, deathToken).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        return;
                                    }
                                    if (epoch != deathReturnEpoch || active == null ||
                                        (!tutorialRun.IsActive && !lorienRun.IsActive))
                                        return;
                                    try
                                    {
                                        if (tutorialRun.IsActive && !active.TutorialStarted)
                                        {
                                            await SendAsync(deathStream, GamePackets.DieState(userId, 1), deathRemote).ConfigureAwait(false);
                                            Log("INFO", "教程死亡倒计时结束：原地复活，继续锁定回城");
                                        }
                                        else
                                        {
                                            groundDrops.Clear();
                                            roomDropLedger.Clear();
                                            pendingGroundRestorePackets = null;
                                            await ReturnToTownAsync(deathStream, deathRemote, active, userId, false,
                                                tutorialRun, lorienRun, equipment, avatars, learnedSkills,
                                                "死亡倒计时回城", returnTownId, returnAreaId, returnX, returnY, returnDirection).ConfigureAwait(false);
                                            Interlocked.Exchange(ref tracedDungeonIndex, 0);
                                            Log("INFO", "死亡倒计时结束：已返回城镇");
                                        }
                                    }
                                    catch (Exception deathEx)
                                    {
                                        Log("WARN", "死亡倒计时回城失败: {0}", deathEx.Message);
                                    }
                                });
                                Log("INFO", "命令 43：角色死亡，已发送 DIE_STATE actor={0} coins={1}，10秒后复活/回城", userId, rewardCount);
                            }
                            break;
                        case 216:
                            // ARAD 客户端城镇/教程状态同步（60 个模块状态）。
                            // 参考服务端与旧版适配实现均按成功字节确认，否则
                            // 客户端会停留在状态同步等待。
                            await SendAsync(stream, GamePackets.CommandResult(216, true), remote).ConfigureAwait(false);
                            Log("INFO", "ARAD 命令 216 已确认（城镇/教程状态同步）");
                            break;
                        case 157:
                            // ARAD 客户端教程/副本进入上报（u32 模式 + 保留字段）。
                            // 参考 ref server 对 Arad 客户端命令 40（FinishLoading）的
                            // 处理：确认成功字节后再补发通知 30，客户端才结束加载等待、
                            // 继续教程地图/漫画流程。通知 31（EnableClearDungeon）只在
                            // 房间怪物全灭后下发，不能在这里提前发。
                            // 同样补发疲劳 HUD/无限疲劳更新（同命令 40），避免
                            // 教程流程因状态缺失导致客户端主动断开。
                            await SendAsync(stream, GamePackets.CommandResult(157, true), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.FinishLoaded(), remote).ConfigureAwait(false);
                            if (active != null)
                            {
                                await SendAsync(stream, GamePackets.NormalFatigueHudState(), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.InfiniteFatigueUpdate(), remote).ConfigureAwait(false);
                            }
                            Log("INFO", "ARAD 命令 157 已确认并补发 FinishLoaded（参考 ref server 命令 40 流程）");
                            break;
                        case 190:
                            await SendAsync(stream, new GameServerPacket(GamePackets.Command, 190,
                                new byte[] { 1, 0, 0, 0, 0 }), remote).ConfigureAwait(false);
                            Log("INFO", "ARAD 命令 190 已确认");
                            break;
                        case GamePackets.SelectCharacter:
                            pendingAutoEnter = null;
                            var characters = _store.ListCharacters(accountId);
                            var slot = request.Body.Length > 2 ? request.Body[2] : (byte)0;
                            active = characters.FirstOrDefault(x => x.Slot == slot);
                            if (active == null)
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.SelectCharacter, 2), remote).ConfigureAwait(false);
                                break;
                            }
                            // During a town-to-channel change the Arad client reuses its
                            // visible roster and sends SELECT_CHARACTER without first
                            // issuing GET_USER_INFO.  Its new channel session still needs
                            // notification 2 to rebuild the character-selection state;
                            // without it, the SELECT_CHARACTER reply is received but the
                            // client never continues with town command 82.
                            if (!userInfoSent)
                            {
                                await SendRosterAsync(stream, remote, accountId).ConfigureAwait(false);
                                userInfoSent = true;
                                Log("INFO", "频道重连选角前已补发 USER_INFO 角色列表");
                            }
                            LoadAndNormalizeSkills(active, learnedSkills);
                            LoadInventory(active.Id, inventory);
                            LoadStorage(active.Id, storage);
                            LoadEquipment(active.Id, active.Job, equipment);
                            LoadAvatars(active.Id, active.Job, avatars);
                            LoadQuests(active.Id, activeQuests, completedQuestIds);
                            CompleteTutorialForDirectTown(active);
                            // A town-to-channel change keeps the character roster in the
                            // client and sends SELECT_CHARACTER immediately after LOGIN,
                            // without the normal GET_USER_INFO/UI delay.  Sending the town
                            // initialization in that teardown window leaves the Arad client
                            // on its "connecting" modal.  Preserve the same 500 ms settling
                            // interval already used by the auto-select path.
                            if (loginResponseUtc != DateTime.MinValue)
                            {
                                var loginElapsed = DateTime.UtcNow - loginResponseUtc;
                                var settleDelay = ChannelLoginSettleMilliseconds - (int)loginElapsed.TotalMilliseconds;
                                if (settleDelay > 0)
                                    await Task.Delay(settleDelay).ConfigureAwait(false);
                            }
                            if (TownSpawnCatalog.ApplySeriaSpawn(active))
                            {
                                _store.UpdatePosition(active.Id, active.TownId, active.AreaId, active.X, active.Y, active.Direction);
                                Log("INFO", "角色 {0} 已迁移到当前城镇 Gate: town={1}, area={2}, ({3},{4})", active.Name, active.TownId, active.AreaId, active.X, active.Y);
                            }
                            await SendAsync(stream, request.IsCitrisCommand
                                ? GamePackets.CitrisSelectOk(active, userId)
                                : GameServerPacket.UseLegacyChannelHeader
                                    ? GamePackets.AradSelectOk(active, userId, activeQuests)
                                    : GamePackets.SelectOk(active, userId, activeQuests), remote).ConfigureAwait(false);
                            if (!request.IsCitrisCommand)
                                await SendCharacterInitializationAsync(stream, remote, active, userId, inventory, storage, equipment, avatars, learnedSkills, activeQuests, completedQuestIds).ConfigureAwait(false);
                            else
                                await SendCitrisCharacterInitializationAsync(stream, remote, active, userId, inventory, storage, equipment, avatars, learnedSkills, activeQuests, completedQuestIds).ConfigureAwait(false);
                            areaRosterInitialized = false;
                            Log("INFO", "选择角色 {0}，已发送 SELECT_CHARACTER 与城镇初始化流", active.Name);
                            break;
                        case GamePackets.ReturnSelectCharacter:
                            active = null; learnedSkills.Clear(); inventory.Clear(); storage.Clear(); equipment.Clear(); avatars.Clear(); activeQuests.Clear(); completedQuestIds.Clear(); areaRosterInitialized = false; tutorialRun.Stop(); groundDrops.Clear(); roomDropLedger.Clear(); pendingGroundRestorePackets = null;
                            Interlocked.Exchange(ref tracedDungeonIndex, -1);
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.ReturnSelectCharacter, true), remote).ConfigureAwait(false);
                            await SendRosterAsync(stream, remote, accountId).ConfigureAwait(false);
                            userInfoSent = true;
                            break;
                        case GamePackets.SetUserPosition:
                            if (active != null && request.Body.Length >= 9)
                            {
                                var reportedX = LittleEndian.ReadInt16(request.Body, 2);
                                var reportedY = LittleEndian.ReadInt16(request.Body, 4);
                                var reportedPosition = TownSpawnCatalog.GetReportedTownPosition(
                                    active.TownId, active.AreaId, reportedX, reportedY, request.Body[6]);
                                if (!areaRosterInitialized)
                                {
                                    // ARAD initially reports the Gate return marker
                                    // (474,234), which is outside Gate.map's only town
                                    // movable rectangle (330,324,289,24).  The local actor
                                    // must instead be activated at the rectangle centre.
                                    TownSpawnCatalog.ApplySeriaSpawn(active);
                                    Log("INFO", "首次 SET_USER_POSITION：客户端上报 ({0},{1})，采用赛丽亚房间可行走坐标 town={2}, area={3}, ({4},{5})",
                                        reportedX, reportedY, active.TownId, active.AreaId, active.X, active.Y);
                                }
                                else
                                {
                                    active.TownId = reportedPosition.TownId;
                                    active.AreaId = reportedPosition.AreaId;
                                    active.X = reportedPosition.X;
                                    active.Y = reportedPosition.Y;
                                    active.Direction = reportedPosition.Direction;
                                }
                                _store.UpdatePosition(active.Id, active.TownId, active.AreaId, active.X, active.Y, active.Direction);
                                if (!areaRosterInitialized)
                                {
                                    // The first SET_USER_POSITION is the end of the
                                    // character-selection initialization. Use the same
                                    // complete activation sequence. USER_STATE(0)
                                    // preserves the local actor; START_MAP remains
                                    // dungeon-only.
                                    Interlocked.Exchange(ref tracedDungeonIndex, 0);
                                    foreach (var packet in GamePackets.InitialTownActivation(active, userId))
                                        await SendAsync(stream, packet, remote).ConfigureAwait(false);
                                    // ref_Server activates Seria/Gate.map with only
                                    // USER_STATE -> USER_AREA -> AREA_USERS.  There is
                                    // no notification 87/CNWarRoom in this flow.
                                    await Task.Delay(100).ConfigureAwait(false);
                                    await SendAsync(stream, TownCharacterPacket(active, userId, equipment, avatars), remote).ConfigureAwait(false);
                                    // The reference server deliberately replays mode 1
                                    // after mode 0.  The first copy belongs to character
                                    // selection; this copy completes the scene-local
                                    // actor after AREA_USERS has created it.
                                    await SendAsync(stream, CharacterDetailsPacket(active, userId, equipment, avatars, learnedSkills), remote).ConfigureAwait(false);
                                    if (request.IsCitrisCommand)
                                    {
                                        foreach (var equipped in equipment.Values.OrderBy(value => value.Slot))
                                            await SendAsync(stream, GamePackets.CitrisUpdateItem(3, equipped.Slot, equipped), remote).ConfigureAwait(false);
                                    }
                                    // STAMINA 必须传 100：不发会进入虚弱状态。
                                    // 100 会触发一次 STAMINA_RECOVER 恢复音效，
                                    // 属已知副作用，保持与回城流程一致。
                                    await SendAsync(stream, GamePackets.StaminaUpdate(100), remote).ConfigureAwait(false);
                                    // 疲劳 HUD 初始化：Arad 客户端在城镇实体出现后
                                    // 需要通知 120（普通疲劳模式）和通知 36（疲劳值），
                                    // 否则进城初始化会在约 1 秒后被客户端判定失败并退出。
                                    await SendAsync(stream, GamePackets.NormalFatigueHudState(), remote).ConfigureAwait(false);
                                    await SendAsync(stream, GamePackets.InfiniteFatigueUpdate(), remote).ConfigureAwait(false);
                                    // The first selection path enters Seria's
                                    // room through SET_USER_POSITION rather
                                    // than SET_USER_AREA.  It must still send
                                    // the available-quest list here; otherwise
                                    // the client only displays quests after a
                                    // later town-area transition.
                                    await SendAsync(stream, GamePackets.AcceptableQuests(
                                        _questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                                    // 通知 136 是教程/漫画入口，不是普通城镇渲染所需的激活包。
                                    // ARAD 直进赛丽亚房间时发送它会把客户端切回教程场景：HUD
                                    // 仍然可见，但地图和角色世界层不会创建。只在明确启用教程时发送。
                                    // Notification 136 is also the client's final
                                    // character-control activation event.  With the
                                    // completed module mask it does not open the
                                    // tutorial; omitting it leaves the town actor
                                    // visible but permanently input-locked.
                                    if (GameServerPacket.UseLegacyChannelHeader || GameServerPacket.TutorialEnabled)
                                        await SendAsync(stream, GamePackets.TutorialBootstrapPacket(), remote).ConfigureAwait(false);
                                    areaRosterInitialized = true;
                                    Log("INFO", "首次 SET_USER_POSITION：赛丽亚房间实体与移动模块已激活");
                                }
                            }
                            break;
                        case GamePackets.SetUserArea:
                            if (active != null && request.Body.Length >= 8)
                            {
                                active.TownId = request.Body[2]; active.AreaId = request.Body[3]; active.X = LittleEndian.ReadInt16(request.Body, 4); active.Y = LittleEndian.ReadInt16(request.Body, 6);
                                _store.UpdatePosition(active.Id, active.TownId, active.AreaId, active.X, active.Y, active.Direction);
                                // 与登录/回城流程一致：先 USER_STATE(0) 使角色
                                // 状态正常化，避免 Arad 客户端显示虚弱图标。
                                await SendAsync(stream, GamePackets.UserStateChanged(userId, 0), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.Area(active, userId), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.AreaRoster(active, userId), remote).ConfigureAwait(false);
                                await Task.Delay(100).ConfigureAwait(false);
                                await SendAsync(stream, TownCharacterPacket(active, userId, equipment, avatars), remote).ConfigureAwait(false);
                                foreach (var equipped in equipment.Values.OrderBy(value => value.Slot))
                                    await SendAsync(stream, request.IsCitrisCommand
                                        ? GamePackets.CitrisUpdateItem(3, equipped.Slot, equipped)
                                        : GamePackets.UpdateItem(3, equipped.Slot, equipped), remote).ConfigureAwait(false);
                                await SendAsync(stream, CharacterDetailsPacket(active, userId, equipment, avatars, learnedSkills), remote).ConfigureAwait(false);
                                // 必须发 STAMINA(100) 恢复体力：不发会导致客户端
                                // 体力 0、进入虚弱状态（会伴随一次恢复音效）。
                                await SendAsync(stream, GamePackets.StaminaUpdate(100), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.NormalFatigueHudState(), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.InfiniteFatigueUpdate(), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                                areaRosterInitialized = true;
                            }
                            break;
                        case GamePackets.FinishLoading:
                            // 55 服务端每次命令 40 都回 Command40{1} + 通知 30
                            // （FinishLoading）。此前只在首次发通知 30，导致
                            // 换房（命令 48）后缺通知 30，客户端画面不渲染
                            // （黑屏但有音效、鼠标可动）。改为每次下发。
                            // 参考 ref server：命令 40 后还补发疲劳 HUD 与
                            // 无限疲劳更新，否则教程/城镇流程可能因状态缺失
                            // 而由客户端主动断开。
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.FinishLoading, true), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.FinishLoaded(), remote).ConfigureAwait(false);
                            if (active != null)
                            {
                                await SendAsync(stream, GamePackets.NormalFatigueHudState(), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.InfiniteFatigueUpdate(), remote).ConfigureAwait(false);
                            }
                            // Ordinary-monster drops are rolled when ARAD opens a path gate.
                            // They are transferred into the target room and become visible
                            // only after that room finishes loading.
                            if (pendingGroundRestorePackets != null)
                            {
                                var restorePackets = pendingGroundRestorePackets;
                                pendingGroundRestorePackets = null;
                                foreach (var restorePacket in restorePackets)
                                    await SendAsync(stream, restorePacket, remote).ConfigureAwait(false);
                                Log("INFO", "GROUND_DROP restored after FinishLoading count={0}", restorePackets.Length);
                            }
                            break;
                        case GamePackets.UseSkill:
                            // 命令 41（UseSkill）：客户端使用技能/攻击。55 回
                            // Command 41 {1}（客户端本地执行战斗模拟）。
                            if (active != null)
                            {
                                await SendAsync(stream, GamePackets.UseSkillReply(), remote).ConfigureAwait(false);
                            }
                            break;
                        case 20:
                        {
                            // 整理背包（ref server SortItem）。仅在城镇可用，副本内拒绝。
                            byte sortListType;
                            if (active == null || !GamePackets.TryReadSortItem(request.Body, out sortListType) ||
                                sortListType != 0 || tutorialRun.IsActive || lorienRun.IsActive)
                            {
                                await SendAsync(stream, GamePackets.SortItemError(
                                    (byte)(request.Body != null && request.Body.Length > 2 ? request.Body[2] : 0), 17), remote).ConfigureAwait(false);
                                break;
                            }
                            ushort[] sortedChangedSlots;
                            if (!_itemCategoryCatalog.TrySortMainInventory(inventory, out sortedChangedSlots))
                            {
                                await SendAsync(stream, GamePackets.SortItemError(sortListType, 17), remote).ConfigureAwait(false);
                                break;
                            }
                            _store.SaveInventory(active.Id, inventory.Values);
                            await SendAsync(stream, GamePackets.SortItemReply(sortListType), remote).ConfigureAwait(false);
                            await SendItemListPacketsAsync(stream, remote, inventory, active.Gold).ConfigureAwait(false);
                            Log("INFO", "命令 20：整理背包完成 changedSlots={0}", sortedChangedSlots.Length);
                            break;
                        }
                        case 50:
                        {
                            // 副本内丢弃物品（ref server DropItem）。
                            ushort dropPosX, dropPosY, dropItemSlot;
                            byte dropItemListType;
                            uint dropItemCount;
                            if (active == null || (!tutorialRun.IsActive && !lorienRun.IsActive) ||
                                !GamePackets.TryReadDropItem(request.Body, out dropPosX, out dropPosY, out dropItemListType, out dropItemSlot, out dropItemCount) ||
                                dropItemListType != 0)
                            {
                                await SendAsync(stream, GamePackets.DropItemError(2,
                                    (byte)(request.Body != null && request.Body.Length > 6 ? request.Body[6] : 0)), remote).ConfigureAwait(false);
                                Log("WARN", "命令 50：拒绝无效副本丢弃 body={0}", Hex(request.Body));
                                break;
                            }
                            InventoryItemRecord dropItemSource;
                            if (!inventory.TryGetValue(dropItemSlot, out dropItemSource) || dropItemSource.ItemId == 0)
                            {
                                await SendAsync(stream, GamePackets.DropItemError(2, dropItemListType), remote).ConfigureAwait(false);
                                Log("WARN", "命令 50：丢弃物品不存在 list={0} slot={1} item={2}", dropItemListType, dropItemSlot, (int)(dropItemSource?.ItemId ?? 0));
                                break;
                            }
                            var droppedIsEquipment = IsEquipmentItem(dropItemSource.ItemId);
                            var droppedAmount = droppedIsEquipment ? 1u : dropItemCount;
                            if (droppedAmount == 0 || (!droppedIsEquipment && droppedAmount > dropItemSource.Count) ||
                                (droppedIsEquipment && dropItemCount != 1))
                            {
                                await SendAsync(stream, GamePackets.DropItemError(17, dropItemListType), remote).ConfigureAwait(false);
                                Log("WARN", "命令 50：丢弃数量无效 slot={0} item={1} requested={2} available={3}",
                                    dropItemSlot, dropItemSource.ItemId, dropItemCount, dropItemSource.Count);
                                break;
                            }
                            var dropPlannedInventory = CloneItems(inventory);
                            var dropRemaining = droppedIsEquipment ? 0u : dropPlannedInventory[dropItemSlot].Count - droppedAmount;
                            if (dropRemaining == 0) dropPlannedInventory.Remove(dropItemSlot);
                            else dropPlannedInventory[dropItemSlot].Count = dropRemaining;
                            try
                            {
                                _store.SaveInventory(active.Id, dropPlannedInventory.Values);
                            }
                            catch (Exception dropEx)
                            {
                                await SendAsync(stream, GamePackets.DropItemError(17, dropItemListType), remote).ConfigureAwait(false);
                                Log("ERROR", "命令 50：副本丢弃事务回滚 error={0}", dropEx.Message);
                                break;
                            }
                            var dropRecord = groundDrops.SpawnForMonster(0, dropItemSource.ItemId, droppedAmount,
                                dropItemSource.Durability, dropPosX, dropPosY,
                                dropItemSource.State, dropItemSource.SealState);
                            if (lorienRun.IsActive)
                                roomDropLedger.Save(lorienRun.RoomX, lorienRun.RoomY, groundDrops);
                            inventory.Clear();
                            foreach (var dropPair in dropPlannedInventory) inventory[dropPair.Key] = dropPair.Value;
                            await SendAsync(stream, GamePackets.DropItemReply(dropItemListType, dropItemSlot, dropRemaining), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.GroundItemSpawned(userId, dropRecord), remote).ConfigureAwait(false);
                            InventoryItemRecord dropRemainingItem;
                            if (dropRemaining != 0 && inventory.TryGetValue(dropItemSlot, out dropRemainingItem))
                                await SendAsync(stream, GamePackets.UpdateItem(0, dropItemSlot, dropRemainingItem), remote).ConfigureAwait(false);
                            await SendItemListPacketsAsync(stream, remote, inventory, active.Gold).ConfigureAwait(false);
                            Log("INFO", "命令 50：副本丢弃成功 itemId={0} slot={1} count={2} remaining={3} runtime={4}",
                                dropItemSource.ItemId, dropItemSlot, droppedAmount, dropRemaining, dropRecord.RuntimeId);
                            break;
                        }
                        case GamePackets.PickupGroundItem:
                            ushort pickupRuntimeId;
                            if (!GamePackets.TryReadPickupGroundItem(request.Body, out pickupRuntimeId))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.PickupGroundItem, 2), remote).ConfigureAwait(false);
                                Log("WARN", "Invalid PICKUP_GROUND_ITEM: {0}", Hex(request.Body));
                                break;
                            }
                            GroundDropRecord pendingDrop;
                            if (active == null || (!tutorialRun.IsActive && !lorienRun.IsActive) ||
                                !groundDrops.TryGet(pickupRuntimeId, out pendingDrop))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.PickupGroundItem, 2), remote).ConfigureAwait(false);
                                Log("WARN", "Rejected PICKUP_GROUND_ITEM runtime={0}, inDungeon={1}",
                                    pickupRuntimeId, tutorialRun.IsActive || lorienRun.IsActive);
                                break;
                            }
                            GroundDropRecord claimedDrop;
                            ushort pickupSlot;
                            if (pendingDrop.ItemId == 0)
                            {
                                if (!groundDrops.TryClaim(pickupRuntimeId, drop =>
                                {
                                    active.Gold = (int)Math.Min(int.MaxValue, (long)active.Gold + drop.Count);
                                    return 0;
                                }, out claimedDrop, out pickupSlot))
                                {
                                    await SendAsync(stream, GamePackets.CommandError(GamePackets.PickupGroundItem, 2), remote).ConfigureAwait(false);
                                    break;
                                }
                                _store.SaveGoldAndInventory(active.Id, active.Gold, inventory.Values);
                                roomDropLedger.Remove(claimedDrop.RuntimeId);
                                await SendAsync(stream, GamePackets.CommandResult(GamePackets.PickupGroundItem, true), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.GroundItemPickedUpGold(userId,
                                    pickupRuntimeId, claimedDrop.Count), remote).ConfigureAwait(false);
                                await SendItemListPacketsAsync(stream, remote, inventory, active.Gold).ConfigureAwait(false);
                                Log("INFO", "GROUND_DROP gold picked runtime={0}, amount={1}, gold={2}",
                                    pickupRuntimeId, claimedDrop.Count, active.Gold);
                                break;
                            }

                            if (!groundDrops.TryClaim(pickupRuntimeId, drop =>
                            {
                                var rewardSlot = AddInventoryReward(inventory, drop.ItemId, drop.Count);
                                InventoryItemRecord added;
                                if (rewardSlot != ushort.MaxValue && IsEquipmentItem(drop.ItemId) &&
                                    inventory.TryGetValue(rewardSlot, out added))
                                {
                                    added.Durability = drop.Durability;
                                    added.State = drop.State;
                                    added.SealState = drop.SealState;
                                }
                                return rewardSlot;
                            }, out claimedDrop, out pickupSlot))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.PickupGroundItem, 2), remote).ConfigureAwait(false);
                                Log("WARN", "PICKUP_GROUND_ITEM failed runtime={0}, item={1}; inventory page may be full",
                                    pickupRuntimeId, pendingDrop.ItemId);
                                break;
                            }
                            _store.SaveInventory(active.Id, inventory.Values);
                            roomDropLedger.Remove(claimedDrop.RuntimeId);
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.PickupGroundItem, true), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.GroundItemPickedUp(userId, pickupRuntimeId, pickupSlot), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.UpdateItem(0, pickupSlot,
                                inventory[pickupSlot]), remote).ConfigureAwait(false);
                            // pickerUserId=0 is the latest reference server's visual cleanup form;
                            // it grants nothing and prevents stale actors after a room transition.
                            await SendAsync(stream, GamePackets.GroundItemPickedUp(0,
                                pickupRuntimeId, 0), remote).ConfigureAwait(false);
                            Log("INFO", "GROUND_DROP picked runtime={0}, item={1}, count={2}, slot={3}",
                                pickupRuntimeId, claimedDrop.ItemId, claimedDrop.Count, pickupSlot);
                            break;
                        case 131:
                            // ARAD sends command 131 for its local monster-death
                            // events: sequence:u16, killer:u16, runtimeId:u16, ...
                            // The earlier boss-only gate discarded ordinary-monster
                            // reports, so their deaths never reached the PVF drop
                            // roller.  Accept only a currently registered room
                            // monster, then normalize it into the common death path.
                            if (lorienRun.IsActive && request.Body.Length >= 6)
                            {
                                var aradMonsterRuntimeId = LittleEndian.ReadUInt16(request.Body, 4);
                                ushort aradMonsterIndex;
                                int aradMonsterLevel;
                                bool aradMonsterIsBoss;
                                if (lorienRun.TryGetMonster(aradMonsterRuntimeId, out aradMonsterIndex,
                                    out aradMonsterLevel, out aradMonsterIsBoss))
                                {
                                    var normalizedDeathBody = new byte[22];
                                    normalizedDeathBody[0] = request.Body[0];
                                    normalizedDeathBody[1] = request.Body[1];
                                    LittleEndian.WriteUInt16(normalizedDeathBody, 2, aradMonsterRuntimeId);
                                    request.Body = normalizedDeathBody;
                                    Log("INFO", "ARAD 命令 131：怪物死亡 runtime={0}, monster={1}, boss={2}",
                                        aradMonsterRuntimeId, aradMonsterIndex, aradMonsterIsBoss);
                                    goto case GamePackets.MonsterDeath;
                                }
                            }
                            Log("INFO", "ARAD 命令 131：忽略非当前房间怪物实体事件 body={0}", Hex(request.Body));
                            break;
                        case GamePackets.MonsterDeath:
                            ushort monsterId;
                            if (!GamePackets.TryReadMonsterDeath(request.Body, out monsterId))
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.MonsterDeath, 2), remote).ConfigureAwait(false);
                                Log("WARN", "无效 MONSTER_DEATH: {0}", Hex(request.Body));
                                break;
                            }
                            var death = tutorialRun.IsActive ? tutorialRun.ReportMonsterDeath(monsterId)
                                : lorienRun.IsActive ? lorienRun.ReportMonsterDeath(monsterId)
                                : MonsterDeathResult.NotInDungeon;
                            var aliveCount = tutorialRun.IsActive ? tutorialRun.AliveMonsterCount : lorienRun.AliveMonsterCount;
                            if (death == MonsterDeathResult.NotInDungeon || death == MonsterDeathResult.UnknownOrAlreadyDead)
                            {
                                Log("WARN", "拒绝 MONSTER_DEATH id={0}, state={1}", monsterId, death);
                                break;
                            }
                            var autoDefeatedIds = new ushort[0];
                            if (lorienRun.IsActive)
                                autoDefeatedIds = lorienRun.TakeAutoDefeatedMonsterIds();
                            var defeatedIds = new List<ushort> { monsterId };
                            defeatedIds.AddRange(autoDefeatedIds);

                            // Notification 38 owns both the death animation and the corpse-drop list.
                            // System-cleared leftovers are removed by notification 127 and must not roll loot.
                            var lootDefeatedIds = new List<ushort> { monsterId };
                            var questItemSlots = new HashSet<ushort>();
                            foreach (var defeatedId in lootDefeatedIds)
                            {
                                ushort monsterIndex = 0;
                                var monsterLevel = 1;
                                var isBoss = false;
                                var isChampion = false;
                                var isSuperChampion = false;
                                if (tutorialRun.IsActive) monsterIndex = 1;
                                else if (lorienRun.IsActive)
                                    lorienRun.TryGetMonster(defeatedId, out monsterIndex, out monsterLevel,
                                        out isBoss, out isChampion, out isSuperChampion);

                                var monsterType = isBoss ? 3 : isSuperChampion ? 2 : isChampion ? 1 : 0;
                                var dropDifficulty = lorienRun.IsActive ? lorienRun.Difficulty : (byte)0;
                                int corpseDropCount;
                                int independentDropCount;
                                var drops = RollMonsterGroundDrops(groundDrops, defeatedId,
                                    monsterIndex, monsterLevel, monsterType, dropDifficulty,
                                    questDropRandom, out corpseDropCount, out independentDropCount);

                                if (active != null && lorienRun.IsActive && monsterIndex > 0)
                                {
                                    AwardMonsterQuestDrops(lorienRun.DungeonId, lorienRun.Difficulty,
                                        monsterIndex, activeQuests, inventory, questDropRandom,
                                        questItemSlots);
                                }

                                var championRewardOrdinal = settlement.AllocateChampionRewardOrdinal(
                                    isBoss, isChampion, isSuperChampion);
                                await SendAsync(stream, GamePackets.MonsterDie(defeatedId, drops,
                                    championRewardOrdinal), remote).ConfigureAwait(false);
                                Log("INFO", "GROUND_DROP roll monster={0} (idx={1}, level={2}, type={3}), corpse={4}, independent={5}, total={6}",
                                    defeatedId, monsterIndex, monsterLevel, monsterType, corpseDropCount,
                                    independentDropCount, drops.Count);
                            }
                            if (death == MonsterDeathResult.RoomCleared)
                            {
                                await SendAsync(stream, GamePackets.AllDieMonster(userId), remote).ConfigureAwait(false);
                                Log("INFO", "ALL_DIE_MONSTER 127：Boss 死亡后清除剩余怪物 count={0}", autoDefeatedIds.Length);
                            }
                            if (active != null && questItemSlots.Count != 0)
                            {
                                _store.SaveInventory(active.Id, inventory.Values);
                                foreach (var questSlot in questItemSlots.OrderBy(value => value))
                                {
                                    InventoryItemRecord questItem;
                                    if (inventory.TryGetValue(questSlot, out questItem))
                                        await SendAsync(stream, GamePackets.UpdateItem(0, questSlot,
                                            questItem), remote).ConfigureAwait(false);
                                }
                            }
                            if (active != null)
                            {
                                var currentDungeonId = lorienRun.IsActive ? lorienRun.DungeonId : GamePackets.TutorialDungeonId;
                                foreach (var huntQuest in activeQuests.Where(value => value.Trigger > 0).ToList())
                                {
                                    QuestDefinition huntDefinition;
                                    if (!_questCatalog.TryGet(huntQuest.QuestId, out huntDefinition) ||
                                        huntDefinition.ConditionType != QuestConditionType.HuntMonster) continue;
                                    var matchedKills = 0;
                                    foreach (var defeatedId in defeatedIds)
                                    {
                                        ushort defeatedMonsterIndex;
                                        int defeatedMonsterLevel;
                                        bool defeatedBoss;
                                        if (lorienRun.IsActive &&
                                            lorienRun.TryGetMonster(defeatedId, out defeatedMonsterIndex, out defeatedMonsterLevel, out defeatedBoss) &&
                                            QuestCatalog.MatchesMonsterKill(huntDefinition, currentDungeonId,
                                                lorienRun.Difficulty, defeatedMonsterIndex))
                                            matchedKills++;
                                    }
                                    if (matchedKills == 0) continue;
                                    huntQuest.Trigger = (byte)Math.Max(0, huntQuest.Trigger - matchedKills);
                                    _store.SaveActiveQuest(active.Id, huntQuest);
                                    await SendAsync(stream, GamePackets.SetQuestTriggerReply(huntQuest.QuestId, huntQuest.Trigger), remote).ConfigureAwait(false);
                                    Log("INFO", "杀怪任务进度 quest={0}, remaining={1}", huntQuest.QuestId, huntQuest.Trigger);
                                }
                            }
                            Log("INFO", "MONSTER_DEATH id={0}, remaining={1}", monsterId, aliveCount);
                            if (death == MonsterDeathResult.RoomCleared && settlement.TryEnableClear())
                            {
                                // Persist the clear condition only after the
                                // authoritative boss room is really empty.
                                if (active != null)
                                {
                                    var clearedDungeonId = lorienRun.IsActive ? lorienRun.DungeonId : GamePackets.TutorialDungeonId;
                                    var changedItemSlots = new HashSet<ushort>();
                                    if (lorienRun.IsActive)
                                    {
                                        foreach (var activeQuest in activeQuests.ToList())
                                        {
                                            QuestDefinition dropDefinition;
                                            if (!_questCatalog.TryGet(activeQuest.QuestId, out dropDefinition)) continue;
                                            var drops = QuestDropPlanner.Plan(dropDefinition, clearedDungeonId,
                                                lorienRun.Difficulty, inventory.Values,
                                                chance => questDropRandom.Next(1, 101) <= chance);
                                            foreach (var drop in drops)
                                            {
                                                var itemSlot = AddInventoryReward(inventory, drop.ItemId, drop.Count);
                                                if (itemSlot == ushort.MaxValue)
                                                {
                                                    Log("WARN", "任务道具页已满，无法发放 quest={0}, item={1}", activeQuest.QuestId, drop.ItemId);
                                                    continue;
                                                }
                                                changedItemSlots.Add(itemSlot);
                                                Log("INFO", "任务道具已自动获得 quest={0}, item={1}, count={2}, slot={3}",
                                                    activeQuest.QuestId, drop.ItemId, drop.Count, itemSlot);
                                            }
                                        }
                                        if (changedItemSlots.Count > 0)
                                        {
                                            _store.SaveInventory(active.Id, inventory.Values);
                                            foreach (var itemSlot in changedItemSlots.OrderBy(value => value))
                                                await SendAsync(stream, GamePackets.UpdateItem(0, itemSlot, inventory[itemSlot]), remote).ConfigureAwait(false);
                                        }
                                        foreach (var seekingQuest in activeQuests.Where(value => value.Trigger != 0).ToList())
                                        {
                                            QuestDefinition seekingDefinition;
                                            if (!_questCatalog.TryGet(seekingQuest.QuestId, out seekingDefinition) ||
                                                seekingDefinition.ConditionType != QuestConditionType.Seeking ||
                                                !QuestCatalog.IsSeekingConditionSatisfied(seekingDefinition, inventory.Values, active.Gold)) continue;
                                            seekingQuest.Trigger = 0;
                                            _store.SaveActiveQuest(active.Id, seekingQuest);
                                            await SendAsync(stream, GamePackets.SetQuestTriggerReply(seekingQuest.QuestId, 0), remote).ConfigureAwait(false);
                                            Log("INFO", "任务收集条件已达成 quest={0}", seekingQuest.QuestId);
                                        }
                                    }
                                    foreach (var clearedQuest in activeQuests.Where(value => value.Trigger != 0).ToList())
                                    {
                                        QuestDefinition clearedDefinition;
                                        if (!_questCatalog.TryGet(clearedQuest.QuestId, out clearedDefinition) ||
                                            clearedDefinition.ConditionType == QuestConditionType.HuntMonster ||
                                            !QuestCatalog.MatchesDungeonClear(clearedDefinition, clearedDungeonId)) continue;
                                        clearedQuest.Trigger = 0;
                                        _store.SaveActiveQuest(active.Id, clearedQuest);
                                        await SendAsync(stream, GamePackets.SetQuestTriggerReply(clearedQuest.QuestId, 0), remote).ConfigureAwait(false);
                                        Log("INFO", "任务条件已达成 quest={0}, dungeon={1}", clearedQuest.QuestId, clearedDungeonId);
                                    }
                                }
                                // 怪物全灭后发通知 31（EnableClearDungeon）：
                                // 客户端据此完成教程剧情（救 NPC）并进入
                                // 结算/自动返回城镇流程（韩服 55 协议确认）。
                                // 洛兰普通房间清空只开路径门（客户端发命令 48
                                // 换房），boss 房全灭才触发结算。
                                Log("INFO", "{0} 已清空；发送 EnableClearDungeon", tutorialRun.IsActive ? "教程房间" : "洛兰 boss 房");
                                await SendAsync(stream, GamePackets.EnableClearDungeon(), remote).ConfigureAwait(false);
                            }

                            // 击杀结算：按角色等级、怪物等级与 Boss 类型计算，
                            // Boss 死亡自动清掉的小怪也逐只计入经验。
                            if (active != null)
                            {
                                var grantedExperience = 0;
                                foreach (var defeatedId in defeatedIds)
                                {
                                    ushort defeatedMonsterIndex;
                                    int defeatedMonsterLevel;
                                    bool defeatedBoss;
                                    if (lorienRun.IsActive && lorienRun.TryGetMonster(defeatedId,
                                        out defeatedMonsterIndex, out defeatedMonsterLevel, out defeatedBoss))
                                        grantedExperience += ExperienceTable.MonsterExperience(active.Level, defeatedMonsterLevel, defeatedBoss);
                                    else if (tutorialRun.IsActive)
                                        grantedExperience += ExperienceTable.MonsterExperience(active.Level, 1, false);
                                }
                                var grant = grantedExperience > 0
                                    ? _store.GrantExperience(active.Id, grantedExperience)
                                    : null;
                                if (grant != null)
                                {
                                    active.Level = (byte)grant.Item2;
                                    active.Exp = grant.Item3;
                                    active.SkillPoints = grant.Item5;
                                    // 通知 37 首字段必须是当前等级：客户端
                                    // 用 byte 等级 + u32 经验调用角色经验接口，
                                    // 等级发 0 会把角色等级刷成 0。
                                    // u32_1 是"累计总经验"（SetExp 需要）；
                                    var cumulativeExp = (grant.Item2 > 1 ? ExperienceTable.ExpForNextLevel(grant.Item2 - 1) : 0) + grant.Item3;
                                    // extraA/extraB are party/apprentice EXP breakdowns.
                                    // A solo monster kill is entirely base experience.
                                    await SendAsync(stream, GamePackets.ExperienceGained(active.Level, (uint)cumulativeExp, 0, 0,
                                        (ushort)Math.Min(active.SkillPoints, ushort.MaxValue)), remote).ConfigureAwait(false);
                                    Log("INFO", "击杀结算: +{0} EXP, level={1}, exp={2}/{3}, SP={4}{5}", grantedExperience,
                                        grant.Item2, grant.Item3, grant.Item4, grant.Item5, grant.Item1 ? " (升级!)" : string.Empty);
                                    // 注意：不再重发 SKILL_INFO——客户端会把它当作技能
                                    // 状态重置，导致技能冷却被错误刷新（用户实测反馈）。
                                    // 仅升级时刷新技能面板；普通击杀不重置冷却。
                                    if (grant.Item1)
                                        await SendAsync(stream, GamePackets.DefaultSkills(active.Job, (ushort)Math.Min(active.SkillPoints, ushort.MaxValue), learnedSkills, _skillCatalog), remote).ConfigureAwait(false);
                                }
                            }
                            break;
                        case GamePackets.SetPlayResult:
                            // 命令 49（SetPlayResult）：客户端在副本怪物全灭后
                            // 请求通关结算（韩服 55 协议确认）。该命令是单向请求，
                            // 不能回 Command 49；客户端只期待：
                            //   通知 34（PlayResult）+ 通知 35（ClearDungeonReward）
                            // 客户端据此完成教程剧情（救 NPC）并自动返回城镇。
                            DungeonClearScore clearScore;
                            if ((tutorialRun.IsActive || lorienRun.IsActive) &&
                                DungeonClearScore.TryParse(request.Body, out clearScore) &&
                                settlement.TryBeginResult())
                            {
                                uint remaining = request.Body.Length >= 6 ? LittleEndian.ReadUInt32(request.Body, 2) : 0u;
                                // Arad 命令 49 在 remaining:u32 后带 9 个 i16 结算字段，
                                // 最后一个是客户端本地计算的评级码（0/35/40/45/
                                // 55/70/85/95/105）。通知 34 必须原样回显该值；
                                // 固定写 userId=1 会使客户端放弃初始化结算玩家，
                                // 卡牌就会显示成“退出队伍的角色”并破坏界面布局。
                                var resultCode = GamePackets.IsValidResultCode(clearScore.ResultCode)
                                    ? clearScore.ResultCode : clearScore.CalculatedResultCode;
                                var dungeonLevel = lorienRun.IsActive ? lorienRun.CurrentLevel() : 1;
                                var dungeonDifficulty = lorienRun.IsActive ? lorienRun.Difficulty : (byte)0;
                                var clearExperience = active == null ? 0 :
                                    ExperienceTable.DungeonClearExperience(active.Level, dungeonLevel,
                                        dungeonDifficulty, resultCode);
                                if (active != null && clearExperience > 0)
                                {
                                    var clearGrant = _store.GrantExperience(active.Id, clearExperience);
                                    if (clearGrant != null)
                                    {
                                        active.Level = (byte)clearGrant.Item2;
                                        active.Exp = clearGrant.Item3;
                                        active.SkillPoints = clearGrant.Item5;
                                        Log("INFO", "通关经验: +{0} EXP, level={1}, exp={2}/{3}, SP={4}{5}",
                                            clearExperience, clearGrant.Item2, clearGrant.Item3, clearGrant.Item4,
                                            clearGrant.Item5, clearGrant.Item1 ? " (升级!)" : string.Empty);
                                    }
                                }
                                settlement.PrepareCardRewards(dungeonLevel, dungeonDifficulty,
                                    questDropRandom, _independentDropCatalog);
                                await SendAsync(stream, GamePackets.SoloPartyInfo(userId), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.PlayResult(resultCode), remote).ConfigureAwait(false);
                                await SendAsync(stream, GamePackets.ClearDungeonReward(0,
                                    (uint)Math.Max(0, clearExperience), settlement.FreeCardRewards,
                                    settlement.PaidCardRewards, settlement.PaidCardPrice), remote).ConfigureAwait(false);
                                if (active != null && lorienRun.IsActive)
                                {
                                    var unlockedDifficulty = _store.UnlockNextDungeonDifficulty(active.Id, lorienRun.DungeonId, lorienRun.Difficulty);
                                    // 结算界面正在严格等待命令 72/74/75，此时插入
                                    // DungeonPermissions 会打乱 Arad 客户端的结算状态机。
                                    // 解锁先持久化，完成回城后再统一刷新权限。
                                    Log("INFO", "副本难度已解锁 dungeon={0}, maxDifficulty={1}", lorienRun.DungeonId, unlockedDifficulty);
                                }
                                Log("INFO", "命令 49：已发送通关结算（remaining={0}, resultCode={1}, clearExp={2}, cardPrice={3}）",
                                    remaining, resultCode, clearExperience, settlement.PaidCardPrice);
                            }
                            else
                            {
                                // ARAD 客户端在收到通知 31（进入游戏世界完成/结算许可）
                                // 后会在进图瞬间上报命令 49（初始结算状态查询）。
                                // 此时副本尚未清空，不能走 34/35 结算流；回一个成功
                                // 字节即可让客户端继续显示教程地图/漫画流程。
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.SetPlayResult, true), remote).ConfigureAwait(false);
                            await SendAsync(stream, GamePackets.FinishLoaded(), remote).ConfigureAwait(false);
                            Log("INFO", "ARAD 命令 49 已确认（初始/未清空状态）");
                            }
                            break;
                        case GamePackets.DungeonItemAction:
                            // DNF55 confirms command 47 as the dungeon-only item action.
                            // Use the same authoritative decrement path as command 18 so the
                            // quick bar, inventory snapshot and persisted stack cannot diverge.
                            if (active != null && (tutorialRun.IsActive || lorienRun.IsActive) && request.Body.Length >= 5)
                            {
                                ushort dungeonItemSlot = LittleEndian.ReadUInt16(request.Body, 2);
                                byte dungeonItemListType = request.Body[4];
                                InventoryItemRecord dungeonItem;
                                InventoryItemCategory dungeonItemCategory;
                                if (dungeonItemListType == 0 && dungeonItemSlot >= 3 &&
                                    inventory.TryGetValue(dungeonItemSlot, out dungeonItem) && dungeonItem.Count > 0 &&
                                    _itemCategoryCatalog.TryGetCategory(dungeonItem.ItemId, out dungeonItemCategory) &&
                                    dungeonItemCategory != InventoryItemCategory.Equipment)
                                {
                                    var usedItemId = dungeonItem.ItemId;
                                    InventoryItemRecord consumedDungeonItem;
                                    uint consumedDungeonCount;
                                    uint remainingItemCount;
                                    var dungeonConsumeRequest = new ConsumeItemRequestRecord
                                    {
                                        Operation = 2,
                                        Slot = dungeonItemSlot,
                                        ItemId = usedItemId,
                                        RequestedCount = 1
                                    };
                                    if (!TryConsumeInventoryItem(inventory, dungeonConsumeRequest,
                                        out consumedDungeonItem, out consumedDungeonCount, out remainingItemCount))
                                    {
                                        await SendAsync(stream, GamePackets.CommandError(
                                            GamePackets.DungeonItemAction, 2), remote).ConfigureAwait(false);
                                        break;
                                    }
                                    _store.SaveInventory(active.Id, inventory.Values);
                                    await SendAsync(stream, GamePackets.DungeonItemActionReply(dungeonItemSlot, dungeonItemListType), remote).ConfigureAwait(false);
                                    InventoryItemRecord remainingItem;
                                    inventory.TryGetValue(dungeonItemSlot, out remainingItem);
                                    await SendAsync(stream, GamePackets.UpdateItem(dungeonItemListType, dungeonItemSlot, remainingItem), remote).ConfigureAwait(false);
                                    Log("INFO", "命令 47：副本内使用物品 itemId={0} slot={1}, 消耗 {2}, 剩余 {3}",
                                        usedItemId, dungeonItemSlot, consumedDungeonCount, remainingItemCount);
                                }
                                else
                                {
                                    Log("WARN", "命令 47：拒绝无效副本物品请求 list={0} slot={1} body={2}", dungeonItemListType, dungeonItemSlot, Hex(request.Body));
                                }
                            }
                            else
                            {
                                Log("WARN", "命令 47：当前不在副本中或请求无效 body={0}", Hex(request.Body));
                            }
                            break;
                        case GamePackets.MoveRoom:
                            // 命令 48（换房）：普通房间清空后客户端请求进入
                            // 下一房间（body[2]=新房间X, body[3]=新房间Y）。
                            // 韩服 55 EntranceService 命令 48 分支确认。
                            if (lorienRun.IsActive && request.Body.Length >= 4)
                            {
                                byte requestedClientX = request.Body[2];
                                byte requestedClientY = request.Body[3];
                                byte previousX = lorienRun.RoomX;
                                byte previousY = lorienRun.RoomY;
                                // The launcher installs the selected PVF room into
                                // its matching room-table cell. From that point
                                // command 48 uses the same coordinates, so no
                                // synthetic translation is valid.
                                byte nextX = requestedClientX;
                                byte nextY = requestedClientY;
                                var leavingDrops = groundDrops.Snapshot();
                                var boundPassiveRuntimeIds = new HashSet<ushort>(
                                    roomDropLedger.GetPassiveRuntimeIds(previousX, previousY));
                                if (lorienRun.TryMoveRoom(nextX, nextY))
                                {
                                    pendingGroundRestorePackets = null;
                                    await SendAsync(stream, GamePackets.CommandResult(GamePackets.MoveRoom, true), remote).ConfigureAwait(false);
                                    foreach (var leavingDrop in leavingDrops)
                                    {
                                        // Passive-object rewards were installed through START_MAP;
                                        // they have no standalone ground actor to remove here.
                                        if (boundPassiveRuntimeIds.Contains(leavingDrop.RuntimeId)) continue;
                                        // Item ID 0 is gold and uses notification 39's longer
                                        // gold layout.  Sending the ordinary item form for it
                                        // leaves a stale gold actor after a room transition.
                                        var removeGroundActor = leavingDrop.ItemId == 0
                                            ? GamePackets.GroundItemPickedUpGold(0, leavingDrop.RuntimeId, 0)
                                            : GamePackets.GroundItemPickedUp(0, leavingDrop.RuntimeId, 0);
                                        await SendAsync(stream, removeGroundActor, remote).ConfigureAwait(false);
                                    }
                                    roomDropLedger.Save(previousX, previousY, groundDrops);
                                    List<PassiveObjectDropRecord> passiveObjectDrops;
                                    // The run tracks whether the client has already constructed this
                                    // coordinate. That is the authority for START_MAP state: the drop
                                    // ledger only persists unclaimed items and may legitimately be empty.
                                    // Treating a missing ledger row as a first visit rebuilt an existing
                                    // client scene, which respawned map AI/breakables and left stale doors.
                                    var clientHasCachedRoom = lorienRun.LastRoomWasRevisit;
                                    var restoredDropLedger = roomDropLedger.TryRestore(nextX, nextY,
                                        groundDrops, out passiveObjectDrops);
                                    if (!restoredDropLedger)
                                    {
                                        groundDrops.Clear();
                                        passiveObjectDrops = new List<PassiveObjectDropRecord>();
                                        if (!clientHasCachedRoom)
                                        {
                                            passiveObjectDrops = GeneratePassiveObjectDrops(lorienRun,
                                                groundDrops, questDropRandom);
                                            roomDropLedger.Save(nextX, nextY, groundDrops, passiveObjectDrops);
                                        }
                                        else
                                        {
                                            Log("WARN", "副本回访缺少掉落快照，保留客户端缓存场景：room=({0},{1})",
                                                nextX, nextY);
                                        }
                                    }
                                    // Command 48 alone only opens the local door.
                                    // The ARAD client will not load the next room
                                    // until it receives START_MAP.  Keep its
                                    // logical coordinates in the packet while
                                    // sourcing the map and monster data from the
                                    // PVF-derived room, as the reference
                                    // server does for each successful transition.
                                    // A ledger restore means this exact coordinate
                                    // was already constructed on the client.  Its
                                    // map scene contains the authoritative local
                                    // state of breakables and static AI, so using
                                    // START_MAP state 1 here would recreate the
                                    // PVF template and respawn those actors.
                                    var roomState = clientHasCachedRoom
                                        ? GamePackets.StartMapCachedRoomState
                                        : GamePackets.StartMapFirstVisitRoomState;
                                    await SendAsync(stream, GamePackets.DungeonStartMap(
                                        requestedClientX, requestedClientY, lorienRun.MapId,
                                        lorienRun.SpawnMonsterRows, lorienRun.CurrentLevel(),
                                        roomState, passiveObjectDrops),
                                        remote).ConfigureAwait(false);
                                    if (restoredDropLedger)
                                        pendingGroundRestorePackets = BuildRestoredGroundDropPackets(userId,
                                            groundDrops, passiveObjectDrops);
                                    Log("INFO", "副本换房已确认 client=({0},{1}) -> server=({2},{3})，mapId={4}，怪物 {5} 只，clientCached={6}，dropLedgerRestored={7}，roomState={8}",
                                        requestedClientX, requestedClientY, nextX, nextY, lorienRun.MapId, lorienRun.AliveMonsterCount,
                                        clientHasCachedRoom, restoredDropLedger, roomState);
                                }
                                else
                                {
                                    await SendAsync(stream, GamePackets.CommandError(GamePackets.MoveRoom, 4), remote).ConfigureAwait(false);
                                    Log("WARN", "副本换房拒绝：client=({0},{1})，server target=({2},{3})",
                                        requestedClientX, requestedClientY,
                                        nextX, nextY);
                                }
                            }
                            else
                            {
                                Log("WARN", "命令 48：当前不在副本中，忽略换房请求");
                            }
                            break;
                        case GamePackets.ScoreScrollState:
                            if ((tutorialRun.IsActive || lorienRun.IsActive) && settlement.TryBeginCardLayout())
                            {
                                await SendAsync(stream, GamePackets.ScoreScrollStateReply(), remote).ConfigureAwait(false);
                                Log("INFO", "命令 72：结算奖励动画已启动，等待客户端命令 73");
                            }
                            else
                            {
                                Log("WARN", "命令 72：结算尚未生成、翻牌布局已发送或当前不在副本中，忽略请求");
                            }
                            break;
                        case GamePackets.CardSelectRightStateCommand:
                            if ((tutorialRun.IsActive || lorienRun.IsActive) &&
                                settlement.TryEnableCardSelection())
                            {
                                await SendAsync(stream, GamePackets.CardSelectRightState(), remote).ConfigureAwait(false);
                                Log("INFO", "命令 73：翻牌资格已启用");
                            }
                            else
                            {
                                await SendAsync(stream, GamePackets.CommandError(
                                    GamePackets.CardSelectRightStateCommand, 4), remote).ConfigureAwait(false);
                                Log("WARN", "命令 73：奖励动画尚未开始或翻牌资格已启用");
                            }
                            break;
                        case GamePackets.SelectCard:
                            if (request.Body.Length >= 4 && active != null && (tutorialRun.IsActive || lorienRun.IsActive))
                            {
                                byte cardTier = request.Body[2];
                                byte visualCardIndex = request.Body[3];
                                var paidPrice = settlement.PaidCardPrice;
                                var duplicate = cardTier == 0 ? settlement.FreeCardSelected :
                                    cardTier == 1 ? settlement.PaidCardSelected : true;
                                var rewards = settlement.GetCardRewards(cardTier, visualCardIndex);
                                Dictionary<ushort, InventoryItemRecord> plannedCardInventory;
                                HashSet<ushort> cardRewardSlots;
                                uint cardGoldReward;
                                if (!settlement.CardSelectionEnabled || visualCardIndex >= 4 || duplicate ||
                                    rewards.Length == 0)
                                {
                                    await SendAsync(stream, GamePackets.CommandError(GamePackets.SelectCard, 4), remote).ConfigureAwait(false);
                                    Log("WARN", "命令 74：翻牌阶段、索引、层级或重复请求无效 tier={0}, card={1}",
                                        cardTier, visualCardIndex);
                                }
                                else if (cardTier == 1 && (uint)Math.Max(0, active.Gold) < paidPrice)
                                {
                                    await SendAsync(stream, GamePackets.CommandError(GamePackets.SelectCard, 4), remote).ConfigureAwait(false);
                                    Log("WARN", "命令 74：黄金卡金币不足 gold={0}, price={1}", active.Gold, paidPrice);
                                }
                                else if (!TryPlanCardRewards(inventory, rewards, out plannedCardInventory,
                                    out cardRewardSlots, out cardGoldReward))
                                {
                                    await SendAsync(stream, GamePackets.CommandError(GamePackets.SelectCard, 4), remote).ConfigureAwait(false);
                                    Log("WARN", "命令 74：奖励无效或对应背包页已满 tier={0}, card={1}",
                                        cardTier, visualCardIndex);
                                }
                                else if (settlement.TrySelectCard(cardTier, visualCardIndex))
                                {
                                    inventory.Clear();
                                    foreach (var pair in plannedCardInventory) inventory[pair.Key] = pair.Value;
                                    var goldDelta = (long)cardGoldReward - (cardTier == 1 ? paidPrice : 0u);
                                    active.Gold = (int)Math.Min(int.MaxValue,
                                        Math.Max(0L, (long)active.Gold + goldDelta));
                                    _store.SaveGoldAndInventory(active.Id, active.Gold, inventory.Values);
                                    foreach (var rewardSlot in cardRewardSlots.OrderBy(value => value))
                                        await SendAsync(stream, GamePackets.UpdateItem(0, rewardSlot,
                                            inventory[rewardSlot]), remote).ConfigureAwait(false);
                                    await SendAsync(stream, GamePackets.SelectCardReply(0,
                                        settlement.FreeCardIndex, settlement.PaidCardIndex), remote).ConfigureAwait(false);
                                    Log("INFO", "命令 74：已确认{0}翻牌 card={1}, goldReward={2}, itemSlots={3}, price={4}, gold={5}",
                                        cardTier == 0 ? "免费" : "黄金", visualCardIndex,
                                        cardGoldReward, string.Join(",", cardRewardSlots),
                                        cardTier == 0 ? 0 : paidPrice, active.Gold);
                                }
                                else
                                {
                                    Log("WARN", "命令 74：翻牌顺序、索引或重复请求无效 tier={0}, card={1}", cardTier, visualCardIndex);
                                }
                            }
                            else
                            {
                                Log("WARN", "命令 74：翻牌布局未生成、已翻牌、请求无效或当前不在副本中");
                            }
                            break;
                        case GamePackets.Eplp:
                            if (request.Body.Length >= 4 && (tutorialRun.IsActive || lorienRun.IsActive))
                            {
                                byte eplpState = request.Body[2];
                                byte eplpOption = request.Body[3];
                                // When the card countdown expires without a
                                // manual choice, the client advances through
                                // EPLP without sending command 74.  Resolve the
                                // free reward to card zero before acknowledging
                                // that transition so the player never loses the
                                // mandatory normal-card reward.
                                if (!settlement.FreeCardSelected && active != null &&
                                    settlement.CardSelectionEnabled)
                                {
                                    Dictionary<ushort, InventoryItemRecord> automaticInventory;
                                    HashSet<ushort> automaticSlots;
                                    uint automaticGold;
                                    if (TryPlanCardRewards(inventory, settlement.GetCardRewards(0, 0),
                                            out automaticInventory, out automaticSlots, out automaticGold) &&
                                        settlement.TrySelectCard(0, 0))
                                    {
                                        inventory.Clear();
                                        foreach (var pair in automaticInventory) inventory[pair.Key] = pair.Value;
                                        active.Gold = (int)Math.Min(int.MaxValue,
                                            (long)Math.Max(0, active.Gold) + automaticGold);
                                        _store.SaveGoldAndInventory(active.Id, active.Gold, inventory.Values);
                                        foreach (var automaticRewardSlot in automaticSlots.OrderBy(value => value))
                                            await SendAsync(stream, GamePackets.UpdateItem(0, automaticRewardSlot,
                                                inventory[automaticRewardSlot]), remote).ConfigureAwait(false);
                                        await SendAsync(stream, GamePackets.SelectCardReply(0,
                                            settlement.FreeCardIndex, settlement.PaidCardIndex), remote).ConfigureAwait(false);
                                        Log("INFO", "命令 75：已自动翻开第一张普通卡，goldReward={0}, itemSlots={1}",
                                            automaticGold, string.Join(",", automaticSlots));
                                    }
                                }
                                await SendAsync(stream, GamePackets.EplpReply(eplpState, eplpOption), remote).ConfigureAwait(false);
                                Log("INFO", "命令 75：Eplp state={0} option={1}", eplpState, eplpOption);
                                // state=1：结算流程结束，客户端请求返回城镇。
                                if (eplpState == 1 && active != null && settlement.ResultSent)
                                {
                                    groundDrops.Clear();
                                    roomDropLedger.Clear();
                                    pendingGroundRestorePackets = null;
                                    await ReturnToTownAsync(stream, remote, active, userId, request.IsCitrisCommand, tutorialRun, lorienRun, equipment, avatars, learnedSkills, "结算完成", returnTownId, returnAreaId, returnX, returnY, returnDirection).ConfigureAwait(false);
                                    Interlocked.Exchange(ref tracedDungeonIndex, 0);
                                    await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                Log("WARN", "命令 75：无效 Eplp 请求");
                            }
                            break;
                        case GamePackets.GiveupGame:
                            // 命令 45 实际是"放弃游戏/离开副本"（GiveupGame，
                            // 韩服 55 协议确认；旧注释称其为房间清空有误）。
                            // 客户端点菜单"返回城镇"即发本命令。55 服务端在
                            // EntranceService.ReturnToTownFromDungeonAsync 中
                            // 不回复 Command 45，而是直接推送城镇通知序列：
                            //   USER_STATE(0) -> USER_AREA -> AREA_USERS
                            //   -> 100ms -> USERINFO mode 0/属性 -> STAMINA。
                            // Arad 的 USER_AREA/AREA_USERS 布局带尾部状态字节
                            // （SelfTest 已验证），与 55 不同，沿用 Arad 布局。
                            if (active != null)
                            {
                                // Unlike the DNF55 client, the Arad command 45
                                // handler calls the active dungeon-selection
                                // controller's exit method only on a success
                                // reply. Send it before rebuilding the town.
                                await SendAsync(stream, GamePackets.CommandResult(GamePackets.GiveupGame, true), remote).ConfigureAwait(false);
                                groundDrops.Clear();
                                roomDropLedger.Clear();
                                pendingGroundRestorePackets = null;
                                await ReturnToTownAsync(stream, remote, active, userId, request.IsCitrisCommand, tutorialRun, lorienRun, equipment, avatars, learnedSkills, "菜单返回城镇", returnTownId, returnAreaId, returnX, returnY, returnDirection).ConfigureAwait(false);
                                Interlocked.Exchange(ref tracedDungeonIndex, 0);
                                await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                            }
                            else
                            {
                                await SendAsync(stream, GamePackets.CommandError(GamePackets.GiveupGame, 4), remote).ConfigureAwait(false);
                                Log("WARN", "命令 45：无活动角色，忽略返回城镇请求");
                            }
                            break;
                        case GamePackets.LeaveDungeonSelectionObserved:
                        case GamePackets.LeaveDungeonSelection:
                            // 地图选择页的“返回城镇”使用 Arad 专有命令 142，
                            // 而副本内菜单使用命令 45。该命令没有命令响应
                            // handler，直接推送城镇恢复通知即可关闭选择界面。
                            if (active != null)
                            {
                                groundDrops.Clear();
                                roomDropLedger.Clear();
                                pendingGroundRestorePackets = null;
                                await ReturnToTownAsync(stream, remote, active, userId, request.IsCitrisCommand, tutorialRun, lorienRun, equipment, avatars, learnedSkills, "地图选择返回城镇", returnTownId, returnAreaId, returnX, returnY, returnDirection).ConfigureAwait(false);
                                Interlocked.Exchange(ref tracedDungeonIndex, 0);
                                await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                            }
                            else Log("WARN", "命令 142：无活动角色，忽略返回城镇请求");
                            break;
                        case GamePackets.TownInit82:
                            await SendAsync(stream, GamePackets.TownInit82Empty(), remote).ConfigureAwait(false);
                            if (active != null)
                                await SendAsync(stream, GamePackets.AcceptableQuests(_questCatalog.GetAcceptableQuestIds(active, activeQuests, completedQuestIds)), remote).ConfigureAwait(false);
                            break;
                        case GamePackets.TownInit130:
                            await SendAsync(stream, GamePackets.TownInit130Empty(), remote).ConfigureAwait(false);
                            // 正常进城（不启动教程）时，命令 130 是城镇资源
                            // 初始化的最后一个请求。Arad 通知 30 handler
                            // @0x4200B6 会按当前模式分别结束副本或城镇加载；
                            // 少发它时客户端已完成 82/130，却一直停留在黑色
                            // Loading 控制器中。过去通知 136 强制进教程，间接
                            // 掩盖了这一缺包。
                            await SendAsync(stream, GamePackets.FinishLoaded(), remote).ConfigureAwait(false);
                            Log("INFO", "命令 130：城镇初始化完成，已发送 FinishLoading 通知");
                            break;
                        case GamePackets.ConfirmActiveModule:
                            await SendAsync(stream, GamePackets.CommandResult(GamePackets.ConfirmActiveModule, true), remote).ConfigureAwait(false);
                            break;
                        default:
                            Log("WARN", "未知命令 type={0} id={1} body={2}", request.Type, request.ProtocolId, Hex(request.Body));
                            break;
                    }
                }
                }
            }
            catch (Exception exception)
            {
                Log("ERROR", "连接 {0} 结束: {1}", remote, exception.Message);
            }
            finally
            {
                dungeonTraceCts.Cancel();
                try { await dungeonTraceTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                dungeonTraceCts.Dispose();
                if (active != null)
                {
                    try
                    {
                        // The reference server checkpoints the live character
                        // state before releasing the session. This matters for
                        // client-side configuration changes that arrive just
                        // before the socket closes.
                        _store.SaveSkills(active.Id, active.SkillPoints, learnedSkills);
                        _store.SaveInventoryAndStorage(active.Id, inventory.Values, storage.Values);
                        _store.SaveEquipment(active.Id, equipment.Values);
                        _store.SaveAvatars(active.Id, avatars.Values);
                        foreach (var quest in activeQuests)
                            _store.SaveActiveQuest(active.Id, quest);
                        _store.UpdatePosition(active.Id, active.TownId, active.AreaId, active.X, active.Y, active.Direction);
                    }
                    catch (Exception saveEx)
                    {
                        Log("ERROR", "TCP 客户端断开前保存角色 {0} 失败: {1}", active.Name, saveEx.Message);
                    }
                }
                groundDrops.Clear();
                roomDropLedger.Clear();
                pendingGroundRestorePackets = null;
                Log("INFO", "TCP 客户端断开: {0}", remote);
            }
        }

        private async Task SendRosterAsync(Stream stream, string remote, int accountId)
        {
            var characters = _store.ListCharacters(accountId);
            if (GameServerPacket.UseLegacyChannelHeader)
                await SendAsync(stream, GamePackets.AradCharacterList(characters), remote).ConfigureAwait(false);
            else
                await SendAsync(stream, GamePackets.CharacterList(characters,
                    characterId => _store.LoadAvatars(characterId)), remote).ConfigureAwait(false);
        }

        private static GameServerPacket BuildAradTownUserList(CharacterRecord active)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); // success
                if (active == null)
                {
                    writer.Write((byte)0);
                    return new GameServerPacket(GamePackets.Command, 134, stream.ToArray());
                }
                writer.Write((byte)1); // 记录数
                var nameBytes = Encoding.GetEncoding(936).GetBytes(active.Name ?? string.Empty);
                writer.Write((uint)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(active.Job);
                writer.Write(active.Level);
                writer.Write((byte)0);
                return new GameServerPacket(GamePackets.Command, 134, stream.ToArray());
            }
        }

        // 累计总经验 = 表1[等级]（上一级阈值）+ 级内经验。
        // 与客户端 SetExp 的累计语义一致（客户端经验显示基数）。
        private static uint CumulativeExperience(CharacterRecord character)
        {
            var baseExp = character.Level > 1 ? ExperienceTable.ExpForNextLevel(character.Level - 1) : 0;
            return (uint)(baseExp + character.Exp);
        }

        private static void ApplyTownConsumable(CharacterRecord character, TownConsumableEffect effect, uint count)
        {
            var total = Math.Min(int.MaxValue, (long)effect.Amount * count);
            switch (effect.Type)
            {
                case TownConsumableEffectType.SkillPoints:
                    character.SkillPoints = AddClamped(character.SkillPoints, total);
                    break;
                case TownConsumableEffectType.Experience:
                    var level = (int)character.Level;
                    var experience = character.Exp;
                    bool leveled;
                    var earnedSkillPoints = ExperienceTable.ApplyExperience(ref level, ref experience,
                        (int)Math.Min(int.MaxValue, total), out leveled);
                    character.Level = (byte)Math.Min(byte.MaxValue, level);
                    character.Exp = experience;
                    character.SkillPoints = AddClamped(character.SkillPoints, earnedSkillPoints);
                    break;
                case TownConsumableEffectType.Strength: character.BonusStrength = AddClamped(character.BonusStrength, total); break;
                case TownConsumableEffectType.Intelligence: character.BonusIntelligence = AddClamped(character.BonusIntelligence, total); break;
                case TownConsumableEffectType.Vitality: character.BonusVitality = AddClamped(character.BonusVitality, total); break;
                case TownConsumableEffectType.Spirit: character.BonusSpirit = AddClamped(character.BonusSpirit, total); break;
                case TownConsumableEffectType.MaximumHp: character.BonusMaximumHp = AddClamped(character.BonusMaximumHp, total); break;
                case TownConsumableEffectType.MaximumMp: character.BonusMaximumMp = AddClamped(character.BonusMaximumMp, total); break;
                case TownConsumableEffectType.MovementSpeed: character.BonusMovementSpeed = AddClamped(character.BonusMovementSpeed, total); break;
                case TownConsumableEffectType.AllResistance: character.BonusAllResistance = AddClamped(character.BonusAllResistance, total); break;
            }
        }

        private static int AddClamped(int current, long addition)
        {
            return (int)Math.Max(0L, Math.Min(int.MaxValue, current + addition));
        }

        internal static GameServerPacket[] BuildEnterDungeonSelectionPackets(ushort userId,
            IEnumerable<KeyValuePair<ushort, byte>> dungeonPermissions)
        {
            return new[]
            {
                GamePackets.UserStateChanged(userId, 1),
                GamePackets.UnavailableUdpHost(),
                GamePackets.DungeonPermissions(dungeonPermissions),
                GamePackets.EnterTutorialSelection()
            };
        }

        internal static KeyValuePair<ushort, byte>[] BuildEffectiveDungeonPermissions(
            DungeonCatalog dungeonCatalog,
            IEnumerable<KeyValuePair<ushort, byte>> persistedPermissions)
        {
            var permissions = new SortedDictionary<ushort, byte>();
            foreach (var row in persistedPermissions ?? Enumerable.Empty<KeyValuePair<ushort, byte>>())
                permissions[row.Key] = (byte)Math.Min(3, (int)row.Value);

            // This emulator is used for content testing. Expose every dungeon
            // present in the active PVF and every one of its four difficulties.
            if (dungeonCatalog != null)
                foreach (var row in dungeonCatalog.Definitions)
                    permissions[row.Key] = 3;
            return permissions.ToArray();
        }

        // 通用副本启动：任意 dungeonId 走 DungeonCatalog（PVF dungeon/*.dgn +
        // map/*.map）解析起始房间 mapId 与怪物行，回复 DUNGEON_INFO +
        // START_MAP。55 流程：CreateDungeonInfo -> TryCreateRoom(start) ->
        // CreateStartMap。启动失败时保留命令错误（客户端停在选择界面）。
        private async Task StartDungeonAsync(Stream stream, string remote, CharacterRecord active, ushort userId,
            ushort dungeonId, byte difficulty, byte dungeonOption,
            TutorialDungeonRun tutorialRun, LorienDungeonRun lorienRun,
            GroundDropState groundDrops, DungeonRoomDropLedger roomDropLedger,
            DungeonSettlementState settlement)
        {
            DungeonCatalog.Definition dungeon;
            if (_dungeonCatalog == null || !_dungeonCatalog.TryGetDefinition(dungeonId, out dungeon))
            {
                Log("WARN", "未知地下城 {0}，已忽略选择请求", dungeonId);
                return;
            }
            var unlockedDifficulties = BuildEffectiveDungeonPermissions(_dungeonCatalog,
                _store.LoadDungeonProgress(active.Id)).ToDictionary(row => row.Key, row => row.Value);
            byte maximumDifficulty;
            if (!unlockedDifficulties.TryGetValue(dungeonId, out maximumDifficulty)) maximumDifficulty = 0;
            if (difficulty > maximumDifficulty)
            {
                Log("WARN", "拒绝未解锁的副本难度 dungeon={0}, requested={1}, unlocked={2}", dungeonId, difficulty, maximumDifficulty);
                return;
            }
            // ReferenceServer creates the run first.  DUNGEON_INFO must describe
            // the same selected layout that START_MAP is about to enter; sending
            // a catalog-level/default maze description first lets the client
            // retain the previous/default first-room map.
            if (!lorienRun.Start(dungeonId, difficulty))
            {
                Log("WARN", "副本 {0} 起始房间 ({1},{2}) 无地图，拒绝进入", dungeonId, dungeon.StartX, dungeon.StartY);
                return;
            }

            // Command 16 only carries the chosen dungeon. Resolve its first
            // map from this exact PVF run, then use that one snapshot to force
            // the client's dungeon and map indexes. DebugConfig is irrelevant
            // after the network session starts.
            var selectedDungeonId = lorienRun.DungeonId;
            var selectedMazeIndex = lorienRun.MazeIndex;
            var selectedBossX = lorienRun.BossX;
            var selectedBossY = lorienRun.BossY;
            var selectedRoomX = lorienRun.RoomX;
            var selectedRoomY = lorienRun.RoomY;
            var selectedMapId = lorienRun.MapId;
            if (selectedDungeonId != dungeonId || selectedMapId == 0)
            {
                lorienRun.Stop();
                Log("WARN", "副本选择状态不一致：requestedDungeon={0}, activeDungeon={1}, mapId={2}",
                    dungeonId, selectedDungeonId, selectedMapId);
                return;
            }
            // The ARAD client owns the initial town-to-dungeon transition. A
            // server-side DUNGEON_INFO/START_MAP pair races that local flow and
            // can rebind the client to its built-in fallback room. Keep the
            // authoritative run state on the server, but only acknowledge the
            // selection; the client loads its own PVF start map. The launcher
            // captures this same dungeon from ARAD's local transition before it
            // constructs the scene, so the server write below is synchronization
            // only and never determines the first-room patch timing.
            ArmClientRoomPatch(remote, selectedDungeonId, selectedRoomX, selectedRoomY);
            await SendAsync(stream, GamePackets.CommandResult(GamePackets.SelectDungeon, true), remote).ConfigureAwait(false);
            // ARAD owns the initial town-to-dungeon map load, but it still
            // needs the selected maze and Boss coordinate. Sending only
            // DUNGEON_INFO cannot race/reload the local fallback map and fixes
            // the default (0,0) Boss marker for every PVF dungeon.
            await SendAsync(stream, GamePackets.DungeonIndexInfo(difficulty,
                selectedDungeonId, selectedMazeIndex, selectedBossX, selectedBossY),
                remote).ConfigureAwait(false);
            groundDrops.Clear();
            roomDropLedger.Clear();
            tutorialRun.Stop();
            settlement.Reset();
            Log("INFO", "已确认副本选择（客户端加载首图，DUNGEON_INFO 已同步） dungeon={0}, maze={1}, start=({2},{3}), boss=({4},{5}), mapId={6}, 怪物 {7} 只, difficulty={8}, option={9}",
                selectedDungeonId, selectedMazeIndex, selectedRoomX, selectedRoomY, selectedBossX, selectedBossY,
                selectedMapId, lorienRun.AliveMonsterCount, difficulty, dungeonOption);
        }

        private void ArmClientRoomPatch(string remote, ushort dungeonId, byte roomX, byte roomY)
        {
            var signalPath = Environment.GetEnvironmentVariable("ARAD_ROOM_PATCH_SIGNAL");
            if (string.IsNullOrWhiteSpace(signalPath) || !File.Exists(signalPath))
            {
                Log("WARN", "副本首房内存补丁未置位：状态文件不存在 remote={0}, dungeon={1}, path={2}",
                    remote, dungeonId, signalPath ?? "(unset)");
                return;
            }
            try
            {
                var parts = File.ReadAllText(signalPath).Trim().Split('|');
                if (parts.Length != 2)
                {
                    Log("WARN", "副本首房内存补丁未置位：状态文件格式无效 remote={0}, path={1}", remote, signalPath);
                    return;
                }
                var processId = int.Parse(parts[0]);
                var stateAddress = Convert.ToUInt32(parts[1], 16);
                var process = System.Diagnostics.Process.GetProcessById(processId);
                if (!string.Equals(process.ProcessName, "ARAD", StringComparison.OrdinalIgnoreCase))
                {
                    Log("WARN", "副本首房内存补丁未置位：状态文件进程不是 ARAD.exe remote={0}, pid={1}", remote, processId);
                    return;
                }
                var handle = OpenProcess(ProcessVmOperation | ProcessVmWrite, false, processId);
                if (handle == IntPtr.Zero)
                {
                    Log("WARN", "副本首房内存补丁未置位：OpenProcess 失败 remote={0}, pid={1}, win32={2}",
                        remote, processId, Marshal.GetLastWin32Error());
                    return;
                }
                try
                {
                    IntPtr written;
                    var dungeonIdBytes = BitConverter.GetBytes((uint)dungeonId);
                    // The launcher owns the PVF map table and captures the
                    // selected dungeon from ARAD's local town-to-dungeon path
                    // before its fallback scene is constructed.  Keep this
                    // server write as a best-effort synchronization only; it
                    // must not be required for the first-room route patch.
                    var selected = WriteProcessMemory(handle, new IntPtr((long)stateAddress + 4),
                        dungeonIdBytes, dungeonIdBytes.Length, out written) && written.ToInt64() == dungeonIdBytes.Length;
                    if (selected)
                        Log("TRACE", "副本首房 PVF 内存补丁已同步 remote={0}, pid={1}, dungeon={2}, room=({3},{4}), selected=0x{5:X8}; 路线由客户端本地捕获置位",
                            remote, processId, dungeonId, roomX, roomY, stateAddress + 4);
                    else
                        Log("WARN", "副本首房 PVF 内存补丁同步失败 remote={0}, pid={1}, dungeon={2}, selected={3}, state=0x{4:X8}, win32={5}",
                            remote, processId, dungeonId, selected, stateAddress,
                            Marshal.GetLastWin32Error());
                }
                finally { CloseHandle(handle); }
            }
            catch (Exception exception)
            {
                Log("WARN", "副本房间内存补丁解锁失败: {0}", exception.Message);
            }
        }

        // Read the live room table from ARAD.exe. The server-side dungeon
        // object is deliberately not consulted here: this is diagnostics for
        // the client's actual map/cell state.
        private static bool TryReadClientRoomMemory(out string details)
        {
            details = "未找到 ARAD.exe";
            var processId = 0;
            var patchStateAddress = 0u;
            try
            {
                var signalPath = Environment.GetEnvironmentVariable("ARAD_ROOM_PATCH_SIGNAL");
                if (!string.IsNullOrWhiteSpace(signalPath) && File.Exists(signalPath))
                {
                    var parts = File.ReadAllText(signalPath).Trim().Split('|');
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0], out processId);
                        uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out patchStateAddress);
                    }
                }
                if (processId <= 0)
                {
                    var candidates = System.Diagnostics.Process.GetProcessesByName("ARAD");
                    try
                    {
                        if (candidates.Length == 1) processId = candidates[0].Id;
                    }
                    finally
                    {
                        foreach (var candidate in candidates) candidate.Dispose();
                    }
                }
                if (processId <= 0) return false;

                using (var process = System.Diagnostics.Process.GetProcessById(processId))
                {
                    if (process.HasExited || !string.Equals(process.ProcessName, "ARAD",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        details = "PID 不是 ARAD.exe: " + processId;
                        return false;
                    }
                    var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);
                    if (handle == IntPtr.Zero)
                    {
                        details = "OpenProcess 失败 pid=" + processId +
                            ", win32=" + Marshal.GetLastWin32Error();
                        return false;
                    }
                    try
                    {
                        var pointers = new byte[8];
                        IntPtr bytesRead;
                        if (!ReadProcessMemory(handle, new IntPtr((long)ClientRoomTablePointerGlobal),
                            pointers, pointers.Length, out bytesRead) || bytesRead.ToInt64() != pointers.Length)
                        {
                            details = "读取 room table 指针失败 pid=" + processId;
                            return false;
                        }
                        var tableBase = BitConverter.ToUInt32(pointers, 0);
                        var tableEnd = BitConverter.ToUInt32(pointers, 4);
                        var span = tableEnd - tableBase;
                        if (tableBase == 0 || tableEnd < tableBase ||
                            span % ClientRoomCellSize != 0)
                        {
                            details = "room table 无效 pid=" + processId +
                                ", base=0x" + tableBase.ToString("X8") +
                                ", end=0x" + tableEnd.ToString("X8");
                            return false;
                        }
                        var cellCount = (int)Math.Min(span / ClientRoomCellSize,
                            (uint)ClientRoomWatchCellLimit);
                        if (cellCount == 0)
                        {
                            details = "room table 为空 pid=" + processId;
                            return false;
                        }
                        var cells = new byte[cellCount * ClientRoomCellSize];
                        if (!ReadProcessMemory(handle, new IntPtr((long)tableBase), cells,
                            cells.Length, out bytesRead) || bytesRead.ToInt64() != cells.Length)
                        {
                            details = "读取 room cells 失败 pid=" + processId;
                            return false;
                        }

                        var width = 0u;
                        if (patchStateAddress != 0)
                        {
                            var state = new byte[24];
                            if (ReadProcessMemory(handle, new IntPtr((long)patchStateAddress),
                                state, state.Length, out bytesRead) &&
                                bytesRead.ToInt64() == state.Length)
                                width = BitConverter.ToUInt32(state, 16);
                        }
                        var activeSlot = -1;
                        var activeMap = 0u;
                        var activeState = 0u;
                        var activeCount = 0;
                        var occupiedCount = 0;
                        var onlyOccupiedSlot = -1;
                        var onlyOccupiedMap = 0u;
                        var onlyOccupiedState = 0u;
                        var occupied = new StringBuilder();
                        for (var slot = 0; slot < cellCount; slot++)
                        {
                            var offset = slot * ClientRoomCellSize;
                            var mapId = BitConverter.ToUInt32(cells, offset);
                            var state = BitConverter.ToUInt32(cells,
                                offset + ClientRoomCellStateOffset);
                            if (mapId == 0 && state == 0) continue;
                            occupiedCount++;
                            onlyOccupiedSlot = slot;
                            onlyOccupiedMap = mapId;
                            onlyOccupiedState = state;
                            if (occupied.Length != 0) occupied.Append(',');
                            occupied.Append(slot).Append(':').Append(mapId).Append('/').Append(state);
                            if (state == 1)
                            {
                                activeCount++;
                                activeSlot = slot;
                                activeMap = mapId;
                                activeState = state;
                            }
                        }
                        var room = "unknown";
                        if (activeCount == 1 && width != 0)
                            room = "(" + (activeSlot % width) + "," + (activeSlot / width) + ")";
                        else if (activeCount > 1)
                            room = "ambiguous";
                        else if (occupiedCount == 1 && width != 0)
                        {
                            activeSlot = onlyOccupiedSlot;
                            activeMap = onlyOccupiedMap;
                            activeState = onlyOccupiedState;
                            room = "(" + (activeSlot % width) + "," + (activeSlot / width) + ")";
                        }
                        details = "pid=" + processId +
                            ", table=0x" + tableBase.ToString("X8") +
                            ", cells=" + cellCount + ", width=" + width +
                            ", active-slot=" + (activeSlot >= 0 && (activeCount == 1 || occupiedCount == 1) ? activeSlot.ToString() : "unknown") +
                            ", active-map=" + (activeSlot >= 0 && (activeCount == 1 || occupiedCount == 1) ? activeMap.ToString() : "unknown") +
                            ", active-state=" + (activeSlot >= 0 && (activeCount == 1 || occupiedCount == 1) ? activeState.ToString() : "unknown") +
                            ", room=" + room +
                            ", occupied=" + (occupied.Length == 0 ? "none" : occupied.ToString());
                        return true;
                    }
                    finally { CloseHandle(handle); }
                }
            }
            catch (Exception exception)
            {
                details = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer,
            int size, out IntPtr bytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer,
            int size, out IntPtr bytesRead);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static GameServerPacket TownCharacterPacket(CharacterRecord character, ushort userId,
            Dictionary<ushort, InventoryItemRecord> equipment, Dictionary<ushort, InventoryItemRecord> avatars)
        {
            return GameServerPacket.UseLegacyChannelHeader
                ? GamePackets.AradTownCharacter(character, userId, equipment.Values, avatars.Values)
                : GamePackets.TownCharacter(character, userId, equipment.Values, avatars.Values);
        }

        private GameServerPacket CharacterDetailsPacket(CharacterRecord character, ushort userId,
            Dictionary<ushort, InventoryItemRecord> equipment, Dictionary<ushort, InventoryItemRecord> avatars,
            List<CharacterSkillRecord> learnedSkills)
        {
            // Arad 客户端的 USERINFO 也必须是完整属性块（含移动速度/跳跃力）。
            // 之前 legacy 路径下发 AradCharacterDetails 极简包（只有名字），
            // 导致客户端属性全为默认值，人物移动极慢、跳跃力异常。
            // 属性块布局已按 Arad 客户端 CreateCurrentCharacterDetails 反推，
            // 与 CharacterDetails 一致；包外 7 字节头仍由 UseLegacyChannelHeader 控制。
            return GamePackets.CharacterDetails(userId, CumulativeExperience(character), character.Job,
                equipment.Values, learnedSkills, _characterStatCatalog.Get(character), _skillCatalog,
                avatars.Values);
        }

        private async Task<CharacterRecord> EnterTownAfterCreateAsync(Stream stream, string remote, CharacterRecord character, ushort userId,
            Dictionary<ushort, InventoryItemRecord> inventory, Dictionary<ushort, InventoryItemRecord> storage,
            Dictionary<ushort, InventoryItemRecord> equipment, Dictionary<ushort, InventoryItemRecord> avatars,
            List<CharacterSkillRecord> learnedSkills, List<CharacterQuestRecord> activeQuests,
            HashSet<ushort> completedQuestIds)
        {
            LoadAndNormalizeSkills(character, learnedSkills);
            LoadInventory(character.Id, inventory);
            LoadStorage(character.Id, storage);
            LoadEquipment(character.Id, character.Job, equipment);
            LoadAvatars(character.Id, character.Job, avatars);
            LoadQuests(character.Id, activeQuests, completedQuestIds);
            if (TownSpawnCatalog.ApplySeriaSpawn(character))
            {
                _store.UpdatePosition(character.Id, character.TownId, character.AreaId, character.X, character.Y, character.Direction);
                Log("INFO", "角色 {0} 已迁移到当前城镇 Gate: town={1}, area={2}, ({3},{4})", character.Name, character.TownId, character.AreaId, character.X, character.Y);
            }
            CompleteTutorialForDirectTown(character);
            await SendAsync(stream, GameServerPacket.UseLegacyChannelHeader
                ? GamePackets.AradSelectOk(character, userId, activeQuests)
                : GamePackets.SelectOk(character, userId, activeQuests), remote).ConfigureAwait(false);
            await SendCharacterInitializationAsync(stream, remote, character, userId,
                inventory, storage, equipment, avatars, learnedSkills, activeQuests, completedQuestIds).ConfigureAwait(false);
            Log("INFO", "创建后自动进入城镇: {0} (slot={1}, job={2})", character.Name, character.Slot, character.Job);
            return character;
        }

        private async Task SendCharacterInitializationAsync(Stream stream, string remote, CharacterRecord character, ushort userId,
            Dictionary<ushort, InventoryItemRecord> inventory, Dictionary<ushort, InventoryItemRecord> storage,
            Dictionary<ushort, InventoryItemRecord> equipment,
            Dictionary<ushort, InventoryItemRecord> avatars,
            List<CharacterSkillRecord> learnedSkills, List<CharacterQuestRecord> activeQuests,
            HashSet<ushort> completedQuestIds)
        {
            // The client does not request these packets after a successful
            // SELECT_CHARACTER.  The China-server flow pushes them immediately.
            // 初始城镇位置来自服务器保存的 area 1 / (474,336)，即 Gate.map
            // 中的赛丽亚房间出生区域。
            // SELECT_CHARACTER's final module bit controls whether the tutorial loading
            // controller is entered. USER_AREA and AREA_USERS remain delayed until the
            // client's first SET_USER_POSITION, then they are sent once as the complete
            // town activation sequence so the local entity is not duplicated.
            await SendAsync(stream, TownCharacterPacket(character, userId, equipment, avatars), remote).ConfigureAwait(false);
            await SendAsync(stream, CharacterDetailsPacket(character, userId, equipment, avatars, learnedSkills), remote).ConfigureAwait(false);
            foreach (var packet in GamePackets.TownInitialization(character.Job, (ushort)Math.Min(character.SkillPoints, ushort.MaxValue), learnedSkills,
                new ushort[0], BuildEffectiveDungeonPermissions(_dungeonCatalog,
                    _store.LoadDungeonProgress(character.Id)), _skillCatalog))
            {
                if (packet.ProtocolId == GamePackets.ItemList)
                {
                    foreach (var itemPacket in BuildItemListPacket(inventory, character.Gold))
                        await SendAsync(stream, itemPacket, remote).ConfigureAwait(false);
                }
                else
                {
                    await SendAsync(stream, packet, remote).ConfigureAwait(false);
                }
            }
            await SendAsync(stream, GamePackets.NormalFatigueHudState(), remote).ConfigureAwait(false);
            await SendAsync(stream, GamePackets.InfiniteFatigueUpdate(), remote).ConfigureAwait(false);
            await SendAsync(stream, GamePackets.StorageItemList(storage.Values), remote).ConfigureAwait(false);
            await SendAsync(stream, BuildAvatarListPacket(avatars), remote).ConfigureAwait(false);
            await SendAsync(stream, GamePackets.CeraBalance((uint)Math.Max(0, character.Cash)), remote).ConfigureAwait(false);
        }

        private async Task SendCitrisCharacterInitializationAsync(Stream stream, string remote, CharacterRecord character, ushort userId,
            Dictionary<ushort, InventoryItemRecord> inventory, Dictionary<ushort, InventoryItemRecord> storage,
            Dictionary<ushort, InventoryItemRecord> equipment, Dictionary<ushort, InventoryItemRecord> avatars,
            List<CharacterSkillRecord> learnedSkills, List<CharacterQuestRecord> activeQuests,
            HashSet<ushort> completedQuestIds)
        {
            var payload = BuildCitrisMode0Payload(character, userId, equipment);
            await SendAsync(stream, new GameServerPacket(GamePackets.Notification, GamePackets.UserInfo, payload), remote).ConfigureAwait(false);
        }

        private byte[] BuildCitrisMode0Payload(CharacterRecord character, ushort userId,
            Dictionary<ushort, InventoryItemRecord> equipment)
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            {
                writer.Write((byte)0);
                writer.Write((ushort)1);
                // Arad indexes the selected entity by its character-list slot.
                // The old server's session user id leaves this lookup null.
                writer.Write(userId);
                var name = Encoding.GetEncoding(936).GetBytes(character.Name ?? string.Empty);
                writer.Write((uint)name.Length);
                writer.Write(name);
                writer.Write(character.Job);
                writer.Write(character.PrimaryGrowType);
                writer.Write(character.Level);
                var equippedItems = (equipment ?? new Dictionary<ushort, InventoryItemRecord>()).Values
                    .Where(value => value != null && value.ItemId != 0 && value.Slot < 0x17)
                    .OrderBy(value => value.Slot)
                    .Take(0x17)
                    .ToArray();
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)equippedItems.Length);
                foreach (var equipped in equippedItems)
                {
                    var durability = equipped.Durability != 0
                        ? equipped.Durability
                        : _equipmentCatalog.GetMaximumDurability(equipped.ItemId);
                    if (durability == 0) durability = 35;
                    writer.Write(equipped.Slot == 9 ? (byte)0 : (byte)equipped.Slot);
                    writer.Write((uint)equipped.ItemId);
                    writer.Write(equipped.State);
                    writer.Write((uint)durability);
                    writer.Write((uint)Math.Max(0, (int)equipped.SealState));
                }
                const int CitrisMode0TailSize = 512;
                var tailUsed = 4 + equippedItems.Length * 14;
                writer.Write(new byte[Math.Max(0, CitrisMode0TailSize - tailUsed)]);
                return buffer.ToArray();
            }
        }

        // 返回城镇统一流程（命令 45 菜单回城 / 命令 75 结算完成共用）。
        // 55 ReturnToTownFromDungeonAsync 流程：清空副本状态、恢复进副本前
        // 位置（副本门口）、依次推 USER_STATE(0) -> USER_AREA -> AREA_USERS
        // -> 100ms -> USERINFO(角色实体+属性)。不再发送 STAMINA：体力已在
        // 登录初始化时置 100，重复发 STAMINA(100) 会触发恢复音效（用户要求关闭）。
        private async Task ReturnToTownAsync(Stream stream, string remote, CharacterRecord active, ushort userId, bool citris,
            TutorialDungeonRun tutorialRun, LorienDungeonRun lorienRun,
            Dictionary<ushort, InventoryItemRecord> equipment, Dictionary<ushort, InventoryItemRecord> avatars,
            List<CharacterSkillRecord> learnedSkills, string reason,
            byte returnTownId, byte returnAreaId, short returnX, short returnY, byte returnDirection)
        {
            tutorialRun.Stop();
            lorienRun.Stop();
            // Restore the position from before entering the dungeon, except
            // when it is the gate trigger itself.  That marker would cause
            // the client to reopen dungeon selection immediately after town
            // reconstruction.
            var returnSpawn = TownSpawnCatalog.GetDungeonReturnSpawn(returnTownId, returnAreaId,
                returnX, returnY, returnDirection);
            active.TownId = returnSpawn.TownId; active.AreaId = returnSpawn.AreaId;
            active.X = returnSpawn.X; active.Y = returnSpawn.Y; active.Direction = returnSpawn.Direction;
            _store.UpdatePosition(active.Id, active.TownId, active.AreaId, active.X, active.Y, active.Direction);
            await SendAsync(stream, GamePackets.UserStateChanged(userId, 0), remote).ConfigureAwait(false);
            await SendAsync(stream, GamePackets.Area(active, userId), remote).ConfigureAwait(false);
            await SendAsync(stream, GamePackets.AreaRoster(active, userId), remote).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            await SendAsync(stream, TownCharacterPacket(active, userId, equipment, avatars), remote).ConfigureAwait(false);
            // Clear rewards also grant experience.  Refresh the town-side SP
            // display after settlement so newly crossed 1/30 EXP thresholds
            // are visible without requiring the player to relog.
            await SendAsync(stream, GamePackets.DefaultSkills(active.Job,
                (ushort)Math.Min(active.SkillPoints, ushort.MaxValue), learnedSkills, _skillCatalog), remote).ConfigureAwait(false);
            // 结算阶段只允许结算/翻牌协议。回城后再下发已持久化的
            // 完整副本权限，保证下次打开地图时能看到新解锁难度。
            await SendAsync(stream, GamePackets.DungeonPermissions(
                BuildEffectiveDungeonPermissions(_dungeonCatalog,
                    _store.LoadDungeonProgress(active.Id))), remote).ConfigureAwait(false);
            foreach (var equipped in equipment.Values.OrderBy(value => value.Slot))
                await SendAsync(stream, citris
                    ? GamePackets.CitrisUpdateItem(3, equipped.Slot, equipped)
                    : GamePackets.UpdateItem(3, equipped.Slot, equipped), remote).ConfigureAwait(false);
            // 必须发 STAMINA(100) 恢复体力：不发会导致客户端体力 0、
            // 返回城镇即进入虚弱状态 0%（协议限制：体力=100 才不虚弱，
            // 但 100 会触发一次 STAMINA_RECOVER 恢复音效）。
            await SendAsync(stream, GamePackets.StaminaUpdate(100), remote).ConfigureAwait(false);
            Log("INFO", "已返回城镇（{0}）副本门口 {1},{2}，level={3} exp={4}", reason, active.X, active.Y, active.Level, active.Exp);
        }

        private async Task SendAsync(Stream stream, GameServerPacket packet, string remote)
        {
            await packet.WriteAsync(stream).ConfigureAwait(false);
            Log("TX", "{0} type={1} id={2} payload={3}", remote, packet.Type, packet.ProtocolId, Hex(packet.Payload));
        }

        private void CompleteTutorialForDirectTown(CharacterRecord character)
        {
            if (character == null || GameServerPacket.TutorialEnabled || character.TutorialStarted)
                return;

            _store.CompleteTutorial(character.Id);
            character.TutorialStarted = true;
            Log("INFO", "教程已关闭：角色 {0} 已持久化为教程完成状态", character.Name);
        }

        private async Task SendAsync(Stream stream, EntrancePacket packet, string remote)
        {
            await packet.WriteAsync(stream).ConfigureAwait(false);
            Log("TX-ENTRANCE", "{0} id={1} payload={2}", remote, packet.ProtocolId, Hex(packet.Payload));
        }

        private static string ReadLoginName(byte[] body)
        {
            byte[] bytes; int next;
            if (TryReadLengthBytes(body, 2, out bytes, out next))
                return Encoding.GetEncoding(936).GetString(bytes);
            // Arad 60 prefixes the login payload with two zero bytes before
            // the account DSTR (sequence:u16, reserved:u16, DSTR account).
            return TryReadLengthBytes(body, 4, out bytes, out next) ? Encoding.GetEncoding(936).GetString(bytes) : null;
        }

        internal static bool TryReadCeraShopPurchase(byte[] body, out byte shopCategory,
            out uint[] commodityNumbers, out byte[] purchaseOptions)
        {
            shopCategory = 0;
            commodityNumbers = new uint[0];
            purchaseOptions = new byte[0];
            if (body == null || body.Length < 8) return false;
            shopCategory = body[2];
            var count = body[3];
            if (count == 0 || count > 32) return false;
            var compactRows = body.Length < 4 + count * 7;
            var rowSize = compactRows ? 5 : 7;
            if (body.Length < 4 + count * rowSize) return false;
            commodityNumbers = new uint[count];
            purchaseOptions = new byte[count];
            for (var index = 0; index < count; index++)
            {
                var offset = 4 + index * rowSize;
                purchaseOptions[index] = body[offset];
                commodityNumbers[index] = LittleEndian.ReadUInt32(body, offset + (compactRows ? 1 : 3));
                if (commodityNumbers[index] == 0) return false;
            }
            return true;
        }

        internal static byte ResolveAvatarPurchaseOption(byte requestedOption,
            byte defaultOption)
        {
            // 0xFF is the protocol's no-explicit-choice marker. Any other
            // value is opaque to the server and is persisted verbatim.
            return requestedOption == byte.MaxValue ? defaultOption : requestedOption;
        }

        private static Dictionary<ushort, InventoryItemRecord> CloneItems(Dictionary<ushort, InventoryItemRecord> source)
        {
            return source.ToDictionary(pair => pair.Key, pair => new InventoryItemRecord
            {
                Slot = pair.Value.Slot, ItemId = pair.Value.ItemId, Count = pair.Value.Count,
                State = pair.Value.State, Durability = pair.Value.Durability, SealState = pair.Value.SealState
            });
        }

        private static string NormalizeLoginName(string reportedName, string configuredName)
        {
            var value = string.IsNullOrWhiteSpace(reportedName) ? configuredName : reportedName.Trim();
            // This literal is a channel-creation handshake marker used by the
            // Arad executable, not a player account. Older builds persisted it
            // as a second account and split characters across two rosters.
            return string.Equals(value, "create_channel_2008", StringComparison.OrdinalIgnoreCase)
                ? configuredName : value;
        }

        private static bool TryReadLengthBytes(byte[] source, int offset, out byte[] result, out int next)
        {
            result = null; next = offset;
            if (source.Length - offset < 4) return false;
            var length = LittleEndian.ReadUInt32(source, offset); offset += 4;
            if (length > int.MaxValue || source.Length - offset < (int)length) return false;
            result = new byte[(int)length]; Buffer.BlockCopy(source, offset, result, 0, result.Length); next = offset + result.Length; return true;
        }

        private void LogPacket(string direction, string remote, PacketFrame frame, byte[] body)
        {
            Log(direction, "{0} type={1} id={2} crc={3:X8} valid={4} body={5}", remote, frame.Type, frame.ProtocolId, frame.DeclaredCrc32, frame.HasValidCrc32, Hex(body));
        }

        private void Log(string level, string format, params object[] args)
        {
            var line = string.Format("{0:O} [{1}] {2}", DateTimeOffset.Now, level, string.Format(format, args));
            Console.WriteLine(line);
            lock (_options.LogDirectory) File.AppendAllText(Path.Combine(_options.LogDirectory, "server.log"), line + Environment.NewLine, Encoding.UTF8);
        }

        private static string Hex(byte[] value)
        {
            if (value == null) return string.Empty;
            var length = Math.Min(value.Length, 128); var builder = new StringBuilder(length * 2);
            for (var index = 0; index < length; index++) builder.Append(value[index].ToString("X2"));
            if (value.Length > length) builder.Append("..."); return builder.ToString();
        }

        private void LoadInventory(int characterId, Dictionary<ushort, InventoryItemRecord> inventory)
        {
            inventory.Clear();
            var changed = false;
            foreach (var item in _store.LoadInventory(characterId))
            {
                changed |= NormalizeItemDurability(item);
                inventory[item.Slot] = item;
            }
            // 按 ref server 的页签槽位规则把错位物品归位，
            // 否则客户端按数字槽位分页时看不到物品。
            changed |= _itemCategoryCatalog.RetabInventory(inventory) > 0;
            if (changed) _store.SaveInventory(characterId, inventory.Values);
        }

        private void LoadStorage(int characterId, Dictionary<ushort, InventoryItemRecord> storage)
        {
            storage.Clear();
            var changed = false;
            foreach (var item in _store.LoadStorage(characterId))
            {
                changed |= NormalizeItemDurability(item);
                storage[item.Slot] = item;
            }
            if (changed) _store.SaveStorage(characterId, storage.Values);
        }

        private void LoadEquipment(int characterId, byte job, Dictionary<ushort, InventoryItemRecord> equipment)
        {
            equipment.Clear();
            var changed = false;
            foreach (var item in _store.LoadEquipment(characterId, job))
            {
                changed |= NormalizeItemDurability(item);
                equipment[item.Slot] = item;
            }
            if (changed) _store.SaveEquipment(characterId, equipment.Values);
        }

        private void LoadAvatars(int characterId, byte job, Dictionary<ushort, InventoryItemRecord> avatars)
        {
            if (NormalizeAvatarSlots(_equipmentCatalog, _store.LoadAvatars(characterId), avatars))
                _store.SaveAvatars(characterId, avatars.Values);
            // An empty avatar inventory is valid.  The old implementation
            // inserted the first PVF entry for every body part; those are
            // base/default resources rather than a coherent cash-shop set and
            // can make a partially equipped actor render transparent.
        }

        internal static bool NormalizeAvatarSlots(EquipmentCatalog equipmentCatalog,
            IEnumerable<InventoryItemRecord> storedItems,
            Dictionary<ushort, InventoryItemRecord> avatars)
        {
            if (avatars == null) throw new ArgumentNullException("avatars");
            avatars.Clear();
            var changed = false;
            var worn = new Dictionary<ushort, InventoryItemRecord>();
            var bagItems = new List<InventoryItemRecord>();
            var displacedWearItems = new List<InventoryItemRecord>();
            foreach (var item in storedItems ?? Enumerable.Empty<InventoryItemRecord>())
            {
                if (item == null || item.ItemId == 0) continue;
                if (item.Count != 0)
                {
                    item.Count = 0;
                    changed = true;
                }
                if (IsAvatarWearDbSlot(item.Slot))
                {
                    var expectedSlot = equipmentCatalog == null
                        ? byte.MaxValue : equipmentCatalog.GetAvatarSlot(item.ItemId);
                    if (expectedSlot != byte.MaxValue && expectedSlot == item.Slot &&
                        !worn.ContainsKey(item.Slot))
                    {
                        worn[item.Slot] = item;
                    }
                    else
                    {
                        changed = true;
                        displacedWearItems.Add(item);
                    }
                }
                else
                {
                    bagItems.Add(item);
                }
            }
            foreach (var pair in worn) avatars[pair.Key] = pair.Value;
            foreach (var item in bagItems.OrderBy(value => value.Slot))
            {
                var slot = item.Slot;
                if (!IsAvatarBagDbSlot(slot) || avatars.ContainsKey(slot))
                {
                    slot = FindFreeAvatarSlot(avatars);
                    changed = true;
                }
                if (slot == ushort.MaxValue) continue;
                if (item.Slot != slot)
                {
                    item.Slot = slot;
                    changed = true;
                }
                avatars[slot] = item;
            }
            foreach (var item in displacedWearItems.OrderBy(value => value.Slot))
            {
                var slot = FindFreeAvatarSlot(avatars);
                if (slot == ushort.MaxValue) continue;
                item.Slot = slot;
                avatars[slot] = item;
            }
            return changed;
        }

        private bool NormalizeItemDurability(InventoryItemRecord item)
        {
            if (item == null || !IsEquipmentItem(item.ItemId)) return false;
            var maximum = _equipmentCatalog.GetMaximumDurability(item.ItemId);
            if (maximum == 0 || item.Durability <= maximum) return false;
            Log("INFO", "校正装备耐久 itemId={0}: {1} -> {2}", item.ItemId, item.Durability, maximum);
            item.Durability = maximum;
            return true;
        }

        private void LoadQuests(int characterId, List<CharacterQuestRecord> activeQuests, HashSet<ushort> completedQuestIds)
        {
            activeQuests.Clear();
            activeQuests.AddRange(_store.LoadActiveQuests(characterId));
            // Repair progress written by the old command-35 implementation.
            // It trusted the client's cached target value (16 for quest 103),
            // although the PVF target is 12, producing -4/12 on screen.
            foreach (var quest in activeQuests)
            {
                QuestDefinition definition;
                if (!_questCatalog.TryGet(quest.QuestId, out definition) ||
                    definition.ConditionType != QuestConditionType.HuntMonster) continue;
                var required = QuestCatalog.GetHuntRequiredCount(definition);
                if (required > 0 && quest.Trigger > required)
                {
                    quest.Trigger = (byte)required;
                    _store.SaveActiveQuest(characterId, quest);
                    Log("INFO", "修复杀怪任务进度 quest={0}, remaining={1}", quest.QuestId, quest.Trigger);
                }
            }
            completedQuestIds.Clear();
            foreach (var questId in _store.LoadCompletedQuests(characterId)) completedQuestIds.Add(questId);
        }

        private ushort AddInventoryReward(Dictionary<ushort, InventoryItemRecord> inventory,
            ushort itemId, uint count)
        {
            if (count == 0) return ushort.MaxValue;
            var isEquipment = IsEquipmentItem(itemId);
            if (!isEquipment)
            {
                InventoryItemCategory category;
                _itemCategoryCatalog.TryGetCategory(itemId, out category);
                var existing = inventory.Values.FirstOrDefault(value =>
                    value.ItemId == itemId && value.Durability == 0 &&
                    ItemCategoryCatalog.IsSlotInTab(value.Slot, category));
                if (existing != null)
                {
                    existing.Count += count;
                    return existing.Slot;
                }
            }
            var slot = FindFreeInventorySlot(inventory, itemId);
            if (slot == ushort.MaxValue) return slot;
            inventory[slot] = new InventoryItemRecord
            {
                Slot = slot, ItemId = itemId, Count = count,
                Durability = isEquipment ? _equipmentCatalog.GetMaximumDurability(itemId) : (ushort)0
            };
            return slot;
        }

        private bool TryPlanCardRewards(Dictionary<ushort, InventoryItemRecord> inventory,
            IEnumerable<DungeonCardReward> rewards,
            out Dictionary<ushort, InventoryItemRecord> plannedInventory,
            out HashSet<ushort> changedSlots, out uint goldReward)
        {
            plannedInventory = CloneItems(inventory);
            changedSlots = new HashSet<ushort>();
            ulong gold = 0;
            foreach (var reward in rewards ?? Enumerable.Empty<DungeonCardReward>())
            {
                if (reward == null || reward.Count == 0) continue;
                if (reward.ItemId == 0)
                {
                    gold = Math.Min(uint.MaxValue, gold + reward.Count);
                    continue;
                }
                var slot = AddInventoryReward(plannedInventory, reward.ItemId, reward.Count);
                if (slot == ushort.MaxValue)
                {
                    goldReward = 0;
                    return false;
                }
                changedSlots.Add(slot);
            }
            goldReward = (uint)gold;
            return changedSlots.Count != 0 || goldReward != 0;
        }

        // 任务交付：扣减 Seeking（收集）型任务要求的物品。
        // QuestDefinition.ConditionRows 的 Seeking 行格式：[itemId, count, ...]。
        // 从 inventory 中按槽位扣减；不足则跳过（客户端已校验 trigger=0 才允许交付）。
        private static int ConsumeQuestItems(QuestDefinition definition,
            Dictionary<ushort, InventoryItemRecord> inventory)
        {
            if (definition == null || definition.ConditionType != QuestConditionType.Seeking || inventory == null) return 0;
            var goldCost = QuestCatalog.GetSeekingGoldRequirement(definition);
            foreach (var row in definition.ConditionRows)
            {
                if (row.Length < 2 || row[0] <= 0) continue;
                var itemId = (ushort)row[0];
                var required = (uint)Math.Max(0, row[1]);
                var remaining = required;
                foreach (var stack in inventory.Values
                    .Where(value => value.ItemId == itemId)
                    .OrderBy(value => value.Count).ToList())
                {
                    if (remaining == 0) break;
                    if (stack.Count <= remaining) { remaining -= stack.Count; inventory.Remove(stack.Slot); }
                    else { stack.Count -= remaining; remaining = 0; }
                }
            }
            return goldCost;
        }

        // 任务交付奖励：解析客户端请求中的可选奖励选择索引，发放固定+可选奖励。
        // 请求布局（55 协议）：[2-3]=questId(u16), [4-5]=selectionIndex(u16，无则 0xFFFF)。
        // RewardRows 行格式：[itemId, count, ...]；RewardSelectionRows 同格式。
        // 返回金币奖励额（reward type 为 gold 时第一行 [0] 为金币数）。
        private int ApplyQuestRewards(QuestDefinition definition, byte[] requestBody,
            CharacterRecord character, Dictionary<ushort, InventoryItemRecord> inventory)
        {
            if (definition == null || inventory == null) return 0;
            var goldReward = 0;
            // 固定物品奖励
            foreach (var row in definition.RewardRows)
            {
                if (row.Length < 2 || row[1] <= 0) continue;
                // In Arad QST reward rows, itemId 0 represents gold and
                // the second column is the amount, even when reward type is
                // still declared as [item].
                if (row[0] == 0)
                {
                    goldReward = (int)Math.Min(int.MaxValue, (long)goldReward + row[1]);
                    continue;
                }
                if (row[0] < 0 || row[0] > ushort.MaxValue) continue;
                AddInventoryReward(inventory, (ushort)row[0], (uint)row[1]);
            }
            // 可选奖励：客户端在 [4-5] 报告选中索引，取该行。
            var selectionIndex = (requestBody != null && requestBody.Length >= 6)
                ? LittleEndian.ReadUInt16(requestBody, 4) : ushort.MaxValue;
            if (selectionIndex != ushort.MaxValue && selectionIndex < definition.RewardSelectionRows.Length)
            {
                var row = definition.RewardSelectionRows[selectionIndex];
                if (row.Length >= 2 && row[0] > 0 && row[1] > 0)
                    AddInventoryReward(inventory, (ushort)row[0], (uint)row[1]);
            }
            return goldReward;
        }

        private void LoadAndNormalizeSkills(CharacterRecord character, List<CharacterSkillRecord> skills)
        {
            var persisted = _store.LoadSkills(character.Id);
            var normalized = GamePackets.CurrentSkills(character.Job, persisted, _skillCatalog);
            var changed = persisted.Count != normalized.Count || persisted
                .OrderBy(value => value.Slot)
                .Zip(normalized.OrderBy(value => value.Slot), (left, right) =>
                    left.Slot == right.Slot && left.SkillId == right.SkillId && left.Level == right.Level)
                .Any(equal => !equal);
            skills.Clear();
            skills.AddRange(normalized);
            if (changed)
            {
                _store.SaveSkills(character.Id, character.SkillPoints, normalized);
                Log("INFO", "角色 {0} 已补齐并规范化出生主动/被动技能，共 {1} 项", character.Name, normalized.Count);
            }
        }

        internal static bool TryPurchaseSkill(SkillCatalog catalog, CharacterRecord character,
            List<CharacterSkillRecord> skills, byte skillId, out byte slot, out byte newLevel, out int cost)
        {
            slot = byte.MaxValue;
            newLevel = 0;
            cost = -1;
            if (catalog == null || character == null || skills == null) return false;

            var definition = catalog.Get(character.Job, skillId);
            cost = catalog.GetPurchaseCost(character.Job, skillId);
            if (definition == null || cost < 0 || character.SkillPoints < cost ||
                (definition.RequiredLevel > 0 && character.Level < definition.RequiredLevel))
                return false;

            var existing = skills.FirstOrDefault(value => value != null && value.SkillId == skillId);
            if (existing != null)
            {
                if (existing.Level == byte.MaxValue ||
                    (definition.MaximumLevel > 0 && existing.Level >= definition.MaximumLevel))
                    return false;
                slot = existing.Slot;
                newLevel = (byte)(existing.Level + 1);
            }
            else
            {
                slot = catalog.FindFreeSkillSlot(character.Job, skillId, skills);
                if (slot == byte.MaxValue) return false;
                newLevel = 1;
            }

            skills.RemoveAll(value => value != null && value.SkillId == skillId);
            skills.Add(new CharacterSkillRecord { Slot = slot, SkillId = skillId, Level = newLevel });
            skills.Sort((left, right) => left.Slot.CompareTo(right.Slot));
            character.SkillPoints -= cost;
            return true;
        }

        // Command 30 moves a skill between its panel slot and a quick slot.
        // The client swaps an occupied destination, so mirror that operation to
        // keep the persisted slot indexes unique.
        internal static bool TryMoveSkillSlot(List<CharacterSkillRecord> skills, byte sourceSlot, byte destinationSlot)
        {
            if (skills == null || sourceSlot == destinationSlot) return false;
            var source = skills.FirstOrDefault(value => value.Slot == sourceSlot);
            if (source == null) return false;
            var destination = skills.FirstOrDefault(value => value.Slot == destinationSlot);
            source.Slot = destinationSlot;
            if (destination != null) destination.Slot = sourceSlot;
            return true;
        }

        internal static bool TryConsumeInventoryItem(Dictionary<ushort, InventoryItemRecord> inventory,
            ConsumeItemRequestRecord request, out InventoryItemRecord item, out uint consumed, out uint remaining)
        {
            item = null;
            consumed = 0;
            remaining = 0;
            if (inventory == null || request == null || request.Operation != 2 ||
                !inventory.TryGetValue(request.Slot, out item) || item == null || item.Count == 0 ||
                (request.ItemId != 0 && request.ItemId != item.ItemId))
                return false;

            consumed = request.RequestedCount == 0 ? 1u : Math.Min(request.RequestedCount, item.Count);
            remaining = item.Count - consumed;
            if (remaining == 0)
                inventory.Remove(request.Slot);
            else
                item.Count = remaining;
            return true;
        }

        internal static bool TryRemoveSkillQuickSlot(List<CharacterSkillRecord> skills, byte sourceSlot)
        {
            if (skills == null || sourceSlot > 5) return false;
            var source = skills.FirstOrDefault(value => value != null && value.Slot == sourceSlot);
            if (source == null || source.Level == 0) return false;
            var occupied = new HashSet<byte>(skills.Where(value => value != null).Select(value => value.Slot));
            for (var slot = 6; slot <= 203; slot++)
            {
                if (!occupied.Contains((byte)slot))
                {
                    source.Slot = (byte)slot;
                    return true;
                }
            }
            return false;
        }

        private List<GroundDropRecord> RollMonsterGroundDrops(GroundDropState groundDrops,
            ushort runtimeId, ushort monsterIndex, int monsterLevel, int monsterType,
            int difficulty, Random random, out int corpseDropCount, out int independentDropCount)
        {
            if (groundDrops == null) throw new ArgumentNullException("groundDrops");
            if (random == null) throw new ArgumentNullException("random");

            var corpseItems = monsterIndex > 0
                ? _monsterCatalog.RollDrops(monsterIndex) : new List<int>();
            if (monsterIndex > 0)
            {
                corpseItems.AddRange(_monsterCatalog.RollSpecificDrops(monsterIndex));
                if (monsterType != 0)
                    corpseItems.AddRange(_monsterCatalog.RollChampionDrops(monsterIndex));
            }
            corpseDropCount = corpseItems.Count;

            var independent = _independentDropCatalog.Roll(monsterLevel,
                monsterType, difficulty, 1);
            independentDropCount = independent.Count;
            var drops = new List<GroundDropRecord>();
            foreach (var result in independent)
            {
                if (drops.Count >= byte.MaxValue) break;
                ushort durability;
                if (!TryGetGroundDropDurability(result.ItemId, out durability))
                {
                    Log("WARN", "忽略无效独立掉落 item={0}, monster={1}",
                        result.ItemId, monsterIndex);
                    continue;
                }
                if (result.Count == 0) continue;
                drops.Add(groundDrops.SpawnForMonster(runtimeId, result.ItemId,
                    result.Count, durability));
            }

            int worldItemId;
            if (drops.Count < byte.MaxValue &&
                _worldDropCatalog.TryRoll(monsterLevel, random, out worldItemId))
            {
                ushort durability;
                if (TryGetGroundDropDurability(worldItemId, out durability))
                    drops.Add(groundDrops.SpawnForMonster(runtimeId, worldItemId,
                        OfflineDropRules.ScaleItemCount(worldItemId, 1), durability));
            }

            foreach (var itemId in corpseItems)
            {
                if (drops.Count >= byte.MaxValue) break;
                ushort durability;
                if (!TryGetGroundDropDurability(itemId, out durability))
                {
                    Log("WARN", "忽略无效怪物掉落 item={0}, monster={1}",
                        itemId, monsterIndex);
                    continue;
                }
                drops.Add(groundDrops.SpawnForMonster(runtimeId, itemId,
                    OfflineDropRules.ScaleItemCount(itemId, 1), durability));
            }
            // Offline dungeon runs need a visible corpse reward even when the
            // PVF-specific, independent, and world tables all miss. Otherwise
            // a correct death notification still looks like a no-drop bug.
            if (drops.Count == 0)
            {
                ushort durability;
                if (TryGetGroundDropDurability(8, out durability))
                    drops.Add(groundDrops.SpawnForMonster(runtimeId, 8, 1, durability));
            }
            return drops;
        }

        private List<PassiveObjectDropRecord> GeneratePassiveObjectDrops(LorienDungeonRun run,
            GroundDropState groundDrops, Random random)
        {
            var result = new List<PassiveObjectDropRecord>();
            DungeonCatalog.Definition dungeon;
            if (run == null || groundDrops == null || random == null || _dungeonCatalog == null ||
                !_dungeonCatalog.TryGetDefinition(run.DungeonId, out dungeon)) return result;

            foreach (var rule in dungeon.PassiveObjectDropRules)
            {
                var dropRate = OfflineDropRules.ScaleDropChance(rule.ItemId, rule.DropRate, 10000);
                if (random.Next(10000) >= dropRate) continue;
                ushort durability;
                if (!TryGetGroundDropDurability(rule.ItemId, out durability))
                {
                    Log("WARN", "Ignored invalid passive-object drop dungeon={0}, object={1}, item={2}",
                        run.DungeonId, rule.ObjectIndex, rule.ItemId);
                    continue;
                }
                result.Add(groundDrops.SpawnForPassiveObject(rule.ObjectIndex, rule.ItemId,
                    OfflineDropRules.ScaleItemCount(rule.ItemId, 1u), durability));
            }
            return result;
        }

        internal static GameServerPacket[] BuildRestoredGroundDropPackets(ushort ownerUserId,
            GroundDropState drops, IEnumerable<PassiveObjectDropRecord> passiveDrops)
        {
            if (drops == null) return new GameServerPacket[0];
            var passiveRuntimeIds = new HashSet<ushort>((passiveDrops ??
                Enumerable.Empty<PassiveObjectDropRecord>()).Where(value => value != null && value.Drop != null)
                .Select(value => value.Drop.RuntimeId));
            return drops.Snapshot().Where(value => !passiveRuntimeIds.Contains(value.RuntimeId))
                .Select(value => GamePackets.GroundItemSpawned(ownerUserId, value)).ToArray();
        }

        private void AwardMonsterQuestDrops(ushort dungeonId, byte difficulty,
            ushort monsterIndex, IEnumerable<CharacterQuestRecord> activeQuests,
            Dictionary<ushort, InventoryItemRecord> inventory, Random random,
            ISet<ushort> changedSlots)
        {
            if (monsterIndex == 0 || inventory == null || random == null || changedSlots == null)
                return;
            foreach (var activeQuest in activeQuests ?? Enumerable.Empty<CharacterQuestRecord>())
            {
                QuestDefinition questDefinition;
                if (!_questCatalog.TryGet(activeQuest.QuestId, out questDefinition)) continue;
                var questDrops = QuestDropPlanner.PlanMonster(questDefinition,
                    dungeonId, difficulty, monsterIndex, inventory.Values,
                    chance => random.Next(1, 101) <= chance, true);
                foreach (var questDrop in questDrops)
                {
                    var itemSlot = AddInventoryReward(inventory,
                        questDrop.ItemId, questDrop.Count);
                    if (itemSlot == ushort.MaxValue)
                    {
                        Log("WARN", "任务道具页已满 quest={0}, monster={1}, item={2}",
                            activeQuest.QuestId, monsterIndex, questDrop.ItemId);
                        continue;
                    }
                    changedSlots.Add(itemSlot);
                }
            }
        }

        // Main item list: slots 0..2 are reserved, 3..8 are the item quick bar,
        // and the visible backpack cells begin at slot 9.
        private bool TryGetGroundDropDurability(int itemId, out ushort durability)
        {
            durability = 0;
            if (itemId == 0) return true; // Gold uses itemId zero on the ground-item wire format.
            if (itemId < 0 || itemId > ushort.MaxValue) return false;
            InventoryItemCategory category;
            if (!_itemCategoryCatalog.TryGetCategory((ushort)itemId, out category)) return false;
            if (category == InventoryItemCategory.Equipment)
                durability = _equipmentCatalog.GetMaximumDurability((ushort)itemId);
            return true;
        }

        private bool IsEquipmentItem(ushort itemId)
        {
            return _itemCategoryCatalog.GetCategory(itemId) == InventoryItemCategory.Equipment;
        }

        private static int GetItemPurchasePrice(ushort itemId, bool equipment)
        {
            if (itemId == 8) return 50;
            if (itemId == 3037) return 100;
            return equipment ? 1000 : 100;
        }

        private ushort FindFreeInventorySlot(Dictionary<ushort, InventoryItemRecord> inventory, ushort itemId)
        {
            return _itemCategoryCatalog.FindFreeSlot(inventory, itemId);
        }

        private static bool IsAvatarWearDbSlot(ushort slot)
        {
            return slot < AvatarWearSlotCount;
        }

        private static bool IsAvatarBagDbSlot(ushort slot)
        {
            return slot >= AvatarBagDbBase && slot <= AvatarBagDbLast;
        }

        internal static ushort AvatarBagDbToWire(ushort dbSlot)
        {
            return IsAvatarBagDbSlot(dbSlot)
                ? checked((ushort)(dbSlot - AvatarBagDbBase)) : ushort.MaxValue;
        }

        internal static ushort AvatarBagWireToDb(ushort wireSlot)
        {
            return wireSlot <= AvatarBagDbLast - AvatarBagDbBase
                ? checked((ushort)(wireSlot + AvatarBagDbBase)) : ushort.MaxValue;
        }

        internal static ushort ResolveAvatarContainerSlot(byte listType, ushort protocolSlot)
        {
            return listType == 1 ? AvatarBagWireToDb(protocolSlot) : protocolSlot;
        }

        internal static ushort FindFreeAvatarSlot(Dictionary<ushort, InventoryItemRecord> avatars)
        {
            for (var value = (int)AvatarBagDbBase; value <= AvatarBagDbLast; value++)
            {
                var slot = (ushort)value;
                if (!avatars.ContainsKey(slot)) return slot;
            }
            return ushort.MaxValue;
        }

        private static bool IsValidContainerSlot(byte listType, ushort slot)
        {
            if (listType == 1) return slot < byte.MaxValue;
            if (listType == 2) return slot < 120;
            return slot >= 3;
        }

        private static bool IsValidEquipmentMoveSlot(byte listType, ushort slot)
        {
            if (listType == 3) return slot < 0x17;
            return listType == 0 && slot >= 3;
        }

        internal static List<GameServerPacket> BuildItemListPacket(Dictionary<ushort, InventoryItemRecord> inventory, int gold,
            ushort? preferredLastSlot = null)
        {
            var packets = new List<GameServerPacket>();
            // 全部物品走 listType 0（ref server 行为）；客户端按页签槽位区间
            // （装备 9..40、消耗品 41..72、材料 73..104、任务 105..136）分页显示。
            var items = (inventory ?? new Dictionary<ushort, InventoryItemRecord>()).Values
                // Avatar expiration 0 is the permanent sentinel, so it remains visible.
                .Where(value => value != null && value.ItemId != 0)
                .OrderBy(x => preferredLastSlot.HasValue && x.Slot == preferredLastSlot.Value ? 1 : 0)
                .ThenBy(x => x.Slot)
                .ToList();
            using (var item = new MemoryStream())
            using (var writer = new BinaryWriter(item))
            {
                writer.Write((byte)0);                          // listType 0：主背包
                writer.Write((ushort)(items.Count + 1));
                // ARAD notification 13 handler 0x4240C4 reads 21-byte rows.
                // Slot 0/item 0 is the wallet record; data contains the gold.
                GamePackets.WriteAradItemRow(writer, 0, 0, (uint)Math.Max(0, gold), 0, 0, 0);
                foreach (var value in items)
                    GamePackets.WriteAradItemRow(writer, value.Slot, value.ItemId, value.Count,
                        value.State, value.Durability, value.SealState);
                packets.Add(new GameServerPacket(GamePackets.Notification, GamePackets.ItemList, item.ToArray()));
            }
            return packets;
        }

        private async Task SendItemListPacketsAsync(Stream stream, string remote,
            Dictionary<ushort, InventoryItemRecord> inventory, int gold, ushort? preferredLastSlot = null)
        {
            foreach (var packet in BuildItemListPacket(inventory, gold, preferredLastSlot))
                await SendAsync(stream, packet, remote).ConfigureAwait(false);
        }

        internal static GameServerPacket BuildAvatarListPacket(Dictionary<ushort, InventoryItemRecord> avatars)
        {
            using (var item = new MemoryStream())
            using (var writer = new BinaryWriter(item))
            {
                var bagRows = (avatars ?? new Dictionary<ushort, InventoryItemRecord>())
                    .Where(pair => pair.Value != null && pair.Value.ItemId != 0 && IsAvatarBagDbSlot(pair.Key))
                    .OrderBy(pair => pair.Key)
                    .ToArray();
                writer.Write((byte)1); // independent avatar inventory
                writer.Write((ushort)bagRows.Length);
                foreach (var pair in bagRows)
                {
                    var value = pair.Value;
                    GamePackets.WriteAradItemRow(writer, AvatarBagDbToWire(pair.Key), value.ItemId, value.Count,
                        value.State, value.Durability, value.SealState);
                    GamePackets.WriteAradItemExtension(writer, 1);
                }
                return new GameServerPacket(GamePackets.Notification, GamePackets.ItemList, item.ToArray());
            }
        }

        public void Dispose() { Stop(); _stopped.Dispose(); _udp.Dispose(); }
    }
}
