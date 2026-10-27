# ARAD Offline DebugConfig Notes

> **Note on paths.** This is the original engineering notebook, kept verbatim for
> its technical content. Paths in it refer to the author's development tree
> (`arad_server\...`, root-level `.ps1` / `.cmd` launcher scripts) and do **not**
> exist in this repository. For build steps and the supported command-line
> surface, see [`../README.md`](../README.md) instead.
>
> 本文件是原始开发笔记，为保留技术细节而原样收录。其中的路径属于作者当时的
> 开发目录结构，本仓库并不存在这些脚本；构建方式与可用参数请以
> [`../README.md`](../README.md) 为准。

`DebugConfig.txt` is read by this ARAD client. The old launcher experiment
which cycled rooms based on a map-key table is retired; it is not used by the
normal launcher path and does not patch `ARAD.exe` or its memory.

The offline loader has three `dungeon index` paths. `0` is a special fixed
offline scene and does not consume `[map index]`. Values from `11000` through
`11999` use `[map index]` as a 1-based map-table entry. Other nonzero values
use `[map index]` as a direct map ID.

For the current Mountain Ridge test configuration, start in town and enter
Mountain Ridge manually:

```text
[dungeon index] 0
[map index] 5607
[path info] 5 3
```

For this path, `map index` must not be treated as the Mountain Ridge start
map. The patch waits until the client loads map `5601` after the player enters
Mountain Ridge from town.

You can run `ARAD.exe` directly. Running this helper also starts the client
without a memory patch:

```powershell
.\arad-debug-room-fix\bin\Release\net8.0\arad-debug-room-fix.exe
```

To trace a map entry without changing it, close ARAD first and run:

```powershell
.\arad-debug-room-fix\bin\Release\net8.0\arad-debug-room-fix.exe --trace-map-index
```

Enter the test dungeon within five minutes. The helper records
either `map-index-mode=entry-number` with its permitted range, or
`map-index-mode=direct-map-id` with the raw ID passed to the map loader. Its
in-memory trace is removed automatically when the game process exits.

For static inspection of a code address, the helper supports:

```powershell
.\arad-debug-room-fix\bin\Release\net8.0\arad-debug-room-fix.exe --disassemble 0x00409435 --count 30
```

To diagnose a town-to-dungeon transition without running a server, use the
root-level launcher script:

```powershell
.\trace-offline-dungeon.ps1
```

It starts the client in a suspended state, installs an inactive in-memory
trace, and asks for confirmation once the client is idle in town. It then
records the map ID and caller address of every subsequent map load to
`map-loader-trace.log`. For the offline GrakKarak fallback it also records the
two transition arguments received by the fallback routine. It does not modify
`ARAD.exe` on disk or start a server.

To replace the client's fixed offline GrakKarak fallback with the selected
dungeon's PVF start map, use:

```powershell
.\launch-offline-dungeon-fix.cmd
```

The helper reads `Script.pvf` once at launch and installs a temporary
process-memory lookup table. For example, dungeon `40` resolves to map `5601`.
It does not use `DebugConfig.txt` after startup, change client files, or start
a server.

To monitor the actual room cell and coordinate selected by ARAD.exe, close
ARAD.exe and run:

```powershell
.\launch-debug-room-slot.cmd
```

With the town DebugConfig above, that launcher now also prints the PVF route
selected for dungeon 40. `room-memory-watch.log` begins with
`pvf-route` and `pvf-route-cell` entries, then records `pvf-route-active` and
a read-only `pvf-route-layout-snapshot` when the first room has loaded. The
direct DebugConfig load path is also armed dynamically: only after ARAD has
created map `5601` in physical room-table slot 0 does the existing memory
patch enable the logical `(0,3)` / slot 12 remap. The log must contain
`ridge-memory-patch armed=true`; the test point is then that the initial
active room is `(0,3)`, map `5601`, and the minimap/doors match the `exits=`
value printed for `pvf-route-cell room=(0,3)`.
The expected route can also be printed without starting ARAD.exe:

```powershell
.\arad-debug-room-fix\bin\Release\net8.0\arad-debug-room-fix.exe --describe-dungeon-route 40
```

The monitor reads the room table and active room state from ARAD.exe memory
and logs `cell-room=(x,y)`, `active-room=(x,y)`, `slot`, `map-id`, cell
address, target-room status, and room-lookup remap hits to
`room-memory-watch.log`. The temporary process-memory patch leaves the
offline loader's sole room object in physical slot 0, updates the active
`(roomX, roomY)`, and maps lookups for the PVF logical start slot
(`slot = x + y * width`) back to physical slot 0. The mapping is bound to the
current room-table address, so it stops matching after the table is replaced.
Because the client's final transition call resets the active coordinates to
`(0,0)`, a second guarded hook reapplies the PVF coordinates immediately after
that call returns. A coordinate-use guard also restores the values immediately
before ARAD reads them for room lookup, while the live monitor repairs later
asynchronous resets of the same validated dungeon-state object.
It does not copy or move a live room object and does not modify the executable
on disk.
