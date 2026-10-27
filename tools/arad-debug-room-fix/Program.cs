using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DF60A1;
using Iced.Intel;

internal static class Program
{
    private const uint CreateSuspended = 0x00000004;
    private const uint MemCommit = 0x00001000;
    private const uint MemReserve = 0x00002000;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint WaitObject0 = 0;

    // This is inside ARAD.exe's DebugConfig-only load routine. It is not used
    // by the network START_MAP handler.
    private const uint DebugConfigGate = 0x0040CD1B;
    private static readonly byte[] ExpectedDebugConfigGateBytes = { 0x74, 0x07 }; // je 0040CD24h
    private const uint MapSelectionHook = 0x00409435;
    private const uint MapSelectionResume = 0x0040943D;
    private const uint DirectMapSelectionHook = 0x004097E4;
    private const uint DirectMapSelectionResume = 0x004097ED;
    private const uint MapLoaderHook = 0x00A727C0;
    private const uint MapLoaderResume = 0x00A727C5;
    private const uint RoomLookupHook = 0x00404D45;
    private const uint RoomLookupResume = 0x00404D4D;
    private const uint OfflineFallbackHook = 0x00A59D63;
    private const uint OfflineFallbackResume = 0x00A59D81;
    // Capture the selected dungeon before the offline flow clears its normal
    // DUNGEON_INFO state.  This hook deliberately replays `push 0`: changing
    // the maze argument here previously made the client unstable.
    private const uint OfflineMazeCaptureHook = 0x00A59B22;
    private const uint OfflineMazeCaptureResume = 0x00A59B29;
    private const uint OfflineStateCaptureHook = 0x00A59B32;
    private const uint OfflineStateCaptureResume = 0x00A59B38;
    // The offline fallback clears the normal DUNGEON_INFO route fields before
    // it creates the scene. During construction, temporarily use the PVF
    // start coordinate as boss coordinate so physical slot zero is not
    // initialized as a boss scene. Restore the real PVF boss afterwards.
    private const uint OfflineDungeonStateHook = 0x00A59C1E;
    private const uint OfflineDungeonStateResume = 0x00A59C24;
    private const uint DungeonStateSetter = 0x00403F40;
    // Relocate the completed room cell before the client commits the dungeon
    // transition. Waiting until the epilogue leaves cached slot-0 state behind.
    private const uint OfflineFinalizeHook = 0x00A5A012;
    private const uint OfflineFinalizeResume = 0x00A5A017;
    private const uint OfflineCoordinateReapplyHook = 0x00A5A01F;
    private const uint OfflineCoordinateReapplyResume = 0x00A5A024;
    private const uint OfflineCoordinateUseHook = 0x009A2C35;
    private const uint OfflineCoordinateUseResume = 0x009A2C3B;
    // 0x00489790 returns the PVF topology value.  These two sites consume
    // that return value immediately before the native scene constructors
    // cache doors, Boss styling, and minimap links.  Patching scene+0x238
    // after construction is too late for those cached objects.
    private const uint OfflineRoutePreconstructHook = 0x00486F44;
    private const uint OfflineRoutePreconstructResume = 0x00486F4A;
    private const uint OfflineRoutePreconstructAlternateHook = 0x00486F94;
    private const uint OfflineRoutePreconstructAlternateResume = 0x00486F9A;
    // These post-construction sites are retained as diagnostics only. The
    // actual PVF topology repair runs before 0x0048E610; writing scene+0x238
    // here cannot rebuild door/minimap objects already cached by the native
    // constructor.
    private const uint OfflineSceneConstructedHook = 0x00486F4F;
    private const uint OfflineSceneConstructedResume = 0x00486F55;
    // The room constructor has a second branch when the client is not in the
    // cached-scene state. It returns the same scene object through EAX, but
    // publishes it at a different instruction.
    private const uint OfflineSceneConstructedAlternateHook = 0x00486FA3;
    private const uint OfflineSceneConstructedAlternateResume = 0x00486FA9;
    // The native room constructor compares its physical (slot-zero) position
    // against the global boss coordinates before returning. Restore the real
    // PVF boss only after that comparison and its scene setup have finished.
    private const uint OfflineBossRestoreHook = 0x00A5BE09;
    private const uint OfflineBossRestoreResume = 0x00A5BE0F;
    private const uint DefaultOfflineFallbackMap = 1001;
    private const int OfflineMazeCaptureStubOffset = 0x100;
    private const int OfflineStateCaptureStubOffset = 0x180;
    private const int OfflineFinalizeStubOffset = 0x1C0;
    // The game-thread PVF topology writer belongs in the finalizer. Keep the
    // later stubs well clear of it; overlap is rejected by --verify-stub.
    private const int OfflineRoomLookupStubOffset = 0x440;
    private const int OfflineCoordinateReapplyStubOffset = 0x4C0;
    private const int OfflineCoordinateUseStubOffset = 0x540;
    private const int OfflineDungeonStateStubOffset = 0x5C0;
    private const int OfflineRoutePreconstructStubOffset = 0x640;
    private const int OfflineRoutePreconstructAlternateStubOffset = 0x6C0;
    private const int OfflineSceneConstructedStubOffset = 0x740;
    private const int OfflineSceneConstructedAlternateStubOffset = 0x7C0;
    private const int OfflineBossRestoreStubOffset = 0x840;
    private const int OfflineDungeonStateOffset = 0x900;
    private const int OfflineDungeonMapTableOffset = 0xA00;
    private const int OfflineRouteStartMapOffset = 0x48;
    private const int OfflineRouteDoorMaskOffset = 0x4C;
    private const int OfflineRouteDoorWriteCountOffset = 0x50;
    private const int OfflineRouteDoorStatusOffset = 0x54;
    private const int OfflineRouteDoorMaskBeforeOffset = 0x58;
    private const int OfflineBossRestoreArmedOffset = 0x60;
    private const int OfflineBossRestoreCountOffset = 0x64;
    private const int OfflineRoutePreconstructCountOffset = 0x68;
    private const int OfflineRoutePreconstructMaskBeforeOffset = 0x6C;
    private const int OfflineDungeonStateSize = 0x80;
    // dungeon id, start map id, packed client start room, room-table width,
    // packed boss room.
    private const int OfflineDungeonMapRecordSize = 20;
    // The offline town-to-dungeon fallback calls the loader from this address.
    // Its caller frame still holds the two selected transition parameters.
    private const uint OfflineFallbackLoaderReturn = 0x00A59D81;
    private const int MapLoaderTraceEntryCount = 16;
    private const int MapLoaderTraceArmedOffset = 4;
    private const int MapLoaderTraceFallbackArg1Offset = 16 + MapLoaderTraceEntryCount * 8;
    private const int MapLoaderTraceFallbackArg2Offset = MapLoaderTraceFallbackArg1Offset + 4;
    private const int RoomTraceRecordCount = 256;
    private const int RoomTraceRecordOffset = 16;
    private const int RoomTraceRecordSize = 16;
    private const int RoomCellSize = 0x28;
    private const int RoomWatchCellCount = 64;
    private const uint RoomTablePointerGlobal = 0x00ED041C;
    private const uint RoomTableEndGlobal = 0x00ED0420;
    private const uint ClientRootGlobal = 0x00ECE658;
    // The active scene table contains one room-scene object per room cell.
    // A scene object's +0x238 byte is the native four-direction door mask.
    private const uint RoomSceneTableGlobal = 0x00ED042C;
    private const int RoomSceneDoorContextOffset = 0x234;
    private const int RoomSceneDoorMaskOffset = 0x238;
    // Boss coordinates are held in the client's checked encrypted-state
    // wrappers. Both backing arrays must decode to the same value.
    private const uint EncryptedStateContextGlobal = 0x010D47E8;
    private const uint EncryptedStateKeyGlobal = 0x010D47EC;
    private const uint EncryptedBossXWrapper = 0x00ED03E0;
    private const uint EncryptedBossYWrapper = 0x00ED03E8;
    private const uint EncryptedRouteXWrapper = 0x00ED03F0;
    private const uint EncryptedRouteYWrapper = 0x00ED03F8;
    // Room lookup receives an index in units of five. The lookup body then
    // multiplies it by eight, yielding the 0x28-byte room-cell stride.
    private const int RoomLookupIndexScale = RoomCellSize / 8;
    private static readonly byte[] ExpectedMapSelectionBytes =
    {
        0x8B, 0x45, 0xD8,                         // mov eax,[ebp-28h]
        0xB9, 0x01, 0x00, 0x00, 0x00              // mov ecx,1
    };
    private static readonly byte[] ExpectedDirectMapSelectionBytes =
    {
        0x8B, 0x4D, 0xD8,                         // mov ecx,[ebp-28h]
        0x83, 0xC0, 0x28,                         // add eax,28h
        0x89, 0x08,                               // mov [eax],ecx
        0x51                                      // push ecx
    };
    private static readonly byte[] ExpectedMapLoaderBytes =
    {
        0x55, 0x8B, 0xEC, 0x51, 0x56              // push ebp; mov ebp,esp; push ecx; push esi
    };
    private static readonly byte[] ExpectedRoomLookupBytes =
    {
        0xA1, 0x1C, 0x04, 0xED, 0x00              // mov eax,[0ED041Ch]
    };
    private static readonly byte[] ExpectedOfflineFallbackBytes =
    {
        0xA1, 0x1C, 0x04, 0xED, 0x00,             // mov eax,[0ED041Ch]
        0xC7, 0x00, 0xE9, 0x03, 0x00, 0x00,       // mov dword ptr [eax],3E9h
        0x8B, 0x0D, 0x48, 0xEC, 0xEC, 0x00,       // mov ecx,[0ECEC48h]
        0x68, 0xE9, 0x03, 0x00, 0x00,             // push 3E9h
        0x89, 0x45, 0xE8,                         // mov [ebp-18h],eax
        0xE8                                      // call 00A727C0h
    };
    private static readonly byte[] ExpectedOfflineMazeCaptureBytes =
    {
        0x6A, 0x00,                               // push 0
        0x8B, 0xCE,                               // mov ecx,esi
        0x89, 0x75, 0xE0                          // mov [ebp-20h],esi
    };
    private static readonly byte[] ExpectedOfflineFinalizeBytes =
    {
        0x8B, 0x41, 0x34,                         // mov eax,[ecx+34h]
        0x6A, 0x00                                // push 0
    };
    private static readonly byte[] ExpectedOfflineStateCaptureBytes =
    {
        0x89, 0xB7, 0xB4, 0x00, 0x00, 0x00       // mov [edi+0B4h],esi
    };
    private static readonly byte[] ExpectedOfflineDungeonStateBytes =
    {
        0x8B, 0x0D, 0x90, 0xC4, 0xEC, 0x00       // mov ecx,[0ECC490h]
    };
    private static readonly byte[] ExpectedOfflineCoordinateReapplyBytes =
    {
        0x8B, 0x4D, 0xF4,                         // mov ecx,[ebp-0Ch]
        0x5F, 0x5E                                // pop edi; pop esi
    };
    private static readonly byte[] ExpectedOfflineCoordinateUseBytes =
    {
        0x8B, 0x86, 0xBC, 0x00, 0x00, 0x00       // mov eax,[esi+0BCh]
    };
    private static readonly byte[] ExpectedOfflineRoutePreconstructBytes =
    {
        0x8B, 0x0B,                               // mov ecx,[ebx]
        0x50,                                     // push eax
        0x51,                                     // push ecx
        0x8B, 0xCF                                // mov ecx,edi
    };
    private static readonly byte[] ExpectedOfflineRoutePreconstructAlternateBytes =
    {
        0x8B, 0x13,                               // mov edx,[ebx]
        0x50,                                     // push eax
        0x52,                                     // push edx
        0x8B, 0xCF                                // mov ecx,edi
    };
    private static readonly byte[] ExpectedOfflineSceneConstructedBytes =
    {
        0x8B, 0x15, 0x2C, 0x04, 0xED, 0x00        // mov edx,[0ED042Ch]
    };
    private static readonly byte[] ExpectedOfflineSceneConstructedAlternateBytes =
    {
        0x8B, 0x0D, 0x2C, 0x04, 0xED, 0x00        // mov ecx,[0ED042Ch]
    };
    private static readonly byte[] ExpectedOfflineBossRestoreBytes =
    {
        0x8B, 0x0D, 0x58, 0xE6, 0xEC, 0x00        // mov ecx,[0ECE658h]
    };
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (HasFlag(args, "--help"))
        {
            Console.WriteLine("Usage: arad-debug-room-fix [--workdir <dir>] [--client <ARAD.exe>] [--trace-map-index] [--launch-map-loader-trace] [--launch-offline-dungeon-fix] [--launch-offline-room-monitor] [--launch-offline-room-trace] [--apply-pvf-route <dungeon-id>] [--force-room-slot <slot>] [--resolve-offline-dungeon <id>] [--describe-pvf-route <dungeon-id>] [--attach-map-loader-trace <pid>] [--attach-room-trace <pid>] [--watch-room-memory <pid> [--watch-until-exit]] [--scan-room-coordinate-accesses] [--find-callers <VA>] [--find-memory-references <VA>] [--disassemble <VA> [--count <instructions>]]");
            return 0;
        }

        if (HasFlag(args, "--verify-stub")) return VerifyStub();

        var configuredWorkdir = GetOption(args, "--workdir");
        var workdir = configuredWorkdir == null
            ? FindClientDirectory(Directory.GetCurrentDirectory()) ??
              FindClientDirectory(AppContext.BaseDirectory) ??
              Path.GetFullPath(Directory.GetCurrentDirectory())
            : Path.GetFullPath(configuredWorkdir);
        var statePath = Path.Combine(workdir, "debug-room-cycle.state");
        var logPath = Path.Combine(workdir, "debug-room-fix.log");
        var mapLoaderTracePid = GetOption(args, "--attach-map-loader-trace");
        if (mapLoaderTracePid != null)
        {
            if (!int.TryParse(mapLoaderTracePid, out var processId) || processId <= 0)
            {
                Console.Error.WriteLine("Invalid ARAD.exe process id: " + mapLoaderTracePid);
                return 2;
            }
            return AttachMapLoaderTrace(processId, Path.Combine(workdir, "map-loader-trace.log"));
        }
        var roomTracePid = GetOption(args, "--attach-room-trace");
        if (roomTracePid != null)
        {
            if (!int.TryParse(roomTracePid, out var processId) || processId <= 0)
            {
                Console.Error.WriteLine("Invalid ARAD.exe process id: " + roomTracePid);
                return 2;
            }
            return AttachRoomTrace(processId, Path.Combine(workdir, "room-lookup-trace.log"));
        }
        var roomWatchPid = GetOption(args, "--watch-room-memory");
        if (roomWatchPid != null)
        {
            if (!int.TryParse(roomWatchPid, out var processId) || processId <= 0)
            {
                Console.Error.WriteLine("Invalid ARAD.exe process id: " + roomWatchPid);
                return 2;
            }
            return WatchRoomMemory(processId, Path.Combine(workdir, "room-memory-watch.log"),
                waitUntilProcessExit: HasFlag(args, "--watch-until-exit"));
        }
        var describeRoute = GetOption(args, "--describe-pvf-route");
        if (describeRoute != null)
        {
            if (!ushort.TryParse(describeRoute, out var dungeonId))
            {
                Console.Error.WriteLine("Invalid dungeon ID: " + describeRoute);
                return 2;
            }
            try
            {
                if (!TryBuildPvfDungeonRoute(ResolvePvfPath(args, workdir), dungeonId, out var route))
                {
                    Console.Error.WriteLine("No complete PVF maze route was found for dungeon " + dungeonId + ".");
                    return 1;
                }
                Console.WriteLine(route.Describe());
                for (var y = 0; y < route.Height; y++)
                {
                    var row = new StringBuilder(route.Width * 5);
                    for (var x = 0; x < route.Width; x++)
                    {
                        if (x != 0) row.Append(' ');
                        row.Append(DescribeDoorMask(route.DoorMasks[x + y * route.Width]));
                    }
                    Console.WriteLine("y=" + y + " " + row);
                }
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Unable to read Script.pvf: " + exception.Message);
                return 2;
            }
        }
        if (HasFlag(args, "--reset"))
        {
            File.WriteAllText(statePath, "0\r\n", Encoding.ASCII);
            WriteLog(logPath, "cycle reset to 0");
            Console.WriteLine("Offline debug room cycle reset.");
            return 0;
        }

        var client = Path.GetFullPath(GetOption(args, "--client") ?? Path.Combine(workdir, "ARAD.exe"));
        if (!File.Exists(client))
        {
            Console.Error.WriteLine("ARAD.exe not found: " + client);
            return 2;
        }

        if (HasFlag(args, "--scan-room-coordinate-accesses"))
            return ScanRoomCoordinateAccesses(client);

        var findCallers = GetOption(args, "--find-callers");
        if (findCallers != null)
        {
            if (!TryParseAddress(findCallers, out var target))
            {
                Console.Error.WriteLine("Invalid call target: " + findCallers);
                return 2;
            }
            return FindDirectCallers(client, target);
        }

        var findMemoryReferences = GetOption(args, "--find-memory-references");
        if (findMemoryReferences != null)
        {
            if (!TryParseAddress(findMemoryReferences, out var target))
            {
                Console.Error.WriteLine("Invalid memory address: " + findMemoryReferences);
                return 2;
            }
            return FindDirectMemoryReferences(client, target);
        }

        var disassembleAddress = GetOption(args, "--disassemble");
        if (disassembleAddress != null)
        {
            if (!TryParseAddress(disassembleAddress, out var address))
            {
                Console.Error.WriteLine("Invalid virtual address: " + disassembleAddress);
                return 2;
            }
            var requestedCount = GetOption(args, "--count");
            var count = requestedCount != null && int.TryParse(requestedCount, out var parsedCount)
                ? parsedCount
                : 200;
            return Disassemble(client, address, count);
        }

        var resolveOfflineDungeon = GetOption(args, "--resolve-offline-dungeon");
        if (resolveOfflineDungeon != null)
        {
            if (!ushort.TryParse(resolveOfflineDungeon, out var dungeonId))
            {
                Console.Error.WriteLine("Invalid dungeon ID: " + resolveOfflineDungeon);
                return 2;
            }
            try
            {
                var mappings = BuildOfflineDungeonMappings(ResolvePvfPath(args, workdir));
                var mapping = mappings.FirstOrDefault(value => value.DungeonId == dungeonId);
                if (mapping.MapId == 0)
                {
                    Console.Error.WriteLine("No start map could be resolved from Script.pvf for dungeon " + dungeonId + ".");
                    return 1;
                }
                Console.WriteLine("dungeon=" + mapping.DungeonId + " start-map=" + mapping.MapId +
                    " start-room=(" + mapping.StartX + "," + mapping.StartY + ") width=" + mapping.Width);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Unable to read Script.pvf: " + exception.Message);
                return 2;
            }
        }

        var launchOfflineDungeonFix = HasFlag(args, "--launch-offline-dungeon-fix");
        var launchOfflineRoomMonitor = HasFlag(args, "--launch-offline-room-monitor");
        var launchOfflineRoomTrace = HasFlag(args, "--launch-offline-room-trace");
        var applyPvfRouteText = GetOption(args, "--apply-pvf-route");
        ushort? applyPvfRouteDungeon = null;
        if (applyPvfRouteText != null)
        {
            if (!ushort.TryParse(applyPvfRouteText, out var parsedDungeonId))
            {
                Console.Error.WriteLine("Invalid PVF route dungeon ID: " + applyPvfRouteText);
                return 2;
            }
            applyPvfRouteDungeon = parsedDungeonId;
            // A route patch is meaningful only while the offline room repair
            // is keeping the selected dungeon's first room coherent.
            launchOfflineRoomMonitor = true;
        }
        var forceRoomSlotText = GetOption(args, "--force-room-slot");
        int? forceRoomSlot = null;
        if (forceRoomSlotText != null)
        {
            if (!int.TryParse(forceRoomSlotText, out var parsedRoomSlot) ||
                parsedRoomSlot < 0 || parsedRoomSlot >= RoomWatchCellCount)
            {
                Console.Error.WriteLine("Invalid room slot (expected 0.." + (RoomWatchCellCount - 1) + "): " + forceRoomSlotText);
                return 2;
            }
            forceRoomSlot = parsedRoomSlot;
        }
        if ((launchOfflineDungeonFix || launchOfflineRoomMonitor || launchOfflineRoomTrace ||
                applyPvfRouteDungeon.HasValue || forceRoomSlot.HasValue) &&
            Process.GetProcessesByName("ARAD").Length != 0)
        {
            Console.Error.WriteLine("ARAD.exe is already running. Close it before starting the offline dungeon fix.");
            return 2;
        }

        var sequence = ReadSequence(statePath);
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        ProcessInformation process;
        var commandLine = new StringBuilder("\"").Append(client).Append("\"");
        if (!CreateProcess(client, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended,
                IntPtr.Zero, workdir, ref startup, out process))
        {
            Console.Error.WriteLine("CreateProcess failed: " + Marshal.GetLastWin32Error());
            return 3;
        }

