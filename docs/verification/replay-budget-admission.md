# 预算准入拒绝记录的空身份回放验证

2026-10-10，对应[修复预算准入拒绝记录的空身份回放](https://github.com/qinjunw/CozyTown-Godot/issues/33)。实现基于集成提交 `f397d9a015e7fd410523abf8f4fe8e54ead85f40`，包含玩家对话和停止预算追加检查；最终验证合入 `5e6e35ad757e8afd74c72911bfd1afecf5da3b2a`。

正式宿主把未获准发送的 `attemptId` 记录为 JSON null。修复前，通过公开 `TownRecording` 记录合法的 `ledger_stopped` 或 `concurrency_limit` 拒绝，`TownReplay.Run` 均在第 1 个边界返回 `invalid_recording / input / JSON root cannot be null.`。两份预算配置下的 4 个新增用例均复现此问题。

`TownReplay.Run` 现在只在 `budget_reserve` 的尝试身份字段显式读取 JSON null，再进入正常预算准入规则。合法拒绝不创建物理尝试，保留已有预留、槽位、尝试和停止原因；后续外部预算输入再次核对完整状态。全局 `TownJson` 非空根约束保持不变。

16 个公开核心输入／回放用例覆盖旧首切片与完整 Demo 配置：4 例正常空身份拒绝通过，4 例获准记录使用 null 或空字符串身份被拒绝，6 例字段缺失或数值／对象类型被拒绝，2 例错误准入结果在 `budget.admission` 报告分歧。获准记录缺身份停在 `budget.attempts[0].attemptId`；字段缺失或类型错误仍为 `invalid_recording / input`。

验证命令：

```powershell
dotnet test tests/CozyTown.Core.Tests/CozyTown.Core.Tests.csproj
dotnet test tests/CozyTown.Host.Tests/CozyTown.Host.Tests.csproj --filter FullyQualifiedName~TownReplayCliTests
dotnet run --project tools/CozyTown.Replay -- --input=docs/verification/evidence/replay/current-gameplay.zip
dotnet run --project tools/CozyTown.Replay -- --input=docs/verification/evidence/replay/tampered-command.zip
```

核心全量 248 项与既有 CLI 缺输入用例均通过。固定正常包通过 399 个边界，退出码为 0；固定篡改包仍在第 2 个边界的 `queries[0]` 停止，退出码为 1。检查使用包内预算和固定记录，不读取真实账本或凭据，不请求真实模型。机器可读结果见[验证摘要](evidence/replay-budget-admission/verification.json)。
