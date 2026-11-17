# UbootLib 整合报告

**日期** 2026-11-17 · **版本** 0.3.0 · **对象** Wolfpack `2026.08.28`（Unity 2020.3.49f1 / IL2CPP 27.1 / x64）

---

## 1. 做了什么

在原 `WolfpackModKit`（加载器 + 运行时骨架）基础上，新增一层**游戏 API 门面**，并整体更名为 **UbootLib**。

| 模块 | 内容 |
|---|---|
| `Api/Catalog` | 从 IL2CPP 元数据生成 **3,106 个游戏类型**的目录（内嵌 331 KB JSON），支持全名/短名精确查找与子串搜索，类型懒解析 |
| `Api/Reflect` | 字段/属性/方法的安全反射：异常吞掉写 Debug 日志，成员缓存，支持 `Chain("crew.uboat.rpm")` 链式读取 |
| `Api/Hub` | 状态门面：`SceneName / InLobby / IsServer / Players / LocalPlayer / Uboats / Pawns / Summary()` |
| 命名 | 命名空间、程序集、GUID（`uboot.lib`）、日志前缀、示例 Mod（`uboot.samplehud`）全部统一 |

保留并复用：Mod 清单与依赖排序、加载隔离、Harmony 帧泵、场景事件、配置热重载、uGUI HUD、主菜单 Mod 面板、网络编解码/传输/握手。

## 2. 验证结果

| 项 | 证据 |
|---|---|
| 编译 | 0 警告 0 错误（`UbootLib.dll` + `SampleHud.dll`） |
| 目录加载 | `[api] catalog loaded: 3106 game types` |
| 搜索可用 | `catalog: 3106 game types (e.g. Uboat, W_Uboat, MenuUboat, TestUboat, UboatRadio, UboatBallast)` |
| 状态门面 | 心跳日志 `scene=Boot role=menu players=0 uboats=0 pawns=0`，随场景切换变化 |
| 运行稳定 | 主菜单 120 fps，日志无错误，`ErrorLog.log` 为空 |
| 编解码自测 | `codec self-test: 16/16 passed`（P4 起保持） |

## 3. 关键判断

- **"所有 API" 的可落地形态是"可发现 + 可安全调用"，不是"逐个手写包装"。** 3,106 个类型逐一手写不现实且会随更新腐烂；目录 + 反射 + 少量强类型门面是可持续的做法。
- **强类型封装目前只覆盖大厅/玩家/潜艇这一层**，其余靠 `Catalog` + `Reflect` 按需取用。这是有意的取舍，后续按真实 Mod 需求逐个"提升"为强类型 API。
- **防御式取值是框架的责任**：所有门面属性失败返回 `null/false`，把"游戏更新导致崩溃"降级为"某个值读不到"。

## 3.5 内容管线（0.4.0 新增）

| 能力 | 实现 | 实测 |
|---|---|---|
| 语言包安装 | 扫描 `Mods/*/lang/*.lang`，校验后复制到 `Wolfpack_Data\Languages` | `language pack installed: UbootLib Demo.lang (header 'UbootLib Demo CN', 320 entries)` |
| 任务校验 | 解析 `Key(value)` 文本，校验必填键/重复键/括号/数值 | `mission ok: demo_mission.txt ("UbootLib Demo Convoy", 20 fields)` |
| 任务生成与导入 | `MissionDoc` 往返序列化 + 剪贴板输出 | 已实现 |

报告里 S2（本地化）与 S3（自定义任务）两个高价值扩展面均已落地。

## 4. 缺口（按优先级）

| # | 缺口 | 影响 | 建议 |
|---|---|---|---|
| 1 | 任务需经剪贴板导入编辑器，任务列表不会自动出现新任务 | 玩家多一步操作 | 逆向社会工坊订阅目录，或做一键导入 UI |
| 2 | 网络未双端验证 | 多人 Mod 不可宣称可用 | 两台客户端联调 |
| 3 | 强类型 API 面偏窄 | 写玩法 Mod 仍需读 `dump.cs` | 按示例 Mod 驱动扩展 |
| 4 | 无 Mod 设置界面 | 只能改 JSON | 复用 `ModsPanel` 的 UI 基建 |
| 5 | `gameVersion` 未做门禁 | 版本不匹配时无提示 | 启动时校验 manifest |

## 5. 结论

UbootLib 现在是一个**可发布的框架骨架 + API 门面**：能装、能跑、能写 Mod、能查 API，且崩溃面被压到最低。

它还不是"玩法 SDK"——缺少内容管线和更厚的强类型 API。但基础已经稳定，后续每个能力都可以独立增量，不会推翻现有结构。
