using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DF60A1;

internal static class AradNetLauncher
{
    // Identifies the exact patch set in appended launcher logs.
    private const string OnlinePatchRevision = "20260813-restore-logical-slot-zero-sync";
    private const uint CreateSuspended = 0x00000004;
    private const uint MemCommit = 0x00001000;
    private const uint MemReserve = 0x00002000;
    private const uint PageExecuteReadWrite = 0x00000040;
    private const uint WaitTimeout = 0x00000102;
    private const uint HighPriorityClass = 0x00000080;

    // 客户端进入副本时，本地 town->dungeon fallback（0x00A59D63）会把选中
    // 地图写成 1001 再调用地图加载器 0x00A727C0。0x00A59D63 所在代码区运行期
    // 改写会触发客户端闪退（实测挂起时安装卡登录、运行中安装约 2 秒内断开），
    // 因此这里只挂安全的地图加载器入口，在 fallback 真正调用它时才动态覆盖。
    private const uint OfflineFallbackHook = 0x00A59D63;           // 仅作文档：绝不写入
    private const uint OfflineFallbackLoaderReturn = 0x00A59D81;
    private const uint MapLoaderHook = 0x00A727C0;
    private const uint MapLoaderResume = 0x00A727C5;
    private const uint MapSlotPointerGlobal = 0x00ED041C;
    private const uint RoomSceneTableGlobal = 0x00ED042C;
    private const uint ClientRootGlobal = 0x00ECE658;
    private const uint DefaultOfflineFallbackMap = 1001;
    private const int OfflineDungeonMapTableOffset = 0x200;
    // dungeon ID, start map ID, packed start room, maze width, packed Boss
    // room, and the PVF [greed] mask for the start room.
    private const int OfflineDungeonMapRecordSize = 24;
    private static readonly byte[] ExpectedMapLoaderBytes = { 0x55, 0x8B, 0xEC, 0x51, 0x56 };

    // The local fallback builds physical slot zero even when the PVF start
    // coordinate is elsewhere.  These hooks run around the native scene
    // constructor, before it caches Boss styling, doors, and minimap links.
    // They are armed by the server only for the selected dungeon.
    private const uint OnlineRoutePreconstructHook = 0x00486F44;
    private const uint OnlineRoutePreconstructResume = 0x00486F4A;
    private const uint OnlineRoutePreconstructAlternateHook = 0x00486F94;
    private const uint OnlineRoutePreconstructAlternateResume = 0x00486F9A;
    private const uint OnlineSceneConstructedHook = 0x00486F4F;
    private const uint OnlineSceneConstructedResume = 0x00486F55;
    private const uint OnlineSceneConstructedAlternateHook = 0x00486FA3;
    private const uint OnlineSceneConstructedAlternateResume = 0x00486FA9;
    private const uint OnlineBossSentinelHook = 0x00A59C1E;
    private const uint OnlineBossSentinelResume = 0x00A59C24;
    private const uint OnlineBossRestoreHook = 0x00A5BE09;
    private const uint OnlineBossRestoreResume = 0x00A5BE0F;
    // This is on ARAD's local town-to-dungeon path, before the Boss-coordinate
    // setter at 0x00A59C1E and before the fallback map loader.  Capturing the
    // selected dungeon here removes the race with a server reply.
    private const uint OnlineRouteCaptureHook = 0x00A59B22;
    private const uint OnlineRouteCaptureResume = 0x00A59B29;
    private const uint EncryptedBossXWrapper = 0x00ED03E0;
    private const uint EncryptedBossYWrapper = 0x00ED03E8;
    private const uint DungeonStateSetter = 0x00403F40;
    // Route state is shared by the pre-construction and post-construction
    // hooks. The scene hooks use the PVF greed mask after the native scene
    // object exists, so its minimap/door topology stays coherent with the
    // visible first-room exits. The fallback creates the scene at physical
    // slot zero; the target logical room cell and scene registration are both
    // patched before the client commits the minimap topology.
    // +0x60 stores the completed first-room scene pointer. Reserve it ahead
    // of the first injected route stub so the external monitor can restore a
    // logical scene-table registration if ARAD clears it after construction.
    private const int OnlineRouteStateSize = 0x70;
    private const int OnlineRoutePreconstructStubOffset = OnlineRouteStateSize;
    private const int OnlineRoutePreconstructAlternateStubOffset = 0x200;
    private const int OnlineSceneConstructedStubOffset = 0x400;
    private const int OnlineSceneConstructedAlternateStubOffset = 0x580;
    private const int OnlineBossSentinelStubOffset = 0x700;
    private const int OnlineBossRestoreStubOffset = 0x900;
    private const int OnlineRouteCaptureStubOffset = 0xB00;
    private const int OnlineRoutePatchAllocationTail = 0xC00;
    private static readonly byte[] ExpectedOnlineRoutePreconstructBytes =
        { 0x8B, 0x0B, 0x50, 0x51, 0x8B, 0xCF };
    private static readonly byte[] ExpectedOnlineRoutePreconstructAlternateBytes =
        { 0x8B, 0x13, 0x50, 0x52, 0x8B, 0xCF };
    private static readonly byte[] ExpectedOnlineSceneConstructedBytes =
        { 0x8B, 0x15, 0x2C, 0x04, 0xED, 0x00 };
    private static readonly byte[] ExpectedOnlineSceneConstructedAlternateBytes =
        { 0x8B, 0x0D, 0x2C, 0x04, 0xED, 0x00 };
    private static readonly byte[] ExpectedOnlineBossSentinelBytes =
        { 0x8B, 0x0D, 0x90, 0xC4, 0xEC, 0x00 };
    private static readonly byte[] ExpectedOnlineBossRestoreBytes =
        { 0x8B, 0x0D, 0x58, 0xE6, 0xEC, 0x00 };
    private static readonly byte[] ExpectedOnlineRouteCaptureBytes =
        { 0x6A, 0x00, 0x8B, 0xCE, 0x89, 0x75, 0xE0 };

    // The initial local fallback constructs the only live map object in physical
    // room slot 0, so that one path needs logical-slot -> slot-0 remapping.
    // Network START_MAP transitions are different: the native handler must use
    // the packet's real logical cell or it clears/reloads the active slot-0 C++
    // object and exits shortly afterwards. Moving/copying a 0x28-byte room
    // object is unsafe, so the two paths are kept as distinct patch modes.
    private const uint OnlineRoomCaptureHook = 0x00427CBD;
    private const uint OnlineRoomCaptureResume = 0x00427CC4;
    private const uint OnlineStateCaptureHook = 0x00A59B32;
    private const uint OnlineStateCaptureResume = 0x00A59B38;
    private const uint RoomLookupHook = 0x00404D45;
    private const uint RoomLookupResume = 0x00404D4D;
    private const uint OnlineCoordinateUseHook = 0x009A2C35;
    private const uint OnlineCoordinateUseResume = 0x009A2C3B;
    private const int OnlineStateCaptureStubOffset = 0x80;
    private const int OnlineRoomLookupStubOffset = 0x100;
    private const int OnlineCoordinateUseStubOffset = 0x200;
    private const int OnlineRoomStateOffset = 0x300;
    private const int OnlineRoomPatchSize = 0x340;
    private static readonly byte[] ExpectedOnlineRoomCaptureBytes =
        { 0x0F, 0xB6, 0x8D, 0x1B, 0xDF, 0xFF, 0xFF };
    private static readonly byte[] ExpectedOnlineStateCaptureBytes =
        { 0x89, 0xB7, 0xB4, 0x00, 0x00, 0x00 };
    private static readonly byte[] ExpectedRoomLookupBytes =
        { 0xA1, 0x1C, 0x04, 0xED, 0x00 };
    private static readonly byte[] ExpectedOnlineCoordinateUseBytes =
        { 0x8B, 0x86, 0xBC, 0x00, 0x00, 0x00 };

    private static readonly object Sync = new();
    private static readonly StringBuilder Log = new();
        private static readonly HashSet<uint> Patched = new();
        private static bool TraceNotify;
        private static bool TraceMap;
        private static bool TraceComic;
        private static bool NoNullGuard;
        private static bool Patch213Sender;
        private static bool ComicOnePage;
        private static bool ComicSkip;
        private static bool FakeKeys;
        private static bool TownStoryPointerGuard = true;
        private static bool TownGuards;
        private static int TownGuardStage;
        private static bool TownEffectGuardOnly;
        private static int EffectSkipPass;
        private static bool EffectRefreshCount;
        private static bool TraceEffectContainer;
        private static bool TraceEffectPass2;
        private static bool EffectPass1NodeGuard;
        private static bool TownResourceHandleGuard;
        private static bool TownNetworkTransitionGuard;
        private static bool BypassInitialLoadGuard;
        private static bool ForceRoomReinit;
        private static bool ForceRoomReinitGlobal;
        private static uint DungeonMapOverrideState;
        private static uint OnlineRoomPatchState;

