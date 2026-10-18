# 当前架构与 NPC 修改入口

本页索引当前四居民 Demo 的模块、公开边界和修改位置。领域词汇见 [CONTEXT.md](../CONTEXT.md)，产品范围见[产品基线](PRODUCT_BASELINE.md)；内容、模型、保存与回放的详细约束分别由[内容合同](CONTENT_AUTHORING.md)、[真实模型合同](REAL_MODEL_CONTRACT.md)、[保存与回放合同](SAVE_REPLAY_CONTRACT.md)维护。

## 模块与依赖

| 部分 | 职责与公开入口 |
| --- | --- |
| Core：`src/CozyTown.Core` | 普通 C# 的权威世界状态、内容校验、局部观察、决策机会、候选校验、经济生产和逻辑快照。[TownWorld](../src/CozyTown.Core/Town/TownWorld.cs) 提供 `Observe`、`Advance`、`GetDecisionRequests`、`MarkDispatched`、`MarkResponseArrived`、`ReceiveResult`、`CreateRuleCandidate`；[TownSnapshot](../src/CozyTown.Core/Town/TownSnapshot.cs) 提供 `CaptureSnapshot`、`CaptureJson`、`RestoreCandidate`。 |
| Model：`src/CozyTown.Model` | 独立 C# 的 DeepSeek HTTP、响应投影与费用、发送次数、物理槽位的持久准入。[ModelContracts](../src/CozyTown.Model/ModelContracts.cs) 定义 `FrozenModelRequest`、`ModelCompletion`、`IModelJournal`；[DeepSeekClient.SendAsync](../src/CozyTown.Model/DeepSeekClient.cs) 返回传输完成结果。宿主接口与账本规则见[模型库说明](../src/CozyTown.Model/README.md)。 |
| Game：`game/Host` | Godot 宿主负责场景提取、实际空间查询、玩家输入、暂停、UI、主线程派发与邮箱、保存发布和运行记录。[TownContentLoader.Load](../game/Host/TownContentLoader.cs) 合并内容；[TownGame](../game/Host/TownGame.cs) 组合世界与模型；[TownModelProtocol.BuildRequest / Parse](../game/Host/TownModelProtocol.cs) 连接 NPC 候选协议。 |
| 内容与表现 | [TownMap.tscn](../game/World/TownMap.tscn) 维护地点、道路、障碍和观察区域；[town.json](../game/Content/town.json) 维护居民、日程、资源与初态。稳定 ID 连接两者，提取后交给 [TownContent.Create](../src/CozyTown.Core/Town/TownContent.cs) 校验；运行状态由 Core 维护。 |
| Launcher：`tools/CozyTown.Play` | [PlayCommand.Run](../tools/CozyTown.Play/Program.cs) 核验候选文件清单和本机绑定，真实模式只读预检配置、凭据格式及已有账本，再启动独立游戏进程。`--check-only` 在预检后退出。双击入口与配置模板见[Windows 真实试玩入口](REAL_PLAY_LAUNCHER.md)。 |
| 保存、记录与回放 | Game 的 [TownSaveFile.Write / Read](../game/Host/TownRunJournal.cs) 发布和读取宿主封套，`TownRunJournal` 实现模型记录、`StartReplay`、`CaptureReplay` 与 `ExportSnapshotZip`。Core 的 [TownReplay.ReadPackage / Run](../src/CozyTown.Core/Town/TownReplay.cs) 重跑记录；[核心回放](REPLAY.md)使用记录中的空间事实，[场景回放](SCENE_REPLAY.md)由 Godot 重新执行空间查询。 |

项目引用为 `Game → Core + Model`、`CozyTown.Play → Core + Model`、`CozyTown.Replay → Core`。Core 与 Model 相互独立，均无 Godot 引用；Launcher 链接同一份 [TownModelRuntimeSettings.cs](../game/Host/TownModelRuntimeSettings.cs)，不引用 Game 工程。

## 从决定到世界事实

1. `TownWorld.GetDecisionRequests` 按居民活动和日程产生机会，冻结自身状态、局部观察、经历、允许动作与目标。
2. Game 通过 `TownModelProtocol.BuildRequest` 构建提供方请求；Model 在持久登记记录和预算准入后发送，并保存完成结果与实际用量。
3. Game 将到达通知和完成结果送入主线程邮箱，解析候选后调用 `ReceiveResult` 封存。Core 在世界提交边界重新检查会话、请求、活动、期限、空间与资源条件，再执行或拒绝。

`ModelCompletion.Succeeded` 只表示模型层没有失败代码；`response_received` 表示结果已封存。世界执行以 `candidate_executed` 为准，拒绝为 `candidate_rejected`。真实决定需关联 `source=real_model`；`rule_fallback` 单独记录规则回退。模型台词、传输成功和规则回退均不能代替真实模型行动已经执行的证据。

