# CozyTown Godot

CozyTown 的独立 Godot 重构项目，面向 Windows/PC，保留小镇生活玩法与四人 AI Agent 实验平台目标。

技术方向已确认采用 Godot .NET 与独立 C# 核心。首版使用自己的存档，不要求读取 Unity 旧档。原 [CozyTown](https://github.com/qinjunw/CozyTown) 继续独立开发，供本项目参考行为、测试和素材；两个仓库不自动同步，也不互相作为构建依赖。

当前仓库处于路线建图与技术研究阶段，尚未包含可运行的 Godot Demo。引擎版本、世界适配边界、内容组织和交付流程通过 GitHub 决策票据确定，不能将计划中的能力当作已实现功能。

## 产品目标

- 玩家能够移动、交互、种植、养鸡、钓鱼、烹饪、交易，并完成生产与再投入的闭环。
- Mina、Eli、Ren、Sora 在同一世界按个人状态与日程自主活动，通过受约束决策进行交谈、赴约和资源协作。
- 世界与居民可以保存并继续运行；旧请求不能污染恢复后的世界，资源不能重复交付。
- 开发者能够观察行为原因、导出实验记录，并区分固定替身、记录回放和真实模型实验。

完整范围见 [产品目标与验收基线](docs/PRODUCT_BASELINE.md)，领域词汇见 [CONTEXT.md](CONTEXT.md)。

## 迭代入口

GitHub Issues 保存路线地图、原生子任务、领取与阻塞关系。入口见 [Wayfinder](docs/wayfinder/README.md)，操作见 [GitHub tracker](docs/wayfinder/github-tracker.md)。

美术可以沿用或重构。采用参考项目的代码、测试或素材时，应记录来源版本，并在本仓库独立验证；不复制 Unity 缓存、机器配置、凭据或旧项目的实时任务状态。
Independent Godot reconstruction of the CozyTown playable town and four-resident AI Agent experiment demo.
