# df60-server-emu

A from-scratch reimplementation of the **server side** of a 2009-era 2D side-scrolling
action MMO client, written for protocol research and preservation.

The client is a closed-source binary. Everything here was produced by **static
disassembly and packet capture** of a legally obtained copy — this repository
contains the resulting protocol logic only. It ships **no game assets, no client
binary, and no game data tables**. You must supply those yourself (see
[Prerequisites](#prerequisites)).

---

## 这是什么

一套本地服务端模拟器，用 C# 重新实现了某款 2009 年 2D 横版动作网游的**下行/上行协议**，
目标是让原版客户端能在离线模式下跑通完整流程：登录 → 角色列表 → 建角 → 进城镇 →
进副本 → 结算。

协议细节全部来自对客户端二进制的静态逆向与联机抓包比对。源码注释里保留了大量
`sub_XXXXXX @0x420B0F` 形式的定位标注，方便后来者复核每一条报文字段的依据。

## 范围：这里有什么，没有什么

| 包含 | 不包含 |
|---|---|
| 协议编解码（`Protocol.cs` / `GamePackets.cs`） | 客户端可执行文件 |
| TCP 会话与状态机（`GameServer.cs`） | 任何 `.pvf` 数据表 |
| SQLite 持久化（`SqliteGameStore.cs` / `schema.sql`） | 任何 `.npk` 图集 / 音频 |
| 副本生成与房间布局（`DungeonCatalog.cs`） | 内存补丁 / 客户端修改工具 |
| 掉落、任务、商店、技能目录 | 第三方专有 DLL |
| 自检套件（`SelfTest.cs`，1700+ 行断言） | 编译产物 |

## Prerequisites

1. **.NET Framework 4.8.1** + .NET SDK（能构建 `net481`）
2. **你自己合法持有的客户端程序**——本仓库不含、也不会分发
3. **对应的数据表文件**（`Script.pvf`），放到 `data/` 目录下

服务端启动时会读取 `data/Script.pvf` 解出副本、怪物、掉落、任务等全部静态数据。
没有这个文件，服务端只能跑到登录阶段。

## 构建

```bash
cd src/DF60A1
dotnet build -c Release -p:Platform=x86
```

目标框架 `net481` / `x86`（客户端是 32 位进程，SQLite 互操作库需要匹配位数）。

## 运行

```bash
# 默认 127.0.0.1:7001，账号 test / test
dotnet run --project src/DF60A1
```

首次启动会在当前目录创建 SQLite 库并执行 `schema.sql`。

命令行参数：

| 参数 | 说明 |
|---|---|
| `--self-test` | 跑内置协议自检套件，不监听端口 |
| `--skill-self-test` | 只跑技能相关断言 |

> 默认账号密码是 `test` / `test`，仅用于本地单机调试，服务端不应暴露到公网。

## 实现状态

已完成：

- 登录、服务器列表、频道选择
- 角色列表 / 建角 / 选角 / 删除
- 城镇初始化（角色实体、NPC、可交互物）
- 新手教学地图
- 洛兰系列副本：进入、房间切换、怪物生成、结算、翻牌
- 掉落系统（地面掉落物、拾取）
- 任务接取/完成、商店目录、技能目录
- 角色数据全量持久化（装备、背包、仓库、时装、技能、任务进度）

未完成：

- 商城交易
- 深渊模式
- 觉醒流程（未测试）
- 装备耐久修理

## 已知问题

1. 所有副本 `slot0` 属性异常，导致小地图房间贴图错位
2. 部分客户端文本显示错位
3. 消耗品使用后数量不减少
4. 翻牌功能可用但展示异常

## 法律声明

本项目**仅供协议研究、逆向工程学习与软件保存（software preservation）用途**。

- 不含任何版权素材，不提供客户端下载，不提供数据表
- 与游戏开发商、发行商无任何关联，未获其授权或认可
- 不得用于搭建面向公众的在线服务，不得用于任何商业用途
- 使用者需自行确保所持客户端与数据表的来源合法，并自行承担相应法律责任

作者不对因使用本项目产生的任何直接或间接损失负责。

## License

MIT — 见 [LICENSE](LICENSE)。
仅覆盖本仓库内的原创代码，不延伸至任何第三方作品。
