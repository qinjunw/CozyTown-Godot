# 停止预算后的跨分支回放验证

2026-10-10，对应[修复停止预算后的跨分支追加回放](https://github.com/qinjunw/CozyTown-Godot/issues/32)，基于集成提交 `c0c1e5b6026a50602b90a874bb272005a9604c10`。

`TownRecording` 记录初始 `StopReason=authentication_failed` 的空预算，随后以 `budget_external` 追加一条合法阶段、费用及槽位均未超限的预留。修复前，`TownReplay.Run` 在首切片与完整 Demo 两份预算配置下均错误返回通过；新增回归的两例均因此失败。

`TownReplayBudget.ApplyExternalState` 现在要求已停止预算的尝试数量保持不变，继续核对停止原因及每项既有尝试。上述输入在第 1 个边界返回 `divergence`，字段为 `budget.external.attempts.length`，预期 `0`，实际 `1`；之后的世界推进边界不执行。

公开核心输入／回放入口新增 10 个用例，覆盖两份预算配置：停止后追加被拒绝、已有尝试合法结算、已有尝试转为未知，以及未停止时合法追加。结算保留 10 个输入 token 与 1 个输出 token 的 28 微元扣减；未知用量保留 2,110,000 微元预留，并分别验证继续占槽及已有生成终态时释放槽位。后续预算输入继续核对费用、槽位和原停止原因。

验证命令：

```powershell
dotnet test tests/CozyTown.Core.Tests/CozyTown.Core.Tests.csproj --filter FullyQualifiedName~TownReplayBudgetStopTests
dotnet test tests/CozyTown.Core.Tests/CozyTown.Core.Tests.csproj
```

在初始基点，核心全量 218 个用例通过；合入包含玩家对话的集成提交 `891ca6f7397ff81eeeb1cb897ef08ab27479f11b` 后，10 个新增用例与核心全量 232 个用例均通过，无失败或跳过。测试使用包内预算状态，不读取真实账本或凭据，不请求真实模型。机器可读结果见[验证摘要](evidence/replay-budget-stop/verification.json)。
