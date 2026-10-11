# 真实模型传输与预算

此库独立于 Godot 和世界核心，提供 DeepSeek HTTP 传输、冻结请求/结果数据和持久预算准入。行动合法性、轮转、暂停、世界提交和读档后的会话隔离由宿主处理。模型层不会直接执行候选。

## 宿主接口

宿主从显式选择的本机配置取得凭据文件和实时账本路径，然后构造 `DeepSeekOptions`、`IModelJournal` 与 `DeepSeekClient`。客户端不搜索凭据、不默认启用真实模式，也不创建缺失账本。`DeepSeekOptions.ExpectedSystemFingerprint` 默认采用已登记探针的观察值；它只检测已知返回差异，不能固定提供方版本。

`FrozenModelRequest` 包含运行/会话/逻辑请求/居民身份、活动修订、阶段、完整冻结请求 JSON、恢复标志及剩余逻辑等待。阶段必须属于所选预算配置；不支持的阶段在账户 HTTP 请求前拒绝。传输限时取 30 秒与剩余等待的较小值。首个请求前验证账户为可用 CNY 账户；也可提前调用 `VerifyAccountAsync`。没有自动重试。

`IModelJournal.WriteRequestAsync` 必须在返回前持久保存请求及其预算关联；失败不会发送模型请求。`WriteCompletionAsync` 保存完成结果，不等待世界解除暂停。宿主将返回的 `ModelCompletion` 放入邮箱，再按会话代数、活动和期限验证。结果记录提供方结束原因、用量、费用结算和在途槽状态；它不等于世界受理或执行。

`ResponseArrived` 在完整 HTTP 正文到达后通知身份和单调经过时间，不包含正文。宿主把此通知放入主线程邮箱，冻结及时回复的逻辑超时，等待最终结果持久化后再受理。`LogicalElapsedToArrival` 排除结算和日志等待。该阶段暂不允许保存半份回复，界面提示稍后保存；日程过期仍可终止待办。

请求准入失败抛出带固定 `Code` 的 `ModelException`。已登记尝试的传输失败返回 `ModelCompletion.FailureCode`。HTTP 错误正文、认证头和思考字段不进入结果；已知密钥回显、非法或不完整候选正文不会作为 `CandidateJson` 导出。宿主仍须按业务 schema 检查可解析候选。

## 账本连续性

`PersistentBudgetLedger.OpenExisting(path, profileVersion)` 读取所选配置的账本与 `.lock` 标记，保留已经发生的调用及扣减。缺省配置仍为首切片。首次发送、一次恢复补发、阶段额度、金额及槽位在同一文件锁事务内检查并持久写入。锁只保护账本事务，不跨 HTTP 请求持有；不同进程不能同时领取同一恢复名额。

同一逻辑请求的普通发送只能登记一次，恢复必须关联同阶段原尝试。未知费用保留单次 CNY 2.11 预留，未知生成保留物理槽；传输取消不会退款或释放槽位。有完整提供方终态但缺少可信用量时，只释放槽位并停止新派发。普通超时保留占用，其余满足额度与槽位条件的请求仍可进入。

完整有效用量按全部输入未命中峰时价格扣减，成功、截断或非法业务候选均不会免除已发生用量。此金额为应用预算计算，不是核对后的供应商账单。响应型号或指纹不符时，保留原预留并停止，避免用未登记模型的价格结算。

账本更新使用刷新后的临时文件与原子替换；旧副本不自动恢复。进程中断后已登记项保留，文件损坏或丢失明确失败。这里的进程故障测试不等于断电持久性证明。

## 注册预算与显式初始化

| 配置版本 | 预算身份 | 发送上限与阶段 | 持久格式 |
| --- | --- | --- | --- |
| `first-slice-v1`（缺省） | `cozytown-godot-first-slice-20260920-v1` | 48：probe 4、baseline 22、counterfactual 22 | 原 schema 1 |
| `full-demo-v1` | `cozytown-godot-full-demo-20261010-v1` | 96：baseline 40、counterfactual 40、player_dialogue 8、manual 8 | schema 2，包含 `profileVersion` |
| `full-demo-v2` | `cozytown-godot-full-demo-20261011-v2` | 96：baseline 40、counterfactual 40、player_dialogue 8、manual 8 | schema 2，包含 `profileVersion` |

