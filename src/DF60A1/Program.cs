using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DF60A1
{
    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetPriorityClass(
            System.Runtime.InteropServices.HandleRef hProcess, uint dwPriorityClass);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr SetProcessAffinityMask(
            System.Runtime.InteropServices.HandleRef hProcess, IntPtr dwProcessAffinityMask);

        private const uint HighPriorityClass = 0x00000080;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var options = ServerOptions.Parse(args);
            GameServerPacket.UseLegacyChannelHeader = options.LegacyChannelHeader;
            // The real ARAD client must finish character selection in
            // Elvengard area 1 (Gate.map / Seria's room).  With its legacy
            // protocol, the tutorial module mask starts dungeon 10000 as soon
            // as notification 136 arrives and skips the requested town entry.
            // Keep tutorial support for the reference client, but mark the
            // legacy ARAD selection flow as direct-to-town.
            GameServerPacket.TutorialEnabled = options.TutorialEnabled && !options.LegacyChannelHeader;
            QuestCatalog.MaxAcceptableQuests = options.MaxQuests;
            Console.WriteLine("可接任务上限: " + options.MaxQuests);

            if (args.Any(x => string.Equals(x, "--quest-probe", StringComparison.OrdinalIgnoreCase)))
            {
                var archive = new PvfArchiveReader(options.ScriptPvfPath);
                var questPaths = archive.EnumeratePaths("quest/").ToList();
                Console.WriteLine("quest/ 文件总数: " + questPaths.Count);
                Console.WriteLine("  .lst: " + questPaths.Count(p => p.EndsWith(".lst", StringComparison.OrdinalIgnoreCase)));
                Console.WriteLine("  .qst: " + questPaths.Count(p => p.EndsWith(".qst", StringComparison.OrdinalIgnoreCase)));
                var catalog = new QuestCatalog(archive);
                Console.WriteLine("QuestCatalog 加载任务数: " + catalog.Count);
                Console.WriteLine("  主线(epic): " + catalog.Definitions.Count(d => d.Grade == QuestGrade.Epic));
                Console.WriteLine("  支线(common unique): " + catalog.Definitions.Count(d => d.Grade == QuestGrade.CommonUnique));
                Console.WriteLine("  重复(normal repeat): " + catalog.Definitions.Count(d => d.Grade == QuestGrade.NormalRepeat));
                Console.WriteLine("  未知: " + catalog.Definitions.Count(d => d.Grade == QuestGrade.Unknown));
                // 列出所有 HuntMonster 任务（conditionRows 第3字段=1 或 0 且有 monster 数量）
                Console.WriteLine("--- HuntMonster 型任务 ---");
                foreach (var d in catalog.Definitions.Where(x => x.ConditionType == QuestConditionType.HuntMonster).OrderBy(x => x.Id).Take(10))
                    Console.WriteLine("  id={0} npc={1} cond=[{2}]", d.Id, d.NpcIndex, string.Join("|", d.ConditionRows.Select(r => "[" + string.Join(",", r) + "]")));
                // 详细输出 quest 2
                if (catalog.TryGet((ushort)2, out var q2))
                {
                    Console.WriteLine("--- quest 2 详细 ---");
                    Console.WriteLine("  type={0} sub={1}", q2.ConditionType, q2.ConditionSubType);
                    Console.WriteLine("  conditionRows=[{0}]", string.Join("|", q2.ConditionRows.Select(r => "[" + string.Join(",", r) + "]")));
                    Console.WriteLine("  rewardRows=[{0}]", string.Join("|", q2.RewardRows.Select(r => "[" + string.Join(",", r) + "]")));
                    Console.WriteLine("  prereq=[{0}] collision=[{1}]", string.Join(",", q2.PrerequisiteQuestIds), string.Join(",", q2.CollisionQuestIds));
                    Console.WriteLine("  npc={0} completeNpc={1}", q2.NpcIndex, q2.CompleteNpcIndex);
                }
                // 详细输出 quest 1007
                if (catalog.TryGet((ushort)1007, out var q1007))
                {
                    Console.WriteLine("--- quest 1007 详细 ---");
                    Console.WriteLine("  type={0} sub={1}", q1007.ConditionType, q1007.ConditionSubType);
                    Console.WriteLine("  conditionRows=[{0}]", string.Join("|", q1007.ConditionRows.Select(r => "[" + string.Join(",", r) + "]")));
                    Console.WriteLine("  rewardRows=[{0}]", string.Join("|", q1007.RewardRows.Select(r => "[" + string.Join(",", r) + "]")));
                    Console.WriteLine("  prereq=[{0}]", string.Join(",", q1007.PrerequisiteQuestIds));
                }
                foreach (var d in catalog.Definitions.OrderBy(x => x.Id).Take(15))
                    Console.WriteLine("  id={0} grade={1} npc={2} job={3} lv={4}-{5} type={6} prereq=[{7}]", d.Id, d.Grade, d.NpcIndex, d.Job, d.MinimumLevel, d.MaximumLevel, d.ConditionType, string.Join(",", d.PrerequisiteQuestIds));
                // 新手鬼剑士(level 1, job 0) 可接任务
                var novice = new CharacterRecord { Level = 1, Job = 0, GrowType = 0 };
                var acceptable = catalog.GetAcceptableQuestIds(novice, new CharacterQuestRecord[0], new HashSet<ushort>());
                Console.WriteLine("新手可接任务数: " + acceptable.Length);
                Console.WriteLine("  IDs: " + string.Join(",", acceptable));
                // 检查关键任务
                foreach (var qid in new ushort[] { 101, 102, 103, 104, 1016 })
                    Console.WriteLine("  quest {0}: {1}", qid, catalog.TryGet(qid, out var _) ? "存在" : "不存在");
                // 详细输出 quest 103（含健忘症等支线）
                if (catalog.TryGet((ushort)103, out var q103))
                {
                    Console.WriteLine("--- quest 103 详细 ---");
                    Console.WriteLine("  grade={0} npc={1} job={2} lv={3}-{4}", q103.Grade, q103.NpcIndex, q103.Job, q103.MinimumLevel, q103.MaximumLevel);
                    Console.WriteLine("  type={0} sub={1}", q103.ConditionType, q103.ConditionSubType);
                    Console.WriteLine("  rewardType={0} rewardRows=[{1}]", q103.RewardType, string.Join("|", q103.RewardRows.Select(r => "[" + string.Join(",", r) + "]")));
                    Console.WriteLine("  conditionRows=[{0}]", string.Join("|", q103.ConditionRows.Select(r => "[" + string.Join(",", r) + "]")));
                    Console.WriteLine("  selection=[{0}]", string.Join("|", q103.RewardSelectionRows.Select(r => "[" + string.Join(",", r) + "]")));
                    Console.WriteLine("  prereq=[{0}] collision=[{1}]", string.Join(",", q103.PrerequisiteQuestIds), string.Join(",", q103.CollisionQuestIds));
                    Console.WriteLine("  scriptPath={0}", q103.ScriptPath);
                }
                // 输出 quest 103 原始 .qst 文本
                uint ign;
                try
                {
                    var qstBytes = archive.ReadAllBytes(q103.ScriptPath.Replace('\\','/').TrimStart('/'), out ign);
                    var qstText = System.Text.Encoding.GetEncoding(949).GetString(qstBytes).Replace("\0","");
                    Console.WriteLine("=== quest 103 原始 .qst ===");
                    Console.WriteLine(qstText);
                }
                catch (Exception ex) { Console.WriteLine("读 .qst 失败: " + ex.Message); }
                return 0;
            }

            if (args.Any(x => string.Equals(x, "--inspect-pvf", StringComparison.OrdinalIgnoreCase)))
            {
                uint fileNumber;
                var bytes = new PvfArchiveReader(options.ScriptPvfPath).ReadAllBytes("etc/channel_info.etc", out fileNumber);
                Console.WriteLine("fileNumber={0} length={1}", fileNumber, bytes.Length);
                Console.WriteLine(Encoding.GetEncoding(949).GetString(bytes).Replace("\0", string.Empty));
                return 0;
            }

            var inspectIndex = Array.FindIndex(args, x => string.Equals(x, "--inspect-pvf-file", StringComparison.OrdinalIgnoreCase));
            if (inspectIndex >= 0)
            {
                if (inspectIndex + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--inspect-pvf-file 需要一个 PVF 内路径");
                    return 2;
                }
                uint fileNumber;
                var bytes = new PvfArchiveReader(options.ScriptPvfPath).ReadAllBytes(args[inspectIndex + 1], out fileNumber);
                Console.WriteLine("fileNumber={0} length={1}", fileNumber, bytes.Length);
                Console.WriteLine(Encoding.GetEncoding(949).GetString(bytes).Replace("\0", string.Empty));
                return 0;
            }

            var listIndex = Array.FindIndex(args, x => string.Equals(x, "--list-pvf", StringComparison.OrdinalIgnoreCase));
            if (listIndex >= 0)
            {
                var prefix = listIndex + 1 < args.Length ? args[listIndex + 1] : string.Empty;
                foreach (var path in new PvfArchiveReader(options.ScriptPvfPath).EnumeratePaths(prefix).OrderBy(value => value))
                    Console.WriteLine(path);
                return 0;
            }

            if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
                return SelfTest.Run();

            if (args.Any(x => string.Equals(x, "--skill-self-test", StringComparison.OrdinalIgnoreCase)))
                return SelfTest.RunSkillRegression();

            if (args.Any(x => string.Equals(x, "--audit-item-categories", StringComparison.OrdinalIgnoreCase)))
            {
                var catalog = new ItemCategoryCatalog(new PvfArchiveReader(options.ScriptPvfPath));
                Console.WriteLine("item definitions: " + catalog.Count);
                Console.WriteLine("equipment={0} stackable={1} consumable={2} material={3} quest={4} unclassified={5}",
                    catalog.EquipmentCount, catalog.StackableCount, catalog.ConsumableCount,
                    catalog.MaterialCount, catalog.QuestCount, catalog.UnclassifiedStackableCount);
                foreach (var pair in catalog.AuditStackableTypes())
                    Console.WriteLine("{0}: {1}", pair.Key, pair.Value);
                foreach (var pair in catalog.AuditStackableTypeExamples(4))
                    Console.WriteLine("  {0} examples: {1}", pair.Key, string.Join(", ", pair.Value));
                return catalog.UnclassifiedStackableCount == 0 &&
                    catalog.ConsumableCount + catalog.MaterialCount + catalog.QuestCount == catalog.StackableCount
                    ? 0 : 1;
            }

            if (args.Any(x => string.Equals(x, "--audit-drops", StringComparison.OrdinalIgnoreCase)))
            {
                var archive = new PvfArchiveReader(options.ScriptPvfPath);
                var monsters = new MonsterCatalog(archive);
                var gold = new MonsterGoldCatalog(archive);
                var independent = new MonsterIndependentDropCatalog(archive, gold);
                var world = new WorldDropCatalog(archive);
                Console.WriteLine("monsterTables={0} specificEntries={1}",
                    monsters.Count, monsters.SpecificDropCount);
                Console.WriteLine("independentRows={0} equipmentBuckets={1}",
                    independent.DropProbabilityCount, independent.EquipmentBucketCount);
                Console.WriteLine("worldRows={0} goldRows={1}", world.RowCount, gold.Count);
                var sampledDrops = 0;
                var sampledGold = 0;
                for (var sample = 0; sample < 2000; sample++)
                {
                    var rolled = independent.Roll(10, 0, 0, 1);
                    sampledDrops += rolled.Count;
                    sampledGold += rolled.Count(value => value.Kind == IndependentDropKind.Gold);
                }
                Console.WriteLine("sampledLevel10Kills=2000 drops={0} gold={1}",
                    sampledDrops, sampledGold);
                return monsters.Count > 0 && independent.DropProbabilityCount > 0 &&
                    independent.EquipmentBucketCount > 0 && gold.Count > 0 &&
                    sampledDrops > 0 && sampledGold > 0 ? 0 : 1;
            }

            if (args.Any(x => string.Equals(x, "--audit-cera-shop", StringComparison.OrdinalIgnoreCase)))
            {
                var archive = new PvfArchiveReader(options.ScriptPvfPath);
                var shop = new CeraShopCatalog(archive);
                var items = new ItemCategoryCatalog(archive);
                var equipment = new EquipmentCatalog(archive);
                var products = shop.Products.ToArray();
                var unknownItems = products.Where(product =>
                {
                    InventoryItemCategory category;
                    return !product.IsAvatar && !items.TryGetCategory(product.ItemId, out category);
                }).ToArray();
                var invalidAvatars = products.Where(product =>
                    product.IsAvatar && !equipment.IsAvatar(product.ItemId)).ToArray();
                Console.WriteLine("products={0} sections={1} zeroPrice={2} unknownItems={3} invalidAvatars={4}",
                    products.Length, products.Select(product => product.Section).Distinct().Count(),
                    products.Count(product => product.CashPrice == 0), unknownItems.Length,
                    invalidAvatars.Length);
                foreach (var section in products.GroupBy(product => product.Section)
                    .OrderBy(group => group.Key))
                    Console.WriteLine("{0}: {1}", section.Key, section.Count());
                foreach (var product in unknownItems.Take(20))
                    Console.WriteLine("unknown commodity={0} item={1} section={2}",
                        product.CommodityNo, product.ItemId, product.Section);
                foreach (var product in invalidAvatars.Take(20))
                    Console.WriteLine("invalid-avatar commodity={0} item={1}",
                        product.CommodityNo, product.ItemId);
                return products.Length > 0 && unknownItems.Length == 0 && invalidAvatars.Length == 0
                    ? 0 : 1;
            }

            if (args.Any(x => string.Equals(x, "--audit-dungeons", StringComparison.OrdinalIgnoreCase)))
            {
                var archive = new PvfArchiveReader(options.ScriptPvfPath);
                var dungeonCatalog = new DungeonCatalog(archive);
                foreach (var dungeonId in new ushort[] { 1, 2, 7 })
                {
                    DungeonCatalog.Definition definition;
                    if (!dungeonCatalog.TryGetDefinition(dungeonId, out definition)) continue;
                    Console.WriteLine("dungeon={0} size={1}x{2} start=({3},{4}) boss=({5},{6}) topology={7}",
                        dungeonId, definition.Width, definition.Height, definition.StartX, definition.StartY,
                        definition.BossX, definition.BossY, definition.RoomTopology);
                    for (byte y = 0; y < definition.Height; y++)
                        for (byte x = 0; x < definition.Width; x++)
                        {
                            ushort mapId;
                            if (dungeonCatalog.TryResolveDefaultLayoutMapId(dungeonId, x, y, out mapId))
                                Console.WriteLine("  room=({0},{1}) map={2}", x, y, mapId);
                        }
                }
                return 0;
            }

            if (args.Any(x => string.Equals(x, "--dungeon-layout-regression", StringComparison.OrdinalIgnoreCase)))
                return DungeonLayoutRegression.Run(options.ScriptPvfPath);

            if (args.Any(x => string.Equals(x, "--audit-stackable-types", StringComparison.OrdinalIgnoreCase)))
            {
                var archive = new PvfArchiveReader(options.ScriptPvfPath);
                uint ignored;
                var listText = Encoding.GetEncoding(949).GetString(
                    archive.ReadAllBytes("stackable/stackable.lst", out ignored)).Replace("\0", string.Empty);
                var paths = ItemCategoryCatalog.ParseStackableListEntries(listText);
                var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var errors = new List<string>();
                foreach (var pair in paths)
                {
                    try
                    {
                        var text = Encoding.GetEncoding(949).GetString(
                            archive.ReadAllBytes(pair.Value, out ignored)).Replace("\0", string.Empty);
                        var type = ItemCategoryCatalog.ParseStackableTypeName(text);
                        if (string.IsNullOrEmpty(type)) type = "<missing>";
                        int count;
                        counts.TryGetValue(type, out count);
                        counts[type] = count + 1;
                    }
                    catch (Exception exception)
                    {
                        if (errors.Count < 50)
                            errors.Add(pair.Key + "=" + pair.Value + " :: " + exception.GetType().Name + ": " + exception.Message);
                    }
                }
                foreach (var pair in counts)
                    Console.WriteLine("{0}: {1}", pair.Key, pair.Value);
                Console.WriteLine("errors: " + errors.Count);
                foreach (var error in errors)
                    Console.WriteLine("  " + error);
                return 0;
            }

            if (args.Any(x => string.Equals(x, "--seed-character", StringComparison.OrdinalIgnoreCase)))
                return SeedCharacter(options);

            if (args.Any(x => string.Equals(x, "--launch", StringComparison.OrdinalIgnoreCase)))
                return LaunchClient(options);

            if (!File.Exists(options.ScriptPvfPath))
            {
                Console.Error.WriteLine("找不到 Script.pvf。");
                Console.Error.WriteLine("请将与Arad客户端匹配的 Script.pvf 放到: " + options.ScriptPvfPath);
                return 5;
            }

            Directory.CreateDirectory(options.DataDirectory);
            Directory.CreateDirectory(options.LogDirectory);
            Console.WriteLine("存档数据库: " + options.DatabasePath);
            // DNF 1.0.0.75 是 2010 年的 32 位客户端，依赖系统定时器精度的
            // 主循环与网络超时。现代 Windows 默认 15.6ms 的计时器会让它
            // 出现随机卡顿/卡死（与运行环境相关，非协议逻辑）。服务端进程
            // 存活期间把系统计时器提升到 1ms，游戏会话全程生效。
            timeBeginPeriod(1);
            using (var store = new SqliteGameStore(options.DatabasePath, options.SchemaPath))
            using (var server = new GameServer(options, store))
            {
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                {
                    eventArgs.Cancel = true;
                    server.Stop();
                };

                server.Run();
            }

            return 0;
        }

        private static int SeedCharacter(ServerOptions options)
        {
            Directory.CreateDirectory(options.DataDirectory);
            using (var store = new SqliteGameStore(options.DatabasePath, options.SchemaPath))
            {
                var accountId = store.EnsureAccount(options.Account, options.Password);
                var created = store.CreateCharacter(accountId, options.CharacterName, options.CharacterJob);
                if (!created.Success)
                {
                    Console.Error.WriteLine("预置角色失败: " + created.Error);
                    return 4;
                }
                Console.WriteLine("已预置角色: {0}, job={1}, slot={2}", created.Character.Name, created.Character.Job, created.Character.Slot);
                return 0;
            }
        }

        private static int LaunchClient(ServerOptions options)
        {
            if (!File.Exists(options.ClientPath))
            {
                Console.Error.WriteLine("找不到客户端: " + options.ClientPath);
                return 2;
            }

            var launcherData = string.Join("?", new[]
            {
                "99", options.Host, options.Port.ToString(), options.Account, options.Password,
                "0", "0", "0", "0", "0", "0", "0", "0"
            });
            var info = new ProcessStartInfo(options.ClientPath, launcherData)
            {
                WorkingDirectory = Path.GetDirectoryName(options.ClientPath),
                UseShellExecute = false
            };
            var process = Process.Start(info);
            Console.WriteLine("已启动 DNF.exe，PID={0}", process == null ? 0 : process.Id);
            Console.WriteLine("启动参数: {0}", launcherData);
            // 老客户端在 Ryzen/多核平台上容易被后台进程抢占导致主循环
            // 超时挂起；把游戏进程优先级提到 High，减少调度抖动。
            if (process != null)
            {
                SetPriorityClass(new System.Runtime.InteropServices.HandleRef(process, process.Handle), HighPriorityClass);
                // 老客户端线程池在 16 线程 CPU 上可能随机卡死（多次转储显示
                // 线程数不断累积）。把游戏限制到前 8 个逻辑核心，减少调度
                // 抖动，这是老游戏常用的稳定化手段；不影响系统其余部分。
                SetProcessAffinityMask(new System.Runtime.InteropServices.HandleRef(process, process.Handle),
                    new IntPtr(0xFF));
            }
            return process == null ? 3 : 0;
        }
    }

    internal sealed class ServerOptions
    {
        public string Host = "127.0.0.1";
        public int Port = 7001;
        public string Account = "test";
        public string Password = "test";
        public string ClientPath;
        public string DataDirectory;
        public string LogDirectory;
        public string DatabasePath;
        public string SchemaPath;
        public string ScriptPvfPath;
        public string CharacterName = "赛丽亚测试";
        public byte CharacterJob;
        public bool AutoSelect;
        public bool LegacyChannelHeader = true;
        public bool TutorialEnabled = true;
        public int MaxQuests = 30;
        public int After49Notification = -1;
        public bool BurstTownNotifications;

        public static ServerOptions Parse(string[] args)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
            var clientDirectory = Path.GetFullPath(Path.Combine(projectRoot, ".."));
            // Keep saves outside bin/.  The executable is rebuilt on every
            // launch and there are several historical output directories in
            // this repository; using BaseDirectory here made direct launches
            // open a different SQLite database from the one used by the
            // bundled launcher (arad_server\arad_data).
            var sharedDataDirectory = Path.Combine(projectRoot, "arad_data");
            var options = new ServerOptions
            {
                DataDirectory = Directory.Exists(sharedDataDirectory)
                    ? sharedDataDirectory
                    : Path.Combine(baseDirectory, "data"),
                LogDirectory = Path.Combine(baseDirectory, "logs"),
                SchemaPath = Path.Combine(baseDirectory, "schema.sql")
            };
            var aradClient = Path.Combine(clientDirectory, "ARAD.exe");
            var dnfClient = Path.Combine(clientDirectory, "DNF.exe");
            options.ClientPath = File.Exists(aradClient) ? aradClient : dnfClient;
            options.ScriptPvfPath = Path.Combine(projectRoot, "data", "Script.pvf");

            for (var index = 0; index < args.Length; index++)
            {
                var value = index + 1 < args.Length ? args[index + 1] : null;
                switch (args[index].ToLowerInvariant())
                {
                    case "--host": if (value != null) options.Host = value; index++; break;
                    case "--port": if (value != null) options.Port = int.Parse(value); index++; break;
                    case "--account": if (value != null) options.Account = value; index++; break;
                    case "--password": if (value != null) options.Password = value; index++; break;
                    case "--client": if (value != null) options.ClientPath = Path.GetFullPath(value); index++; break;
                    case "--data": if (value != null) options.DataDirectory = Path.GetFullPath(value); index++; break;
                    case "--pvf": if (value != null) options.ScriptPvfPath = Path.GetFullPath(value); index++; break;
                    case "--character": if (value != null) options.CharacterName = value; index++; break;
                    case "--job": if (value != null) options.CharacterJob = byte.Parse(value); index++; break;
                    case "--auto-select": options.AutoSelect = true; break;
                    case "--legacy-channel-header":
                        options.LegacyChannelHeader = true;
                        break;
                    case "--tutorial":
                        if (value != null && value.StartsWith("off", StringComparison.OrdinalIgnoreCase))
                            options.TutorialEnabled = false;
                        else if (value != null && value.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                            options.TutorialEnabled = true;
                        index++;
                        break;
                    case "--max-quests":
                        if (value != null && int.TryParse(value, out var maxQuests) && maxQuests > 0)
                            options.MaxQuests = maxQuests;
                        index++;
                        break;
                    case "--after49":
                        if (value != null && int.TryParse(value, out var after49) && after49 >= 0 && after49 <= 255)
                            options.After49Notification = after49;
                        index++;
                        break;
                    case "--burst-town-notify":
                        options.BurstTownNotifications = true;
                        break;
                }
            }

            options.DatabasePath = Path.Combine(options.DataDirectory, "arad.sqlite");
            return options;
        }
    }
}