        try
        {
            if (launchOfflineDungeonFix || launchOfflineRoomMonitor || launchOfflineRoomTrace ||
                forceRoomSlot.HasValue)
            {
                IReadOnlyList<OfflineDungeonMapping> mappings;
                PvfDungeonRoute? routePatch = null;
                try
                {
                    mappings = BuildOfflineDungeonMappings(ResolvePvfPath(args, workdir));
                    if (applyPvfRouteDungeon.HasValue &&
                        !TryBuildPvfDungeonRoute(ResolvePvfPath(args, workdir), applyPvfRouteDungeon.Value,
                            out routePatch))
                    {
                        Console.Error.WriteLine("No complete PVF maze route was found for dungeon " +
                            applyPvfRouteDungeon.Value + ".");
                        TerminateProcess(process.hProcess, 1);
                        return 4;
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("Unable to read Script.pvf: " + exception.Message);
                    TerminateProcess(process.hProcess, 1);
                    return 4;
                }

                if (!InstallOfflineDungeonFix(process.hProcess, mappings, routePatch,
                        out var offlineStateAddress))
                {
                    Console.Error.WriteLine("The offline dungeon fix was not installed. ARAD.exe was not started.");
                    TerminateProcess(process.hProcess, 1);
                    return 4;
                }
                IntPtr forcedRoomStateAddress = IntPtr.Zero;
                if (forceRoomSlot.HasValue &&
                    !InstallRoomSlotOverride(process.hProcess, forceRoomSlot.Value, out forcedRoomStateAddress))
                {
                    Console.Error.WriteLine("The room-slot override was not installed. ARAD.exe was not started.");
                    TerminateProcess(process.hProcess, 1);
                    return 4;
                }
                IntPtr roomTraceAddress = IntPtr.Zero;
                if (launchOfflineRoomTrace && !InstallRoomTrace(process.hProcess, out roomTraceAddress))
                {
                    Console.Error.WriteLine("The room trace was not installed. ARAD.exe was not started.");
                    TerminateProcess(process.hProcess, 1);
                    return 4;
                }
                IntPtr mapLoaderTraceAddress = IntPtr.Zero;
                if (launchOfflineRoomTrace && !InstallMapLoaderTrace(process.hProcess, out mapLoaderTraceAddress))
                {
                    Console.Error.WriteLine("The map-loader trace was not installed. ARAD.exe was not started.");
                    TerminateProcess(process.hProcess, 1);
                    return 4;
                }
                if (launchOfflineRoomTrace && !ArmMapLoaderTrace(process.hProcess, mapLoaderTraceAddress))
                {
                    TerminateProcess(process.hProcess, 1);
                    return 4;
                }
                if (ResumeThread(process.hThread) == uint.MaxValue)
                {
                    Console.Error.WriteLine("ResumeThread failed: " + Marshal.GetLastWin32Error());
                    TerminateProcess(process.hProcess, 1);
                    return 5;
                }

                var mountainRidge = mappings.FirstOrDefault(value => value.DungeonId == 40);
                Console.WriteLine("ARAD.exe started with the offline dungeon fallback fix. No server was started.");
                Console.WriteLine("Loaded " + mappings.Count + " PVF dungeon start-map mappings" +
                    (mountainRidge.MapId == 0 ? "." : "; dungeon 40 -> map " + mountainRidge.MapId +
                        ", room=(" + mountainRidge.StartX + "," + mountainRidge.StartY + "), slot=" +
                        (mountainRidge.StartX + mountainRidge.StartY * mountainRidge.Width) + "."));
                if (routePatch != null)
                    Console.WriteLine("PVF route patch armed: " + routePatch.Describe() +
                        ". It writes only after the matching dungeon scene table is ready.");
                if (forceRoomSlot.HasValue)
                    Console.WriteLine("Room lookup override is active: slot=" + forceRoomSlot.Value +
                        " (lookup index=" + (forceRoomSlot.Value * RoomLookupIndexScale) + ").");
                if (forceRoomSlot.HasValue)
                    return MonitorForcedRoomSlot(process.hProcess, process.dwProcessId,
                        forcedRoomStateAddress, forceRoomSlot.Value,
                        Path.Combine(workdir, "room-lookup-force.log"));
                if (launchOfflineRoomMonitor)
                {
                    var roomLogPath = Path.Combine(workdir, "room-memory-watch.log");
                    WriteLog(roomLogPath, "live monitor started pid=" + process.dwProcessId +
                        " room-table-pointer=0x" + RoomTablePointerGlobal.ToString("X8"));
                    Console.WriteLine("Live room monitor is active. Enter a dungeon normally; map ID and room cell address are printed below.");
                    return WatchRoomMemory((int)process.dwProcessId, roomLogPath, emitConsole: true,
                        waitUntilProcessExit: true, mappings: mappings,
                        relocationStateAddress: offlineStateAddress, routePatch: routePatch);
                }
                if (launchOfflineRoomTrace)
                {
                    var roomLogPath = Path.Combine(workdir, "room-lookup-trace.log");
                    var mapLogPath = Path.Combine(workdir, "map-loader-trace.log");
                    WriteLog(roomLogPath, "started pid=" + process.dwProcessId + " room-lookup=0x" +
                        RoomLookupHook.ToString("X8"));
                    WriteLog(mapLogPath, "started pid=" + process.dwProcessId + " map-loader=0x" +
                        MapLoaderHook.ToString("X8"));
                    Console.WriteLine("Room and transition traces are active. Enter a dungeon once; records are written to " + mapLogPath);
                    return MonitorMapLoaderTrace(process.hProcess, process.dwProcessId, mapLoaderTraceAddress, mapLogPath);
                }
                return 0;
            }

            if (HasFlag(args, "--launch-map-loader-trace"))
            {
                if (!InstallMapLoaderTrace(process.hProcess, out var mapLoaderTraceAddress))
                {
                    Console.Error.WriteLine("The map-loader trace was not installed. ARAD.exe was not started.");
                    TerminateProcess(process.hProcess, 1);
                    return 4;
                }
                if (ResumeThread(process.hThread) == uint.MaxValue)
                {
                    Console.Error.WriteLine("ResumeThread failed: " + Marshal.GetLastWin32Error());
                    TerminateProcess(process.hProcess, 1);
                    return 5;
                }

                Console.WriteLine("ARAD.exe started. Wait until it is idle in town, then press Enter to arm the map-loader trace.");
                Console.ReadLine();
                if (!ArmMapLoaderTrace(process.hProcess, mapLoaderTraceAddress)) return 4;
                WriteLog(Path.Combine(workdir, "map-loader-trace.log"), "armed pid=" + process.dwProcessId +
                    " map-loader=0x" + MapLoaderHook.ToString("X8"));
                Console.WriteLine("Trace armed. Enter the dungeon once, then close ARAD.exe or wait five minutes.");
                return MonitorMapLoaderTrace(process.hProcess, process.dwProcessId, mapLoaderTraceAddress,
                    Path.Combine(workdir, "map-loader-trace.log"));
            }

            if (!HasFlag(args, "--trace-map-index"))
            {
                if (ResumeThread(process.hThread) == uint.MaxValue)
                {
                    Console.Error.WriteLine("ResumeThread failed: " + Marshal.GetLastWin32Error());
                    TerminateProcess(process.hProcess, 1);
                    return 5;
                }
                Console.WriteLine("ARAD.exe started without a memory patch. DebugConfig.txt controls the offline settings.");
                return 0;
            }

            if (!InstallMapIndexTrace(process.hProcess, out var traceAddress))
            {
                Console.Error.WriteLine("The map-index trace was not installed. ARAD.exe was not started.");
                TerminateProcess(process.hProcess, 1);
                return 4;
            }

            if (ResumeThread(process.hThread) == uint.MaxValue)
            {
                Console.Error.WriteLine("ResumeThread failed: " + Marshal.GetLastWin32Error());
                TerminateProcess(process.hProcess, 1);
                return 5;
            }

            Console.WriteLine("ARAD.exe started. Enter the test dungeon within five minutes; the selected map index will be logged.");
            var trace = WaitForMapIndexTrace(process.hProcess, traceAddress, TimeSpan.FromMinutes(5));
            if (trace.HasValue)
            {
                var result = trace.Value;
                var message = result.Mode == MapIndexMode.EntryNumber
                    ? "pid=" + process.dwProcessId +
                      " map-index-mode=entry-number" +
                      " map-index-requested=" + result.Requested +
                      " map-index-maximum=" + result.Maximum +
                      " map-index-applied=" + result.Applied
                    : "pid=" + process.dwProcessId +
                      " map-index-mode=direct-map-id" +
                      " map-index-requested=" + result.Requested +
                      " map-index-passed-to-loader=" + result.Applied;
                WriteLog(logPath, message);
                Console.WriteLine("Recorded " + message);
            }
            else
            {
                WriteLog(logPath, "pid=" + process.dwProcessId + " map-index validation was not reached within five minutes");
                Console.WriteLine("Map-index validation was not reached within five minutes; ARAD.exe continues running.");
            }

            return 0;
        }
        finally
        {
            if (process.hThread != IntPtr.Zero) CloseHandle(process.hThread);
            if (process.hProcess != IntPtr.Zero) CloseHandle(process.hProcess);
        }
    }

    private static bool InstallMapIndexTrace(IntPtr process, out IntPtr traceAddress)
    {
        traceAddress = IntPtr.Zero;
        var originalEntry = new byte[ExpectedMapSelectionBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(MapSelectionHook), originalEntry, originalEntry.Length, out _) ||
            !originalEntry.SequenceEqual(ExpectedMapSelectionBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x00409435. This tracer only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalDirect = new byte[ExpectedDirectMapSelectionBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(DirectMapSelectionHook), originalDirect, originalDirect.Length, out _) ||
            !originalDirect.SequenceEqual(ExpectedDirectMapSelectionBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x004097E4. This tracer only supports the supplied ARAD.exe build.");
            return false;
        }

        var allocation = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)0x100,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (allocation == IntPtr.Zero || allocation.ToInt64() < 0 || allocation.ToInt64() > uint.MaxValue)
        {
            Console.Error.WriteLine("VirtualAllocEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var stubAddress = checked((uint)allocation.ToInt64());
        var directStubAddress = checked(stubAddress + 0x80);
        var stateAddress = checked(stubAddress + 0x100);
        var entryStub = BuildMapIndexTraceStub(stubAddress, stateAddress);
        var directStub = BuildDirectMapIndexTraceStub(directStubAddress, stateAddress);
        if (entryStub.Length > 0x80 || directStub.Length > 0x80)
        {
            Console.Error.WriteLine("The trace stubs do not fit in their reserved slots.");
            return false;
        }
        var payload = new byte[0x110];
        Buffer.BlockCopy(entryStub, 0, payload, 0, entryStub.Length);
        Buffer.BlockCopy(directStub, 0, payload, 0x80, directStub.Length);
        if (!WriteProcessMemory(process, allocation, payload, payload.Length, out _) ||
            !FlushInstructionCache(process, allocation, (UIntPtr)payload.Length))
        {
            Console.Error.WriteLine("Writing the map-index trace stub failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var entry = new byte[ExpectedMapSelectionBytes.Length];
        entry[0] = 0xE9;
        WriteRelative(entry, 1, checked(MapSelectionHook + 5), stubAddress);
        for (var index = 5; index < entry.Length; index++) entry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(MapSelectionHook), entry, entry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(MapSelectionHook), (UIntPtr)entry.Length))
        {
            Console.Error.WriteLine("Installing the map-index trace failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var directEntry = new byte[ExpectedDirectMapSelectionBytes.Length];
        directEntry[0] = 0xE9;
        WriteRelative(directEntry, 1, checked(DirectMapSelectionHook + 5), directStubAddress);
        for (var index = 5; index < directEntry.Length; index++) directEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(DirectMapSelectionHook), directEntry, directEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(DirectMapSelectionHook), (UIntPtr)directEntry.Length))
        {
            Console.Error.WriteLine("Installing the direct map-ID trace failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        traceAddress = new IntPtr(stateAddress);
        return true;
    }

    private static byte[] BuildMapIndexTraceStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        code.AddRange(new byte[] { 0x50, 0x51, 0x52, 0x56, 0x57 }); // Preserve caller state.
        code.AddRange(new byte[] { 0x8B, 0x55, 0xD8 }); // edx = [ebp-28h] (requested index)
        code.AddRange(new byte[] { 0x89, 0x15 }); AddUInt32(code, stateAddress);
        code.AddRange(new byte[] { 0x8B, 0x93, 0x04, 0x02, 0x00, 0x00 }); // edx = [ebx+204h] (maximum)
        code.AddRange(new byte[] { 0x89, 0x15 }); AddUInt32(code, checked(stateAddress + 4));
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 8));
        code.AddRange(BitConverter.GetBytes(1u)); // Mark trace complete after both values are written.
        code.AddRange(new byte[] { 0x5F, 0x5E, 0x5A, 0x59, 0x58 });
        code.AddRange(ExpectedMapSelectionBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(MapSelectionResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildDirectMapIndexTraceStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        code.AddRange(new byte[] { 0x50, 0x51, 0x52, 0x56, 0x57 }); // Preserve caller state.
        code.AddRange(new byte[] { 0x8B, 0x55, 0xD8 }); // edx = [ebp-28h] (direct map ID)
        code.AddRange(new byte[] { 0x89, 0x15 }); AddUInt32(code, stateAddress);
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 4));
        code.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 8));
        code.AddRange(new byte[] { 0x02, 0x00, 0x00, 0x00 }); // Mode: direct map ID.
        code.AddRange(new byte[] { 0x5F, 0x5E, 0x5A, 0x59, 0x58 });
        code.AddRange(ExpectedDirectMapSelectionBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(DirectMapSelectionResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static MapIndexTrace? WaitForMapIndexTrace(IntPtr process, IntPtr traceAddress, TimeSpan timeout)
    {
        var buffer = new byte[12];
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && WaitForSingleObject(process, 0) != WaitObject0)
        {
            if (ReadProcessMemory(process, traceAddress, buffer, buffer.Length, out _) &&
                BitConverter.ToUInt32(buffer, 8) is var mode && mode != 0)
            {
                var requested = BitConverter.ToUInt32(buffer, 0);
                var maximum = BitConverter.ToUInt32(buffer, 4);
                var entryNumberMode = mode == (uint)MapIndexMode.EntryNumber;
                var applied = entryNumberMode
                    ? (requested >= 1 && requested <= maximum ? requested : 1u)
                    : requested;
                return new MapIndexTrace((MapIndexMode)mode, requested, maximum, applied);
            }
            Thread.Sleep(50);
        }
        return null;
    }

    private static int AttachMapLoaderTrace(int processId, string logPath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                Console.Error.WriteLine("ARAD.exe has already exited: " + processId);
                return 2;
            }

            if (!InstallMapLoaderTrace(process.Handle, out var traceAddress)) return 4;
            if (!ArmMapLoaderTrace(process.Handle, traceAddress)) return 4;
            WriteLog(logPath, "attached pid=" + processId + " map-loader=0x" + MapLoaderHook.ToString("X8"));
            Console.WriteLine("Attached to ARAD.exe. Enter a dungeon without a server; map-loader calls will be written to " + logPath);
            return MonitorMapLoaderTrace(process.Handle, unchecked((uint)processId), traceAddress, logPath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Unable to attach map-loader trace: " + exception.Message);
            return 4;
        }
    }

    private static bool ArmMapLoaderTrace(IntPtr process, IntPtr traceAddress)
    {
        var armed = BitConverter.GetBytes(1u);
        var armedAddress = IntPtr.Add(traceAddress, MapLoaderTraceArmedOffset);
        if (WriteProcessMemory(process, armedAddress, armed, armed.Length, out _)) return true;
        Console.Error.WriteLine("Arming the map-loader trace failed: " + Marshal.GetLastWin32Error());
        return false;
    }

