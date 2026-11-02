# CozyTown Godot

Windows/PC 小镇生活与四居民 AI Agent 实验 Demo，使用 Godot .NET 和独立 C# 核心。

玩家可以种植、养鸡、钓鱼、烹饪和交易；Mina、Eli、Ren、Sora 按各自日程、状态和局部观察决定互动。模型提交行动候选，世界规则检查后执行；对话文本不直接创建物品或资金。

## 下载 Windows 候选与试玩

main 成功的 **Windows toolchain** CI 提供 Windows x64 候选 Artifact，保留 14 天，需要登录 GitHub 下载。按 [下载与校验指引](docs/WINDOWS_DELIVERY.md)取指定运行的候选，先解压 Artifact 外层 ZIP，再校验并解压内层程序 ZIP；无需安装 Godot 或 .NET SDK。

双击程序目录的 `Start-Playtest.cmd`，以独立数据目录运行离线规则与固定客户端；按 [人工试玩清单](docs/PLAYTEST.md)完成 alpha 最低必测并记录扩展回归的通过、失败和未测项。保存后重开用 F9 读取该候选进度。公开版本以 [Releases](https://github.com/qinjunw/CozyTown-Godot/releases)中实际已发布的附件为准；维护者草稿在人工验收和发布确认后公开。

## 从源码运行

本地构建需要 Windows x64、Git、PowerShell 7，以及下载 Godot、.NET SDK 和 NuGet 包的网络连接。在 PowerShell 中运行：

```powershell
git clone https://github.com/qinjunw/CozyTown-Godot.git
cd CozyTown-Godot
pwsh -File tools/windows/install-toolchain.ps1
pwsh -File tools/windows/verify.ps1
```

工具链固定为 Godot 4.7.2 .NET 和 .NET SDK 10.0.401，版本与摘要见 [toolchain.lock.json](tools/windows/toolchain.lock.json)。安装目录为忽略的 `.local/tools/`。

验证结果位于本次 `artifacts/verification/<run-id>/`。打开其中的 `windows/CozyTown.exe` 进入小镇，并保留整个 `windows/` 目录；导出包包含所需的 .NET 运行时。构建参数与检查范围见 [Windows 构建与验证](docs/WINDOWS_TOOLCHAIN.md)。

普通启动使用离线规则，玩家生成反馈使用固定客户端示例。基本操作包括 WASD／方向键移动、E 交互、B 查看背包、Tab 观察居民、Esc 打开菜单、F5／F9 保存与读取；完整说明见 [小镇操作](docs/PLAYABLE_SLICE.md)。

## 功能与接口

| 范围 | 已有能力 | 文档 |
| --- | --- | --- |
| 玩家生活 | 钱包、背包、五格快捷栏、商店买卖、种植与晨间结算、睡眠、养鸡、钓鱼、烹饪 | [经济](docs/PLAYER_ECONOMY.md)、[种植与睡眠](docs/PLAYER_FARMING.md)、[生产](docs/PLAYER_PRODUCTION.md) |
| 居民与模型 | 四居民日程、邀请、赴约、交谈与资源交换；可选真实模型行动与玩家反馈 | [玩家对话](docs/PLAYER_DIALOGUE.md)、[模型接口](src/CozyTown.Model/README.md) |
| 保存与实验 | 逻辑快照、恢复分支、运行记录导出、预算与请求去重 | [保存合同](docs/SAVE_REPLAY_CONTRACT.md)、[实验运行](docs/FULL_DEMO_RUN.md) |
| 离线回放 | 核心按记录重算；Godot 重新执行空间查询；首个分歧停止 | [核心回放](docs/REPLAY.md)、[场景回放](docs/SCENE_REPLAY.md) |

真实模型运行需要仓库外凭据、显式配置、已登记的预算配置和现有预算账本。配置、候选绑定与只读预检见 [Windows 试玩入口](docs/REAL_PLAY_LAUNCHER.md)。新克隆不包含已配置的双击入口或账本；配置模板不能直接用于真实运行。存档恢复不返还已发生的调用消耗，凭据、实际账本和本机配置保存在 Git 之外。

## 开发与验证

Core 管理世界规则和逻辑状态，Model 管理 HTTP 传输与持久预算，Godot Host 管理场景、输入、UI 和保存发布。模块接口与 NPC 修改入口见 [架构索引](docs/ARCHITECTURE.md)，内容编辑见 [内容合同](docs/CONTENT_AUTHORING.md)，术语见 [CONTEXT.md](CONTEXT.md)。

普通构建验证使用上面的 `verify.ps1`；`-Graphical` 另运行图形检查并捕获截图。包含独立审查收据、固定输入和两类回放的正式完整验证见 [完整 Demo 运行说明](docs/FULL_DEMO_RUN.md#固定客户端验证)。这些离线检查不发送真实模型请求。

自动检查、真实模型实验和人工验收按候选版本分别记录。历史结果与验收范围见 [验收映射](docs/DEMO_ACCEPTANCE_MAP.md)，包括 [v1 未达标结果及失败样本](docs/verification/full-demo-real-2026-10-11.md)和 [v2 评测结果](docs/verification/full-demo-real-v2-2026-10-11.md)。旧候选结果不表示当前源码已通过同样的真实评测或人工操作；核心测试通过也不证明画面、键鼠操作或跨设备运行已经验收。

产品范围见 [产品基线](docs/PRODUCT_BASELINE.md)，任务与依赖见 [GitHub Issues](https://github.com/qinjunw/CozyTown-Godot/issues)，维护流程见 [GitHub tracker](docs/wayfinder/github-tracker.md)。

## 来源与许可

项目参考了 CozyTown Unity 版本的行为、测试和素材，使用独立工程与存档格式。Unity 参考仓库需要访问权限；公开运行不依赖该仓库。确定的参考版本见 [产品基线](docs/PRODUCT_BASELINE.md#参考基线与来源)，素材版本、逐文件哈希与图集规格见 [美术来源](docs/art/SOURCES.md)。

本仓库尚未提供项目许可证。素材的来源记录与许可状态见 [美术来源](docs/art/SOURCES.md)；公开可见性不替代项目或素材的许可声明。
