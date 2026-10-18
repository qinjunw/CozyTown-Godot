# Windows 真实试玩入口

`tools/CozyTown.Play` 是可维护的启动与只读预检 CLI。它验证指定 Windows 候选的运行文件、模型配置、凭据格式与现存共享账本，然后启动游戏。离线预检不证明服务端认证、模型响应或真实生成成功。

用户收到匹配候选的入口目录后，双击 `Start-Real-Model.cmd` 试玩，或双击 `Start-Offline.cmd` 进入规则模式；配置由发布者预先绑定。真实启动后四名居民可能自动请求模型，与玩家生成反馈共用同一额度。规则模式使用自己的存档目录。

当前独立试玩配置为 `manual-play-v1`：预算身份 `cozytown-godot-manual-play-20261017-v1`，CNY 5、最多 96 次，全部属于 `manual`，共用两个物理槽，每次保守预留 CNY 2.11。金额或次数任一上限先到即停。源码登记不代表本机现实预算已经启用；现实启用使用唯一新账本、新世界目录和匹配候选，历史 `full-demo-v2` 八次人工阶段保留原归属。正式评测驱动拒绝此试玩配置。

## 发布与本机配置

从仓库根目录发布 Windows x64 启动器：

```powershell
dotnet publish tools/CozyTown.Play/CozyTown.Play.csproj -c Release -r win-x64 --self-contained true -o artifacts/play-launcher
```

发布目录包含运行所需的 .NET 文件。将整个目录保留在本机，生成的双击入口调用其中的 `CozyTown.Play.exe`，无需系统安装 .NET SDK。

复制[启动配置模板](../tools/CozyTown.Play/launcher.example.json)到忽略的 `.local/` 目录。相对路径以该 JSON 文件所在目录为基准；CLI 将它们解析为绝对路径。填写：

| 字段 | 含义 |
| --- | --- |
| `gameExecutable` | 已冻结 Windows 运行包内的 `CozyTown.exe` |
| `modelConfig` | 由 [manual-play.example.json](../src/CozyTown.Model/manual-play.example.json)填写的本机模型配置；凭据和账本路径必须为绝对路径，`budgetProfileVersion` 为 `manual-play-v1` |
| `executionPurpose` | `manual`；NPC、玩家、失败与恢复共用人工试玩阶段额度 |
| `realDataDirectory`、`offlineDataDirectory` | 两个互不包含、且与运行包、私有文件及历史运行分开的新存档目录；模板位于 `play-data/manual-play-v1/real` 与 `offline` |
| `packageManifest` | 与候选绑定的运行文件清单 |
| `expectedSourceCommit` | 该候选的完整 40 位源码提交 |
| `packageManifestSha256` | 该文件清单的 SHA-256 |

文件清单使用以下格式。`files` 覆盖运行目录内全部文件，`path` 相对 `CozyTown.exe` 的目录，`bytes` 与 `sha256` 来自实际冻结文件；源码提交须与候选来源核验一致：

```json
{
  "sourceCommit": "<candidate-source-commit>",
  "files": [
    { "path": "CozyTown.exe", "bytes": 123, "sha256": "<file-sha256>" },
    { "path": "CozyTown.pck", "bytes": 456, "sha256": "<file-sha256>" }
  ]
}
```

清单摘要把本机入口绑定到一份已核验清单；它不代替候选来源审查。运行文件增加、删除或变化后应重新核验候选，再更新本机绑定。CLI 不固定旧提交、文件数量或单个服务端指纹。

## 只读预检与双击入口

```powershell
artifacts/play-launcher/CozyTown.Play.exe --real --settings=.local/play/launcher.local.json --check-only
artifacts/play-launcher/CozyTown.Play.exe --offline --settings=.local/play/launcher.local.json --check-only
pwsh -File tools/windows/new-play-entries.ps1 -LauncherExecutable artifacts/play-launcher/CozyTown.Play.exe -Settings .local/play/launcher.local.json -OutputDirectory .local/play/entries
```

生成目录中，`Start-Real-Model.cmd` 启动真实试玩，`Start-Offline.cmd` 启动规则模式。已有离线游戏入口仍可独立使用。生成的 `entry.local.json` 保存相对引用，实际配置和这些生成文件留在忽略目录；不要把凭据、本机配置、存档或共享账本加入 Git 或 Release。

真实启动传递 `-- --model-config=<absolute-local-config> --execution-purpose=manual --data-dir=<absolute-real-data>`；离线启动只传递独立数据目录，不读取模型配置、凭据或账本。同模式入口持有互斥锁直至游戏退出。预检不会创建数据目录、启动游戏、发送请求或写入账本。

真实预检读取已登记预算配置和 `OpenExisting/Snapshot`。输出区分总次数、人工次数、已结算金额、保留金额、可用金额、槽位和 `stopReason`。指纹、服务端策略和响应校验由游戏运行时根据实际 `manual` 用途处理。启动器不恢复停止账本、不结算未知请求、不初始化缺失账本，也不扩额。

配置或文件错误会返回非零退出码和脱敏错误代码。`ledger_stopped` 表示共享账本已经停止；查看同一报告中的具体 `stopReason`。`money_limit` 表示可用金额无法预留下一次请求；预留金额不等于确认扣费。次数、槽位和配置错误分别报告，不统一显示为额度不足。

试玩时走近居民并交谈，选择“生成一段反馈”。面板区分固定回应、模型反馈、规则回退、等待和具体失败；菜单或失焦等其他暂停生效时停止派发与展示。观察面板显示当前预算版本、总次数和人工阶段上限；导出记录可关联模型请求、候选与世界受理/执行。模型回复完成不等于行动已被世界采用。保存恢复保持预算身份，旧预算存档被拒绝且保留当前世界；操作见[切片使用说明](PLAYABLE_SLICE.md)，约束见[玩家对话合同](PLAYER_DIALOGUE_CONTRACT.md)和[保存恢复合同](SAVE_REPLAY_CONTRACT.md)。

## 验证范围

`PlayCommandTests` 通过公开 CLI 使用合成凭据、临时账本和固定文件验证只读预检、参数计划、包完整性、停止原因与各项拒绝。测试不调用服务端。实际画面和真实文本由人工试玩验收；启动后居民可能自动调用模型并消耗同一人工额度。
