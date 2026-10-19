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

验证依次执行锁定还原、独立核心 NUnit 测试、Godot 导入、Debug 宿主构建、无界面场景执行、Windows ExportRelease 导出和导出程序启动。`-Graphical` 额外打开真实图形窗口、执行同一验证并保存 viewport PNG；无桌面环境省略此选项。每次生成独立的 `artifacts/verification/<run-id>/`，包含日志、TRX、JSON、截图（如果请求）及 `windows/` 导出目录。

导出程序在自己的目录启动，PATH 中移除开发工具，DOTNET_ROOT 指向不存在的 SDK 目录。脚本核对 `System.Private.CoreLib` 实际加载路径位于导出包。此检查证明本次运行加载了随包运行时；不同显卡、干净 Windows 系统和发行签名仍需另行验收。

双击输出目录中的 `windows/CozyTown.exe` 可打开诊断场景。分发时保留整个 `windows/` 目录，其中包括 `.pck` 和 `data_CozyTown.Game_windows_x86_64/`。仅复制 EXE 无法组成完整程序。

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

`sourceCommit` 必须等于 `ExpectedCommit` 及当前 HEAD，树必须匹配；`baseCommit` 必须是其祖先且存在非空差异。缺轴、未完成、有阻断、报告不存在或空白、摘要不符均拒绝。先执行不需要工具链的预检，再启动完整验证：

```powershell
pwsh -File tools/windows/verify-full-demo.ps1 -ExpectedCommit <full-commit-sha> -ReviewReceipt <absolute-receipt.json> -ReviewPreflightOnly
pwsh -File tools/windows/verify-full-demo.ps1 -ExpectedCommit <full-commit-sha> -ReviewReceipt <absolute-receipt.json>
```

完整验证报告保存收据及两轴报告的身份和摘要，每个重型子进程前后复核源码、输入及审查文件未变化。预检通过只证明文件合同符合要求；实际审查结论由独立审查者负责。离线 fixture 的合成收据仅测试命令边界，不能代替最终审查：

```powershell
python tools/windows/test-final-review-gate.py
```

## 场景与证据范围

`game/Main.tscn` 的 Advance 按钮调用独立核心累加 120；Save / reload 将 JSON 写入磁盘并恢复到新实例。普通运行写入 `user://toolchain-probe.json`。计数是工具链探针，不代表世界时间；该 JSON 也不是正式游戏存档合同。

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

正常验证设置 `RestoreLockedMode=true`，导出中的隐式 restore 也受约束。GitHub Actions 的 Windows toolchain 工作流在 Windows runner 上执行相同的无界面流程。具体运行结论以对应提交的 Actions 结果、验证报告和[制品验收票](https://github.com/qinjunw/CozyTown-Godot/issues/38)为准。

## CI 私有 Release 制品

同仓库 PR、main 推送和手动运行使用唯一标签 `ci-windows-<run-id>-<attempt>`，将验证输出上传到本私有仓库的 Draft Release。每次运行创建新标签和草稿，不覆盖旧归档。验证成功时，`windows-toolchain.zip` 包含每次运行的完整 `windows/`，以及全部顶层 JSON、TRX 和日志；验证失败时，`windows-toolchain-failure.zip` 包含完整 `artifacts/verification/`。构建、测试、导出、打包、上传或下载校验失败均使工作流失败。

`manifest.json` 记录仓库、工作流、run ID、attempt、事件、实际验证源码提交和树、Release 目标提交和树、验证报告摘要及全部 ZIP 成员的路径、长度和 SHA-256。PR 使用实际合成提交作为 `sourceCommit`，Release 标签指向 PR head；两者树摘要不一致时拒绝打包。不能把标签目标改写成验证源码身份。

单个附件最大为 1,900,000,000 字节。压缩包超过此值时，按字节拆为 `.part001` 等附件；manifest 的 `assets` 保留顺序、各段长度和摘要，`archive` 保留原 ZIP 的总长度和摘要。成功包省去逐边界 `data/`，失败包保留全部诊断文件。归档拒绝符号链接、重复或不安全的 ZIP 路径。

使用具有本仓库 write/push 权限的 GitHub 账号打开 [Releases](https://github.com/qinjunw/CozyTown-Godot/releases)，选择对应 run/attempt 的 Draft Release，下载 `manifest.json` 及其 `assets` 中的全部附件到一个新目录；仅有读取权限的账号不能列出草稿，见[GitHub 草稿列表权限](https://docs.github.com/en/rest/releases/releases#list-releases)。也可以使用登录同一有权账号的 GitHub CLI。在 PowerShell 7 中替换标签和下载目录占位符后运行：

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

校验命令先检查各附件，再按 manifest 顺序重组分段，核对完整 ZIP 和每个解压成员的长度、SHA-256 与清单。CI 发布步骤还核对服务器返回的附件长度和 digest，并实际下载同一草稿后执行此校验。取用程序时解压 ZIP，保留 `<verification-run>/windows/` 的全部文件。

发布 job 的 `contents: write` 是 job 级权限，因需读取同一 runner 的验证文件而与构建共处；仅同仓库事件进入该 job，`GH_TOKEN` 仅注入最后发布步骤，checkout 不保留凭据。脚本在创建标签前复核 GitHub run/attempt/event、PR head 归属及仓库 private 状态。fork PR 使用独立的 `contents: read` job 执行完整验证，不能发布草稿。固定验证不加载私人模型凭据，也不消耗真实模型预算。

离线命令边界检查使用小型固定输出，覆盖成功收集、完整失败诊断、分段重组、无效输入和损坏附件：

```powershell
python tools/windows/test-ci-release.py
```
