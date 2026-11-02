# Windows 构建与验证

需要 Windows x64、Git、PowerShell 7（`pwsh`），以及访问 GitHub Releases、Microsoft 下载站和 nuget.org 的网络连接。

工具链固定为 Godot `4.7.2-stable` .NET、同版 .NET 导出模板和 .NET SDK `10.0.401`。核心、测试与宿主均使用 `net10.0`。引擎和 SDK 的下载地址及摘要保存在 [toolchain.lock.json](../tools/windows/toolchain.lock.json)，SDK 选择由 [global.json](../global.json) 约束。Unity 安装不参与构建。

## 从检出到导出

在仓库根目录运行：

```powershell
pwsh -File tools/windows/install-toolchain.ps1
pwsh -File tools/windows/verify.ps1 -Graphical
```

安装脚本下载约 1.6 GB 的官方压缩包，先核对摘要再解压到忽略的 `.local/tools/`。Godot 使用便携模式；模板、编辑器设置和 NuGet 缓存也保存在此目录。已有压缩包会再次校验，已完成的安装可以复用。安装器不会将工具写入系统安装目录或修改系统 PATH。

验证依次执行锁定还原、Core、Model、Host 三组独立 NUnit 测试、Godot 导入、Debug 宿主构建、无界面场景执行、Windows ExportRelease 导出和导出程序启动。`-Graphical` 额外打开真实图形窗口、执行同一验证并保存 viewport PNG；无桌面环境省略此选项。每次生成独立的 `artifacts/verification/<run-id>/`，包含日志、TRX、JSON、截图（如果请求）及 `windows/` 导出目录。

导出程序在自己的目录启动，PATH 中移除开发工具，DOTNET_ROOT 指向不存在的 SDK 目录。脚本核对 `System.Private.CoreLib` 实际加载路径位于导出包。此检查证明本次运行加载了随包运行时；不同显卡、干净 Windows 系统和发行签名仍需另行验收。

双击输出目录中的 `windows/CozyTown.exe` 默认打开 `Town.tscn` 四居民小镇，使用规则模式；操作见[切片使用说明](PLAYABLE_SLICE.md)。分发时保留整个 `windows/` 目录，其中包括 `.pck` 和 `data_CozyTown.Game_windows_x86_64/`。仅复制 EXE 无法组成完整程序。

## 正式完整 Demo 验证

`verify-full-demo.ps1` 在启动构建、场景矩阵和分支回放前，要求固定源码的独立 Standards、Spec 审查已完成且无阻断发现。开发期间的 `verify.ps1`、构建和定向测试沿用原入口。

审查者将收据与报告保存在仓库外。收据格式如下，所有摘要替换为实际值，两个报告由不同审查者提供；`reportPath` 相对于收据目录，也可使用绝对路径：

```json
{
  "schemaVersion": 1,
  "sourceCommit": "<full-commit-sha>",
  "sourceTreeSha": "<full-tree-sha>",
  "baseCommit": "<full-base-commit-sha>",
  "completedAtUtc": "<UTC-ISO-timestamp>",
  "reviews": [
    {
      "axis": "standards", "status": "completed", "blockingFindings": 0,
      "reviewerId": "<standards-reviewer>", "reportPath": "standards.md",
      "reportSha256": "<report-sha256>"
    },
    {
      "axis": "spec", "status": "completed", "blockingFindings": 0,
      "reviewerId": "<spec-reviewer>", "reportPath": "spec.md",
      "reportSha256": "<report-sha256>"
    }
  ]
}
```

标识及报告绑定字段必须是 JSON 字符串；`schemaVersion`、`blockingFindings` 必须是整数。`completedAtUtc` 使用含秒的 UTC ISO 时间字符串，以 `Z` 或 `+00:00` 结尾，可包含小数秒。`sourceCommit` 必须等于 `ExpectedCommit` 及当前 HEAD，树必须匹配；`baseCommit` 必须是其祖先且存在非空差异。缺轴、未完成、有阻断、报告不存在或空白、摘要不符均拒绝。先执行不需要工具链的预检，再启动完整验证：

```powershell
pwsh -File tools/windows/verify-full-demo.ps1 -ExpectedCommit <full-commit-sha> -ReviewReceipt <absolute-receipt.json> -ReviewPreflightOnly
pwsh -File tools/windows/verify-full-demo.ps1 -ExpectedCommit <full-commit-sha> -ReviewReceipt <absolute-receipt.json>
```

完整验证报告保存收据及两轴报告的身份和摘要，每个重型子进程前后复核源码、输入及审查文件未变化。预检通过只证明文件合同符合要求；实际审查结论由独立审查者负责。离线 fixture 的合成收据仅测试命令边界，不能代替最终审查：

```powershell
python tools/windows/test-final-review-gate.py
```

## 场景与证据范围

