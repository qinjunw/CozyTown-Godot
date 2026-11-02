# Windows 候选包、试玩与发布

不编译源码也可以使用 CI 生成的 Windows x64 候选包。CI 是 GitHub 自动运行的构建与检查；候选包通过自动检查后，仍需在本机完成[人工试玩清单](PLAYTEST.md)。

## 三种包的区别

| 位置 | 用途与访问方式 | 下一步 |
| --- | --- | --- |
| Actions 的 `windows-candidate-<run-id>-<attempt>` Artifact | main 成功运行的候选；登录 GitHub 后下载，保留 14 天 | 校验、解压，在本机试玩 |
| Releases 中的 Draft | 维护者可见的发布草稿，资产来自已验收的同一个 CI 程序 ZIP | 核对验收记录与附件，等待发布确认 |
| 已发布的 Pre-release | 面向公众的预发布版本 | 下载已发布附件；预发布标记不会限制公众访问 |

自动构建与准备草稿均不公开发布 Release。是否已经有公开运行包，以 [Releases 列表](https://github.com/qinjunw/CozyTown-Godot/releases)为准。Artifact 的登录要求和网页下载入口见 [GitHub 下载说明](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)。

## 1. 下载指定 CI 候选

先取得维护者提供的 **main 成功运行链接、源码提交和程序 ZIP 的 SHA-256**。不要仅凭“最新一次”选择包；PR 检查结果不能替代 main 的发布候选。

1. 登录 GitHub，打开提供的运行链接；也可以从仓库的 [Actions](https://github.com/qinjunw/CozyTown-Godot/actions)选择 **Windows toolchain**。
2. 确认运行已成功，分支为 `main`，提交与待验收版本一致。点击提交链接可复制完整源码提交；运行页 URL 中 `actions/runs/` 后的数字是 run ID。
3. 在运行页的 **Artifacts** 区域，下载 `windows-candidate-<run-id>-<attempt>`。记录完整 Artifact 名称，末尾数字是该运行的 attempt；同一运行重新执行时可能变化。
4. 用 Windows“全部解压”打开下载的外层 ZIP，解压到一个新的候选目录。目录内应有以下三个文件：

```text
candidate-directory/
  CozyTown-windows-x64-<sha12>.zip
  candidate.json
  SHA256SUMS.txt
```

`<sha12>` 是源码提交的前 12 位。`candidate.json` 供校验工具核对来源与文件清单，不要求人工逐字段阅读。`SHA256SUMS.txt` 记录**内层程序 ZIP**的摘要；GitHub 下载的外层 Artifact ZIP 是另一份文件，不能混用其摘要。

如果页面没有候选 Artifact，先确认选中了成功的 main 运行。候选过期、上传失败或被删除时停止取件；请维护者重新生成候选，重新记录 run、attempt 和 ZIP 摘要并核验。重跑即使使用同一源码，也不能静默替换已验收的程序 ZIP。

## 2. 校验内层程序 ZIP

先保留内层 ZIP，尚不要只取出 EXE。打开 PowerShell，将下面路径换成实际文件：

```powershell
Get-FileHash -LiteralPath '<candidate-directory>/CozyTown-windows-x64-<sha12>.zip' -Algorithm SHA256
```

将显示的 64 位 `Hash` 与维护者给出的**程序 ZIP SHA-256**逐字核对，并核对 `SHA256SUMS.txt`。若摘要、源码或 run/attempt 不一致，不把该包用于原候选的验收或发布，重新取得正确候选。

已有仓库和 PowerShell 7 的读者，也可以从仓库根目录使用完整校验入口：

```powershell
pwsh -File tools/windows/delivery.ps1 -Mode Verify `
  -PackageRoot '<absolute-candidate-directory>' `
  -ExpectedSha256 '<expected-program-zip-sha256>'
```

`PackageRoot` 是放置上述三个文件的目录。此入口核对程序 ZIP 摘要、候选清单及包内成员；退出码为 0 表示校验通过，不表示游戏画面或人工操作已验收。普通试玩只需解压后的程序，无需安装 PowerShell 7、Godot 或 .NET SDK。

## 3. 第二次解压并试玩

1. 将通过校验的 `CozyTown-windows-x64-<sha12>.zip` 再次“全部解压”到新的程序目录。
2. 保留整个目录。根目录包含 `START-HERE.md`、`Start-Playtest.cmd`、`CozyTown.exe`、`.pck` 及随包运行时；仅复制 EXE 无法运行完整游戏。
3. 阅读 `START-HERE.md`，双击 **Start-Playtest.cmd**。默认居民使用离线规则，玩家生成反馈使用固定客户端示例，不发送真实模型请求。
4. 按 [PLAYTEST](PLAYTEST.md)填写结果。先完成快速检查，再做完整发布验收；开始前记录源码提交、程序 ZIP 摘要及 run/attempt。

该入口的数据目录为 `%LOCALAPPDATA%\CozyTown-Godot\Playtests\<sha12>`，与普通游戏目录分开。关闭后重开同一入口会启动新世界；按 **F9** 才读取该候选先前用 **F5** 保存的进度。同源码候选会复用这个目录，重新测试时先确认是否已有同候选存档，并保留旧记录。

直接打开 `CozyTown.exe` 仍可使用普通入口，但采用默认 `user://town` 数据目录，不获得上述试玩隔离。已有本地构建也可按[工具链说明](WINDOWS_TOOLCHAIN.md)试玩；本地包的结果归属该本地构建。准备发布 CI 候选时，需记录并验收实际将要发布的同一程序 ZIP。

## 4. 维护者准备同包草稿

先取得 [PLAYTEST](PLAYTEST.md)的完整发布验收结果与明确的通过反馈，保留原程序 ZIP 和摘要。草稿准备只搬运已构建的同包资产，不为发布重新编译。

具有仓库写入权限的维护者在 GitHub 操作：

1. 打开 Actions，选择 **Prepare Windows prerelease**，点击 **Run workflow**，分支选择 `main`。该工作流合入默认分支后才会出现手动入口。
2. 填写下表，运行后等待成功。

| 输入 | 填写内容 |
| --- | --- |
| `run_id` | 已成功的 main **Windows toolchain** run ID，不是本次准备草稿的运行 ID |
| `version` | 未使用的版本标签，例如 `v0.1.0-alpha.1` |
| `expected_sha256` | 本机已验收的**内层程序 ZIP**完整 SHA-256 |

3. 打开 Releases 中生成的草稿，核对版本、源码提交、原 CI 链接、ZIP 文件名与摘要。资产应包含原程序 ZIP、`candidate.json` 和 `SHA256SUMS.txt`。
4. 保持为 Draft，等待公开发布确认。如果原 Artifact 已过期，或重跑后无法取得与验收摘要一致的原包，停止准备；重新生成、核验和记录候选，不沿用旧包的通过结论。

需要使用 CLI 的维护者，可在安装了 PowerShell 7、GitHub CLI，并已登录具备仓库写入权限的账号后，从相应源码检出运行：

```powershell
pwsh -File tools/windows/delivery.ps1 -Mode PrepareDraft `
  -PackageRoot '<new-candidate-download-directory>' `
  -Repository 'qinjunw/CozyTown-Godot' `
  -RunId '<successful-main-run-id>' `
  -Version 'v0.1.0-alpha.1' `
  -ExpectedSha256 '<tested-program-zip-sha256>'
```

此模式从指定成功 main 运行下载候选，核对摘要后创建带附件的 draft/prerelease；它不公开发布。使用新的下载目录，不覆盖已有候选或历史 Release。网页手动触发的权限与步骤见 [GitHub 手动工作流说明](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)。

### CI 打包输入

构建端使用 `verify.ps1` 的成功输出生成候选。将占位符换成本次验证目录和实际运行身份，从仓库根目录运行：

```powershell
pwsh -File tools/windows/delivery.ps1 -Mode Package `
  -VerificationRoot '<artifacts/verification-directory>' `
  -PackageRoot '<artifacts/windows-candidate-directory>' `
  -Repository 'qinjunw/CozyTown-Godot' `
  -RunId '<run-id>' -RunAttempt '<attempt-number>' `
  -SourceCommit '<full-source-commit>'
```

程序 ZIP 包含完整 Windows 导出目录及 `START-HERE.md`、`Start-Playtest.cmd`；验证日志、凭据、真实账本与玩家存档不属于程序包。`candidate.json` 使用 `schemaVersion=1`，记录 `repository`、`sourceCommit`、`runId`、`runAttempt`、`archive` 的名称/字节数/SHA-256，以及 `files` 中各成员的相对路径/字节数/SHA-256。

本地打包可用于检查文件结构；准备发布草稿仍须由 `PrepareDraft` 核对 GitHub 上指定的成功 main 运行及其候选，不能把任意本地包当作 CI 已验证的发布资产。

## 5. 验收确认后公开

公开操作由维护者在取得本候选的人工验收和发布确认后执行：

1. 在 Releases 打开本次草稿的编辑页，核对待发布的版本、源码、CI 链接和已验收 ZIP 摘要。
2. 发布说明写明已测项目、Windows 环境、已知问题和未测项目。新候选不能引用旧候选的人工或真实模型结果作为本版通过依据。
3. 保持 **Pre-release** 标记，点击 **Publish release** 才公开该预发布。Draft 仅维护者可见；已发布 Pre-release 对公众可见，不能将它当作私有验收区。
4. 回到公开 Release 页面，确认附件可下载，下载程序 ZIP 后重新核对验收摘要。不要换成重新打包的 ZIP，也不要把 GitHub 自动生成的源码 ZIP 当作 Windows 程序包。

草稿保存与公开发布是不同操作，界面步骤见 [GitHub Release 管理说明](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)。本流程不修改或发布既有历史草稿。

真实模型为独立可选实验，须另有有效本机配置、现有账本和明确预算；配置见 [Windows 真实试玩入口](REAL_PLAY_LAUNCHER.md)。默认候选的固定客户端示例仅验证离线流程，不能记作真实模型成功。
