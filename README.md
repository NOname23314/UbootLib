# UbootLib

**Wolfpack 的 Mod 框架与游戏 API 门面**（Steam AppID 490920 · Unity 2020.3.49f1 · IL2CPP）。

UbootLib 做两件事：

1. **运行时**：给 Mod 一个稳定的家——清单、加载隔离、逐帧泵、场景事件、配置热重载、HUD、主菜单管理面板、网络通道。
2. **API 层**：把游戏里 3,106 个类型变成可检索、可安全调用的门面，让 Mod 不用直接啃 `dump.cs`，也不会因为一个字段改名就崩进程。

---

## 能力一览

| 分类 | 能力 | 状态 |
|---|---|---|
| 加载 | `Mods/<Id>/manifest.json`、依赖拓扑排序、`loadOrder`、重复 Id 去重 | ✅ |
| 隔离 | 初始化失败 → 禁用并写入 `modkit.quarantined.log`；连续异常自动禁用 | ✅ |
| 运行时 | Harmony 帧泵（`CursorController.Update`）、场景事件（`OnSceneLoaded`） | ✅ |
| 配置 | JSON 落盘 + `FileSystemWatcher` 热重载，回调固定主线程 | ✅ |
| HUD | uGUI 叠加层，四角锚点、高度自适应、跨场景重建 | ✅ |
| UI | 主菜单 `MODS` 面板：启停、刷新、重启游戏 | ✅ |
| **API** | **类型目录**（3,106 类，可搜索）、**安全反射**（字段/属性/方法）、**状态门面**（玩家/潜艇/主机） | ✅ |
| 网络 | 帧编解码（16/16 自测）、Steam P2P channel 5、Mod 清单握手（非阻断） | ⚠️ 待双端联调 |
| 内容 | 运行时 PNG→Texture2D | ✅ |
| 内容管线 | 自定义任务（校验/生成/剪贴板导入）、语言包（自动安装到游戏 Languages） | ✅ |

---

## 环境

