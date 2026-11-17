# Wolfpack Mod 框架 —— 项目分析与可行性报告

| 项目 | 内容 |
|---|---|
| 报告版本 | v1.0 |
| 报告日期 | 2026-11-17 |
| 分析对象 | `D:\Program Files (x86)\steam\steamapps\common\Wolfpack` |
| 分析方式 | 静态分析（PE / IL2CPP 元数据 / 序列化资源 / 用户数据），未修改游戏文件 |
| 结论 | **可行**。建议以 BepInEx 6（Unity.IL2CPP x64）+ Il2CppInterop 为地基，构建一层面向 Wolfpack 的 ModKit API |

---

## 0. 摘要（先看这里）

**Wolfpack 是一个 Unity 2020.3.49f1 + IL2CPP（64 位）的联机潜艇模拟游戏。** 它与"可以随便丢 dll 进去"的 Mono 游戏不同，所有 C# 逻辑都被编译进 `GameAssembly.dll`，因此 Mod 框架必须建立在 IL2CPP 互操作（interop）层之上。

**关键判断：**

1. **技术上完全可做。** 游戏无任何反作弊（无 EAC / BattlEye / 自定义完整性校验），无 DRM 包装，IL2CPP 元数据版本为 27.1（2020.3 系列标准版本），属于 BepInEx 6 / Il2CppInterop 1.5.x 明确支持的区间。
2. **游戏自带两套"半成品模组体系"**，可作为框架的落地抓手：文本格式的自定义任务（配合 Steam 创意工坊）与明文 JSON 语言包。这两处不需要注入即可扩展。
3. **真正的难点在网络层，而不是注入层。** 游戏使用自研的 Steam P2P 协议（`W_NetworkManager`，主机即服务端），并且在大厅加入流程中校验客户端版本号。任何"多人可用的 Mod"都必须与这套协议共存，这是本框架最有价值、也最有风险的部分。
4. **建议路线：** BepInEx 6 Bleeding Edge（`Unity.IL2CPP-win-x64`，构建号 ≥ #793，内置 Il2CppInterop 1.5.3）作为加载器/补丁层；在其上实现 `Wolfpack ModKit`：Mod 清单与生命周期、配置、本地化注入、任务/资源内容管线、以及一个可选的"网络桥"（复用游戏 P2P 通道做 Mod 消息同步）。

**投入预估：** 可用原型 3–5 人日；具备内容扩展 + 网络同步的 v1.0 约 12–18 人日。

---

## 1. 项目识别

| 项 | 实测值 | 依据 |
|---|---|---|
| 安装路径 | `D:\Program Files (x86)\steam\steamapps\common\Wolfpack` | 目录探查 |
| Steam AppID | **490920** | `appmanifest_490920.acf` |
| 构建号 / Depot | buildid **24991795** / depot 490921 | `appmanifest_490920.acf` |
| 安装体积 | 5,598,987,338 B（约 5.22 GiB） | `SizeOnDisk` |
| 安装/更新时间 | 2026-10-29 03:56 | `LastUpdated` 时间戳换算 |
| 引擎版本 | **Unity 2020.3.49f1** (18249dd5551b) | `UnityPlayer.dll` 版本信息 + `globalgamemanagers` |
| 脚本后端 | **IL2CPP，x64（PE32+ / 0x8664）** | 无 `Managed` 目录；`GameAssembly.dll` PE 头 |
| 元数据版本 | **27（运行时判定 27.1）** | `global-metadata.dat` 头部 `0xFAB11BAF` / 27 |
| bundleVersion | **2026.08.28** | `globalgamemanagers` 内 PlayerSettings |
| 开发商/发行 | Usurpator | `app.info` |
| 反作弊 / DRM | **无**（仅 CRT 常规 `IsDebuggerPresent`） | 目录与导入表检查 |

**根目录构成（全部文件）：**

```
Wolfpack.exe              887,880 B    Unity 启动桩
UnityPlayer.dll        28,526,512 B    Unity 运行时
GameAssembly.dll       31,115,264 B    IL2CPP 编译产物（全部游戏逻辑）
baselib.dll               501,680 B    IL2CPP 基础库
UnityCrashHandler64.exe 1,232,816 B    崩溃上报
Wolfpack_Data\                         资源与元数据（约 5 GB）
```