Core 测试检查世界规则、状态与回放等确定性合同；Model 测试使用固定客户端检查传输、预算准入和记录边界；Host 测试检查宿主协议、输入与保存等合同。三组测试通过不表示实际 Godot 场景或真人操作通过。`verify.ps1` 另外执行内容校验、场景与导出程序的固定 smoke 检查，具体场景范围以本次报告为准；历史候选的真实模型和人工验收仍以各自固定源码及报告为证据，不能移作当前提交的验收结果。

普通入口由 `game/project.godot` 的 `run/main_scene=res://Town.tscn` 指定。`TownGame` 检测到 `--toolchain-smoke` 后才切换到工具链探针 `game/Main.tscn`：Advance 按钮调用独立核心累加 120，Save / reload 将 JSON 写入磁盘并恢复到新实例。直接启动 Main 场景且不带探针参数时写入 `user://toolchain-probe.json`。计数是工具链探针，不代表世界时间；该 JSON 也不是正式游戏存档合同。

自动验证通过 Godot 内部 `Pressed` 信号检查按钮接线，核对 `0 → 120 → 保存恢复 120 → 继续至 121`。无界面和图形模式使用同一行为入口：

```text
-- --toolchain-smoke --report=<absolute.json> [--screenshot=<absolute.png>]
```

验证快照写入报告旁的 `.snapshot.json`。成功同时要求进程退出零、没有引擎错误日志、生成本次 JSON 报告且全部状态断言通过；图形模式还要求截图落盘。无界面结果不证明绘制效果，内部信号也不证明真人鼠标键盘操作。脚本为每个进程设置超时，失败保留输出并返回非零。

## 编辑器与工具复用

在 PowerShell 7 中运行：

```powershell
$toolchain = & ./tools/windows/install-toolchain.ps1
$env:DOTNET_ROOT = Split-Path $toolchain.DotNet
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:NUGET_PACKAGES = Join-Path $toolchain.ToolsRoot 'nuget-packages'
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
& $toolchain.GodotEditor --path game --editor
```

另一干净检出可以使用相同便携工具：给安装和验证脚本都传入 `-ToolsRoot <absolute-tools-directory>`。仓库的 `.godot/`、`bin/` 和 `obj/` 属于本次检出的构建缓存，不随工具目录复用。

## 依赖锁与 CI

Godot Debug 引用 `GodotSharpEditor`，ExportDebug / ExportRelease 不引用它。因此宿主按配置维护三份 `packages.<Configuration>.lock.json`；核心与测试各有一份 `packages.lock.json`。核心和宿主预声明 `win-x64` 依赖图，以便导出时仍可使用锁定还原。

需要修改依赖时，在上述工具环境中显式重新生成受影响的锁文件并审查差异：

```powershell
foreach ($configuration in @('Debug', 'ExportDebug', 'ExportRelease')) {
    & $toolchain.DotNet restore game/CozyTown.Game.csproj "-p:Configuration=$configuration" --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw 'Host restore failed.' }
}
& $toolchain.DotNet restore tests/CozyTown.Core.Tests/CozyTown.Core.Tests.csproj --force-evaluate
if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
```

正常验证设置 `RestoreLockedMode=true`，导出中的隐式 restore 也受约束。GitHub Actions 的 Windows toolchain 工作流在 Windows runner 上执行相同的无界面流程。具体运行结论以对应提交的 Actions 结果及验证报告为准，候选程序的人工试玩单独记录。

## 当前 CI 候选与预发布

Windows toolchain 工作流对同仓库事件和 fork PR 分别使用 `verify`、`verify-fork` job 执行离线检查、构建、测试、场景验证及 Windows 导出启动。两个名称供分支保护使用，按事件来源选择执行；构建 CI 仅有 `contents: read` 权限，不创建 Release。固定验证不加载私人模型凭据，也不消耗真实模型预算。

main 的成功 CI 运行提供名为 `windows-candidate-<run-id>-<attempt>` 的 Actions Artifact，保留 14 天。下载需要登录 GitHub；解压 Artifact 后可取得程序 ZIP、`candidate.json` 和 `SHA256SUMS.txt`。候选身份、摘要核验及下载步骤见 [Windows 候选交付](WINDOWS_DELIVERY.md)。程序需完整解压后运行，操作和人工验收记录见[试玩说明](PLAYTEST.md)。

候选 Artifact 是指定 CI 运行的交付物，不是已公开的 Release。维护者在 main 上手动运行 `Prepare Windows prerelease`，填写 `run_id`、`version` 和 `expected_sha256`；摘要指内层 `CozyTown-windows-x64-<sha12>.zip`，不是 Actions Artifact 外层 ZIP 或服务端 Artifact 摘要。工作流调用 `delivery.ps1` 的 `PrepareDraft` 模式，从成功的 main 运行下载并核验候选，然后将同一程序包及其身份、摘要文件放入草稿预发布。准备草稿不重新构建程序，也不自动公开；人工验收通过后再按交付说明发布。

试玩时记录 Artifact 名称中的 run/attempt、源码提交和内层程序 ZIP 摘要，准备草稿时再次核对。同一 run 重跑后若无法取回原程序包，应停止交付并重新核验，不沿用旧包的人工验收结果。