- 游戏：Wolfpack，已验证 `2026.08.28` / IL2CPP 27.1 / x64
- 加载器：[BepInEx 6 BE](https://builds.bepinex.dev/projects/bepinex_be) `Unity.IL2CPP-win-x64` **#793+**
- 构建：.NET SDK 6+

---

## 安装

1. 首次启动游戏，等 BepInEx 生成 `BepInEx/interop/`（约 90 秒）。
2. 把 `UbootLib.dll` 放进 `BepInEx/plugins/UbootLib/`。
3. 启动游戏 → 主菜单右上角出现 `MODS`；日志看 `BepInEx/LogOutput.log`。

卸载：删除该目录即可，不改动任何游戏原文件。

---

## API 层

### 1. 类型目录 `Api.Catalog`

```csharp
int total = Catalog.Count;                       // 3106
var t = Catalog.Find("W_Uboat");                 // 全名或唯一短名
Type clr = t?.Clr;                               // 互操作代理类型（懒解析）
foreach (var hit in Catalog.Search("Torpedo", 10))
    KitLog.LogInfo($"{hit.FullName}  methods={hit.Methods}  base={hit.Base}");
```

目录随程序集内嵌（`api-catalog.json`，331 KB），无需 Mod 自带 `dump.cs`。

### 2. 安全反射 `Api.Reflect`

```csharp
object speed = Reflect.GetField(uboat, "speed");
bool ok = Reflect.TryGetField<float>(uboat, "depth", out var depth);
Reflect.SetField(uboat, "rpm", 120f);
object nested = Reflect.Chain(pawn, "crew.uboat.propulsion.rpm");  // 链式读取，自动空检查
```

所有异常被吞掉并写 Debug 日志——**Mod 不会因为成员改名而崩溃**。

### 3. 状态门面 `Api.Hub`

```csharp
Hub.SceneName;        // 当前场景
Hub.InLobby;          // 是否在大厅
Hub.IsServer;         // 是否主机
Hub.Players;          // IReadOnlyList<W_Player>
Hub.LocalPlayer;      // 按本机 SteamID 匹配
Hub.Uboats;           // 场上所有 W_Uboat
Hub.Pawns;            // 所有 WolfpackPawn
Hub.Summary();        // "scene=Boot role=menu players=0 uboats=0 pawns=0"
Hub.Search("Radio");  // 快捷搜索目录
```

每个属性都是防御式的：游戏更新导致取值失败时返回 `null/false`，不抛异常。

---

## 写一个 Mod

```
Mods/MyMod/{manifest.json, MyMod.dll}
```

```json
{
  "id": "author.mymod",
  "name": "My Mod",
  "version": "1.0.0",
  "entry": "MyMod.Plugin",
  "network": "client-only"
}
```

```csharp
using UbootLib;
using UbootLib.Api;

public class Plugin : ModBase
{
    private HudLabel _hud;

    public override void OnLoad(ModContext ctx)
    {
        _hud = ctx.Hud.AddLabel("hud", new(-24, 96), 18, null, HudAnchor.BottomRight);
        ctx.GameEvents.SceneLoaded += (s, i) => ctx.Log($"scene {s}");
    }

    public override void OnUpdate()
    {
        if (Hub.InLobby)
            _hud?.SetText($"{Hub.Players.Count} players / {Hub.Uboats.Count} uboats");
    }
}
```

`ModContext` 暴露：`Log/Warn/Error`、`GetConfig/SetConfig`、`Harmony`、`Hud`、`GameEvents`、`Manifest`、`Directory`。

---

## 内容管线

### 语言包

Mod 把自己的 `.lang` 放在 `Mods/<Id>/lang/`，框架在启动时（早于 Setup 场景加载语言）校验并复制到
`Wolfpack_Data\Languages\`，随后在游戏 Options 里即可选到。

`.lang` 就是游戏原生格式：`{ "header": "...", "pairs": [ { "key": 20, "value": "Play" } ] }`。
校验会拒绝缺 `header`/`pairs`、key 不是数字、value 不是字符串的文件，并在日志说明原因。

### 自定义任务

Mod 把任务文本放在 `Mods/<Id>/missions/*.txt`，框架启动时解析并校验（必填 `MissionTitle`、
`MissionDescription`、`TonnageGoal`；检查重复键、括号配对、数值类型）：

```
[content] mission ok: demo_mission.txt ("UbootLib Demo Convoy", 20 fields)
```

游戏内导入方式：任务编辑器 → Import mission from clipboard。代码里可直接：

```csharp
var doc = MissionDoc.Parse(File.ReadAllText(path));
doc.Set("TonnageGoal", "15000");
Missions.CopyToClipboard(doc);
```

---

## 构建

```powershell
$env:WOLFPACK_DIR = "D:\Program Files (x86)\steam\steamapps\common\Wolfpack"
dotnet build src/UbootLib/UbootLib.csproj -c Release

# 或一键构建 + 打包发行版
powershell -ExecutionPolicy Bypass -File build.ps1
```

---

## 已知限制

1. **不能用 IMGUI**：ClassInjector 注入的 MonoBehaviour 在本作会因 Il2CppInterop 终结器崩溃，UI 一律走 uGUI，帧泵走 Harmony。
2. **中文 HUD**：内置 Arial 无 CJK 字形，需自备字体资产。
3. **网络**：channel 5 已实现且不干扰游戏 1–4 通道，但单进程回环不能证明投递语义，握手待两台客户端验证。
4. **API 覆盖是"可发现"而非"已封装"**：3,106 个类型都能查到并反射访问，但强类型封装目前只有玩家/潜艇/大厅这一层。
5. **游戏更新**：补丁目标（`CursorController.Update/OnSceneLoaded`）在版本变动后可能失效，需重跑 Il2CppDumper 更新。

---

## 文档

- [整合报告](REPORT.md) —— API 整合范围、验证结果、缺口与路线
- [可行性报告](docs/Wolfpack_Mod框架可行性分析报告.md)
- [P1](docs/P1_验证记录.md) · [P2](docs/P2_验证记录.md) · [P3](docs/P3_验证记录.md) · [P4](docs/P4_网络层记录.md) · [P5](docs/P5_Mod启停菜单记录.md)

## 许可证

MIT。本项目不包含也不分发任何 Wolfpack 游戏资源；请遵守游戏 EULA 与 Steam 创意工坊协议。