> **注**：`app.info` 中的 bundleVersion 为 `2026.08.28`，而用户目录下 `Keybindings.json` 的版本标记是 `Open Beta 2026.07.03 a`。说明该作处于**高频更新的 Open Beta 阶段**，框架必须考虑版本漂移（见第 7 节风险 R2）。

---

## 2. 技术架构剖析

### 2.1 构建形态：纯 IL2CPP

- 不存在 `Wolfpack_Data\Managed\`（Mono 游戏必有），也没有任何 `.dll` 形式的游戏程序集。
- 逻辑全部 AOT 编译进 `GameAssembly.dll`；`Wolfpack_Data\il2cpp_data\Metadata\global-metadata.dat`（7.6 MB）保存类型/方法/字符串元数据。
- `il2cpp_data\etc\mono\*` 与 `il2cpp_data\Resources\*.dll-resources` 表明使用 Mono BCL（mscorlib 等）经 IL2CPP 静态编译。

**含义**：Mod 不能直接引用 `Assembly-CSharp.dll`；必须通过 Il2CppInterop 由元数据动态生成代理程序集，并在运行时经 IL2CPP 运行时 API 调用。Harmony 补丁作用在 `GameAssembly.dll` 的原生方法地址上。

### 2.2 程序集构成（59 个 image）

`ScriptingAssemblies.json` 列出 59 个程序集，其中与 Mod 开发相关的：

| 程序集 | 说明 |
|---|---|
| `Assembly-CSharp.dll` | 游戏主逻辑（全部 9,480 个类型都在此 image 内） |
| `Assembly-CSharp-firstpass.dll` | 插件目录代码（Steamworks 等） |
| `UnityEngine.*`（约 30 个模块） | Unity 模块化 API，含 UI / TextMeshPro / Physics / Animation |
| `Steamworks.NET`（`CSteamworks.dll` + `steam_api64.dll`） | Steam 接口，SDK 1.51 / Steamworks.NET 15.0.1 |
| `Newtonsoft.Json.dll` | JSON 序列化（设置、任务、语言包都依赖它） |
| `Unity.TextMeshPro` / `Unity.Postprocessing.Runtime` | UI 文本与后处理 |
| `Unity.Services.*` / `Purchasing` | Unity 云服务与内购（分析/统计） |
| `Ookii.Dialogs` / `System.Windows.Forms` | 任务编辑器使用 WinForms 文件对话框（**重要：说明编辑器是桌面式 UI，可被 Mod 复用**） |

### 2.3 代码规模（Il2CppDumper 实测）

| 指标 | 数值 |
|---|---|
| 类型总数 | **9,480** |
| 方法总数 | **64,662** |
| 字段总数 | **49,347** |
| MonoBehaviour 子类 | 966 |
| ScriptableObject 子类 | 26 |
| `Wolfpack.*` 命名空间类型 | 221 |
| `W_*` 前缀类型（核心系统） | 192 |
| 全局命名空间类型 | 约 3,609（游戏主体逻辑大量位于全局命名空间） |

> 游戏本体代码并不在 `Wolfpack.*` 命名空间里——那主要是 UI 与任务数据结构；**核心玩法系统（`W_NetworkManager`、`W_Uboat`、`WolfpackPawn` 等）直接位于全局命名空间**，这一点对框架的 API 设计影响很大（见 6.3）。

### 2.4 场景与资源

构建中包含的关卡场景：

```
Assets/Scenes/Boot/Boot.unity      引导（Steam 初始化、光标、设置加载）
Assets/Scenes/Setup/Setup.unity    启动/设置流程
Assets/SteamTestMain.unity         主菜单 / 大厅
Assets/Skirmish1.unity             遭遇战
Assets/Tutorial.unity              教程
```

资源文件：`level0..level4`、`resources.assets`(649 MB) + `resources.assets.resS`(847 MB)、`sharedassets1..3`（合计约 3 GB）。

**实测结论：资源未加密、未压缩，第三方工具可读。** 用 UnityPy 解析：

- `sharedassets0.assets` → 23 个对象（Texture2D/Sprite/Material/Font…），版本 `2020.3f1`；
- `level1` → **22,840 个对象**，其中 6,423 个 GameObject、5,940 个 Transform、3,622 个 MeshRenderer。

**含义**：资源替换（贴图、模型、材质）在"重新打包 SerializedFile 或注入 AssetBundle"层面是可行的，不存在 UnityFS 加密或自定义打包壳。

### 2.5 数据落盘位置

| 类别 | 路径 | 格式 |
|---|---|---|
| 用户设置 | `%USERPROFILE%\AppData\LocalLow\Usurpator\Wolfpack\settings.json` | JSON（Newtonsoft，含画质/音量/按键/海图颜色等 80+ 字段） |
| 键位 | 同目录 `Keybindings.json` | JSON（`{Version, Keys[], Values[]}`，键码数组） |
| 上次任务 | 同目录 `LastMissionSettings.json` | JSON |
| 语音音量 | 同目录 `voipvolumes.json` | JSON |
| 日志 | 同目录 `Player.log` / `Player-prev.log` | 文本（含完整调用栈，**Mod 调试的主要观测点**） |
| 封禁名单 | `Wolfpack_Data\UserData\<SteamID64>\Banlist.xml` | XML |
| 语言包 | `Wolfpack_Data\Languages\*.lang` | JSON（`{header, pairs:[{key:int, value:string}]}`） |
| 站位名 | `Wolfpack_Data\Language\{Official,User}\*.xml` | XML（Station 名称本地化） |

---

## 3. 运行时结构（对 Mod 框架最关键的四个系统）

### 3.1 自研网络栈 `W_*`（Steam P2P，主机权威）

这是本作最独特的架构，也是最需要框架适配的部分。

| 组件 | 职责 |
|---|---|
| `W_NetworkManager`（180 个方法，单例） | 会话管理、P2P 收发、对象生成/销毁、RPC 派发、心跳、封禁 |
| `W_NetworkIdentity` | 网络对象身份（ObjectID / 生成者） |
| `W_NetworkBehaviour` | 网络行为基类（`W_Uboat`、`W_PlayerAvatar`、`WolfpackPawn`、`W_TDC`、`UboatPropulsion`、`UboatRadio`、`AI_Deckgun2` 等 20+ 类继承） |
| `SyncBase` + `SyncedInt/Float/Bool/String/Char/Vector/Quaternion/Ulong/Short/Byte/CharArray` | 状态同步变量（自动打包进 `ObjectData` 消息） |
| `W_RemoteProcedure` → `W_RPC`（`SimpleRPC`/`FloatRPC`/`IntRPC`/`VectorFloatRPC`…）、`W_ClientCommand` 及其派生 | RPC 与客户端指令 |
| `W_Networkmessage` | 消息类型枚举：`Instantiate`、`DestroyObject`、`ObjectData`、`RPC`、`ClientCommand`、`JoinGameRequest/Reply`、`LobbyDataUpdate`、`Ping`、`Kick`、`RadioChar`… 共 36+ 项 |
| `W_ObjectType` | 可生成对象枚举：`PlayerAvatar`、`Uboat`、`Torpedo`、`Merchant`、`TribalDestroyer`、`BogueCarrier`、`FlowerCorvette`、`Sloop`、`MapLine`、`MapCircle`… |

**传输细节：** 基于 `SteamNetworking` 的 `SendP2PPacket`，定义 4 个逻辑通道：

```
protocolChannel = 1   游戏协议
voiceChannel    = 2   VOIP 语音
texChatChannel  = 3   文字聊天
morseChannel    = 4   摩尔斯电码
```

序列化是手写位打包（`addInt/addUint/addFloat/addVector/addQuaternion/addString/addUlong/addbyteArray` 与对应 `read*`），写入 `networkSendBuffer`。

**主机权威：** `W_NetworkManager.IsServer` 为真时本机即服务器；`serverSendToAllClients`、`clientSendToServer`、`spawnObject/spawnForAll`、`serverInstantiate/clientInstantiate` 等 API 表明所有实体由主机生成并下发。

**加入流程包含版本校验**，字符串证据：

```
"Client attemted to join with wrong version of game! Server has version: "
"Game version mismatch. Tried to join lobby with version "
```

> **对框架的直接约束**：Mod 不得修改游戏版本号（否则无法加入大厅）；若要做"多人协同的 Mod"，需在既有通道内做消息多路复用，并在握手阶段交换 Mod 清单，而不是新增网络端口（Steam P2P 只走 Steam 中继/直连，无法自行开 socket 给非 Steam 会话使用）。

### 3.2 任务系统（内容 Mod 的主战场）

- 数据结构：`MissionMetaData`、`MissionData`、`DifficultySettings`、`TargetSettings`、`ObjectiveSettings`、`LocationSettings`、`WeatherSettings`、`Order`（29/16/31/16/7/8/7/10 个方法）。
- 文件格式：**纯文本的 key(...) 结构**，文件头注释即 `// Mission file for the game Wolfpack. https://store.steampowered.com/app/490920/Wolfpack/`，键包括 `MissionTitle(`、`MissionDescription(`、`ShortDescription(`、`TargetList(`、`AntiTargetList(`、`TimeLimit(`、`MissionEndsIfDiscovered(`、`Language(` 等。
- 解析器：`MissionFileParser`（失败时日志 `"Failed to parse the data using MissionFileParser"`）。
- 编辑器：`MissionEditor`（33 个方法，88 个字段）+ 一整套 `Wolfpack.UI.MissionEditor.*Controller`，并通过 WinForms 文件对话框选择文件（`Ookii.Dialogs`）。
- 分发：**Steam 创意工坊**。`MissionBrowser.WorkshopState`、日志与提示串 `"Uploading mission to workshop!"`、`"Click to browse missions on the Steam Workshop!"`、`"Overwrite the currently selected mission in the workshop?"`、协议链接 `http://steamcommunity.com/sharedfiles/workshoplegalagreement`。