`delivery.ps1` 的 `Package`、`Verify` 与 `PrepareDraft` 参数和输入约束统一维护在[交付说明](WINDOWS_DELIVERY.md)。完整 Demo 的独立审查收据要求仍按本页[正式完整 Demo 验证](#正式完整-demo-验证)执行；普通 CI 候选通过不替代该完整门、真实模型评测或人工 GUI 验收。历史候选的结果保持原源码归属。

## 历史私有 CI 归档与取件

`ci-release.ps1` 保留为历史私有归档工具，不属于当前 CI 候选或草稿预发布流程。历史运行以标签 `ci-windows-<run-id>-<attempt>` 在当时的私有仓库中保存 Draft Release，每次创建新归档。成功包 `windows-toolchain.zip` 包含完整 `windows/` 和顶层 JSON、TRX、日志；失败包 `windows-toolchain-failure.zip` 保存完整 `artifacts/verification/`。仓库公开或新增交付流程均不会自动发布这些旧草稿；历史制品验收来源见[将 Windows CI 制品转存私有 Release 并保留失败诊断](https://github.com/qinjunw/CozyTown-Godot/issues/38)。

历史 `manifest.json` 记录仓库、工作流、run ID、attempt、事件、实际验证源码提交和树、Release 目标提交和树、验证报告摘要及全部 ZIP 成员的路径、长度和 SHA-256。PR 使用实际合成提交作为 `sourceCommit`，Release 标签指向 PR head；两者树摘要不一致时拒绝打包。不能把标签目标改写成验证源码身份。

历史工具的单个附件最大为 1,900,000,000 字节。压缩包超过此值时，按字节拆为 `.part001` 等附件；manifest 的 `assets` 保留顺序、各段长度和摘要，`archive` 保留原 ZIP 的总长度和摘要。成功包省去逐边界 `data/`，失败包保留全部诊断文件。归档拒绝符号链接、重复或不安全的 ZIP 路径。

取用既有草稿需要具有本仓库 write/push 权限的 GitHub 账号。打开 [Releases](https://github.com/qinjunw/CozyTown-Godot/releases)，选择对应 run/attempt 的 Draft Release，将 `manifest.json` 及其 `assets` 中的全部附件下载到一个新目录；仅有读取权限的账号不能列出草稿，见[GitHub 草稿列表权限](https://docs.github.com/en/rest/releases/releases#list-releases)。也可使用登录同一有权账号的 GitHub CLI，在 PowerShell 7 中替换标签和下载目录占位符后运行：

```powershell
$repository = 'qinjunw/CozyTown-Godot'
$releaseTag = 'ci-windows-<run-id>-<attempt>'
$downloadDirectory = '<download-directory>'
$pagesJson = & gh api --paginate --slurp "repos/$repository/releases?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Cannot list Draft Releases.' }
$pages = ConvertFrom-Json -InputObject ($pagesJson -join "`n") -NoEnumerate
$matchingReleases = @(foreach ($page in $pages) {
    foreach ($release in $page) {
        if ($release.tag_name -ceq $releaseTag) { $release }
    }
})
if ($matchingReleases.Count -ne 1) { throw 'Expected exactly one release with this tag.' }
$releaseJson = & gh api "repos/$repository/releases/$($matchingReleases[0].id)"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Draft Release metadata by ID.' }
$release = ConvertFrom-Json -InputObject ($releaseJson -join "`n")
if ($release.tag_name -cne $releaseTag -or $release.draft -ne $true) {
    throw 'Release ID does not identify the requested Draft Release.'
}
$release.html_url
& gh release download $releaseTag --repo $repository --dir $downloadDirectory
if ($LASTEXITCODE -ne 0) { throw 'Draft Release download failed.' }
pwsh -File tools/windows/ci-release.ps1 -Mode Verify -PackageRoot $downloadDirectory
if ($LASTEXITCODE -ne 0) { throw 'Draft Release package verification failed.' }
```

草稿的 `/releases/tags/<tag>` 查询可能返回 404。上述命令遍历全部 Release 列表页，以大小写完全一致的标签唯一匹配，再按 ID 读取元数据；浏览链接使用返回的 `html_url`。下载仍使用现有 `gh release download` 入口。

历史工具的 Verify 命令先检查各附件，再按 manifest 顺序重组分段，核对完整 ZIP 和每个解压成员的长度、SHA-256 与清单。旧 Publish 路径还核对服务器返回的附件长度和 digest，并实际下载同一草稿后执行此校验。取用程序时解压 ZIP，保留 `<verification-run>/windows/` 的全部文件。

旧 Publish 路径在创建标签前复核 GitHub run/attempt/event、PR head 归属及仓库 private 状态；这些限制属于历史私有归档合同，不能用于描述当前公开候选工作流。

历史工具的离线命令边界检查使用小型固定输出，覆盖验证失败拒绝成功打包、成功收集、完整失败诊断、分段重组、无效输入和损坏附件：

```powershell
python tools/windows/test-ci-release.py
```
