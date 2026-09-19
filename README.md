# CozyTown Godot

CozyTown 的独立 Godot 重构项目，面向 Windows/PC，保留小镇生活玩法与四人 AI Agent 实验平台目标。

技术方向已确认采用 Godot .NET 与独立 C# 核心。首版使用自己的存档，不要求读取 Unity 旧档。原 [CozyTown](https://github.com/qinjunw/CozyTown) 继续独立开发，供本项目参考行为、测试和素材；两个仓库不自动同步，也不互相作为构建依赖。

当前包含可构建的 Godot .NET 诊断场景、独立 C# 核心探针与 Windows 验证脚本。诊断场景检查按钮调用、JSON 写盘恢复及继续运行；完整小镇玩法与四人 Agent Demo 尚待实现。

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

下一份可玩交付的[范围与验收](docs/FIRST_PLAYABLE_SLICE.md)聚焦四人自主互动：独立规则与真实模型共同决策，世界日程优先，首切片画面达到可对外演示的完成度。该范围已确定，场景与居民功能仍待实现。

## 迭代入口

GitHub Issues 保存路线地图、原生子任务、领取与阻塞关系。入口见 [Wayfinder](docs/wayfinder/README.md)，操作见 [GitHub tracker](docs/wayfinder/github-tracker.md)。

美术可以沿用或重构。采用参考项目的代码、测试或素材时，应记录来源版本，并在本仓库独立验证；不复制 Unity 缓存、机器配置、凭据或旧项目的实时任务状态。