### 3.3 本地化系统（最低成本的可扩展点）

- `Wolfpack_Data\Languages\Official English.lang`：JSON，`pairs` 数组内 `key` 为 **`Localisation` 枚举的整数值**，`value` 为文本；28 KB。
- `readme.txt` 官方明确允许自制语言包："To create a custom language pack: Duplicate the Official English pack…"。
- 另有 `Language\Official\English.xml`：站位/设备名称键值（`<rudder>Rudder controls</rudder>` 等）。
- 运行时日志：`"languages path: ...\Wolfpack_Data\Languages"`。

### 3.4 其它可复用系统

| 系统 | 关键类 | 价值 |
|---|---|---|
| 设置 | `W_Options`（97 方法 / 81 字段）、`W_SettingsMenu` | Mod 配置界面可挂靠现有选项菜单 |
| 语音 | `VOIP_PlayerVoice`、`VoiceRoom*`、`VOIPVolumes` | 已按舱室做空间音频，Mod 可复用 |
| 海图 | `W_MapTools`、`W_MapLine`、`MaptoolsUI` | 地图绘制类 Mod 的入口 |
| 数学/物理 | `W_Math`、`EMotorSim2`、`UboatPropulsion`、`W_UboatMovement` | 手感/物理类 Mod 的补丁点 |
| AI | `AI_Escort`、`AI_Convoy`、`AI_Boid`、`AI_Deckgun2`、`AI_TorpedoVision` | 难度/行为 Mod |
| 天空天气 | `Tenkoku.Core.TenkokuModule`（549 字段） | 第三方天气插件，可脚本化 |
| 识别手册 | `RecognitionManual`（46 方法） | 内容展示类 Mod |

