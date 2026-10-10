# 完整 Demo 预算配置离线验证

日期：2026-10-10。实现提交 `4014c635e5788ce1c594544e67c83c57ff38fa54`，集成基点 `61c852f5f427d9b078df4fe65337d900a5a477c9`，Windows 上使用 .NET SDK 10.0.401。对应[实现完整 Demo 的独立预算配置与持久账本](https://github.com/qinjunw/CozyTown-Godot/issues/34)，配置依据[已批准合同](../FULL_DEMO_EVALUATION_CONTRACT.md)。

## 输入与边界

检查通过公开 `PersistentBudgetLedger`、`DeepSeekClient` 及其已有固定 HTTP 边界进行。账本位于临时目录，凭据为合成值；首切片输入来自已提交的探针快照，保留其 4 次发送、2,212 microCNY 扣减。完整 Demo 的账本由显式初始化接口创建。外部请求为零，没有读取用户凭据或既有实时账本。

新增 `full-demo-v1` 为 CNY 5,000,000 microCNY、96 次、baseline 40 / counterfactual 40 / player_dialogue 8 / manual 8、单次预留 2,110,000、两个共享物理槽。新账本持久 schema 2 与配置版本；缺省 `first-slice-v1` 继续读取和更新原 schema 1、48 次配置。`BudgetSnapshot` 与 `BudgetAttempt` 字段保持不变。

## 测量结果

| 检查 | 结果 |
| --- | --- |
| 模型库测试 | 70 通过，0 失败，0 跳过 |
| 宿主兼容测试 | 141 通过，0 失败，0 跳过 |
| 原探针离线自检 | 21 通过，远程请求 0 |
| 初始化工具 Release 构建 | 0 警告，0 错误 |
| 首次 CLI 初始化 | 退出 0，输出完整 Demo 的零消耗快照 |
| 同一路径再次初始化 | 退出 1、`ledger_already_exists`，账本 SHA256 不变 |
| 相对路径 CLI 初始化 | 退出 1、`initialization_arguments_required` |

公开边界检查覆盖四阶段各自上限和总 96 次、金额与两槽共用、未知费用及槽位保留、停止后已有尝试结算、恢复沿用原阶段且最多一次。配置版本、身份、币种、schema、总量、金额、预留、并发或阶段上限漂移均拒绝。客户端新旧配置错配在凭据读取及 HTTP 前失败；不支持的请求阶段在账户 HTTP 前失败。

三个双进程竞争检查分别验证新建账本、最后一个物理槽与一次恢复名额，各仅一个进程成功。另一个进程在登记恢复后被终止；重新打开仍保留 player_dialogue 阶段、CNY 2.11 预留和一个物理槽，第二次恢复被拒绝。现有账本、残锁或旧备份阻止重新初始化；损坏账本不自动恢复。这里验证进程故障行为，不证明断电持久性。

红绿过程先观察缺少初始化/配置参数/玩家阶段接口造成编译失败，再完成对应接口。阶段预检回归在实现前失败：原客户端会调用账户 HTTP，并在后续账本处拒绝；实现后，三个未注册阶段用例均在 HTTP 前拒绝。其余连续性检查验证既有规则在新注册配置下仍成立。

## 重现与证据

在仓库根目录，以已安装的项目 SDK 执行：

```powershell
dotnet test tests/CozyTown.Model.Tests/CozyTown.Model.Tests.csproj -c Release
dotnet test tests/CozyTown.Host.Tests/CozyTown.Host.Tests.csproj -c Release
dotnet run --project tools/experiments/CozyTown.ModelProbe/ModelProbe.csproj -c Release -- --self-test
dotnet restore tools/CozyTown.Budget/CozyTown.Budget.csproj --locked-mode
dotnet build tools/CozyTown.Budget/CozyTown.Budget.csproj -c Release --no-restore
```

[结果计数](evidence/full-demo-budget/results.json)、[CLI 检查](evidence/full-demo-budget/cli-results.json)、[合成初始快照](evidence/full-demo-budget/initialized-synthetic-budget.json)、[源码哈希](evidence/full-demo-budget/source-manifest.json)。完整测试 TRX 保存在候选制品的 `artifacts/budget-verification/`，其中包含本机运行路径，不提交到仓库。

操作入口和配置模板见[模型库说明](../../src/CozyTown.Model/README.md)。本结果验证模型适配与账本；宿主的预算身份、四阶段路由、真实玩家生成入口、两天驱动与付费样本仍由[综合评测](https://github.com/qinjunw/CozyTown-Godot/issues/25)接续。

## 场景回放合入后的兼容检查

合入集成提交 `1287f35` 后形成 `170d288bf7a0a1187fbc8b0fb3923401aa0c8ec1`。新预算相关 24 项模型测试及 141 项宿主测试再次通过；Godot 游戏执行锁定依赖还原和 Debug 构建，0 警告、0 错误。模型与核心的完整 Demo 配置在版本、身份、金额、预留、总量、并发和四阶段上限上相同；宿主使用的模型快照与核心预算状态保持相同字段。

该检查确认新增模型配置与正式场景回放源码同时编译。模型源码变化会改变场景回放的模拟源码摘要；此前场景矩阵仍属于其原候选。[贯通完整 Demo 宿主阶段与离线评测入口](https://github.com/qinjunw/CozyTown-Godot/issues/35)将冻结新候选并重新生成回放输入。
