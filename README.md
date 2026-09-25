# CozyTown Godot

CozyTown 的独立 Godot 重构项目，面向 Windows/PC，保留小镇生活玩法与四人 AI Agent 实验平台目标。

技术方向已确认采用 Godot .NET 与独立 C# 核心。首版使用自己的存档，不要求读取 Unity 旧档。原 [CozyTown](https://github.com/qinjunw/CozyTown) 继续独立开发，供本项目参考行为、测试和素材；两个仓库不自动同步，也不互相作为构建依赖。

当前默认入口为四人小镇切片：玩家移动、观察和交谈，居民独立决定邀请、回应、赴约及资源交换，支持日程、暂停、完整逻辑存档和运行记录导出。真实模型通过独立 C# 适配器接入；默认启动采用规则模式。操作与配置见[切片使用说明](docs/PLAYABLE_SLICE.md)，实际通过项及待验收项见[验证记录](docs/verification/four-resident-slice.md)。

玩家现可查看钱包、背包与五格快捷栏，并在杂货铺买卖物品；双方资产及操作去重随存档恢复。初态、报价和验证入口见[玩家资产与商店操作](docs/PLAYER_ECONOMY.md)。六块菜地支持三种作物的种植、浇水和收获，每日 05:00 结算成长与商店补给；玩家可在家选择休息 1～12 小时，见[种植与睡眠操作](docs/PLAYER_FARMING.md)。

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

首份可玩交付的[范围与验收](docs/FIRST_PLAYABLE_SLICE.md)聚焦四人自主互动，世界日程优先，并要求对外演示画面完成度。种植与交易已贯通，养鸡、钓鱼和烹饪由后续票据交付。

内容采用 Godot 场景与独立 JSON 分工编辑，固定身份连接地图、居民和日程；提取器与联合校验执行[内容来源与一致性合同](docs/CONTENT_AUTHORING.md)。

保存恢复采用完整逻辑快照，不兼容时保护当前世界并拒绝读取；真实请求续接受独立预算账本约束。状态、失败处理与回放输入见[存档与回放合同](docs/SAVE_REPLAY_CONTRACT.md)；完整回放执行器尚未交付。

真实模型采用 DeepSeek `deepseek-flash`，由 C# 直接接入；首轮实验共用人民币 5 元、48 次发送上限。参数、费用预留、恢复补发和失败验收见[真实模型接入合同](docs/REAL_MODEL_CONTRACT.md)。真实服务验证与离线测试分开统计。

## 迭代入口

GitHub Issues 保存路线地图、原生子任务、领取与阻塞关系。入口见 [Wayfinder](docs/wayfinder/README.md)，操作见 [GitHub tracker](docs/wayfinder/github-tracker.md)。

后续玩家生活闭环、模型对话、两类回放及最终 Windows 交付的职责与证据要求见[完整 Demo 验收映射](docs/DEMO_ACCEPTANCE_MAP.md)。新增状态的存档、输入记录和失败验收随对应功能交付。

美术可以沿用或重构。采用参考项目的代码、测试或素材时，应记录来源版本，并在本仓库独立验证；不复制 Unity 缓存、机器配置、凭据或旧项目的实时任务状态。
