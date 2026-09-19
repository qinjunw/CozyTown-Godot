# Godot .NET 的 Windows 构建、测试与交付链

核验日期：2026-09-19。研究票据：[核验 Godot .NET 的 Windows 构建、测试与交付链](https://github.com/qinjunw/CozyTown-Godot/issues/2)。输入为 [产品基线](../PRODUCT_BASELINE.md)、[领域语言](../../CONTEXT.md)、官方发布与固定版本源码、Microsoft 和测试工具维护者文档。

本项目已确认 Windows/PC 优先、Godot .NET、独立 C# 核心及新存档。本文提供工具链建议与构建验证判据；没有安装工具、创建游戏工程、导出游戏、运行 CI 或调用真实模型。

## 结论与建议

**Godot 4.7.2-stable .NET x86_64 与同版 .NET 导出模板的发行已经核验；SDK 和目标框架尚未锁定。** 建议在后继 [验证 Windows Godot .NET 的最小构建与导出](https://github.com/qinjunw/CozyTown-Godot/issues/9) 中，优先检查 SDK 10.0.401 / `net10.0` 核心、宿主和测试，并以 SDK 8.0.425 / `net8.0` 作官方默认对照。两者都是待实测组合，不能把文档支持写成项目已通过构建。[Godot 发行](https://github.com/godotengine/godot-builds/releases/tag/4.7.2-stable)、[项目生成器](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/editor/GodotTools/GodotTools.ProjectEditor/ProjectGenerator.cs)。

优先验证 .NET 10 的原因是支持周期：Microsoft 当前列出 .NET 8 于 2026-11-10 结束支持，.NET 10 LTS 于 2028-11-14 结束支持。Godot 4.7.2 默认仍为 `net8.0`，因此 `net10.0` 属于工程建议，需验证编辑器加载、场景执行和导出。若正式采用 .NET 8，应明确升级时点。[.NET 支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。

## 已核验：发行与框架

| 项目 | 一手证据与结果 | 对本项目的含义 |
| --- | --- | --- |
| Godot 稳定版 | Windows 下载页与官方 `godot-builds` 的 `4.7.2-stable` release API 均指向 2026-08-18；API 的 `prerelease=false` | 固定 tag，不跟随通用 `latest` |
| .NET 编辑器 | release 列出 `Godot_v4.7.2-stable_mono_win64.zip`，116,582,061 字节 | 文件名仍用 `mono`，选择支持 C# 的变体 |
| .NET 模板 | release 列出 `Godot_v4.7.2-stable_mono_export_templates.tpz`，1,202,598,411 字节 | 编辑器和模板一起固定版本 |
| Godot MSBuild SDK | NuGet 官方 feed 存在 `Godot.NET.Sdk` 版本 `4.7.2` | 宿主项目可固定 `Godot.NET.Sdk/4.7.2` |
| .NET SDK | Microsoft 发行元数据列出 10.0.401 与 8.0.425，均发布于 2026-09-08 | SDK 版本不同于 Runtime 版本和项目 TFM |
| 默认目标框架 | 4.7.2 `ProjectGenerator` 与 `GodotSharp.csproj` 为 `net8.0`；Android 条件为 `net9.0` | Windows 首版不需要 Android workload |
| 编辑器宿主 | 4.7.2 `GodotPlugins.csproj` 为 `net8.0`，设置 `RollForward=LatestMajor` | 源码允许选择较新的已安装 Runtime；这仍不是本项目 net10 实测证据 |

来源：[Windows 下载页](https://godotengine.org/download/windows/)、[固定 release API](https://api.github.com/repos/godotengine/godot-builds/releases/tags/4.7.2-stable)、[Godot.NET.Sdk feed](https://api.nuget.org/v3-flatcontainer/godot.net.sdk/index.json)、[.NET 10 元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)、[.NET 8 元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json)、[GodotSharp 项目](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/glue/GodotSharp/GodotSharp/GodotSharp.csproj)、[GodotPlugins 项目](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/glue/GodotSharp/GodotPlugins/GodotPlugins.csproj)。

release API 返回的 SHA-256：

| 资产 | API 中的摘要 |
| --- | --- |
| `Godot_v4.7.2-stable_mono_win64.zip` | `a2a48473a7414c5f19fab690518caebb738c09ef9601f6bd2388676a7f53b3c0` |
| `Godot_v4.7.2-stable_mono_export_templates.tpz` | `92f8681e349ef1f90891b792da95e3b2b0bd1ed610b78018c58feb2d87e15a9d` |

本次没有下载资产，未把这些摘要与本机文件重新计算比对。后继证据应保存实际下载来源、文件摘要和 `godot --version` 输出。

### 来源差异与处理

- `godot-builds/releases/latest` 本次跳转到 **3.6.3-stable**。它属于另一维护线，不能作为 Godot 4 的自动选版依据；4.7.2 已用具体 tag 和 API 交叉确认。[latest 入口](https://github.com/godotengine/godot-builds/releases/latest)。
- `stable` 与 `4.7` C# 文档仍用 Godot 4.5 需要 .NET 8 或更新版本的措辞。本文用 4.7.2 的项目生成器、GodotSharp 和宿主源码补充核实，没有仅凭旧措辞推断新版本。[4.7 C# 基础](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_basics.html)。
- release HTML 的资产区域出现动态加载错误，浏览工具点击下载链接也返回内部错误；官方 API 可读，给出文件、大小、摘要与下载 URL。它证明资产列入发行，不证明本次已经下载或运行。
- `gdUnit4Net` 的维护者 README 当前支持徽章仍列 Godot 4.3/4.4、.NET 8/9，不能据此宣称已覆盖 4.7.2 + .NET 10。[维护者仓库](https://github.com/godot-gdunit-labs/gdUnit4Net)。

## 已核验：本机条件

| 只读检查 | 结果 |
| --- | --- |
| PATH 上的 `dotnet --info` | x64 Host 9.0.5；有 `Microsoft.NETCore.App 9.0.5`，没有可发现的 SDK |
| Unity 安装附带的 `dotnet --list-sdks` | 可读到 SDK 8.0.318；不在常规 PATH SDK 搜索结果中 |
| `Get-Command godot,Godot` | 没有找到命令 |
| Downloads 中 Godot 命名文件 | 定向检查没有找到；未全盘搜索，不能断言任何位置都没有 Godot |
| 新仓库检出 | 只有规则、产品、领域与路线资料，没有可执行 Godot 项目或测试工程 |

建议使用独立 SDK 与 Godot 路径，避免将 Unity 安装目录变成新项目构建依赖。本机 SDK 8.0.318 的存在只说明还有一个编译工具入口，不代表候选组合已经就绪。机器路径保存在忽略的本地配置，仓库保留版本声明和逻辑变量。

## 独立核心与项目文件建议

核心采用普通 `Microsoft.NET.Sdk` 类库，Godot 宿主通过 `ProjectReference` 引用；两者的候选 TFM 一起接受后继验证。核心不引用 `GodotSharp` 或暴露节点类型，时间、模型客户端和记录输出使用少量显式接口。此结构落实独立核心要求，不需要双引擎兼容层。

`netstandard2.1` 是可选的 API 兼容目标，不是独立核心的必要条件。Microsoft 列出 .NET 8/9/10 均实现 .NET Standard 2.1；本仓库无需向旧 Unity 共享程序集，因此不必沿用旧核心的 TFM。测试工程需要可执行的 .NET TFM，不能只以 .NET Standard 运行。[目标框架](https://learn.microsoft.com/en-us/dotnet/standard/frameworks)、[NUnit .NET 测试说明](https://docs.nunit.org/articles/nunit/getting-started/dotnet-core-and-dotnet-standard.html)。

建议提交 `project.godot`、宿主 `.csproj` 与 `.sln`、核心/测试项目、共享版本声明、`global.json`、包锁文件、`export_presets.cfg` 和资源导入设置。忽略 `.godot/`、`bin/`、`obj/`、导出产物与凭据。Godot C# 文档要求保留项目和解决方案文件；4.7.2 导出插件也检查解决方案存在。[工程工作流](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_basics.html#project-setup-and-workflow)、[导出插件](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/editor/GodotTools/GodotTools/Export/ExportPlugin.cs)。

实际验证后，建议在 `global.json` 固定完整 SDK 版本，设置 `allowPrerelease=false`；严格复现可用 `rollForward=disable`。本地与 CI 同步更新。SDK 选择与 `TargetFramework` 是独立配置。[Microsoft global.json](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)。

## 测试：原生能力、第三方和项目代码

| 层级 | 已核验能力 | 建议与需要实现的部分 |
| --- | --- | --- |
| 纯 C# 核心 | NUnit 通过适配器与 `Microsoft.NET.Test.Sdk` 接入 `dotnet test` | 使用固定客户端测试规则、快照、预算、迟到响应与回放；固定稳定包版本，不引用 Unity 缓存的 NUnit |
| 导入和构建 | CLI 提供 `--import`、`--build-solutions`、`--headless` | 分别检查资源导入、C# 编译及日志；导入成功不等于场景测试通过 |
| Godot 场景 | CLI 可启动指定 `.tscn`，`SceneTree.Quit(exitCode)` 可设置退出码 | 首轮用小型 C# 检验场景等待必要帧、断言行为、写结果后退出；项目需要定义超时、错误与零用例判定 |
| 场景框架 | `gdUnit4Net` 提供场景 runner、断言与 VSTest 适配 | 是第三方可选依赖；采用前验证具体引擎/TFM 组合，不作为首次导出的前提 |
| 画面与交互 | headless 使用无窗口显示驱动和 Dummy 音频 | 另做有图形界面的 Windows 验证；headless 不能证明像素、字体、鼠标命中或音频正常 |

来源：[NUnit CLI](https://docs.nunit.org/articles/nunit/getting-started/dotnet-core-and-dotnet-standard.html)、[4.7 CLI](https://docs.godotengine.org/en/4.7/tutorials/editor/command_line_tutorial.html)、[SceneTree.quit](https://docs.godotengine.org/en/stable/classes/class_scenetree.html#class-scenetree-method-quit)、[gdUnit4Net](https://github.com/godot-gdunit-labs/gdUnit4Net)。

Godot `--test` 是引擎以 `tests=yes` 编译后使用的引擎测试入口，不能直接代替游戏 C# 测试发现器。`--check-only` 配合 `--script` 检查脚本解析，不能替代宿主编译和玩法断言。[CLI 工具说明](https://docs.godotengine.org/en/4.7/tutorials/editor/command_line_tutorial.html#command-line-reference)。

## 后继最小构建验证

以下是建议入口，**本次未执行**。路径、场景和预设是待实现示例；实施时须建立相应工程、结果协议、退出码与超时检查。包锁文件应先生成并提交，输出目录应先创建。

```powershell
dotnet --info
godot --version
dotnet restore CozyTown.sln --locked-mode
dotnet test tests/CozyTown.Core.Tests/CozyTown.Core.Tests.csproj --configuration Release --no-restore --logger trx
godot --headless --path game --import
dotnet build game/CozyTown.Game.csproj --configuration Debug --no-restore
godot --headless --path game res://Tests/ToolchainSmoke.tscn
godot --headless --path game --export-release "Windows Desktop" ../artifacts/windows/CozyTown.exe
```

宿主配置是 `Debug`、`ExportDebug`、`ExportRelease`；普通类库可用 `Release`。示例让导出器选择发布配置，不把普通 `Release` 编译视为完成导出。[4.7.2 SDK 配置](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/Sdk.props)。

CLI 导出需要编辑器二进制、同版 .NET 模板和 `export_presets.cfg` 中同名预设。输出相对路径以 `project.godot` 所在目录为基准；自动化脚本宜解析为绝对输出路径。[CLI 导出](https://docs.godotengine.org/en/4.7/tutorials/editor/command_line_tutorial.html#exporting)。

建议验收五类证据：

1. 干净检出通过 restore、核心测试、导入和宿主编译；记录提交 SHA、工具/TFM/包版本与测试数量。
2. C# 检验场景调用独立核心、观察物理帧、检查节点生命周期与一次保存/恢复；失败非零退出，超时或缺完成标记均失败。
3. Windows `ExportRelease` 在独立目录启动并完成同一固定客户端检查；保存文件清单和摘要。
4. 在没有开发 SDK 的 Windows 环境运行完整包，验证随包 Runtime 与数据完整。4.7.2 构建源码调用 `dotnet publish --self-contained true`，本次没有验证最终包。[BuildSystem](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/editor/GodotTools/GodotTools/Build/BuildSystem.cs)。
5. 有图形界面的候选包验证中文、像素缩放、输入/失焦、碰撞与退出；记录机器、窗口尺寸和结果。渲染器及最低硬件由相关决策票据确定。

## Windows 交付与 CI 建议

首版建议使用 Windows x86_64 runner。GitHub 官方提供 `setup-dotnet`、restore/build/test 与结果上传方式；Godot 获取、模板准备和场景断言由项目脚本组合，不必先引入第三方 Godot Action。[GitHub .NET CI](https://docs.github.com/en/actions/tutorials/build-and-test-code/net)。

建议流水线顺序：固定提交与工具摘要；锁定依赖还原；核心测试；headless 导入与宿主构建；固定场景检查；Windows 导出与启动检查。无论成败保存 TRX、Godot 日志和场景结果；成功时保存完整目录 ZIP、清单和 SHA-256。自动测试默认没有真实模型凭据，保留时长和发布权限由实施配置明确。

Godot .NET 导出插件打包 `dotnet publish` 产物；不嵌入时生成 `data_<project>_<platform>_<arch>` 目录。因此应压缩完整实际导出目录，不能仅上传 `.exe` 或 `.pck`。`--export-pack` 只输出资源包，不证明 Windows 包可运行。[4.7.2 导出实现](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/editor/GodotTools/GodotTools/Export/ExportPlugin.cs)、[导出项目说明](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html)。

`export_presets.cfg` 可跟踪，`.godot/export_credentials.cfg` 不提交。用于回放的 JSON 等非资源文件需显式纳入导出过滤，避免编辑器可读而发布包缺失。[导出配置与资源过滤](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html#configuration-files)。

Windows 签名需要工具和证书，是发布选择，不是 headless 构建必需步骤。本研究不替用户决定安装器、证书、商店发布或 ARM64 支持；首次建议目标是可验证的 Windows x86_64 目录包。[Windows 导出](https://docs.godotengine.org/en/4.7/tutorials/export/exporting_for_windows.html)。

## 未验证与决策边界

- 工具链实测和 SDK/TFM 锁定交给 [验证 Windows Godot .NET 的最小构建与导出](https://github.com/qinjunw/CozyTown-Godot/issues/9)，本轮不执行。
- 新核心没有兼容 Unity 的义务；无需仅为旧工程一致性继续 `netstandard2.1`。
- 场景测试框架、渲染器/最低硬件、正式交付形式、签名和 CI 发布策略尚未由本研究决定。
- 本文没有本项目构建、测试、导出、CI 或玩家机器运行成功记录；文档与源码能力不替代这些验收。