---

## 4. 可扩展面评估

| # | 扩展面 | 可行度 | 说明 |
|---|---|---|---|
| S1 | 代码注入 / Harmony 补丁 | ★★★★★ | 无 AC、无完整性校验；BE6 + Il2CppInterop 成熟路径 |
| S2 | 本地化 / 文本替换 | ★★★★★ | 明文 JSON + XML，官方支持自制语言包 |
| S3 | 自定义任务 | ★★★★★ | 文本格式 + 创意工坊，零注入可完成 |
| S4 | UI 扩展（HUD/菜单） | ★★★★☆ | uGUI + TextMeshPro，可在运行时构建 Canvas；需处理输入系统 |
| S5 | 资源替换（贴图/模型） | ★★★☆☆ | SerializedFile 未加密可解析，但需重打包或注入 AB，更新易失效 |
| S6 | 玩法逻辑修改 | ★★★★☆ | 方法级补丁可行；难点是网络同步一致性 |
| S7 | 多人协同 Mod | ★★☆☆☆ | 需复用 P2P 通道 + 自建握手；版本校验与主机权威是硬约束 |
| S8 | 新实体/新舰船 | ★★☆☆☆ | `W_ObjectType` 为编译期枚举，扩展需运行时注册表 + 全端同步 |

**分级建议：** 框架 v1 聚焦 S2/S3/S4/S6（单机 + 客户端可见），S7/S8 作为实验特性，默认在公开房间禁用。