观察面板的[最近决定摘要](../game/Host/TownResidentDecisionSummary.cs)从现有事件和当前会话的 NPC 完成记录关联请求、候选与上述世界事实。`invite` 或 `propose_exchange` 执行分别只表示发出邀请或报价；资源交付以 `resource_transferred` 记录为准。

## NPC 修改索引

| 目标 | 修改类型与文件 | 公共检查入口 |
| --- | --- | --- |
| 改人设、需求和初态资产 | **配置修改**：[town.json](../game/Content/town.json) 的 `personality`、`desiredResourceId`、`inventory`、`coins`。[counterfactual.json](../game/Content/counterfactual.json) 仅按居民 ID 替换库存。保留稳定 ID，编辑后启动新运行；人设不覆盖当前资产事实。 | 内容验证；检查新记录中的冻结输入、初态和内容指纹。 |
| 改世界日程或决策节奏 | **配置修改**：`town.json` 的 `schedule`，时段须连续覆盖 0–1440，目标地点存在且可达。机会生成、轮转、回应轮次与冷却在 `TownWorld.GetDecisionRequests` 及其调度逻辑中，改变这些行为属于**代码合同修改**。 | 内容验证；调度变化再运行 Core/Host 测试及[完整离线门](FULL_DEMO_RUN.md#固定客户端验证)。世界时间与现实等待分别遵循[时钟合同](SAVE_REPLAY_CONTRACT.md#逻辑待办与真实传输)。 |
| 增加动作或改变字段规则 | **代码合同修改**：[TownContracts.ActionCandidate](../src/CozyTown.Core/Town/TownContracts.cs)、`TownWorld` 的候选范围、字段校验和执行分派，以及 `TownModelProtocol` 的请求与解析。只改提示词不会增加世界能力；新增持久状态还须扩展快照与回放。 | `TownNpcChoiceContractTests` 检查冻结约束与保存回放；通过正常世界入口验证受理、拒绝和执行结果，再走完整离线门。 |
| 改模型配置或接入服务 | **配置修改**：`TownModelRuntimeSettings` 只提供凭据/账本路径、预期指纹及已登记预算 profile，用途由显式运行参数决定。更换服务、型号、认证、终态、用量或价格预留属于**代码合同修改**，入口为 `DeepSeekClient`、NPC 的 `TownModelProtocol`、玩家的 [TownPlayerDialogueProtocol](../game/Host/TownPlayerDialogueProtocol.cs)。 | Model/Host 离线测试与 Launcher 的两种 `--check-only`。当前服务路径见模型合同；预检通过不证明真实请求成功，真实运行的样本与消耗另行记录。 |
| 观察运行、检查存档与记录 | 日常操作见[切片操作](PLAYABLE_SLICE.md)：Tab 或点击居民打开观察手册，F5/F9 保存/读取，面板可导出记录。若改变观察字段、保存状态或记录边界，属于**代码合同修改**，入口为 `TownWorld.Observe`、`TownSnapshot`、`TownGame` 和 `TownRunJournal`。 | 用导出 ZIP 执行核心与场景回放；保存、续接和分叉按保存合同检查。UI 和身体表现仍需实际运行验证。 |

观察手册是开发者的全局视角。模型持续使用该居民自己的冻结局部观察；面板能看到另一居民的资产或经历，不表示模型也获得这些信息。

## 保存、记录与验证入口

Core 捕获逻辑状态，Game 封套另外保留宿主路线、未消费时间、冻结请求和运行关联。恢复先在隔离候选中校验，提交后建立新分支；真实预算和玩家生成的跨分支终结记录独立持久保留，读取旧档不退还现实消耗，也不导入保存后的文本或经历。文件发布、失败处理和恢复顺序以[保存合同](SAVE_REPLAY_CONTRACT.md)为准。

以下命令从仓库根目录执行，路径参数使用实际绝对路径。内容验证只加载和校验内容，不推进世界或请求模型：

```powershell
<godot-console> --headless --path game -- --validate-content --scenario=baseline --report=<absolute-report.json> --data-dir=<new-absolute-data-directory>
dotnet test tests/CozyTown.Host.Tests/CozyTown.Host.Tests.csproj -c Release --filter FullyQualifiedName~TownNpcChoiceContractTests
dotnet test tests/CozyTown.Model.Tests/CozyTown.Model.Tests.csproj -c Release
dotnet run --project tools/CozyTown.Replay -- --input=<export.zip> --report=<absolute-core-report.json>
<godot-console> --headless --path game -- --scene-replay=<export.zip> --report=<absolute-scene-report.json>
```

内容验证覆盖的其他初态和 Windows 自动检查由 [verify.ps1](../tools/windows/verify.ps1) 编排；冻结候选的完整检查见[完整 Demo 运行说明](FULL_DEMO_RUN.md#固定客户端验证)。核心回放只依赖包内内容、状态、输入与空间事实；场景回放还要求匹配的引擎、场景资源、查询配置和模拟源码摘要。两类回放都在首个分歧停止，均不请求真实模型或写现实预算。
