# CozyTown Godot

CozyTown 的独立 Godot 重构项目，面向 Windows/PC，保留小镇生活玩法与四人 AI Agent 实验平台目标。

技术方向已确认采用 Godot .NET 与独立 C# 核心。首版使用自己的存档，不要求读取 Unity 旧档。原 [CozyTown](https://github.com/qinjunw/CozyTown) 继续独立开发，供本项目参考行为、测试和素材；两个仓库不自动同步，也不互相作为构建依赖。

当前默认入口为四人小镇切片：玩家移动、观察和交谈，居民独立决定邀请、回应、赴约及资源交换，支持日程、暂停、完整逻辑存档和运行记录导出。真实模型通过独立 C# 适配器接入；默认启动采用规则模式。操作与配置见[切片使用说明](docs/PLAYABLE_SLICE.md)，实际通过项及待验收项见[验证记录](docs/verification/four-resident-slice.md)。

玩家现可查看钱包、背包与五格快捷栏，并在杂货铺买卖物品；双方资产及操作去重随存档恢复。初态、报价和验证入口见[玩家资产与商店操作](docs/PLAYER_ECONOMY.md)。六块菜地支持三种作物的种植、浇水和收获，每日 05:00 结算成长与商店补给；玩家可在家选择休息 1～12 小时，见[种植与睡眠操作](docs/PLAYER_FARMING.md)。

鸡舍支持喂食、清晨产蛋和领取，池塘提供三种鱼与空钩结果，厨房提供五份配方。产物可直接出售或加工后出售；材料、待领产物及钓鱼随机状态随存档恢复，见[养鸡、钓鱼与烹饪](docs/PLAYER_PRODUCTION.md)。自动与 Windows 操作结果见[生产验证记录](docs/verification/player-production.md)，待用户操作的项目见[人工验收清单](docs/verification/player-production-manual-checklist.md)。

居民交谈面板可显式生成一段反馈，支持对话暂停、关闭或切人终结，以及等待和已显示文本的保存恢复。固定客户端与回放检查已通过，用户已于 2026-10-11 通过[Windows 对话清单](docs/verification/player-dialogue-manual.md)验收。完整 Demo v2 的两组真实指标、8 段正确玩家文本及四包 12 次回放均通过，见[第二轮真实评测](docs/verification/full-demo-real-v2-2026-10-11.md)；[v1 未达标结果](docs/verification/full-demo-real-2026-10-11.md)及失败样本保持原归属。最终 Windows 与 CI 制品交付仍待验收。行为与自动证据见[玩家对话合同](docs/PLAYER_DIALOGUE_CONTRACT.md)及[验证记录](docs/verification/player-dialogue.md)。

## 构建与运行

需要 Windows x64、Git 和 PowerShell 7。在仓库根目录运行：

```powershell
pwsh -File tools/windows/install-toolchain.ps1
pwsh -File tools/windows/verify.ps1 -Graphical
```

工具固定为 Godot 4.7.2 .NET 与 .NET SDK 10.0.401，安装到忽略的 `.local/tools/`。验证结果和可运行程序保存在 `artifacts/verification/<run-id>/`；启动其中的 `windows/CozyTown.exe`，并保留整个导出目录。操作、锁文件和证明范围见 [Windows 构建与验证](docs/WINDOWS_TOOLCHAIN.md)，已执行结果见 [工具链验证记录](docs/verification/windows-toolchain-2026-09-19.md)。

## 产品目标

- 玩家能够移动、交互、种植、养鸡、钓鱼、烹饪、交易，并完成生产与再投入的闭环。
- Mina、Eli、Ren、Sora 在同一世界按个人状态与日程自主活动，通过受约束决策进行交谈、赴约和资源协作。
- 世界与居民可以保存并继续运行；旧请求不能污染恢复后的世界，资源不能重复交付。
- 开发者能够观察行为原因、导出实验记录，并区分固定替身、记录回放和真实模型实验。

完整范围见 [产品目标与验收基线](docs/PRODUCT_BASELINE.md)，领域词汇见 [CONTEXT.md](CONTEXT.md)。

首份可玩交付的[范围与验收](docs/FIRST_PLAYABLE_SLICE.md)聚焦四人自主互动，世界日程优先，并要求对外演示画面完成度。种植、养鸡、钓鱼、烹饪与交易已接入同一玩家资产及晨间结算。

内容采用 Godot 场景与独立 JSON 分工编辑，固定身份连接地图、居民和日程；提取器与联合校验执行[内容来源与一致性合同](docs/CONTENT_AUTHORING.md)。

保存恢复采用完整逻辑快照，不兼容时保护当前世界并拒绝读取；真实请求续接受独立预算账本约束。状态、失败处理与回放输入见[存档与回放合同](docs/SAVE_REPLAY_CONTRACT.md)。[离线核心回放 CLI](docs/REPLAY.md)重算规则、预算与父分支前缀，逐边界核对完整状态；[Godot 场景回放](docs/SCENE_REPLAY.md)在源场景和 Windows 导出中重新执行碰撞查询。固定包绑定其源码摘要，修改模拟源码后须重新生成并验证。

真实模型采用 DeepSeek `deepseek-flash`，由 C# 直接接入；首轮实验的 48 次发送已用尽。参数、费用预留、恢复补发和失败验收见[真实模型接入合同](docs/REAL_MODEL_CONTRACT.md)。完整 Demo v1 的[未达标结果和全部失败样本](docs/verification/full-demo-real-2026-10-11.md)保留，旧账本已停止且余量未转移。独立 `full-demo-v2` 在冻结候选通过完整离线门后完成两组真实评测，71 次预算尝试、70 次 HTTP 共保守扣减 CNY 0.290836；`manual` 八次未使用，留给用户最终 Windows 试玩。指标、失败、回放及交付限制见[第二轮报告](docs/verification/full-demo-real-v2-2026-10-11.md)。登记检查见[第二轮预算验证](docs/verification/full-demo-budget-v2-2026-10-11.md)，执行入口见[完整 Demo 运行说明](docs/FULL_DEMO_RUN.md)，阶段和通过标准见[跨日评测合同](docs/FULL_DEMO_EVALUATION_CONTRACT.md)。真实服务验证与离线测试分开统计。

## 迭代入口

GitHub Issues 保存路线地图、原生子任务、领取与阻塞关系。入口见 [Wayfinder](docs/wayfinder/README.md)，操作见 [GitHub tracker](docs/wayfinder/github-tracker.md)。

后续玩家生活闭环、模型对话、两类回放及最终 Windows 交付的职责与证据要求见[完整 Demo 验收映射](docs/DEMO_ACCEPTANCE_MAP.md)。新增状态的存档、输入记录和失败验收随对应功能交付。

美术可以沿用或重构。采用参考项目的代码、测试或素材时，应记录来源版本，并在本仓库独立验证；不复制 Unity 缓存、机器配置、凭据或旧项目的实时任务状态。
