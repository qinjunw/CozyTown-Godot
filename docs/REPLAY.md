# 输入记录与离线核心回放

本页说明[版本化输入记录与离线核心回放](https://github.com/qinjunw/CozyTown-Godot/issues/22)的实现与操作。回放重跑当前 C# 世界和宿主输入规则，使用包内空间事实回答逐次查询，并与每个边界的完整预期状态比较。它验证核心过程；Godot 再次计算碰撞由[场景回放](https://github.com/qinjunw/CozyTown-Godot/issues/23)承接。

## 运行

在仓库根目录使用 .NET 10 SDK。无需模型配置、密钥或现实预算账本。

```powershell
dotnet run --project tools/CozyTown.Replay -- --input=<export.zip> --report=<report.json>
dotnet run --project tools/CozyTown.Replay -- --input=<run-directory>
dotnet run --project tools/CozyTown.Replay -- --input=<replay.json> --metrics=<metrics.json>
```

输入可以是单独的 `replay.json`、运行目录或导出 ZIP。目录读取追加的 `events.jsonl`，校验记录序号及已封存前缀；ZIP 同时校验其中的输入记录和封存包。单独 JSON 与固定样例 ZIP 使用包内边界。

标准输出及 `--report` 是 JSON。通过返回 0，规则分歧、记录中断或未封存前缀返回 1，读取失败返回 2。失败报告包含首个边界、字段、预期值、实际值、相关命令及输入、规则与数值版本。CLI 报告省略完整末态；库返回值保留末态供父分支校验。`--metrics` 另外记录进程耗时、峰值工作集与垃圾回收后的托管内存。

正在追加且超过封存包的目录报告 `unsealed_recording`。它重算最后完整前缀供诊断，不能计为完整评测通过。使用游戏的“导出记录”得到封存 ZIP。导出在同一输入记录锁内捕获前缀、写入包并复制归档；后台响应不能在包头与 JSONL 之间插入另一条回放边界。

## 记录内容

`TownReplayPackage` 结构版本为 1，绑定当前 `town-social-production-v4` 规则和 `pixels-double-grid16-radius5-morning-motion-prefix-v3` 数值合同。包内保存内容清单、定义、初态快照、运行会话身份、初始宿主状态及预算视图。未知版本或内容指纹不符会拒绝回放。

每个 `TownTrace` 有连续序号、命令及输入、输入指纹、按调用顺序排列的空间查询和预期检查点。世界入口覆盖推进、移动、交谈、决定请求、派发、到达、候选受理、事件确认、交易、快捷栏、农作、睡眠和生产。宿主入口覆盖点击路线、键盘取消路线、移动、暂停原因、接受的现实时间、固定步消耗与冻结提供者输入。

睡眠记录原操作、请求小时数、现实等待和暂停输入，实际推进、跨日结算与拒绝结果由规则重新计算。冻结输入按逻辑请求保存；终结后从活跃宿主视图清理，原输入仍留在历史边界中。

模型完整响应到达、完成记录落盘、邮箱入队、邮箱进入世界边界分别记录。核心的到达时间输入独立于后续写盘和暂停时长；及时收齐的响应不会因晚落盘变成超时。退役会话的晚到完成连同完整响应导入当前分支的记录，再由当前世界按旧会话拒绝。提供者密钥与授权头不进入记录；响应含本次密钥时只保存明确的 `credential_echo` 遮蔽标记。

预算模拟只使用包内初始视图、额度判定、占用、结算、未知用量及其他分支的账本变化事实。它重新计算原发送、恢复次数、阶段、总数、金额和并发准入，不打开现实账本。支持首切片 `first-slice-budget-v1` 与已批准的 `full-demo-v1`；录制器可显式绑定对应 profile，未知 profile 拒绝。完整 Demo 的现实账本接线由其评测实现承接。

## 检查点、分叉与容量

世界和宿主各保留首个完整预期检查点，后续用 Brotli 编码的 JSON 字段差量保存，并指向上一检查点的边界序号。`TownCheckpointCursor.ReadWorld` / `ReadHost` 仅重建独立的预期 JSON。命令始终进入正常世界或宿主入口，差量与检查点不会写入执行中的世界。

每个边界重建完整预期状态后，比较身份、资产、阶段、去重记录、冻结上下文、随机状态、待办、事件和宿主游标。坐标分量允许绝对误差 `0.00001`；其他字段精确比较。查询须按原顺序匹配实体、种类、起终点与身体半径，之后才返回记录的通行结果。漏查询、多查询和错顺序均停止。

保存同时写入父运行的精确输入前缀、保存快照和宿主状态。子包内嵌所需父前缀，先重算父状态并核对来源快照、父分支、保存世界序号和分叉序号，再通过正常恢复规则建立子分支。保存点之后的父运行尾部不执行。缺父前缀时世界仍可按存档续接，但离线回放报告 `missing_parent_prefix`。

单个包和 JSONL 读取上限各为 512 MiB，单个解压检查点上限为 64 MiB，父链最多 32 层。录制器以 448 MiB 保守估计上限检查新增边界；达到限制或边界写入失败时保留最后完整前缀并停止新的世界更新和真实派发。`recording_interrupted` 明确报告最后完整边界，不能计为评测通过。文件读写采用流式序列化，避免额外保留整包 JSON 字符串。

旧记录缺少 `replay.json`、必要输入、差量基点或当前完整状态字段时报告能力不足或无效记录。不会补造旧版农作、生产或模型历史。

## 固定样例与验证

[当前玩法固定包](verification/evidence/replay/current-gameplay.zip)从当前内容数据经正常公开入口生成，覆盖四居民规则决定、失败移动、路线取消、叠加暂停、购买、种植、浇水、三天睡眠、收获、鸡饲喂与收取、钓鱼、烹饪以及重复操作。它使用固定的核心空间事实，不充当 Godot 碰撞重算证据。

```powershell
dotnet run --project tools/CozyTown.Replay -- --input=docs/verification/evidence/replay/current-gameplay.zip
dotnet run --project tools/CozyTown.Replay -- --input=docs/verification/evidence/replay/tampered-command.zip
dotnet run --project tools/CozyTown.Replay -- --generate-fixtures --content=docs/verification/evidence/player-production/content-baseline.json --output=<output-directory>
```

正常包通过 399 个边界；篡改包把第 2 个边界的无效移动改为正常位置，重算时发生未记录的空间查询，立即报告 `queries[0]` 分歧。

源场景使用 `--headless --path game -- --replay-record-smoke --record-seconds=<2..360> --data-dir=<absolute-directory> --report=<absolute-report.json>`，按实际单调时间走普通宿主固定步，使用规则模式，然后导出和离线重算。该入口拒绝真实模型配置。

2026-10-10 实测保存在[记录性能证据](verification/evidence/replay/)。独立完整块编码的 30 秒包为 41.26 MB、同进程读取回放工作集 2.24 GB；字段差量后的 30 秒包为 11.70 MB、工作集 539.6 MB。360 秒真实墙钟规则运行产生 57,054 个边界，JSON 89.96 MB、JSONL 95.90 MB、ZIP 84.36 MB，并通过全部边界。长测包含正常居民活动，不是重复启动样本。它证明该固定规则运行的容量，不代表真实模型响应、未来对话或其他内容的上界。

360 秒包在流式改造前同进程读取回放的峰值工作集为 3.99 GB，原报告保留。改造后用同一 ZIP 在独立 CLI 进程复跑，57,054 个边界全部通过，耗时 88.20 秒、峰值工作集 350.81 MB，回收后托管内存 6.23 MB。两者进程范围不同；独立 CLI 结果验证当前读取和回放路径，不能据此推断引擎的固定占用。

核心和宿主测试验证查询顺序、命令及检查点篡改、缺口、差量基点、重复恢复、旧会话结果、暂停下及时到达但延迟落盘、独立预算准入、记录截断、容量中断和并发导出。现实账本与密钥哨兵文件在离线回放前后逐字节相同。
