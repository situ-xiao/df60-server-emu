# df60-server-emu

[![.NET](https://img.shields.io/badge/.NET-net481%20%2F%20x86-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)]()

某款 2009 年 2D 横版动作网游的**服务端协议模拟器**，用 C# 从零重新实现。

客户端是闭源二进制，本项目的一切协议逻辑都来自对它的**静态反汇编**与**联机抓包比对**。
仓库只包含协议逻辑代码，**不含任何游戏资源、客户端程序或数据表**。

> 定位：协议研究 / 逆向工程学习 / 软件保存（software preservation）。
> 不提供客户端下载，不提供数据表，不面向公众服务。

---

## 目录

- [项目背景](#项目背景)
- [工作原理](#工作原理)
- [范围：这里有什么，没有什么](#范围这里有什么没有什么)
- [仓库结构](#仓库结构)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [命令行参数](#命令行参数)
- [配置文件与数据目录](#配置文件与数据目录)
- [实现状态](#实现状态)
- [已知问题](#已知问题)
- [排错](#排错)
- [参与贡献](#参与贡献)
- [法律声明](#法律声明)
- [许可证](#许可证)

---

## 项目背景

这套客户端发布于 2009 年，服务端早已停止运营。它的网络层是一个未公开文档的
私有 TCP 协议：自定义包头、自研校验、按命令号分发的变长负载。

本项目的做法是**先逆向、再验证**：

1. 在反汇编器里定位客户端的命令分发函数，读出每个命令号的字段布局；
2. 用 `sub_XXXXXX @0x......` 的形式把地址写进注释，作为该字段的出处；
3. 让模拟器发出报文，对比客户端行为，确认布局正确；
4. 把验证过的断言固化成自检用例（`SelfTest.cs`，1700+ 行）。

因此源码注释里会看到大量形如 `// DNF.exe 1.0.1.9, sub_419420 NOTIPACKET_CHANNEL_INFO case 1`
的标注 —— 这不是噪音，是每个字节的依据。要复核任何一条报文，直接跳地址即可。

## 工作原理

```
┌──────────────┐   TCP 7001    ┌──────────────────────┐
│  游戏客户端   │ ───────────▶ │  DF60A1（本项目）      │
│ （你自备，闭源）│ ◀─────────── │  · 会话状态机          │
└──────────────┘   私有协议     │  · 命令分发            │
                               │  · SQLite 持久化       │
        │                      └──────────┬───────────┘
        │ 静态数据（副本/怪物/掉落/任务）      │
        └──────────────────────────────────┘
                  读取 Script.pvf
```

服务端承担**权威状态**：房间拓扑、掉落归属、任务进度、角色数据。
战斗演算由客户端负责，服务端只校验结果并落库。

## 范围：这里有什么，没有什么

| 包含 | 不包含 |
|---|---|
| 协议编解码（`Protocol.cs` / `GamePackets.cs`） | 客户端可执行文件 |
| TCP 会话与状态机（`GameServer.cs`） | 任何 `.pvf` 数据表 |
| SQLite 持久化（`SqliteGameStore.cs` / `schema.sql`） | 任何 `.npk` 图集 / 音频 |
| 副本生成与房间布局（`DungeonCatalog.cs`） | 客户端内存补丁 / 修改工具 |
| 掉落、任务、商店、技能目录 | 第三方专有 DLL |
| 自检套件（`SelfTest.cs`） | 编译产物 |

## 仓库结构

```
.
├── src/DF60A1/
│   ├── Program.cs              入口、命令行解析、自检调度
│   ├── GameServer.cs           TCP 会话、状态机、主循环
│   ├── GamePackets.cs          下行报文构造（按命令号）
│   ├── Protocol.cs             上行解码、包头解析、校验
│   ├── EntranceProtocol.cs     登录/频道握手
│   ├── SqliteGameStore.cs      角色、背包、装备、任务持久化
│   ├── schema.sql              建库脚本
│   ├── DungeonCatalog.cs       副本与房间拓扑
│   ├── DropCatalogs.cs         掉落表
│   ├── QuestCatalog.cs         任务表
│   ├── MonsterCatalog.cs       怪物表
│   ├── SkillCatalog.cs         技能表
│   ├── ItemCategoryCatalog.cs  物品分类
│   ├── PvfArchive.cs           数据表读取器
│   ├── SelfTest.cs             协议自检断言
│   └── ...                     其余目录/状态类
├── LICENSE
└── README.md
```

## 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows（服务端本身跨平台，但客户端仅 Windows） |
| 运行时 | .NET Framework 4.8.1 |
| 构建 | .NET SDK 6.0 或更高（需能构建 `net481`） |
| 架构 | **x86**（客户端是 32 位进程，SQLite 互操作库须匹配位数） |
| 客户端 | 你合法自持的 2009 版客户端 |
| 数据表 | 与客户端版本匹配的 `Script.pvf` |

## 快速开始

### 1. 准备数据表

把 `Script.pvf` 放到 `src/DF60A1/data/` 下：

```
src/DF60A1/
└── data/
    └── Script.pvf
```

> 服务端启动时会解出副本、怪物、掉落、任务等全部静态数据。
> 缺这个文件只能跑到登录握手阶段，之后会直接报错退出。
> 路径可用 `--pvf` 覆盖。

### 2. 构建

```bash
cd src/DF60A1
dotnet build -c Release -p:Platform=x86
```

产物：`bin/x86/Release/net481/DF60A1.exe`

### 3. 先跑自检（不需要客户端）

```bash
dotnet run --project src/DF60A1 -c Release -p:Platform=x86 -- --self-test
```

会校验协议编解码、目录表加载、数据库往返。**首次上手建议先跑这一步**，
能立刻区分"是服务端没配好"还是"客户端连不上"。

### 4. 启动服务端

```bash
dotnet run --project src/DF60A1 -c Release -p:Platform=x86
```

默认监听 `127.0.0.1:7001`，账号 `test` / 密码 `test`。
首次启动会在 `arad_data/` 下建库并执行 `schema.sql`。

### 5. 连接客户端

让客户端指向 `127.0.0.1:7001` 即可。

> 客户端如何改指向，取决于你的客户端版本与来源，本项目不涉及、也不附带任何
> 客户端修改手段。若客户端连不上，见下方[排错](#排错)。

## 命令行参数

| 参数 | 说明 |
|---|---|
| `--host <ip>` | 监听地址，默认 `127.0.0.1` |
| `--port <n>` | 监听端口，默认 `7001` |
| `--account <name>` | 预置账号名 |
| `--password <pass>` | 预置账号密码 |
| `--character <name>` | 预置角色名 |
| `--job <id>` | 预置角色职业 |
| `--pvf <path>` | 数据表路径，默认 `data/Script.pvf` |
| `--data <dir>` | 数据目录（数据库所在），默认 `arad_data/` |
| `--launch` / `--client <path>` | 启动服务端后自动拉起客户端 |
| `--auto-select` | 自动走完选角，省去手动点选 |
| `--tutorial` | 直接进新手教学地图 |
| `--legacy-channel-header` | 用旧版频道包头（兼容部分修改端） |

### 调试与审计

| 参数 | 说明 |
|---|---|
| `--self-test` | 跑协议自检套件，不监听端口 |
| `--skill-self-test` | 只跑技能相关断言 |
| `--audit-dungeons` | 审计副本表，列出无法解析的房间 |
| `--audit-drops` | 审计掉落表 |
| `--audit-item-categories` | 审计物品分类 |
| `--audit-cera-shop` | 审计商店目录 |
| `--audit-stackable-types` | 审计可堆叠物品类型 |
| `--dungeon-layout-regression` | 副本布局回归（防房间拓扑退化） |
| `--list-pvf [prefix]` | 列出数据表内部条目 |
| `--inspect-pvf-file <path>` | 打印数据表内某个文件的内容 |
| `--quest-probe` | 打印任务表结构诊断信息 |
| `--seed-character` | 灌入一个测试角色 |

审计类命令**只读不写**，适合排查"某个副本刷不出怪""掉落为空白"这类问题。

## 配置文件与数据目录

```
src/DF60A1/
├── data/
│   └── Script.pvf        # 数据表（你自备，不纳入版本控制）
└── arad_data/
    ├── arad.sqlite       # 角色/背包/任务/装备全量存档
    ├── arad.sqlite-wal
    └── arad.sqlite-shm
```

`arad_data/` 会在首次启动时自动创建。删除整个目录 = 清档重来（不可恢复）。

数据库表：`accounts`、`characters`、`character_items`、`character_storage`、
`character_equipment`、`character_avatars`、`character_skills`、`character_quests`、
`character_completed_quests`、`character_dungeon_progress`、`account_client_config`。

## 实现状态

**已完成**

- 登录、服务器列表、频道选择
- 角色列表 / 建角 / 选角 / 删除
- 城镇初始化（角色实体、NPC、可交互物）
- 新手教学地图
- 洛兰系列副本：进入、房间切换、怪物生成、结算、翻牌
- 掉落系统（地面掉落物、拾取、归属判定）
- 任务接取 / 完成、商店目录、技能目录
- 角色数据全量持久化（装备、背包、仓库、时装、技能、任务进度）

**未完成**

- 商城交易
- 深渊模式
- 觉醒流程（未测试）
- 装备耐久修理

## 已知问题

1. 所有副本 `slot0` 属性异常，导致小地图房间贴图错位
2. 部分客户端文本显示错位
3. 消耗品使用后数量不减少
4. 翻牌功能可用，但展示异常

这些都是**已知未修**的，欢迎 PR，但请附上可复现步骤。

## 排错

**启动即退出，提示找不到 Script.pvf**
→ 确认 `src/DF60A1/data/Script.pvf` 存在；或用 `--pvf <绝对路径>` 指定。

**端口 7001 被占用**
→ 先 `netstat -ano | findstr :7001` 找到占用进程；或用 `--port` 换端口
（注意客户端侧也要同步改）。

**`BadImageFormatException` / SQLite 加载失败**
→ 位数不匹配。确认用 `-p:Platform=x86` 构建，且运行的是
`bin/x86/Release/net481/` 下的产物。

**客户端连上后卡在选角 / 城镇黑屏**
→ 先跑 `--self-test`，再跑 `--audit-dungeons` 看副本表是否解析完整。
数据表版本与客户端不匹配是最常见原因。

**能进副本但房间全错 / 怪不对**
→ 跑 `--dungeon-layout-regression`，它会列出无法解析的房间及其 PVF 条目。

## 参与贡献

欢迎 issue 和 PR。提交前请：

1. 跑 `--self-test` 全绿；
2. 涉及副本的改动跑 `--dungeon-layout-regression`；
3. **不要**在 PR 里附带任何游戏资源、反编译产物或客户端文件。

代码风格：C# 7.3（`net481` 限制），无 nullable，`x86` 目标。注释里引用地址时
保持 `sub_XXXXXX @0x......` 格式，方便他人复核。

## 法律声明

本项目**仅供协议研究、逆向工程学习与软件保存用途**。

- 不含任何版权素材，不提供客户端下载，不提供数据表
- 与游戏开发商、发行商无任何关联，未获其授权或认可
- 不得用于搭建面向公众的在线服务，不得用于任何商业用途
- 使用者需自行确保所持客户端与数据表的来源合法，并自行承担相应法律责任

作者不对因使用本项目产生的任何直接或间接损失负责。

## 许可证

[MIT](LICENSE) © 2026 LuMyh

仅覆盖本仓库内的原创代码，不延伸至任何第三方作品。
