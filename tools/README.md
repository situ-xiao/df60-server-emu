# tools —— 客户端运行时补丁工具

这两个工具都**不属于服务端**，它们操作的是运行中的游戏客户端进程（`ARAD.exe`）。

> ## ⚠️ 使用前必读
>
> - 二者都会向游戏进程内存写入代码和数据（`WriteProcessMemory` / 远程线程），
>   属于**运行时内存补丁**。请只在你自己持有的客户端上、本地单机环境下使用。
> - **不修改磁盘上的 `ARAD.exe`**，所有改动只存在于进程内存，关掉进程即消失。
> - 需要**精确匹配的客户端版本**。补丁在写入前会校验目标地址处的原始字节，
>   版本不符会拒绝执行而不是写坏内存。
> - 本项目不含客户端、不含数据表。请自备。
> - 用于研究老游戏的运行时行为；请自行确保使用场景合法。

## 为什么需要这个

服务端本身不需要任何补丁就能跑。但客户端有一个与 PVF 房间拓扑相关的缺陷：
离线/联机加载副本时，首个房间被放进了物理 room table 的 slot 0，
而 PVF 声明的逻辑起点槽位（如 `(0,3)` / slot 12）被忽略，导致小地图房间贴图错位。

`arad-net-launcher` 的作用就是在客户端提交房间拓扑之前，把逻辑槽位与物理槽位
重新对齐。

## 两个工具的分工

| 工具 | 目标框架 | 定位 |
|---|---|---|
| `arad-net-launcher` | net8.0 | **日常用这个**。拉起客户端，装全套补丁（房间 slot 归属、怪物死亡上报、城镇资源守卫） |
| `arad-debug-room-fix` | net8.0 | **诊断用**。追踪地图加载、反汇编、房间内存监视，适合定位新问题 |

两者都链接服务端的 `PvfArchive.cs` / `DungeonCatalog.cs`（`arad-net-launcher`
还链接 `Protocol.cs`），用来读 PVF 里的房间拓扑——所以它们必须留在本仓库内，
不能单独拷出去编译。

## 构建

```bash
# 房间补丁启动器
dotnet build tools/arad-net-launcher -c Release

# 诊断工具（会链接 Iced 反汇编库）
dotnet build tools/arad-debug-room-fix -c Release
```

产物在各自的 `bin/Release/net8.0/` 下。需要 .NET 8 SDK + 运行时。

## arad-net-launcher

### 基本用法

```bash
arad-net-launcher.exe \
  --client  "C:\path\to\ARAD.exe" \
  --workdir "C:\path\to\client" \
  --host 127.0.0.1 --port 7001 \
  --account test --password test \
  --pvf "C:\path\to\Script.pvf" \
  --log launcher.log
```

它不直接 `CreateProcess` 就放任运行，而是：

1. 以 `CREATE_SUSPENDED` 创建客户端进程；
2. 校验目标地址原始字节 → 分配内存 → 写入补丁桩 / 钩子 → 改跳转；
3. 恢复主线程。

所以补丁在客户端跑第一行代码之前就已就位。

### 核心参数

| 参数 | 说明 |
|---|---|
| `--client <path>` | 客户端路径（必填） |
| `--workdir <dir>` | 客户端工作目录，默认当前目录 |
| `--host` / `--port` | 服务器地址，默认 `127.0.0.1:7001` |
| `--account` / `--password` | 登录凭据，默认 `test` / `test` |
| `--pvf <path>` | 数据表路径（读房间拓扑用） |
| `--log <file>` | 补丁日志 |
| `--attach-pid <pid>` | 附加到已在运行的客户端，不新建进程 |
| `--wait-exit` | 等客户端退出后自己再退出 |
| `--watch-online-room` | 持续监视联机房间状态 |

### 其余开关

另外还有 30 余个实验性开关（`--town-guards`、`--force-room-reinit`、
`--effect-pass1-node-guard`、`--trace-map` 等），是开发过程中逐个排查崩溃时留下的
分档验证入口，默认关闭。**不建议在生产环境随意开启**——它们的组合没有完全覆盖测试。

## arad-debug-room-fix

### 诊断副本地图加载

```bash
# 只追踪不修改：进副本后记录实际加载的 map id 与调用者地址
arad-debug-room-fix.exe --client "C:\path\to\ARAD.exe" --trace-map-index

# 打印某个副本在 PVF 里的期望路由，不用启动客户端
arad-debug-room-fix.exe --describe-pvf-route 40

# 反汇编客户端某一处地址
arad-debug-room-fix.exe --disassemble 0x00409435 --count 30

# 监视房间表与当前房间坐标，输出到 room-memory-watch.log
arad-debug-room-fix.exe --client "C:\path\to\ARAD.exe" --watch-room-memory
```

### 常用参数

| 参数 | 说明 |
|---|---|
| `--client <path>` | 客户端路径 |
| `--pvf <path>` | 数据表路径 |
| `--trace-map-index` | 追踪地图加载，不改行为 |
| `--describe-pvf-route <id>` | 打印某副本的 PVF 路由与起点槽位 |
| `--disassemble <addr> --count <n>` | 反汇编 N 条指令 |
| `--find-callers <addr>` | 找调用方 |
| `--watch-room-memory` | 房间表 / 坐标 / 槽位监视 |
| `--attach-room-trace` | 附加到已运行客户端做房间追踪 |
| `--help` | 全部命令 |

`--trace-*` 系列只读，不改客户端行为，是排查"副本刷错房间"的首选入口。

## 工作边界

- 只在内存中改动，磁盘 `ARAD.exe` 保持不变（可随时用原文件核对哈希）
- 补丁前校验原始字节，版本不符即中止
- 所有补丁都绑定到具体地址；客户端换版本后地址失效，必须重新逆向定位
- 端口、账号、路径全部通过参数传入，无硬编码