    private static int Main(string[] args)
    {
        var client = GetOption(args, "--client");
        var workdir = GetOption(args, "--workdir") ?? Directory.GetCurrentDirectory();
        var host = GetOption(args, "--host") ?? "127.0.0.1";
        var port = GetOption(args, "--port") ?? "7001";
        var account = GetOption(args, "--account") ?? "test";
        var password = GetOption(args, "--password") ?? "test";
        var pvfPath = GetOption(args, "--pvf");
        // 副本首图补丁不再使用延迟安装：钩子在挂起阶段装入地图加载器入口，
        // 只有客户端本地 fallback 调用加载器时才动态生效。
        var logFile = GetOption(args, "--log");
        var attachPidText = GetOption(args, "--attach-pid");
        var attachFakeKeysPidText = GetOption(args, "--attach-fakekeys");
        var attachModeTracePidText = GetOption(args, "--attach-mode-trace");
        var watchOnlineRoomPidText = GetOption(args, "--watch-online-room");
        var onlineRoomStateText = GetOption(args, "--online-room-state");
        var dungeonOverrideStateText = GetOption(args, "--dungeon-override-state");
        var waitExit = HasFlag(args, "--wait-exit");
        TraceNotify = HasFlag(args, "--trace-notify");
        TraceMap = HasFlag(args, "--trace-map");
        TraceComic = HasFlag(args, "--trace-comic");
        // 普通 ARAD 登录必须保持客户端的城镇/渲染流程原样。以下教程与
        // 故障诊断补丁只允许显式启用，不能在每次联网启动时默认注入。
        ComicOnePage = HasFlag(args, "--comic-onepage");
        ComicSkip = HasFlag(args, "--comic-skip");
        FakeKeys = HasFlag(args, "--fake-keys");
        TownStoryPointerGuard = HasFlag(args, "--town-story-pointer-guard");
        TownGuards = HasFlag(args, "--town-guards");
        int parsedTownGuardStage;
        TownGuardStage = int.TryParse(GetOption(args, "--town-guard-stage"), out parsedTownGuardStage)
            ? Math.Max(0, Math.Min(6, parsedTownGuardStage))
            : 3;
        if (TownGuards)
            TownGuardStage = 6;
        TownEffectGuardOnly = HasFlag(args, "--town-effect-guard-only");
        int parsedEffectSkipPass;
        EffectSkipPass = int.TryParse(GetOption(args, "--effect-skip-pass"), out parsedEffectSkipPass)
            ? Math.Max(0, Math.Min(3, parsedEffectSkipPass))
            : 0;
        EffectRefreshCount = HasFlag(args, "--effect-refresh-count");
        TraceEffectContainer = HasFlag(args, "--trace-effect-container");
        TraceEffectPass2 = HasFlag(args, "--trace-effect-pass2");
        EffectPass1NodeGuard = HasFlag(args, "--effect-pass1-node-guard");
        TownResourceHandleGuard = HasFlag(args, "--town-resource-handle-guard");
        TownNetworkTransitionGuard = !HasFlag(args, "--no-town-network-transition-guard");
        // 客户端房间对象 0x009DCFA0 开头检查 [esi+414h]（"房间已初始化"防重入
        // 标志），非 0 时直接跳过整个房间初始化。若上一次副本/城镇切换后该
        // 标志残留（正常清零点 0x004172F5 未走到），收到新的 START_MAP 时会
        // 跳过初始化，导致内置格拉卡默认房间残留并覆盖在服务器房间之上。
        // 0x427BDF 会在每次 START_MAP（包括普通换房）执行。强制清零 +414h
        // 会让整个副本房间管理器重复构造，并使角色仍持有的状态对象失效。
        // 现在首图由 fallback 内存补丁修复，因此默认保留客户端原生防重入；
        // 仅诊断时显式使用 --force-room-reinit。
        // 旧的全函数 NOP 方案保留在 --force-room-reinit-global，供 A/B 对比。
        ForceRoomReinit = HasFlag(args, "--force-room-reinit");
        ForceRoomReinitGlobal = HasFlag(args, "--force-room-reinit-global");
        // 0xA4791C is the client's original re-entry guard for the initial room load.
        // Keep it intact in normal runs.  Bypassing it can start a second room
        // initialization while the selected dungeon is still being committed,
        // leaving the built-in Grakka room underneath the server-provided room.
        BypassInitialLoadGuard = HasFlag(args, "--bypass-initial-load-guard");
        // 客户端原生 0xAAED3C（mov eax,[ebx+104h]）在通道表项尚未初始化
        // 时 ebx=0，直接访问违规闪退（WER P8=006aed3c）。InstallChannelNullGuard
        // 在 0xBB5620 放守卫桩：ebx==0 或 [ebx+104h]==0 时跳到函数原有安全
        // 结尾 0xAAF005。此前桩内崩溃来自 --trace-map 的 DumpReport 探针自身
        // 解引用坏指针，与守卫桩无关。默认安装守卫，调试用 --no-nullguard 关闭。
        NoNullGuard = HasFlag(args, "--no-nullguard");
        // Keep the original command sender by default.  The optional suppression
        // switch is retained only for protocol diagnostics.
        Patch213Sender = HasFlag(args, "--suppress-213");

        if (HasFlag(args, "--verify-online-room-fix"))
            return VerifyOnlineRoomCoordinateFix();

        if (!string.IsNullOrWhiteSpace(watchOnlineRoomPidText))
        {
            int watchPid;
            uint watchState;
            uint watchDungeonOverrideState = 0;
            if (!int.TryParse(watchOnlineRoomPidText, out watchPid) || watchPid <= 0 ||
                !uint.TryParse(onlineRoomStateText, out watchState) || watchState < 0x10000)
            {
                WriteLine("无效的联机房间监控参数: pid=" + watchOnlineRoomPidText +
                    ", state=" + onlineRoomStateText);
                Flush(logFile);
                return 2;
            }
            if (!string.IsNullOrWhiteSpace(dungeonOverrideStateText) &&
                (!uint.TryParse(dungeonOverrideStateText, out watchDungeonOverrideState) ||
                 watchDungeonOverrideState < 0x10000))
            {
                WriteLine("无效的首房补丁状态参数: " + dungeonOverrideStateText);
                Flush(logFile);
                return 2;
            }
            return WatchOnlineRoomCoordinates(watchPid, watchState,
                watchDungeonOverrideState, logFile);
        }

        int attachPid;
        if (!string.IsNullOrWhiteSpace(attachModeTracePidText))
        {
            if (!int.TryParse(attachModeTracePidText, out attachPid) || attachPid <= 0)
            {
                WriteLine("无效 --attach-mode-trace: " + attachModeTracePidText);
                Flush(logFile);
                return 2;
            }
            return RunAttachedModeTrace(attachPid, logFile);
        }
        if (!string.IsNullOrWhiteSpace(attachFakeKeysPidText))
        {
            if (!int.TryParse(attachFakeKeysPidText, out attachPid) || attachPid <= 0)
            {
                WriteLine("无效 --attach-fakekeys: " + attachFakeKeysPidText);
                Flush(logFile);
                return 2;
            }
            return RunAttachedFakeKeys(attachPid, logFile);
        }
        if (!string.IsNullOrWhiteSpace(attachPidText))
        {
            if (!int.TryParse(attachPidText, out attachPid) || attachPid <= 0)
            {
                WriteLine("无效 --attach-pid: " + attachPidText);
                Flush(logFile);
                return 2;
            }
            return RunAttachedMapTrace(attachPid, logFile);
        }

        if (string.IsNullOrWhiteSpace(client))
        {
            var arad = Path.Combine(workdir, "ARAD.exe");
            var dnf = Path.Combine(workdir, "DNF.exe");
            client = File.Exists(arad) ? arad : dnf;
        }
        client = Path.GetFullPath(client);
        workdir = Path.GetFullPath(workdir);
        pvfPath = Path.GetFullPath(pvfPath ?? Path.Combine(workdir, "Script.pvf"));

        if (!File.Exists(client))
        {
            WriteLine("找不到客户端: " + client);
            Flush(logFile);
            return 2;
        }

        IReadOnlyList<OfflineDungeonMapping> offlineMappings;
        try
        {
            offlineMappings = BuildOfflineDungeonMappings(pvfPath);
        }
        catch (Exception exception)
        {
            WriteLine("读取 Script.pvf 失败: " + exception.Message);
            Flush(logFile);
            return 2;
        }
        var ridgeMapping = offlineMappings.FirstOrDefault(value => value.DungeonId == 40);
        WriteLine("已读取副本首图映射: count=" + offlineMappings.Count +
            (ridgeMapping.MapId == 0 ? string.Empty : ", dungeon=40 -> map=" + ridgeMapping.MapId));
        WriteLine("online patch revision=" + OnlinePatchRevision +
            "; START_MAP rebuild hook 0x00427CF9 is disabled");
        if (HasFlag(args, "--verify-dungeon-start-override"))
        {
            var tableAddress = 0x10000000u + OfflineDungeonMapTableOffset;
            var stateAddress = checked(tableAddress + sizeof(uint) + (uint)offlineMappings.Count * OfflineDungeonMapRecordSize);
            var stub = BuildDungeonMapLoaderOverrideStub(0x10000000u, tableAddress, stateAddress,
                stateAddress + 0x100);
            var ridgeSlot = (ridgeMapping.PackedStartRoom & 0xFF) +
                ((ridgeMapping.PackedStartRoom >> 8) & 0xFF) * ridgeMapping.Width;
            WriteLine("副本首房间补丁校验: stub=" + stub.Length + "/" + OfflineDungeonMapTableOffset +
                ", dungeon=40 room=(" + (ridgeMapping.PackedStartRoom & 0xFF) + "," +
                ((ridgeMapping.PackedStartRoom >> 8) & 0xFF) + "), slot=" + ridgeSlot);
            Flush(logFile);
            return stub.Length <= OfflineDungeonMapTableOffset ? 0 : 2;
        }

        int parsedPort;
        if (!int.TryParse(port, out parsedPort) || parsedPort < 1 || parsedPort > 65535)
        {
            WriteLine("无效端口: " + port);
            Flush(logFile);
            return 2;
        }

        var launcherData = string.Join("?", new[]
        {
            "99", host, parsedPort.ToString(), account, password,
            "0", "0", "0", "0", "0", "0", "0", "0"
        });

        WriteLine("client=" + client);
        WriteLine("workdir=" + workdir);
        WriteLine("args=" + launcherData);

        var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        PROCESS_INFORMATION processInfo;
        var commandLine = "\"" + client + "\" " + launcherData;
        if (!CreateProcess(client, commandLine, IntPtr.Zero, IntPtr.Zero, false,
            CreateSuspended, IntPtr.Zero, workdir,
            ref startupInfo, out processInfo))
        {
            WriteLine("CreateProcess 失败: " + Marshal.GetLastWin32Error());
            Flush(logFile);
            return 3;
        }
        WriteLine("已创建挂起客户端 PID=" + processInfo.dwProcessId);

        try
        {
            if (!TryPatch(processInfo.hProcess, offlineMappings))
            {
                WriteLine("补丁写入失败，正在结束客户端。");
                TerminateProcess(processInfo.hProcess, 0);
                return 4;
            }

            if (ResumeThread(processInfo.hThread) == unchecked((uint)-1))
            {
                WriteLine("ResumeThread 失败: " + Marshal.GetLastWin32Error());
                TerminateProcess(processInfo.hProcess, 0);
                return 5;
            }
            WriteLine("客户端已恢复运行，联网补丁生效。");
            StartOnlineRoomCoordinateMonitor(processInfo.dwProcessId, OnlineRoomPatchState,
                DungeonMapOverrideState, workdir);
            PublishDungeonRoomPatchState(workdir, (uint)processInfo.dwProcessId);

            // 动态副本首图补丁已随 TryPatch 装入地图加载器入口，不写
            // 0x00A59D63，也无需定时器：只有在客户端本地 fallback 调用加载器
            // （mapId=1001 且返回地址匹配）时才会把地图覆盖为选中副本的 PVF
            // 首图，并把正确 mapId 写回槽位供后续加载调用使用。
            SetPriorityClass(new HandleRef(null, processInfo.hProcess), HighPriorityClass);
            SetProcessAffinityMask(new HandleRef(null, processInfo.hProcess), new IntPtr(0xFF));

            if (waitExit)
            {
                WriteLine("等待客户端退出 ...");
                if (TraceMap)
                {
                    Thread.Sleep(3000);
                    LogRemoteModules(processInfo.hProcess);
                    InstallExitTrace(processInfo.hProcess);
                    InstallCrtExitTrace(processInfo.hProcess);
                    InstallDumpReportTrace(processInfo.hProcess);
                }
                var lastMapResult = 0u;
                var lastMapId = 0u;
                var lastRingCount = 0u;
                var lastExitCount = 0u;
                var lastCrtCounts = new uint[4];
                var lastDrCounts = new uint[3];
                var lastInternalCount = 0u;
                var lastGuardCount = 0u;
                var lastComicPage = 0u;
                var lastComicCount = 0u;
                var lastChannelSig = 0u;
                var lastOverrideCount = 0u;
                var pollDeadline = DateTime.UtcNow.AddSeconds(300);
                while (WaitForSingleObject(processInfo.hProcess, 0) != 0 && DateTime.UtcNow < pollDeadline)
                {
                    if (TraceMap)
                    {
                        var mapBuf = new byte[28];
                        IntPtr read;
                        if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0AC00), mapBuf, mapBuf.Length, out read))
                        {
                            var mapResult = BitConverter.ToUInt32(mapBuf, 0);
                            var mapId = BitConverter.ToUInt32(mapBuf, 8);
                            var dungeonInstance = BitConverter.ToUInt32(mapBuf, 16);
                            var roomObject = BitConverter.ToUInt32(mapBuf, 20);
                            var comicMarker = BitConverter.ToUInt32(mapBuf, 24);
                            if (mapResult != lastMapResult || mapId != lastMapId)
                            {
                                lastMapResult = mapResult;
                                lastMapId = mapId;
                                WriteLine("地图加载: mapId=" + mapId + " result=0x" + mapResult.ToString("X8") +
                                    " dungeon=0x" + dungeonInstance.ToString("X8") +
                                    " room=0x" + roomObject.ToString("X8") +
                                    " comic=0x" + comicMarker.ToString("X8"));
                            }
                        }
                    }
                    var ringBuf = new byte[0x120];
                    IntPtr ringRead;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0AA80), ringBuf, ringBuf.Length, out ringRead))
                    {
                        var ringCount = BitConverter.ToUInt32(ringBuf, 4);
                        if (ringCount != lastRingCount)
                        {
                            lastRingCount = ringCount;
                            var lastId = BitConverter.ToUInt32(ringBuf, 8);
                            var tail = new StringBuilder();
                            var start = ringCount > 20 ? ringCount - 20 : 0u;
                            for (var k = start; k < ringCount; k++)
                            {
                                tail.Append(ringBuf[12 + (int)(k & 0xFF)].ToString("X2")).Append(' ');
                            }
                            WriteLine("通知计数=" + ringCount + " last=0x" + lastId.ToString("X2") + " tail=" + tail);
                        }
                    }
                    var exitSlotBuf = new byte[16];
                    IntPtr exitRead;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0AD10), exitSlotBuf, exitSlotBuf.Length, out exitRead))
                    {
                        var exitCount = BitConverter.ToUInt32(exitSlotBuf, 12);
                        if (exitCount != 0 && exitCount != lastExitCount)
                        {
                            lastExitCount = exitCount;
                            var retAddr = BitConverter.ToUInt32(exitSlotBuf, 4);
                            var exitCode = BitConverter.ToUInt32(exitSlotBuf, 8);
                            WriteLine("ExitProcess 调用: count=" + exitCount +
                                " ret=0x" + retAddr.ToString("X8") +
                                " code=0x" + exitCode.ToString("X8"));
                        }
                    }
                    var crtSlotBuf = new byte[0x40];
                    IntPtr crtRead;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0AD20), crtSlotBuf, crtSlotBuf.Length, out crtRead))
                    {
                        for (var s = 0; s < 4; s++)
                        {
                            var count = BitConverter.ToUInt32(crtSlotBuf, s * 12 + 8);
                            if (count != 0 && count != lastCrtCounts[s])
                            {
                                lastCrtCounts[s] = count;
                                var retAddr = BitConverter.ToUInt32(crtSlotBuf, s * 12);
                                var code = BitConverter.ToUInt32(crtSlotBuf, s * 12 + 4);
                                WriteLine("CRT exit[" + s + "] 调用: count=" + count +
                                    " ret=0x" + retAddr.ToString("X8") +
                                    " code=0x" + code.ToString("X8"));
                            }
                        }
                    }
                    var drSlotBuf = new byte[0x40];
                    IntPtr drRead;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0AD80), drSlotBuf, drSlotBuf.Length, out drRead))
                    {
                        for (var s = 0; s < 3; s++)
                        {
                            var count = BitConverter.ToUInt32(drSlotBuf, s * 12 + 8);
                            if (count != 0 && count != lastDrCounts[s])
                            {
                                lastDrCounts[s] = count;
                                var retAddr = BitConverter.ToUInt32(drSlotBuf, s * 12);
                                var arg0 = BitConverter.ToUInt32(drSlotBuf, s * 12 + 4);
                                WriteLine("DumpReport[" + s + "] 调用: count=" + count +
                                    " ret=0x" + retAddr.ToString("X8") +
                                    " arg0=0x" + arg0.ToString("X8"));
                            }
                        }
                    }
                    var intSlotBuf = new byte[12];
                    IntPtr intRead;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0ADC0), intSlotBuf, intSlotBuf.Length, out intRead))
                    {
                        var count = BitConverter.ToUInt32(intSlotBuf, 8);
                        if (count != 0 && count != lastInternalCount)
                        {
                            lastInternalCount = count;
                            var retAddr = 0u;
                            var arg0 = BitConverter.ToUInt32(intSlotBuf, 0);
                            var fieldB4 = 0u;
                            var fieldB8 = 0u;
                            var extraBuf = new byte[0x40];
                            IntPtr extraRead;
                            if (arg0 != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)arg0 + 0xB4), extraBuf, 8, out extraRead))
                            {
                                fieldB4 = BitConverter.ToUInt32(extraBuf, 0);
                                fieldB8 = BitConverter.ToUInt32(extraBuf, 4);
                            }
                            var excCode = 0u;
                            var excAddr = 0u;
                            var excRec = 0u;
                            var contextPtr = 0u;
                            var eip = 0u;
                            var realEbp = 0u;
                            var realExcPtr = 0u;
                            var realEip = 0u;
                            var realCtxEbp = 0u;
                            var realEsp = 0u;
                            var regEax = 0u;
                            var regEcx = 0u;
                            var regEdx = 0u;
                            var regEbx = 0u;
                            var regEsi = 0u;
                            var regEdi = 0u;
                            var realExcCode = 0u;
                            var realExcAddr = 0u;
                            var ebpSlot = new byte[4];
                            IntPtr ebpSlotRead;
                            if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0ADE0), ebpSlot, 4, out ebpSlotRead))
                                realEbp = BitConverter.ToUInt32(ebpSlot, 0);
                            if (realEbp != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)realEbp + 8), ebpSlot, 4, out ebpSlotRead))
                            {
                                realExcPtr = BitConverter.ToUInt32(ebpSlot, 0);
                                var pair = new byte[8];
                                IntPtr pairRead;
                                if (realExcPtr != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)realExcPtr), pair, 8, out pairRead))
                                {
                                    var rec = BitConverter.ToUInt32(pair, 0);
                                    var ctx = BitConverter.ToUInt32(pair, 4);
                                    var tmp = new byte[4];
                                    IntPtr tmpRead;
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0xB8), tmp, 4, out tmpRead))
                                        realEip = BitConverter.ToUInt32(tmp, 0);
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0xB4), tmp, 4, out tmpRead))
                                        realCtxEbp = BitConverter.ToUInt32(tmp, 0);
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0xC4), tmp, 4, out tmpRead))
                                        realEsp = BitConverter.ToUInt32(tmp, 0);
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0xB0), tmp, 4, out tmpRead))
                                        regEax = BitConverter.ToUInt32(tmp, 0);
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0xAC), tmp, 4, out tmpRead))
                                        regEcx = BitConverter.ToUInt32(tmp, 0);
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0xA8), tmp, 4, out tmpRead))
                                        regEdx = BitConverter.ToUInt32(tmp, 0);
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0xA4), tmp, 4, out tmpRead))
                                        regEbx = BitConverter.ToUInt32(tmp, 0);
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0xA0), tmp, 4, out tmpRead))
                                        regEsi = BitConverter.ToUInt32(tmp, 0);
                                    if (ctx != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)ctx + 0x9C), tmp, 4, out tmpRead))
                                        regEdi = BitConverter.ToUInt32(tmp, 0);
                                    if (rec != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)rec), tmp, 4, out tmpRead))
                                        realExcCode = BitConverter.ToUInt32(tmp, 0);
                                    if (rec != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)rec + 0xC), tmp, 4, out tmpRead))
                                        realExcAddr = BitConverter.ToUInt32(tmp, 0);
                                }
                            }
                            var stackTrace = new StringBuilder();
                            var walkEbp = realCtxEbp;
                            for (var frame = 0; frame < 28 && walkEbp != 0; frame++)
                            {
                                var frameBuf = new byte[8];
                                IntPtr frameRead;
                                if (!ReadProcessMemory(processInfo.hProcess, new IntPtr((long)walkEbp), frameBuf, 8, out frameRead))
                                    break;
                                var nextEbp = BitConverter.ToUInt32(frameBuf, 0);
                                var ret = BitConverter.ToUInt32(frameBuf, 4);
                                stackTrace.Append(" f").Append(frame).Append("=").Append(ret.ToString("X8"));
                                if (nextEbp <= walkEbp) break;
                                walkEbp = nextEbp;
                            }
                            var espTrace = new StringBuilder();
                            if (realEsp != 0)
                            {
                                var espBuf = new byte[128];
                                IntPtr espRead;
                                if (ReadProcessMemory(processInfo.hProcess, new IntPtr((long)realEsp), espBuf, 128, out espRead))
                                {
                                    for (var j = 0; j + 4 <= espRead.ToInt32(); j += 4)
                                    {
                                        var v = BitConverter.ToUInt32(espBuf, j);
                                        if (v >= 0x400000 && v < 0x20000000)
                                            espTrace.Append(" [").Append((realEsp + (uint)j).ToString("X8")).Append("]=").Append(v.ToString("X8"));
                                    }
                                }
                            }
                            if (arg0 != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)arg0), extraBuf, 0x20, out extraRead))
                            {
                                excRec = BitConverter.ToUInt32(extraBuf, 0);
                                contextPtr = BitConverter.ToUInt32(extraBuf, 4);
                            }
                            if (excRec != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)excRec), extraBuf, 0x40, out extraRead))
                            {
                                excCode = BitConverter.ToUInt32(extraBuf, 0);
                                excAddr = BitConverter.ToUInt32(extraBuf, 12);
                            }
                            if (contextPtr != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)contextPtr + 0xB8), extraBuf, 4, out extraRead))
                                eip = BitConverter.ToUInt32(extraBuf, 0);
                            var argDump = "";
                            if (arg0 != 0 && ReadProcessMemory(processInfo.hProcess, new IntPtr((long)arg0), extraBuf, 0x30, out extraRead))
                            {
                                var sb = new StringBuilder();
                                for (var k = 0; k < 0x30; k++) sb.Append(extraBuf[k].ToString("X2")).Append(' ');
                                argDump = sb.ToString();
                            }
                            WriteLine("DumpReport!内部异常处理器 调用: count=" + count +
                                " ret=0x" + retAddr.ToString("X8") +
                                " arg0=0x" + arg0.ToString("X8") +
                                " ebp=0x" + realEbp.ToString("X8") +
                                " excptr=0x" + realExcPtr.ToString("X8") +
                                " realCode=0x" + realExcCode.ToString("X8") +
                                " realAddr=0x" + realExcAddr.ToString("X8") +
                                " realEip=0x" + realEip.ToString("X8") +
                                " realCtxEbp=0x" + realCtxEbp.ToString("X8") +
                                " realEsp=0x" + realEsp.ToString("X8") +
                                " eax=0x" + regEax.ToString("X8") +
                                " ecx=0x" + regEcx.ToString("X8") +
                                " edx=0x" + regEdx.ToString("X8") +
                                " ebx=0x" + regEbx.ToString("X8") +
                                " esi=0x" + regEsi.ToString("X8") +
                                " edi=0x" + regEdi.ToString("X8") +
                                " stack=" + stackTrace +
                                " espvals=" + espTrace +
                                " excRec=0x" + excRec.ToString("X8") +
                                " ctx=0x" + contextPtr.ToString("X8") +
                                " +B4=0x" + fieldB4.ToString("X8") +
                                " +B8=0x" + fieldB8.ToString("X8") +
                                " excCode=0x" + excCode.ToString("X8") +
                                " excAddr=0x" + excAddr.ToString("X8") +
                                " eip=0x" + eip.ToString("X8") +
                                " mem=" + argDump);
                        }
                    }
                    var guardSlotBuf = new byte[12];
                    IntPtr guardRead;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0AC30), guardSlotBuf, guardSlotBuf.Length, out guardRead))
                    {
                        var guardCount = BitConverter.ToUInt32(guardSlotBuf, 8);
                        if (guardCount != 0 && guardCount != lastGuardCount)
                        {
                            lastGuardCount = guardCount;
                            var retAddr = BitConverter.ToUInt32(guardSlotBuf, 0);
                            var thisPtr = BitConverter.ToUInt32(guardSlotBuf, 4);
                            WriteLine("空指针守卫触发: count=" + guardCount +
                                " ret=0x" + retAddr.ToString("X8") +
                                " this=0x" + thisPtr.ToString("X8"));
                        }
                    }
                    var comicStateBuf = new byte[16];
                    IntPtr comicStateRead;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0AC40), comicStateBuf, comicStateBuf.Length, out comicStateRead))
                    {
                        var comicThis = BitConverter.ToUInt32(comicStateBuf, 0);
                        var comicPage = BitConverter.ToUInt32(comicStateBuf, 4);
                        var comicCount = BitConverter.ToUInt32(comicStateBuf, 8);
                        var comicSub = BitConverter.ToUInt32(comicStateBuf, 12);
                        if (comicThis != 0 && (comicPage != lastComicPage || comicCount != lastComicCount))
                        {
                            lastComicPage = comicPage;
                            lastComicCount = comicCount;
                            WriteLine("漫画状态: this=0x" + comicThis.ToString("X8") +
                                " page=" + comicPage +
                                " count=" + comicCount +
                                " sub=" + comicSub);
                        }
                    }
                    var channelBuf = new byte[0x30];
                    IntPtr channelRead;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00F9FF30), channelBuf, channelBuf.Length, out channelRead))
                    {
                        var sig = 0u;
                        for (var c = 0; c < 8; c++)
                        {
                            var v = BitConverter.ToUInt32(channelBuf, c * 4);
                            sig = sig * 31 + v;
                        }
                        if (sig != lastChannelSig)
                        {
                            lastChannelSig = sig;
                            var idx = 0u;
                            var idxBuf = new byte[4];
                            IntPtr idxRead2;
                            if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00FA018C), idxBuf, 4, out idxRead2))
                                idx = BitConverter.ToUInt32(idxBuf, 0);
                            WriteLine("频道表: index=" + idx +
                                " [0]=" + BitConverter.ToUInt32(channelBuf, 0).ToString("X8") +
                                " [1]=" + BitConverter.ToUInt32(channelBuf, 4).ToString("X8") +
                                " [2]=" + BitConverter.ToUInt32(channelBuf, 8).ToString("X8") +
                                " [3]=" + BitConverter.ToUInt32(channelBuf, 12).ToString("X8") +
                                " [4]=" + BitConverter.ToUInt32(channelBuf, 16).ToString("X8") +
                                " [5]=" + BitConverter.ToUInt32(channelBuf, 20).ToString("X8") +
                                " [6]=" + BitConverter.ToUInt32(channelBuf, 24).ToString("X8") +
                                " [7]=" + BitConverter.ToUInt32(channelBuf, 28).ToString("X8"));
                        }
                    }
                    if (DungeonMapOverrideState != 0)
                    {
                        var overrideBuf = new byte[20];
                        IntPtr overrideRead;
                        if (ReadProcessMemory(processInfo.hProcess,
                                new IntPtr((long)DungeonMapOverrideState), overrideBuf, overrideBuf.Length, out overrideRead))
                        {
                            var overrideCount = BitConverter.ToUInt32(overrideBuf, 0);
                            if (overrideCount != lastOverrideCount)
                            {
                                lastOverrideCount = overrideCount;
                                var room = BitConverter.ToUInt32(overrideBuf, 12);
                                var roomX = room & 0xFF;
                                var roomY = (room >> 8) & 0xFF;
                                var width = BitConverter.ToUInt32(overrideBuf, 16);
                                var slot = roomX + roomY * width;
                                WriteLine("副本首图/首房间覆盖触发: count=" + overrideCount +
                                    " dungeon=" + BitConverter.ToUInt32(overrideBuf, 4) +
                                    " mapId=" + BitConverter.ToUInt32(overrideBuf, 8) +
                                    " room=(" + roomX + "," + roomY + "), slot=" + slot +
                                    ", width=" + width);
                            }
                        }
                    }
                    Thread.Sleep(25);
                }
                if (TraceMap)
                {
                    var exitBuf = new byte[32];
                    IntPtr read;
                    if (ReadProcessMemory(processInfo.hProcess, new IntPtr(0x00C0AD00), exitBuf, exitBuf.Length, out read))
                    {
                        var exitCount = BitConverter.ToUInt32(exitBuf, 12);
                        var entryCount = BitConverter.ToUInt32(exitBuf, 28);
                        if (exitCount != 0 || entryCount != 0)
                        {
                            var retAddr = BitConverter.ToUInt32(exitBuf, entryCount != 0 ? 20 : 4);
                            var exitCode = BitConverter.ToUInt32(exitBuf, entryCount != 0 ? 24 : 8);
                            WriteLine("ExitProcess 调用: count=" + (entryCount != 0 ? entryCount : exitCount) +
                                " ret=0x" + retAddr.ToString("X8") +
                                " code=0x" + exitCode.ToString("X8"));
                        }
                    }
                }
                WriteLine("客户端已退出。");
            }
            return 0;
        }
        finally
        {
            if (processInfo.hProcess != IntPtr.Zero) CloseHandle(processInfo.hProcess);
            if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);
            Flush(logFile);
        }
    }

    private static int RunAttachedMapTrace(int processId, string logFile)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var processHandle = process.Handle;
            var emptyTraceSlots = new byte[28];
            IntPtr written;
            if (!WriteProcessMemory(processHandle, new IntPtr(0x00C0AC00),
                    emptyTraceSlots, emptyTraceSlots.Length, out written))
            {
                WriteLine("清空地图跟踪槽失败: " + Marshal.GetLastWin32Error());
                Flush(logFile);
                return 4;
            }
            if (!InstallMapTrace(processHandle) || !InstallRoomTrace(processHandle))
            {
                WriteLine("附加地图跟踪桩失败，PID=" + processId);
                Flush(logFile);
                return 4;
            }

            WriteLine("已附加客户端地图跟踪，PID=" + processId + "；等待 START_MAP 通知...");
            var lastMapResult = 0u;
            var lastMapId = 0u;
            var deadline = DateTime.UtcNow.AddSeconds(300);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                var mapBuf = new byte[28];
                IntPtr read;
                if (ReadProcessMemory(processHandle, new IntPtr(0x00C0AC00),
                        mapBuf, mapBuf.Length, out read))
                {
                    var mapResult = BitConverter.ToUInt32(mapBuf, 0);
                    var mapId = BitConverter.ToUInt32(mapBuf, 8);
                    var dungeonInstance = BitConverter.ToUInt32(mapBuf, 16);
                    var roomObject = BitConverter.ToUInt32(mapBuf, 20);
                    if (mapResult != lastMapResult || mapId != lastMapId)
                    {
                        lastMapResult = mapResult;
                        lastMapId = mapId;
                        WriteLine("客户端实际加载: mapId=" + mapId +
                            " result=0x" + mapResult.ToString("X8") +
                            " dungeon=0x" + dungeonInstance.ToString("X8") +
                            " room=0x" + roomObject.ToString("X8"));
                        Flush(logFile);
                    }
                }
                Thread.Sleep(100);
            }
            WriteLine("客户端地图跟踪结束，PID=" + processId);
            Flush(logFile);
            return 0;
        }
        catch (Exception exception)
        {
            WriteLine("附加客户端地图跟踪失败: " + exception.Message);
            Flush(logFile);
            return 4;
        }
    }

    private static int RunAttachedFakeKeys(int processId, string logFile)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!InstallFakeKeys(process.Handle))
            {
                WriteLine("附加伪按键钩子失败，PID=" + processId);
                Flush(logFile);
                return 4;
            }
            WriteLine("已附加伪按键钩子，PID=" + processId + "；控制表指针位于 0x00C0AEF0。");
            Flush(logFile);
            return 0;
        }
        catch (Exception exception)
        {
            WriteLine("附加伪按键钩子失败: " + exception.Message);
            Flush(logFile);
            return 4;
        }
    }

    private static int RunAttachedModeTrace(int processId, string logFile)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!InstallStartMapModeTrace(process.Handle))
            {
                WriteLine("附加 START_MAP 模式记录桩失败，PID=" + processId);
                Flush(logFile);
                return 4;
            }
            WriteLine("START_MAP 模式记录桩已附加，结果位于 0x00C0AC20。");
            Flush(logFile);
            return 0;
        }
        catch (Exception exception)
        {
            WriteLine("附加 START_MAP 模式记录桩失败: " + exception.Message);
            Flush(logFile);
            return 4;
        }
    }

    private static bool InstallStartMapModeTrace(IntPtr processHandle)
    {
        // START_MAP handler @0x42820A compares the active game mode against 2.
        // Record that value, then preserve both original branch destinations.
        const uint stubVa = 0x00C0ABA0;
        var stub = new byte[24];
        var offset = 0;
        stub[offset++] = 0xA3;                                  // mov [0xC0AC20],eax
        WriteUInt32(stub, ref offset, 0x00C0AC20);
        stub[offset++] = 0x83; stub[offset++] = 0xF8; stub[offset++] = 0x02; // cmp eax,2
        stub[offset++] = 0x0F; stub[offset++] = 0x85;            // jne 0x42827D
        WriteRelative(stub, ref offset, stubVa + (uint)offset + 4, 0x0042827D);
        stub[offset++] = 0xE9;                                  // jmp 0x42820F
        WriteRelative(stub, ref offset, stubVa + (uint)offset + 4, 0x0042820F);

        IntPtr written;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out written) ||
            !WriteProcessMemory(processHandle, new IntPtr(0x00C0AC20), new byte[4], 4, out written))
        {
            WriteLine("START_MAP 模式记录桩写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }
        var entry = new byte[] { 0xE9, 0, 0, 0, 0 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, 0x0042820F, stubVa);
        return Patch(processHandle, 0x0042820A,
            new byte[] { 0x83, 0xF8, 0x02, 0x75, 0x6E }, entry);
    }

    private static string GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool TryPatch(IntPtr processHandle, IReadOnlyList<OfflineDungeonMapping> dungeonMappings)
    {
        var ok = true;
        // The native sender already serializes command 131 with a live actor
        // ID. Its final local-player comparison excludes ordinary monsters;
        // leave its earlier entity validation intact and only remove that gate.
        ok &= InstallNativeMonsterDeathReportPatch(processHandle);
        // Notification 22 (USER_POSITION) normally skips the local actor:
        // ARAD assumes the local client owns its movement and branches around
        // the entity lookup when packet uid == the current player uid.  The
        // simulator is authoritative for dungeon entry, so let this packet
        // update the local actor through the same vtable position routine used
        // for remote actors.
        ok &= Patch(processHandle, 0x0042613E,
            new byte[] { 0x0F, 0x84, 0xFC, 0x00, 0x00, 0x00 },
            new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
        ok &= Patch(processHandle, 0x40C950, new byte[] { 0x7D, 0x15 }, new byte[] { 0xEB, 0x15 });
        ok &= Patch(processHandle, 0x40CC93, new byte[] { 0x7D, 0x34 }, new byte[] { 0xEB, 0x34 });
        ok &= Patch(processHandle, 0x40CCD0, new byte[] { 0x75, 0x19 }, new byte[] { 0xEB, 0x19 });
        ok &= Patch(processHandle, 0x40CD00, new byte[] { 0x75, 0x08 }, new byte[] { 0x90, 0x90 });
            if (BypassInitialLoadGuard)
                ok &= Patch(processHandle, 0xA4791C, new byte[] { 0x0F, 0x84, 0x0E, 0x03, 0x00, 0x00 }, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
            if (!NoNullGuard)
                ok &= InstallChannelNullGuard(processHandle);
            if (TownStoryPointerGuard || TownGuardStage > 0)
                ok &= InstallTownSceneGuard(processHandle, TownGuardStage);
            if (TownEffectGuardOnly && TownGuardStage < 6)
                ok &= InstallTownEffectNodeGuard(processHandle);
            if (EffectSkipPass > 0)
                ok &= InstallTownEffectPassSkip(processHandle, EffectSkipPass);
            if (EffectRefreshCount)
                ok &= InstallTownEffectCountRefresh(processHandle);
            if (TraceEffectContainer)
                ok &= InstallTownEffectContainerTrace(processHandle);
            if (TraceEffectPass2)
                ok &= InstallTownEffectPass2Trace(processHandle);
            if (EffectPass1NodeGuard)
                ok &= InstallTownEffectPass1NodeGuard(processHandle);
            if (TownNetworkTransitionGuard)
                ok &= InstallTownNetworkTransitionGuard(processHandle);
            if (TownResourceHandleGuard)
                ok &= InstallTownResourceHandleGuard(processHandle);
            if (ForceRoomReinit)
                ok &= InstallStartMapRoomResetGuard(processHandle);
            ok &= InstallStartMapMapLoaderTrace(processHandle);
            ok &= InstallOnlineRoomCoordinateFix(processHandle);
            ok &= InstallDungeonMapLoaderOverride(processHandle, dungeonMappings,
                OnlineRoomPatchState);
            if (ForceRoomReinitGlobal)
            {
                // 旧方案：0x009DCFA0 的 "jne 009DD2A1"（跳过房间初始化）整体
                // NOP，强制所有调用方（城镇/副本）都执行初始化。仅诊断用。
                ok &= Patch(processHandle, 0x009DCFA0 + 0x11,
                    new byte[] { 0x0F, 0x85, 0xEA, 0x02, 0x00, 0x00 },
                    new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
                if (ok)
                    WriteLine("房间初始化强制（全局）已启用: 0x009DCFB1 jne -> NOP (--force-room-reinit-global)");
            }
            if (TraceNotify)
                ok &= InstallNotifyTrace(processHandle);
            if (TraceMap)
                ok &= InstallMapTrace(processHandle);
            if (TraceMap)
                ok &= InstallRoomTrace(processHandle);
            if (TraceMap)
                ok &= InstallComicTrace(processHandle);
            if (TraceComic)
                ok &= InstallComicTrace(processHandle);
            if (TraceMap)
                ok &= InstallComicStateTrace(processHandle);
            if (TraceComic)
                ok &= InstallComicStateTrace(processHandle);
            if (Patch213Sender)
        {
            // 命令 213 由 0xAA5AC0 发送（经命令回执包装器）。直接 no-op
            // 发送函数会破坏其他启动路径（实测客户端连不上服务器），
            // 因此只跳过教程初始化里的调用点 0xAA65C6：
            //   E8 F5 F4 FF FF  = call 0xAA5AC0
            // 该调用点忽略返回值，NOP 掉后客户端不再发送 213，
            // 也就不会进入“未回包导致响应缓冲损坏 → 闪退”的路径。
                ok &= Patch(processHandle, 0x00AA65C6,
                new byte[] { 0xE8, 0xF5, 0xF4, 0xFF, 0xFF },
                new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
        }
            if (ComicOnePage)
                ok &= InstallComicOnePage(processHandle);
            if (ComicSkip)
                ok &= InstallComicSkip(processHandle);
            if (FakeKeys)
                ok &= InstallFakeKeys(processHandle);
        IntPtr bytesWritten;
        if (WriteProcessMemory(processHandle, new IntPtr(0xECD566), new byte[] { 0x01 }, 1, out bytesWritten))
            WriteLine("联网标志 0xECD566 已置 1。");
        else
            ok = false;
        return ok;
    }

    private static bool InstallNativeMonsterDeathReportPatch(IntPtr processHandle)
    {
        const uint senderGateVa = 0x004A99F3;
        const uint senderGateJumpVa = 0x004A99FC;
        var expectedGate = new byte[]
        {
            0x8B, 0xCE, 0xE8, 0xF6, 0xF9, 0xFE, 0xFF,
            0x84, 0xC0, 0x74, 0x79
        };
        var currentGate = new byte[expectedGate.Length];
        IntPtr bytesRead;
        if (!ReadProcessMemory(processHandle, new IntPtr(senderGateVa), currentGate,
                currentGate.Length, out bytesRead) || bytesRead.ToInt64() != currentGate.Length ||
            !currentGate.SequenceEqual(expectedGate))
        {
            WriteLine("native-monster-death patch rejected: unexpected ARAD.exe bytes at 0x" +
                senderGateVa.ToString("X8"));
            return false;
        }

        var ok = Patch(processHandle, senderGateJumpVa, new byte[] { 0x74, 0x79 },
            new byte[] { 0x90, 0x90 });
        if (ok)
            WriteLine("native-monster-death report enabled: cmd=131 for ordinary monsters");
        return ok;
    }

    private static void PublishDungeonRoomPatchState(string workdir, uint processId)
    {
        if (DungeonMapOverrideState == 0) return;
        var signalPath = Environment.GetEnvironmentVariable("ARAD_ROOM_PATCH_SIGNAL");
        if (string.IsNullOrWhiteSpace(signalPath))
            signalPath = Path.Combine(workdir, "arad_room_patch.state");
        try
        {
            File.WriteAllText(signalPath, processId + "|" + DungeonMapOverrideState.ToString("X8"), Encoding.ASCII);
            WriteLine("副本房间纯内存补丁待命: " + signalPath);
        }
        catch (Exception exception)
        {
            WriteLine("副本房间补丁状态发布失败: " + exception.Message);
        }
    }

    private static IReadOnlyList<OfflineDungeonMapping> BuildOfflineDungeonMappings(string pvfPath)
    {
        if (!File.Exists(pvfPath))
            throw new FileNotFoundException("Script.pvf 不存在", pvfPath);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var catalog = new DungeonCatalog(new PvfArchiveReader(pvfPath));
        var mappings = new List<OfflineDungeonMapping>();
        for (var rawDungeonId = 0; rawDungeonId <= ushort.MaxValue; rawDungeonId++)
        {
            var dungeonId = (ushort)rawDungeonId;
            DungeonCatalog.RunLayout layout;
            DungeonCatalog.Room room;
            if (!catalog.TrySelectRunLayout(dungeonId, null, out layout) || layout.Width == 0 ||
                layout.Height == 0 || layout.Maze.RoomTopology == null ||
                !catalog.TryCreateRoom(layout, layout.StartX, layout.StartY, out room) ||
                room.MapId == 0)
                continue;

            // The client constructs only this local fallback scene during
            // dungeon selection.  Supplying every room here made its native
            // start-map path bind a later route record before START_MAP exists.
            // Subsequent rooms remain owned by the normal START_MAP handler.
            var startSlot = layout.StartX + layout.StartY * layout.Width;
            if (startSlot < 0 || startSlot >= layout.Maze.RoomTopology.Length)
                continue;
            var symbol = char.ToUpperInvariant(layout.Maze.RoomTopology[startSlot]);
            var doorMask = symbol >= 'A' && symbol <= 'P'
                ? (byte)(symbol - 'A')
                : (byte)0;
            mappings.Add(new OfflineDungeonMapping(dungeonId, room.MapId, layout.StartX,
                layout.StartY, layout.Width, layout.BossX, layout.BossY, doorMask));
        }
        if (mappings.Count == 0)
            throw new InvalidDataException("Script.pvf 中没有可用的副本房间");
        return mappings;
    }

    private static bool InstallDungeonMapLoaderOverride(IntPtr process,
        IReadOnlyList<OfflineDungeonMapping> mappings, uint onlineRoomStateAddress)
    {
        if (mappings == null || mappings.Count == 0)
        {
            WriteLine("Script.pvf 中没有可用的副本首图映射");
            return false;
        }

        var original = new byte[ExpectedMapLoaderBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(MapLoaderHook), original, original.Length, out _) ||
            !original.SequenceEqual(ExpectedMapLoaderBytes))
        {
            WriteLine("0x" + MapLoaderHook.ToString("X8") + " 的客户端字节不匹配，未安装动态副本首图补丁");
            return false;
        }
        if (!VerifyPatchSite(process, OnlineRoutePreconstructHook,
                ExpectedOnlineRoutePreconstructBytes) ||
            !VerifyPatchSite(process, OnlineRoutePreconstructAlternateHook,
                ExpectedOnlineRoutePreconstructAlternateBytes) ||
            !VerifyPatchSite(process, OnlineSceneConstructedHook,
                ExpectedOnlineSceneConstructedBytes) ||
            !VerifyPatchSite(process, OnlineSceneConstructedAlternateHook,
                ExpectedOnlineSceneConstructedAlternateBytes) ||
            !VerifyPatchSite(process, OnlineBossSentinelHook,
                ExpectedOnlineBossSentinelBytes) ||
            !VerifyPatchSite(process, OnlineBossRestoreHook,
                ExpectedOnlineBossRestoreBytes) ||
            !VerifyPatchSite(process, OnlineRouteCaptureHook,
                ExpectedOnlineRouteCaptureBytes))
        {
            WriteLine("The ARAD.exe route-construction bytes do not match this supported build.");
            return false;
        }

        var stateOffset = checked(OfflineDungeonMapTableOffset + sizeof(uint) + mappings.Count * OfflineDungeonMapRecordSize);
        // state: legacy loader fields followed by route preconstruction state.
        var payloadLength = checked(stateOffset + OnlineRoutePatchAllocationTail);
        var allocation = VirtualAllocEx(process, IntPtr.Zero, payloadLength,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (allocation == IntPtr.Zero || allocation.ToInt64() < 0 || allocation.ToInt64() > uint.MaxValue)
        {
            WriteLine("VirtualAllocEx 失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        var stubAddress = checked((uint)allocation.ToInt64());
        var mapTableAddress = checked((uint)(stubAddress + OfflineDungeonMapTableOffset));
        var stateAddress = checked((uint)(stubAddress + stateOffset));
        var stub = BuildDungeonMapLoaderOverrideStub(stubAddress, mapTableAddress, stateAddress,
            onlineRoomStateAddress);
        var routePreconstructAddress = checked(stateAddress + OnlineRoutePreconstructStubOffset);
        var routePreconstructAlternateAddress = checked(stateAddress + OnlineRoutePreconstructAlternateStubOffset);
        var sceneConstructedAddress = checked(stateAddress + OnlineSceneConstructedStubOffset);
        var sceneConstructedAlternateAddress = checked(stateAddress + OnlineSceneConstructedAlternateStubOffset);
        var bossSentinelAddress = checked(stateAddress + OnlineBossSentinelStubOffset);
        var bossRestoreAddress = checked(stateAddress + OnlineBossRestoreStubOffset);
        var routeCaptureAddress = checked(stateAddress + OnlineRouteCaptureStubOffset);
        var routePreconstruct = BuildOnlineRoutePreconstructStub(routePreconstructAddress,
            stateAddress, mapTableAddress, ExpectedOnlineRoutePreconstructBytes,
            OnlineRoutePreconstructResume);
        var routePreconstructAlternate = BuildOnlineRoutePreconstructStub(routePreconstructAlternateAddress,
            stateAddress, mapTableAddress, ExpectedOnlineRoutePreconstructAlternateBytes,
            OnlineRoutePreconstructAlternateResume);
        var sceneConstructed = BuildOnlineSceneConstructedStub(sceneConstructedAddress,
            stateAddress, mapTableAddress, ExpectedOnlineSceneConstructedBytes,
            OnlineSceneConstructedResume, true);
        var sceneConstructedAlternate = BuildOnlineSceneConstructedStub(sceneConstructedAlternateAddress,
            stateAddress, mapTableAddress, ExpectedOnlineSceneConstructedAlternateBytes,
            OnlineSceneConstructedAlternateResume, false);
        var bossSentinel = BuildOnlineBossSentinelStub(bossSentinelAddress, stateAddress,
            mapTableAddress);
        var bossRestore = BuildOnlineBossRestoreStub(bossRestoreAddress, stateAddress);
        var routeCapture = BuildOnlineRouteCaptureStub(routeCaptureAddress, stateAddress,
            mapTableAddress);
        if (stub.Length > OfflineDungeonMapTableOffset)
        {
            WriteLine("动态副本首图补丁桩超出预留空间");
            return false;
        }
        if (routePreconstruct.Length > OnlineRoutePreconstructAlternateStubOffset -
                OnlineRoutePreconstructStubOffset ||
            routePreconstructAlternate.Length > OnlineSceneConstructedStubOffset -
                OnlineRoutePreconstructAlternateStubOffset ||
            sceneConstructed.Length > OnlineSceneConstructedAlternateStubOffset -
                OnlineSceneConstructedStubOffset ||
            sceneConstructedAlternate.Length > OnlineBossSentinelStubOffset -
                OnlineSceneConstructedAlternateStubOffset ||
            bossSentinel.Length > OnlineBossRestoreStubOffset - OnlineBossSentinelStubOffset ||
            bossRestore.Length > OnlineRouteCaptureStubOffset - OnlineBossRestoreStubOffset ||
            routeCapture.Length > OnlineRoutePatchAllocationTail - OnlineRouteCaptureStubOffset)
        {
            WriteLine("The online PVF route stubs exceed their reserved memory.");
            return false;
        }

        var payload = new byte[payloadLength];
        Buffer.BlockCopy(stub, 0, payload, 0, stub.Length);
        Buffer.BlockCopy(routePreconstruct, 0, payload,
            stateOffset + OnlineRoutePreconstructStubOffset, routePreconstruct.Length);
        Buffer.BlockCopy(routePreconstructAlternate, 0, payload,
            stateOffset + OnlineRoutePreconstructAlternateStubOffset, routePreconstructAlternate.Length);
        Buffer.BlockCopy(sceneConstructed, 0, payload,
            stateOffset + OnlineSceneConstructedStubOffset, sceneConstructed.Length);
        Buffer.BlockCopy(sceneConstructedAlternate, 0, payload,
            stateOffset + OnlineSceneConstructedAlternateStubOffset, sceneConstructedAlternate.Length);
        Buffer.BlockCopy(bossSentinel, 0, payload,
            stateOffset + OnlineBossSentinelStubOffset, bossSentinel.Length);
        Buffer.BlockCopy(bossRestore, 0, payload,
            stateOffset + OnlineBossRestoreStubOffset, bossRestore.Length);
        Buffer.BlockCopy(routeCapture, 0, payload,
            stateOffset + OnlineRouteCaptureStubOffset, routeCapture.Length);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)mappings.Count), 0, payload,
            OfflineDungeonMapTableOffset, sizeof(uint));
        var cursor = OfflineDungeonMapTableOffset + sizeof(uint);
        foreach (var mapping in mappings)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(mapping.DungeonId), 0, payload, cursor, sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes(mapping.MapId), 0, payload, cursor + sizeof(uint), sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes(mapping.PackedStartRoom), 0, payload, cursor + 2 * sizeof(uint), sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes(mapping.Width), 0, payload, cursor + 3 * sizeof(uint), sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes(mapping.PackedBossRoom), 0, payload, cursor + 4 * sizeof(uint), sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes(mapping.StartDoorMask), 0, payload, cursor + 5 * sizeof(uint), sizeof(uint));
            cursor += OfflineDungeonMapRecordSize;
        }

        IntPtr written;
        if (!WriteProcessMemory(process, allocation, payload, payload.Length, out written) ||
            !FlushInstructionCache(process, allocation, (UIntPtr)payload.Length))
        {
            WriteLine("写入动态副本首图补丁桩失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        var routePreconstructEntry = CreateJumpEntry(ExpectedOnlineRoutePreconstructBytes.Length,
            OnlineRoutePreconstructHook, routePreconstructAddress);
        var routePreconstructAlternateEntry = CreateJumpEntry(
            ExpectedOnlineRoutePreconstructAlternateBytes.Length,
            OnlineRoutePreconstructAlternateHook, routePreconstructAlternateAddress);
        var sceneConstructedEntry = CreateJumpEntry(ExpectedOnlineSceneConstructedBytes.Length,
            OnlineSceneConstructedHook, sceneConstructedAddress);
        var sceneConstructedAlternateEntry = CreateJumpEntry(
            ExpectedOnlineSceneConstructedAlternateBytes.Length,
            OnlineSceneConstructedAlternateHook, sceneConstructedAlternateAddress);
        var bossSentinelEntry = CreateJumpEntry(ExpectedOnlineBossSentinelBytes.Length,
            OnlineBossSentinelHook, bossSentinelAddress);
        var bossRestoreEntry = CreateJumpEntry(ExpectedOnlineBossRestoreBytes.Length,
            OnlineBossRestoreHook, bossRestoreAddress);
        var routeCaptureEntry = CreateJumpEntry(ExpectedOnlineRouteCaptureBytes.Length,
            OnlineRouteCaptureHook, routeCaptureAddress);
        var entry = new byte[5] { 0xE9, 0, 0, 0, 0 };
        WriteRelative(entry, 1, checked(MapLoaderHook + 5), stubAddress);
        if (!Patch(process, OnlineRoutePreconstructHook, ExpectedOnlineRoutePreconstructBytes,
                routePreconstructEntry) ||
            !Patch(process, OnlineRoutePreconstructAlternateHook,
                ExpectedOnlineRoutePreconstructAlternateBytes, routePreconstructAlternateEntry) ||
            !Patch(process, OnlineSceneConstructedHook, ExpectedOnlineSceneConstructedBytes,
                sceneConstructedEntry) ||
            !Patch(process, OnlineSceneConstructedAlternateHook,
                ExpectedOnlineSceneConstructedAlternateBytes, sceneConstructedAlternateEntry) ||
            !Patch(process, OnlineBossSentinelHook, ExpectedOnlineBossSentinelBytes,
                bossSentinelEntry) ||
            !Patch(process, OnlineBossRestoreHook, ExpectedOnlineBossRestoreBytes,
                bossRestoreEntry) ||
            !Patch(process, OnlineRouteCaptureHook, ExpectedOnlineRouteCaptureBytes,
                routeCaptureEntry) ||
            !WriteProcessMemory(process, new IntPtr(MapLoaderHook), entry, entry.Length, out written) ||
            !FlushInstructionCache(process, new IntPtr(MapLoaderHook), (UIntPtr)entry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineRoutePreconstructHook),
                (UIntPtr)routePreconstructEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineRoutePreconstructAlternateHook),
                (UIntPtr)routePreconstructAlternateEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineSceneConstructedHook),
                (UIntPtr)sceneConstructedEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineSceneConstructedAlternateHook),
                (UIntPtr)sceneConstructedAlternateEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineBossSentinelHook),
                (UIntPtr)bossSentinelEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineBossRestoreHook),
                (UIntPtr)bossRestoreEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineRouteCaptureHook),
                (UIntPtr)routeCaptureEntry.Length))
        {
            WriteLine("安装动态副本首图补丁失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        DungeonMapOverrideState = stateAddress;
        WriteLine("已安装动态副本首图补丁（本地路线捕获 + 地图加载器入口）: capture=0x" +
            OnlineRouteCaptureHook.ToString("X8") + ", loader=0x" +
            MapLoaderHook.ToString("X8") + ", table=" + mappings.Count +
            ", stub=0x" + stubAddress.ToString("X8") + ", state=0x" + stateAddress.ToString("X8"));
        return true;
    }

    private static byte[] BuildDungeonMapLoaderOverrideStub(uint stubAddress, uint mapTableAddress,
        uint stateAddress, uint onlineRoomStateAddress)
    {
        // 进入 0x00A727C0 时（原入口尚未执行，ebp 仍是调用方栈帧）：
        //   [esp+0] = 返回地址，[esp+4] = mapId，[ebp+8] = 调用方参数1。
        // 离线 fallback 调用时 [ebp+8] 即选中副本 id（map-loader-trace 实测
        // dungeon=40 -> 0x28）。只处理 ret==0x00A59D81 且 mapId==1001 的调用。
        var code = new List<byte>();
        code.Add(0x9C);                                                  // pushfd
        code.AddRange(new byte[] { 0x50, 0x51, 0x52, 0x53, 0x56, 0x57 });// push eax/ecx/edx/ebx/esi/edi
        code.AddRange(new byte[] { 0x81, 0x7C, 0x24, 0x1C });            // cmp dword [esp+1Ch],ret
        AddUInt32(code, OfflineFallbackLoaderReturn);
        code.Add(0x75); var notFallbackJump = code.Count; code.Add(0);   // jne done
        code.AddRange(new byte[] { 0x81, 0x7C, 0x24, 0x20 });            // cmp dword [esp+20h],1001
        AddUInt32(code, DefaultOfflineFallbackMap);
        code.Add(0x75); var notDefaultMapJump = code.Count; code.Add(0); // jne done

        code.AddRange(new byte[] { 0x8B, 0x45, 0x08 });                  // mov eax,[ebp+8] (dungeon id)
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 4));      // mov [state+4],eax (lastDungeonId)

        code.Add(0x8B); code.Add(0x0D); AddUInt32(code, mapTableAddress);// mov ecx,[tableCount]
        code.AddRange(new byte[] { 0x85, 0xC9, 0x74 }); var emptyTableJump = code.Count; code.Add(0); // test ecx,ecx; jz done
        code.Add(0xBA); AddUInt32(code, checked(mapTableAddress + sizeof(uint))); // mov edx,records

        var lookupLoop = code.Count;
        code.AddRange(new byte[] { 0x39, 0x02, 0x74 }); var foundJump = code.Count; code.Add(0); // cmp [edx],eax; je found
        code.AddRange(new byte[] { 0x83, 0xC2, OfflineDungeonMapRecordSize, 0x49, 0x75 }); var loopJump = code.Count; code.Add(0); // add edx,recordSize; dec ecx; jnz loop
        code.Add(0xEB); var notFoundJump = code.Count; code.Add(0);      // jmp done

        var found = code.Count;
        code.AddRange(new byte[] { 0x8B, 0x42, 0x04 });                  // mov eax,[edx+4] (correct map id)
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 8));      // mov [state+8],eax (lastMapId)
        code.AddRange(new byte[] { 0x8B, 0x4A, 0x08 });                  // mov ecx,[edx+8] (runtime start room x|y<<8)
        code.Add(0x89); code.Add(0x0D); AddUInt32(code, checked(stateAddress + 12)); // mov [state+12],ecx
        code.AddRange(new byte[] { 0x8B, 0x4A, 0x0C });                 // mov ecx,[edx+12] (maze width)
        code.Add(0x89); code.Add(0x0D); AddUInt32(code, checked(stateAddress + 16)); // mov [state+16],ecx
        code.AddRange(new byte[] { 0x89, 0x44, 0x24, 0x20 });            // mov [esp+20h],eax (覆盖 loader 参数)
        code.Add(0x8B); code.Add(0x0D); AddUInt32(code, MapSlotPointerGlobal); // mov ecx,[0ED041Ch]
        code.AddRange(new byte[] { 0x85, 0xC9, 0x74 }); var slotJump = code.Count; code.Add(0); // test ecx,ecx; jz slotDone
        code.AddRange(new byte[] { 0x89, 0x01 });                        // mov [ecx],eax (写槽位，供后续 0x00486D10 读取)
        var slotDone = code.Count;
        code[slotJump] = checked((byte)(slotDone - (slotJump + 1)));
        // Redirect only the recognized dungeon fallback return. The post-loader
        // stub arms the stable logical-slot -> physical-slot-0 mapping; it never
        // copies or constructs a room object.
        code.Add(0xB8); var postReturnImmediate = code.Count; AddUInt32(code, 0); // mov eax,postLoader
        code.AddRange(new byte[] { 0x89, 0x44, 0x24, 0x1C });             // mov [esp+1Ch],eax
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, stateAddress); // inc dword [state] (fireCount)

        var done = code.Count;
        code[notFallbackJump] = checked((byte)(done - (notFallbackJump + 1)));
        code[notDefaultMapJump] = checked((byte)(done - (notDefaultMapJump + 1)));
        code[emptyTableJump] = checked((byte)(done - (emptyTableJump + 1)));
        code[foundJump] = checked((byte)(found - (foundJump + 1)));
        code[loopJump] = unchecked((byte)(lookupLoop - (loopJump + 1))); // 向后跳转为负偏移
        code[notFoundJump] = checked((byte)(done - (notFoundJump + 1)));

        code.AddRange(new byte[] { 0x5F, 0x5E, 0x5B, 0x5A, 0x59, 0x58, 0x9D }); // pop edi/esi/ebx/edx/ecx/eax/popfd
        code.AddRange(new byte[] { 0x55, 0x8B, 0xEC, 0x51, 0x56 });       // 重放被覆盖的原始入口
        code.Add(0xE9);
        var resumeJumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(MapLoaderResume - resumeJumpNextInstruction)));

        var postLoader = code.Count;
        var postReturnBytes = BitConverter.GetBytes(checked(stubAddress + (uint)postLoader));
        for (var index = 0; index < postReturnBytes.Length; index++)
            code[postReturnImmediate + index] = postReturnBytes[index];

        // The loader has completed the only live room object in physical slot 0.
        // Publish the PVF coordinates and the client's scaled logical lookup
        // index, then let the shared lookup/coordinate guards keep them paired.
        // No bytes inside the 0x28-byte room object are copied or synthesized.
        code.Add(0x9C);                                                    // pushfd
        code.Add(0x60);                                                    // pushad
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 12));       // mov eax,[state+12] (x|y<<8)
        code.Add(0xA3); AddUInt32(code, checked(onlineRoomStateAddress + 4));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC8 });                    // movzx ecx,al (x)
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 });  // shr eax,8; movzx eax,al (y)
        code.AddRange(new byte[] { 0x0F, 0xAF, 0x05 }); AddUInt32(code, checked(stateAddress + 16)); // imul eax,[state+16]
        code.AddRange(new byte[] { 0x01, 0xC8, 0x8D, 0x04, 0x80 });       // add eax,ecx; lea eax,[eax+eax*4]
        code.Add(0xA3); AddUInt32(code, checked(onlineRoomStateAddress + 8)); // scaled logical slot
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code,
            checked(onlineRoomStateAddress + 0x10));
        AddUInt32(code, 1);                                                // pending if table is not ready
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code,
            checked(onlineRoomStateAddress + 0x20));
        AddUInt32(code, 2);                                                // source: local fallback
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, onlineRoomStateAddress);
        code.Add(0x8B); code.Add(0x15); AddUInt32(code, MapSlotPointerGlobal); // mov edx,[ED041C]
        code.AddRange(new byte[] { 0x85, 0xD2, 0x74 }); var noRoomTableJump = code.Count; code.Add(0); // test edx,edx; jz postDone
        code.AddRange(new byte[] { 0x89, 0x15 }); AddUInt32(code,
            checked(onlineRoomStateAddress + 0x0C));                       // remember physical table
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code,
            checked(onlineRoomStateAddress + 0x10));
        AddUInt32(code, 2);                                                // remap active
        var postDone = code.Count;
        code[noRoomTableJump] = checked((byte)(postDone - (noRoomTableJump + 1)));

        // Publish the fallback coordinate before the loader returns. The
        // client's door/Boss classification runs immediately afterwards; the
        // external monitor used to repair this too late, after the first-room
        // door type had already been cached from the default (0,0) state.
        code.Add(0x8B); code.Add(0x15); AddUInt32(code,
            checked(onlineRoomStateAddress + 0x14));                       // client dungeon state
        code.AddRange(new byte[] { 0x81, 0xFA, 0x00, 0x00, 0x01, 0x00 });  // cmp edx,10000h
        code.Add(0x72); var invalidClientStateJump = code.Count; code.Add(0); // jb postCoordinatesDone
        code.Add(0xA1); AddUInt32(code, checked(onlineRoomStateAddress + 4));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC8 });                    // movzx ecx,al (x)
        code.AddRange(new byte[] { 0x89, 0x8A, 0xB8, 0x00, 0x00, 0x00 });  // mov [edx+B8h],ecx
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 });  // y
        code.AddRange(new byte[] { 0x89, 0x82, 0xBC, 0x00, 0x00, 0x00 });  // mov [edx+BCh],eax
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code,
            checked(onlineRoomStateAddress + 0x1C));                       // coordinate write count
        var postCoordinatesDone = code.Count;
        code[invalidClientStateJump] = checked((byte)(postCoordinatesDone -
            (invalidClientStateJump + 1)));
        code.Add(0x61);                                                    // popad
        code.Add(0x9D);                                                    // popfd
        code.Add(0xE9);
        var postResumeNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineFallbackLoaderReturn - postResumeNextInstruction)));

        return code.ToArray();
    }

    private static bool VerifyPatchSite(IntPtr process, uint address, byte[] expected)
    {
        var actual = new byte[expected.Length];
        return ReadProcessMemory(process, new IntPtr(address), actual, actual.Length, out _) &&
            actual.SequenceEqual(expected);
    }

    private static byte[] CreateJumpEntry(int length, uint hookAddress, uint targetAddress)
    {
        var entry = Enumerable.Repeat((byte)0x90, length).ToArray();
        entry[0] = 0xE9;
        WriteRelative(entry, 1, checked(hookAddress + 5), targetAddress);
        return entry;
    }

    private static void AddOnlineRouteMappingLookup(List<byte> code, uint stateAddress,
        uint mapTableAddress, List<int> finishJumps, out int foundOffset)
    {
        // The table is injected next to the state block and has an explicit
        // count. Do not trust any client pointer while the dungeon state is
        // being rebuilt.
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 4)); // selected dungeon
        code.Add(0x8B); code.Add(0x0D); AddUInt32(code, mapTableAddress); // count
        code.AddRange(new byte[] { 0x85, 0xC9, 0x0F, 0x84 });
        finishJumps.Add(code.Count); AddInt32(code, 0); // table empty
        code.Add(0xBA); AddUInt32(code, checked(mapTableAddress + sizeof(uint))); // records
        var loopOffset = code.Count;
        code.AddRange(new byte[] { 0x39, 0x02, 0x0F, 0x84 }); // cmp [edx],eax; je found
        var foundJump = code.Count; AddInt32(code, 0);
        code.AddRange(new byte[] { 0x83, 0xC2, OfflineDungeonMapRecordSize, 0x49, 0x0F, 0x85 });
        var loopJump = code.Count; AddInt32(code, 0);
        code.Add(0xE9); finishJumps.Add(code.Count); AddInt32(code, 0); // missing mapping
        foundOffset = code.Count;
        PatchRelativeJump(code, foundJump, foundOffset, 4);
        PatchRelativeJump(code, loopJump, loopOffset, 4);
    }

    private static void PatchRelativeJump(List<byte> code, int displacementOffset,
        int destinationOffset, int displacementSize)
    {
        var relative = BitConverter.GetBytes(destinationOffset - (displacementOffset + displacementSize));
        for (var index = 0; index < relative.Length; index++)
            code[displacementOffset + index] = relative[index];
    }

    private static void PatchFinishJumps(List<byte> code, uint stubAddress,
        IEnumerable<int> displacementOffsets, int finishOffset)
    {
        foreach (var displacementOffset in displacementOffsets)
        {
            var sourceAfter = checked(stubAddress + (uint)displacementOffset + sizeof(int));
            AddOrReplaceInt32(code, displacementOffset,
                unchecked((int)(checked(stubAddress + (uint)finishOffset) - sourceAfter)));
        }
    }

    private static void AddOrReplaceInt32(List<byte> code, int offset, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var index = 0; index < bytes.Length; index++) code[offset + index] = bytes[index];
    }

    private static void AddEncryptedStateSetter(List<byte> code, uint stubAddress,
        uint valueAddress, uint wrapperAddress)
    {
        code.Add(0x68); AddUInt32(code, valueAddress); // push &value
        code.Add(0xB9); AddUInt32(code, wrapperAddress); // mov ecx, wrapper
        AddRelativeCall(code, stubAddress, DungeonStateSetter);
    }

    private static byte[] BuildOnlineRoutePreconstructStub(uint stubAddress, uint stateAddress,
        uint mapTableAddress, byte[] displacedInstruction, uint resumeAddress)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();

        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 24));
        code.Add(1); // cmp [preconstruct armed], 1
        code.AddRange(new byte[] { 0x0F, 0x85 }); finishJumps.Add(code.Count); AddInt32(code, 0);

        AddOnlineRouteMappingLookup(code, stateAddress, mapTableAddress, finishJumps, out _);
        // EDX points to the selected mapping record here.
        code.AddRange(new byte[] { 0x8B, 0x42, 0x04 });
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 28)); // start map
        code.AddRange(new byte[] { 0x8B, 0x42, 0x14 });
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 32)); // start greed
        code.AddRange(new byte[] { 0x8B, 0x42, 0x10 });
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 36)); // actual Boss room
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 68));
        AddUInt32(code, 0); // post-construction scene topology writer is not armed yet

        // The same hook can construct unrelated scenes while town state is
        // changing.  Consume the one-shot only for the selected start map.
        code.AddRange(new byte[] { 0x8B, 0x03 }); // room-cell map ID
        code.AddRange(new byte[] { 0x3B, 0x05 }); AddUInt32(code, checked(stateAddress + 28));
        code.AddRange(new byte[] { 0x0F, 0x85 }); finishJumps.Add(code.Count); AddInt32(code, 0);

        // Build the logical cell the same way the native room lookup does.
        // The fallback object itself remains in physical cell 0, but the
        // minimap's adjacency pass reads map IDs from the logical cell grid.
        // Publishing only this 4-byte map ID is safe: no room object, state,
        // or pointer payload is copied to the target cell.
        code.AddRange(new byte[] { 0x8B, 0x42, 0x08 }); // packed start x|y<<8
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC8 }); // ecx = x
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 }); // eax = y
        code.AddRange(new byte[] { 0x0F, 0xAF, 0x42, 0x0C, 0x01, 0xC8 }); // y*width+x
        code.AddRange(new byte[] { 0x83, 0xF8, 0x0F });
        code.AddRange(new byte[] { 0x0F, 0x87 }); finishJumps.Add(code.Count); AddInt32(code, 0);
        code.AddRange(new byte[] { 0x6B, 0xC0, 0x28 }); // cell byte offset
        code.AddRange(new byte[] { 0x8B, 0x0D }); AddUInt32(code, MapSlotPointerGlobal);
        code.AddRange(new byte[] { 0x81, 0xF9 }); AddUInt32(code, 0x00010000);
        code.AddRange(new byte[] { 0x0F, 0x82 }); finishJumps.Add(code.Count); AddInt32(code, 0);
        code.AddRange(new byte[] { 0x01, 0xC1 }); // ecx = logical cell
        code.AddRange(new byte[] { 0x8B, 0x42, 0x04 }); // map id
        code.AddRange(new byte[] { 0x89, 0x01 }); // logical cell map id = start map
        code.AddRange(new byte[] { 0x89, 0x0D }); AddUInt32(code, checked(stateAddress + 88));
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 92));

        // pushad saved EAX at [esp+1Ch].  Replacing that saved value makes the
        // displaced `push eax` send the PVF greed mask into 0x0048E610.
        code.AddRange(new byte[] { 0x8B, 0x44, 0x24, 0x1C });
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 44));
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 32));
        code.AddRange(new byte[] { 0x89, 0x44, 0x24, 0x1C });
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 40));
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 68));
        AddUInt32(code, 1); // arm the matching post-construction scene hook
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 24));
        AddUInt32(code, 0); // one selected start scene only

        var finishOffset = code.Count;
        PatchFinishJumps(code, stubAddress, finishJumps, finishOffset);
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(displacedInstruction);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(resumeAddress - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOnlineSceneConstructedStub(uint stubAddress, uint stateAddress,
        uint mapTableAddress, byte[] displacedInstruction, uint resumeAddress, bool usesEsiIndex)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();

        // Both constructor branches pass the completed scene object in EAX.
        // The native constructor has already consumed the substituted greed
        // argument; synchronizing its cached mask here also updates the minimap
        // topology without moving the room-cell object out of physical slot 0.
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D });
        AddUInt32(code, checked(stateAddress + 24));
        code.Add(0); // route preconstruction must have consumed the one-shot
        code.AddRange(new byte[] { 0x0F, 0x85 }); finishJumps.Add(code.Count); AddInt32(code, 0);
        code.AddRange(new byte[] { 0x83, 0x3D });
        AddUInt32(code, checked(stateAddress + 68));
        code.Add(1); // only after a selected route was actually constructed
        // scene-door-arm is set to 1 by the pre-construction hook.  The old
        // JE skipped this block precisely when that arm was valid, leaving
        // the scene's door/minimap cache at the fallback topology.
        code.AddRange(new byte[] { 0x0F, 0x85 }); finishJumps.Add(code.Count); AddInt32(code, 0);
        code.AddRange(new byte[] { 0x89, 0xC7 }); // edi = scene (incoming eax)
        code.AddRange(new byte[] { 0x81, 0xFF }); AddUInt32(code, 0x00010000);
        code.AddRange(new byte[] { 0x0F, 0x82 }); finishJumps.Add(code.Count); AddInt32(code, 0);
        // Preserve the one live physical first-room scene for the external
        // monitor. It may be registered by native code at slot zero again
        // after this constructor path returns; the monitor can then restore
        // only its logical table entry without copying the C++ object.
        code.AddRange(new byte[] { 0x89, 0x3D }); AddUInt32(code, checked(stateAddress + 96));
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 100));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0x87, 0x38, 0x02, 0x00, 0x00 });
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 72)); // cached mask before
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 32)); // PVF greed
        code.AddRange(new byte[] { 0x88, 0x87, 0x38, 0x02, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 76));

        // The two original instructions below commit EAX to the native scene
        // table at the current byte offset. In the fallback path that index is always
        // physical slot zero, so the minimap sees a disconnected room even
        // after its greed mask is correct. Redirect the original index to the
        // selected PVF start slot. This changes only the native registration
        // index; it neither moves nor duplicates the C++ scene/room objects.
        AddOnlineRouteMappingLookup(code, stateAddress, mapTableAddress, finishJumps, out _);
        code.AddRange(new byte[] { 0x8B, 0x42, 0x08 }); // packed PVF start x|y<<8
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC8 }); // ecx = start x
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 }); // eax = start y
        code.AddRange(new byte[] { 0x0F, 0xAF, 0x42, 0x0C }); // eax *= width
        code.AddRange(new byte[] { 0x01, 0xC8 }); // eax = logical scene index
        // The supplied client always has a 4x4 scene table. Do not redirect
        // unsupported layouts instead of risking an out-of-range native write.
        code.AddRange(new byte[] { 0x83, 0xF8, 0x0F });
        code.AddRange(new byte[] { 0x0F, 0x87 }); finishJumps.Add(code.Count); AddInt32(code, 0);
        code.AddRange(new byte[] { 0x89, 0x05 }); AddUInt32(code, checked(stateAddress + 80));
        code.AddRange(new byte[] { 0xC1, 0xE0, 0x02 }); // scene index -> byte offset
        // EAX is the completed scene pointer and must remain untouched. The
        // first constructor branch stores through ESI; the alternate branch
        // stores through EDX, so replace only its saved index register.
        code.AddRange(usesEsiIndex
            ? new byte[] { 0x89, 0x44, 0x24, 0x04 } // saved ESI = logical offset
            : new byte[] { 0x89, 0x44, 0x24, 0x14 }); // saved EDX = logical offset
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 84));
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 68));
        AddUInt32(code, 0); // one physical first-room scene only

        var finishOffset = code.Count;
        PatchFinishJumps(code, stubAddress, finishJumps, finishOffset);
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(displacedInstruction);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(resumeAddress - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOnlineRouteCaptureStub(uint stubAddress, uint stateAddress,
        uint mapTableAddress)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();

        // This executes in the client's own town-to-dungeon transition.  Its
        // caller still owns the selected dungeon in [ebp+8], but it runs before
        // ARAD clears DUNGEON_INFO and before it constructs the fallback scene.
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x8B, 0x55, 0x08 }); // mov edx,[ebp+8]
        code.Add(0x89); code.Add(0x15); AddUInt32(code, checked(stateAddress + 4));

        // Mark a new route generation before any constructor work begins. The
        // external monitor snapshots its stage counters on this exact marker,
        // so stale completion counters from the preceding dungeon cannot arm a
        // write while this new scene is still under construction.
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 104));

        // A cancelled selection or an unsupported dungeon must never carry the
        // previous dungeon's one-shot into a later scene construction.
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 24));
        AddUInt32(code, 0);
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 48));
        AddUInt32(code, 0);

        code.Add(0x8B); code.Add(0x0D); AddUInt32(code, mapTableAddress); // count
        code.AddRange(new byte[] { 0x85, 0xC9, 0x0F, 0x84 });
        finishJumps.Add(code.Count); AddInt32(code, 0);
        code.Add(0xB8); AddUInt32(code, checked(mapTableAddress + sizeof(uint))); // records
        var loopOffset = code.Count;
        code.AddRange(new byte[] { 0x39, 0x10, 0x0F, 0x84 }); // cmp [eax],edx
        var foundJump = code.Count; AddInt32(code, 0);
        code.AddRange(new byte[] { 0x83, 0xC0, OfflineDungeonMapRecordSize, 0x49, 0x0F, 0x85 });
        var loopJump = code.Count; AddInt32(code, 0);
        code.Add(0xE9); finishJumps.Add(code.Count); AddInt32(code, 0);

        var foundOffset = code.Count;
        // Clear stale restore state, then arm the route.  The later sentinel
        // hook consumes this arm to hide the real Boss only while the first
        // physical-slot-zero scene is being constructed.
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 24));
        AddUInt32(code, 1);
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 64));

        var finishOffset = code.Count;
        PatchFinishJumps(code, stubAddress, finishJumps, finishOffset);
        PatchRelativeJump(code, foundJump, foundOffset, 4);
        PatchRelativeJump(code, loopJump, loopOffset, 4);
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(ExpectedOnlineRouteCaptureBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(OnlineRouteCaptureResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOnlineBossSentinelStub(uint stubAddress, uint stateAddress,
        uint mapTableAddress)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 24));
        code.Add(1);
        code.AddRange(new byte[] { 0x0F, 0x85 }); finishJumps.Add(code.Count); AddInt32(code, 0);

        AddOnlineRouteMappingLookup(code, stateAddress, mapTableAddress, finishJumps, out _);
        code.AddRange(new byte[] { 0x8B, 0x42, 0x10 }); // packed actual Boss
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 36));

        // Physical slot zero must not be classified as a Boss scene while its
        // logical start coordinate is elsewhere.  Use ARAD's encrypted setters
        // rather than writing backing memory directly.
        code.Add(0xB8); AddUInt32(code, 0xFF);
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 56));
        AddEncryptedStateSetter(code, stubAddress, checked(stateAddress + 56), EncryptedBossXWrapper);
        code.Add(0xB8); AddUInt32(code, 0xFF);
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 60));
        AddEncryptedStateSetter(code, stubAddress, checked(stateAddress + 60), EncryptedBossYWrapper);
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 48));
        AddUInt32(code, 1); // restore the actual Boss after native construction

        var finishOffset = code.Count;
        PatchFinishJumps(code, stubAddress, finishJumps, finishOffset);
        code.Add(0x61);
        code.Add(0x9D);
        code.AddRange(ExpectedOnlineBossSentinelBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(OnlineBossSentinelResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOnlineBossRestoreStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 48));
        code.Add(1);
        code.Add(0x75); var finishJump = code.Count; code.Add(0);
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 48));
        AddUInt32(code, 0);

        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 36));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC0 });
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 56));
        AddEncryptedStateSetter(code, stubAddress, checked(stateAddress + 56), EncryptedBossXWrapper);
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 36));
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 });
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 60));
        AddEncryptedStateSetter(code, stubAddress, checked(stateAddress + 60), EncryptedBossYWrapper);
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 52));

        var finishOffset = code.Count;
        code[finishJump] = checked((byte)(finishOffset - (finishJump + 1)));
        code.Add(0x61);
        code.Add(0x9D);
        code.AddRange(ExpectedOnlineBossRestoreBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(OnlineBossRestoreResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static bool InstallOnlineRoomCoordinateFix(IntPtr process)
    {
        var captureBytes = new byte[ExpectedOnlineRoomCaptureBytes.Length];
        var stateCaptureBytes = new byte[ExpectedOnlineStateCaptureBytes.Length];
        var lookupBytes = new byte[ExpectedRoomLookupBytes.Length];
        var coordinateBytes = new byte[ExpectedOnlineCoordinateUseBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OnlineRoomCaptureHook), captureBytes,
                captureBytes.Length, out _) ||
            !captureBytes.SequenceEqual(ExpectedOnlineRoomCaptureBytes) ||
            !ReadProcessMemory(process, new IntPtr(OnlineStateCaptureHook), stateCaptureBytes,
                stateCaptureBytes.Length, out _) ||
            !stateCaptureBytes.SequenceEqual(ExpectedOnlineStateCaptureBytes) ||
            !ReadProcessMemory(process, new IntPtr(RoomLookupHook), lookupBytes,
                lookupBytes.Length, out _) ||
            !lookupBytes.SequenceEqual(ExpectedRoomLookupBytes) ||
            !ReadProcessMemory(process, new IntPtr(OnlineCoordinateUseHook), coordinateBytes,
                coordinateBytes.Length, out _) ||
            !coordinateBytes.SequenceEqual(ExpectedOnlineCoordinateUseBytes))
        {
            WriteLine("联机房间坐标补丁的客户端字节不匹配；仅支持当前 ARAD.exe 版本");
            return false;
        }

        var allocation = VirtualAllocEx(process, IntPtr.Zero, OnlineRoomPatchSize,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (allocation == IntPtr.Zero || allocation.ToInt64() < 0 || allocation.ToInt64() > uint.MaxValue)
        {
            WriteLine("联机房间坐标补丁 VirtualAllocEx 失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        var captureStubAddress = checked((uint)allocation.ToInt64());
        var stateCaptureStubAddress = checked(captureStubAddress + OnlineStateCaptureStubOffset);
        var lookupStubAddress = checked(captureStubAddress + OnlineRoomLookupStubOffset);
        var coordinateStubAddress = checked(captureStubAddress + OnlineCoordinateUseStubOffset);
        var stateAddress = checked(captureStubAddress + OnlineRoomStateOffset);
        var captureStub = BuildOnlineRoomCaptureStub(captureStubAddress, stateAddress);
        var stateCaptureStub = BuildOnlineStateCaptureStub(stateCaptureStubAddress, stateAddress);
        var lookupStub = BuildOnlineRoomLookupStub(lookupStubAddress, stateAddress);
        var coordinateStub = BuildOnlineCoordinateUseStub(coordinateStubAddress, stateAddress);
        if (captureStub.Length > OnlineStateCaptureStubOffset ||
            stateCaptureStub.Length > OnlineRoomLookupStubOffset - OnlineStateCaptureStubOffset ||
            lookupStub.Length > OnlineCoordinateUseStubOffset - OnlineRoomLookupStubOffset ||
            coordinateStub.Length > OnlineRoomStateOffset - OnlineCoordinateUseStubOffset)
        {
            WriteLine("联机房间坐标补丁桩超出预留空间");
            return false;
        }

        var payload = new byte[OnlineRoomPatchSize];
        Buffer.BlockCopy(captureStub, 0, payload, 0, captureStub.Length);
        Buffer.BlockCopy(stateCaptureStub, 0, payload, OnlineStateCaptureStubOffset,
            stateCaptureStub.Length);
        Buffer.BlockCopy(lookupStub, 0, payload, OnlineRoomLookupStubOffset, lookupStub.Length);
        Buffer.BlockCopy(coordinateStub, 0, payload, OnlineCoordinateUseStubOffset,
            coordinateStub.Length);
        if (!WriteProcessMemory(process, allocation, payload, payload.Length, out _) ||
            !FlushInstructionCache(process, allocation, (UIntPtr)payload.Length))
        {
            WriteLine("写入联机房间坐标补丁桩失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        var captureEntry = Enumerable.Repeat((byte)0x90, ExpectedOnlineRoomCaptureBytes.Length).ToArray();
        captureEntry[0] = 0xE9;
        WriteRelative(captureEntry, 1, checked(OnlineRoomCaptureHook + 5), captureStubAddress);
        var stateCaptureEntry = Enumerable.Repeat((byte)0x90,
            ExpectedOnlineStateCaptureBytes.Length).ToArray();
        stateCaptureEntry[0] = 0xE9;
        WriteRelative(stateCaptureEntry, 1, checked(OnlineStateCaptureHook + 5),
            stateCaptureStubAddress);
        var lookupEntry = new byte[ExpectedRoomLookupBytes.Length];
        lookupEntry[0] = 0xE9;
        WriteRelative(lookupEntry, 1, checked(RoomLookupHook + 5), lookupStubAddress);
        var coordinateEntry = Enumerable.Repeat((byte)0x90, ExpectedOnlineCoordinateUseBytes.Length).ToArray();
        coordinateEntry[0] = 0xE9;
        WriteRelative(coordinateEntry, 1, checked(OnlineCoordinateUseHook + 5), coordinateStubAddress);

        var ok = Patch(process, OnlineRoomCaptureHook,
                ExpectedOnlineRoomCaptureBytes, captureEntry) &&
            Patch(process, OnlineStateCaptureHook,
                ExpectedOnlineStateCaptureBytes, stateCaptureEntry) &&
            Patch(process, RoomLookupHook, ExpectedRoomLookupBytes, lookupEntry) &&
            Patch(process, OnlineCoordinateUseHook,
                ExpectedOnlineCoordinateUseBytes, coordinateEntry);
        if (!ok ||
            !FlushInstructionCache(process, new IntPtr(OnlineRoomCaptureHook),
                (UIntPtr)captureEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineStateCaptureHook),
                (UIntPtr)stateCaptureEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(RoomLookupHook),
                (UIntPtr)lookupEntry.Length) ||
            !FlushInstructionCache(process, new IntPtr(OnlineCoordinateUseHook),
                (UIntPtr)coordinateEntry.Length))
        {
            WriteLine("安装联机房间坐标入口补丁失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        OnlineRoomPatchState = stateAddress;
        WriteLine("联机房间坐标补丁已安装: START_MAP=0x" +
            OnlineRoomCaptureHook.ToString("X8") + ", state-capture=0x" +
            OnlineStateCaptureHook.ToString("X8") + ", lookup=0x" +
            RoomLookupHook.ToString("X8") + ", coordinate-use=0x" +
            OnlineCoordinateUseHook.ToString("X8") + ", state=0x" +
            stateAddress.ToString("X8") + "（全部副本动态生效）");
        return true;
    }

    private static byte[] BuildOnlineRoomCaptureStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x0F, 0xB6, 0x85, 0x1A, 0xDF, 0xFF, 0xFF }); // x
        code.AddRange(new byte[] { 0x0F, 0xB6, 0x8D, 0x1B, 0xDF, 0xFF, 0xFF }); // y
        code.AddRange(new byte[] { 0xC1, 0xE1, 0x08, 0x09, 0xC8 }); // y<<8 | x
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 4)); // packed room
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 0x10));
        AddUInt32(code, 3); // native START_MAP transition; never remap to slot 0
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 0x20));
        AddUInt32(code, 1); // source: network START_MAP
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, stateAddress); // event count
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(ExpectedOnlineRoomCaptureBytes);
        code.Add(0xE9);
        var nextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(OnlineRoomCaptureResume - nextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOnlineStateCaptureStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        code.AddRange(ExpectedOnlineStateCaptureBytes); // original mov [edi+0B4h],esi
        code.AddRange(new byte[] { 0x89, 0x3D });
        AddUInt32(code, checked(stateAddress + 0x14)); // active dungeon-state pointer
        code.AddRange(new byte[] { 0xFF, 0x05 });
        AddUInt32(code, checked(stateAddress + 0x24)); // capture count
        code.Add(0xE9);
        var nextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(OnlineStateCaptureResume - nextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOnlineRoomLookupStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        var originalJumps = new List<int>();
        var activeJump = 0;
        var fallbackJump = 0;
        var remappedJumps = new List<int>();

        void AddShortJump(List<int> offsets, byte opcode)
        {
            code.Add(opcode);
            offsets.Add(code.Count);
            code.Add(0);
        }

        code.Add(0x9C); // pushfd
        code.Add(0xA1); AddUInt32(code, MapSlotPointerGlobal); // physical table base

        // A network START_MAP owns a real logical room cell. Record the lookup
        // for diagnostics/coordinate repair, then execute the client's original
        // base+logical-index calculation without redirecting it to slot 0.
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 0x10));
        code.Add(3);
        code.Add(0x75); fallbackJump = code.Count; code.Add(0); // jne fallback remap modes
        code.AddRange(new byte[] { 0x89, 0x15 }); AddUInt32(code, checked(stateAddress + 8));
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 0x0C));
        AddShortJump(originalJumps, 0xEB);

        var fallbackOffset = code.Count;
        code[fallbackJump] = checked((byte)(fallbackOffset - (fallbackJump + 1)));
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 0x10));
        code.Add(1); // initial fallback lookup is pending
        code.Add(0x75); activeJump = code.Count; code.Add(0); // jne check active mapping
        code.AddRange(new byte[] { 0x85, 0xC0 });
        AddShortJump(originalJumps, 0x74); // table not ready; leave pending armed
        code.AddRange(new byte[] { 0x89, 0x15 }); AddUInt32(code, checked(stateAddress + 8));
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 0x0C));
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 0x10));
        AddUInt32(code, 2); // active remap
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 0x18));
        AddShortJump(remappedJumps, 0xEB);

        var activeOffset = code.Count;
        code[activeJump] = checked((byte)(activeOffset - (activeJump + 1)));
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 0x10));
        code.Add(2);
        AddShortJump(originalJumps, 0x75);
        code.AddRange(new byte[] { 0x3B, 0x05 }); AddUInt32(code, checked(stateAddress + 0x0C));
        AddShortJump(originalJumps, 0x75); // room table changed
        code.AddRange(new byte[] { 0x3B, 0x15 }); AddUInt32(code, checked(stateAddress + 8));
        AddShortJump(originalJumps, 0x75); // a different logical room
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 0x18));
        AddShortJump(remappedJumps, 0xEB);

        var originalOffset = code.Count;
        code.AddRange(new byte[] { 0x8D, 0x04, 0xD0 }); // original base+logical-index*8
        var doneOffset = code.Count;
        foreach (var displacement in originalJumps)
            code[displacement] = checked((byte)(originalOffset - (displacement + 1)));
        foreach (var displacement in remappedJumps)
            code[displacement] = checked((byte)(doneOffset - (displacement + 1)));
        code.Add(0x9D); // popfd
        code.Add(0xE9);
        var nextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(RoomLookupResume - nextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOnlineCoordinateUseStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();

        void AddFinishJump(byte condition)
        {
            code.Add(condition);
            finishJumps.Add(code.Count);
            code.Add(0);
        }

        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 0x10));
        code.Add(2);
        AddFinishJump(0x75);
        code.Add(0xA1); AddUInt32(code, MapSlotPointerGlobal);
        code.AddRange(new byte[] { 0x3B, 0x05 }); AddUInt32(code, checked(stateAddress + 0x0C));
        AddFinishJump(0x75);
        code.AddRange(new byte[] { 0x81, 0xFE, 0x00, 0x00, 0x01, 0x00 });
        AddFinishJump(0x72); // jb invalid ESI
        code.AddRange(new byte[] { 0x89, 0x35 }); AddUInt32(code, checked(stateAddress + 0x14));
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 4));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC8 });
        code.AddRange(new byte[] { 0x89, 0x8E, 0xB8, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 });
        code.AddRange(new byte[] { 0x89, 0x86, 0xBC, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 0x1C));

        var finishOffset = code.Count;
        foreach (var displacement in finishJumps)
            code[displacement] = checked((byte)(finishOffset - (displacement + 1)));
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(ExpectedOnlineCoordinateUseBytes);
        code.Add(0xE9);
        var nextInstruction = checked(stubAddress + (uint)code.Count + sizeof(int));
        AddInt32(code, unchecked((int)(OnlineCoordinateUseResume - nextInstruction)));
        return code.ToArray();
    }

    private static int VerifyOnlineRoomCoordinateFix()
    {
        const uint sampleBase = 0x10000000;
        const uint sampleState = sampleBase + OnlineRoomStateOffset;
        const uint sampleRouteState = sampleBase + 0xA00;
        const uint sampleRouteTable = sampleBase + 0xC00;
        var capture = BuildOnlineRoomCaptureStub(sampleBase, sampleState);
        var stateCapture = BuildOnlineStateCaptureStub(
            sampleBase + OnlineStateCaptureStubOffset, sampleState);
        var lookup = BuildOnlineRoomLookupStub(sampleBase + OnlineRoomLookupStubOffset, sampleState);
        var coordinate = BuildOnlineCoordinateUseStub(sampleBase + OnlineCoordinateUseStubOffset, sampleState);
        var route = BuildOnlineRoutePreconstructStub(sampleRouteState + OnlineRoutePreconstructStubOffset,
            sampleRouteState, sampleRouteTable, ExpectedOnlineRoutePreconstructBytes,
            OnlineRoutePreconstructResume);
        var alternateRoute = BuildOnlineRoutePreconstructStub(
            sampleRouteState + OnlineRoutePreconstructAlternateStubOffset, sampleRouteState,
            sampleRouteTable, ExpectedOnlineRoutePreconstructAlternateBytes,
            OnlineRoutePreconstructAlternateResume);
        var sceneConstructed = BuildOnlineSceneConstructedStub(
            sampleRouteState + OnlineSceneConstructedStubOffset, sampleRouteState,
            sampleRouteTable, ExpectedOnlineSceneConstructedBytes,
            OnlineSceneConstructedResume, true);
        var sceneConstructedAlternate = BuildOnlineSceneConstructedStub(
            sampleRouteState + OnlineSceneConstructedAlternateStubOffset, sampleRouteState,
            sampleRouteTable, ExpectedOnlineSceneConstructedAlternateBytes,
            OnlineSceneConstructedAlternateResume, false);
        var bossSentinel = BuildOnlineBossSentinelStub(sampleRouteState + OnlineBossSentinelStubOffset,
            sampleRouteState, sampleRouteTable);
        var bossRestore = BuildOnlineBossRestoreStub(sampleRouteState + OnlineBossRestoreStubOffset,
            sampleRouteState);
        var routeCapture = BuildOnlineRouteCaptureStub(sampleRouteState + OnlineRouteCaptureStubOffset,
            sampleRouteState, sampleRouteTable);
        var captureMode3 = new List<byte> { 0xC7, 0x05 };
        AddUInt32(captureMode3, sampleState + 0x10);
        AddUInt32(captureMode3, 3);
        var mode3Compare = new List<byte> { 0x83, 0x3D };
        AddUInt32(mode3Compare, sampleState + 0x10);
        mode3Compare.Add(3);
        var sceneDoorArmCompare = new List<byte> { 0x83, 0x3D };
        AddUInt32(sceneDoorArmCompare, sampleRouteState + 68);
        sceneDoorArmCompare.Add(1);
        sceneDoorArmCompare.AddRange(new byte[] { 0x0F, 0x85 });
        var sceneRegistrationEsi = new byte[] { 0x89, 0x44, 0x24, 0x04 };
        var sceneRegistrationEdx = new byte[] { 0x89, 0x44, 0x24, 0x14 };
        var logicalRoomCellStride = new byte[] { 0x6B, 0xC0, 0x28 };
        var logicalRoomCellPointer = BitConverter.GetBytes(MapSlotPointerGlobal);
        var valid = capture.Length <= OnlineStateCaptureStubOffset &&
            stateCapture.Length <= OnlineRoomLookupStubOffset - OnlineStateCaptureStubOffset &&
            lookup.Length <= OnlineCoordinateUseStubOffset - OnlineRoomLookupStubOffset &&
            coordinate.Length <= OnlineRoomStateOffset - OnlineCoordinateUseStubOffset &&
            ResolveTailJump(capture, sampleBase) == OnlineRoomCaptureResume &&
            ResolveTailJump(stateCapture, sampleBase + OnlineStateCaptureStubOffset) ==
                OnlineStateCaptureResume &&
            ResolveTailJump(lookup, sampleBase + OnlineRoomLookupStubOffset) == RoomLookupResume &&
            ResolveTailJump(coordinate, sampleBase + OnlineCoordinateUseStubOffset) ==
                OnlineCoordinateUseResume &&
            route.Length <= OnlineRoutePreconstructAlternateStubOffset -
                OnlineRoutePreconstructStubOffset &&
            alternateRoute.Length <= OnlineSceneConstructedStubOffset -
                OnlineRoutePreconstructAlternateStubOffset &&
            sceneConstructed.Length <= OnlineSceneConstructedAlternateStubOffset -
                OnlineSceneConstructedStubOffset &&
            sceneConstructedAlternate.Length <= OnlineBossSentinelStubOffset -
                OnlineSceneConstructedAlternateStubOffset &&
            bossSentinel.Length <= OnlineBossRestoreStubOffset - OnlineBossSentinelStubOffset &&
            bossRestore.Length <= OnlineRouteCaptureStubOffset - OnlineBossRestoreStubOffset &&
            routeCapture.Length <= OnlineRoutePatchAllocationTail - OnlineRouteCaptureStubOffset &&
            ResolveTailJump(route, sampleRouteState + OnlineRoutePreconstructStubOffset) ==
                OnlineRoutePreconstructResume &&
            ResolveTailJump(alternateRoute,
                sampleRouteState + OnlineRoutePreconstructAlternateStubOffset) ==
                OnlineRoutePreconstructAlternateResume &&
            ResolveTailJump(sceneConstructed,
                sampleRouteState + OnlineSceneConstructedStubOffset) ==
                OnlineSceneConstructedResume &&
            ResolveTailJump(sceneConstructedAlternate,
                sampleRouteState + OnlineSceneConstructedAlternateStubOffset) ==
                OnlineSceneConstructedAlternateResume &&
            ResolveTailJump(bossSentinel, sampleRouteState + OnlineBossSentinelStubOffset) ==
                OnlineBossSentinelResume &&
            ResolveTailJump(bossRestore, sampleRouteState + OnlineBossRestoreStubOffset) ==
                OnlineBossRestoreResume &&
            ResolveTailJump(routeCapture, sampleRouteState + OnlineRouteCaptureStubOffset) ==
                OnlineRouteCaptureResume &&
            route.AsSpan().IndexOf(new byte[] { 0x89, 0x44, 0x24, 0x1C }) >= 0 &&
            alternateRoute.AsSpan().IndexOf(new byte[] { 0x89, 0x44, 0x24, 0x1C }) >= 0 &&
            route.AsSpan().IndexOf(logicalRoomCellStride) >= 0 &&
            alternateRoute.AsSpan().IndexOf(logicalRoomCellStride) >= 0 &&
            route.AsSpan().IndexOf(logicalRoomCellPointer) >= 0 &&
            alternateRoute.AsSpan().IndexOf(logicalRoomCellPointer) >= 0 &&
            route.Skip(route.Length - (ExpectedOnlineRoutePreconstructBytes.Length + 5))
                .Take(ExpectedOnlineRoutePreconstructBytes.Length)
                .SequenceEqual(ExpectedOnlineRoutePreconstructBytes) &&
            alternateRoute.Skip(alternateRoute.Length -
                (ExpectedOnlineRoutePreconstructAlternateBytes.Length + 5))
                .Take(ExpectedOnlineRoutePreconstructAlternateBytes.Length)
                .SequenceEqual(ExpectedOnlineRoutePreconstructAlternateBytes) &&
            sceneConstructed.Skip(sceneConstructed.Length -
                (ExpectedOnlineSceneConstructedBytes.Length + 5))
                .Take(ExpectedOnlineSceneConstructedBytes.Length)
                .SequenceEqual(ExpectedOnlineSceneConstructedBytes) &&
            sceneConstructedAlternate.Skip(sceneConstructedAlternate.Length -
                (ExpectedOnlineSceneConstructedAlternateBytes.Length + 5))
                .Take(ExpectedOnlineSceneConstructedAlternateBytes.Length)
                .SequenceEqual(ExpectedOnlineSceneConstructedAlternateBytes) &&
            sceneConstructed.AsSpan().IndexOf(sceneDoorArmCompare.ToArray()) >= 0 &&
            sceneConstructedAlternate.AsSpan().IndexOf(sceneDoorArmCompare.ToArray()) >= 0 &&
            sceneConstructed.AsSpan().IndexOf(sceneRegistrationEsi) >= 0 &&
            sceneConstructedAlternate.AsSpan().IndexOf(sceneRegistrationEdx) >= 0 &&
            bossSentinel.Skip(bossSentinel.Length - (ExpectedOnlineBossSentinelBytes.Length + 5))
                .Take(ExpectedOnlineBossSentinelBytes.Length)
                .SequenceEqual(ExpectedOnlineBossSentinelBytes) &&
            bossRestore.Skip(bossRestore.Length - (ExpectedOnlineBossRestoreBytes.Length + 5))
                .Take(ExpectedOnlineBossRestoreBytes.Length)
                .SequenceEqual(ExpectedOnlineBossRestoreBytes) &&
            bossSentinel.AsSpan().IndexOf(BitConverter.GetBytes(EncryptedBossXWrapper)) >= 0 &&
            bossSentinel.AsSpan().IndexOf(BitConverter.GetBytes(EncryptedBossYWrapper)) >= 0 &&
            bossRestore.AsSpan().IndexOf(BitConverter.GetBytes(EncryptedBossXWrapper)) >= 0 &&
            bossRestore.AsSpan().IndexOf(BitConverter.GetBytes(EncryptedBossYWrapper)) >= 0 &&
            routeCapture.AsSpan().IndexOf(BitConverter.GetBytes(sampleRouteState + 24)) >= 0 &&
            routeCapture.Skip(routeCapture.Length - (ExpectedOnlineRouteCaptureBytes.Length + 5))
                .Take(ExpectedOnlineRouteCaptureBytes.Length)
                .SequenceEqual(ExpectedOnlineRouteCaptureBytes) &&
            capture.AsSpan().IndexOf(captureMode3.ToArray()) >= 0 &&
            lookup.AsSpan().IndexOf(mode3Compare.ToArray()) >= 0 &&
            lookup.AsSpan().IndexOf(new byte[] { 0x8D, 0x04, 0xD0 }) >= 0 &&
            coordinate.AsSpan().IndexOf(mode3Compare.ToArray()) < 0 &&
            coordinate.AsSpan().IndexOf(new byte[] { 0x89, 0x8E, 0xB8, 0x00, 0x00, 0x00 }) >= 0;
        Console.WriteLine("online-room-fix verify=" + (valid ? "passed" : "failed") +
            " capture-bytes=" + capture.Length + " state-capture-bytes=" +
            stateCapture.Length + " lookup-bytes=" + lookup.Length +
            " coordinate-bytes=" + coordinate.Length +
            " route-bytes=" + route.Length + "/" + alternateRoute.Length +
            " scene-bytes=" + sceneConstructed.Length + "/" + sceneConstructedAlternate.Length +
            " boss-bytes=" + bossSentinel.Length + "/" + bossRestore.Length +
            " route-capture-bytes=" + routeCapture.Length);
        return valid ? 0 : 2;
    }

    private static uint ResolveTailJump(byte[] code, uint address)
    {
        if (code.Length < 5 || code[code.Length - 5] != 0xE9) return 0;
        return unchecked(address + (uint)code.Length +
            (uint)BitConverter.ToInt32(code, code.Length - sizeof(int)));
    }

    private static void StartOnlineRoomCoordinateMonitor(int processId, uint stateAddress,
        uint dungeonOverrideStateAddress, string workdir)
    {
        if (stateAddress < 0x10000)
        {
            WriteLine("联机房间坐标监控未启动：补丁状态地址无效");
            return;
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                executable = Process.GetCurrentProcess().MainModule.FileName;
            var monitorLog = Path.Combine(workdir, "online-room-memory-watch.log");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workdir
            };
            startInfo.ArgumentList.Add("--watch-online-room");
            startInfo.ArgumentList.Add(processId.ToString());
            startInfo.ArgumentList.Add("--online-room-state");
            startInfo.ArgumentList.Add(stateAddress.ToString());
            if (dungeonOverrideStateAddress >= 0x10000)
            {
                startInfo.ArgumentList.Add("--dungeon-override-state");
                startInfo.ArgumentList.Add(dungeonOverrideStateAddress.ToString());
            }
            startInfo.ArgumentList.Add("--log");
            startInfo.ArgumentList.Add(monitorLog);
            using var monitor = Process.Start(startInfo);
            if (monitor == null)
                throw new InvalidOperationException("Process.Start returned null");
            WriteLine("联机房间坐标 2ms 防回写监控已启动: pid=" + monitor.Id +
                ", log=" + monitorLog);
        }
        catch (Exception exception)
        {
            WriteLine("联机房间坐标监控启动失败（进程内钩子仍有效）: " + exception.Message);
        }
    }

    private static int WatchOnlineRoomCoordinates(int processId, uint stateAddress,
        uint dungeonOverrideStateAddress, string logFile)
    {
        if (string.IsNullOrWhiteSpace(logFile))
            logFile = Path.Combine(Directory.GetCurrentDirectory(), "online-room-memory-watch.log");
        logFile = Path.GetFullPath(logFile);
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return 2;
            AppendOnlineRoomLog(logFile, "monitor started pid=" + processId +
                " state=0x" + stateAddress.ToString("X8") +
                (dungeonOverrideStateAddress >= 0x10000
                    ? " override-state=0x" + dungeonOverrideStateAddress.ToString("X8")
                    : string.Empty));

            var state = new byte[0x28];
            var routeState = new byte[OnlineRouteStateSize];
            var pointer = new byte[sizeof(uint)];
            var fields = new byte[12];
            var lastEvent = 0u;
            var lastStateCaptureCount = 0u;
            var lastRemapHits = 0u;
            var lastCoordinateHits = 0u;
            var lastRouteCaptureCount = uint.MaxValue;
            var lastRoutePreconstructCount = uint.MaxValue;
            var lastBossRestoreCount = uint.MaxValue;
            var lastSceneDoorWriteCount = uint.MaxValue;
            var lastLogicalSceneRegistrationCount = uint.MaxValue;
            var lastLogicalRoomCellWriteCount = uint.MaxValue;
            var observedRouteGeneration = uint.MaxValue;
            var generationPreconstructBaseline = 0u;
            var generationSceneDoorBaseline = 0u;
            var generationBossRestoreBaseline = 0u;
            var repairCount = 0u;
            var logicalSlotRepairCount = 0u;
            var logicalSceneSlotRepairCount = 0u;
            while (WaitForSingleObject(process.Handle, 0) != 0)
            {
                if (!ReadProcessMemory(process.Handle, new IntPtr(stateAddress), state,
                        state.Length, out _))
                {
                    Thread.Sleep(2);
                    continue;
                }

                var eventCount = BitConverter.ToUInt32(state, 0);
                var packedRoom = BitConverter.ToUInt32(state, 4);
                var logicalLookupIndex = BitConverter.ToUInt32(state, 8);
                var tableBase = BitConverter.ToUInt32(state, 0x0C);
                var mode = BitConverter.ToUInt32(state, 0x10);
                var clientState = BitConverter.ToUInt32(state, 0x14);
                var remapHits = BitConverter.ToUInt32(state, 0x18);
                var coordinateHits = BitConverter.ToUInt32(state, 0x1C);
                var source = BitConverter.ToUInt32(state, 0x20);
                var stateCaptureCount = BitConverter.ToUInt32(state, 0x24);
                var routeStartMap = 0u;
                var routePreconstructCount = 0u;
                var routeSceneDoorWriteCount = 0u;
                var routeBossRestoreCount = 0u;
                var routeLogicalSlot = 0u;
                var routeFirstScene = 0u;
                var routeFirstSceneCaptureCount = 0u;
                if (dungeonOverrideStateAddress >= 0x10000)
                {
                    if (ReadProcessMemory(process.Handle,
                            new IntPtr(dungeonOverrideStateAddress), routeState,
                            routeState.Length, out _))
                    {
                        var routeArmed = BitConverter.ToUInt32(routeState, 24);
                        var startMap = BitConverter.ToUInt32(routeState, 28);
                        var greed = BitConverter.ToUInt32(routeState, 32);
                        var packedBoss = BitConverter.ToUInt32(routeState, 36);
                        var preconstructCount = BitConverter.ToUInt32(routeState, 40);
                        var nativeMask = BitConverter.ToUInt32(routeState, 44);
                        var bossRestoreArm = BitConverter.ToUInt32(routeState, 48);
                        var bossRestoreCount = BitConverter.ToUInt32(routeState, 52);
                        var captureCount = BitConverter.ToUInt32(routeState, 64);
                        var sceneDoorArm = BitConverter.ToUInt32(routeState, 68);
                        var sceneDoorMaskBefore = BitConverter.ToUInt32(routeState, 72);
                        var sceneDoorWriteCount = BitConverter.ToUInt32(routeState, 76);
                        var logicalSceneSlot = BitConverter.ToUInt32(routeState, 80);
                        var logicalSceneRegistrationCount = BitConverter.ToUInt32(routeState, 84);
                        var logicalRoomCell = BitConverter.ToUInt32(routeState, 88);
                        var logicalRoomCellWriteCount = BitConverter.ToUInt32(routeState, 92);
                        var firstScene = BitConverter.ToUInt32(routeState, 96);
                        var firstSceneCaptureCount = BitConverter.ToUInt32(routeState, 100);
                        var routeGeneration = BitConverter.ToUInt32(routeState, 104);
                        routeStartMap = startMap;
                        if (routeGeneration != observedRouteGeneration)
                        {
                            observedRouteGeneration = routeGeneration;
                            generationPreconstructBaseline = preconstructCount;
                            generationSceneDoorBaseline = sceneDoorWriteCount;
                            generationBossRestoreBaseline = bossRestoreCount;
                            AppendOnlineRoomLog(logFile, "pvf-route generation=" + routeGeneration +
                                " dungeon=" + BitConverter.ToUInt32(routeState, 4) +
                                " baselines=preconstruct:" + generationPreconstructBaseline +
                                ",scene:" + generationSceneDoorBaseline +
                                ",boss:" + generationBossRestoreBaseline);
                        }
                        routePreconstructCount = preconstructCount > generationPreconstructBaseline
                            ? preconstructCount - generationPreconstructBaseline : 0;
                        routeSceneDoorWriteCount = sceneDoorWriteCount > generationSceneDoorBaseline
                            ? sceneDoorWriteCount - generationSceneDoorBaseline : 0;
                        routeBossRestoreCount = bossRestoreCount > generationBossRestoreBaseline
                            ? bossRestoreCount - generationBossRestoreBaseline : 0;
                        routeLogicalSlot = routeSceneDoorWriteCount != 0 ? logicalSceneSlot : 0;
                        routeFirstScene = routeSceneDoorWriteCount != 0 ? firstScene : 0;
                        routeFirstSceneCaptureCount = routeSceneDoorWriteCount != 0
                            ? firstSceneCaptureCount : 0;
                        if (captureCount != lastRouteCaptureCount ||
                            preconstructCount != lastRoutePreconstructCount ||
                            bossRestoreCount != lastBossRestoreCount ||
                            sceneDoorWriteCount != lastSceneDoorWriteCount ||
                            logicalSceneRegistrationCount != lastLogicalSceneRegistrationCount ||
                            logicalRoomCellWriteCount != lastLogicalRoomCellWriteCount)
                        {
                            lastRouteCaptureCount = captureCount;
                            lastRoutePreconstructCount = preconstructCount;
                            lastBossRestoreCount = bossRestoreCount;
                            lastSceneDoorWriteCount = sceneDoorWriteCount;
                            lastLogicalSceneRegistrationCount = logicalSceneRegistrationCount;
                            lastLogicalRoomCellWriteCount = logicalRoomCellWriteCount;
                            AppendOnlineRoomLog(logFile, "pvf-route state dungeon=" +
                                BitConverter.ToUInt32(routeState, 4) + " capture-hits=" + captureCount +
                                " route-arm=" + routeArmed + " map=" + startMap +
                                " greed=0x" + greed.ToString("X") + " boss=(" +
                                (packedBoss & 0xFF) + "," + ((packedBoss >> 8) & 0xFF) +
                                ") preconstruct-hits=" + preconstructCount +
                                " native-mask=0x" + nativeMask.ToString("X") +
                                " scene-door-arm=" + sceneDoorArm +
                                " scene-door-before=0x" + sceneDoorMaskBefore.ToString("X") +
                                " scene-door-writes=" + sceneDoorWriteCount +
                                " logical-room-cell=0x" + logicalRoomCell.ToString("X8") +
                                " logical-room-writes=" + logicalRoomCellWriteCount +
                                " logical-scene-slot=" + logicalSceneSlot +
                                " logical-scene-writes=" + logicalSceneRegistrationCount +
                                " first-scene=0x" + firstScene.ToString("X8") +
                                " first-scene-captures=" + firstSceneCaptureCount +
                                " generation=" + routeGeneration +
                                " boss-restore-arm=" + bossRestoreArm +
                                " boss-restore-hits=" + bossRestoreCount +
                                " state=0x" + dungeonOverrideStateAddress.ToString("X8"));
                        }
                    }
                }

                // The local fallback physically constructs its first room at
                // slot zero.  ARAD can clear the logical map-id and scene-table
                // registration after the constructor hook returns, so keep the
                // logical entries coherent. Scene registration is transferred,
                // rather than mirrored: the return-to-town cleanup releases
                // every non-null scene-table entry, so a duplicate pointer
                // would release the same C++ scene object twice.
                if (mode == 2 && routeStartMap != 0 && routeLogicalSlot > 0 &&
                    routeLogicalSlot < 16 && routePreconstructCount != 0 &&
                    routeSceneDoorWriteCount != 0 && routeBossRestoreCount != 0 &&
                    ReadProcessMemory(process.Handle, new IntPtr(MapSlotPointerGlobal), pointer,
                        pointer.Length, out _))
                {
                    var liveTableBase = BitConverter.ToUInt32(pointer, 0);
                    var logicalCellAddress = unchecked(liveTableBase + routeLogicalSlot * 0x28);
                    if (liveTableBase >= 0x10000 && logicalCellAddress >= liveTableBase &&
                        ReadProcessMemory(process.Handle, new IntPtr(liveTableBase), pointer,
                            pointer.Length, out _))
                    {
                        var slotZeroBefore = BitConverter.ToUInt32(pointer, 0);
                        var logicalBefore = 0u;
                        if (ReadProcessMemory(process.Handle, new IntPtr(logicalCellAddress), pointer,
                                pointer.Length, out _))
                            logicalBefore = BitConverter.ToUInt32(pointer, 0);

                        var targetWritten = logicalBefore == routeStartMap ||
                            WriteProcessMemory(process.Handle, new IntPtr(logicalCellAddress),
                                BitConverter.GetBytes(routeStartMap), sizeof(uint), out _);
                        var logicalAfter = 0u;
                        if (ReadProcessMemory(process.Handle, new IntPtr(logicalCellAddress), pointer,
                                pointer.Length, out _))
                            logicalAfter = BitConverter.ToUInt32(pointer, 0);

                        // The fallback constructor always starts at physical slot zero.
                        // Once the real PVF slot has its map ID, discard that temporary
                        // slot-zero map ID in the same monitor iteration. Leaving both
                        // cells populated makes the minimap bind the scene to slot zero;
                        // clearing it before the logical cell is ready leaves an island.
                        var slotZeroWritten = false;
                        var slotZeroAfter = slotZeroBefore;
                        if (routeLogicalSlot != 0 && targetWritten &&
                            logicalAfter == routeStartMap && slotZeroBefore == routeStartMap)
                        {
                            slotZeroWritten = WriteProcessMemory(process.Handle,
                                new IntPtr(liveTableBase), new byte[sizeof(uint)], sizeof(uint), out _);
                            if (ReadProcessMemory(process.Handle, new IntPtr(liveTableBase), pointer,
                                    pointer.Length, out _))
                                slotZeroAfter = BitConverter.ToUInt32(pointer, 0);
                        }

                        if (logicalBefore != routeStartMap || slotZeroWritten)
                        {
                            logicalSlotRepairCount++;
                            AppendOnlineRoomLog(logFile, "logical-slot-memory repair=" +
                                logicalSlotRepairCount + " slot=" + routeLogicalSlot +
                                " map=" + routeStartMap + " table=0x" +
                                liveTableBase.ToString("X8") + " slot0-before=" +
                                slotZeroBefore + " target-before=" + logicalBefore +
                                " target-write=" + targetWritten + " slot0-write=" +
                                slotZeroWritten + " slot0-after=" + slotZeroAfter +
                                " target-after=" + logicalAfter);
                        }

                        if (routeFirstScene >= 0x10000 && routeFirstSceneCaptureCount != 0 &&
                            ReadProcessMemory(process.Handle, new IntPtr(RoomSceneTableGlobal), pointer,
                                pointer.Length, out _))
                        {
                            var sceneTable = BitConverter.ToUInt32(pointer, 0);
                            var logicalSceneAddress = unchecked(sceneTable + routeLogicalSlot * sizeof(uint));
                            if (sceneTable >= 0x10000 && logicalSceneAddress >= sceneTable &&
                                ReadProcessMemory(process.Handle, new IntPtr(sceneTable), pointer,
                                    pointer.Length, out _))
                            {
                                var physicalSceneBefore = BitConverter.ToUInt32(pointer, 0);
                                var logicalSceneBefore = 0u;
                                if (ReadProcessMemory(process.Handle, new IntPtr(logicalSceneAddress), pointer,
                                        pointer.Length, out _))
                                    logicalSceneBefore = BitConverter.ToUInt32(pointer, 0);
                                var sceneWritten = logicalSceneBefore == routeFirstScene ||
                                    WriteProcessMemory(process.Handle, new IntPtr(logicalSceneAddress),
                                        BitConverter.GetBytes(routeFirstScene), sizeof(uint), out _);
                                var logicalSceneAfter = 0u;
                                if (ReadProcessMemory(process.Handle, new IntPtr(logicalSceneAddress), pointer,
                                        pointer.Length, out _))
                                    logicalSceneAfter = BitConverter.ToUInt32(pointer, 0);

                                // Clear slot zero only after the target slot has
                                // been read back as the exact same scene pointer.
                                var sourceCleared = false;
                                var physicalSceneAfter = physicalSceneBefore;
                                if (sceneWritten && logicalSceneAfter == routeFirstScene &&
                                    physicalSceneBefore == routeFirstScene)
                                {
                                    sourceCleared = WriteProcessMemory(process.Handle, new IntPtr(sceneTable),
                                        new byte[sizeof(uint)], sizeof(uint), out _);
                                    if (ReadProcessMemory(process.Handle, new IntPtr(sceneTable), pointer,
                                            pointer.Length, out _))
                                        physicalSceneAfter = BitConverter.ToUInt32(pointer, 0);
                                }
                                if (logicalSceneBefore != routeFirstScene || sourceCleared)
                                {
                                    logicalSceneSlotRepairCount++;
                                    AppendOnlineRoomLog(logFile, "logical-scene-slot-memory transfer=" +
                                        logicalSceneSlotRepairCount + " slot=" + routeLogicalSlot +
                                        " scene=0x" + routeFirstScene.ToString("X8") + " table=0x" +
                                        sceneTable.ToString("X8") + " slot0-before=0x" +
                                        physicalSceneBefore.ToString("X8") + " target-before=0x" +
                                        logicalSceneBefore.ToString("X8") + " target-write=" + sceneWritten +
                                        " target-after=0x" + logicalSceneAfter.ToString("X8") +
                                        " slot0-clear=" + sourceCleared + " slot0-after=0x" +
                                        physicalSceneAfter.ToString("X8"));
                                }

                            }
                        }
                    }
                }
                if (eventCount != 0 && eventCount != lastEvent)
                {
                    lastEvent = eventCount;
                    AppendOnlineRoomLog(logFile, "room transition count=" + eventCount +
                        " source=" + (source == 2 ? "fallback" : source == 1 ? "START_MAP" : "unknown") +
                        " room=(" + (packedRoom & 0xFF) + "," + ((packedRoom >> 8) & 0xFF) +
                        ") logical-slot=" + (logicalLookupIndex / 5) +
                        " physical-slot=" + (mode == 3 ? logicalLookupIndex / 5 : 0) +
                        " mode=" + mode +
                        " client-state=0x" + clientState.ToString("X8") +
                        " state-captures=" + stateCaptureCount +
                        " lookup-hits=" + remapHits +
                        " coordinate-hits=" + coordinateHits);
                }
                if (stateCaptureCount != lastStateCaptureCount || remapHits != lastRemapHits ||
                    coordinateHits != lastCoordinateHits)
                {
                    lastStateCaptureCount = stateCaptureCount;
                    lastRemapHits = remapHits;
                    lastCoordinateHits = coordinateHits;
                    AppendOnlineRoomLog(logFile, "patch counters state-captures=" +
                        stateCaptureCount + " lookup-hits=" + remapHits +
                        " coordinate-hits=" + coordinateHits + " client-state=0x" +
                        clientState.ToString("X8"));
                }

                // Only the initial local fallback needs coordinate repair.
                // A native START_MAP transition must retain the old coordinate
                // until its own handler computes the path-gate entry direction.
                if (mode == 2 && tableBase >= 0x10000 &&
                    ReadProcessMemory(process.Handle, new IntPtr(MapSlotPointerGlobal), pointer,
                        pointer.Length, out _) && BitConverter.ToUInt32(pointer, 0) == tableBase)
                {
                    uint layout;
                    uint roomX;
                    uint roomY;
                    if (clientState < 0x10000 ||
                        !TryReadOnlineClientRoomStateAt(process.Handle, clientState, fields,
                            out layout, out roomX, out roomY))
                    {
                        if (TryReadOnlineClientRoomState(process.Handle, fields, out clientState,
                                out layout, out roomX, out roomY))
                        {
                            var captured = BitConverter.GetBytes(clientState);
                            WriteProcessMemory(process.Handle,
                                new IntPtr(unchecked(stateAddress + 0x14)), captured,
                                captured.Length, out _);
                        }
                    }

                    var targetX = packedRoom & 0xFF;
                    var targetY = (packedRoom >> 8) & 0xFF;
                    if (clientState >= 0x10000 && layout >= 0x10000 &&
                        (roomX != targetX || roomY != targetY))
                    {
                        var modeCheck = new byte[sizeof(uint)];
                        if (ReadProcessMemory(process.Handle,
                                new IntPtr(unchecked(stateAddress + 0x10)), modeCheck,
                                modeCheck.Length, out _) &&
                            BitConverter.ToUInt32(modeCheck, 0) == 2)
                        {
                            var coordinates = new byte[sizeof(uint) * 2];
                            Buffer.BlockCopy(BitConverter.GetBytes(targetX), 0, coordinates, 0,
                                sizeof(uint));
                            Buffer.BlockCopy(BitConverter.GetBytes(targetY), 0, coordinates,
                                sizeof(uint), sizeof(uint));
                            if (WriteProcessMemory(process.Handle,
                                    new IntPtr(unchecked(clientState + 0xB8)), coordinates,
                                    coordinates.Length, out _))
                            {
                                repairCount++;
                                AppendOnlineRoomLog(logFile, "coordinate repair=" + repairCount +
                                    " room=(" + targetX + "," + targetY + ") client-state=0x" +
                                    clientState.ToString("X8"));
                            }
                        }
                    }
                }
                Thread.Sleep(2);
            }
            AppendOnlineRoomLog(logFile, "monitor ended pid=" + processId);
            return 0;
        }
        catch (Exception exception)
        {
            AppendOnlineRoomLog(logFile, "monitor failed pid=" + processId + ": " +
                exception.Message);
            return 4;
        }
    }

    private static bool TryReadOnlineClientRoomState(IntPtr process, byte[] fields,
        out uint state, out uint layout, out uint roomX, out uint roomY)
    {
        state = layout = roomX = roomY = 0;
        var pointer = new byte[sizeof(uint)];
        if (!ReadProcessMemory(process, new IntPtr(ClientRootGlobal), pointer,
                pointer.Length, out _))
            return false;
        var root = BitConverter.ToUInt32(pointer, 0);
        if (root < 0x10000 ||
            !ReadProcessMemory(process, new IntPtr(unchecked(root + 0x86C)), pointer,
                pointer.Length, out _))
            return false;
        var owner = BitConverter.ToUInt32(pointer, 0);
        if (owner < 0x10000 ||
            !ReadProcessMemory(process, new IntPtr(unchecked(owner + 0x14)), pointer,
                pointer.Length, out _))
            return false;
        state = BitConverter.ToUInt32(pointer, 0);
        return state >= 0x10000 && TryReadOnlineClientRoomStateAt(process, state, fields,
            out layout, out roomX, out roomY);
    }

    private static bool TryReadOnlineClientRoomStateAt(IntPtr process, uint state, byte[] fields,
        out uint layout, out uint roomX, out uint roomY)
    {
        layout = roomX = roomY = 0;
        if (!ReadProcessMemory(process, new IntPtr(unchecked(state + 0xB4)), fields,
                fields.Length, out _))
            return false;
        layout = BitConverter.ToUInt32(fields, 0);
        roomX = BitConverter.ToUInt32(fields, 4);
        roomY = BitConverter.ToUInt32(fields, 8);
        return layout >= 0x10000;
    }

    private static void AppendOnlineRoomLog(string path, string message)
    {
        try
        {
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " +
                message + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void AddUInt32(List<byte> buffer, uint value)
    {
        buffer.AddRange(BitConverter.GetBytes(value));
    }

    private static void AddInt32(List<byte> buffer, int value)
    {
        buffer.AddRange(BitConverter.GetBytes(value));
    }

    private static void AddRelativeCall(List<byte> buffer, uint stubAddress, uint destination)
    {
        buffer.Add(0xE8);
        var nextInstruction = checked(stubAddress + (uint)buffer.Count + 4);
        AddInt32(buffer, unchecked((int)(destination - nextInstruction)));
    }

    private static void WriteRelative(byte[] buffer, int offset, uint sourceAfterInstruction, uint destination)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(unchecked((int)(destination - sourceAfterInstruction))), 0,
            buffer, offset, sizeof(int));
    }

    private static bool InstallNotifyTrace(IntPtr processHandle)
    {
        // 通知分发表入口 0x41E85F-0x41E882：
        //   movzx eax,byte [ebp-0Dh]; cmp eax,0C1h; ja 4371C3h;
        //   mov ecx,[ebp-88ECh]; jmp dword [ecx*4+4371DBh]
        // 把最后的 jmp 改成跳转到记录桩，桩把通知 ID 写进 0xC0AA80 环形区，
        // 再跳回原分发表。环形区布局：
        //   0xC0AA80 magic "ARAD"
        //   0xC0AA84 u32 counter
        //   0xC0AA88 u32 lastId
        //   0xC0AA8C byte ring[256]
        const uint stubVa = 0x00C0A9C6;
        const uint ringVa = 0x00C0AA80;
        const uint counterVa = ringVa + 4;
        const uint lastIdVa = ringVa + 8;
        const uint ringBytesVa = ringVa + 12;
        const uint tableVa = 0x004371DB;
        var stub = new byte[57];
        var offset = 0;

        stub[offset++] = 0x60;                                  // pushad
        stub[offset++] = 0x8B; stub[offset++] = 0x85;           // mov eax,[ebp-88ECh]
        stub[offset++] = 0x14; stub[offset++] = 0x77;
        stub[offset++] = 0xFF; stub[offset++] = 0xFF;
        stub[offset++] = 0x8B; stub[offset++] = 0x0D;           // mov ecx,[counter]
        WriteUInt32(stub, ref offset, counterVa);
        stub[offset++] = 0x41;                                  // inc ecx
        stub[offset++] = 0x89; stub[offset++] = 0x0D;           // mov [counter],ecx
        WriteUInt32(stub, ref offset, counterVa);
        stub[offset++] = 0x89; stub[offset++] = 0x05;           // mov [lastId],eax
        WriteUInt32(stub, ref offset, lastIdVa);
        stub[offset++] = 0x81; stub[offset++] = 0xE1;           // and ecx,0FFh
        stub[offset++] = 0xFF; stub[offset++] = 0x00;
        stub[offset++] = 0x00; stub[offset++] = 0x00;
        stub[offset++] = 0x88; stub[offset++] = 0x81;           // mov [ecx+ring],al
        WriteUInt32(stub, ref offset, ringBytesVa);
        stub[offset++] = 0xC7; stub[offset++] = 0x05;           // mov dword [ring],"ARAD"
        WriteUInt32(stub, ref offset, ringVa);
        stub[offset++] = 0x41; stub[offset++] = 0x52;
        stub[offset++] = 0x41; stub[offset++] = 0x44;
        stub[offset++] = 0x61;                                  // popad
        stub[offset++] = 0xFF; stub[offset++] = 0x24;           // jmp dword [ecx*4+table]
        stub[offset++] = 0x8D;
        WriteUInt32(stub, ref offset, tableVa);

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
        {
            WriteLine("通知记录桩写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        var entry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, 0x0041E884, stubVa);
        var original = new byte[] { 0xFF, 0x24, 0x8D, 0xDB, 0x71, 0x43, 0x00 };
        if (!Patch(processHandle, 0x0041E87F, original, entry))
            return false;
        WriteLine("通知 ID 记录桩已安装: ring=0x" + ringVa.ToString("X8") + " stub=0x" + stubVa.ToString("X8"));
        return true;
    }

    private static bool InstallExitTrace(IntPtr processHandle)
    {
        // 把客户端 IAT 里的 ExitProcess 指针替换成记录桩，抓退出调用地址与退出码。
        // 桩（0xC0ACC0）：返回地址 -> 0xC0AD04，退出码 -> 0xC0AD08，计数 -> 0xC0AD0C，
        // 原地址存 0xC0AD00，最后 jmp 原函数。
        var kernel = GetModuleHandle("kernel32.dll");
        if (kernel == IntPtr.Zero) return true;
        var exitProcess = GetProcAddress(kernel, "ExitProcess");
        if (exitProcess == IntPtr.Zero) return true;

        // 客户端是 32 位进程，解析其自身的 kernel32 导出表以获取 ExitProcess 地址。
        var remoteExit = ResolveRemoteExport(processHandle, GetProcessId(processHandle), "kernel32.dll", "ExitProcess");
        if (remoteExit != IntPtr.Zero) exitProcess = remoteExit;

        const uint stubVa = 0x00C0ACC0;
        const uint originalVa = 0x00C0AD00;
        const uint returnVa = 0x00C0AD04;
        const uint codeVa = 0x00C0AD08;
        const uint countVa = 0x00C0AD0C;

        var stub = new byte[30];
        var offset = 0;
        stub[offset++] = 0x8B; stub[offset++] = 0x04; stub[offset++] = 0x24;      // mov eax,[esp]
        stub[offset++] = 0xA3;                                                    // mov [returnVa],eax
        WriteUInt32(stub, ref offset, returnVa);
        stub[offset++] = 0x8B; stub[offset++] = 0x44; stub[offset++] = 0x24; stub[offset++] = 0x04; // mov eax,[esp+4]
        stub[offset++] = 0xA3;                                                    // mov [codeVa],eax
        WriteUInt32(stub, ref offset, codeVa);
        stub[offset++] = 0xFF; stub[offset++] = 0x05;                             // inc dword [countVa]
        WriteUInt32(stub, ref offset, countVa);
        stub[offset++] = 0xA1;                                                    // mov eax,[originalVa]
        WriteUInt32(stub, ref offset, originalVa);
        stub[offset++] = 0xFF; stub[offset++] = 0xE0;                             // jmp eax

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
        {
            WriteLine("ExitProcess 记录桩写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }
        var needle = BitConverter.GetBytes((uint)exitProcess.ToInt64());
        var targets = new List<uint>();
        var address = IntPtr.Zero;
        while (true)
        {
            MEMORY_BASIC_INFORMATION mbi;
            if (!VirtualQueryEx(processHandle, address, out mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()))
                break;
            var size = (long)mbi.RegionSize;
            if (size > 0 && size <= 0x7FFFFFFF && mbi.State == 0x1000)
            {
                var data = new byte[(int)size];
                IntPtr read;
                if (ReadProcessMemory(processHandle, mbi.BaseAddress, data, data.Length, out read))
                {
                    for (var i = 0; i <= data.Length - 4; i++)
                    {
                        if (data[i] == needle[0] && data[i + 1] == needle[1] && data[i + 2] == needle[2] && data[i + 3] == needle[3])
                        {
                            var target = (uint)(mbi.BaseAddress.ToInt64() + i);
                            if (target >= stubVa - 0x80 && target <= originalVa + 0x40) continue;
                            targets.Add(target);
                        }
                    }
                }
            }
            var next = mbi.BaseAddress.ToInt64() + size;
            if (next <= address.ToInt64()) break;
            address = new IntPtr(next);
        }
        if (targets.Count != 0)
        {
            if (!WriteProcessMemory(processHandle, new IntPtr((long)originalVa), BitConverter.GetBytes((uint)exitProcess.ToInt64()), 4, out bytesWritten))
            {
                WriteLine("ExitProcess 原地址写入失败: " + Marshal.GetLastWin32Error());
                return true;
            }
            var hookedCount = 0;
            foreach (var target in targets)
            {
                uint iatOldProtect;
                VirtualProtectEx(processHandle, new IntPtr((long)target), 4, 0x40, out iatOldProtect);
                if (!WriteProcessMemory(processHandle, new IntPtr((long)target), BitConverter.GetBytes(stubVa), 4, out bytesWritten))
                {
                    WriteLine("ExitProcess IAT 替换失败 @0x" + target.ToString("X8") + ": " + Marshal.GetLastWin32Error());
                    continue;
                }
                hookedCount++;
            }
            WriteLine("ExitProcess 已挂钩: iat count=" + hookedCount + "/" + targets.Count +
                " stub=0x" + stubVa.ToString("X8") + " exitProcess=0x" + ((uint)exitProcess.ToInt64()).ToString("X8"));
        }
        // 兜底：直接挂钩 ExitProcess 代码入口（保存前 6 字节，改页权限后写 jmp）。
        const uint entryStubVa = 0x00C0ACC0;
        const uint entryOriginalVa = 0x00C0AD10;
        const uint entryReturnVa = 0x00C0AD14;
        const uint entryCodeVa = 0x00C0AD18;
        const uint entryCountVa = 0x00C0AD1C;
        var originalBytes = new byte[6];
        IntPtr readBytes;
        if (!ReadProcessMemory(processHandle, exitProcess, originalBytes, 6, out readBytes))
        {
            WriteLine("读取 ExitProcess 入口失败: " + Marshal.GetLastWin32Error());
            return true;
        }
        uint oldProtect;
        if (!VirtualProtectEx(processHandle, exitProcess, 6, 0x40, out oldProtect))
        {
            WriteLine("VirtualProtectEx 失败: " + Marshal.GetLastWin32Error());
            return true;
        }
        if (!WriteProcessMemory(processHandle, new IntPtr((long)entryOriginalVa), originalBytes, 6, out bytesWritten))
        {
            WriteLine("保存 ExitProcess 原始字节失败: " + Marshal.GetLastWin32Error());
            return true;
        }
        var entryStub = new byte[6 + 6 + 6 + 5 + 5 + 5 + 2 + 6 + 5];
        var so = 0;
        entryStub[so++] = 0x8B; entryStub[so++] = 0x04; entryStub[so++] = 0x24;                       // mov eax,[esp]
        entryStub[so++] = 0xA3;                                                                       // mov [ret],eax
        WriteUInt32(entryStub, ref so, entryReturnVa);
        entryStub[so++] = 0x8B; entryStub[so++] = 0x44; entryStub[so++] = 0x24; entryStub[so++] = 0x04;    // mov eax,[esp+4]
        entryStub[so++] = 0xA3;                                                                       // mov [code],eax
        WriteUInt32(entryStub, ref so, entryCodeVa);
        entryStub[so++] = 0xFF; entryStub[so++] = 0x05;                                               // inc dword [count]
        WriteUInt32(entryStub, ref so, entryCountVa);
        Buffer.BlockCopy(originalBytes, 0, entryStub, so, 6); so += 6;                                // 原始 6 字节
        entryStub[so++] = 0xE9;                                                                       // jmp exitProcess+6
        WriteRelative(entryStub, ref so, (uint)(entryStubVa + so), (uint)(exitProcess.ToInt64() + 6));
        if (!WriteProcessMemory(processHandle, new IntPtr((long)entryStubVa), entryStub, entryStub.Length, out bytesWritten))
        {
            WriteLine("ExitProcess 入口桩写入失败: " + Marshal.GetLastWin32Error());
            return true;
        }
        var jmp = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00, 0x90 };
        var jo = 1;
        WriteRelative(jmp, ref jo, (uint)(exitProcess.ToInt64() + 5), entryStubVa);
        if (!WriteProcessMemory(processHandle, exitProcess, jmp, 6, out bytesWritten))
        {
            WriteLine("ExitProcess 入口 jmp 写入失败: " + Marshal.GetLastWin32Error());
            return true;
        }
        WriteLine("ExitProcess 入口已挂钩: entry=0x" + ((uint)exitProcess.ToInt64()).ToString("X8") +
            " stub=0x" + entryStubVa.ToString("X8"));
        return true;
    }

    private static IntPtr ResolveRemoteExport(IntPtr processHandle, uint processId, string moduleName, string procName)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000008 | 0x00000010, processId); // TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32
        if (snapshot == IntPtr.Zero)
        {
            WriteLine("CreateToolhelp32Snapshot 失败: " + Marshal.GetLastWin32Error());
            return IntPtr.Zero;
        }
        var entry = new MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>() };
        uint moduleBase = 0;
        var firstOk = Module32First(snapshot, ref entry);
        if (!firstOk)
        {
            WriteLine("Module32First 失败: " + Marshal.GetLastWin32Error() + " dwSize=" + entry.dwSize);
        }
        if (firstOk)
        {
            var count = 0;
            do
            {
                if (count < 5) WriteLine("模块: " + entry.szModule + " base=0x" + entry.modBaseAddr.ToString("X8"));
                count++;
                if (string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    moduleBase = entry.modBaseAddr;
                    break;
                }
            } while (Module32Next(snapshot, ref entry));
            WriteLine("模块枚举总数(前几个): " + count);
        }
        CloseHandle(snapshot);
        if (moduleBase == 0)
        {
            // 兜底：用 psapi EnumProcessModulesEx 枚举 32 位模块。
            uint needed;
            if (EnumProcessModulesEx(processHandle, null, 0, out needed, 0x01))
            {
                var handles = new IntPtr[needed / 4];
                uint got;
                if (EnumProcessModulesEx(processHandle, handles, (uint)handles.Length * 4, out got, 0x01))
                {
                    for (var i = 0; i < handles.Length; i++)
                    {
                        var name = new System.Text.StringBuilder(260);
                        GetModuleBaseName(processHandle, handles[i], name, 260);
                        if (string.Equals(name.ToString(), moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            MODULEINFO info;
                            if (GetModuleInformation(processHandle, handles[i], out info, (uint)Marshal.SizeOf<MODULEINFO>()))
                            {
                                moduleBase = (uint)info.lpBaseOfDll.ToInt64();
                                WriteLine("psapi 找到 " + moduleName + " base=0x" + moduleBase.ToString("X8"));
                                break;
                            }
                        }
                    }
                }
            }
            if (moduleBase == 0)
            {
                WriteLine("远程模块枚举未找到 " + moduleName);
                return IntPtr.Zero;
            }
        }
        WriteLine("远程模块 " + moduleName + " base=0x" + moduleBase.ToString("X8"));

        var header = new byte[0x1000];
        IntPtr read;
        if (!ReadProcessMemory(processHandle, new IntPtr((long)moduleBase), header, header.Length, out read)) return IntPtr.Zero;
        var pe = BitConverter.ToInt32(header, 0x3C);
        if (pe < 0 || pe + 0x80 > header.Length) return IntPtr.Zero;
        var magic = BitConverter.ToUInt16(header, pe + 24);
        var exportRva = 0u;
        var exportSize = 0u;
        if (magic == 0x10B) // PE32
        {
            exportRva = BitConverter.ToUInt32(header, pe + 24 + 96 + 0);
            exportSize = BitConverter.ToUInt32(header, pe + 24 + 96 + 4);
        }
        else if (magic == 0x20B) // PE32+
        {
            exportRva = BitConverter.ToUInt32(header, pe + 24 + 112 + 0);
            exportSize = BitConverter.ToUInt32(header, pe + 24 + 112 + 4);
        }
        if (exportRva == 0 || exportSize == 0 || exportSize > 0x100000) return IntPtr.Zero;
        WriteLine("远程导出目录 rva=0x" + exportRva.ToString("X8") + " size=0x" + exportSize.ToString("X8"));
        var exportData = new byte[exportSize];
        if (!ReadProcessMemory(processHandle, new IntPtr((long)(moduleBase + exportRva)), exportData, exportData.Length, out read)) return IntPtr.Zero;

        var numberOfNames = BitConverter.ToUInt32(exportData, 24);
        var addressOfFunctions = BitConverter.ToUInt32(exportData, 28);
        var addressOfNames = BitConverter.ToUInt32(exportData, 32);
        var addressOfNameOrdinals = BitConverter.ToUInt32(exportData, 36);
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(procName);
        for (uint i = 0; i < numberOfNames; i++)
        {
            var nameRva = BitConverter.ToUInt32(exportData, (int)(addressOfNames - exportRva + i * 4));
            var nameAddr = moduleBase + nameRva;
            var nameBuf = new byte[64];
            if (!ReadProcessMemory(processHandle, new IntPtr((long)nameAddr), nameBuf, nameBuf.Length, out read)) continue;
            var len = 0;
            while (len < nameBuf.Length && nameBuf[len] != 0) len++;
            if (len == nameBytes.Length)
            {
                var match = true;
                for (var j = 0; j < len; j++) if (nameBuf[j] != nameBytes[j]) { match = false; break; }
                if (match)
                {
                    var ordinal = BitConverter.ToUInt16(exportData, (int)(addressOfNameOrdinals - exportRva + i * 2));
                    var functionRva = BitConverter.ToUInt32(exportData, (int)(addressOfFunctions - exportRva + ordinal * 4));
                    return new IntPtr((long)(moduleBase + functionRva));
                }
            }
        }
        return IntPtr.Zero;
    }

    private static void InstallCrtExitTrace(IntPtr processHandle)
    {
        var processId = GetProcessId(processHandle);
        var slot = 0;
        foreach (var pair in new[] { new[] { "msvcr71.dll", "exit" }, new[] { "msvcr71.dll", "_exit" },
            new[] { "msvcp71.dll", "exit" }, new[] { "msvcp71.dll", "_exit" } })
        {
            var moduleName = pair[0];
            var procName = pair[1];
            var func = ResolveRemoteExport(processHandle, processId, moduleName, procName);
            if (func == IntPtr.Zero) continue;
            var original = new byte[6];
            IntPtr readBytes;
            if (!ReadProcessMemory(processHandle, func, original, 6, out readBytes)) continue;
            var stubVa = (uint)(0x00C0AE00 + slot * 0x60);
            var retSlot = (uint)(0x00C0AD20 + slot * 12);
            var countSlot = retSlot + 8;
            var stub = new byte[6 + 6 + 6 + 5 + 5 + 5 + 2 + 6 + 5];
            var so = 0;
            stub[so++] = 0x8B; stub[so++] = 0x45; stub[so++] = 0x08;                       // mov eax,[ebp+8]
            stub[so++] = 0xA3;                                                             // mov [ret],eax
            WriteUInt32(stub, ref so, retSlot);
            stub[so++] = 0x31; stub[so++] = 0xC0;                                          // xor eax,eax
            stub[so++] = 0xA3;                                                             // mov [code],eax
            WriteUInt32(stub, ref so, retSlot + 4);
            stub[so++] = 0xFF; stub[so++] = 0x05;                                          // inc dword [count]
            WriteUInt32(stub, ref so, countSlot);
            Buffer.BlockCopy(original, 0, stub, so, 6); so += 6;
            stub[so++] = 0xE9;
            WriteRelative(stub, ref so, (uint)(stubVa + so), (uint)(func.ToInt64() + 6));
            IntPtr bytesWritten;
            if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten)) continue;
            uint oldProtect;
            if (!VirtualProtectEx(processHandle, func, 6, 0x40, out oldProtect)) continue;
            var jmp = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00, 0x90 };
            var jo = 1;
            WriteRelative(jmp, ref jo, (uint)(func.ToInt64() + 5), stubVa);
            if (!WriteProcessMemory(processHandle, func, jmp, 6, out bytesWritten)) continue;
            WriteLine("CRT " + moduleName + "!" + procName + " 已挂钩: entry=0x" + ((uint)func.ToInt64()).ToString("X8") +
                " stub=0x" + stubVa.ToString("X8"));
            slot++;
        }
    }

    private static void LogRemoteModules(IntPtr processHandle)
    {
        uint needed;
        if (!EnumProcessModulesEx(processHandle, null, 0, out needed, 0x01)) return;
        var handles = new IntPtr[needed / 4];
        uint got;
        if (!EnumProcessModulesEx(processHandle, handles, (uint)handles.Length * 4, out got, 0x01)) return;
        for (var i = 0; i < handles.Length; i++)
        {
            var name = new System.Text.StringBuilder(260);
            GetModuleBaseName(processHandle, handles[i], name, 260);
            MODULEINFO info;
            if (GetModuleInformation(processHandle, handles[i], out info, (uint)Marshal.SizeOf<MODULEINFO>()))
            {
                WriteLine("模块: " + name + " base=0x" + ((uint)info.lpBaseOfDll.ToInt64()).ToString("X8") +
                    " size=0x" + info.SizeOfImage.ToString("X8"));
            }
        }
    }

    private static void InstallDumpReportTrace(IntPtr processHandle)
    {
        var processId = GetProcessId(processHandle);
        var slot = 0;
        foreach (var procName in new[] { "InitStackTraceLibrary", "ImplicitExceptionHandler", "RegisterExceptionCallback" })
        {
            var func = ResolveRemoteExport(processHandle, processId, "DumpReport.dll", procName);
            if (func == IntPtr.Zero) continue;
            var original = new byte[6];
            IntPtr readBytes;
            if (!ReadProcessMemory(processHandle, func, original, 6, out readBytes)) continue;
            var stubVa = (uint)(0x00C0AF00 + slot * 0x40);
            var retSlot = (uint)(0x00C0AD80 + slot * 12);
            var stub = new byte[31];
            var so = 0;
            stub[so++] = 0x60;                                                             // pushad
            stub[so++] = 0x8B; stub[so++] = 0x44; stub[so++] = 0x24; stub[so++] = 0x20;    // mov eax,[esp+0x20]
            stub[so++] = 0xA3;                                                             // mov [ret],eax
            WriteUInt32(stub, ref so, retSlot);
            stub[so++] = 0x8B; stub[so++] = 0x44; stub[so++] = 0x24; stub[so++] = 0x24;    // mov eax,[esp+0x24]
            stub[so++] = 0xA3;                                                             // mov [code],eax
            WriteUInt32(stub, ref so, retSlot + 4);
            stub[so++] = 0xFF; stub[so++] = 0x05;                                          // inc dword [count]
            WriteUInt32(stub, ref so, retSlot + 8);
            stub[so++] = 0x61;                                                             // popad
            stub[so++] = 0xE9;
            WriteRelative(stub, ref so, (uint)(stubVa + so), (uint)(func.ToInt64() + 6));
            IntPtr bytesWritten;
            if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten)) continue;
            uint oldProtect;
            if (!VirtualProtectEx(processHandle, func, 6, 0x40, out oldProtect)) continue;
            var jmp = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00, 0x90 };
            var jo = 1;
            WriteRelative(jmp, ref jo, (uint)(func.ToInt64() + 5), stubVa);
            if (!WriteProcessMemory(processHandle, func, jmp, 6, out bytesWritten)) continue;
            WriteLine("DumpReport!" + procName + " 已挂钩: entry=0x" + ((uint)func.ToInt64()).ToString("X8") +
                " stub=0x" + stubVa.ToString("X8"));
            slot++;
        }
        // 内部异常处理器后半段 0x200010F1（DumpReport 未导出，原指令不依赖 EBP）。
        // 此时 [ebp+8] 仍是异常信息指针；记录后执行原指令并跳回 0x200010F7。
        var internalHandler = new IntPtr(0x200010F1);
        var internalOriginal = new byte[6];
        IntPtr internalRead;
        if (ReadProcessMemory(processHandle, internalHandler, internalOriginal, 6, out internalRead))
        {
            const uint stubVa = 0x00C0AEC0;
            const uint retSlot = 0x00C0ADC0;
            var stub = new byte[48];
            var so = 0;
            stub[so++] = 0x55;                                                             // push ebp
            stub[so++] = 0x60;                                                             // pushad
            stub[so++] = 0x8B; stub[so++] = 0x44; stub[so++] = 0x24; stub[so++] = 0x24;    // mov eax,[esp+0x24] (original ebp)
            stub[so++] = 0xA3;                                                             // mov [0xC0ADE0],eax
            WriteUInt32(stub, ref so, 0x00C0ADE0u);
            stub[so++] = 0x8B; stub[so++] = 0x44; stub[so++] = 0x24; stub[so++] = 0x28;    // mov eax,[esp+0x28] (old stack top)
            stub[so++] = 0xA3;                                                             // mov [ret],eax
            WriteUInt32(stub, ref so, retSlot);
            stub[so++] = 0xA1;                                                             // mov eax,[0x20003018]
            WriteUInt32(stub, ref so, 0x20003018u);                                        // DumpReport 全局异常信息指针
            stub[so++] = 0xA3;                                                             // mov [arg0],eax
            WriteUInt32(stub, ref so, retSlot + 4);
            stub[so++] = 0xFF; stub[so++] = 0x05;                                          // inc dword [count]
            WriteUInt32(stub, ref so, retSlot + 8);
            stub[so++] = 0x61;                                                             // popad
            stub[so++] = 0x58;                                                             // pop ebp
            stub[so++] = 0xE9;
            WriteRelative(stub, ref so, (uint)(stubVa + so), 0x200010F7u);
            IntPtr bytesWritten;
            if (WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
            {
                uint oldProtect;
                if (VirtualProtectEx(processHandle, internalHandler, 6, 0x40, out oldProtect))
                {
                    var jmp = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00, 0x90 };
                    var jo = 1;
                    WriteRelative(jmp, ref jo, 0x200010F6u, stubVa);
                    if (WriteProcessMemory(processHandle, internalHandler, jmp, 6, out bytesWritten))
                    {
                        WriteLine("DumpReport!内部异常处理器 0x200010F1 已挂钩: stub=0x" + stubVa.ToString("X8"));
                    }
                }
            }
        }
    }

    private static bool InstallComicTrace(IntPtr processHandle)
    {
        // CNRDComicCutWindow 工厂 0x95AC40 入口（push esi; push 0; push 0; push -1; ...）。
        // 在入口写标记 0xC0AC20=1，再跳回 0x95AC45 继续原逻辑。
        const uint stubVa = 0x00C0AB80;
        var stub = new byte[15];
        var offset = 0;
        stub[offset++] = 0xC7; stub[offset++] = 0x05;             // mov dword [0xC0AC20],1
        WriteUInt32(stub, ref offset, 0x00C0AC20);
        stub[offset++] = 0x01; stub[offset++] = 0x00;
        stub[offset++] = 0x00; stub[offset++] = 0x00;
        stub[offset++] = 0xE9;                                    // jmp 0x95AC45
        WriteRelative(stub, ref offset, stubVa + 15, 0x0095AC45);

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
        {
            WriteLine("漫画记录桩写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }
        var entry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00, 0x90, 0x90, 0x90 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, 0x0095AC45, stubVa);
        if (!Patch(processHandle, 0x0095AC40, new byte[] { 0x56, 0x6A, 0x00, 0x6A, 0x00, 0x6A, 0xFF, 0x68 }, entry))
            return false;
        WriteLine("漫画窗口记录桩已安装: comic=0x00C0AC20");
        return true;
    }

    private static bool InstallComicStateTrace(IntPtr processHandle)
    {
        // 在漫画翻页函数 0x95B000 的调用点 0x95B48E 挂桩：
        //   call 0x95B000（翻页/按键处理）前记录漫画窗口状态到
        //   0xC0AC40 this / 0xC0AC44 当前页 / 0xC0AC48 总页数 /
        //   0xC0AC4C 子状态，再执行原 call 并跳回 0x95B493。
        const uint stubVa = 0x00C0AA00;
        var stub = new byte[49];
        var offset = 0;
        stub[offset++] = 0x89; stub[offset++] = 0x35;           // mov [0xC0AC40],esi
        WriteUInt32(stub, ref offset, 0x00C0AC40);
        stub[offset++] = 0x8B; stub[offset++] = 0x86;           // mov eax,[esi+0F0h]
        stub[offset++] = 0xF0; stub[offset++] = 0x00;
        stub[offset++] = 0x00; stub[offset++] = 0x00;
        stub[offset++] = 0xA3;                                  // mov [0xC0AC44],eax
        WriteUInt32(stub, ref offset, 0x00C0AC44);
        stub[offset++] = 0x8B; stub[offset++] = 0x86;           // mov eax,[esi+0F4h]
        stub[offset++] = 0xF4; stub[offset++] = 0x00;
        stub[offset++] = 0x00; stub[offset++] = 0x00;
        stub[offset++] = 0xA3;                                  // mov [0xC0AC48],eax
        WriteUInt32(stub, ref offset, 0x00C0AC48);
        stub[offset++] = 0x8B; stub[offset++] = 0x86;           // mov eax,[esi+0F8h]
        stub[offset++] = 0xF8; stub[offset++] = 0x00;
        stub[offset++] = 0x00; stub[offset++] = 0x00;
        stub[offset++] = 0xA3;                                  // mov [0xC0AC4C],eax
        WriteUInt32(stub, ref offset, 0x00C0AC4C);
        stub[offset++] = 0xE8;                                  // call 0x95B000
        WriteRelative(stub, ref offset, stubVa + 0x27 + 5, 0x0095B000);
        stub[offset++] = 0xE9;                                  // jmp 0x95B493
        WriteRelative(stub, ref offset, stubVa + 0x2C + 5, 0x0095B493);

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
        {
            WriteLine("漫画状态记录桩写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }
        var entry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, 0x0095B493, stubVa);
        if (!Patch(processHandle, 0x0095B48E, new byte[] { 0xE8, 0x6D, 0xFB, 0xFF, 0xFF }, entry))
            return false;
        WriteLine("漫画状态记录桩已安装: page=0x00C0AC44 count=0x00C0AC48");
        return true;
    }

    private static bool InstallComicOnePage(IntPtr processHandle)
    {
        // 漫画窗口初始化 0x95AAE0 用 00AD5D00 的返回值设置总页数
        // [esi+0F4h]。把 00AD5D00 改成直接返回 1，使漫画只有一页：
        // 第一页播完后按确认键即可走结束流程，避免卡在最后一页导致
        // 客户端超时退出。
        if (!Patch(processHandle, 0x00AD5D00,
            new byte[] { 0x8B, 0x41, 0x58, 0x2B, 0x41, 0x54 },
            new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }))
            return false;
        WriteLine("漫画单页补丁已安装: 00AD5D00 -> mov eax,1; ret");
        return true;
    }

    private static bool InstallComicSkip(IntPtr processHandle)
    {
        // 漫画结束消息 0x78(120) 在本客户端没有任何分发点消费（分发点只
        // 处理 0x4E=78），导致漫画永远卡在最后一页并触发客户端 2 分钟
        // 超时退出。直接让漫画窗口工厂 0x95AC40 返回 0（不创建漫画窗口），
        // 客户端应直接进入教程地图。
        if (!Patch(processHandle, 0x0095AC40,
            new byte[] { 0x56, 0x6A, 0x00, 0x6A, 0x00, 0x6A, 0xFF, 0x68 },
            new byte[] { 0x32, 0xC0, 0xC2, 0x08, 0x00, 0x90, 0x90, 0x90 }))
            return false;
        WriteLine("漫画跳过补丁已安装: 95AC40 -> xor al,al; ret 8");
        return true;
    }

    private static bool InstallFakeKeys(IntPtr processHandle)
    {
        // ARAD 用 DirectInput 独占模式读键盘，SendInput/PostMessage 均无法
        // 注入（GetAsyncKeyState 也为 0）。这里挂钩输入管理器的 IsKeyPressed
        // （0xAF5260），先查“伪按键表”（按 DIK 索引），命中直接返回按下，
        // 否则走原逻辑。外部控制器向伪按键表写 1/0 即可模拟方向键/攻击键。
        // 注意：0xC0A800-0xC0A960 是游戏真实代码，绝不能覆盖；这里用
        // VirtualAllocEx 在进程内分配独立 RWX 页放桩与伪按键表。
        var allocated = VirtualAllocEx(processHandle, IntPtr.Zero, 0x2000, 0x3000, 0x40);
        if (allocated == IntPtr.Zero)
        {
            WriteLine("伪按键钩子内存分配失败: " + Marshal.GetLastWin32Error());
            return false;
        }
        var stubVa = (uint)allocated.ToInt64();
        var fakeBase = stubVa + 0x100;
        IntPtr controlWritten;
        var controlBlock = new byte[8];
        Buffer.BlockCopy(BitConverter.GetBytes(fakeBase), 0, controlBlock, 0, 4);
        controlBlock[4] = (byte)'K';
        controlBlock[5] = (byte)'E';
        controlBlock[6] = (byte)'Y';
        controlBlock[7] = (byte)'S';
        if (!WriteProcessMemory(processHandle, new IntPtr(0x00C0AEF0),
                controlBlock, controlBlock.Length, out controlWritten))
        {
            WriteLine("伪按键控制表指针写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }
        var stub = new byte[84];
        var o = 0;
        stub[o++] = 0x55; stub[o++] = 0x8B; stub[o++] = 0xEC;          // push ebp; mov ebp,esp
        stub[o++] = 0x53;                                              // push ebx
        stub[o++] = 0x8B; stub[o++] = 0x45; stub[o++] = 0x08;          // mov eax,[ebp+8]
        stub[o++] = 0x85; stub[o++] = 0xC0;                            // test eax,eax
        stub[o++] = 0x78; stub[o++] = 0x42;                            // js fail
        stub[o++] = 0x83; stub[o++] = 0xF8; stub[o++] = 0x6B;          // cmp eax,6Bh
        stub[o++] = 0x7D; stub[o++] = 0x3D;                            // jge fail
        stub[o++] = 0x8A; stub[o++] = 0x51; stub[o++] = 0x0C;          // mov dl,[ecx+0Ch]
        stub[o++] = 0x84; stub[o++] = 0xD2;                            // test dl,dl
        stub[o++] = 0x74; stub[o++] = 0x36;                            // je fail
        stub[o++] = 0x8B; stub[o++] = 0x9C; stub[o++] = 0x81;          // mov ebx,[ecx+eax*4+0x310]
        stub[o++] = 0x10; stub[o++] = 0x03; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0x81; stub[o++] = 0xFB; stub[o++] = 0x00;          // cmp ebx,100h
        stub[o++] = 0x01; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0x73; stub[o++] = 0x27;                            // jae fail
        stub[o++] = 0x8A; stub[o++] = 0x83;                            // mov al,[ebx+fakeBase]
        WriteUInt32(stub, ref o, fakeBase);
        stub[o++] = 0x84; stub[o++] = 0xC0;                            // test al,al
        stub[o++] = 0x75; stub[o++] = 0x19;                            // jne success
        stub[o++] = 0x8A; stub[o++] = 0x94; stub[o++] = 0x0B;          // mov dl,[ecx+ebx+0x10D]
        stub[o++] = 0x0D; stub[o++] = 0x01; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0x84; stub[o++] = 0xD2;                            // test dl,dl
        stub[o++] = 0x74; stub[o++] = 0x12;                            // je fail
        stub[o++] = 0x8A; stub[o++] = 0x94; stub[o++] = 0x0B;          // mov dl,[ecx+ebx+0x10E]
        stub[o++] = 0x0E; stub[o++] = 0x01; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0x84; stub[o++] = 0xD2;                            // test dl,dl
        stub[o++] = 0x0F; stub[o++] = 0x94; stub[o++] = 0xC0;          // sete al
        stub[o++] = 0xEB; stub[o++] = 0x06;                            // jmp done
        stub[o++] = 0xB0; stub[o++] = 0x01;                            // success: mov al,1
        stub[o++] = 0xEB; stub[o++] = 0x02;                            // jmp done
        stub[o++] = 0x32; stub[o++] = 0xC0;                            // fail: xor al,al
        stub[o++] = 0x5B;                                              // done: pop ebx
        stub[o++] = 0x5D;                                              // pop ebp
        stub[o++] = 0xC2; stub[o++] = 0x04; stub[o++] = 0x00;          // ret 4

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
        {
            WriteLine("伪按键钩子写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }
        var entry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, 0x00AF5265, stubVa);
        if (!Patch(processHandle, 0x00AF5260, new byte[] { 0x55, 0x8B, 0xEC, 0x8B, 0x45 }, entry))
            return false;
        // 战斗/移动走 IsKeyDown（0xAF58D0）与边缘检测（0xAF5900），同样加伪按键检查。
        InstallFakeKeysIsDown(processHandle, 0x00AF58D0, stubVa + 0x180, fakeBase);
        // Town movement queries the input-manager vtable at +0x18, whose
        // concrete implementation in this client is 0xAD8FF0. Hook that real
        // held-state path so the diagnostic key test can prove movement.
        InstallFakeKeysHeldState(processHandle, 0x00AD8FF0, stubVa + 0x280, fakeBase);
        // Keep the direct-DIK helper hooks for combat/UI diagnostics.
        InstallFakeKeysIsDown(processHandle, 0x00AF58D0, stubVa + 0x180, fakeBase);
        InstallFakeKeysIsDown(processHandle, 0x00AF5900, stubVa + 0x200, fakeBase);
        WriteLine("伪按键钩子已安装: fake=0x" + fakeBase.ToString("X8"));
        return true;
    }

    private static bool InstallFakeKeysIsDown(IntPtr processHandle, uint targetVa, uint stubVa, uint fakeBase)
    {
        // IsKeyDown/edge：key=arg，先查伪按键表（按映射后的码），命中返回 1，
        // 否则走原逻辑读 [obj+0x710+key]（IsKeyDown）或边缘检测。
        var stub = new byte[80];
        var o = 0;
        stub[o++] = 0x55; stub[o++] = 0x8B; stub[o++] = 0xEC;          // push ebp; mov ebp,esp
        stub[o++] = 0x53;                                              // push ebx
        stub[o++] = 0x8B; stub[o++] = 0x45; stub[o++] = 0x08;          // mov eax,[ebp+8]
        stub[o++] = 0x85; stub[o++] = 0xC0;                            // test eax,eax
        stub[o++] = 0x78; stub[o++] = 0x32;                            // js fail
        stub[o++] = 0x83; stub[o++] = 0xF8; stub[o++] = 0x6B;          // cmp eax,6Bh
        stub[o++] = 0x7D; stub[o++] = 0x2D;                            // jge fail
        stub[o++] = 0x8A; stub[o++] = 0x51; stub[o++] = 0x0C;          // mov dl,[ecx+0Ch]
        stub[o++] = 0x84; stub[o++] = 0xD2;                            // test dl,dl
        stub[o++] = 0x74; stub[o++] = 0x26;                            // je fail
        stub[o++] = 0x8B; stub[o++] = 0x9C; stub[o++] = 0x81;          // mov ebx,[ecx+eax*4+0x310]
        stub[o++] = 0x10; stub[o++] = 0x03; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0x81; stub[o++] = 0xFB; stub[o++] = 0x00;          // cmp ebx,100h
        stub[o++] = 0x01; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0x73; stub[o++] = 0x17;                            // jae fail
        stub[o++] = 0x8A; stub[o++] = 0x83;                            // mov al,[ebx+fakeBase]
        WriteUInt32(stub, ref o, fakeBase);
        stub[o++] = 0x84; stub[o++] = 0xC0;                            // test al,al
        stub[o++] = 0x75; stub[o++] = 0x09;                            // jne success
        stub[o++] = 0x8A; stub[o++] = 0x84; stub[o++] = 0x01;          // mov al,[ecx+eax+0x710] (original)
        stub[o++] = 0x10; stub[o++] = 0x07; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0xEB; stub[o++] = 0x06;                            // jmp done
        stub[o++] = 0xB0; stub[o++] = 0x01;                            // success: mov al,1
        stub[o++] = 0xEB; stub[o++] = 0x02;                            // jmp done
        stub[o++] = 0x32; stub[o++] = 0xC0;                            // fail: xor al,al
        stub[o++] = 0x5B;                                              // done: pop ebx
        stub[o++] = 0x5D;                                              // pop ebp
        stub[o++] = 0xC2; stub[o++] = 0x04; stub[o++] = 0x00;          // ret 4

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
            return false;
        var entry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, targetVa + 5, stubVa);
        return Patch(processHandle, targetVa, new byte[] { 0x55, 0x8B, 0xEC, 0x8B, 0x45 }, entry);
    }

    private static bool InstallFakeKeysHeldState(IntPtr processHandle, uint targetVa, uint stubVa, uint fakeBase)
    {
        // Reproduce 0xAD8FF0 when no fake key is set: map action to DIK with
        // [ecx+action*4+0x310], then read [ecx+DIK+0x10D].
        var stub = new byte[80];
        var o = 0;
        stub[o++] = 0x55; stub[o++] = 0x8B; stub[o++] = 0xEC;
        stub[o++] = 0x53;
        stub[o++] = 0x8B; stub[o++] = 0x45; stub[o++] = 0x08;
        stub[o++] = 0x85; stub[o++] = 0xC0;
        stub[o++] = 0x78; stub[o++] = 0x32;
        stub[o++] = 0x83; stub[o++] = 0xF8; stub[o++] = 0x6B;
        stub[o++] = 0x7D; stub[o++] = 0x2D;
        stub[o++] = 0x8A; stub[o++] = 0x51; stub[o++] = 0x0C;
        stub[o++] = 0x84; stub[o++] = 0xD2;
        stub[o++] = 0x74; stub[o++] = 0x26;
        stub[o++] = 0x8B; stub[o++] = 0x9C; stub[o++] = 0x81;
        stub[o++] = 0x10; stub[o++] = 0x03; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0x81; stub[o++] = 0xFB; stub[o++] = 0x00;
        stub[o++] = 0x01; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0x73; stub[o++] = 0x17;
        stub[o++] = 0x8A; stub[o++] = 0x83;
        WriteUInt32(stub, ref o, fakeBase);
        stub[o++] = 0x84; stub[o++] = 0xC0;
        stub[o++] = 0x75; stub[o++] = 0x09;
        stub[o++] = 0x8A; stub[o++] = 0x84; stub[o++] = 0x0B;
        stub[o++] = 0x0D; stub[o++] = 0x01; stub[o++] = 0x00; stub[o++] = 0x00;
        stub[o++] = 0xEB; stub[o++] = 0x06;
        stub[o++] = 0xB0; stub[o++] = 0x01;
        stub[o++] = 0xEB; stub[o++] = 0x02;
        stub[o++] = 0x32; stub[o++] = 0xC0;
        stub[o++] = 0x5B;
        stub[o++] = 0x5D;
        stub[o++] = 0xC2; stub[o++] = 0x04; stub[o++] = 0x00;

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
            return false;
        var entry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, targetVa + 5, stubVa);
        return Patch(processHandle, targetVa, new byte[] { 0x55, 0x8B, 0xEC, 0x8B, 0x45 }, entry);
    }

    private static bool InstallRoomTrace(IntPtr processHandle)
    {
        // 通知 29 handler 内：
        //   00427CB7 mov [ebp-20ECh],eax   ; dungeon 实例
        //   00427CD8 mov [ebp-20E0h],eax   ; 房间对象
        // 分别把结果记录到 0xC0AC10 / 0xC0AC14，再跳回后续指令。
        const uint dungeonStubVa = 0x00C0AB40;
        const uint roomStubVa = 0x00C0AB60;

        var dungeonStub = new byte[16];
        var offset = 0;
        dungeonStub[offset++] = 0x89; dungeonStub[offset++] = 0x85;   // mov [ebp-20ECh],eax
        dungeonStub[offset++] = 0x14; dungeonStub[offset++] = 0xDF;
        dungeonStub[offset++] = 0xFF; dungeonStub[offset++] = 0xFF;
        dungeonStub[offset++] = 0xA3;                                  // mov [0xC0AC10],eax
        WriteUInt32(dungeonStub, ref offset, 0x00C0AC10);
        dungeonStub[offset++] = 0xE9;                                  // jmp 0x427CBD
        WriteRelative(dungeonStub, ref offset, dungeonStubVa + 16, 0x00427CBD);

        var roomStub = new byte[16];
        offset = 0;
        roomStub[offset++] = 0x89; roomStub[offset++] = 0x85;          // mov [ebp-20E0h],eax
        roomStub[offset++] = 0x20; roomStub[offset++] = 0xDF;
        roomStub[offset++] = 0xFF; roomStub[offset++] = 0xFF;
        roomStub[offset++] = 0xA3;                                     // mov [0xC0AC14],eax
        WriteUInt32(roomStub, ref offset, 0x00C0AC14);
        roomStub[offset++] = 0xE9;                                     // jmp 0x427CDE
        WriteRelative(roomStub, ref offset, roomStubVa + 16, 0x00427CDE);

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)dungeonStubVa), dungeonStub, dungeonStub.Length, out bytesWritten) ||
            !WriteProcessMemory(processHandle, new IntPtr((long)roomStubVa), roomStub, roomStub.Length, out bytesWritten))
        {
            WriteLine("房间记录桩写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        var dungeonEntry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00, 0x90 };
        var dOff = 1;
        WriteRelative(dungeonEntry, ref dOff, 0x00427CBC, dungeonStubVa);
        if (!Patch(processHandle, 0x00427CB7, new byte[] { 0x89, 0x85, 0x14, 0xDF, 0xFF, 0xFF }, dungeonEntry))
            return false;

        var roomEntry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00, 0x90 };
        var rOff = 1;
        WriteRelative(roomEntry, ref rOff, 0x00427CDD, roomStubVa);
        if (!Patch(processHandle, 0x00427CD8, new byte[] { 0x89, 0x85, 0x20, 0xDF, 0xFF, 0xFF }, roomEntry))
            return false;
        WriteLine("房间/副本实例记录桩已安装: dungeon=0x00C0AC10 room=0x00C0AC14");
        return true;
    }

    private static bool InstallMapTrace(IntPtr processHandle)
    {
        // 通知 29（START_MAP）handler 中：
        //   00427D9D call 00A727C0h        ; 按 mapId 加载地图对象
        //   00427DA2 mov [ebp-20F4h],eax   ; 保存返回值
        // 把 mov 改成跳到记录桩：保存返回值到 0xC0AC00，mapId 到 0xC0AC08，
        // 再跳回 0x427DA8 继续。
        const uint stubVa = 0x00C0AB00;
        var stub = new byte[28];
        var offset = 0;
        stub[offset++] = 0x89; stub[offset++] = 0x85;           // mov [ebp-20F4h],eax
        stub[offset++] = 0x0C; stub[offset++] = 0xDF;
        stub[offset++] = 0xFF; stub[offset++] = 0xFF;
        stub[offset++] = 0xA3;                                   // mov [0xC0AC00],eax
        WriteUInt32(stub, ref offset, 0x00C0AC00);
        stub[offset++] = 0x0F; stub[offset++] = 0xB7;            // movzx eax,word [ebp-20F8h]
        stub[offset++] = 0x85; stub[offset++] = 0x08;
        stub[offset++] = 0xDF; stub[offset++] = 0xFF;
        stub[offset++] = 0xFF;
        stub[offset++] = 0xA3;                                   // mov [0xC0AC08],eax
        WriteUInt32(stub, ref offset, 0x00C0AC08);
        stub[offset++] = 0xE9;                                   // jmp 0x427DA8
        WriteRelative(stub, ref offset, stubVa + 28, 0x00427DA8);

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
        {
            WriteLine("地图记录桩写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }
        var entry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00, 0x90 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, 0x00427DA7, stubVa);
        var original = new byte[] { 0x89, 0x85, 0x0C, 0xDF, 0xFF, 0xFF };
        if (!Patch(processHandle, 0x00427DA2, original, entry))
            return false;
        WriteLine("地图加载记录桩已安装: result=0x00C0AC00 mapId=0x00C0AC08");
        return true;
    }

    private static void WriteUInt32(byte[] buffer, ref int offset, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        buffer[offset++] = bytes[0];
        buffer[offset++] = bytes[1];
        buffer[offset++] = bytes[2];
        buffer[offset++] = bytes[3];
    }

    private static bool InstallChannelNullGuard(IntPtr processHandle)
    {
        // 崩溃点 0xAAED3C 是 mov eax,[ebx+104h]，ebx 来自全局频道表
        // 0xF9FF30。ARAD 在网络会话未完成时该表项为 0，导致空指针崩溃。
        // 这里在代码洞 0xBB5620 放一个保护桩：
        //   1) ebx != 0 时走原逻辑（检查 [ebx+104h] 后跳回 0xAAED4A）；
        //   2) ebx == 0 时先记录调用方/this 到 0xC0AC30-0xC0AC38，
        //      再扫描频道表找一个有效通道对象顶替（避免直接返回导致
        //      后续教程流程异常退出），找不到才走安全返回 0xAAF005。
        const uint stubVa = 0x00BB5620;
        const uint safeReturnVa = 0x00AAF005;
        const uint continueVa = 0x00AAED4A;
        var stub = new byte[96];
        var offset = 0;

        stub[offset++] = 0x8B; stub[offset++] = 0xD9;          // mov ebx,ecx
        stub[offset++] = 0x85; stub[offset++] = 0xDB;          // test ebx,ebx
        stub[offset++] = 0x75; stub[offset++] = 0x39;          // jne +0x39 -> shared check (0x3F)
        stub[offset++] = 0xA1;                                  // mov eax,[0xC0AC38]
        WriteUInt32(stub, ref offset, 0x00C0AC38);
        stub[offset++] = 0x40;                                  // inc eax
        stub[offset++] = 0xA3;                                  // mov [0xC0AC38],eax
        WriteUInt32(stub, ref offset, 0x00C0AC38);
        stub[offset++] = 0x8B; stub[offset++] = 0x04; stub[offset++] = 0x24; // mov eax,[esp]
        stub[offset++] = 0xA3;                                  // mov [0xC0AC30],eax
        WriteUInt32(stub, ref offset, 0x00C0AC30);
        stub[offset++] = 0x89; stub[offset++] = 0x0D;           // mov [0xC0AC34],ecx
        WriteUInt32(stub, ref offset, 0x00C0AC34);
        stub[offset++] = 0xB8;                                  // mov eax,0xF9FF30
        WriteUInt32(stub, ref offset, 0x00F9FF30);
        stub[offset++] = 0xBA; stub[offset++] = 0x10;           // mov edx,16
        stub[offset++] = 0x00; stub[offset++] = 0x00;
        stub[offset++] = 0x00;
        stub[offset++] = 0x8B; stub[offset++] = 0x08;           // scan: mov ecx,[eax]
        stub[offset++] = 0x85; stub[offset++] = 0xC9;           // test ecx,ecx
        stub[offset++] = 0x75; stub[offset++] = 0x06;           // jne +6 -> found
        stub[offset++] = 0x83; stub[offset++] = 0xC0; stub[offset++] = 0x04; // add eax,4
        stub[offset++] = 0x4A;                                  // dec edx
        stub[offset++] = 0x75; stub[offset++] = 0xF4;           // jne -12 -> scan
        stub[offset++] = 0x8B; stub[offset++] = 0xD9;           // found: mov ebx,ecx
        stub[offset++] = 0x85; stub[offset++] = 0xDB;           // test ebx,ebx
        stub[offset++] = 0x0F; stub[offset++] = 0x84;           // je near safeReturn
        WriteRelative(stub, ref offset, stubVa + 0x3F, safeReturnVa);
        stub[offset++] = 0x8B; stub[offset++] = 0x83;           // shared: mov eax,[ebx+104h]
        stub[offset++] = 0x04; stub[offset++] = 0x01;
        stub[offset++] = 0x00; stub[offset++] = 0x00;
        stub[offset++] = 0x85; stub[offset++] = 0xC0;           // test eax,eax
        stub[offset++] = 0x0F; stub[offset++] = 0x84;           // je near safeReturn
        WriteRelative(stub, ref offset, stubVa + 0x4D, safeReturnVa);
        stub[offset++] = 0xE9;                                  // jmp near continueVa
        WriteRelative(stub, ref offset, stubVa + 0x52, continueVa);

        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)stubVa), stub, stub.Length, out bytesWritten))
        {
            WriteLine("空指针保护桩写入失败: " + Marshal.GetLastWin32Error());
            return false;
        }

        var original = new byte[] { 0x8B, 0xD9, 0x8B, 0x83, 0x04 };
        var entry = new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 };
        var entryOffset = 1;
        WriteRelative(entry, ref entryOffset, 0x00AAED3F, stubVa);
        return Patch(processHandle, 0x00AAED3A, original, entry);
    }

    private static bool InstallTownSceneGuard(IntPtr processHandle, int guardStage)
    {
        // Entering town renders correctly, but the story-mode scene update
        // reads [0xF9FCD8] as a pointer.  The story handle store (0x43EC90)
        // leaves a small 15-bit handle there, so the dereference at 0xABD896
        // (mov eax,[eax+2040h]) faults ~1.4s after town entry.  Skip the
        // story update when the value is not a real pointer (< 0x10000).
        const uint crashVa = 0x00ABD896;
        const uint continueVa = 0x00ABD8A6;
        const uint epilogueVa = 0x00ABDA43;
        var current = new byte[6];
        IntPtr read;
        if (!ReadProcessMemory(processHandle, new IntPtr((long)crashVa), current, 6, out read))
            return false;
        if (current[0] != 0x8B || current[1] != 0x80 || current[2] != 0x40 || current[3] != 0x20)
            return false;

        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;
        var stubVa = (uint)alloc.ToInt64();
        var stub = new byte[64];
        var o = 0;
        stub[o++] = 0x3D; stub[o++] = 0x00; stub[o++] = 0x00; stub[o++] = 0x01; stub[o++] = 0x00; // cmp eax,10000h
        stub[o++] = 0x72; stub[o++] = 0x15;                                                   // jb safe
        stub[o++] = 0x8B; stub[o++] = 0x80; stub[o++] = 0x40; stub[o++] = 0x20; stub[o++] = 0x00; stub[o++] = 0x00; // mov eax,[eax+2040h]
        stub[o++] = 0x8B; stub[o++] = 0x08;                                                   // mov ecx,[eax]
        stub[o++] = 0x8D; stub[o++] = 0x55; stub[o++] = 0xFC;                                 // lea edx,[ebp-4]
        stub[o++] = 0x52;                                                                    // push edx
        stub[o++] = 0x50;                                                                    // push eax
        stub[o++] = 0xFF; stub[o++] = 0x51; stub[o++] = 0x0C;                                // call dword [ecx+0Ch]
        stub[o++] = 0xE9;                                                                    // jmp continue
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, continueVa);
        stub[o++] = 0xE9;                                                                    // safe: jmp epilogue
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, epilogueVa);

        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, o, out written))
            return false;

        var entry = new byte[6];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, crashVa + 5, stubVa);
        entry[5] = 0x90;
        var ok = Patch(processHandle, crashVa, new byte[] { 0x8B, 0x80, 0x40, 0x20, 0x00, 0x00 }, entry);
        if (ok)
            WriteLine("城镇故事场景守卫已安装: 0xABD896 -> stub=0x" + stubVa.ToString("X8"));

        // The pointer check above is the only default town patch. It guards the
        // observed 1.2-second delayed fault without changing any world-render
        // function. The remaining historical workarounds are available only via
        // --town-guards for diagnostics because several suppress visible layers.
        if (guardStage < 1)
            return ok;

        // Mode A: the channel/session object's +0x70C component is null when
        // the town scene was not fully created; SetFlag(0x4A8B70) then faults
        // at 0x4A8B8E.  Skip the SetFlag call when [esi+70C] == 0.
        const uint flagVa = 0x004A2C0E;
        const uint flagContinueVa = 0x004A2C1E;
        var flagCurrent = new byte[6];
        IntPtr flagRead;
        if (!ReadProcessMemory(processHandle, new IntPtr((long)flagVa), flagCurrent, 6, out flagRead))
            return ok;
        if (flagCurrent[0] != 0x8B || flagCurrent[1] != 0x8E || flagCurrent[2] != 0x0C || flagCurrent[3] != 0x07)
            return ok;

        var flagAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (flagAlloc == IntPtr.Zero)
            return ok;
        var flagStubVa = (uint)flagAlloc.ToInt64();
        var flagStub = new byte[48];
        var fo = 0;
        flagStub[fo++] = 0x8B; flagStub[fo++] = 0x8E; flagStub[fo++] = 0x0C; flagStub[fo++] = 0x07; flagStub[fo++] = 0x00; flagStub[fo++] = 0x00; // mov ecx,[esi+70Ch]
        flagStub[fo++] = 0x85; flagStub[fo++] = 0xC9;                                                   // test ecx,ecx
        flagStub[fo++] = 0x74; var flagNullJump = fo++;                                                  // jz cleanup
        flagStub[fo++] = 0x68; flagStub[fo++] = 0x00; flagStub[fo++] = 0x00; flagStub[fo++] = 0x00; flagStub[fo++] = 0xFF; // push 0FF000000h
        flagStub[fo++] = 0xE8;                                                                           // call 0x4A8B70
        WriteRelative(flagStub, ref fo, flagStubVa + (uint)fo + 4, 0x004A8B70u);
        flagStub[fo++] = 0xE9;                                                                           // valid: continue
        WriteRelative(flagStub, ref fo, flagStubVa + (uint)fo + 4, flagContinueVa);
        var flagCleanupOffset = fo;
        // The original caller has already pushed its first color argument at
        // 0x4A2BED/0x4A2C09. If the component is null and SetFlag is skipped,
        // discard that argument exactly as the original stdcall callee would.
        flagStub[fo++] = 0x83; flagStub[fo++] = 0xC4; flagStub[fo++] = 0x04;                              // add esp,4
        flagStub[fo++] = 0xE9;                                                                           // null: continue
        WriteRelative(flagStub, ref fo, flagStubVa + (uint)fo + 4, flagContinueVa);
        flagStub[flagNullJump] = checked((byte)(flagCleanupOffset - (flagNullJump + 1)));

        IntPtr flagWritten;
        if (!WriteProcessMemory(processHandle, flagAlloc, flagStub, fo, out flagWritten))
            return ok;
        var flagEntry = new byte[6];
        flagEntry[0] = 0xE9;
        var feo = 1;
        WriteRelative(flagEntry, ref feo, flagVa + 5, flagStubVa);
        flagEntry[5] = 0x90;
        var flagOk = Patch(processHandle, flagVa, new byte[] { 0x8B, 0x8E, 0x0C, 0x07, 0x00, 0x00 }, flagEntry);
        if (flagOk)
            WriteLine("城镇旗标守卫已安装: 0x4A2C0E -> stub=0x" + flagStubVa.ToString("X8"));

        if (guardStage < 2)
            return ok && flagOk;

        // The story transition update 0x43EA60 overwrites the session globals
        // (0xF9A598/0xF9FCD8/0xF9FCD4/0xECEC8C) with small 15-bit handles while
        // the town scene is loading.  The town update loop then dereferences
        // them as pointers and faults.  NOP the four handle stores so the
        // globals keep their real object pointers.
        var handleOk = true;
        handleOk &= Patch(processHandle, 0x0043EC95, new byte[] { 0xA3, 0x98, 0xA5, 0xF9, 0x00 }, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
        handleOk &= Patch(processHandle, 0x0043ECA1, new byte[] { 0xA3, 0xD8, 0xFC, 0xF9, 0x00 }, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
        handleOk &= Patch(processHandle, 0x0043ECAD, new byte[] { 0xA3, 0xD4, 0xFC, 0xF9, 0x00 }, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
        handleOk &= Patch(processHandle, 0x0043ECB9, new byte[] { 0xA3, 0x8C, 0xEC, 0xEC, 0x00 }, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
        if (handleOk)
            WriteLine("故事句柄覆盖已禁用: 0x43EC95/ECA1/ECAD/ECB9");

        if (guardStage < 3)
            return ok && flagOk && handleOk;

        // The story update calls the tiny getter 0x43D7F0 (mov al,[ecx+8])
        // with [0xF9A598], which is freed during town entry (0xCDCDCDCD).
        // Return 0 unless ecx is a plausible heap pointer (0x10000..0x3FFFFFFF).
        var getterOk = Patch(processHandle, 0x0043D7F0,
            new byte[] { 0x8A, 0x41, 0x08, 0xC3 },
            new byte[] {
                0x81, 0xF9, 0x00, 0x00, 0x01, 0x00,       // cmp ecx,10000h
                0x72, 0x0C,                               // jb zero
                0x81, 0xF9, 0x00, 0x00, 0x00, 0x40,       // cmp ecx,40000000h
                0x73, 0x06,                               // jae zero
                0x8A, 0x41, 0x08,                         // mov al,[ecx+8]
                0xC3,                                     // ret
                0x32, 0xC0,                               // zero: xor al,al
                0xC3                                      // ret
            });
        if (getterOk)
            WriteLine("故事状态 getter 已加固: 0x43D7F0");

        if (guardStage < 4)
            return ok && flagOk && handleOk && getterOk;

        // 0xA6F89D marks [0xF9A598] as freed (0xCDCDCDCD) when the story
        // manager is released.  The object itself is not freed, so keeping the
        // pointer lets the town/story update continue without a use-after-free.
        var markerOk = Patch(processHandle, 0x00A6F89D,
            new byte[] { 0xC7, 0x05, 0x98, 0xA5, 0xF9, 0x00, 0xCD, 0xCD, 0xCD, 0xCD },
            new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
        if (markerOk)
            WriteLine("故事管理器释放标记已禁用: 0xA6F89D");

        if (guardStage < 5)
            return ok && flagOk && handleOk && getterOk && markerOk;

        // Do not disable 0x8B3DB0: it is the complete in-town scene update,
        // and returning from its prologue removes the map, actors and monsters
        // even though the server has sent them. Bypass only the two unsafe
        // story-manager subsections and resume the original main update path.
        var sceneOk = Patch(processHandle, 0x008B3DD4,
            new byte[] { 0x8A, 0x50, 0x08, 0x84, 0xD2, 0x0F },
            // next EIP 0x8B3DD9 + 0x17 = 0x8B3DF0
            new byte[] { 0xE9, 0x17, 0x00, 0x00, 0x00, 0x90 });
        sceneOk &= Patch(processHandle, 0x008B3DF0,
            new byte[] { 0x8A, 0x86, 0xF8, 0x01, 0x00, 0x00 },
            // next EIP 0x8B3DF5 + 0x4F = 0x8B3E44
            new byte[] { 0xE9, 0x4F, 0x00, 0x00, 0x00, 0x90 });
        if (sceneOk)
            WriteLine("城镇场景更新已保留，危险故事段已跳过: 0x8B3DD4/0x8B3DF0");

        if (guardStage < 6)
            return ok && flagOk && handleOk && getterOk && markerOk && sceneOk;

        // Keep the complete effect-update function. Only validate each node in
        // its third virtual-call pass; corrupt entries are skipped while every
        // valid character/map effect continues through the original method.
        var effectOk = InstallTownEffectNodeGuard(processHandle);
        if (effectOk)
            WriteLine("角色特效损坏节点守卫已安装: 0x508960（保留完整特效循环）");
        return ok && flagOk && handleOk && getterOk && markerOk && sceneOk && effectOk;
    }

    private static bool InstallTownEffectNodeGuard(IntPtr processHandle)
    {
        // Original loop at 0x508960:
        //   mov ecx,[ebx]; lea eax,[ecx+edi*4]; mov ecx,[eax]
        //   mov edx,[ecx]; call dword [edx+18h]
        // A stale list entry observed during town activation supplied a vtable
        // below the image (0x1D0FFF). Validate the object, vtable and call target
        // and skip only that node. ARAD.exe .text is 0x401000..0xC0B000 and its
        // vtables live in the following 0xC0B000..0xE19000 section.
        const uint hookVa = 0x00508960;
        const uint resumeVa = 0x0050896C;
        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;

        var stubVa = (uint)alloc.ToInt64();
        var stub = new byte[96];
        var o = 0;
        var shortJumpOffsets = new List<int>();
        stub[o++] = 0x8B; stub[o++] = 0x0B;                                      // mov ecx,[ebx]
        stub[o++] = 0x8D; stub[o++] = 0x04; stub[o++] = 0xB9;                    // lea eax,[ecx+edi*4]
        stub[o++] = 0x8B; stub[o++] = 0x08;                                      // mov ecx,[eax]
        stub[o++] = 0x81; stub[o++] = 0xF9;                                      // cmp ecx,10000h
        WriteUInt32(stub, ref o, 0x00010000u);
        stub[o++] = 0x72; shortJumpOffsets.Add(o++);                              // jb skip
        stub[o++] = 0x81; stub[o++] = 0xF9;                                      // cmp ecx,7FFF0000h
        WriteUInt32(stub, ref o, 0x7FFF0000u);
        stub[o++] = 0x73; shortJumpOffsets.Add(o++);                              // jae skip
        stub[o++] = 0x8B; stub[o++] = 0x11;                                      // mov edx,[ecx]
        stub[o++] = 0x81; stub[o++] = 0xFA;                                      // cmp edx,C0B000h
        WriteUInt32(stub, ref o, 0x00C0B000u);
        stub[o++] = 0x72; shortJumpOffsets.Add(o++);                              // jb skip
        stub[o++] = 0x81; stub[o++] = 0xFA;                                      // cmp edx,E18FE4h
        WriteUInt32(stub, ref o, 0x00E18FE4u);
        stub[o++] = 0x73; shortJumpOffsets.Add(o++);                              // jae skip
        stub[o++] = 0x8B; stub[o++] = 0x42; stub[o++] = 0x18;                    // mov eax,[edx+18h]
        stub[o++] = 0x3D; WriteUInt32(stub, ref o, 0x00401000u);                  // cmp eax,401000h
        stub[o++] = 0x72; shortJumpOffsets.Add(o++);                              // jb skip
        stub[o++] = 0x3D; WriteUInt32(stub, ref o, 0x00C0B000u);                  // cmp eax,C0B000h
        stub[o++] = 0x73; shortJumpOffsets.Add(o++);                              // jae skip
        stub[o++] = 0xFF; stub[o++] = 0xD0;                                      // call eax
        stub[o++] = 0xE9;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);               // resume valid node
        var skipOffset = o;
        stub[o++] = 0xE9;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);               // resume corrupt node
        foreach (var jumpOffset in shortJumpOffsets)
            stub[jumpOffset] = checked((byte)(skipOffset - (jumpOffset + 1)));

        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, o, out written))
            return false;

        var entry = new byte[5];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, hookVa + 5, stubVa);
        return Patch(processHandle, hookVa,
            new byte[] { 0x8B, 0x0B, 0x8D, 0x04, 0xB9 }, entry);
    }

    private static bool InstallTownEffectPass1NodeGuard(IntPtr processHandle)
    {
        // The first pass can contain an already-destroyed entry whose object
        // header/vtable has been overwritten with 1. Preserve the pass and skip
        // only entries without an ARAD vtable and executable virtual target.
        const uint hookVa = 0x00508910;
        const uint resumeVa = 0x00508920;
        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;
        var stubVa = (uint)alloc.ToInt64();
        var stub = new byte[96];
        var o = 0;
        var shortJumpOffsets = new List<int>();
        stub[o++] = 0x8B; stub[o++] = 0x4D; stub[o++] = 0xFC;                    // mov ecx,[ebp-4]
        stub[o++] = 0x8B; stub[o++] = 0x11;                                      // mov edx,[ecx]
        stub[o++] = 0x8B; stub[o++] = 0x0C; stub[o++] = 0xBA;                    // mov ecx,[edx+edi*4]
        stub[o++] = 0x89; stub[o++] = 0x0D; WriteUInt32(stub, ref o, 0x00C0AE00u); // trace object
        stub[o++] = 0x89; stub[o++] = 0x3D; WriteUInt32(stub, ref o, 0x00C0AE0Cu); // trace index
        stub[o++] = 0x81; stub[o++] = 0xF9; WriteUInt32(stub, ref o, 0x00010000u); // cmp ecx,10000h
        stub[o++] = 0x72; shortJumpOffsets.Add(o++);
        stub[o++] = 0x81; stub[o++] = 0xF9; WriteUInt32(stub, ref o, 0x7FFF0000u); // cmp ecx,7FFF0000h
        stub[o++] = 0x73; shortJumpOffsets.Add(o++);
        stub[o++] = 0x8B; stub[o++] = 0x01;                                      // mov eax,[ecx]
        stub[o++] = 0xA3; WriteUInt32(stub, ref o, 0x00C0AE04u);                  // trace vtable
        stub[o++] = 0x3D; WriteUInt32(stub, ref o, 0x00C0B000u);                 // cmp eax,C0B000h
        stub[o++] = 0x72; shortJumpOffsets.Add(o++);
        stub[o++] = 0x3D; WriteUInt32(stub, ref o, 0x00E18FE4u);                 // cmp eax,E18FE4h
        stub[o++] = 0x73; shortJumpOffsets.Add(o++);
        stub[o++] = 0x8B; stub[o++] = 0x50; stub[o++] = 0x14;                    // mov edx,[eax+14h]
        stub[o++] = 0x89; stub[o++] = 0x15; WriteUInt32(stub, ref o, 0x00C0AE08u); // trace target
        stub[o++] = 0x81; stub[o++] = 0xFA; WriteUInt32(stub, ref o, 0x00401000u); // cmp edx,401000h
        stub[o++] = 0x72; shortJumpOffsets.Add(o++);
        stub[o++] = 0x81; stub[o++] = 0xFA; WriteUInt32(stub, ref o, 0x00C0B000u); // cmp edx,C0B000h
        stub[o++] = 0x73; shortJumpOffsets.Add(o++);
        stub[o++] = 0xFF; stub[o++] = 0xD2;                                      // call edx
        stub[o++] = 0xE9;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);
        var skipOffset = o;
        stub[o++] = 0xE9;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);
        foreach (var jumpOffset in shortJumpOffsets)
            stub[jumpOffset] = checked((byte)(skipOffset - (jumpOffset + 1)));
        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, o, out written))
            return false;
        var entry = new byte[5];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, hookVa + 5, stubVa);
        var ok = Patch(processHandle, hookVa,
            new byte[] { 0x8B, 0x4D, 0xFC, 0x8B, 0x11 }, entry);
        if (ok)
            WriteLine("角色特效第一遍损坏节点守卫已安装: 0x508910");
        return ok;
    }

    private static bool InstallStartMapRoomResetGuard(IntPtr processHandle)
    {
        // START_MAP 处理器 0x427BBA 在收到通知 29 时：
        //   0x427BD0 push 0
        //   0x427BD2 mov ecx,[0xECECBC]
        //   0x427BD8 call 0x40E420        ; eax = [ecx+15DCh] 房间对象
        //   0x427BDD mov ecx,eax
        //   0x427BDF call 0x009DCFA0     ; 房间初始化（入口检查 [ecx+414h]）
        // 0x009DCFA0 开头 mov al,[esi+414h]; test al,al; jne 0x009DD2A1，
        // +414h 非 0 时跳过整个初始化。若标志残留，内置格拉卡默认房间不会
        // 被重置，服务器房间对象被叠加在其上。
        // 该守卫把 0x427BDF 的 call 改跳到一个桩：先记录诊断（房间对象、
        // +414h 标志），再清零 [ecx+414h]，调用原 0x009DCFA0，然后回到
        // 0x427BE4 继续。只影响 START_MAP 路径。
        const uint hookVa = 0x00427BDF;
        const uint roomInitVa = 0x009DCFA0;
        const uint resumeVa = 0x00427BE4;
        const uint traceSlot = 0x00C0B100;
        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;
        var stubVa = (uint)alloc.ToInt64();
        var stub = new byte[0x40];
        var o = 0;
        stub[o++] = 0xA1; WriteUInt32(stub, ref o, traceSlot);                 // mov eax,[traceSlot]
        stub[o++] = 0x40;                                                       // inc eax
        stub[o++] = 0xA3; WriteUInt32(stub, ref o, traceSlot);                 // mov [traceSlot],eax
        stub[o++] = 0x89; stub[o++] = 0x0D; WriteUInt32(stub, ref o, traceSlot + 4); // mov [traceSlot+4],ecx
        stub[o++] = 0x8A; stub[o++] = 0x81; stub[o++] = 0x14; stub[o++] = 0x04; stub[o++] = 0x00; stub[o++] = 0x00; // mov al,[ecx+414h]
        stub[o++] = 0xA2; WriteUInt32(stub, ref o, traceSlot + 8);             // mov [traceSlot+8],al
        stub[o++] = 0xC6; stub[o++] = 0x41; stub[o++] = 0x14; stub[o++] = 0x00; // mov byte [ecx+414h],0
        stub[o++] = 0xE8;                                                       // call roomInitVa
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, roomInitVa);
        stub[o++] = 0xE9;                                                       // jmp resumeVa
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);
        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, o, out written))
            return false;
        var entry = new byte[5];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, hookVa + 5, stubVa);
        var ok = Patch(processHandle, hookVa,
            new byte[] { 0xE8, 0xBC, 0x53, 0x5B, 0x00 }, entry);
        if (ok)
            WriteLine("START_MAP 房间重置守卫已安装: 0x427BDF（先清 +414h 再初始化房间），诊断槽=0x" + traceSlot.ToString("X8"));
        return ok;
    }

    private static bool InstallStartMapMapLoaderTrace(IntPtr processHandle)
    {
        // START_MAP 处理器在 0x427D9D 调用地图加载器 0xA727C0（参数=mapId）。
        // 把该 call 改跳到一个桩：记录 mapId 与返回的地图句柄到诊断槽
        // 0xC0B10C/0xC0B110，再调用原加载器并回到 0x427DA2。用于确认客户端
        // 是否真的按 START_MAP 的 mapId 加载地图（若句柄为 0/固定默认对象，
        // 就是"大场景加载成格拉卡"的直接证据）。
        const uint hookVa = 0x00427D9D;
        const uint loaderVa = 0x00A727C0;
        const uint resumeVa = 0x00427DA2;
        const uint traceSlot = 0x00C0B100;
        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;
        var stubVa = (uint)alloc.ToInt64();
        var stub = new byte[0x40];
        var o = 0;
        stub[o++] = 0x8B; stub[o++] = 0x04; stub[o++] = 0x24;                   // mov eax,[esp] (mapId)
        stub[o++] = 0xA3; WriteUInt32(stub, ref o, traceSlot + 0x0C);           // mov [traceSlot+0C],eax
        stub[o++] = 0xE8;                                                       // call loaderVa
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, loaderVa);
        stub[o++] = 0xA3; WriteUInt32(stub, ref o, traceSlot + 0x10);           // mov [traceSlot+10],eax
        stub[o++] = 0xE9;                                                       // jmp resumeVa
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);
        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, o, out written))
            return false;
        var entry = new byte[5];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, hookVa + 5, stubVa);
        var ok = Patch(processHandle, hookVa,
            new byte[] { 0xE8, 0x1E, 0xAA, 0x64, 0x00 }, entry);
        if (ok)
            WriteLine("START_MAP 地图加载追踪已安装: 0x427D9D -> 0x" + stubVa.ToString("X8"));
        return ok;
    }

    private static bool InstallTownNetworkTransitionGuard(IntPtr processHandle)
    {
        // This ARAD build expects the channel and game services to live on
        // separate sockets.  Our real server intentionally serves both phases on
        // one connection.  After town activation the client therefore queues a
        // channel-to-game disconnect event even though the game session is already
        // active.  The original tail at 0x43EBEF closes the live socket, displays
        // an empty notice and overwrites one of four global managers with a random
        // value.
        //
        // IMPORTANT: the obsolete transition event only occupies the command-122
        // loop between 0x43EBEF and 0x43EC2B.  The scene-switch core BELOW
        // 0x43EC2B (scene manager [0xEC14F4], scene fn 0x00A70140, notify 38,
        // state-machine dispatch) is required for the first dungeon room: it
        // tears down the built-in Grakka room and builds the server-provided
        // room.  Jumping both tails to the function epilogue (0x43ECBE, the
        // previous behaviour) skipped that core, which left the default Grakka
        // room underneath the correct room on first entry into any dungeon.
        // We therefore consume the stale event by entering the normal scene
        // path at 0x43EC2B instead of returning early.
        var ok = Patch(processHandle, 0x0043EBEF,
            new byte[] { 0x39, 0xBE, 0x24, 0x10, 0x00, 0x00 },
            new byte[] { 0xE9, 0x37, 0x00, 0x00, 0x00, 0x90 });  // jmp 0x0043EC2B
        // Status 2 enters a branch just after the tail check and reports the
        // obsolete channel transition with command 122 once per frame.  It
        // must consume the same stale event without touching the game socket.
        ok &= Patch(processHandle, 0x0043EBFF,
            new byte[] { 0x8B, 0x0D, 0x98, 0xA5, 0xF9, 0x00 },
            new byte[] { 0xE9, 0x27, 0x00, 0x00, 0x00, 0x90 });  // jmp 0x0043EC2B
        if (ok)
            WriteLine("单连接城镇切换已适配: 0x43EBEF/0x43EBFF -> 0x43EC2B（保留场景切换核心）");
        return ok;
    }

    private static bool InstallTownResourceHandleGuard(IntPtr processHandle)
    {
        // During town activation 0x43EA60 stores a small resource handle in
        // [0xECEC8C]. The caller at 0x4A6BBB later treats it as a manager
        // pointer and calls 0x403E40, which immediately dereferences [ecx+2C].
        // Keep the original getter for real pointers; return null only for an
        // impossible user-space pointer. This leaves the surrounding scene and
        // rendering path untouched.
        const uint hookVa = 0x004A6BBB;
        const uint getterVa = 0x00403E40;
        const uint resumeVa = 0x004A6BC6;
        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;

        var stubVa = (uint)alloc.ToInt64();
        var stub = new byte[64];
        var o = 0;
        stub[o++] = 0x8B; stub[o++] = 0x0D; WriteUInt32(stub, ref o, 0x00ECEC8Cu); // mov ecx,[ECEC8C]
        stub[o++] = 0x81; stub[o++] = 0xF9; WriteUInt32(stub, ref o, 0x00010000u); // cmp ecx,10000h
        stub[o++] = 0x72; var smallJump = o++;                                    // jb zero
        stub[o++] = 0x81; stub[o++] = 0xF9; WriteUInt32(stub, ref o, 0x7FFF0000u); // cmp ecx,7FFF0000h
        stub[o++] = 0x73; var highJump = o++;                                     // jae zero
        stub[o++] = 0xE8;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, getterVa);                // call getter
        stub[o++] = 0xE9;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);                // resume valid
        var zeroOffset = o;
        stub[o++] = 0x33; stub[o++] = 0xC0;                                       // xor eax,eax
        stub[o++] = 0xE9;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);                // resume invalid
        stub[smallJump] = checked((byte)(zeroOffset - (smallJump + 1)));
        stub[highJump] = checked((byte)(zeroOffset - (highJump + 1)));

        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, o, out written))
            return false;

        var entry = new byte[6];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, hookVa + 5, stubVa);
        entry[5] = 0x90;
        var ok = Patch(processHandle, hookVa,
            new byte[] { 0x8B, 0x0D, 0x8C, 0xEC, 0xEC, 0x00 }, entry);
        if (ok)
            WriteLine("城镇资源句柄守卫已安装: 0x4A6BBB ([0xECEC8C])");
        if (!ok)
            return false;

        // The town action dispatcher uses [0xF9FCD8] as an interface pointer
        // in every switch branch. On this client/server pairing the slot can
        // still contain its small resource handle. Return from this one action
        // dispatch only while the interface is unavailable; valid objects run
        // the byte-for-byte original prologue and all original virtual calls.
        const uint actionHookVa = 0x00401040;
        const uint actionResumeVa = 0x00401046;
        var actionAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (actionAlloc == IntPtr.Zero)
            return false;
        var actionStubVa = (uint)actionAlloc.ToInt64();
        var actionStub = new byte[64];
        var ao = 0;
        actionStub[ao++] = 0xA1; WriteUInt32(actionStub, ref ao, 0x00F9FCD8u);      // mov eax,[F9FCD8]
        actionStub[ao++] = 0x3D; WriteUInt32(actionStub, ref ao, 0x00010000u);     // cmp eax,10000h
        actionStub[ao++] = 0x72; var actionSmallJump = ao++;                      // jb unavailable
        actionStub[ao++] = 0x3D; WriteUInt32(actionStub, ref ao, 0x7FFF0000u);     // cmp eax,7FFF0000h
        actionStub[ao++] = 0x73; var actionHighJump = ao++;                       // jae unavailable
        actionStub[ao++] = 0x55;                                                  // original: push ebp
        actionStub[ao++] = 0x8B; actionStub[ao++] = 0xEC;                         // mov ebp,esp
        actionStub[ao++] = 0x8B; actionStub[ao++] = 0x45; actionStub[ao++] = 0x08; // mov eax,[ebp+8]
        actionStub[ao++] = 0xE9;
        WriteRelative(actionStub, ref ao, actionStubVa + (uint)ao + 4, actionResumeVa);
        var unavailableOffset = ao;
        actionStub[ao++] = 0xC3;                                                  // cdecl early return
        actionStub[actionSmallJump] = checked((byte)(unavailableOffset - (actionSmallJump + 1)));
        actionStub[actionHighJump] = checked((byte)(unavailableOffset - (actionHighJump + 1)));
        if (!WriteProcessMemory(processHandle, actionAlloc, actionStub, ao, out written))
            return false;
        var actionEntry = new byte[6];
        actionEntry[0] = 0xE9;
        var actionEntryOffset = 1;
        WriteRelative(actionEntry, ref actionEntryOffset, actionHookVa + 5, actionStubVa);
        actionEntry[5] = 0x90;
        ok = Patch(processHandle, actionHookVa,
            new byte[] { 0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08 }, actionEntry);
        if (ok)
            WriteLine("城镇动作接口守卫已安装: 0x401040 ([0xF9FCD8])");
        if (!ok)
            return false;

        // 0x8B3DB6 loads [0xF9A598], then 0x8B3DD4 reads its +8 flag.
        // A small story handle cannot supply that flag. In that temporary
        // state, resume at the normal town-scene body (0x8B3DF0); when the
        // manager is a real pointer, preserve both original flag checks.
        const uint storyHookVa = 0x008B3DD4;
        const uint storyValidResumeVa = 0x008B3DD9;
        const uint storyUnavailableResumeVa = 0x008B3DF0;
        var storyAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (storyAlloc == IntPtr.Zero)
            return false;
        var storyStubVa = (uint)storyAlloc.ToInt64();
        var storyStub = new byte[64];
        var so = 0;
        storyStub[so++] = 0x3D; WriteUInt32(storyStub, ref so, 0x00010000u);       // cmp eax,10000h
        storyStub[so++] = 0x72; var storySmallJump = so++;                        // jb unavailable
        storyStub[so++] = 0x3D; WriteUInt32(storyStub, ref so, 0x7FFF0000u);       // cmp eax,7FFF0000h
        storyStub[so++] = 0x73; var storyHighJump = so++;                         // jae unavailable
        storyStub[so++] = 0x8A; storyStub[so++] = 0x50; storyStub[so++] = 0x08;   // original: mov dl,[eax+8]
        storyStub[so++] = 0x84; storyStub[so++] = 0xD2;                           // original: test dl,dl
        storyStub[so++] = 0xE9;
        WriteRelative(storyStub, ref so, storyStubVa + (uint)so + 4, storyValidResumeVa);
        var storyUnavailableOffset = so;
        storyStub[so++] = 0xE9;
        WriteRelative(storyStub, ref so, storyStubVa + (uint)so + 4, storyUnavailableResumeVa);
        storyStub[storySmallJump] = checked((byte)(storyUnavailableOffset - (storySmallJump + 1)));
        storyStub[storyHighJump] = checked((byte)(storyUnavailableOffset - (storyHighJump + 1)));
        if (!WriteProcessMemory(processHandle, storyAlloc, storyStub, so, out written))
            return false;
        var storyEntry = new byte[5];
        storyEntry[0] = 0xE9;
        var storyEntryOffset = 1;
        WriteRelative(storyEntry, ref storyEntryOffset, storyHookVa + 5, storyStubVa);
        ok = Patch(processHandle, storyHookVa,
            new byte[] { 0x8A, 0x50, 0x08, 0x84, 0xD2 }, storyEntry);
        if (ok)
            WriteLine("城镇故事更新句柄守卫已安装: 0x8B3DD4 ([0xF9A598])");
        if (!ok)
            return false;

        // The per-frame town update directly reads [F9FCD4]+0x20. When the
        // slot is still a transition handle, feed its existing "no object"
        // branch (eax == 0); valid manager pointers keep the original read.
        const uint frameHookVa = 0x00446A1A;
        const uint frameValidResumeVa = 0x00446A20;
        const uint frameUnavailableResumeVa = 0x00446A23;
        var frameAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (frameAlloc == IntPtr.Zero)
            return false;
        var frameStubVa = (uint)frameAlloc.ToInt64();
        var frameStub = new byte[64];
        var fro = 0;
        frameStub[fro++] = 0x8B; frameStub[fro++] = 0x0D; WriteUInt32(frameStub, ref fro, 0x00F9FCD4u); // original load
        frameStub[fro++] = 0x81; frameStub[fro++] = 0xF9; WriteUInt32(frameStub, ref fro, 0x00010000u);
        frameStub[fro++] = 0x72; var frameSmallJump = fro++;
        frameStub[fro++] = 0x81; frameStub[fro++] = 0xF9; WriteUInt32(frameStub, ref fro, 0x7FFF0000u);
        frameStub[fro++] = 0x73; var frameHighJump = fro++;
        frameStub[fro++] = 0xE9;
        WriteRelative(frameStub, ref fro, frameStubVa + (uint)fro + 4, frameValidResumeVa);
        var frameUnavailableOffset = fro;
        frameStub[fro++] = 0x33; frameStub[fro++] = 0xC0;                         // no object
        frameStub[fro++] = 0xE9;
        WriteRelative(frameStub, ref fro, frameStubVa + (uint)fro + 4, frameUnavailableResumeVa);
        frameStub[frameSmallJump] = checked((byte)(frameUnavailableOffset - (frameSmallJump + 1)));
        frameStub[frameHighJump] = checked((byte)(frameUnavailableOffset - (frameHighJump + 1)));
        if (!WriteProcessMemory(processHandle, frameAlloc, frameStub, fro, out written))
            return false;
        var frameEntry = new byte[6];
        frameEntry[0] = 0xE9;
        var frameEntryOffset = 1;
        WriteRelative(frameEntry, ref frameEntryOffset, frameHookVa + 5, frameStubVa);
        frameEntry[5] = 0x90;
        ok = Patch(processHandle, frameHookVa,
            new byte[] { 0x8B, 0x0D, 0xD4, 0xFC, 0xF9, 0x00 }, frameEntry);
        if (ok)
            WriteLine("城镇帧资源守卫已安装: 0x446A1A ([0xF9FCD4])");
        if (!ok)
            return false;

        // Tiny story-state getter: mov al,[ecx+8]; ret. Small transition
        // handles mean "not active" and must not be dereferenced.
        const uint storyGetterHookVa = 0x0043D7F0;
        var storyGetterAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (storyGetterAlloc == IntPtr.Zero)
            return false;
        var storyGetterStubVa = (uint)storyGetterAlloc.ToInt64();
        var storyGetterStub = new byte[48];
        var sgo = 0;
        storyGetterStub[sgo++] = 0x81; storyGetterStub[sgo++] = 0xF9; WriteUInt32(storyGetterStub, ref sgo, 0x00010000u);
        storyGetterStub[sgo++] = 0x72; var storyGetterSmallJump = sgo++;
        storyGetterStub[sgo++] = 0x81; storyGetterStub[sgo++] = 0xF9; WriteUInt32(storyGetterStub, ref sgo, 0x7FFF0000u);
        storyGetterStub[sgo++] = 0x73; var storyGetterHighJump = sgo++;
        storyGetterStub[sgo++] = 0x8A; storyGetterStub[sgo++] = 0x41; storyGetterStub[sgo++] = 0x08;
        storyGetterStub[sgo++] = 0xC3;
        var storyGetterUnavailableOffset = sgo;
        storyGetterStub[sgo++] = 0x32; storyGetterStub[sgo++] = 0xC0;             // xor al,al
        storyGetterStub[sgo++] = 0xC3;
        storyGetterStub[storyGetterSmallJump] = checked((byte)(storyGetterUnavailableOffset - (storyGetterSmallJump + 1)));
        storyGetterStub[storyGetterHighJump] = checked((byte)(storyGetterUnavailableOffset - (storyGetterHighJump + 1)));
        if (!WriteProcessMemory(processHandle, storyGetterAlloc, storyGetterStub, sgo, out written))
            return false;
        var storyGetterEntry = new byte[5];
        storyGetterEntry[0] = 0xE9;
        var storyGetterEntryOffset = 1;
        WriteRelative(storyGetterEntry, ref storyGetterEntryOffset, storyGetterHookVa + 5, storyGetterStubVa);
        ok = Patch(processHandle, storyGetterHookVa,
            new byte[] { 0x8A, 0x41, 0x08, 0xC3, 0xCC }, storyGetterEntry);
        if (ok)
            WriteLine("故事状态 getter 守卫已安装: 0x43D7F0");
        if (!ok)
            return false;

        // This boolean query assumes the getter can never return null and
        // immediately takes result+0x14C. Reject only an invalid manager at
        // entry and report false; real managers execute the original query.
        const uint queryHookVa = 0x00555180;
        const uint queryResumeVa = 0x00555188;
        var queryAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (queryAlloc == IntPtr.Zero)
            return false;
        var queryStubVa = (uint)queryAlloc.ToInt64();
        var queryStub = new byte[64];
        var qo = 0;
        queryStub[qo++] = 0x81; queryStub[qo++] = 0xF9; WriteUInt32(queryStub, ref qo, 0x00010000u); // cmp ecx,10000h
        queryStub[qo++] = 0x72; var querySmallJump = qo++;
        queryStub[qo++] = 0x81; queryStub[qo++] = 0xF9; WriteUInt32(queryStub, ref qo, 0x7FFF0000u); // cmp ecx,7FFF0000h
        queryStub[qo++] = 0x73; var queryHighJump = qo++;
        queryStub[qo++] = 0x56;                                                    // original: push esi
        queryStub[qo++] = 0x8B; queryStub[qo++] = 0xF1;                           // mov esi,ecx
        queryStub[qo++] = 0xE8;
        WriteRelative(queryStub, ref qo, queryStubVa + (uint)qo + 4, 0x00403E40u); // original getter call
        queryStub[qo++] = 0xE9;
        WriteRelative(queryStub, ref qo, queryStubVa + (uint)qo + 4, queryResumeVa);
        var queryUnavailableOffset = qo;
        queryStub[qo++] = 0x33; queryStub[qo++] = 0xC0;                            // false
        queryStub[qo++] = 0xC3;
        queryStub[querySmallJump] = checked((byte)(queryUnavailableOffset - (querySmallJump + 1)));
        queryStub[queryHighJump] = checked((byte)(queryUnavailableOffset - (queryHighJump + 1)));
        if (!WriteProcessMemory(processHandle, queryAlloc, queryStub, qo, out written))
            return false;
        var queryEntry = new byte[8];
        queryEntry[0] = 0xE9;
        var queryEntryOffset = 1;
        WriteRelative(queryEntry, ref queryEntryOffset, queryHookVa + 5, queryStubVa);
        queryEntry[5] = 0x90; queryEntry[6] = 0x90; queryEntry[7] = 0x90;
        ok = Patch(processHandle, queryHookVa,
            new byte[] { 0x56, 0x8B, 0xF1, 0xE8, 0xB8, 0xEC, 0xEA, 0xFF }, queryEntry);
        if (ok)
            WriteLine("城镇资源状态查询守卫已安装: 0x555180");
        if (!ok)
            return false;

        // Resource manager getter. Its contract already returns null when no
        // resource object exists; extend that contract to the transition-time
        // small handles before touching [ecx+2C]. This protects every genuine
        // caller without changing successful manager creation or caching.
        const uint getterHookVa = 0x00403E40;
        const uint getterResumeVa = 0x00403E46;
        var getterAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (getterAlloc == IntPtr.Zero)
            return false;
        var getterStubVa = (uint)getterAlloc.ToInt64();
        var getterStub = new byte[64];
        var go = 0;
        getterStub[go++] = 0x81; getterStub[go++] = 0xF9; WriteUInt32(getterStub, ref go, 0x00010000u); // cmp ecx,10000h
        getterStub[go++] = 0x72; var getterSmallJump = go++;                       // jb unavailable
        getterStub[go++] = 0x81; getterStub[go++] = 0xF9; WriteUInt32(getterStub, ref go, 0x7FFF0000u); // cmp ecx,7FFF0000h
        getterStub[go++] = 0x73; var getterHighJump = go++;                        // jae unavailable
        getterStub[go++] = 0x56;                                                   // original: push esi
        getterStub[go++] = 0x8B; getterStub[go++] = 0xF1;                          // mov esi,ecx
        getterStub[go++] = 0x8B; getterStub[go++] = 0x46; getterStub[go++] = 0x2C; // mov eax,[esi+2c]
        getterStub[go++] = 0xE9;
        WriteRelative(getterStub, ref go, getterStubVa + (uint)go + 4, getterResumeVa);
        var getterUnavailableOffset = go;
        getterStub[go++] = 0x33; getterStub[go++] = 0xC0;                          // xor eax,eax
        getterStub[go++] = 0xC3;                                                   // ret
        getterStub[getterSmallJump] = checked((byte)(getterUnavailableOffset - (getterSmallJump + 1)));
        getterStub[getterHighJump] = checked((byte)(getterUnavailableOffset - (getterHighJump + 1)));
        if (!WriteProcessMemory(processHandle, getterAlloc, getterStub, go, out written))
            return false;
        var getterEntry = new byte[6];
        getterEntry[0] = 0xE9;
        var getterEntryOffset = 1;
        WriteRelative(getterEntry, ref getterEntryOffset, getterHookVa + 5, getterStubVa);
        getterEntry[5] = 0x90;
        ok = Patch(processHandle, getterHookVa,
            new byte[] { 0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x2C }, getterEntry);
        if (ok)
            WriteLine("资源管理器 getter 守卫已安装: 0x403E40");
        if (!ok)
            return false;

        // 0x43EC41 invokes the network-manager teardown 0xA70140 from the
        // legacy transition path. That routine calls closesocket through
        // 0xAAA760 and clears the session object; this client never reconnects
        // afterward. Suppress only this teardown call. Packet processing,
        // scene activation and the rest of the transition function continue.
        ok = Patch(processHandle, 0x0043EC41,
            new byte[] { 0xE8, 0xFA, 0x14, 0x63, 0x00 },
            new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
        if (ok)
            WriteLine("错误的城镇网络会话清理已抑制: 0x43EC41");
        if (!ok)
            return false;

        // The teardown routine finishes by replacing the still-addressable
        // story manager slot with the debug sentinel CDCDCDCD. Later town
        // transition code continues to use that manager. Keep only the slot
        // assignment from happening; the teardown call and every other cleanup
        // instruction before it still execute normally.
        ok = Patch(processHandle, 0x00A6F89D,
            new byte[] { 0xC7, 0x05, 0x98, 0xA5, 0xF9, 0x00, 0xCD, 0xCD, 0xCD, 0xCD },
            new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
        if (ok)
            WriteLine("故事管理器槽释放标记已抑制: 0xA6F89D");
        return ok;
    }

    private static bool InstallTownEffectPassSkip(IntPtr processHandle, int pass)
    {
        uint address;
        uint target;
        byte[] original;
        if (pass == 1)
        {
            address = 0x005088F9;
            target = 0x00508925;
            original = new byte[] { 0x33, 0xFF, 0x85, 0xF6, 0x7E, 0x26 };
        }
        else if (pass == 2)
        {
            address = 0x00508925;
            target = 0x0050894A;
            original = new byte[] { 0x83, 0x7D, 0x08, 0x01, 0x75, 0x1F };
        }
        else
        {
            address = 0x0050894A;
            target = 0x00508971;
            original = new byte[] { 0x33, 0xFF, 0x85, 0xF6, 0x7E, 0x21 };
        }
        var replacement = new byte[6];
        replacement[0] = 0xE9;
        var o = 1;
        WriteRelative(replacement, ref o, address + 5, target);
        replacement[5] = 0x90;
        var ok = Patch(processHandle, address, original, replacement);
        if (ok)
            WriteLine("诊断：临时跳过角色特效遍历 pass " + pass);
        return ok;
    }

    private static bool InstallTownEffectCountRefresh(IntPtr processHandle)
    {
        // 0x5088D0 calculates the vector length once, runs pass 1, then calls
        // 0x508540 which erases expired entries. Pass 2 incorrectly reuses the
        // pre-erase count and walks beyond [ebx+30, ebx+34). Recompute the count
        // immediately after cleanup while preserving all three original passes.
        const uint hookVa = 0x00508932;
        const uint loopVa = 0x00508938;
        const uint afterLoopVa = 0x0050894A;
        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x100, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;
        var stubVa = (uint)alloc.ToInt64();
        var stub = new byte[64];
        var o = 0;
        stub[o++] = 0x8B; stub[o++] = 0x4B; stub[o++] = 0x30;                    // mov ecx,[ebx+30h]
        stub[o++] = 0x85; stub[o++] = 0xC9;                                      // test ecx,ecx
        stub[o++] = 0x74; var zeroJump = o++;                                    // jz zero
        stub[o++] = 0x8B; stub[o++] = 0x73; stub[o++] = 0x34;                    // mov esi,[ebx+34h]
        stub[o++] = 0x2B; stub[o++] = 0xF1;                                      // sub esi,ecx
        stub[o++] = 0xC1; stub[o++] = 0xFE; stub[o++] = 0x02;                    // sar esi,2
        stub[o++] = 0xEB; var readyJump = o++;                                   // jmp ready
        var zeroOffset = o;
        stub[o++] = 0x33; stub[o++] = 0xF6;                                      // zero: xor esi,esi
        var readyOffset = o;
        stub[o++] = 0x33; stub[o++] = 0xFF;                                      // ready: xor edi,edi
        stub[o++] = 0x85; stub[o++] = 0xF6;                                      // test esi,esi
        stub[o++] = 0x0F; stub[o++] = 0x8E;                                      // jle afterLoop
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, afterLoopVa);
        stub[o++] = 0xE9;                                                        // jmp loop
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, loopVa);
        stub[zeroJump] = checked((byte)(zeroOffset - (zeroJump + 1)));
        stub[readyJump] = checked((byte)(readyOffset - (readyJump + 1)));
        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, o, out written))
            return false;

        var entry = new byte[6];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, hookVa + 5, stubVa);
        entry[5] = 0x90;
        var ok = Patch(processHandle, hookVa,
            new byte[] { 0x33, 0xFF, 0x85, 0xF6, 0x7E, 0x12 }, entry);
        if (ok)
            WriteLine("角色特效数组长度修复已安装: 0x508932（清理后重新计数）");
        return ok;
    }

    private static bool InstallTownEffectContainerTrace(IntPtr processHandle)
    {
        // Diagnostic-only hook: execute the original cleanup call, then expose
        // this/vector begin/vector end/old count in a remote read-only snapshot.
        const uint hookVa = 0x0050892D;
        const uint cleanupVa = 0x00508540;
        const uint resumeVa = 0x00508932;
        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x200, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;
        var stubVa = (uint)alloc.ToInt64();
        var stateVa = stubVa + 0x100;
        var stub = new byte[0x120];
        var o = 0;
        stub[o++] = 0xE8;                                                        // original call 0x508540
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, cleanupVa);
        stub[o++] = 0x60;                                                        // pushad
        stub[o++] = 0x89; stub[o++] = 0x1D; WriteUInt32(stub, ref o, stateVa);   // mov [state],ebx
        stub[o++] = 0x8B; stub[o++] = 0x43; stub[o++] = 0x30;                    // mov eax,[ebx+30]
        stub[o++] = 0xA3; WriteUInt32(stub, ref o, stateVa + 4);                 // mov [state+4],eax
        stub[o++] = 0x8B; stub[o++] = 0x43; stub[o++] = 0x34;                    // mov eax,[ebx+34]
        stub[o++] = 0xA3; WriteUInt32(stub, ref o, stateVa + 8);                 // mov [state+8],eax
        stub[o++] = 0x89; stub[o++] = 0x35; WriteUInt32(stub, ref o, stateVa + 12); // mov [state+12],esi
        stub[o++] = 0xFF; stub[o++] = 0x05; WriteUInt32(stub, ref o, stateVa + 16); // inc [state+16]
        stub[o++] = 0x61;                                                        // popad
        stub[o++] = 0xE9;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);
        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, 0x120, out written))
            return false;
        var entry = new byte[5];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, hookVa + 5, stubVa);
        var ok = Patch(processHandle, hookVa,
            new byte[] { 0xE8, 0x0E, 0xFC, 0xFF, 0xFF }, entry);
        if (ok)
            WriteLine("effect-trace-state=0x" + stateVa.ToString("X8"));
        return ok;
    }

    private static bool InstallTownEffectPass2Trace(IntPtr processHandle)
    {
        const uint hookVa = 0x00508938;
        const uint resumeVa = 0x00508945;
        var alloc = VirtualAllocEx(processHandle, IntPtr.Zero, 0x200, 0x3000, 0x40);
        if (alloc == IntPtr.Zero)
            return false;
        var stubVa = (uint)alloc.ToInt64();
        var stateVa = stubVa + 0x100;
        var stub = new byte[0x120];
        var o = 0;
        stub[o++] = 0x8B; stub[o++] = 0x4B; stub[o++] = 0x30;                    // mov ecx,[ebx+30h]
        stub[o++] = 0x8D; stub[o++] = 0x04; stub[o++] = 0xB9;                    // lea eax,[ecx+edi*4]
        stub[o++] = 0x8B; stub[o++] = 0x08;                                      // mov ecx,[eax]
        stub[o++] = 0x8B; stub[o++] = 0x11;                                      // mov edx,[ecx]
        stub[o++] = 0x60;                                                        // pushad
        stub[o++] = 0x89; stub[o++] = 0x3D; WriteUInt32(stub, ref o, stateVa);   // mov [state],edi
        stub[o++] = 0x89; stub[o++] = 0x0D; WriteUInt32(stub, ref o, stateVa + 4); // mov [state+4],ecx
        stub[o++] = 0x89; stub[o++] = 0x15; WriteUInt32(stub, ref o, stateVa + 8); // mov [state+8],edx
        stub[o++] = 0x8B; stub[o++] = 0x42; stub[o++] = 0x1C;                    // mov eax,[edx+1Ch]
        stub[o++] = 0xA3; WriteUInt32(stub, ref o, stateVa + 12);                // mov [state+12],eax
        stub[o++] = 0xFF; stub[o++] = 0x05; WriteUInt32(stub, ref o, stateVa + 16); // inc calls
        stub[o++] = 0x61;                                                        // popad
        stub[o++] = 0xFF; stub[o++] = 0x52; stub[o++] = 0x1C;                    // original virtual call
        stub[o++] = 0x60;                                                        // pushad
        stub[o++] = 0x89; stub[o++] = 0x3D; WriteUInt32(stub, ref o, stateVa + 20); // completed index
        stub[o++] = 0xFF; stub[o++] = 0x05; WriteUInt32(stub, ref o, stateVa + 24); // inc completed calls
        stub[o++] = 0x61;                                                        // popad
        stub[o++] = 0xE9;
        WriteRelative(stub, ref o, stubVa + (uint)o + 4, resumeVa);
        IntPtr written;
        if (!WriteProcessMemory(processHandle, alloc, stub, 0x120, out written))
            return false;
        var entry = new byte[6];
        entry[0] = 0xE9;
        var eo = 1;
        WriteRelative(entry, ref eo, hookVa + 5, stubVa);
        entry[5] = 0x90;
        var ok = Patch(processHandle, hookVa,
            new byte[] { 0x8B, 0x4B, 0x30, 0x8D, 0x04, 0xB9 }, entry);
        if (ok)
            WriteLine("effect-pass2-trace-state=0x" + stateVa.ToString("X8"));
        return ok;
    }

    private static void WriteRelative(byte[] buffer, ref int offset, uint nextAddress, uint target)
    {
        var displacement = (int)(target - nextAddress);
        var bytes = BitConverter.GetBytes(displacement);
        buffer[offset++] = bytes[0];
        buffer[offset++] = bytes[1];
        buffer[offset++] = bytes[2];
        buffer[offset++] = bytes[3];
    }

    private static bool Patch(IntPtr processHandle, uint address, byte[] original, byte[] replacement)
    {
        if (Patched.Contains(address)) return true;
        var current = new byte[original.Length];
        IntPtr bytesRead;
        if (!ReadProcessMemory(processHandle, new IntPtr((long)address), current, current.Length, out bytesRead))
            return false;
        for (var i = 0; i < original.Length; i++)
            if (current[i] != original[i])
                return false;
        IntPtr bytesWritten;
        if (!WriteProcessMemory(processHandle, new IntPtr((long)address), replacement, replacement.Length, out bytesWritten))
            return false;
        Patched.Add(address);
        WriteLine("已补丁 0x" + address.ToString("X8") + " " + Hex(original) + " -> " + Hex(replacement));
        return true;
    }

    private static string Hex(byte[] data)
    {
        var builder = new StringBuilder(data.Length * 2);
        foreach (var value in data) builder.Append(value.ToString("X2"));
        return builder.ToString();
    }

    private static void WriteLine(string line)
    {
        lock (Sync) Log.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
        Console.WriteLine(line);
    }

    private static void Flush(string logFile)
    {
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(logFile)) return;
            try { File.AppendAllText(Path.GetFullPath(logFile), Log.ToString(), Encoding.UTF8); }
            catch { }
            Log.Clear();
        }
    }

    private readonly struct OfflineDungeonMapping
    {
        public OfflineDungeonMapping(uint dungeonId, uint mapId, byte startX, byte startY, byte width,
            byte bossX, byte bossY, byte startDoorMask)
        {
            DungeonId = dungeonId;
            MapId = mapId;
            PackedStartRoom = (uint)(startX | (startY << 8));
            Width = width;
            PackedBossRoom = (uint)(bossX | (bossY << 8));
            StartDoorMask = startDoorMask;
        }

        public uint DungeonId { get; }
        public uint MapId { get; }
        public uint PackedStartRoom { get; }
        public uint Width { get; }
        public uint PackedBossRoom { get; }
        public uint StartDoorMask { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreateProcess(string applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
        string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr address, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr processHandle, IntPtr address, byte[] buffer, int size, out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr processHandle, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(HandleRef processHandle, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr SetProcessAffinityMask(HandleRef processHandle, IntPtr processAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualQueryEx(IntPtr hProcess, IntPtr address, out MEMORY_BASIC_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr address, int size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr address, int size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Module32First(IntPtr snapshot, ref MODULEENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Module32Next(IntPtr snapshot, ref MODULEENTRY32 entry);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr hProcess, IntPtr[] modules, uint count, out uint needed, uint filterFlag);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleBaseName(IntPtr hProcess, IntPtr module, System.Text.StringBuilder name, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr module, out MODULEINFO info, uint size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MODULEENTRY32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public uint modBaseAddr;
        public uint modBaseSize;
        public uint hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