    private static int MonitorMapLoaderTrace(IntPtr process, uint processId, IntPtr traceAddress, string logPath)
    {
        var buffer = new byte[MapLoaderTraceFallbackArg2Offset + 4];
        var observed = 0u;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline && WaitForSingleObject(process, 0) != WaitObject0)
        {
            if (ReadProcessMemory(process, traceAddress, buffer, buffer.Length, out _))
            {
                var current = BitConverter.ToUInt32(buffer, 0);
                if (current != observed)
                {
                    var first = observed;
                    var delta = unchecked(current - observed);
                    if (delta > MapLoaderTraceEntryCount)
                    {
                        first = unchecked(current - MapLoaderTraceEntryCount);
                        WriteLog(logPath, "map-loader entries overwritten before polling: " + delta);
                    }
                    for (var sequence = first; sequence != current; sequence = unchecked(sequence + 1))
                    {
                        var slot = 16 + (int)(sequence & (MapLoaderTraceEntryCount - 1)) * 8;
                        var mapId = BitConverter.ToUInt32(buffer, slot);
                        var returnAddress = BitConverter.ToUInt32(buffer, slot + 4);
                        var message = "map-loader count=" + unchecked(sequence + 1) +
                            " map-id=" + mapId +
                            " caller=0x" + returnAddress.ToString("X8") +
                            " call-site=0x" + unchecked(returnAddress - 5).ToString("X8");
                        if (returnAddress == OfflineFallbackLoaderReturn)
                        {
                            message += " offline-transition-arg1=0x" +
                                BitConverter.ToUInt32(buffer, MapLoaderTraceFallbackArg1Offset).ToString("X8") +
                                " offline-transition-arg2=0x" +
                                BitConverter.ToUInt32(buffer, MapLoaderTraceFallbackArg2Offset).ToString("X8");
                            var roomTablePointer = new byte[sizeof(uint)];
                            if (ReadProcessMemory(process, new IntPtr(0x00ED041C), roomTablePointer,
                                    roomTablePointer.Length, out _))
                            {
                                var roomTable = BitConverter.ToUInt32(roomTablePointer, 0);
                                message += " room-table=0x" + roomTable.ToString("X8");
                                if (roomTable != 0)
                                {
                                    var firstCell = new byte[sizeof(uint)];
                                    if (ReadProcessMemory(process, new IntPtr(roomTable), firstCell,
                                            firstCell.Length, out _))
                                        message += " cell0-map=" + BitConverter.ToUInt32(firstCell, 0);
                                }
                            }
                        }
                        WriteLog(logPath, message);
                    }
                    observed = current;
                }
            }
            Thread.Sleep(50);
        }
        WriteLog(logPath, "map-loader trace ended pid=" + processId +
            (WaitForSingleObject(process, 0) == WaitObject0 ? " (process exited)" : " (five-minute timeout)"));
        return 0;
    }

    private static bool InstallMapLoaderTrace(IntPtr process, out IntPtr traceAddress)
    {
        traceAddress = IntPtr.Zero;
        var original = new byte[ExpectedMapLoaderBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(MapLoaderHook), original, original.Length, out _) ||
            !original.SequenceEqual(ExpectedMapLoaderBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + MapLoaderHook.ToString("X8") +
                ". Close ARAD.exe and attach only to the supplied client build.");
            return false;
        }

        var allocation = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)0x200,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (allocation == IntPtr.Zero || allocation.ToInt64() < 0 || allocation.ToInt64() > uint.MaxValue)
        {
            Console.Error.WriteLine("VirtualAllocEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var stubAddress = checked((uint)allocation.ToInt64());
        var stateAddress = checked(stubAddress + 0x100);
        var stub = BuildMapLoaderTraceStub(stubAddress, stateAddress);
        if (stub.Length > 0x100)
        {
            Console.Error.WriteLine("Map-loader trace stub is too large.");
            return false;
        }

        var payload = new byte[0x200];
        Buffer.BlockCopy(stub, 0, payload, 0, stub.Length);
        if (!WriteProcessMemory(process, allocation, payload, payload.Length, out _) ||
            !FlushInstructionCache(process, allocation, (UIntPtr)payload.Length))
        {
            Console.Error.WriteLine("Writing the map-loader trace stub failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var entry = new byte[ExpectedMapLoaderBytes.Length];
        entry[0] = 0xE9;
        WriteRelative(entry, 1, checked(MapLoaderHook + 5), stubAddress);
        if (!WriteProcessMemory(process, new IntPtr(MapLoaderHook), entry, entry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(MapLoaderHook), (UIntPtr)entry.Length))
        {
            Console.Error.WriteLine("Installing the map-loader trace failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        traceAddress = new IntPtr(stateAddress);
        return true;
    }

    private static byte[] BuildMapLoaderTraceStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        // At the function entry [esp] is the caller return address and [esp+4]
        // is the map ID. Preserve all state before replaying the overwritten prologue.
        code.Add(0x9C);                         // pushfd
        code.AddRange(new byte[] { 0x50, 0x51, 0x52 });
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + MapLoaderTraceArmedOffset));
        code.Add(0);
        code.Add(0x74);
        var disabledJumpOffset = code.Count;
        code.Add(0);
        code.AddRange(new byte[] { 0x8B, 0x44, 0x24, 0x14 }); // eax = map ID
        code.AddRange(new byte[] { 0x8B, 0x54, 0x24, 0x10 }); // edx = return address
        code.AddRange(new byte[] { 0x8B, 0x0D }); AddUInt32(code, stateAddress);
        code.AddRange(new byte[] { 0x83, 0xE1, MapLoaderTraceEntryCount - 1 });
        code.AddRange(new byte[] { 0x8D, 0x0C, 0xCD }); AddUInt32(code, checked(stateAddress + 16));
        code.AddRange(new byte[] { 0x89, 0x01, 0x89, 0x51, 0x04 });
        // At this point EBP is still the 00A59910 stack frame. Save its
        // inputs only when this is the offline fallback call.
        code.AddRange(new byte[] { 0x81, 0xFA }); AddUInt32(code, OfflineFallbackLoaderReturn);
        code.AddRange(new byte[] { 0x75, 0x10 });
        code.AddRange(new byte[] { 0x8B, 0x45, 0x08, 0xA3 }); AddUInt32(code, checked(stateAddress + MapLoaderTraceFallbackArg1Offset));
        code.AddRange(new byte[] { 0x8B, 0x45, 0x0C, 0xA3 }); AddUInt32(code, checked(stateAddress + MapLoaderTraceFallbackArg2Offset));
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, stateAddress);
        var restoreOffset = code.Count;
        code[disabledJumpOffset] = checked((byte)(restoreOffset - (disabledJumpOffset + 1)));
        code.AddRange(new byte[] { 0x5A, 0x59, 0x58, 0x9D });
        code.AddRange(ExpectedMapLoaderBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(MapLoaderResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static int AttachRoomTrace(int processId, string logPath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                Console.Error.WriteLine("ARAD.exe has already exited: " + processId);
                return 2;
            }

            if (!InstallRoomTrace(process.Handle, out var traceAddress)) return 4;
            WriteLog(logPath, "attached pid=" + processId + " room-lookup=0x" + RoomLookupHook.ToString("X8"));
            Console.WriteLine("Attached to ARAD.exe. Room lookup x/y/cell records will be written to " + logPath);
            return MonitorRoomTrace(process.Handle, unchecked((uint)processId), traceAddress, logPath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Unable to attach room trace: " + exception.Message);
            return 4;
        }
    }

    private static bool InstallRoomTrace(IntPtr process, out IntPtr traceAddress)
    {
        traceAddress = IntPtr.Zero;
        var original = new byte[ExpectedRoomLookupBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(RoomLookupHook), original, original.Length, out _) ||
            !original.SequenceEqual(ExpectedRoomLookupBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + RoomLookupHook.ToString("X8") +
                ". Close ARAD.exe and attach only to the supplied client build.");
            return false;
        }

        var allocation = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)0x2000,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (allocation == IntPtr.Zero || allocation.ToInt64() < 0 || allocation.ToInt64() > uint.MaxValue)
        {
            Console.Error.WriteLine("VirtualAllocEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var stubAddress = checked((uint)allocation.ToInt64());
        var stateAddress = checked(stubAddress + 0x100);
        var stub = BuildRoomTraceStub(stubAddress, stateAddress);
        if (stub.Length > 0x100 || RoomTraceRecordOffset + RoomTraceRecordCount * RoomTraceRecordSize > 0x1F00)
        {
            Console.Error.WriteLine("Room trace payload is too large.");
            return false;
        }

        var payload = new byte[0x2000];
        Buffer.BlockCopy(stub, 0, payload, 0, stub.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(1u), 0, payload, 0x100 + 4, sizeof(uint));
        if (!WriteProcessMemory(process, allocation, payload, payload.Length, out _) ||
            !FlushInstructionCache(process, allocation, (UIntPtr)payload.Length))
        {
            Console.Error.WriteLine("Writing the room trace stub failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var entry = new byte[ExpectedRoomLookupBytes.Length];
        entry[0] = 0xE9;
        WriteRelative(entry, 1, checked(RoomLookupHook + 5), stubAddress);
        if (!WriteProcessMemory(process, new IntPtr(RoomLookupHook), entry, entry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(RoomLookupHook), (UIntPtr)entry.Length))
        {
            Console.Error.WriteLine("Installing the room trace failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        traceAddress = new IntPtr(stateAddress);
        return true;
    }

    private static bool InstallRoomSlotOverride(IntPtr process, int roomSlot,
        out IntPtr stateAddress)
    {
        stateAddress = IntPtr.Zero;
        var original = new byte[8];
        if (!ReadProcessMemory(process, new IntPtr(RoomLookupHook), original, original.Length, out _) ||
            !original.Take(ExpectedRoomLookupBytes.Length).SequenceEqual(ExpectedRoomLookupBytes) ||
            original[5] != 0x8D || original[6] != 0x04 || original[7] != 0xD0)
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + RoomLookupHook.ToString("X8") +
                ". This room-slot probe only supports the supplied ARAD.exe build.");
            return false;
        }

        var allocation = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)0x200,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (allocation == IntPtr.Zero || allocation.ToInt64() < 0 ||
            allocation.ToInt64() > uint.MaxValue)
        {
            Console.Error.WriteLine("VirtualAllocEx failed for room-slot override: " +
                Marshal.GetLastWin32Error());
            return false;
        }

        var stubAddress = checked((uint)allocation.ToInt64());
        var state = checked(stubAddress + 0x100);
        var forcedIndex = checked((uint)(roomSlot * RoomLookupIndexScale));
        var stub = BuildRoomSlotOverrideStub(stubAddress, state, forcedIndex);
        var payload = new byte[0x110];
        Buffer.BlockCopy(stub, 0, payload, 0, stub.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(forcedIndex), 0, payload, 0x104, sizeof(uint));
        if (!WriteProcessMemory(process, allocation, payload, payload.Length, out _) ||
            !FlushInstructionCache(process, allocation, (UIntPtr)payload.Length))
        {
            Console.Error.WriteLine("Writing the room-slot override failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }

        var entry = new byte[5] { 0xE9, 0, 0, 0, 0 };
        WriteRelative(entry, 1, checked(RoomLookupHook + 5), stubAddress);
        if (!WriteProcessMemory(process, new IntPtr(RoomLookupHook), entry, entry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(RoomLookupHook), (UIntPtr)entry.Length))
        {
            Console.Error.WriteLine("Installing the room-slot override failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }

        stateAddress = new IntPtr(state);
        Console.WriteLine("Installed room-slot lookup override: slot=" + roomSlot +
            ", lookup-index=" + forcedIndex + ", hook=0x" + RoomLookupHook.ToString("X8"));
        return true;
    }

    private static byte[] BuildRoomSlotOverrideStub(uint stubAddress, uint stateAddress,
        uint forcedIndex)
    {
        var code = new List<byte>();
        code.Add(0x9C);                                      // pushfd
        code.Add(0x52);                                      // preserve caller edx
        code.Add(0x51);                                      // preserve caller ecx
        code.Add(0xA1); AddUInt32(code, RoomTablePointerGlobal); // mov eax,[room table]
        code.AddRange(new byte[] { 0x85, 0xC0 });             // test eax,eax
        code.Add(0x74);
        var noTableJump = code.Count;
        code.Add(0);                                         // -> original calculation
        code.Add(0xBA); AddUInt32(code, forcedIndex);        // mov edx,forced slot*5
        code.AddRange(new byte[] { 0x8D, 0x04, 0xD0 });      // lea eax,[eax+edx*8]
        code.Add(0x89); code.Add(0x15); AddUInt32(code, checked(stateAddress + 4));
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, stateAddress);
        code.Add(0xEB);
        var forcedDoneJump = code.Count;
        code.Add(0);                                         // -> restore

        var originalCalculation = code.Count;
        code.AddRange(new byte[] { 0x8D, 0x04, 0xD0 });      // original lea eax,[eax+edx*8]
        var restore = code.Count;
        code[noTableJump] = checked((byte)(originalCalculation - (noTableJump + 1)));
        code[forcedDoneJump] = checked((byte)(restore - (forcedDoneJump + 1)));
        code.Add(0x59);                                      // restore caller ecx
        code.Add(0x5A);                                      // restore caller edx
        code.Add(0x9D);                                      // restore flags
        code.Add(0xE9);
        var jumpNext = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(RoomLookupResume - jumpNext)));
        return code.ToArray();
    }

    private static int MonitorForcedRoomSlot(IntPtr process, uint processId,
        IntPtr stateAddress, int roomSlot, string logPath)
    {
        var buffer = new byte[8];
        var observed = 0u;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        WriteLog(logPath, "forced room-slot monitor started pid=" + processId +
            " slot=" + roomSlot + " lookup-index=" + (roomSlot * RoomLookupIndexScale));
        Console.WriteLine("Room-slot override monitor is active. Enter the dungeon within five minutes.");
        while (DateTime.UtcNow < deadline && WaitForSingleObject(process, 0) != WaitObject0)
        {
            if (ReadProcessMemory(process, stateAddress, buffer, buffer.Length, out _))
            {
                var count = BitConverter.ToUInt32(buffer, 0);
                if (count != observed)
                {
                    observed = count;
                    var forcedIndex = BitConverter.ToUInt32(buffer, 4);
                    var roomDetails = "table=unavailable";
                    var pointer = new byte[8];
                    IntPtr bytesRead;
                    if (ReadProcessMemory(process, new IntPtr(RoomTablePointerGlobal), pointer,
                        pointer.Length, out bytesRead) && bytesRead.ToInt64() == pointer.Length)
                    {
                        var roomBase = BitConverter.ToUInt32(pointer, 0);
                        var cellAddress = roomBase == 0 ? 0u :
                            checked(roomBase + (uint)(roomSlot * RoomCellSize));
                        var cell = new byte[RoomCellSize];
                        if (cellAddress != 0 && ReadProcessMemory(process,
                            new IntPtr((long)cellAddress), cell, cell.Length, out bytesRead) &&
                            bytesRead.ToInt64() == cell.Length)
                            roomDetails = "table=0x" + roomBase.ToString("X8") +
                                " cell=0x" + cellAddress.ToString("X8") +
                                " map=" + BitConverter.ToUInt32(cell, 0) +
                                " state=" + BitConverter.ToUInt32(cell, 0x24);
                    }
                    WriteLog(logPath, "forced room lookup count=" + count +
                        " slot=" + roomSlot + " lookup-index=" + forcedIndex +
                        " " + roomDetails);
                    Console.WriteLine("forced room lookup count=" + count +
                        " slot=" + roomSlot + " lookup-index=" + forcedIndex +
                        " " + roomDetails);
                }
            }
            Thread.Sleep(25);
        }
        WriteLog(logPath, "forced room-slot monitor ended pid=" + processId +
            (WaitForSingleObject(process, 0) == WaitObject0 ? " (process exited)" : " (five-minute timeout)"));
        return 0;
    }

    private static byte[] BuildRoomTraceStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        // Replay the two instructions which calculate the returned cell first.
        code.Add(0xA1); AddUInt32(code, 0x00ED041C);                 // mov eax,[room table]
        code.AddRange(new byte[] { 0x8D, 0x04, 0xD0 });             // lea eax,[eax+edx*8]
        // Preserve caller-visible state while recording the already computed cell.
        code.AddRange(new byte[] { 0x9C, 0x51, 0x52 });             // pushfd; push ecx; push edx
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 4));
        code.Add(0);
        code.Add(0x74);
        var skipTraceJump = code.Count;
        code.Add(0);
        code.AddRange(new byte[] { 0x8B, 0x15 }); AddUInt32(code, stateAddress); // edx=count
        code.AddRange(new byte[] { 0x81, 0xFA }); AddUInt32(code, RoomTraceRecordCount);
        code.Add(0x73);
        var fullTraceJump = code.Count;
        code.Add(0);
        code.AddRange(new byte[] { 0xC1, 0xE2, 0x04 });             // shl edx,4
        code.AddRange(new byte[] { 0x81, 0xC2 }); AddUInt32(code, checked(stateAddress + RoomTraceRecordOffset));
        code.AddRange(new byte[] { 0x8B, 0x4D, 0x04, 0x89, 0x0A }); // caller
        code.AddRange(new byte[] { 0x8B, 0x4D, 0x08, 0x89, 0x4A, 0x04 }); // x
        code.AddRange(new byte[] { 0x8B, 0x4D, 0x0C, 0x89, 0x4A, 0x08 }); // y
        code.AddRange(new byte[] { 0x89, 0x42, 0x0C });             // calculated cell
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, stateAddress);
        var restoreOffset = code.Count;
        code[skipTraceJump] = checked((byte)(restoreOffset - (skipTraceJump + 1)));
        code[fullTraceJump] = checked((byte)(restoreOffset - (fullTraceJump + 1)));
        code.AddRange(new byte[] { 0x5A, 0x59, 0x9D });             // pop edx; pop ecx; popfd
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(RoomLookupResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static int MonitorRoomTrace(IntPtr process, uint processId, IntPtr traceAddress, string logPath)
    {
        var buffer = new byte[RoomTraceRecordOffset + RoomTraceRecordCount * RoomTraceRecordSize];
        var observed = 0u;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline && WaitForSingleObject(process, 0) != WaitObject0)
        {
            if (ReadProcessMemory(process, traceAddress, buffer, buffer.Length, out _))
            {
                var current = Math.Min(BitConverter.ToUInt32(buffer, 0), (uint)RoomTraceRecordCount);
                for (var sequence = observed; sequence < current; sequence++)
                {
                    var offset = RoomTraceRecordOffset + (int)sequence * RoomTraceRecordSize;
                    var caller = BitConverter.ToUInt32(buffer, offset);
                    var x = BitConverter.ToUInt32(buffer, offset + 4);
                    var y = BitConverter.ToUInt32(buffer, offset + 8);
                    var cell = BitConverter.ToUInt32(buffer, offset + 12);
                    var cellBytes = new byte[0x28];
                    uint mapId = 0;
                    uint state = 0;
                    if (cell != 0 && ReadProcessMemory(process, new IntPtr(cell), cellBytes, cellBytes.Length, out _))
                    {
                        mapId = BitConverter.ToUInt32(cellBytes, 0);
                        state = BitConverter.ToUInt32(cellBytes, 0x24);
                    }
                    WriteLog(logPath, "room-lookup count=" + (sequence + 1) +
                        " caller=0x" + caller.ToString("X8") +
                        " x=" + x + " y=" + y +
                        " cell=0x" + cell.ToString("X8") +
                        " map=" + mapId + " state=" + state);
                }
                observed = current;
            }
            Thread.Sleep(50);
        }
        WriteLog(logPath, "room trace ended pid=" + processId +
            (WaitForSingleObject(process, 0) == WaitObject0 ? " (process exited)" : " (five-minute timeout)"));
        return 0;
    }

    private static int WatchRoomMemory(int processId, string logPath, bool emitConsole = false,
        bool waitUntilProcessExit = false, IReadOnlyList<OfflineDungeonMapping> mappings = null,
        IntPtr relocationStateAddress = default, PvfDungeonRoute? routePatch = null)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                Console.Error.WriteLine("ARAD.exe has already exited: " + processId);
                return 2;
            }

            var pointerBuffer = new byte[sizeof(uint) * 2];
            var cellsBuffer = new byte[RoomWatchCellCount * RoomCellSize];
            var previous = new RoomCellValue[RoomWatchCellCount];
            var previousBase = uint.MaxValue;
            var previousEnd = uint.MaxValue;
            var previousClientState = uint.MaxValue;
            var previousClientX = uint.MaxValue;
            var previousClientY = uint.MaxValue;
            var previousRoomAttributes = new RoomAttributeValue(uint.MaxValue, uint.MaxValue,
                uint.MaxValue, byte.MaxValue, byte.MaxValue);
            var previousTransitionState = uint.MaxValue;
            var previousTransitionX = uint.MaxValue;
            var previousTransitionY = uint.MaxValue;
            var previousRelocationCount = 0u;
            var previousRelocationStatus = byte.MaxValue;
            var previousRemapCount = 0u;
            var previousRemapEnabled = false;
            var previousCoordinateReapplyCount = 0u;
            var previousCoordinateUseCount = 0u;
            var previousDungeonRouteStateCount = 0u;
            var previousGameThreadGreedCount = 0u;
            var previousGameThreadGreedStatus = uint.MaxValue;
            var previousPreconstructCount = 0u;
            var previousBossRestoreCount = 0u;
            var forcedCoordinateRepairCount = 0u;
            var previousRoutePatch = PvfRoutePatchResult.None;
            string? previousRoutePatchStatus = null;
            var relocation = relocationStateAddress == IntPtr.Zero
                ? Array.Empty<byte>()
                : new byte[OfflineDungeonStateSize];
            var deadline = waitUntilProcessExit ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromMinutes(5);
            ReportRoomMemory(logPath, emitConsole,
                "watch started pid=" + processId + " room-table-pointer=0x" +
                RoomTablePointerGlobal.ToString("X8"));
            if (!emitConsole)
                Console.WriteLine("Watching room table memory for five minutes. Changes are written to " + logPath);

            while (DateTime.UtcNow < deadline && WaitForSingleObject(process.Handle, 0) != WaitObject0)
            {
                if (!ReadProcessMemory(process.Handle, new IntPtr(RoomTablePointerGlobal), pointerBuffer,
                        pointerBuffer.Length, out _))
                {
                    Thread.Sleep(2);
                    continue;
                }

                var roomBase = BitConverter.ToUInt32(pointerBuffer, 0);
                var roomEnd = BitConverter.ToUInt32(pointerBuffer, sizeof(uint));
                var roomCellCount = roomEnd >= roomBase && (roomEnd - roomBase) % RoomCellSize == 0
                    ? (int)Math.Min((roomEnd - roomBase) / RoomCellSize, RoomWatchCellCount)
                    : 0;
                var roomTableChanged = roomBase != previousBase || roomEnd != previousEnd;
                if (roomTableChanged)
                {
                    previousBase = roomBase;
                    previousEnd = roomEnd;
                    Array.Clear(previous, 0, previous.Length);
                    if (roomBase != 0 || roomEnd != 0)
                        ReportRoomMemory(logPath, emitConsole,
                            "room-table-base=0x" + roomBase.ToString("X8") +
                            " end=0x" + roomEnd.ToString("X8") + " slots=" + roomCellCount);
                }

                var hasRelocationState = relocation.Length != 0 &&
                    ReadProcessMemory(process.Handle, relocationStateAddress, relocation,
                        relocation.Length, out _);
                var capturedClientState = hasRelocationState
                    ? BitConverter.ToUInt32(relocation, 0x14)
                    : 0;
                var hasClientRoom = capturedClientState >= 0x10000
                    ? TryReadClientRoomStateAt(process.Handle, capturedClientState, out var clientLayout,
                        out var clientX, out var clientY)
                    : TryReadClientRoomState(process.Handle, out capturedClientState, out clientLayout,
                        out clientX, out clientY);
                var clientState = capturedClientState;
                var remapEnabled = hasRelocationState && relocation[0x20] != 0;
                if (hasClientRoom && remapEnabled)
                {
                    var packedRoom = BitConverter.ToUInt32(relocation, 8);
                    var targetX = packedRoom & 0xFF;
                    var targetY = (packedRoom >> 8) & 0xFF;
                    if (clientX != targetX || clientY != targetY)
                    {
                        // This is the position guard from the user-tested
                        // stable patch. The client writes (0,0) again after
                        // the offline transition returns, so the guard writes
                        // the PVF start coordinates back into the exact
                        // captured dungeon-state instance.
                        var coordinates = new byte[sizeof(uint) * 2];
                        Buffer.BlockCopy(BitConverter.GetBytes(targetX), 0, coordinates, 0, sizeof(uint));
                        Buffer.BlockCopy(BitConverter.GetBytes(targetY), 0, coordinates, sizeof(uint),
                            sizeof(uint));
                        if (WriteProcessMemory(process.Handle, new IntPtr(unchecked(clientState + 0xB8)),
                                coordinates, coordinates.Length, out _))
                        {
                            clientX = targetX;
                            clientY = targetY;
                            forcedCoordinateRepairCount++;
                            ReportRoomMemory(logPath, emitConsole,
                                "room-coordinate-guard repair=" + forcedCoordinateRepairCount +
                                " room=(" + targetX + "," + targetY + ") source=monitor-write");
                        }
                    }
                }
                var entryMask = (byte)0;
                if (hasClientRoom && !roomTableChanged && clientState == previousTransitionState)
                    entryMask = ResolveEntranceMask(previousTransitionX, previousTransitionY, clientX, clientY);
                if (hasClientRoom)
                {
                    previousTransitionState = clientState;
                    previousTransitionX = clientX;
                    previousTransitionY = clientY;
                }
                else
                {
                    previousTransitionState = uint.MaxValue;
                    previousTransitionX = uint.MaxValue;
                    previousTransitionY = uint.MaxValue;
                }
                var activeRoomChanged = hasClientRoom && (clientState != previousClientState ||
                    clientX != previousClientX || clientY != previousClientY);
                if (activeRoomChanged)
                {
                    previousClientState = clientState;
                    previousClientX = clientX;
                    previousClientY = clientY;
                    ReportRoomMemory(logPath, emitConsole,
                        "active-room=(" + clientX + "," + clientY + ") client-state=0x" +
                        clientState.ToString("X8") + " layout=0x" + clientLayout.ToString("X8") +
                        " source=ARAD.exe-memory");
                }

                // The offline fallback only creates one live scene: the PVF
                // start map can live in physical cell zero while its logical
                // PVF slot is elsewhere.  Write that actual scene's [greed]
                // mask.  Requiring a complete scene table here made this
                // branch wait forever and therefore write nothing at all.
                if (routePatch != null)
                {
                    var routeResult = PvfRoutePatchResult.None;
                    var routeStatus = hasClientRoom ? "unknown" : "client-state-not-ready";
                    var routeApplied = hasClientRoom && TryApplyPvfRouteDoorMasks(process.Handle, routePatch,
                        roomBase, roomCellCount, clientX, clientY, out routeResult, out routeStatus);
                    if (routeApplied && !routeResult.Equals(previousRoutePatch))
                    {
                        previousRoutePatch = routeResult;
                        previousRoutePatchStatus = "applied";
                        ReportRoomMemory(logPath, emitConsole,
                            "pvf-route-greed status=applied dungeon=" + routePatch.DungeonId +
                            " map=" + routePatch.StartMapId + " logical-slot=" + routeResult.LogicalSlot +
                            " logical-map=" + routeResult.LogicalMapId + " room=(" + routeResult.RoomX + "," +
                            routeResult.RoomY + ")" +
                            " physical-slot=" + routeResult.PhysicalSlot + " scene=0x" +
                            routeResult.Scene.ToString("X8") + " mask-before=0x" +
                            routeResult.MaskBefore.ToString("X2") + " mask-after=0x" +
                            routeResult.MaskAfter.ToString("X2") + " doors=" +
                            DescribeDoorMask(routeResult.MaskAfter) + " changed=" +
                            routeResult.Changed.ToString().ToLowerInvariant() + " route=" + routePatch.Describe() +
                            " source=ARAD.exe-memory");
                    }
                    else if (!routeApplied && !string.Equals(routeStatus, previousRoutePatchStatus,
                        StringComparison.Ordinal))
                    {
                        previousRoutePatchStatus = routeStatus;
                        ReportRoomMemory(logPath, emitConsole,
                            "pvf-route-greed status=waiting dungeon=" + routePatch.DungeonId +
                            " reason=" + routeStatus + " source=ARAD.exe-memory");
                    }
                }
                if (hasClientRoom && TryReadRoomAttributes(process.Handle, clientState, clientX, clientY, entryMask,
                        out var attributes) && !attributes.Equals(previousRoomAttributes))
                {
                    previousRoomAttributes = attributes;
                    var exitMask = (byte)(attributes.DoorMask & ~attributes.EntranceMask);
                    ReportRoomMemory(logPath, emitConsole,
                        "client-room-attributes room=(" + clientX + "," + clientY + ")" +
                        " boss-target=(" + attributes.BossX + "," + attributes.BossY + ")" +
                        " current-is-boss=" + (clientX == attributes.BossX && clientY == attributes.BossY)
                            .ToString().ToLowerInvariant() +
                        " door-mask=0x" + attributes.DoorMask.ToString("X2") +
                        " doors=" + DescribeDoorMask(attributes.DoorMask) +
                        " entered-from=" + (attributes.EntranceMask == 0
                            ? "unknown"
                            : DescribeDoorMask(attributes.EntranceMask)) +
                        " exit-mask=0x" + exitMask.ToString("X2") +
                        " exits=" + DescribeDoorMask(exitMask) +
                        " scene=0x" + attributes.Scene.ToString("X8") +
                        " source=ARAD.exe-memory");
                }
                else if (activeRoomChanged)
                {
                    ReportRoomMemory(logPath, emitConsole,
                        "client-room-attributes unavailable room=(" + clientX + "," + clientY +
                        ") reason=scene-not-ready-or-checked-state-mismatch source=ARAD.exe-memory");
                }

                if (hasRelocationState)
                {
                    var status = relocation[0x18];
                    if (status != 0 && status != previousRelocationStatus)
                    {
                        previousRelocationStatus = status;
                        ReportRoomMemory(logPath, emitConsole,
                            "room-relocation status=" + DescribeRelocationStatus(status));
                    }
                    var applied = BitConverter.ToUInt32(relocation, 0x0C);
                    if (applied != previousRelocationCount)
                    {
                        previousRelocationCount = applied;
                        var packedRoom = BitConverter.ToUInt32(relocation, 8);
                        ReportRoomMemory(logPath, emitConsole,
                            "room-relocation count=" + applied +
                            " logical-slot=" + BitConverter.ToUInt32(relocation, 4) +
                            " physical-slot=0" +
                            " room=(" + (packedRoom & 0xFF) + "," + ((packedRoom >> 8) & 0xFF) + ")" +
                            " physical-cell=0x" + BitConverter.ToUInt32(relocation, 0x10).ToString("X8") +
                            " client-state=0x" + BitConverter.ToUInt32(relocation, 0x14).ToString("X8"));
                    }
                    var remapCount = BitConverter.ToUInt32(relocation, 0x28);
                    if (remapEnabled != previousRemapEnabled || remapCount != previousRemapCount)
                    {
                        previousRemapEnabled = remapEnabled;
                        previousRemapCount = remapCount;
                        ReportRoomMemory(logPath, emitConsole,
                            "room-lookup-remap enabled=" + remapEnabled.ToString().ToLowerInvariant() +
                            " logical-slot=" + BitConverter.ToUInt32(relocation, 4) +
                            " physical-slot=0 hits=" + remapCount +
                            " table=0x" + BitConverter.ToUInt32(relocation, 0x2C).ToString("X8"));
                    }
                    var coordinateReapplyCount = BitConverter.ToUInt32(relocation, 0x30);
                    if (coordinateReapplyCount != previousCoordinateReapplyCount)
                    {
                        previousCoordinateReapplyCount = coordinateReapplyCount;
                        var packedRoom = BitConverter.ToUInt32(relocation, 8);
                        ReportRoomMemory(logPath, emitConsole,
                            "room-coordinate-reapply count=" + coordinateReapplyCount +
                            " room=(" + (packedRoom & 0xFF) + "," + ((packedRoom >> 8) & 0xFF) + ")" +
                            " after-call=0x00A58A60");
                    }
                    var coordinateUseCount = BitConverter.ToUInt32(relocation, 0x34);
                    if (coordinateUseCount != previousCoordinateUseCount)
                    {
                        previousCoordinateUseCount = coordinateUseCount;
                        ReportRoomMemory(logPath, emitConsole,
                            "room-coordinate-use-guard count=" + coordinateUseCount +
                            " hook=0x" + OfflineCoordinateUseHook.ToString("X8"));
                    }
                    var dungeonRouteStateCount = BitConverter.ToUInt32(relocation, 0x44);
                    if (dungeonRouteStateCount != previousDungeonRouteStateCount)
                    {
                        previousDungeonRouteStateCount = dungeonRouteStateCount;
                        ReportRoomMemory(logPath, emitConsole,
                            "route-state setters=" + dungeonRouteStateCount +
                            " boss=(" + BitConverter.ToUInt32(relocation, 0x3C) + "," +
                            BitConverter.ToUInt32(relocation, 0x40) +
                            ") flags=(0,0) source=ARAD.exe-native-setter");
                    }
                    var bossRestoreCount = BitConverter.ToUInt32(relocation, OfflineBossRestoreCountOffset);
                    if (bossRestoreCount != previousBossRestoreCount)
                    {
                        previousBossRestoreCount = bossRestoreCount;
                        ReportRoomMemory(logPath, emitConsole,
                            "boss-coordinate-restore count=" + bossRestoreCount +
                            " boss=(" + BitConverter.ToUInt32(relocation, 0x3C) + "," +
                            BitConverter.ToUInt32(relocation, 0x40) +
                            ") after-room-constructor=0x00486C40 source=ARAD.exe-native-setter");
                    }
                    var gameThreadGreedCount = BitConverter.ToUInt32(relocation, 0x50);
                    var gameThreadGreedStatus = BitConverter.ToUInt32(relocation, 0x54);
                    if (gameThreadGreedCount != previousGameThreadGreedCount ||
                        gameThreadGreedStatus != previousGameThreadGreedStatus)
                    {
                        previousGameThreadGreedCount = gameThreadGreedCount;
                        previousGameThreadGreedStatus = gameThreadGreedStatus;
                        ReportRoomMemory(logPath, emitConsole,
                            "pvf-route-greed-game-thread status=" +
                            DescribeGameThreadGreedStatus(gameThreadGreedStatus) +
                            " count=" + gameThreadGreedCount +
                            " start-map=" + BitConverter.ToUInt32(relocation, 0x48) +
                            " mask-before=0x" + BitConverter.ToUInt32(relocation, 0x58).ToString("X2") +
                            " mask-after=0x" + BitConverter.ToUInt32(relocation, 0x4C).ToString("X2") +
                            " doors=" + DescribeDoorMask((byte)BitConverter.ToUInt32(relocation, 0x4C)) +
                            " source=ARAD.exe-game-thread");
                    }
                    var preconstructCount = BitConverter.ToUInt32(relocation,
                        OfflineRoutePreconstructCountOffset);
                    if (preconstructCount != previousPreconstructCount)
                    {
                        previousPreconstructCount = preconstructCount;
                        ReportRoomMemory(logPath, emitConsole,
                            "pvf-route-preconstruct count=" + preconstructCount +
                            " start-map=" + BitConverter.ToUInt32(relocation, OfflineRouteStartMapOffset) +
                            " mask-before=0x" + BitConverter.ToUInt32(relocation,
                                OfflineRoutePreconstructMaskBeforeOffset).ToString("X2") +
                            " mask-after=0x" + BitConverter.ToUInt32(relocation,
                                OfflineRouteDoorMaskOffset).ToString("X2") +
                            " doors=" + DescribeDoorMask((byte)BitConverter.ToUInt32(relocation,
                                OfflineRouteDoorMaskOffset)) +
                            " before-constructor=0x0048E610 source=ARAD.exe-game-thread");
                    }
                }

                if (roomBase == 0 || roomCellCount == 0 ||
                    !ReadProcessMemory(process.Handle, new IntPtr(roomBase), cellsBuffer,
                        roomCellCount * RoomCellSize, out _))
                {
                    Thread.Sleep(2);
                    continue;
                }

                for (var slot = 0; slot < roomCellCount; slot++)
                {
                    var offset = slot * RoomCellSize;
                    var value = new RoomCellValue(BitConverter.ToUInt32(cellsBuffer, offset),
                        BitConverter.ToUInt32(cellsBuffer, offset + 0x24));
                    if (value.Equals(previous[slot])) continue;
                    if (value.MapId != 0 || value.State != 0 || previous[slot].MapId != 0 || previous[slot].State != 0)
                    {
                        var width = ResolveRoomWidth(mappings, value.MapId, roomCellCount);
                        var room = width > 0
                            ? "(" + (slot % width) + "," + (slot / width) + ")"
                            : "unknown";
                        ReportRoomMemory(logPath, emitConsole,
                            "room-slot=" + slot + " cell-room=" + room + " width=" + width +
                            " active-room=" + (hasClientRoom ? "(" + clientX + "," + clientY + ")" : "unknown") +
                            " map-id=" + value.MapId + " room-cell=0x" +
                            unchecked(roomBase + (uint)offset).ToString("X8") + " state=" + value.State +
                            " source=ARAD.exe-memory");
                    }
                    previous[slot] = value;
                }
                Thread.Sleep(2);
            }

            ReportRoomMemory(logPath, emitConsole, "room memory watch ended pid=" + processId +
                (WaitForSingleObject(process.Handle, 0) == WaitObject0 ? " (process exited)" : " (five-minute timeout)"));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Unable to watch room memory: " + exception.Message);
            return 4;
        }
    }

    private static bool TryReadClientRoomState(IntPtr process, out uint state, out uint layout,
        out uint roomX, out uint roomY)
    {
        state = layout = roomX = roomY = 0;
        var value = new byte[sizeof(uint)];
        if (!ReadProcessMemory(process, new IntPtr(ClientRootGlobal), value, value.Length, out _))
            return false;
        var root = BitConverter.ToUInt32(value, 0);
        if (root < 0x10000 ||
            !ReadProcessMemory(process, new IntPtr(unchecked(root + 0x86C)), value, value.Length, out _))
            return false;
        var owner = BitConverter.ToUInt32(value, 0);
        if (owner < 0x10000 ||
            !ReadProcessMemory(process, new IntPtr(unchecked(owner + 0x14)), value, value.Length, out _))
            return false;
        state = BitConverter.ToUInt32(value, 0);
        if (state < 0x10000) return false;

        return TryReadClientRoomStateAt(process, state, out layout, out roomX, out roomY);
    }

    private static bool TryReadClientRoomStateAt(IntPtr process, uint state, out uint layout,
        out uint roomX, out uint roomY)
    {
        layout = roomX = roomY = 0;
        var fields = new byte[12];
        if (!ReadProcessMemory(process, new IntPtr(unchecked(state + 0xB4)), fields,
                fields.Length, out _))
            return false;
        layout = BitConverter.ToUInt32(fields, 0);
        roomX = BitConverter.ToUInt32(fields, 4);
        roomY = BitConverter.ToUInt32(fields, 8);
        return layout >= 0x10000;
    }

    private static bool TryReadRoomAttributes(IntPtr process, uint clientState, uint roomX, uint roomY,
        byte entranceMask, out RoomAttributeValue attributes)
    {
        attributes = default;
        if (roomX > 63 || roomY > 63 ||
            !TryReadEncryptedStateValue(process, EncryptedBossXWrapper, out var bossX) ||
            !TryReadEncryptedStateValue(process, EncryptedBossYWrapper, out var bossY))
            return false;

        if (!TryReadActiveRoomGridWidth(process, clientState, out var gridWidth)) return false;

        var pointer = new byte[sizeof(uint)];
        if (!ReadProcessMemory(process, new IntPtr(RoomSceneTableGlobal), pointer, pointer.Length, out _))
            return false;
        var scenes = BitConverter.ToUInt32(pointer, 0);
        var sceneIndex = (ulong)roomX + (ulong)roomY * gridWidth;
        if (scenes < 0x10000 || sceneIndex > 0x3FFFFFFF ||
            !ReadProcessMemory(process, new IntPtr(unchecked(scenes + (uint)(sceneIndex * sizeof(uint)))),
                pointer, pointer.Length, out _))
            return false;
        var scene = BitConverter.ToUInt32(pointer, 0);
        if (scene < 0x10000) return false;

        // The client itself reads +0x238 through 0x00482780. Its neighboring
        // +0x234 pointer belongs to a separate navigation helper and is not
        // required for this native door-mask read.
        var sceneFields = new byte[RoomSceneDoorMaskOffset - RoomSceneDoorContextOffset + 1];
        if (!ReadProcessMemory(process, new IntPtr(unchecked(scene + RoomSceneDoorContextOffset)),
                sceneFields, sceneFields.Length, out _))
            return false;
        attributes = new RoomAttributeValue(bossX, bossY, scene,
            sceneFields[RoomSceneDoorMaskOffset - RoomSceneDoorContextOffset], entranceMask);
        return true;
    }

    private static bool TryReadActiveRoomGridWidth(IntPtr process, uint clientState, out uint width)
    {
        width = 0;
        if (!TryReadEncryptedStateValue(process, 0x00ED03D8, out var dimensionIndex)) return false;

        var fields = new byte[sizeof(uint)];
        if (!ReadProcessMemory(process, new IntPtr(unchecked(clientState + 0xB0)), fields, fields.Length, out _))
            return false;
        var dimensions = BitConverter.ToUInt32(fields, 0);
        if (dimensions < 0x10000 || dimensionIndex > 0x3FFFFFFF) return false;

        var pairOffset = checked(dimensionIndex * 8);
        if (!ReadProcessMemory(process, new IntPtr(unchecked(dimensions + pairOffset)), fields,
                fields.Length, out _))
            return false;
        width = BitConverter.ToUInt32(fields, 0);
        return width is > 0 and <= RoomWatchCellCount;
    }

    private static bool TryReadEncryptedStateValue(IntPtr process, uint wrapperAddress, out uint value)
    {
        value = 0;
        var wrapper = new byte[8];
        var globals = new byte[8];
        if (!ReadProcessMemory(process, new IntPtr(wrapperAddress), wrapper, wrapper.Length, out _) ||
            !ReadProcessMemory(process, new IntPtr(EncryptedStateContextGlobal), globals, globals.Length, out _))
            return false;
        var index = BitConverter.ToUInt32(wrapper, 4);
        var context = BitConverter.ToUInt32(globals, 0);
        var key = BitConverter.ToUInt32(globals, 4);
        if (context < 0x10000 || index > 0x3FFFFFFF) return false;

        var arrays = new byte[8];
        if (!ReadProcessMemory(process, new IntPtr(unchecked(context + 0x44)), arrays, arrays.Length, out _))
            return false;
        var firstArray = BitConverter.ToUInt32(arrays, 0);
        var secondArray = BitConverter.ToUInt32(arrays, 4);
        if (firstArray < 0x10000 || secondArray < 0x10000) return false;

        var storageOffset = checked(index * sizeof(uint));
        var encoded = new byte[sizeof(uint)];
        if (!ReadProcessMemory(process, new IntPtr(unchecked(firstArray + storageOffset)), encoded, encoded.Length, out _))
            return false;
        var firstEncoded = BitConverter.ToUInt32(encoded, 0);
        if (!ReadProcessMemory(process, new IntPtr(unchecked(secondArray + storageOffset)), encoded, encoded.Length, out _))
            return false;
        var secondEncoded = BitConverter.ToUInt32(encoded, 0);
        var first = firstEncoded ^ unchecked(firstArray + storageOffset) ^ key;
        var second = secondEncoded ^ unchecked(secondArray + storageOffset) ^ key;
        if (first != second || first > 255) return false;
        value = first;
        return true;
    }

    private static byte ResolveEntranceMask(uint previousX, uint previousY, uint currentX, uint currentY)
    {
        if (previousX == currentX + 1 && previousY == currentY) return 0x01; // came from right
        if (previousX + 1 == currentX && previousY == currentY) return 0x04; // came from left
        if (previousX == currentX && previousY == currentY + 1) return 0x08; // came from above
        if (previousX == currentX && previousY + 1 == currentY) return 0x02; // came from below
        return 0;
    }

    private static string DescribeDoorMask(byte mask)
    {
        var directions = new StringBuilder(4);
        if ((mask & 0x01) != 0) directions.Append('R');
        if ((mask & 0x02) != 0) directions.Append('D');
        if ((mask & 0x04) != 0) directions.Append('L');
        if ((mask & 0x08) != 0) directions.Append('U');
        return directions.Length == 0 ? "-" : directions.ToString();
    }

    private static string DescribeRelocationStatus(byte status)
    {
        return status switch
        {
            1 => "armed",
            2 => "finalizing",
            3 => "skipped-invalid-client-state",
            4 => "skipped-missing-layout",
            5 => "skipped-missing-room-table",
            6 => "skipped-slot-out-of-range",
            7 => "applied",
            8 => "skipped-physical-slot-not-loaded",
            _ => "unknown-" + status
        };
    }

    private static string DescribeGameThreadGreedStatus(uint status)
    {
        return status switch
        {
            0 => "waiting",
            1 => "no-selected-route",
            2 => "room-table-not-ready",
            3 => "start-cell-mismatch",
            4 => "scene-not-ready",
            5 => "applied",
            6 => "applied-preconstruct",
            _ => "unknown-" + status
        };
    }

    private static int ResolveRoomWidth(IReadOnlyList<OfflineDungeonMapping> mappings,
        uint mapId, int roomCellCount)
    {
        if (mappings != null)
        {
            foreach (var mapping in mappings)
                if (mapping.MapId == mapId && mapping.Width > 0)
                    return mapping.Width;
        }

        var square = (int)Math.Sqrt(Math.Max(0, roomCellCount));
        return square > 0 && square * square == roomCellCount ? square : 0;
    }

    private static void ReportRoomMemory(string logPath, bool emitConsole, string message)
    {
        WriteLog(logPath, message);
        if (emitConsole) Console.WriteLine(message);
    }

    private static string ResolvePvfPath(string[] args, string workdir)
    {
        return Path.GetFullPath(GetOption(args, "--pvf") ?? Path.Combine(workdir, "Script.pvf"));
    }

    private static IReadOnlyList<OfflineDungeonMapping> BuildOfflineDungeonMappings(string pvfPath)
    {
        if (!File.Exists(pvfPath)) throw new FileNotFoundException("Script.pvf was not found", pvfPath);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var catalog = new DungeonCatalog(new PvfArchiveReader(pvfPath));
        var mappings = new List<OfflineDungeonMapping>();
        for (var rawDungeonId = 0; rawDungeonId <= ushort.MaxValue; rawDungeonId++)
        {
            var dungeonId = (ushort)rawDungeonId;
            if (!catalog.TrySelectRunLayout(dungeonId, null, out var layout) ||
                !catalog.TryCreateRoom(layout, layout.StartX, layout.StartY, out var room) ||
                room.MapId == 0)
                continue;
            mappings.Add(new OfflineDungeonMapping(dungeonId, room.MapId, layout.StartX, layout.StartY,
                layout.Width, layout.BossX, layout.BossY));
        }
        return mappings;
    }

    private static bool TryBuildPvfDungeonRoute(string pvfPath, ushort dungeonId, out PvfDungeonRoute route)
    {
        route = null!;
        if (!File.Exists(pvfPath)) throw new FileNotFoundException("Script.pvf was not found", pvfPath);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var catalog = new DungeonCatalog(new PvfArchiveReader(pvfPath));
        if (!catalog.TrySelectRunLayout(dungeonId, null, out var layout) ||
            layout.Width == 0 || layout.Height == 0 ||
            layout.Width * layout.Height > RoomWatchCellCount ||
            layout.Maze.RoomTopology.Length != layout.Width * layout.Height ||
            !catalog.TryCreateRoom(layout, layout.StartX, layout.StartY, out var startRoom) ||
            startRoom.MapId == 0)
            return false;

        var masks = new byte[layout.Width * layout.Height];
        var mapIds = new uint[masks.Length];
        for (var slot = 0; slot < masks.Length; slot++)
        {
            // [greed] is a row-major y=0-at-top PVF grid.  This is exactly
            // the client coordinate system: slot = x + y * width.  Therefore
            // no Y flip is permitted when applying it to the scene table.
            var symbol = layout.Maze.RoomTopology[slot];
            var normalized = char.ToUpperInvariant(symbol);
            masks[slot] = normalized >= 'A' && normalized <= 'P'
                ? (byte)(normalized - 'A')
                : (byte)0;
            var x = (byte)(slot % layout.Width);
            var y = (byte)(slot / layout.Width);
            if (catalog.TryCreateRoom(layout, x, y, out var room))
                mapIds[slot] = room.MapId;
        }

        route = new PvfDungeonRoute(dungeonId, startRoom.MapId, layout.Width, layout.Height,
            layout.StartX, layout.StartY, layout.BossX, layout.BossY, masks, mapIds);
        return true;
    }

    private static bool TryApplyPvfRouteDoorMasks(IntPtr process, PvfDungeonRoute route,
        uint roomBase, int roomCellCount, uint roomX, uint roomY, out PvfRoutePatchResult result, out string status)
    {
        result = PvfRoutePatchResult.None;
        status = "unknown";
        // ARAD allocates a four-by-four physical room-cell buffer even for a
        // 3x4 dungeon such as Scasa's Lair. In DebugConfig/offline mode the
        // loader creates only the selected start-map scene, so this method
        // intentionally patches that real scene rather than assuming every
        // logical PVF cell already has a scene object.
        if (route == null || roomBase < 0x10000 || roomCellCount < route.CellCount)
        {
            status = "room-table-not-ready base=0x" + roomBase.ToString("X8") +
                " cells=" + roomCellCount;
            return false;
        }

        var cells = new byte[roomCellCount * RoomCellSize];
        if (!ReadProcessMemory(process, new IntPtr(roomBase), cells, cells.Length, out _))
        {
            status = "room-table-unreadable base=0x" + roomBase.ToString("X8");
            return false;
        }
        var physicalSlot = -1;
        for (var slot = 0; slot < roomCellCount; slot++)
        {
            if (BitConverter.ToUInt32(cells, slot * RoomCellSize) == route.StartMapId)
            {
                physicalSlot = slot;
                break;
            }
        }
        if (physicalSlot < 0)
        {
            status = "start-map-not-ready expected=" + route.StartMapId;
            return false;
        }

        var sceneTableBytes = new byte[sizeof(uint)];
        if (!ReadProcessMemory(process, new IntPtr(RoomSceneTableGlobal), sceneTableBytes,
                sceneTableBytes.Length, out _))
        {
            status = "scene-table-global-unreadable";
            return false;
        }
        var sceneTable = BitConverter.ToUInt32(sceneTableBytes, 0);
        if (sceneTable < 0x10000)
        {
            status = "scene-table-not-ready value=0x" + sceneTable.ToString("X8");
            return false;
        }

        var sceneBytes = new byte[sizeof(uint)];
        var sceneAddress = unchecked(sceneTable + (uint)(physicalSlot * sizeof(uint)));
        if (!ReadProcessMemory(process, new IntPtr(sceneAddress), sceneBytes, sceneBytes.Length, out _))
        {
            status = "scene-table-unreadable slot=" + physicalSlot + " table=0x" + sceneTable.ToString("X8");
            return false;
        }
        var scene = BitConverter.ToUInt32(sceneBytes, 0);
        if (scene < 0x10000)
        {
            status = "start-scene-not-ready physical-slot=" + physicalSlot + " scene=0x" + scene.ToString("X8");
            return false;
        }
        if (roomX >= route.Width || roomY >= route.Height)
        {
            status = "active-room-outside-pvf-route room=(" + roomX + "," + roomY + ")";
            return false;
        }
        var logicalSlot = checked((int)(roomX + roomY * route.Width));
        if (logicalSlot < 0 || logicalSlot >= route.CellCount || route.MapIds[logicalSlot] == 0)
        {
            status = "pvf-room-layout-mismatch logical-slot=" + logicalSlot;
            return false;
        }
        var mask = new byte[1];
        if (!ReadProcessMemory(process, new IntPtr(unchecked(scene + RoomSceneDoorMaskOffset)),
                mask, mask.Length, out _))
        {
            status = "start-scene-mask-unreadable scene=0x" + scene.ToString("X8");
            return false;
        }
        var previousMask = mask[0];
        var targetMask = route.DoorMasks[logicalSlot];
        // The scene needs its PVF topology before the native transition code
        // examines it.  The injected finalizer writes this byte in ARAD's game
        // thread; this monitor is intentionally read-only so it cannot race
        // the map loader or turn a late write into a misleading success log.
        if (previousMask != targetMask)
        {
            status = "scene-mask-mismatch expected=0x" + targetMask.ToString("X2") +
                " actual=0x" + previousMask.ToString("X2");
            return false;
        }
        result = new PvfRoutePatchResult(sceneTable, scene, logicalSlot, physicalSlot,
            route.MapIds[logicalSlot], (byte)roomX, (byte)roomY, previousMask, targetMask,
            false);
        status = "applied";
        return true;
    }

    private static bool InstallOfflineDungeonFix(IntPtr process, IReadOnlyList<OfflineDungeonMapping> mappings,
        PvfDungeonRoute? routePatch, out IntPtr statePointer)
    {
        statePointer = IntPtr.Zero;
        if (mappings == null || mappings.Count == 0)
        {
            Console.Error.WriteLine("The PVF did not contain any usable dungeon start maps.");
            return false;
        }

        var original = new byte[ExpectedOfflineFallbackBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineFallbackHook), original, original.Length, out _) ||
            !original.SequenceEqual(ExpectedOfflineFallbackBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineFallbackHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalMazeCapture = new byte[ExpectedOfflineMazeCaptureBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineMazeCaptureHook), originalMazeCapture,
                originalMazeCapture.Length, out _) ||
            !originalMazeCapture.SequenceEqual(ExpectedOfflineMazeCaptureBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineMazeCaptureHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalFinalize = new byte[ExpectedOfflineFinalizeBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineFinalizeHook), originalFinalize,
                originalFinalize.Length, out _) ||
            !originalFinalize.SequenceEqual(ExpectedOfflineFinalizeBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineFinalizeHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalStateCapture = new byte[ExpectedOfflineStateCaptureBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineStateCaptureHook), originalStateCapture,
                originalStateCapture.Length, out _) ||
            !originalStateCapture.SequenceEqual(ExpectedOfflineStateCaptureBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineStateCaptureHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalDungeonState = new byte[ExpectedOfflineDungeonStateBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineDungeonStateHook), originalDungeonState,
                originalDungeonState.Length, out _) ||
            !originalDungeonState.SequenceEqual(ExpectedOfflineDungeonStateBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineDungeonStateHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalRoomLookup = new byte[ExpectedRoomLookupBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(RoomLookupHook), originalRoomLookup,
                originalRoomLookup.Length, out _) ||
            !originalRoomLookup.SequenceEqual(ExpectedRoomLookupBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + RoomLookupHook.ToString("X8") +
                ". The logical-room remap only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalCoordinateReapply = new byte[ExpectedOfflineCoordinateReapplyBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineCoordinateReapplyHook), originalCoordinateReapply,
                originalCoordinateReapply.Length, out _) ||
            !originalCoordinateReapply.SequenceEqual(ExpectedOfflineCoordinateReapplyBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineCoordinateReapplyHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalCoordinateUse = new byte[ExpectedOfflineCoordinateUseBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineCoordinateUseHook), originalCoordinateUse,
                originalCoordinateUse.Length, out _) ||
            !originalCoordinateUse.SequenceEqual(ExpectedOfflineCoordinateUseBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineCoordinateUseHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalRoutePreconstruct = new byte[ExpectedOfflineRoutePreconstructBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineRoutePreconstructHook), originalRoutePreconstruct,
                originalRoutePreconstruct.Length, out _) ||
            !originalRoutePreconstruct.SequenceEqual(ExpectedOfflineRoutePreconstructBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineRoutePreconstructHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalRoutePreconstructAlternate =
            new byte[ExpectedOfflineRoutePreconstructAlternateBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineRoutePreconstructAlternateHook),
                originalRoutePreconstructAlternate, originalRoutePreconstructAlternate.Length, out _) ||
            !originalRoutePreconstructAlternate.SequenceEqual(ExpectedOfflineRoutePreconstructAlternateBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" +
                OfflineRoutePreconstructAlternateHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalSceneConstructed = new byte[ExpectedOfflineSceneConstructedBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineSceneConstructedHook), originalSceneConstructed,
                originalSceneConstructed.Length, out _) ||
            !originalSceneConstructed.SequenceEqual(ExpectedOfflineSceneConstructedBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineSceneConstructedHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalSceneConstructedAlternate = new byte[ExpectedOfflineSceneConstructedAlternateBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineSceneConstructedAlternateHook),
                originalSceneConstructedAlternate, originalSceneConstructedAlternate.Length, out _) ||
            !originalSceneConstructedAlternate.SequenceEqual(ExpectedOfflineSceneConstructedAlternateBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" +
                OfflineSceneConstructedAlternateHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var originalBossRestore = new byte[ExpectedOfflineBossRestoreBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(OfflineBossRestoreHook), originalBossRestore,
                originalBossRestore.Length, out _) ||
            !originalBossRestore.SequenceEqual(ExpectedOfflineBossRestoreBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + OfflineBossRestoreHook.ToString("X8") +
                ". This fix only supports the supplied ARAD.exe build.");
            return false;
        }
        var payloadLength = checked(OfflineDungeonMapTableOffset + sizeof(uint) +
            mappings.Count * OfflineDungeonMapRecordSize);
        var allocation = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)payloadLength,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (allocation == IntPtr.Zero || allocation.ToInt64() < 0 || allocation.ToInt64() > uint.MaxValue)
        {
            Console.Error.WriteLine("VirtualAllocEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var stubAddress = checked((uint)allocation.ToInt64());
        var mazeCaptureStubAddress = checked(stubAddress + OfflineMazeCaptureStubOffset);
        var stateCaptureStubAddress = checked(stubAddress + OfflineStateCaptureStubOffset);
        var finalizeStubAddress = checked(stubAddress + OfflineFinalizeStubOffset);
        var roomLookupStubAddress = checked(stubAddress + OfflineRoomLookupStubOffset);
        var coordinateReapplyStubAddress = checked(stubAddress + OfflineCoordinateReapplyStubOffset);
        var coordinateUseStubAddress = checked(stubAddress + OfflineCoordinateUseStubOffset);
        var dungeonStateStubAddress = checked(stubAddress + OfflineDungeonStateStubOffset);
        var routePreconstructStubAddress = checked(stubAddress + OfflineRoutePreconstructStubOffset);
        var routePreconstructAlternateStubAddress = checked(stubAddress +
            OfflineRoutePreconstructAlternateStubOffset);
        var sceneConstructedStubAddress = checked(stubAddress + OfflineSceneConstructedStubOffset);
        var sceneConstructedAlternateStubAddress = checked(stubAddress +
            OfflineSceneConstructedAlternateStubOffset);
        var bossRestoreStubAddress = checked(stubAddress + OfflineBossRestoreStubOffset);
        var stateAddress = checked(stubAddress + OfflineDungeonStateOffset);
        var mapTableAddress = checked(stubAddress + OfflineDungeonMapTableOffset);
        var stub = BuildOfflineDungeonFixStub(stubAddress, mapTableAddress, stateAddress);
        var mazeCaptureStub = BuildOfflineMazeCaptureStub(mazeCaptureStubAddress, mapTableAddress, stateAddress);
        var stateCaptureStub = BuildOfflineDungeonStateCaptureStub(stateCaptureStubAddress, stateAddress);
        var finalizeStub = BuildOfflineDungeonFinalizeStub(finalizeStubAddress, stateAddress);
        var roomLookupStub = BuildOfflineRoomLookupRemapStub(roomLookupStubAddress, stateAddress);
        var coordinateReapplyStub = BuildOfflineCoordinateReapplyStub(coordinateReapplyStubAddress, stateAddress);
        var coordinateUseStub = BuildOfflineCoordinateUseStub(coordinateUseStubAddress, stateAddress);
        var dungeonStateStub = BuildOfflineDungeonStateStub(dungeonStateStubAddress, stateAddress);
        var routePreconstructStub = BuildOfflineRoutePreconstructStub(routePreconstructStubAddress,
            stateAddress, ExpectedOfflineRoutePreconstructBytes, OfflineRoutePreconstructResume);
        var routePreconstructAlternateStub = BuildOfflineRoutePreconstructStub(
            routePreconstructAlternateStubAddress, stateAddress,
            ExpectedOfflineRoutePreconstructAlternateBytes, OfflineRoutePreconstructAlternateResume);
        var sceneConstructedStub = BuildOfflineSceneConstructedStub(sceneConstructedStubAddress, stateAddress,
            ExpectedOfflineSceneConstructedBytes, OfflineSceneConstructedResume);
        var sceneConstructedAlternateStub = BuildOfflineSceneConstructedStub(sceneConstructedAlternateStubAddress,
            stateAddress, ExpectedOfflineSceneConstructedAlternateBytes, OfflineSceneConstructedAlternateResume);
        var bossRestoreStub = BuildOfflineBossRestoreStub(bossRestoreStubAddress, stateAddress);
        if (stub.Length > OfflineMazeCaptureStubOffset ||
            mazeCaptureStub.Length > OfflineStateCaptureStubOffset - OfflineMazeCaptureStubOffset ||
            stateCaptureStub.Length > OfflineFinalizeStubOffset - OfflineStateCaptureStubOffset ||
            finalizeStub.Length > OfflineRoomLookupStubOffset - OfflineFinalizeStubOffset ||
            roomLookupStub.Length > OfflineCoordinateReapplyStubOffset - OfflineRoomLookupStubOffset ||
            coordinateReapplyStub.Length > OfflineCoordinateUseStubOffset - OfflineCoordinateReapplyStubOffset ||
            coordinateUseStub.Length > OfflineDungeonStateStubOffset - OfflineCoordinateUseStubOffset ||
            dungeonStateStub.Length > OfflineRoutePreconstructStubOffset - OfflineDungeonStateStubOffset ||
            routePreconstructStub.Length > OfflineRoutePreconstructAlternateStubOffset -
                OfflineRoutePreconstructStubOffset ||
            routePreconstructAlternateStub.Length > OfflineSceneConstructedStubOffset -
                OfflineRoutePreconstructAlternateStubOffset ||
            sceneConstructedStub.Length > OfflineSceneConstructedAlternateStubOffset -
                OfflineSceneConstructedStubOffset ||
            sceneConstructedAlternateStub.Length > OfflineBossRestoreStubOffset -
                OfflineSceneConstructedAlternateStubOffset ||
            bossRestoreStub.Length > OfflineDungeonStateOffset - OfflineBossRestoreStubOffset)
        {
            Console.Error.WriteLine("The offline dungeon stubs overlap their reserved data.");
            return false;
        }

        var payload = new byte[payloadLength];
        Buffer.BlockCopy(stub, 0, payload, 0, stub.Length);
        Buffer.BlockCopy(mazeCaptureStub, 0, payload, OfflineMazeCaptureStubOffset, mazeCaptureStub.Length);
        Buffer.BlockCopy(stateCaptureStub, 0, payload, OfflineStateCaptureStubOffset, stateCaptureStub.Length);
        Buffer.BlockCopy(finalizeStub, 0, payload, OfflineFinalizeStubOffset, finalizeStub.Length);
        Buffer.BlockCopy(roomLookupStub, 0, payload, OfflineRoomLookupStubOffset, roomLookupStub.Length);
        Buffer.BlockCopy(coordinateReapplyStub, 0, payload, OfflineCoordinateReapplyStubOffset,
            coordinateReapplyStub.Length);
        Buffer.BlockCopy(coordinateUseStub, 0, payload, OfflineCoordinateUseStubOffset,
            coordinateUseStub.Length);
        Buffer.BlockCopy(dungeonStateStub, 0, payload, OfflineDungeonStateStubOffset,
            dungeonStateStub.Length);
        Buffer.BlockCopy(routePreconstructStub, 0, payload, OfflineRoutePreconstructStubOffset,
            routePreconstructStub.Length);
        Buffer.BlockCopy(routePreconstructAlternateStub, 0, payload,
            OfflineRoutePreconstructAlternateStubOffset, routePreconstructAlternateStub.Length);
        Buffer.BlockCopy(sceneConstructedStub, 0, payload, OfflineSceneConstructedStubOffset,
            sceneConstructedStub.Length);
        Buffer.BlockCopy(sceneConstructedAlternateStub, 0, payload, OfflineSceneConstructedAlternateStubOffset,
            sceneConstructedAlternateStub.Length);
        Buffer.BlockCopy(bossRestoreStub, 0, payload, OfflineBossRestoreStubOffset, bossRestoreStub.Length);
        if (routePatch != null)
        {
            var startSlot = routePatch.StartX + routePatch.StartY * routePatch.Width;
            if (startSlot < 0 || startSlot >= routePatch.CellCount ||
                routePatch.MapIds[startSlot] != routePatch.StartMapId)
            {
                Console.Error.WriteLine("The selected PVF route has an invalid start-room topology.");
                return false;
            }
            Buffer.BlockCopy(BitConverter.GetBytes(routePatch.StartMapId), 0, payload,
                OfflineDungeonStateOffset + OfflineRouteStartMapOffset, sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes((uint)routePatch.DoorMasks[startSlot]), 0, payload,
                OfflineDungeonStateOffset + OfflineRouteDoorMaskOffset, sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes(1u), 0, payload,
                OfflineDungeonStateOffset + 0x5C, sizeof(uint));
        }
        Buffer.BlockCopy(BitConverter.GetBytes((uint)mappings.Count), 0, payload, OfflineDungeonMapTableOffset,
            sizeof(uint));
        var cursor = OfflineDungeonMapTableOffset + sizeof(uint);
        foreach (var mapping in mappings)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(mapping.DungeonId), 0, payload, cursor, sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes(mapping.MapId), 0, payload, cursor + sizeof(uint), sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes((uint)(mapping.StartX | mapping.StartY << 8)), 0, payload,
                cursor + 8, sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes((uint)mapping.Width), 0, payload,
                cursor + 12, sizeof(uint));
            Buffer.BlockCopy(BitConverter.GetBytes((uint)(mapping.BossX | mapping.BossY << 8)), 0, payload,
                cursor + 16, sizeof(uint));
            cursor += OfflineDungeonMapRecordSize;
        }

        if (!WriteProcessMemory(process, allocation, payload, payload.Length, out _) ||
            !FlushInstructionCache(process, allocation, (UIntPtr)payload.Length))
        {
            Console.Error.WriteLine("Writing the offline fallback stub failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var entry = new byte[5];
        entry[0] = 0xE9;
        WriteRelative(entry, 1, checked(OfflineFallbackHook + 5), stubAddress);
        if (!WriteProcessMemory(process, new IntPtr(OfflineFallbackHook), entry, entry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineFallbackHook), (UIntPtr)entry.Length))
        {
            Console.Error.WriteLine("Installing the offline fallback fix failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        var mazeCaptureEntry = new byte[ExpectedOfflineMazeCaptureBytes.Length];
        mazeCaptureEntry[0] = 0xE9;
        WriteRelative(mazeCaptureEntry, 1, checked(OfflineMazeCaptureHook + 5), mazeCaptureStubAddress);
        for (var index = 5; index < mazeCaptureEntry.Length; index++) mazeCaptureEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineMazeCaptureHook), mazeCaptureEntry,
                mazeCaptureEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineMazeCaptureHook),
                (UIntPtr)mazeCaptureEntry.Length))
        {
            Console.Error.WriteLine("Installing the offline dungeon route capture failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var stateCaptureEntry = new byte[ExpectedOfflineStateCaptureBytes.Length];
        stateCaptureEntry[0] = 0xE9;
        WriteRelative(stateCaptureEntry, 1, checked(OfflineStateCaptureHook + 5), stateCaptureStubAddress);
        for (var index = 5; index < stateCaptureEntry.Length; index++) stateCaptureEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineStateCaptureHook), stateCaptureEntry,
                stateCaptureEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineStateCaptureHook),
                (UIntPtr)stateCaptureEntry.Length))
        {
            Console.Error.WriteLine("Installing the offline dungeon-state capture failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var dungeonStateEntry = new byte[ExpectedOfflineDungeonStateBytes.Length];
        dungeonStateEntry[0] = 0xE9;
        WriteRelative(dungeonStateEntry, 1, checked(OfflineDungeonStateHook + 5), dungeonStateStubAddress);
        for (var index = 5; index < dungeonStateEntry.Length; index++) dungeonStateEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineDungeonStateHook), dungeonStateEntry,
                dungeonStateEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineDungeonStateHook),
                (UIntPtr)dungeonStateEntry.Length))
        {
            Console.Error.WriteLine("Installing the offline dungeon route-state fix failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var roomLookupEntry = new byte[ExpectedRoomLookupBytes.Length];
        roomLookupEntry[0] = 0xE9;
        WriteRelative(roomLookupEntry, 1, checked(RoomLookupHook + 5), roomLookupStubAddress);
        if (!WriteProcessMemory(process, new IntPtr(RoomLookupHook), roomLookupEntry,
                roomLookupEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(RoomLookupHook),
                (UIntPtr)roomLookupEntry.Length))
        {
            Console.Error.WriteLine("Installing the logical-room remap failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var finalizeEntry = new byte[ExpectedOfflineFinalizeBytes.Length];
        finalizeEntry[0] = 0xE9;
        WriteRelative(finalizeEntry, 1, checked(OfflineFinalizeHook + 5), finalizeStubAddress);
        if (!WriteProcessMemory(process, new IntPtr(OfflineFinalizeHook), finalizeEntry,
                finalizeEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineFinalizeHook),
                (UIntPtr)finalizeEntry.Length))
        {
            Console.Error.WriteLine("Installing the offline room finalizer failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var coordinateReapplyEntry = new byte[ExpectedOfflineCoordinateReapplyBytes.Length];
        coordinateReapplyEntry[0] = 0xE9;
        WriteRelative(coordinateReapplyEntry, 1, checked(OfflineCoordinateReapplyHook + 5),
            coordinateReapplyStubAddress);
        if (!WriteProcessMemory(process, new IntPtr(OfflineCoordinateReapplyHook), coordinateReapplyEntry,
                coordinateReapplyEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineCoordinateReapplyHook),
                (UIntPtr)coordinateReapplyEntry.Length))
        {
            Console.Error.WriteLine("Installing the post-commit room-coordinate fix failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var coordinateUseEntry = new byte[ExpectedOfflineCoordinateUseBytes.Length];
        coordinateUseEntry[0] = 0xE9;
        WriteRelative(coordinateUseEntry, 1, checked(OfflineCoordinateUseHook + 5), coordinateUseStubAddress);
        for (var index = 5; index < coordinateUseEntry.Length; index++) coordinateUseEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineCoordinateUseHook), coordinateUseEntry,
                coordinateUseEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineCoordinateUseHook),
                (UIntPtr)coordinateUseEntry.Length))
        {
            Console.Error.WriteLine("Installing the room-coordinate use guard failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var routePreconstructEntry = new byte[ExpectedOfflineRoutePreconstructBytes.Length];
        routePreconstructEntry[0] = 0xE9;
        WriteRelative(routePreconstructEntry, 1, checked(OfflineRoutePreconstructHook + 5),
            routePreconstructStubAddress);
        for (var index = 5; index < routePreconstructEntry.Length; index++)
            routePreconstructEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineRoutePreconstructHook), routePreconstructEntry,
                routePreconstructEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineRoutePreconstructHook),
                (UIntPtr)routePreconstructEntry.Length))
        {
            Console.Error.WriteLine("Installing the pre-construction PVF route patch failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var routePreconstructAlternateEntry =
            new byte[ExpectedOfflineRoutePreconstructAlternateBytes.Length];
        routePreconstructAlternateEntry[0] = 0xE9;
        WriteRelative(routePreconstructAlternateEntry, 1,
            checked(OfflineRoutePreconstructAlternateHook + 5), routePreconstructAlternateStubAddress);
        for (var index = 5; index < routePreconstructAlternateEntry.Length; index++)
            routePreconstructAlternateEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineRoutePreconstructAlternateHook),
                routePreconstructAlternateEntry, routePreconstructAlternateEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineRoutePreconstructAlternateHook),
                (UIntPtr)routePreconstructAlternateEntry.Length))
        {
            Console.Error.WriteLine("Installing the alternate pre-construction PVF route patch failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var sceneConstructedEntry = new byte[ExpectedOfflineSceneConstructedBytes.Length];
        sceneConstructedEntry[0] = 0xE9;
        WriteRelative(sceneConstructedEntry, 1, checked(OfflineSceneConstructedHook + 5),
            sceneConstructedStubAddress);
        for (var index = 5; index < sceneConstructedEntry.Length; index++) sceneConstructedEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineSceneConstructedHook), sceneConstructedEntry,
                sceneConstructedEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineSceneConstructedHook),
                (UIntPtr)sceneConstructedEntry.Length))
        {
            Console.Error.WriteLine("Installing the post-construction PVF route patch failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var sceneConstructedAlternateEntry = new byte[ExpectedOfflineSceneConstructedAlternateBytes.Length];
        sceneConstructedAlternateEntry[0] = 0xE9;
        WriteRelative(sceneConstructedAlternateEntry, 1, checked(OfflineSceneConstructedAlternateHook + 5),
            sceneConstructedAlternateStubAddress);
        for (var index = 5; index < sceneConstructedAlternateEntry.Length; index++)
            sceneConstructedAlternateEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineSceneConstructedAlternateHook),
                sceneConstructedAlternateEntry, sceneConstructedAlternateEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineSceneConstructedAlternateHook),
                (UIntPtr)sceneConstructedAlternateEntry.Length))
        {
            Console.Error.WriteLine("Installing the alternate post-construction PVF route patch failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        var bossRestoreEntry = new byte[ExpectedOfflineBossRestoreBytes.Length];
        bossRestoreEntry[0] = 0xE9;
        WriteRelative(bossRestoreEntry, 1, checked(OfflineBossRestoreHook + 5), bossRestoreStubAddress);
        for (var index = 5; index < bossRestoreEntry.Length; index++) bossRestoreEntry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(OfflineBossRestoreHook), bossRestoreEntry,
                bossRestoreEntry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(OfflineBossRestoreHook),
                (UIntPtr)bossRestoreEntry.Length))
        {
            Console.Error.WriteLine("Installing the post-construction boss-coordinate restore failed: " +
                Marshal.GetLastWin32Error());
            return false;
        }
        statePointer = new IntPtr(stateAddress);
        return true;
    }

    private static byte[] BuildOfflineDungeonFixStub(uint stubAddress, uint mapTableAddress,
        uint stateAddress)
    {
        var code = new List<byte>();
        // The original caller stores the selected dungeon ID at [ebp+8].
        code.AddRange(new byte[] { 0x8B, 0x55, 0x08 }); // mov edx,[ebp+8]
        code.Add(0xA1); AddUInt32(code, mapTableAddress); // eax = table count
        code.Add(0xBF); AddUInt32(code, checked(mapTableAddress + sizeof(uint))); // edi = first record
        code.AddRange(new byte[] { 0x85, 0xC0, 0x74 }); // test eax,eax; jz fallback
        var emptyTableJumpOffset = code.Count;
        code.Add(0);

        var lookupLoopOffset = code.Count;
        code.AddRange(new byte[] { 0x39, 0x17, 0x75 }); // cmp [edi],edx; jne next
        var nextRecordJumpOffset = code.Count;
        code.Add(0);
        // Keep the offline loader's only live object in physical slot 0. The
        // room lookup hook maps the PVF logical slot back to that object.
        code.AddRange(new byte[] { 0x8B, 0x57, 0x04 }); // edx = map ID
        code.AddRange(new byte[] { 0x89, 0x15 }); AddUInt32(code, checked(stateAddress + 0x1C));
        code.AddRange(new byte[] { 0x8B, 0x4F, 0x08 }); // ecx = packed start room
        code.AddRange(new byte[] { 0x89, 0x0D }); AddUInt32(code, checked(stateAddress + 8));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0x47, 0x09 }); // eax = y
        code.AddRange(new byte[] { 0x0F, 0xAF, 0x47, 0x0C }); // eax = y * width
        code.AddRange(new byte[] { 0x0F, 0xB6, 0x4F, 0x08 }); // ecx = x
        code.AddRange(new byte[] { 0x03, 0xC1 }); // eax = slot
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 4));
        code.AddRange(new byte[] { 0x8B, 0x47, 0x10 }); // eax = packed boss room
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 0x38));
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 4)); // eax = logical start slot
        code.AddRange(new byte[] { 0x8D, 0x0C, 0x80 }); // ecx=slot*5 (lookup units)
        code.AddRange(new byte[] { 0x89, 0x0D }); AddUInt32(code, checked(stateAddress + 0x24));
        code.AddRange(new byte[] { 0xC6, 0x05 }); AddUInt32(code, checked(stateAddress + 0x20));
        code.Add(0); // disable any previous table's remap while loading
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, stateAddress);
        AddUInt32(code, 1); // arm post-load relocation
        code.AddRange(new byte[] { 0xC6, 0x05 }); AddUInt32(code, checked(stateAddress + 0x18));
        code.Add(1); // armed
        code.Add(0xEB);
        var foundUseMapJumpOffset = code.Count;
        code.Add(0);

        var nextRecordOffset = code.Count;
        code.AddRange(new byte[] { 0x83, 0xC7, OfflineDungeonMapRecordSize, 0x48, 0x75 }); // add edi,16; dec eax; jnz loop
        var continueLookupJumpOffset = code.Count;
        code.Add(0);

        var fallbackOffset = code.Count;
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, stateAddress);
        AddUInt32(code, 0); // unknown dungeon: retain the client's cell-0 behavior
        code.AddRange(new byte[] { 0xC6, 0x05 }); AddUInt32(code, checked(stateAddress + 0x18));
        code.Add(0);
        code.AddRange(new byte[] { 0xC6, 0x05 }); AddUInt32(code, checked(stateAddress + 0x20));
        code.Add(0);
        code.Add(0xBA); AddUInt32(code, DefaultOfflineFallbackMap); // mov edx,1001

        var useMapOffset = code.Count;
        code.AddRange(new byte[] { 0x8B, 0x0D }); AddUInt32(code, RoomTablePointerGlobal); // ecx = base
        code.AddRange(new byte[] { 0x89, 0xC8, 0x85, 0xC0, 0x74 }); // eax=base; test; jz prepare
        var noCellJumpOffset = code.Count;
        code.Add(0);

        code.AddRange(new byte[] { 0x89, 0x10 }); // mov [eax],edx

        var prepareOffset = code.Count;
        code.AddRange(new byte[] { 0x8B, 0x0D }); AddUInt32(code, 0x00ECEC48); // mov ecx,[0ECEC48h]
        code.Add(0x52); // push edx
        code.AddRange(new byte[] { 0x89, 0x45, 0xE8 }); // mov [ebp-18h],eax
        code.Add(0xE8);
        var loaderCallNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(MapLoaderHook - loaderCallNextInstruction)));
        code.Add(0xE9);
        var resumeJumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineFallbackResume - resumeJumpNextInstruction)));

        code[emptyTableJumpOffset] = checked((byte)(fallbackOffset - (emptyTableJumpOffset + 1)));
        code[nextRecordJumpOffset] = checked((byte)(nextRecordOffset - (nextRecordJumpOffset + 1)));
        code[foundUseMapJumpOffset] = checked((byte)(useMapOffset - (foundUseMapJumpOffset + 1)));
        code[continueLookupJumpOffset] = unchecked((byte)(lookupLoopOffset - (continueLookupJumpOffset + 1)));
        code[noCellJumpOffset] = checked((byte)(prepareOffset - (noCellJumpOffset + 1)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineMazeCaptureStub(uint stubAddress, uint mapTableAddress,
        uint stateAddress)
    {
        var code = new List<byte>();

        // [ebp+8] is the dungeon ID selected by the town transition.  Preserve
        // every caller register and retain only PVF's packed boss coordinate.
        // The original instructions are replayed unchanged below, including
        // the offline flow's fixed maze index zero.
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x8B, 0x55, 0x08 }); // mov edx,[ebp+8]
        code.Add(0xA1); AddUInt32(code, mapTableAddress); // eax=count
        code.Add(0xBF); AddUInt32(code, checked(mapTableAddress + sizeof(uint))); // edi=first record
        code.AddRange(new byte[] { 0x85, 0xC0, 0x74 }); // test eax,eax; jz clear
        var clearForEmptyJump = code.Count;
        code.Add(0);

        var loopOffset = code.Count;
        code.AddRange(new byte[] { 0x39, 0x17, 0x74 }); // cmp [edi],edx; je found
        var foundJump = code.Count;
        code.Add(0);
        code.AddRange(new byte[] { 0x83, 0xC7, OfflineDungeonMapRecordSize, 0x48, 0x75 });
        var loopJump = code.Count;
        code.Add(0);

        var clearOffset = code.Count;
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 0x34));
        AddUInt32(code, 0); // no selected PVF route
        code.Add(0xEB);
        var finishFromClearJump = code.Count;
        code.Add(0);

        var foundOffset = code.Count;
        code.AddRange(new byte[] { 0x8B, 0x47, 0x10 }); // eax=packed boss room
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 0x38));
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 0x34));
        AddUInt32(code, 1); // selected PVF route is ready for the later setter

        var finishOffset = code.Count;
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(ExpectedOfflineMazeCaptureBytes);
        code.Add(0xE9);
        var resumeNext = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineMazeCaptureResume - resumeNext)));

        code[clearForEmptyJump] = checked((byte)(clearOffset - (clearForEmptyJump + 1)));
        code[foundJump] = checked((byte)(foundOffset - (foundJump + 1)));
        code[loopJump] = unchecked((byte)(loopOffset - (loopJump + 1)));
        code[finishFromClearJump] = checked((byte)(finishOffset - (finishFromClearJump + 1)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineDungeonStateStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        var skipJumps = new List<int>();

        void AddSkipJump(byte condition)
        {
            code.Add(condition);
            skipJumps.Add(code.Count);
            code.Add(0);
        }

        void AddSetter(uint valueAddress, uint setterGlobal)
        {
            code.Add(0x68); AddUInt32(code, valueAddress); // push &value
            code.Add(0xB9); AddUInt32(code, setterGlobal); // mov ecx, setter
            code.Add(0xE8);
            var callNext = checked(stubAddress + (uint)code.Count + 4);
            AddInt32(code, unchecked((int)(DungeonStateSetter - callNext)));
        }

        // 0x00A59B3D clears the same four globals that DUNGEON_INFO initializes.
        // We run after that clear and use the original setter so the values stay
        // coherent with the client's pointer-encryption bookkeeping.  The two
        // trailing route flags belong to packet context and must remain zero in
        // DebugConfig/offline mode.
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 0x34));
        code.Add(1); // cmp dword ptr [armed],1
        AddSkipJump(0x75); // jne skip

        // Use an out-of-grid sentinel until the native scene constructor is
        // finished. The constructor must not see either the PVF boss or the
        // start coordinate as its boss target.
        code.Add(0xB8); AddUInt32(code, 0xFF); // mov eax, 255
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 0x3C));
        AddSetter(checked(stateAddress + 0x3C), 0x00ED03E0);

        code.Add(0xB8); AddUInt32(code, 0xFF); // mov eax, 255
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 0x40));
        AddSetter(checked(stateAddress + 0x40), 0x00ED03E8);
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 0x44));
        code.AddRange(new byte[] { 0xC7, 0x05 });
        AddUInt32(code, checked(stateAddress + OfflineBossRestoreArmedOffset));
        AddUInt32(code, 1); // restore the true PVF boss after construction

        var skipOffset = code.Count;
        foreach (var displacement in skipJumps)
            code[displacement] = checked((byte)(skipOffset - (displacement + 1)));
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(ExpectedOfflineDungeonStateBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineDungeonStateResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineBossRestoreStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();

        void AddSetter(uint valueAddress, uint setterGlobal)
        {
            code.Add(0x68); AddUInt32(code, valueAddress); // push &value
            code.Add(0xB9); AddUInt32(code, setterGlobal); // mov ecx, encrypted wrapper
            code.Add(0xE8);
            var callNext = checked(stubAddress + (uint)code.Count + 4);
            AddInt32(code, unchecked((int)(DungeonStateSetter - callNext)));
        }

        // This outer call-site executes after 0x00486C40 returns, outside its
        // exception-frame epilogue. Restore the real PVF boss safely here.
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D });
        AddUInt32(code, checked(stateAddress + OfflineBossRestoreArmedOffset));
        code.Add(1); // cmp dword ptr [restore armed],1
        code.Add(0x75); // jne finish
        var finishJumpOffset = code.Count;
        code.Add(0);
        code.AddRange(new byte[] { 0xC7, 0x05 });
        AddUInt32(code, checked(stateAddress + OfflineBossRestoreArmedOffset));
        AddUInt32(code, 0);

        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 0x38)); // packed PVF boss room
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC0 }); // eax = boss X
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 0x3C));
        AddSetter(checked(stateAddress + 0x3C), EncryptedBossXWrapper);

        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 0x38));
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 }); // eax = boss Y
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + 0x40));
        AddSetter(checked(stateAddress + 0x40), EncryptedBossYWrapper);
        code.AddRange(new byte[] { 0xFF, 0x05 });
        AddUInt32(code, checked(stateAddress + OfflineBossRestoreCountOffset));

        var finishOffset = code.Count;
        code[finishJumpOffset] = checked((byte)(finishOffset - (finishJumpOffset + 1)));
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(ExpectedOfflineBossRestoreBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineBossRestoreResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineDungeonStateCaptureStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        code.AddRange(ExpectedOfflineStateCaptureBytes);
        code.AddRange(new byte[] { 0x89, 0x3D }); // mov [captured client state],edi
        AddUInt32(code, checked(stateAddress + 0x14));
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineStateCaptureResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineDungeonFinalizeStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();

        int AddFinishJump(byte condition)
        {
            code.AddRange(new byte[] { 0x0F, condition });
            var displacement = code.Count;
            AddInt32(code, 0);
            finishJumps.Add(displacement);
            return displacement;
        }

        void AddStatus(byte status)
        {
            code.AddRange(new byte[] { 0xC6, 0x05 });
            AddUInt32(code, checked(stateAddress + 0x18));
            code.Add(status);
        }

        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, stateAddress);
        code.Add(1); // cmp dword ptr [armed],1
        AddFinishJump(0x85); // jne finish
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, stateAddress); AddUInt32(code, 0);
        AddStatus(2); // finalizing

        // This pointer was captured from EDI at 0x00A59B32, where the original
        // instruction itself dereferences it. That is safer than walking a
        // partially initialized global object chain from injected code.
        code.AddRange(new byte[] { 0x8B, 0x15 }); AddUInt32(code, checked(stateAddress + 0x14));
        code.AddRange(new byte[] { 0x81, 0xFA }); AddUInt32(code, 0x00010000);
        AddStatus(3);
        AddFinishJump(0x82);
        code.AddRange(new byte[] { 0x83, 0xBA, 0xB4, 0x00, 0x00, 0x00, 0x00 });
        AddStatus(4);
        AddFinishJump(0x84); // layout pointer is null

        code.AddRange(new byte[] { 0x8B, 0x35 }); AddUInt32(code, RoomTablePointerGlobal); // esi=base
        code.AddRange(new byte[] { 0x81, 0xFE }); AddUInt32(code, 0x00010000);
        AddStatus(5);
        AddFinishJump(0x82);
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 4)); // eax=slot
        code.AddRange(new byte[] { 0x6B, 0xC0, RoomCellSize }); // eax=slot*0x28
        code.AddRange(new byte[] { 0x03, 0xC6 }); // eax=target cell
        code.AddRange(new byte[] { 0x3B, 0xC6 });
        AddStatus(6);
        AddFinishJump(0x82); // address wrapped below table base
        code.AddRange(new byte[] { 0x8D, 0x50, RoomCellSize }); // edx=target end
        code.AddRange(new byte[] { 0x3B, 0xD0 });
        AddFinishJump(0x82); // target end wrapped
        code.AddRange(new byte[] { 0x3B, 0x15 }); AddUInt32(code, RoomTableEndGlobal);
        AddFinishJump(0x87); // ja finish
        code.AddRange(new byte[] { 0x89, 0x35 }); AddUInt32(code, checked(stateAddress + 0x10));
        code.AddRange(new byte[] { 0x89, 0x35 }); AddUInt32(code, checked(stateAddress + 0x2C));
        code.AddRange(new byte[] { 0x8B, 0x0D }); AddUInt32(code, checked(stateAddress + 0x1C));
        code.AddRange(new byte[] { 0x39, 0x08 }); // cmp [logical target cell],expected map
        AddStatus(8);
        AddFinishJump(0x85); // loader did not construct the PVF start slot

        code.AddRange(new byte[] { 0x8B, 0x15 }); AddUInt32(code, checked(stateAddress + 0x14));
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 8));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC8 }); // ecx=low byte (x)
        code.AddRange(new byte[] { 0x89, 0x8A, 0xB8, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 }); // eax=y
        code.AddRange(new byte[] { 0x89, 0x82, 0xBC, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xC6, 0x05 }); AddUInt32(code, checked(stateAddress + 0x20));
        code.Add(1); // map logical PVF room lookups to the physical slot-zero scene
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 0x0C));
        AddStatus(7); // applied

        var finishOffset = code.Count;
        foreach (var displacement in finishJumps)
        {
            var sourceAfter = checked(stubAddress + (uint)displacement + sizeof(int));
            var destination = checked(stubAddress + (uint)finishOffset);
            var relative = BitConverter.GetBytes(unchecked((int)(destination - sourceAfter)));
            for (var index = 0; index < relative.Length; index++) code[displacement + index] = relative[index];
        }
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(ExpectedOfflineFinalizeBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineFinalizeResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineSceneConstructedStub(uint stubAddress, uint stateAddress,
        byte[] displacedInstruction, uint resumeAddress)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();

        int AddFinishJump(byte condition)
        {
            code.AddRange(new byte[] { 0x0F, condition });
            var displacement = code.Count;
            AddInt32(code, 0);
            finishJumps.Add(displacement);
            return displacement;
        }

        void AddStatus(uint value)
        {
            code.AddRange(new byte[] { 0xC7, 0x05 });
            AddUInt32(code, checked(stateAddress + OfflineRouteDoorStatusOffset));
            AddUInt32(code, value);
        }

        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D }); AddUInt32(code, checked(stateAddress + 0x5C));
        code.Add(1);
        var noRouteJump = AddFinishJump(0x85);

        // Bind this one-off route patch to the selected physical start map.
        // The generic offline patch remains enabled for every dungeon, but this
        // route topology must never be applied to a different selected map.
        // Both construction branches arrive with their newly built scene in
        // EAX. Preserve it before loading the selected map ID into EAX.
        code.AddRange(new byte[] { 0x89, 0xC7 }); // edi = scene (eax)
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 0x1C));
        code.AddRange(new byte[] { 0x3B, 0x05 }); AddUInt32(code,
            checked(stateAddress + OfflineRouteStartMapOffset));
        var mapMismatchJump = AddFinishJump(0x85);
        code.AddRange(new byte[] { 0x81, 0xFF }); AddUInt32(code, 0x00010000);
        var sceneMissingJump = AddFinishJump(0x82);
        code.AddRange(new byte[] { 0x0F, 0xB6, 0x87, 0x38, 0x02, 0x00, 0x00 });
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + OfflineRouteDoorMaskBeforeOffset));
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + OfflineRouteDoorMaskOffset));
        code.AddRange(new byte[] { 0x88, 0x87, 0x38, 0x02, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code,
            checked(stateAddress + OfflineRouteDoorWriteCountOffset));
        AddStatus(5);
        code.AddRange(new byte[] { 0xC7, 0x05 }); AddUInt32(code, checked(stateAddress + 0x5C));
        AddUInt32(code, 0); // patch only the selected start-room scene

        var finishOffset = code.Count;
        foreach (var displacement in finishJumps)
        {
            var sourceAfter = checked(stubAddress + (uint)displacement + sizeof(int));
            var destination = checked(stubAddress + (uint)finishOffset);
            var relative = BitConverter.GetBytes(unchecked((int)(destination - sourceAfter)));
            for (var index = 0; index < relative.Length; index++) code[displacement + index] = relative[index];
        }
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(displacedInstruction);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(resumeAddress - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineRoutePreconstructStub(uint stubAddress, uint stateAddress,
        byte[] displacedInstruction, uint resumeAddress)
    {
        var code = new List<byte>();

        // EAX is the value returned by 0x00489790.  pushad stores it at
        // [esp+1C].  Replacing that saved value lets popad restore the PVF
        // greed mask into EAX, so the original `push eax` passes it directly
        // into 0x0048E610 before that constructor builds any cached doors or
        // minimap objects.  Every other register and EFLAGS are restored.
        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x83, 0x3D });
        AddUInt32(code, checked(stateAddress + 0x5C));
        code.Add(1); // cmp dword ptr [preconstruct armed], 1
        code.AddRange(new byte[] { 0x0F, 0x85 }); // jne finish
        var finishJumpOffset = code.Count;
        AddInt32(code, 0);

        // EBX is the room-cell pointer at both construction branches.  Do not
        // consume the one-shot route patch for any unrelated scene created
        // before the selected dungeon's start map.
        code.AddRange(new byte[] { 0x8B, 0x03 }); // eax = room-cell map ID
        code.AddRange(new byte[] { 0x3B, 0x05 });
        AddUInt32(code, checked(stateAddress + OfflineRouteStartMapOffset));
        code.AddRange(new byte[] { 0x0F, 0x85 }); // jne finish
        var mapMismatchJumpOffset = code.Count;
        AddInt32(code, 0);

        code.AddRange(new byte[] { 0x8B, 0x44, 0x24, 0x1C }); // eax = saved native mask
        code.Add(0xA3); AddUInt32(code, checked(stateAddress + OfflineRoutePreconstructMaskBeforeOffset));
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + OfflineRouteDoorMaskOffset));
        code.AddRange(new byte[] { 0x89, 0x44, 0x24, 0x1C }); // saved EAX = PVF mask
        code.AddRange(new byte[] { 0xFF, 0x05 });
        AddUInt32(code, checked(stateAddress + OfflineRoutePreconstructCountOffset));
        code.AddRange(new byte[] { 0xC7, 0x05 });
        AddUInt32(code, checked(stateAddress + OfflineRouteDoorStatusOffset));
        AddUInt32(code, 6); // applied before native scene construction
        code.AddRange(new byte[] { 0xC7, 0x05 });
        AddUInt32(code, checked(stateAddress + 0x5C));
        AddUInt32(code, 0); // the post-construction writer must remain inert

        var finishOffset = code.Count;
        foreach (var displacement in new[] { finishJumpOffset, mapMismatchJumpOffset })
        {
            var sourceAfter = checked(stubAddress + (uint)displacement + sizeof(int));
            var relative = unchecked((int)(checked(stubAddress + (uint)finishOffset) - sourceAfter));
            var encodedRelative = BitConverter.GetBytes(relative);
            for (var index = 0; index < encodedRelative.Length; index++)
                code[displacement + index] = encodedRelative[index];
        }

        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(displacedInstruction);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(resumeAddress - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineRoomLookupRemapStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        var originalJumps = new List<int>();

        void AddOriginalJump(byte condition)
        {
            code.Add(condition);
            originalJumps.Add(code.Count);
            code.Add(0);
        }

        code.Add(0x9C); // pushfd
        code.Add(0xA1); AddUInt32(code, RoomTablePointerGlobal); // eax=physical table base
        code.AddRange(new byte[] { 0x80, 0x3D }); AddUInt32(code, checked(stateAddress + 0x20));
        code.Add(1);
        AddOriginalJump(0x75); // remap disabled
        code.AddRange(new byte[] { 0x3B, 0x05 }); AddUInt32(code, checked(stateAddress + 0x2C));
        AddOriginalJump(0x75); // room table was replaced
        code.AddRange(new byte[] { 0x3B, 0x15 }); AddUInt32(code, checked(stateAddress + 0x24));
        AddOriginalJump(0x75); // this is not the logical start slot
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 0x28));
        code.Add(0xEB);
        var remappedDoneJump = code.Count;
        code.Add(0);

        var originalOffset = code.Count;
        code.AddRange(new byte[] { 0x8D, 0x04, 0xD0 }); // eax=base+logical-index*8
        var doneOffset = code.Count;
        foreach (var displacement in originalJumps)
            code[displacement] = checked((byte)(originalOffset - (displacement + 1)));
        code[remappedDoneJump] = checked((byte)(doneOffset - (remappedDoneJump + 1)));
        code.Add(0x9D); // popfd
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(RoomLookupResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineCoordinateReapplyStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();

        void AddFinishJump(byte condition)
        {
            code.AddRange(new byte[] { 0x0F, condition });
            finishJumps.Add(code.Count);
            AddInt32(code, 0);
        }

        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x80, 0x3D }); AddUInt32(code, checked(stateAddress + 0x20));
        code.Add(1);
        AddFinishJump(0x85); // remap is not active
        code.AddRange(new byte[] { 0x8B, 0x15 }); AddUInt32(code, checked(stateAddress + 0x14));
        code.AddRange(new byte[] { 0x81, 0xFA }); AddUInt32(code, 0x00010000);
        AddFinishJump(0x82);
        code.AddRange(new byte[] { 0x83, 0xBA, 0xB4, 0x00, 0x00, 0x00, 0x00 });
        AddFinishJump(0x84);
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 8));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC8 }); // ecx=x
        code.AddRange(new byte[] { 0x89, 0x8A, 0xB8, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 }); // eax=y
        code.AddRange(new byte[] { 0x89, 0x82, 0xBC, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 0x30));

        var finishOffset = code.Count;
        foreach (var displacement in finishJumps)
        {
            var sourceAfter = checked(stubAddress + (uint)displacement + sizeof(int));
            var destination = checked(stubAddress + (uint)finishOffset);
            var relative = BitConverter.GetBytes(unchecked((int)(destination - sourceAfter)));
            for (var index = 0; index < relative.Length; index++) code[displacement + index] = relative[index];
        }
        code.Add(0x61); // popad
        code.Add(0x9D); // popfd
        code.AddRange(ExpectedOfflineCoordinateReapplyBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineCoordinateReapplyResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static byte[] BuildOfflineCoordinateUseStub(uint stubAddress, uint stateAddress)
    {
        var code = new List<byte>();
        var finishJumps = new List<int>();

        void AddFinishJump(byte condition)
        {
            code.AddRange(new byte[] { 0x0F, condition });
            finishJumps.Add(code.Count);
            AddInt32(code, 0);
        }

        code.Add(0x9C); // pushfd
        code.Add(0x60); // pushad
        code.AddRange(new byte[] { 0x80, 0x3D }); AddUInt32(code, checked(stateAddress + 0x20));
        code.Add(1);
        AddFinishJump(0x85);
        code.AddRange(new byte[] { 0x3B, 0x35 }); AddUInt32(code, checked(stateAddress + 0x14));
        AddFinishJump(0x85); // ESI is not the active dungeon-state object
        code.Add(0xA1); AddUInt32(code, checked(stateAddress + 8));
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC8 });
        code.AddRange(new byte[] { 0x89, 0x8E, 0xB8, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xC1, 0xE8, 0x08, 0x0F, 0xB6, 0xC0 });
        code.AddRange(new byte[] { 0x89, 0x86, 0xBC, 0x00, 0x00, 0x00 });
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, checked(stateAddress + 0x34));

        var finishOffset = code.Count;
        foreach (var displacement in finishJumps)
        {
            var sourceAfter = checked(stubAddress + (uint)displacement + sizeof(int));
            var destination = checked(stubAddress + (uint)finishOffset);
            var relative = BitConverter.GetBytes(unchecked((int)(destination - sourceAfter)));
            for (var index = 0; index < relative.Length; index++) code[displacement + index] = relative[index];
        }
        code.Add(0x61);
        code.Add(0x9D);
        code.AddRange(ExpectedOfflineCoordinateUseBytes);
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(OfflineCoordinateUseResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static bool InstallDebugMapCycle(IntPtr process, uint sequence, out IntPtr traceAddress)
    {
        traceAddress = IntPtr.Zero;
        if (!PatchCode(process, DebugConfigGate, ExpectedDebugConfigGateBytes, new byte[] { 0x90, 0x90 },
                "DebugConfig gate"))
        {
            return false;
        }

        var original = new byte[ExpectedMapSelectionBytes.Length];
        if (!ReadProcessMemory(process, new IntPtr(MapSelectionHook), original, original.Length, out _) ||
            !original.SequenceEqual(ExpectedMapSelectionBytes))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x00409435. This launcher only supports the supplied ARAD.exe build.");
            return false;
        }

        var allocation = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)0x200,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (allocation == IntPtr.Zero || allocation.ToInt64() < 0 || allocation.ToInt64() > uint.MaxValue)
        {
            Console.Error.WriteLine("VirtualAllocEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var stubAddress = checked((uint)allocation.ToInt64());
        var stateAddress = checked(stubAddress + 0x100);
        var selectedKeyAddress = checked(stateAddress + 4);
        var selectedMapAddress = checked(stateAddress + 8);
        var entryCountAddress = checked(stateAddress + 12);
        var stub = BuildMapSelectionStub(stubAddress, stateAddress, selectedKeyAddress,
            selectedMapAddress, entryCountAddress);
        var payload = new byte[0x110];
        Buffer.BlockCopy(stub, 0, payload, 0, stub.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, payload, 0x100, sizeof(uint));
        if (!WriteProcessMemory(process, allocation, payload, payload.Length, out _) ||
            !FlushInstructionCache(process, allocation, (UIntPtr)payload.Length))
        {
            Console.Error.WriteLine("Writing the debug-map stub failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        var entry = new byte[ExpectedMapSelectionBytes.Length];
        entry[0] = 0xE9; // jmp <stub>
        WriteRelative(entry, 1, checked(MapSelectionHook + 5), stubAddress);
        for (var index = 5; index < entry.Length; index++) entry[index] = 0x90;
        if (!WriteProcessMemory(process, new IntPtr(MapSelectionHook), entry, entry.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(MapSelectionHook), (UIntPtr)entry.Length))
        {
            Console.Error.WriteLine("Patching the DebugConfig map selector failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        traceAddress = new IntPtr(stateAddress);
        return true;
    }

    private static bool PatchCode(IntPtr process, uint address, byte[] expected, byte[] replacement, string name)
    {
        var actual = new byte[expected.Length];
        if (!ReadProcessMemory(process, new IntPtr(address), actual, actual.Length, out _) ||
            !actual.SequenceEqual(expected))
        {
            Console.Error.WriteLine("Unexpected bytes at 0x" + address.ToString("X8") + " for " + name + ".");
            return false;
        }
        if (!WriteProcessMemory(process, new IntPtr(address), replacement, replacement.Length, out _) ||
            !FlushInstructionCache(process, new IntPtr(address), (UIntPtr)replacement.Length))
        {
            Console.Error.WriteLine("Patching " + name + " failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    private static byte[] BuildMapSelectionStub(uint stubAddress, uint stateAddress, uint selectedKeyAddress,
        uint selectedMapAddress, uint entryCountAddress)
    {
        var code = new List<byte>();
        code.AddRange(new byte[] { 0x50, 0x51, 0x52, 0x56, 0x57 }); // push eax/ecx/edx/esi/edi
        code.AddRange(new byte[] { 0x8B, 0xB3, 0x0C, 0x02, 0x00, 0x00 }); // esi=[ebx+20Ch]
        code.AddRange(new byte[] { 0x8B, 0xBB, 0x10, 0x02, 0x00, 0x00 }); // edi=[ebx+210h]
        code.AddRange(new byte[] { 0x2B, 0xFE, 0xC1, 0xEF, 0x03, 0x85, 0xFF }); // count=(end-start)/8
        code.Add(0x74); // jz restore registers when the table is empty
        var emptyJumpOffset = code.Count;
        code.Add(0);

        code.Add(0xA1); AddUInt32(code, stateAddress); // eax=[sequence]
        code.AddRange(new byte[] { 0xFF, 0x05 }); AddUInt32(code, stateAddress); // ++sequence
        code.AddRange(new byte[] { 0x33, 0xD2, 0xF7, 0xF7 }); // edx=0; edx:eax / edi
        code.AddRange(new byte[] { 0x8B, 0x04, 0xD6 }); // eax=[esi+edx*8] (valid map key)
        code.Add(0xA3); AddUInt32(code, selectedKeyAddress);
        code.AddRange(new byte[] { 0x89, 0x45, 0xD8 }); // [ebp-28h]=selected map key
        code.AddRange(new byte[] { 0x8B, 0x44, 0xD6, 0x04 }); // eax=[esi+edx*8+4] (actual mapId)
        code.Add(0xA3); AddUInt32(code, selectedMapAddress);
        code.AddRange(new byte[] { 0x89, 0x3D }); AddUInt32(code, entryCountAddress);

        var restoreOffset = code.Count;
        code[emptyJumpOffset] = checked((byte)(restoreOffset - (emptyJumpOffset + 1)));
        code.AddRange(new byte[] { 0x5F, 0x5E, 0x5A, 0x59, 0x58 }); // restore registers
        code.AddRange(ExpectedMapSelectionBytes); // displaced instructions
        code.Add(0xE9);
        var jumpNextInstruction = checked(stubAddress + (uint)code.Count + 4);
        AddInt32(code, unchecked((int)(MapSelectionResume - jumpNextInstruction)));
        return code.ToArray();
    }

    private static SelectionTrace? WaitForSelection(IntPtr process, IntPtr traceAddress, TimeSpan timeout)
    {
        var buffer = new byte[16];
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && WaitForSingleObject(process, 0) != WaitObject0)
        {
            if (ReadProcessMemory(process, traceAddress, buffer, buffer.Length, out _))
            {
                var mapKey = BitConverter.ToUInt32(buffer, 4);
                if (mapKey != 0)
                {
                    return new SelectionTrace(
                        BitConverter.ToUInt32(buffer, 0),
                        mapKey,
                        BitConverter.ToUInt32(buffer, 8),
                        BitConverter.ToUInt32(buffer, 12));
                }
            }
            Thread.Sleep(50);
        }
        return null;
    }

    private static int VerifyStub()
    {
        const uint sampleStubAddress = 0x00500000;
        var stub = BuildMapSelectionStub(sampleStubAddress, 0x00500100, 0x00500104, 0x00500108, 0x0050010C);
        var preservedInstructions = ExpectedMapSelectionBytes.Concat(new byte[] { 0xE9 }).ToArray();
        var preservedOffset = stub.Length - (preservedInstructions.Length + sizeof(int));
        if (preservedOffset < 0 || !stub.Skip(preservedOffset).Take(preservedInstructions.Length)
                .SequenceEqual(preservedInstructions))
        {
            Console.Error.WriteLine("Stub verification failed: displaced instructions are missing.");
            return 1;
        }

        var relative = BitConverter.ToInt32(stub, stub.Length - sizeof(int));
        var destination = unchecked((uint)(sampleStubAddress + (uint)stub.Length + relative));
        if (destination != MapSelectionResume)
        {
            Console.Error.WriteLine("Stub verification failed: resume address is incorrect.");
            return 1;
        }

        var loaderStub = BuildMapLoaderTraceStub(sampleStubAddress, 0x00500100);
        var loaderPreserved = ExpectedMapLoaderBytes.Concat(new byte[] { 0xE9 }).ToArray();
        var loaderPreservedOffset = loaderStub.Length - (loaderPreserved.Length + sizeof(int));
        if (loaderPreservedOffset < 0 || !loaderStub.Skip(loaderPreservedOffset).Take(loaderPreserved.Length)
                .SequenceEqual(loaderPreserved))
        {
            Console.Error.WriteLine("Stub verification failed: map-loader prologue is missing.");
            return 1;
        }
        var loaderRelative = BitConverter.ToInt32(loaderStub, loaderStub.Length - sizeof(int));
        var loaderDestination = unchecked((uint)(sampleStubAddress + (uint)loaderStub.Length + loaderRelative));
        if (loaderDestination != MapLoaderResume)
        {
            Console.Error.WriteLine("Stub verification failed: map-loader resume address is incorrect.");
            return 1;
        }

        var offlineStub = BuildOfflineDungeonFixStub(sampleStubAddress, 0x00500240, 0x00500200);
        var mazeCaptureStubAddress = sampleStubAddress + OfflineMazeCaptureStubOffset;
        var mazeCaptureStub = BuildOfflineMazeCaptureStub(mazeCaptureStubAddress, 0x00500240, 0x00500200);
        var stateCaptureStubAddress = sampleStubAddress + OfflineStateCaptureStubOffset;
        var stateCaptureStub = BuildOfflineDungeonStateCaptureStub(stateCaptureStubAddress, 0x00500200);
        var finalizeStubAddress = sampleStubAddress + OfflineFinalizeStubOffset;
        var finalizeStub = BuildOfflineDungeonFinalizeStub(finalizeStubAddress, 0x00500200);
        var roomLookupStubAddress = sampleStubAddress + OfflineRoomLookupStubOffset;
        var roomLookupStub = BuildOfflineRoomLookupRemapStub(roomLookupStubAddress, 0x00500200);
        var coordinateReapplyStubAddress = sampleStubAddress + OfflineCoordinateReapplyStubOffset;
        var coordinateReapplyStub = BuildOfflineCoordinateReapplyStub(coordinateReapplyStubAddress, 0x00500200);
        var coordinateUseStubAddress = sampleStubAddress + OfflineCoordinateUseStubOffset;
        var coordinateUseStub = BuildOfflineCoordinateUseStub(coordinateUseStubAddress, 0x00500200);
        var dungeonStateStubAddress = sampleStubAddress + OfflineDungeonStateStubOffset;
        var dungeonStateStub = BuildOfflineDungeonStateStub(dungeonStateStubAddress, 0x00500200);
        var routePreconstructStubAddress = sampleStubAddress + OfflineRoutePreconstructStubOffset;
        var routePreconstructStub = BuildOfflineRoutePreconstructStub(routePreconstructStubAddress,
            0x00500200, ExpectedOfflineRoutePreconstructBytes, OfflineRoutePreconstructResume);
        var routePreconstructAlternateStubAddress = sampleStubAddress +
            OfflineRoutePreconstructAlternateStubOffset;
        var routePreconstructAlternateStub = BuildOfflineRoutePreconstructStub(
            routePreconstructAlternateStubAddress, 0x00500200,
            ExpectedOfflineRoutePreconstructAlternateBytes, OfflineRoutePreconstructAlternateResume);
        var sceneConstructedStubAddress = sampleStubAddress + OfflineSceneConstructedStubOffset;
        var sceneConstructedStub = BuildOfflineSceneConstructedStub(sceneConstructedStubAddress, 0x00500200,
            ExpectedOfflineSceneConstructedBytes, OfflineSceneConstructedResume);
        var sceneConstructedAlternateStubAddress = sampleStubAddress + OfflineSceneConstructedAlternateStubOffset;
        var sceneConstructedAlternateStub = BuildOfflineSceneConstructedStub(sceneConstructedAlternateStubAddress,
            0x00500200, ExpectedOfflineSceneConstructedAlternateBytes, OfflineSceneConstructedAlternateResume);
        var bossRestoreStubAddress = sampleStubAddress + OfflineBossRestoreStubOffset;
        var bossRestoreStub = BuildOfflineBossRestoreStub(bossRestoreStubAddress, 0x00500200);
        if (offlineStub.Length > OfflineMazeCaptureStubOffset ||
            mazeCaptureStub.Length > OfflineStateCaptureStubOffset - OfflineMazeCaptureStubOffset ||
            stateCaptureStub.Length > OfflineFinalizeStubOffset - OfflineStateCaptureStubOffset ||
            finalizeStub.Length > OfflineRoomLookupStubOffset - OfflineFinalizeStubOffset ||
            roomLookupStub.Length > OfflineCoordinateReapplyStubOffset - OfflineRoomLookupStubOffset ||
            coordinateReapplyStub.Length > OfflineCoordinateUseStubOffset - OfflineCoordinateReapplyStubOffset ||
            coordinateUseStub.Length > OfflineDungeonStateStubOffset - OfflineCoordinateUseStubOffset ||
            dungeonStateStub.Length > OfflineRoutePreconstructStubOffset - OfflineDungeonStateStubOffset ||
            routePreconstructStub.Length > OfflineRoutePreconstructAlternateStubOffset -
                OfflineRoutePreconstructStubOffset ||
            routePreconstructAlternateStub.Length > OfflineSceneConstructedStubOffset -
                OfflineRoutePreconstructAlternateStubOffset ||
            sceneConstructedStub.Length > OfflineSceneConstructedAlternateStubOffset -
                OfflineSceneConstructedStubOffset ||
            sceneConstructedAlternateStub.Length > OfflineBossRestoreStubOffset -
                OfflineSceneConstructedAlternateStubOffset ||
            bossRestoreStub.Length > OfflineDungeonStateOffset - OfflineBossRestoreStubOffset)
        {
            Console.Error.WriteLine("Stub verification failed: offline dungeon stubs overlap reserved data.");
            return 1;
        }
        if (!VerifyGeneratedCode("offline fallback", offlineStub, sampleStubAddress,
                OfflineFallbackResume) ||
            !VerifyGeneratedCode("offline dungeon route capture", mazeCaptureStub, mazeCaptureStubAddress,
                OfflineMazeCaptureResume) ||
            !VerifyGeneratedCode("dungeon-state capture", stateCaptureStub, stateCaptureStubAddress,
                OfflineStateCaptureResume) ||
            !VerifyGeneratedCode("room finalizer", finalizeStub, finalizeStubAddress,
                OfflineFinalizeResume) ||
            !VerifyGeneratedCode("logical-room lookup", roomLookupStub, roomLookupStubAddress,
                RoomLookupResume) ||
            !VerifyGeneratedCode("post-commit coordinates", coordinateReapplyStub,
                coordinateReapplyStubAddress, OfflineCoordinateReapplyResume) ||
            !VerifyGeneratedCode("coordinate-use guard", coordinateUseStub,
                coordinateUseStubAddress, OfflineCoordinateUseResume) ||
            !VerifyGeneratedCode("offline dungeon route state", dungeonStateStub,
                dungeonStateStubAddress, OfflineDungeonStateResume) ||
            !VerifyGeneratedCode("pre-construction PVF topology", routePreconstructStub,
                routePreconstructStubAddress, OfflineRoutePreconstructResume) ||
            !VerifyGeneratedCode("alternate pre-construction PVF topology", routePreconstructAlternateStub,
                routePreconstructAlternateStubAddress, OfflineRoutePreconstructAlternateResume) ||
            !VerifyGeneratedCode("post-construction PVF topology", sceneConstructedStub,
                sceneConstructedStubAddress, OfflineSceneConstructedResume) ||
            !VerifyGeneratedCode("alternate post-construction PVF topology", sceneConstructedAlternateStub,
                sceneConstructedAlternateStubAddress, OfflineSceneConstructedAlternateResume) ||
            !VerifyGeneratedCode("post-construction boss restore", bossRestoreStub, bossRestoreStubAddress,
                OfflineBossRestoreResume))
            return 1;
        var offlineRelative = BitConverter.ToInt32(offlineStub, offlineStub.Length - sizeof(int));
        var offlineDestination = unchecked((uint)(sampleStubAddress + (uint)offlineStub.Length + offlineRelative));
        if (offlineDestination != OfflineFallbackResume)
        {
            Console.Error.WriteLine("Stub verification failed: offline fallback resume address is incorrect.");
            return 1;
        }
        if (offlineStub.AsSpan().IndexOf(new byte[] { 0x83, 0xC7, OfflineDungeonMapRecordSize }) < 0 ||
            offlineStub.AsSpan().IndexOf(new byte[] { 0x0F, 0xB6, 0x47, 0x09,
                0x0F, 0xAF, 0x47, 0x0C, 0x0F, 0xB6, 0x4F, 0x08, 0x03, 0xC1 }) < 0 ||
            offlineStub.AsSpan().IndexOf(new byte[] { 0x83, 0xE1, 0xFF }) >= 0 ||
            offlineStub.AsSpan().IndexOf(new byte[] { 0x83, 0xE0, 0xFF }) >= 0)
        {
            Console.Error.WriteLine("Stub verification failed: safe PVF room-slot decoding is missing.");
            return 1;
        }
        var mazeCaptureRelative = BitConverter.ToInt32(mazeCaptureStub, mazeCaptureStub.Length - sizeof(int));
        var mazeCaptureDestination = unchecked((uint)(mazeCaptureStubAddress +
            (uint)mazeCaptureStub.Length + mazeCaptureRelative));
        if (mazeCaptureDestination != OfflineMazeCaptureResume ||
            !mazeCaptureStub.Skip(mazeCaptureStub.Length - (ExpectedOfflineMazeCaptureBytes.Length + 5))
                .Take(ExpectedOfflineMazeCaptureBytes.Length).SequenceEqual(ExpectedOfflineMazeCaptureBytes) ||
            mazeCaptureStub.AsSpan().IndexOf(new byte[] { 0x6A, 0x00, 0x8B, 0xCE, 0x89, 0x75, 0xE0 }) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: offline route capture does not preserve the maze-zero flow.");
            return 1;
        }
        var stateCaptureRelative = BitConverter.ToInt32(stateCaptureStub, stateCaptureStub.Length - sizeof(int));
        var stateCaptureDestination = unchecked((uint)(stateCaptureStubAddress +
            (uint)stateCaptureStub.Length + stateCaptureRelative));
        if (stateCaptureDestination != OfflineStateCaptureResume ||
            !stateCaptureStub.Take(ExpectedOfflineStateCaptureBytes.Length)
                .SequenceEqual(ExpectedOfflineStateCaptureBytes))
        {
            Console.Error.WriteLine("Stub verification failed: dungeon-state capture is incomplete.");
            return 1;
        }
        var finalizeRelative = BitConverter.ToInt32(finalizeStub, finalizeStub.Length - sizeof(int));
        var finalizeDestination = unchecked((uint)(finalizeStubAddress + (uint)finalizeStub.Length +
            finalizeRelative));
        if (finalizeDestination != OfflineFinalizeResume ||
            offlineStub.AsSpan().IndexOf(new byte[] { 0x8D, 0x0C, 0x80 }) < 0 ||
            finalizeStub.AsSpan().IndexOf(new byte[] { 0x8B, 0x0D }) < 0 ||
            finalizeStub.AsSpan().IndexOf(new byte[] { 0x8B, 0x06, 0x8B, 0x17,
                0x89, 0x16, 0x89, 0x07 }) >= 0 ||
            finalizeStub.AsSpan().IndexOf(new byte[] { 0x0F, 0xB6, 0xC8 }) < 0 ||
            finalizeStub[0] != 0x9C || finalizeStub[1] != 0x60 ||
            finalizeStub.AsSpan().IndexOf(new byte[] { 0x61, 0x9D }.Concat(ExpectedOfflineFinalizeBytes)
                .ToArray()) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: guarded target-room construction is incomplete.");
            return 1;
        }
        var roomLookupRelative = BitConverter.ToInt32(roomLookupStub, roomLookupStub.Length - sizeof(int));
        var roomLookupDestination = unchecked((uint)(roomLookupStubAddress +
            (uint)roomLookupStub.Length + roomLookupRelative));
        if (roomLookupDestination != RoomLookupResume ||
            roomLookupStub.AsSpan().IndexOf(new byte[] { 0x8D, 0x04, 0xD0 }) < 0 ||
            roomLookupStub.AsSpan().IndexOf(new byte[] { 0xFF, 0x05 }) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: logical-room lookup remap is incomplete.");
            return 1;
        }
        var coordinateReapplyRelative = BitConverter.ToInt32(coordinateReapplyStub,
            coordinateReapplyStub.Length - sizeof(int));
        var coordinateReapplyDestination = unchecked((uint)(coordinateReapplyStubAddress +
            (uint)coordinateReapplyStub.Length + coordinateReapplyRelative));
        if (coordinateReapplyDestination != OfflineCoordinateReapplyResume ||
            coordinateReapplyStub.AsSpan().IndexOf(new byte[] { 0x0F, 0xB6, 0xC8 }) < 0 ||
            coordinateReapplyStub.AsSpan().IndexOf(new byte[] { 0x61, 0x9D }
                .Concat(ExpectedOfflineCoordinateReapplyBytes).ToArray()) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: post-commit coordinate reapply is incomplete.");
            return 1;
        }
        var coordinateUseRelative = BitConverter.ToInt32(coordinateUseStub,
            coordinateUseStub.Length - sizeof(int));
        var coordinateUseDestination = unchecked((uint)(coordinateUseStubAddress +
            (uint)coordinateUseStub.Length + coordinateUseRelative));
        if (coordinateUseDestination != OfflineCoordinateUseResume ||
            coordinateUseStub.AsSpan().IndexOf(new byte[] { 0x3B, 0x35 }) < 0 ||
            coordinateUseStub.AsSpan().IndexOf(new byte[] { 0x61, 0x9D }
                .Concat(ExpectedOfflineCoordinateUseBytes).ToArray()) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: coordinate-use guard is incomplete.");
            return 1;
        }
        var dungeonStateRelative = BitConverter.ToInt32(dungeonStateStub,
            dungeonStateStub.Length - sizeof(int));
        var dungeonStateDestination = unchecked((uint)(dungeonStateStubAddress +
            (uint)dungeonStateStub.Length + dungeonStateRelative));
        if (dungeonStateDestination != OfflineDungeonStateResume ||
            dungeonStateStub.AsSpan().IndexOf(BitConverter.GetBytes(DungeonStateSetter)) >= 0 ||
            dungeonStateStub.AsSpan().IndexOf(new byte[] { 0xB9, 0xE0, 0x03, 0xED, 0x00, 0xE8 }) < 0 ||
            dungeonStateStub.AsSpan().IndexOf(new byte[] { 0xB9, 0xE8, 0x03, 0xED, 0x00, 0xE8 }) < 0 ||
            dungeonStateStub.AsSpan().IndexOf(new byte[] { 0xB9, 0xF0, 0x03, 0xED, 0x00 }) >= 0 ||
            dungeonStateStub.AsSpan().IndexOf(new byte[] { 0xB9, 0xF8, 0x03, 0xED, 0x00 }) >= 0)
        {
            Console.Error.WriteLine("Stub verification failed: offline boss-route state is incomplete or writes packet flags.");
            return 1;
        }
        if (dungeonStateStub.AsSpan().IndexOf(new byte[] { 0xB8, 0xFF, 0x00, 0x00, 0x00 }) < 0 ||
            dungeonStateStub.AsSpan().IndexOf(BitConverter.GetBytes(checked(0x00500200u +
                (uint)OfflineBossRestoreArmedOffset))) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: out-of-grid boss sentinel is missing.");
            return 1;
        }
        var routePreconstructRelative = BitConverter.ToInt32(routePreconstructStub,
            routePreconstructStub.Length - sizeof(int));
        var routePreconstructDestination = unchecked((uint)(routePreconstructStubAddress +
            (uint)routePreconstructStub.Length + routePreconstructRelative));
        if (routePreconstructDestination != OfflineRoutePreconstructResume ||
            !routePreconstructStub.Skip(routePreconstructStub.Length -
                    (ExpectedOfflineRoutePreconstructBytes.Length + 5))
                .Take(ExpectedOfflineRoutePreconstructBytes.Length)
                .SequenceEqual(ExpectedOfflineRoutePreconstructBytes) ||
            routePreconstructStub.AsSpan().IndexOf(new byte[] { 0x89, 0x44, 0x24, 0x1C }) < 0 ||
            routePreconstructStub.AsSpan().IndexOf(BitConverter.GetBytes(checked(0x00500200u +
                (uint)OfflineRouteDoorMaskOffset))) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: pre-construction PVF topology replacement is incomplete.");
            return 1;
        }
        var routePreconstructAlternateRelative = BitConverter.ToInt32(routePreconstructAlternateStub,
            routePreconstructAlternateStub.Length - sizeof(int));
        var routePreconstructAlternateDestination = unchecked((uint)(routePreconstructAlternateStubAddress +
            (uint)routePreconstructAlternateStub.Length + routePreconstructAlternateRelative));
        if (routePreconstructAlternateDestination != OfflineRoutePreconstructAlternateResume ||
            !routePreconstructAlternateStub.Skip(routePreconstructAlternateStub.Length -
                    (ExpectedOfflineRoutePreconstructAlternateBytes.Length + 5))
                .Take(ExpectedOfflineRoutePreconstructAlternateBytes.Length)
                .SequenceEqual(ExpectedOfflineRoutePreconstructAlternateBytes) ||
            routePreconstructAlternateStub.AsSpan().IndexOf(new byte[] { 0x89, 0x44, 0x24, 0x1C }) < 0 ||
            routePreconstructAlternateStub.AsSpan().IndexOf(BitConverter.GetBytes(checked(0x00500200u +
                (uint)OfflineRouteDoorMaskOffset))) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: alternate pre-construction PVF topology replacement is incomplete.");
            return 1;
        }
        var sceneConstructedRelative = BitConverter.ToInt32(sceneConstructedStub,
            sceneConstructedStub.Length - sizeof(int));
        var sceneConstructedDestination = unchecked((uint)(sceneConstructedStubAddress +
            (uint)sceneConstructedStub.Length + sceneConstructedRelative));
        if (sceneConstructedDestination != OfflineSceneConstructedResume ||
            !sceneConstructedStub.Skip(sceneConstructedStub.Length -
                    (ExpectedOfflineSceneConstructedBytes.Length + 5))
                .Take(ExpectedOfflineSceneConstructedBytes.Length).SequenceEqual(ExpectedOfflineSceneConstructedBytes) ||
            sceneConstructedStub.AsSpan().IndexOf(new byte[] { 0x0F, 0xB6, 0x87, 0x38, 0x02 }) < 0 ||
            sceneConstructedStub.AsSpan().IndexOf(new byte[] { 0x88, 0x87, 0x38, 0x02 }) < 0 ||
            sceneConstructedStub.AsSpan().IndexOf(new byte[] { 0x89, 0xC7 }) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: post-construction PVF topology write is incomplete.");
            return 1;
        }
        var sceneConstructedAlternateRelative = BitConverter.ToInt32(sceneConstructedAlternateStub,
            sceneConstructedAlternateStub.Length - sizeof(int));
        var sceneConstructedAlternateDestination = unchecked((uint)(sceneConstructedAlternateStubAddress +
            (uint)sceneConstructedAlternateStub.Length + sceneConstructedAlternateRelative));
        if (sceneConstructedAlternateDestination != OfflineSceneConstructedAlternateResume ||
            !sceneConstructedAlternateStub.Skip(sceneConstructedAlternateStub.Length -
                    (ExpectedOfflineSceneConstructedAlternateBytes.Length + 5))
                .Take(ExpectedOfflineSceneConstructedAlternateBytes.Length)
                .SequenceEqual(ExpectedOfflineSceneConstructedAlternateBytes) ||
            sceneConstructedAlternateStub.AsSpan().IndexOf(new byte[] { 0x0F, 0xB6, 0x87, 0x38, 0x02 }) < 0 ||
            sceneConstructedAlternateStub.AsSpan().IndexOf(new byte[] { 0x88, 0x87, 0x38, 0x02 }) < 0 ||
            sceneConstructedAlternateStub.AsSpan().IndexOf(new byte[] { 0x89, 0xC7 }) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: alternate post-construction PVF topology " +
                "write is incomplete.");
            return 1;
        }
        var bossRestoreRelative = BitConverter.ToInt32(bossRestoreStub,
            bossRestoreStub.Length - sizeof(int));
        var bossRestoreDestination = unchecked((uint)(bossRestoreStubAddress +
            (uint)bossRestoreStub.Length + bossRestoreRelative));
        if (bossRestoreDestination != OfflineBossRestoreResume ||
            !bossRestoreStub.Skip(bossRestoreStub.Length - (ExpectedOfflineBossRestoreBytes.Length + 5))
                .Take(ExpectedOfflineBossRestoreBytes.Length).SequenceEqual(ExpectedOfflineBossRestoreBytes) ||
            bossRestoreStub.AsSpan().IndexOf(BitConverter.GetBytes(checked(0x00500200u + 0x38))) < 0 ||
            bossRestoreStub.AsSpan().IndexOf(BitConverter.GetBytes(checked(0x00500200u +
                (uint)OfflineBossRestoreArmedOffset))) < 0 ||
            bossRestoreStub.AsSpan().IndexOf(BitConverter.GetBytes(EncryptedBossXWrapper)) < 0 ||
            bossRestoreStub.AsSpan().IndexOf(BitConverter.GetBytes(EncryptedBossYWrapper)) < 0)
        {
            Console.Error.WriteLine("Stub verification failed: post-construction PVF boss restore is incomplete.");
            return 1;
        }
        Console.WriteLine("Stub verification passed. selection-bytes=" + stub.Length +
            " map-loader-bytes=" + loaderStub.Length + " offline-fallback-bytes=" + offlineStub.Length +
            " offline-route-capture-bytes=" + mazeCaptureStub.Length +
            " offline-state-capture-bytes=" + stateCaptureStub.Length +
            " offline-finalize-bytes=" + finalizeStub.Length +
            " offline-room-lookup-bytes=" + roomLookupStub.Length +
            " offline-coordinate-reapply-bytes=" + coordinateReapplyStub.Length +
            " offline-coordinate-use-bytes=" + coordinateUseStub.Length +
            " offline-route-state-bytes=" + dungeonStateStub.Length +
            " offline-route-preconstruct-bytes=" + routePreconstructStub.Length + "/" +
            routePreconstructAlternateStub.Length +
            " offline-scene-constructed-bytes=" + sceneConstructedStub.Length + "/" +
            sceneConstructedAlternateStub.Length +
            " offline-boss-restore-bytes=" + bossRestoreStub.Length +
            " resume=0x" + destination.ToString("X8"));
        return 0;
    }

    private static bool VerifyGeneratedCode(string name, byte[] code, uint address,
        params uint[] permittedExternalBranches)
    {
        var decoder = Iced.Intel.Decoder.Create(32, code);
        decoder.IP = address;
        var end = checked(address + (uint)code.Length);
        var instructionAddresses = new HashSet<ulong>();
        var branchTargets = new List<ulong>();
        while (decoder.IP < end)
        {
            instructionAddresses.Add(decoder.IP);
            decoder.Decode(out var instruction);
            if (instruction.Code == Code.INVALID || instruction.Length == 0 || decoder.IP > end)
            {
                Console.Error.WriteLine("Stub verification failed: " + name +
                    " contains an invalid or truncated instruction at 0x" + instruction.IP.ToString("X8") + ".");
                return false;
            }
            if (instruction.FlowControl == FlowControl.ConditionalBranch ||
                instruction.FlowControl == FlowControl.UnconditionalBranch)
                branchTargets.Add(instruction.NearBranchTarget);
        }

        foreach (var target in branchTargets)
        {
            var isInternal = target >= address && target < end;
            if ((isInternal && !instructionAddresses.Contains(target)) ||
                (!isInternal && !permittedExternalBranches.Contains((uint)target)))
            {
                Console.Error.WriteLine("Stub verification failed: " + name +
                    " branches to an invalid target 0x" + target.ToString("X8") + ".");
                return false;
            }
        }
        return decoder.IP == end;
    }

    private static int Disassemble(string client, uint virtualAddress, int count)
    {
        if (count <= 0 || count > 2000)
        {
            Console.Error.WriteLine("Instruction count must be between 1 and 2000.");
            return 2;
        }

        var image = File.ReadAllBytes(client);
        if (!TryMapVirtualAddressToFileOffset(image, virtualAddress, out var fileOffset))
        {
            Console.Error.WriteLine("Virtual address is not backed by the executable image: 0x" + virtualAddress.ToString("X8"));
            return 2;
        }

        var decoder = Iced.Intel.Decoder.Create(32, new ByteArrayCodeReader(image, fileOffset, image.Length - fileOffset),
            DecoderOptions.None);
        decoder.IP = virtualAddress;
        var formatter = new IntelFormatter();
        var output = new TextFormatterOutput();
        for (var index = 0; index < count; index++)
        {
            var instruction = decoder.Decode();
            if (instruction.Code == Code.INVALID)
            {
                Console.WriteLine(instruction.IP.ToString("X8") + "  <invalid>");
                return 1;
            }
            output.Clear();
            formatter.Format(in instruction, output);
            Console.WriteLine(instruction.IP.ToString("X8") + "  " + output.Text);
        }
        return 0;
    }

    private static int ScanRoomCoordinateAccesses(string client)
    {
        var image = File.ReadAllBytes(client);
        if (image.Length < 0x40) return 2;
        var peOffset = BitConverter.ToInt32(image, 0x3C);
        if (peOffset < 0 || peOffset + 24 > image.Length ||
            BitConverter.ToUInt32(image, peOffset) != 0x00004550)
            return 2;

        var sectionCount = BitConverter.ToUInt16(image, peOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(image, peOffset + 20);
        var optionalHeader = peOffset + 24;
        if (optionalHeader + optionalHeaderSize > image.Length) return 2;
        var imageBase = BitConverter.ToUInt32(image, optionalHeader + 28);
        var sectionTable = optionalHeader + optionalHeaderSize;
        var formatter = new IntelFormatter();
        var output = new TextFormatterOutput();
        var matches = 0;

        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var header = sectionTable + sectionIndex * 40;
            if (header + 40 > image.Length) return 2;
            var virtualAddress = BitConverter.ToUInt32(image, header + 12);
            var rawSize = BitConverter.ToUInt32(image, header + 16);
            var rawOffset = BitConverter.ToUInt32(image, header + 20);
            var characteristics = BitConverter.ToUInt32(image, header + 36);
            if ((characteristics & 0x20000000) == 0 || rawSize == 0 ||
                rawOffset > int.MaxValue || rawSize > int.MaxValue || rawOffset + rawSize > image.Length)
                continue;

            var decoder = Iced.Intel.Decoder.Create(32,
                new ByteArrayCodeReader(image, (int)rawOffset, (int)rawSize), DecoderOptions.None);
            decoder.IP = imageBase + virtualAddress;
            var sectionEnd = decoder.IP + rawSize;
            while (decoder.IP < sectionEnd)
            {
                var instruction = decoder.Decode();
                if (instruction.Code == Code.INVALID || instruction.Mnemonic != Mnemonic.Mov ||
                    instruction.Op0Kind != OpKind.Memory || instruction.MemoryBase == Register.None ||
                    (instruction.MemoryDisplacement64 != 0xB8 && instruction.MemoryDisplacement64 != 0xBC))
                    continue;
                output.Clear();
                formatter.Format(instruction, output);
                Console.WriteLine(instruction.IP.ToString("X8") + "  " + output.Text);
                matches++;
            }
        }

        Console.WriteLine("coordinate-offset accesses=" + matches);
        return 0;
    }

    private static int FindDirectCallers(string client, uint target)
    {
        return ScanExecutableInstructions(client, "callers", instruction =>
            instruction.Mnemonic == Mnemonic.Call &&
            instruction.Op0Kind == OpKind.NearBranch32 &&
            instruction.NearBranchTarget == target);
    }

    private static int FindDirectMemoryReferences(string client, uint target)
    {
        return ScanExecutableInstructions(client, "memory-references", instruction =>
            instruction.Op0Kind == OpKind.Memory &&
            instruction.MemoryBase == Register.None &&
            instruction.MemoryDisplacement64 == target);
    }

    private static int ScanExecutableInstructions(string client, string label,
        Func<Instruction, bool> predicate)
    {
        var image = File.ReadAllBytes(client);
        if (image.Length < 0x40) return 2;
        var peOffset = BitConverter.ToInt32(image, 0x3C);
        if (peOffset < 0 || peOffset + 24 > image.Length ||
            BitConverter.ToUInt32(image, peOffset) != 0x00004550)
            return 2;

        var sectionCount = BitConverter.ToUInt16(image, peOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(image, peOffset + 20);
        var optionalHeader = peOffset + 24;
        if (optionalHeader + optionalHeaderSize > image.Length) return 2;
        var imageBase = BitConverter.ToUInt32(image, optionalHeader + 28);
        var sectionTable = optionalHeader + optionalHeaderSize;
        var formatter = new IntelFormatter();
        var output = new TextFormatterOutput();
        var matches = 0;

        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var header = sectionTable + sectionIndex * 40;
            if (header + 40 > image.Length) return 2;
            var virtualAddress = BitConverter.ToUInt32(image, header + 12);
            var rawSize = BitConverter.ToUInt32(image, header + 16);
            var rawOffset = BitConverter.ToUInt32(image, header + 20);
            var characteristics = BitConverter.ToUInt32(image, header + 36);
            if ((characteristics & 0x20000000) == 0 || rawSize == 0 ||
                rawOffset > int.MaxValue || rawSize > int.MaxValue || rawOffset + rawSize > image.Length)
                continue;

            var decoder = Iced.Intel.Decoder.Create(32,
                new ByteArrayCodeReader(image, (int)rawOffset, (int)rawSize), DecoderOptions.None);
            decoder.IP = imageBase + virtualAddress;
            var sectionEnd = decoder.IP + rawSize;
            while (decoder.IP < sectionEnd)
            {
                var instruction = decoder.Decode();
                if (instruction.Code == Code.INVALID || !predicate(instruction)) continue;
                output.Clear();
                formatter.Format(in instruction, output);
                Console.WriteLine(instruction.IP.ToString("X8") + "  " + output.Text);
                matches++;
            }
        }

        Console.WriteLine(label + "=" + matches);
        return 0;
    }

    private static bool TryMapVirtualAddressToFileOffset(byte[] image, uint virtualAddress, out int fileOffset)
    {
        fileOffset = 0;
        if (image.Length < 0x100 || BitConverter.ToUInt16(image, 0) != 0x5A4D) return false;
        var peHeader = BitConverter.ToInt32(image, 0x3C);
        if (peHeader < 0 || peHeader > image.Length - 24 || BitConverter.ToUInt32(image, peHeader) != 0x00004550)
            return false;
        var sectionCount = BitConverter.ToUInt16(image, peHeader + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(image, peHeader + 20);
        var optionalHeader = peHeader + 24;
        if (optionalHeader > image.Length - 32 || BitConverter.ToUInt16(image, optionalHeader) != 0x10B) return false;
        var imageBase = BitConverter.ToUInt32(image, optionalHeader + 28);
        if (virtualAddress < imageBase) return false;
        var rva = virtualAddress - imageBase;
        var section = optionalHeader + optionalHeaderSize;
        for (var index = 0; index < sectionCount; index++, section += 40)
        {
            if (section > image.Length - 40) return false;
            var virtualSize = BitConverter.ToUInt32(image, section + 8);
            var virtualStart = BitConverter.ToUInt32(image, section + 12);
            var rawSize = BitConverter.ToUInt32(image, section + 16);
            var rawStart = BitConverter.ToUInt32(image, section + 20);
            var size = Math.Max(virtualSize, rawSize);
            if (rva < virtualStart || rva - virtualStart >= size) continue;
            var rawOffset = (ulong)rawStart + rva - virtualStart;
            if (rawOffset >= (ulong)image.Length) return false;
            fileOffset = checked((int)rawOffset);
            return true;
        }
        return false;
    }

    private static bool TryParseAddress(string input, out uint address)
    {
        var value = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? input.Substring(2) : input;
        return uint.TryParse(value, System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out address);
    }

    private sealed class TextFormatterOutput : FormatterOutput
    {
        private readonly StringBuilder builder = new StringBuilder();
        public string Text => builder.ToString();
        public void Clear() => builder.Clear();
        public override void Write(string text, FormatterTextKind kind) => builder.Append(text);
    }

    private static uint ReadSequence(string path)
    {
        if (!File.Exists(path)) return 0;
        return uint.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : 0;
    }

    private static string? FindClientDirectory(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ARAD.exe"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static void WriteSequence(string path, uint value)
    {
        File.WriteAllText(path, value.ToString() + "\r\n", Encoding.ASCII);
    }

    private static void WriteLog(string path, string message)
    {
        File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + "\r\n", Encoding.UTF8);
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string option)
    {
        return args.Any(arg => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddUInt32(List<byte> buffer, uint value)
    {
        buffer.AddRange(BitConverter.GetBytes(value));
    }

    private static void AddInt32(List<byte> buffer, int value)
    {
        buffer.AddRange(BitConverter.GetBytes(value));
    }

    private static void WriteRelative(byte[] buffer, int offset, uint sourceAfterInstruction, uint destination)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(unchecked((int)(destination - sourceAfterInstruction))), 0,
            buffer, offset, sizeof(int));
    }

    private readonly struct SelectionTrace
    {
        public SelectionTrace(uint nextSequence, uint mapKey, uint mapId, uint entryCount)
        {
            NextSequence = nextSequence;
            MapKey = mapKey;
            MapId = mapId;
            EntryCount = entryCount;
        }

        public uint NextSequence { get; }
        public uint MapKey { get; }
        public uint MapId { get; }
        public uint EntryCount { get; }
    }

    private readonly struct OfflineDungeonMapping
    {
        public OfflineDungeonMapping(uint dungeonId, uint mapId, byte startX, byte startY, byte width,
            byte bossX, byte bossY)
        {
            DungeonId = dungeonId;
            MapId = mapId;
            StartX = startX;
            StartY = startY;
            Width = width;
            BossX = bossX;
            BossY = bossY;
        }

        public uint DungeonId { get; }
        public uint MapId { get; }
        public byte StartX { get; }
        public byte StartY { get; }
        public byte Width { get; }
        public byte BossX { get; }
        public byte BossY { get; }
    }

    private sealed class PvfDungeonRoute
    {
        public PvfDungeonRoute(ushort dungeonId, uint startMapId, byte width, byte height,
            byte startX, byte startY, byte bossX, byte bossY, byte[] doorMasks, uint[] mapIds)
        {
            DungeonId = dungeonId;
            StartMapId = startMapId;
            Width = width;
            Height = height;
            StartX = startX;
            StartY = startY;
            BossX = bossX;
            BossY = bossY;
            DoorMasks = doorMasks;
            MapIds = mapIds;
        }

        public ushort DungeonId { get; }
        public uint StartMapId { get; }
        public byte Width { get; }
        public byte Height { get; }
        public byte StartX { get; }
        public byte StartY { get; }
        public byte BossX { get; }
        public byte BossY { get; }
        public byte[] DoorMasks { get; }
        public uint[] MapIds { get; }
        public int CellCount { get { return Width * Height; } }

        public string Describe()
        {
            return "dungeon=" + DungeonId + " map=" + StartMapId + " size=" + Width + "x" + Height +
                " start=(" + StartX + "," + StartY + ") boss=(" + BossX + "," + BossY + ")";
        }
    }

    private readonly struct PvfRoutePatchResult : IEquatable<PvfRoutePatchResult>
    {
        public static readonly PvfRoutePatchResult None = new PvfRoutePatchResult(0, 0, -1, -1, 0, 0, 0, 0, 0, false);

        public PvfRoutePatchResult(uint sceneTable, uint scene, int logicalSlot, int physicalSlot,
            uint logicalMapId, byte roomX, byte roomY, byte maskBefore, byte maskAfter, bool changed)
        {
            SceneTable = sceneTable;
            Scene = scene;
            LogicalSlot = logicalSlot;
            PhysicalSlot = physicalSlot;
            LogicalMapId = logicalMapId;
            RoomX = roomX;
            RoomY = roomY;
            MaskBefore = maskBefore;
            MaskAfter = maskAfter;
            Changed = changed;
        }

        public uint SceneTable { get; }
        public uint Scene { get; }
        public int LogicalSlot { get; }
        public int PhysicalSlot { get; }
        public uint LogicalMapId { get; }
        public byte RoomX { get; }
        public byte RoomY { get; }
        public byte MaskBefore { get; }
        public byte MaskAfter { get; }
        public bool Changed { get; }

        public bool Equals(PvfRoutePatchResult other) => SceneTable == other.SceneTable && Scene == other.Scene &&
            LogicalSlot == other.LogicalSlot && PhysicalSlot == other.PhysicalSlot &&
            LogicalMapId == other.LogicalMapId && RoomX == other.RoomX && RoomY == other.RoomY &&
            MaskBefore == other.MaskBefore && MaskAfter == other.MaskAfter && Changed == other.Changed;
        public override bool Equals(object? obj) => obj is PvfRoutePatchResult other && Equals(other);
        public override int GetHashCode()
        {
            var routeIdentity = HashCode.Combine(SceneTable, Scene, LogicalSlot, PhysicalSlot);
            var routeState = HashCode.Combine(LogicalMapId, RoomX, RoomY, MaskBefore, MaskAfter, Changed);
            return HashCode.Combine(routeIdentity, routeState);
        }
    }

    private readonly struct RoomCellValue : IEquatable<RoomCellValue>
    {
        public RoomCellValue(uint mapId, uint state)
        {
            MapId = mapId;
            State = state;
        }

        public uint MapId { get; }
        public uint State { get; }

        public bool Equals(RoomCellValue other) => MapId == other.MapId && State == other.State;
        public override bool Equals(object? obj) => obj is RoomCellValue other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(MapId, State);
    }

    private readonly struct RoomAttributeValue : IEquatable<RoomAttributeValue>
    {
        public RoomAttributeValue(uint bossX, uint bossY, uint scene, byte doorMask, byte entranceMask)
        {
            BossX = bossX;
            BossY = bossY;
            Scene = scene;
            DoorMask = doorMask;
            EntranceMask = entranceMask;
        }

        public uint BossX { get; }
        public uint BossY { get; }
        public uint Scene { get; }
        public byte DoorMask { get; }
        public byte EntranceMask { get; }

        public bool Equals(RoomAttributeValue other) => BossX == other.BossX && BossY == other.BossY &&
            Scene == other.Scene && DoorMask == other.DoorMask && EntranceMask == other.EntranceMask;
        public override bool Equals(object? obj) => obj is RoomAttributeValue other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(BossX, BossY, Scene, DoorMask, EntranceMask);
    }

    private enum MapIndexMode : uint
    {
        EntryNumber = 1,
        DirectMapId = 2
    }

    private readonly struct MapIndexTrace
    {
        public MapIndexTrace(MapIndexMode mode, uint requested, uint maximum, uint applied)
        {
            Mode = mode;
            Requested = requested;
            Maximum = maximum;
            Applied = applied;
        }

        public MapIndexMode Mode { get; }
        public uint Requested { get; }
        public uint Maximum { get; }
        public uint Applied { get; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size,
        uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer,
        int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer,
        int size, out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
