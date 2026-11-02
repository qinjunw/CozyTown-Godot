# 玩家对话验证记录

对应[实现玩家可选模型对话与会话续接](https://github.com/qinjunw/CozyTown-Godot/issues/21)。实现从 integration `29c3c1c55fa63a3d09f049f1d43a4450314afa42` 开始，按[玩家对话合同](../PLAYER_DIALOGUE_CONTRACT.md)完成公开交互与宿主入口。普通验证使用固定客户端与合成预算账本；未读取真实凭据，未发送付费请求。

## 自动检查

使用仓库锁定的 .NET SDK 10.0.401 与 Godot 4.7.2 .NET。可重跑：

```powershell
dotnet test tests/CozyTown.Core.Tests
dotnet test tests/CozyTown.Host.Tests
dotnet test tests/CozyTown.Model.Tests
dotnet build game/CozyTown.Game.csproj
```

| 公开边界 | 已验证行为 |
| --- | --- |
| 玩家交互与会话 | 四位居民的附近固定内容、显式单段生成、独立玩家身份、同居民 NPC 待办不被覆盖、文本不改变资产 |
| 展示与正常提交 | 对话暂停期间没有经历提交；合法显示后的经历在正常边界只提交一次；重复响应与展示被拒绝 |
| 关闭与切人 | 排队、在途、封存未显示、已显示四阶段关闭；旧结果不能进入下一居民面板或产生未显示文本经历 |
| 暂停与计时 | menu、focus、loading、fault 阻止生成和展示；完整到达 59 秒后延后落盘仍及时；60 秒排队或在途逻辑等待到期 |
| 保存与恢复 | 等待状态保留原输入、身份和剩余 47 秒；封存结果不重发；已显示状态不重复提交；缺字段、错关联、旧 schema 与缺少提交依据拒读 |
| 持久终结入口 | 新 registry 实例读取先前关闭/显示事实；读取旧等待档保留固定内容但禁止再生成和导入未来模型经历；预发布日志或 registry 失败保护当前面板 |
| 共用预算入口 | 两个 NPC 预留占满后玩家等待；读档不返还原用量；同逻辑恢复最多一次且跨 registry/ledger 重新打开核对 |
| 协议与来源 | 空文本、缺字段、重复字段、额外动作字段和控制字符回退；超时、预算、旧 profile 不可用原因可见；玩家不能以 NPC 身份保存冻结请求 |

核心对话专项 14 例、宿主对话专项 25 例与派发门 9 例通过。记录/回放专项另外覆盖四居民公开入口、请求创建前计时结清、到达与落盘两种先后次序、文本及身份篡改、旧档终结事实与父前缀。最终完整检查为 core 222、host 141、model 46 例通过，见[检查汇总](evidence/player-dialogue/final-checks.json)。Windows 统一验证不启用 `-Graphical`，source/export 的基础、两组 slice、经济、农作与生产检查全部通过；导出进程使用包内 .NET 运行时。Godot .NET 编译与 headless 资源导入通过。自动检查不表示用户画面与 Windows 键鼠验收完成。

## Windows 用户操作清单

状态：用户于 2026-10-11 确认 `de520bab` 离线候选的[四组人工清单](player-dialogue-manual.md)通过。通过依据为用户反馈；未提供逐项观察或截图。此记录不覆盖后续候选，真实服务样本单独记录。

以下保留复测步骤，使用同一候选构建的普通入口，保留运行记录目录与使用的 `--dialogue-fixture`。

1. 普通规则模式下依次靠近 Mina、Eli、Ren、Sora，按 E 打开，确认姓名、肖像、固定回应与暂停时钟。点击生成，确认固定客户端来源、一段文本和禁用的生成按钮；点击继续散步后世界恢复。
2. 使用 `delayed` 样例。生成后立即 Esc 打开菜单，再关闭菜单；重复一次生成后切出应用、两秒后切回。确认等待状态可读、叠加暂停期间新文本不出现、恢复后才出现当前居民文本。
3. 使用 `delayed` 样例生成后立即关闭，再打开同居民或另一附近居民。确认旧两秒回复不能替换新面板；新会话不会自动生成。
4. 使用 `invalid`、`budget`、`timeout` 分别确认可理解的原因、固定回退与关闭按钮；`timeout` 等待满 60 秒，菜单或失焦期间 60 秒逻辑等待余量仍递减。
5. 使用 `timeout` 样例生成并 F5 保存，继续等待后 F9。确认面板目标与保存时余量恢复，世界仍暂停。关闭该生成后 F9 再读旧等待档，确认明确显示已终结原因，不能再次生成。
6. 使用 `delayed` 样例生成前后分别保存等待或已显示状态并 F9。确认已显示文本属于原会话，关闭后没有重复经历；菜单保持打开时读取，对话在菜单解除前不派发或展示新文本。
7. 用户反馈姓名/肖像/文字排版、按钮可读性与屏幕比例问题。会话身份、到达/展示/终结、经历一次提交与回放由代理自动结构检查，人工无需核对日志字段。

## 新评测预算接线

完整 Demo 的 `player_dialogue` 阶段已接通 `TownGame.PlayerDialogue.cs` 的真实模式分支与 `SendPlayerDialogueAsync`、`TownDialogueHost.Freeze` 的阶段参数及 `TownSaveFile.ValidateHostSnapshot` 的阶段校验。玩家传输使用 `player_text` 身份、同一客户端/账本/两物理槽及持久终结 registry；接线和离线证据见[宿主验证记录](full-demo-host-2026-10-10.md)。本记录不含真实服务样本；后续样本见 [v1 未达标结果](full-demo-real-2026-10-11.md)及 [v2 评测结果](full-demo-real-v2-2026-10-11.md)，分别保留其候选归属。首切片旧 profile 的真实玩家入口明确回退。

## 固定候选

候选提交为 `de520babd9956274605a51852329876b55e730f5`，已合 integration `c0c1e5b`。本地归档为 `artifacts/player-dialogue-candidate/de520bab.zip`，SHA-256 为 `c5e775848e22c004b774b009a12e85df0f0f1a2b6b1eb94ddf29856ba675ad1f`。解压后双击 `Start.cmd` 启动默认离线 success 样例；其余四种样例有独立 `Run-*.cmd` 入口，每次建立独立数据目录。人工只需完成[四组操作](player-dialogue-manual.md)，无需检查日志字段。
