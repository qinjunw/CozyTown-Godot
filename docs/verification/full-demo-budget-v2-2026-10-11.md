# 完整 Demo 第二轮预算登记验证

对应[登记修复后真实评测预算并保留旧账本](https://github.com/qinjunw/CozyTown-Godot/issues/39)。代码提交为 `509e4a34a05815f480f0f0f72e3b5bf5ff892762`，基于集成提交 `90b009a3576502737dca382d3dae7d5208ab361c`。本记录验证注册、账本命令和公开宿主／回放接口；未运行最终候选完整场景门、停止现实 v1、初始化现实 v2 或执行第二轮真实评测。

## 变更

原完整 Demo 仅注册 `full-demo-v1`。新增已授权 `full-demo-v2`／`cozytown-godot-full-demo-20261011-v2`，总金额 5,000,000 microCNY、96 次，阶段为 baseline 40、counterfactual 40、player_dialogue 8、manual 8；两物理槽、单次预留 2,110,000 microCNY。v1 注册、身份和限额保留，v1／v2 账本与存档双向拒绝串用。

宿主把 v2 玩家生成分配到 `player_dialogue`，实验 NPC 使用所属场景阶段；人工用途的两类新请求均使用 `manual`。保存恢复校验保留原配置、用途、阶段和冻结正文。核心注册对应回放合同，完整固定驱动及验证脚本默认使用 v2，输入计划仍为 `full-demo-input-v1`。

预算 CLI 新增显式 `--stop`，调用现有 `PersistentBudgetLedger.Stop` 并输出 `before`／`after` 视图。首次原因、全部尝试、已结算费用、未知预留及物理槽保持；重复停止不退还消耗。公开命令入口与顶层程序使用同一 `BudgetCommand.Run`。操作顺序及占位符命令见[运行说明](../FULL_DEMO_RUN.md)。

## 红绿与回归

测试边界沿用已确认的 Model 预算／客户端、公开预算命令、Core 回放、Host 保存／阶段映射／导出及离线 CLI。模型客户端使用固定 HTTP handler；账本、凭据哨兵和故障样本均为临时合成文件。

| 切片 | 红阶段观察 | 绿阶段 |
| --- | --- | --- |
| Model v2 初始化 | `budget_profile_not_registered`，1 个测试失败 | 1／1 |
| 预算停止命令 | 公开命令类型尚不存在，编译 `CS0234` | 1／1 |
| Core v2 注册 | v1 通过，v2 以未支持配置拒绝 | 2／2 |
| Host 配置映射 | v2 错映射到首切片回放合同 | 2／2 |
| Host 场景保存限制 | v2 未拒绝离线 fixture 场景 | 1／1 |

完整单元回归使用 .NET SDK 10.0.401、Debug 和 `-p:RestoreLockedMode=true`：Core **301／301**、Model **83／83**、Host **250／250**，合计 **634**，失败及跳过均为零。Godot C# 项目构建为零警告、零错误；完整评测脚本通过 PowerShell 语法解析。

回归保留首切片及 v1 行为，覆盖 v2 的 96 次／阶段上限、金额与槽位、未知占用、至多一次原阶段恢复、两个进程竞争初始化／恢复／槽位、终止进程后的预留保留。宿主覆盖实验／人工路由、玩家及 NPC 冻结请求保存读取、错误配置和阶段拒绝。导出测试对首切片、v1、v2 分别执行保存父前缀、子分支 ZIP 与离线 CLI，并检查预算及宿主分歧。

另用编译后的预算 CLI 进程在 ignored 临时目录初始化并停止一个合成 v2 账本，两个退出码均为 0，身份匹配、尝试与费用均为零。测试不读取本机凭据或现实账本，不发送外部模型 HTTP。保留一次误用 `dotnet test --locked-mode` 的 `MSB1001` 命令失败日志；改用上述 MSBuild 属性后完整回归通过。

## 证据与后继执行

[机器可读结果](evidence/full-demo-budget-v2/results.json)关联 9 份红绿 TRX、停止命令编译红日志、三份完整单元 TRX、构建日志及合成 CLI 收据；[原始文件清单](evidence/full-demo-budget-v2/raw-manifest.json)记录 24 个文件的长度和 SHA-256。[归档收据](evidence/full-demo-budget-v2/archive-receipt.json)记录 `raw-evidence.zip`：173,069 字节，SHA-256 `fad96455f46bac186d7012598782fb7d650f96a29073b57a2d2d150c258a7911`。归档含全部原始文件及两份元数据，解压逐文件长度与哈希匹配；原始副本保留在本轮外部证据目录。

最终集成候选须重新冻结并通过完整 headless 源场景、Windows 导出和同包回放门。随后由唯一真实执行者停止旧现实 v1、核对历史费用和尝试不变，再初始化唯一现实 v2 并保留零消耗收据。上述现实操作不属于本记录的已完成结果。

保留 v1 预算注册只证明合同仍受支持，不承诺跨源码版本重算旧真实记录。旧包使用原归档匹配程序，源码摘要保护保持。第二轮真实采用率、全部失败与费用由[综合评测](https://github.com/qinjunw/CozyTown-Godot/issues/25)另行报告；本次离线通过不能证明模型采用率提高。