---

## 5. 加载器技术选型

| 方案 | 组成 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| **A. BepInEx 6 BE（推荐）** | `BepInEx-Unity.IL2CPP-win-x64`（构建 #793+，Il2CppInterop 1.5.3，UnityDoorstop 4.6） | 开箱即用的 loader/config/log/Harmony；社区验证充分；支持 metadata 27.x | BE 构建为"bleeding edge"，需锁定具体版本号 | ✅ 采用 |
| B. 自研注入 | Doorstop + 手写 Il2CppInterop 宿主 + 自研 chainloader | 完全可控、体积小 | 重复造轮子（配置、日志、补丁、依赖解析全要自己写），维护成本高 | 备选（若 A 出现无法绕过的兼容问题） |
| C. 纯内容路线 | 仅任务包 / 语言包 / 资源替换 | 零风险、零注入、兼容性最好 | 无法触及逻辑与 UI，能力上限低 | 作为"降级方案"并行提供 |

**A 方案关键事实核对：**

- 官方文档明确：IL2CPP 游戏只能使用 **Bleeding Edge** 构建，Windows x64 对应 `Unity.IL2CPP-win-x64`。
- Il2CppInterop 自 1.5.3 起（BE #777 起合入）对本作这种 2020.3.49 / metadata 27.1 的构建支持良好。
- 需 .NET 运行时宿主（BE6 托管于 CoreCLR）；本机已具备 .NET 6/8/9/10，插件开发统一目标 `net6.0`。

---

## 6. 建议的框架架构：Wolfpack ModKit

### 6.1 总体分层

```
┌──────────────────────────────────────────────────────────────┐
│  Mods/  (用户内容)                                            │
│   ├─ SomeMissionPack/  manifest.json + missions/*.wolfpack    │
│   ├─ BetterHUD/        manifest.json + BetterHUD.dll          │
│   └─ zh-CN.lang                                               │
├──────────────────────────────────────────────────────────────┤
│  WolfpackModKit  (框架 API 层，本项目的核心产物)                │
│   ├─ ModLoader      清单解析 / 依赖排序 / 启用禁用 / 热重载      │
│   ├─ ModContext     日志、配置、本地化、资源、事件总线           │
│   ├─ ContentPipeline 任务注入 / AssetBundle / 贴图替换          │
│   ├─ GameApi        Uboat / Pawn / Mission / Options 封装       │
│   ├─ NetworkBridge  Mod 消息分帧 / 清单握手 / 主机授权           │
│   ├─ UI             Mods 菜单 / HUD 注入 / 键位注册             │
│   └─ Diagnostics    崩溃隔离 / 版本探测 / 兼容性告警            │
├──────────────────────────────────────────────────────────────┤
│  BepInEx 6 (Unity.IL2CPP) + Il2CppInterop + HarmonyX          │
├──────────────────────────────────────────────────────────────┤
│  GameAssembly.dll  (IL2CPP 原生代码)                           │
└──────────────────────────────────────────────────────────────┘
```

### 6.2 目录与清单约定

