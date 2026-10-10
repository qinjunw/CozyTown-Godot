# 父分支宿主与预算连续性验证

本记录对应[修复父分支回放的宿主与预算连续性](https://github.com/qinjunw/CozyTown-Godot/issues/36)。基线为 `ff81cbf1f28557036eccdaa468b6e7f2785299f5`，实现为 `e37533173619d3d29cee6a09dc0426d16bbd779e`。验证使用已批准的核心回放、宿主保存/记录导出、离线 CLI 和场景回放命令入口；[结构化结果](evidence/replay-parent-continuity/results.json)保留版本、包哈希、计数与首分歧。

## 问题与变更

原回放只把父世界末态传给子分支。父包的保存宿主与子初始宿主同时改成虚构路线或时间仍可通过；删除任一宿主也会跳过比较。子初始预算单独合法即可通过，因而能移除父永久停止、减少历史尝试或费用。

递归执行现在保留重算的宿主与隔离预算末态。保存宿主先与父末态完整比较，坐标沿用既有容差；子初始宿主再核对路线、游标、未消费时间及冻结请求。只允许当前宿主重新计算 `menu`、`focus`、`loading`、`fault`、`dialogue` 暂停原因，其他原因须保留。纯世界父链继续支持。

父包含预算时，子初始预算通过现有 `ApplyExternalState` 连续性规则核对。已有结算、未知状态、尝试身份、恢复名额、已释放的槽和永久停止不能回退；其他分支追加和已有尝试的合法结算仍可前进。停止后的结算保留停止，不能增加尝试。缺少所需宿主或预算依赖有独立错误字段。多层祖先失败保留原父边界、命令和输入。

## 红绿与公开入口结果

| 检查 | 原实现结果 | 修复后结果 |
| --- | --- | --- |
| 同时虚构保存/子初始宿主 | 1 个拒绝断言失败，错误记录被标为通过 | 拒绝并定位父 `host_elapsed` 边界 |
| 缺少保存/子初始宿主 | 缺保存宿主的拒绝断言失败 | 两种缺依赖均明确拒绝 |
| 父停止后子预算去停止并准入发送，两种 profile | 2 个拒绝断言失败 | 均在父 `budget_stop` 边界拒绝 |
| 宿主保存、ZIP 和 CLI，两种 profile 各 6 个情况 | 2 个合法情况通过，10 个篡改拒绝断言失败 | 12/12 通过 |

新增核心用例 33/33 通过，覆盖冻结输入、路线游标、已知/未知暂停、多层父链、两个 profile 的费用/次数/未知状态/占槽/恢复身份回退、永久停止以及合法结算和外部分支前进。完整核心 283/283、宿主 153/153 通过，无跳过。宿主测试实际经过 `TownSaveFile.Write/Read`、`TownRunJournal.ExportSnapshotZip`、`TownReplay.ReadPackage` 和 `ReplayCommand.Run`；合成凭据及账本哨兵逐字节保持不变。

```powershell
dotnet test tests/CozyTown.Core.Tests --filter FullyQualifiedName~TownReplayParentContinuityTests
dotnet test tests/CozyTown.Host.Tests --filter FullyQualifiedName~TownReplayParentContinuityExportTests
```

## 同包场景检查

在干净实现提交上用 .NET `10.0.401`、Godot `4.7.2.stable.mono.official.ed1daf0bf` 重新构建、录制并导出 Windows 程序。宿主和 CLI 构建均为 0 错误、0 警告。新模拟源码摘要为 `07975501034143f014140088b6083abcfb8030628cf43a4ee30f0af3a4b1abfa`。每份输入分别进入核心 CLI、源场景 headless 和 Windows 导出 headless，15 个命令结果均符合预期。导出运行时环境排除已安装 .NET，脚本核对实际运行时来自导出目录。

| 包 | 三个入口的共同结果 | 源场景/导出的实际移动查询、初始身体查询 |
| --- | --- | --- |
| `gameplay.json` | 通过 949 个子边界及 3 个父边界 | 3071、10 |
| `player-dialogue.json` | 通过 98 个边界 | 22、5 |
| `player-dialogue-retired-restore.json` | 通过 4 个子边界及 27 个父边界 | 19、10 |
| 同时把保存/子初始未消费时间改为 `.7` | 父第 3 边界 `parent.savedHost.unconsumedSeconds` 分歧 | 1、5 |
| 父末追加永久停止，子初始预算恢复为空 | 父第 4 边界 `parent.budget.external.stopReason` 分歧 | 1、5 |

正常包由现有 `--scene-replay-fixture` 入口生成。两个篡改包分别修改正常玩法包的两个宿主字段，或在父前缀追加合法 `budget_stop` 并让子起点保留原空预算。篡改包不修改世界检查点；场景入口先重跑父物理查询，再检查连续性。[逐入口报告](evidence/replay-parent-continuity/)将本机运行路径替换为占位符，原始包、日志和 TRX 另行保留。

本轮真实服务调用为 0，未访问用户凭据或现实账本。完整 Demo 的两天操作驱动与最终候选矩阵由[贯通完整 Demo 宿主阶段与离线评测入口](https://github.com/qinjunw/CozyTown-Godot/issues/35)承接；画面和键鼠验收仍由用户执行。