三份配置均为 CNY 5,000,000 microCNY、每次预留 2,110,000、最多两个物理槽。`ModelBudgetProfile` 只提供这三份注册值；账本中的身份、版本、金额、阶段和并发必须逐项匹配。首切片 schema 1 更新不写新增版本字段，原探针工具继续使用原配置。`PersistentBudgetLedger` 的静态常量仍表示首切片；选择完整 Demo 时使用实例 `Profile` 或 `Snapshot()`。v1 与 v2 的注册对象和预算身份独立，即使限额相同也不能互相打开账本。

`DeepSeekOptions` 第四参数 `BudgetProfileVersion` 缺省为 `first-slice-v1`，第三参数继续表示系统指纹。[完整 Demo 模板](full-demo.example.json)用于构造模型库选项；实际绝对路径保存在忽略的本机配置。该模板不自动启用游戏宿主，宿主阶段路由和真实运行由[完整 Demo 综合评测](https://github.com/qinjunw/CozyTown-Godot/issues/25)接续。

新完整 Demo 预算只由 `PersistentBudgetLedger.InitializeNew(path, profileVersion)` 或下列离线工具显式初始化，`profileVersion` 仅接受已注册的 v1／v2。当前授权使用 v2；先完成最终候选完整离线门，再选定唯一账本位置并创建父目录，执行一次：

```powershell
dotnet run --project tools/CozyTown.Budget/CozyTown.Budget.csproj -c Release -- --initialize --profile full-demo-v2 --ledger-file <new-absolute-ledger-path>
```

工具创建新账本和持久锁标记、刷新文件并输出零消耗快照，不读取凭据或访问服务。现有账本、锁标记或 `.previous` 备份任一存在即拒绝；竞争初始化最多一个进程成功。中断留下的标记保留，普通启动和恢复不补建、不恢复备份。保持所选路径，不以换目录、复制或删除账本刷新同一预算。首切片没有新增初始化入口。

`PersistentBudgetLedger.Stop(reason)` 保留第一次停止原因和全部尝试、费用、预留及槽位，之后拒绝新准入。预算 CLI 的 `--stop --profile <registered-profile> --ledger-file <existing-absolute-path> --reason <reason-code>` 调用该接口，输出停止前后视图；重复停止不会退还用量。显式停旧 v1、初始化新 v2 的操作顺序及收据要求见[完整 Demo 运行说明](../../docs/FULL_DEMO_RUN.md)。初始化和停止失败均输出 JSON `code` 及非零退出码。

完整 Demo 使用 `ModelStage.PlayerDialogue` 与 `ModelStage.Manual` 映射到 `player_dialogue`、`manual`；NPC 和玩家使用同一金额及槽位池。恢复必须沿用原阶段，阶段间不能转移额度。`manual` 的八次仅供用户最终试玩；综合评测驱动负责该阶段的进入条件。注册配置和初始化功能不增加付费重跑授权。

## 离线验证

在仓库根目录执行：

```powershell
& ./.local/tools/dotnet/dotnet.exe test tests/CozyTown.Model.Tests/CozyTown.Model.Tests.csproj --configuration Release
```

测试使用临时目录中的合成凭据和公开探针账本快照，不读本机凭据或实时账本，不发送外部请求。覆盖探针 4 次/2,212 微元消耗承接、三份配置隔离、显式初始化和停止、费用与槽位、错误响应、记录失败、两个子进程竞争初始化/槽位/恢复、终止占额进程后的再次打开及额度不回退。原 v1 离线结果见[预算配置验证](../../docs/verification/full-demo-budget-2026-10-10.md)，v2 登记结果见[第二轮预算登记验证](../../docs/verification/full-demo-budget-v2-2026-10-11.md)。

传输解析和账本字段源自本仓库 `a672eb58c8b1d0e8db35272677dc17cf55c66ead` 中的独立探针。此库保留其账本格式，但采用游戏合同允许其他请求继续排队的未知状态规则；探针程序保持原有首错停止策略。