```
Wolfpack/
├─ winhttp.dll / doorstop_config.ini / .doorstop_version   ← BepInEx 引导
├─ BepInEx/
│   ├─ core/                        BE 运行时 + Interop 程序集
│   ├─ plugins/
│   │   └─ WolfpackModKit.dll       框架本体
│   └─ config/
│       └─ wolfpack.modkit.cfg      全局开关（安全模式、网络 Mod 策略）
└─ Mods/                             ← 框架自有目录（不污染 plugins）
    ├─ 000-core/
    └─ <ModId>/
        ├─ manifest.json
        ├─ <ModId>.dll (可选)
        ├─ missions/*.wolfpack
        ├─ lang/*.lang
        └─ assets/*.bundle
```

`manifest.json` 草案：

```json
{
  "id": "wolfpack.betterhud",
  "name": "Better HUD",
  "version": "1.2.0",
  "author": "you",
  "entry": "BetterHud.Plugin",
  "gameVersion": ">=2026.08.28",
  "dependsOn": ["wolfpack.core>=1.0.0"],
  "network": "client-only",
  "loadOrder": 100
}
```

`network` 取值 `client-only` / `host-required` / `all-clients`，用于公开房间的自动策略（见 R3）。

### 6.3 关键设计决策

1. **用"类型名 + 命名空间"做解析，不用编译期引用。** 由于核心类分布在全局命名空间且 IL2CPP 会裁掉未使用的泛型实例，框架应提供 `Il2Cpp.GetType("W_Uboat")` 式的延迟解析与缓存，避免版本更新导致的硬崩溃。
2. **补丁注册集中在 `PatchRegistry`。** 所有 Harmony 补丁带 `Applied/TargetNotFound` 状态上报，存档到日志，便于"游戏更新后哪些 Mod 失效"的一键诊断。
3. **网络层只做"多路复用"，不改协议语义。** 在 `protocolChannel` 的应用层消息前加 1 字节 Mod 子类型前缀，由 `NetworkBridge` 切分；未安装对应 Mod 的客户端丢弃并计数，不报错。握手阶段交换 `{modId, version}` 集合。
4. **内容优先于代码。** 任务/语言包走"纯数据"路径（S2/S3），失败不影响游戏启动；只有 `.dll` 类 Mod 走注入路径。
5. **崩溃隔离。** 单个 Mod 初始化异常必须被捕获并禁用该 Mod（写入 `ModsQuarantine.json`），保证游戏仍可启动——这是玩家体验的底线。

### 6.4 首个里程碑要验证的技术风险（P1）

| 验证项 | 方法 | 通过标准 |
|---|---|---|
| 注入是否成功 | 安装 BE6 → 启动游戏 → 读 `BepInEx/LogOutput.txt` | 出现 `Chainloader started` 且插件加载日志 |
| Interop 是否可用 | 插件中 `Il2CppInterop` 解析 `W_NetworkManager` 并读 `instance` | 非空且不抛异常 |
| Harmony 是否生效 | 补丁 `W_GameManager` 的 `Start` 打日志 | 游戏内日志出现补丁输出 |
| 资源加载是否可用 | `AssetBundle.LoadFromFile` 加载自制 bundle | 能实例化一个 Cube |
| 版本探测 | 读 `Application.version` | 返回 `2026.08.28` |

---

## 7. 风险清单与对策

| ID | 风险 | 影响 | 对策 |
|---|---|---|---|
| R1 | IL2CPP 互操作限制（泛型擦除、反射受限、委托编组） | 部分 API 无法直接调用 | 优先用 Il2CppInterop 生成的强类型代理；对泛型用 `Il2CppClass` 反射 API；避免跨域委托 |
| R2 | 游戏高频更新（Open Beta，2026.07 → 2026.08 已变） | 补丁失效、字段偏移变化 | 框架内置版本探测 + `PatchRegistry` 状态表；按版本归档适配层；发布页标注兼容版本 |
| R3 | 多人公平性 / 作弊争议 | 社区封禁、房间不兼容 | `manifest.network` 分级；公开房间默认只允许 `client-only`；主机可查看并踢出 Mod 客户端 |
| R4 | 网络版本校验（`Game version mismatch`） | 改版本号即无法联机 | 绝不修改版本号；Mod 信息走自定义消息；握手失败仅提示不阻断 |
| R5 | BE6 为 nightly 构建 | 上游变更导致失效 | 锁定 `6.0.0-be.793+dbc82f0`；仓库内附校验和与离线包 |
| R6 | 性能（64,662 方法的 interop 调用开销） | 帧率下降 | 补丁只挂热点方法；缓存 IL2CPP 指针；避免每帧反射 |
| R7 | 法律与条款 | 下架风险 | 不重分发游戏原始资源；Workshop 内容遵守 Valve 协议；文档声明"仅限单机/私服" |

