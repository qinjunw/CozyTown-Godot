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

正常验证设置 `RestoreLockedMode=true`，导出中的隐式 restore 也受约束。GitHub Actions 的 Windows toolchain 工作流在 Windows runner 上执行相同的无界面流程，并保留导出包和日志 14 天。具体运行结论以对应提交的 Actions 结果和验证报告为准。