---

## 8. 实施计划

| 阶段 | 内容 | 产出 | 预估 |
|---|---|---|---|
| **P0 侦察** ✅ | 项目识别、IL2CPP dump、架构分析 | 本报告 + `analysis/` 工件 | 已完成 |
| **P1 环境落地** | 安装 BE6、注入验证、Interop/Harmony/AB 冒烟测试 | `HelloWolfpack` 插件 + 验证记录 | 0.5–1 人日 |
| **P2 框架核心** | ModLoader、manifest、配置、日志、生命周期、隔离 | `WolfpackModKit.dll` v0.1 | 2–3 人日 |
| **P3 游戏适配** | GameApi（Uboat/Pawn/Mission）、场景钩子、UI 注入 | v0.5，可写第一个玩法 Mod | 3–5 人日 |
| **P4 内容与网络** | 任务管线、AssetBundle、NetworkBridge、握手 | v0.9 | 3–5 人日 |
| **P5 工具链与发布** | 模板工程、一键构建部署、示例 Mod ×3、中文文档 | v1.0 | 2–3 人日 |

---

## 9. 已产出的分析工件（可复用）

| 路径 | 内容 |
|---|---|
| `analysis/il2cpp/dump.cs` | 14.9 MB，全部 9,480 个类型的完整签名（含 RVA/偏移） |
| `analysis/il2cpp/il2cpp.h` | 18.7 MB，C++ 结构体定义（native 补丁用） |
| `analysis/il2cpp/script.json` | 38.3 MB，IDA/Ghidra 符号脚本 |
| `analysis/il2cpp/stringliteral.json` | 14,986 条字符串字面量（分析文案、路径、协议） |
| `analysis/il2cpp/DummyDll/` | 59 个桩程序集，可直接在 Rider/VS 中引用以获得智能提示 |
| `analysis/types.json` + `parse_dump.py` | 结构化类型索引（命名空间/基类/方法数） |
| `analysis/analyze_core.py`、`show_class.py` | 核心类筛选、单类全文查看工具 |
| `tools/Il2CppDumper/` | 反编译工具链（可对更新后的版本重跑） |

> 说明：`DummyDll` 仅用于编译期引用与代码阅读，**不能**在运行时加载；运行时代理由 Il2CppInterop 在首次启动时生成（`BepInEx/interop/`）。

---

## 10. 结论与建议

1. **项目可行且条件良好**：Unity 2020.3.49 + IL2CPP 27.1 属于工具链成熟区间；无 AC/DRM；资源未加密；游戏自带任务与本地化两套扩展点。
2. **建议的框架定位**：不是"又一个 BepInEx 整合包"，而是**面向 Wolfpack 的 Mod 运行时与内容管线**——把"清单/依赖/配置/隔离/网络策略/任务注入"这些每个 Mod 都要重复实现的东西固化下来。
3. **先做窄，再做深**：P1 用 1 天验证注入与 Interop（决定整个项目成败）；随后以"本地化 + 自定义任务 + HUD"三类真实 Mod 驱动 API 设计，避免过度设计。
4. **网络 Mod 谨慎推进**：主机权威 + 版本校验决定了它的复杂度，建议放到 v1.0 之后，并以"私服/好友房"为默认场景。

---

*本报告基于静态分析，未运行游戏本体。P1 阶段的运行时验证结果可能修正第 5 节的选型细节。*
