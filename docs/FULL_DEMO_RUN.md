# 完整 Demo 运行与离线验证

本页保留 `full-demo-v1`／`full-demo-v2` 正式评测驱动与历史试玩流程。当前独立 `manual-play-v1` 人工试玩从[Windows 双击入口](REAL_PLAY_LAUNCHER.md)进入；该配置不能用于本页的正式评测驱动。

完整 Demo 驱动使用[跨日评测合同](FULL_DEMO_EVALUATION_CONTRACT.md)和注册输入 [full-demo-input.json](../game/Content/full-demo-input.json)。`fixed` 与 `real` 执行同一顺序：购买种子和饲料、种植浇水、喂鸡、首日窗口、两次 12 小时睡眠、领蛋再喂食、次日窗口中的保存读取，以及依次与 Mina、Eli、Ren、Sora 交谈。

冻结候选 `f9551c19eb1866ba79cc1312ca7821004fbb7fa1` 已通过完整离线门、两组 v2 真实评测及四份真实包的 12 次离线回放，结果见[第二轮报告](verification/full-demo-real-v2-2026-10-11.md)。用户已于 2026-10-11 通过[玩家对话人工验收](verification/player-dialogue-manual.md)，并确认 `576ae64` 候选的[七组综合离线操作及可选真实 M01](https://github.com/qinjunw/CozyTown-Godot/issues/26#issuecomment-6111460458)通过。当前制品渠道与验收证据见[将 Windows CI 制品转存私有 Release 并保留失败诊断](https://github.com/qinjunw/CozyTown-Godot/issues/38)，最终交付状态见[验收并交付完整 Demo Windows 候选](https://github.com/qinjunw/CozyTown-Godot/issues/26)。[旧宿主记录](verification/full-demo-host-2026-10-10.md)和 [v1 未达标结果](verification/full-demo-real-2026-10-11.md)保留原候选与样本归属。

冻结版另有[独立干净构建与四份真实记录的新 Windows 回放](verification/full-demo-clean-2026-10-11.md)。用户已验收修复预算显示的 [576ae64 候选](verification/budget-ui-candidate-2026-10-12.md)；人工反馈、离线门与摘要保护证据保留原归属。回放绑定修复另有[新源码离线复核](verification/replay-outcome-binding-2026-10-12.md)和匹配 Windows 包，[七组操作清单](verification/full-demo-clean-manual.md)作为使用参考保留，不重复整套人工操作，也不将旧人工反馈或真实样本记为新源码通过。

输入版本为 `full-demo-input-v1`。外部输入只允许与已注册命令和参数相同的内容，不能通过修改 JSON 扩大窗口、调用数或改变操作。默认输入使用 Godot 资源路径，源工程和 Windows 导出包都能读取。

用户于 2026-10-11 另行授权修复后的 `full-demo-v2` 一轮评测，现已完成。当前完整固定入口默认绑定 v2，原输入计划保持不变。下述完整门已在冻结候选通过；未来修改模拟源码时须重新冻结和验证。旧 v1 真实包继续使用其归档匹配程序复验，不能用新程序跳过摘要检查。

## 固定客户端验证

在待验证提交的干净检出中，从仓库根目录运行：

```powershell
$fullDemoCommit = git rev-parse HEAD
pwsh -NoProfile -File tools/windows/verify-full-demo.ps1 `
    -ExpectedCommit $fullDemoCommit -ToolsRoot '<toolchain-root>'
```

`ExpectedCommit` 必须为完整的 40 位提交哈希。脚本核对运行前后的提交和未提交文件；Godot 自动重写的已有美术 `.import` 元数据单独允许。其他源码、内容、脚本或文档变动会停止本轮。`ToolsRoot` 指向 [Windows 工具链](../tools/windows/toolchain.lock.json)的安装目录。可选 `-InputFile '<absolute-input.json>'` 显式选择外部输入；脚本保存原文件及 SHA-256，并检查它在运行期间没有改变。

脚本依次执行：

1. `verify.ps1` 的全部自动检查，包括核心、模型、宿主测试，以及源场景和 Windows 导出中的经济、成熟作物、生产、配方与容量检查。
2. `verify-scene-replay.ps1` 的正常及故障包矩阵，验证缺依赖拒绝、实际空间查询和首分歧。
3. 基准和资源对照的完整 `fixed` 运行。两组分别生成保存前、读取后两个分支导出 ZIP。
4. 对上述四个 ZIP 分别执行核心 CLI、源场景 headless 和 Windows 导出 headless，共 12 次同包回放。
5. Windows 导出中的完整基准 `fixed` 运行，不传外部输入参数，检查包内默认资源入口与前两组使用相同计划。

脚本始终使用 headless，不提供图形开关或真实模型配置。Windows 检查将 `DOTNET_ROOT` 指向不存在的目录，限制 `PATH` 为 Windows 系统目录，并核对场景回放实际使用包内运行时。子验证目录由本次进程输出的唯一 `Evidence:` 路径确定，不复用任意旧候选。

输出保存在 ignored `artifacts/full-demo-verification/<run-id>/`。`verification.json` 关联子验证报告、源提交、工具链摘要、输入原文件及规范化计划摘要、模拟源码摘要、Windows 文件哈希、各组窗口和分支，以及回放边界与查询计数。进程日志记录命令、退出码、超时和耗时；核心回放另有时间及内存报告。每个分支保留 ZIP 哈希、压缩体积、完整包体积和父前缀计数。

任何步骤失败立即停止，保留已经生成的报告和记录，最终 `passed` 为 `false`。完整固定运行须有两天四名居民的采用记录、一次晨间、两次睡眠、保存读取分支、四段正确会话的固定模型文本和零 HTTP 发送。回放须核对全部分支边界、每段初态的五个身体查询、所有 motion 查询和实际引擎调用数。固定通过只证明本轮离线行为；真实模型指标及人工画面验收分别记录。

## 时间、窗口与保存

`fixed` 驱动每步接受明确的 `0.05` 秒现实时间输入，其 CPU 执行时间不充当世界时间。`real` 驱动使用单调时钟记录实际经过时间，包括 I/O 与等待；正常世界时速为每现实秒 `0.5` 世界分钟。行走使用普通路线和实际 Godot 碰撞查询，睡眠使用床的普通业务入口。

每个日间窗口达到 180 秒或 18 次普通 NPC 准入时停止新增请求。已有逻辑待办最多收尾 60 秒；恢复占原阶段额度，不占新的普通准入名额。第二天在窗口第 90 秒保存并读取；若窗口先达到停止条件，先停止新增请求，再执行该必需保存读取和收尾。读取不回退驱动游标、窗口时间、准入数、费用或未知槽。未完成必需操作时报告失败。

两次 12 小时睡眠跨一次 05:00 晨间结算。四段玩家文本按冻结的居民顺序生成，对话期间世界暂停；菜单、失焦、读取及故障暂停仍阻止派发和展示。关闭或切换居民终结旧生成，恢复保留原请求内容及阶段。详见[玩家对话合同](PLAYER_DIALOGUE_CONTRACT.md)和[保存续接合同](SAVE_REPLAY_CONTRACT.md)。

## 历史真实评测与 v2 试玩配置

真实模式需要仓库外的显式本机配置；模板见 [full-demo.example.json](../src/CozyTown.Model/full-demo.example.json)。配置包含 `credentialFile`、`ledgerFile`、`expectedSystemFingerprint`、`budgetProfileVersion`，其中路径必须为绝对路径，本次 profile 为 `full-demo-v2`。旧 v1 仍可显式选择，但必须使用自己的账本、世界身份和存档；两个版本互相拒绝串用。凭据、实际路径和真实账本不进入输入包或 Git。

v1 已停止，v2 已显式登记并完成本轮真实评测；核对结果见[预算记录](verification/full-demo-budget-v2-2026-10-11.md)和[第二轮报告](verification/full-demo-real-v2-2026-10-11.md)。当时的试玩复用共享 v2 账本，只能使用尚未花费的 `manual` 八次；不得重新初始化账本或转移旧余量。以下停止与初始化命令仅记录该轮登记流程，不是当前试玩前置步骤。

历史停止操作在旧评测进程退出后执行，并保留独立收据：

```powershell
dotnet run --project tools/CozyTown.Budget -- `
    --stop --profile full-demo-v1 --ledger-file '<existing-v1-absolute-ledger.json>' `
    --reason superseded_by_full_demo_v2
```

成功收据包含 `operation`、`profileVersion` 和 `before`／`after` 完整预算视图；核对尝试、费用、预留和槽位不变，仅增加停止原因。重复停止保留第一次原因，不退还任何占用。收据不包含凭据和本机路径；错误返回非零退出码及 JSON `code`。此操作不发送 HTTP。

历史 v2 初始化是单独的显式操作，在最终候选完整离线门、用户授权及真实执行条件满足后，使用当时尚不存在的唯一位置：

```powershell
dotnet run --project tools/CozyTown.Budget -- `
    --initialize --profile full-demo-v2 --ledger-file '<new-v2-absolute-ledger.json>'
```

父目录须已存在。账本、锁标记或历史文件已经存在时，初始化拒绝覆盖；游戏只打开现有账本。初始化收据的身份须为 `cozytown-godot-full-demo-20261011-v2`，尝试、费用、预留和槽位均为零。离线验证脚本不执行停止或初始化，也不创建本轮真实预算。

本轮已执行的单组真实评测入口为以下形式；保留用于说明记录来源，不授权重复执行：

```text
<godot-console> --headless --path game -- --full-demo-evaluation=real --scenario=baseline --execution-purpose=experiment --model-config=<absolute-local-model.json> --report=<absolute-report.json> --data-dir=<new-absolute-data-directory>
```

资源对照使用 `--scenario=counterfactual`，并引用同一独立预算账本。两组 NPC 新请求分别计入 `baseline`、`counterfactual`，每阶段最多 40 次；两组玩家文本共用 `player_dialogue`，最多 8 次。完整预算总上限人民币 5 元、96 次发送，NPC、玩家及恢复共享两个物理槽。恢复保留冻结请求的原阶段，阶段额度不互借。

历史 v2 用户试玩显式配置该共享账本，并使用单独用途：

```text
<windows-export>/CozyTown.exe -- --model-config=<absolute-local-model.json> --execution-purpose=manual --data-dir=<new-absolute-manual-directory>
```

该用途中的 NPC 与玩家新请求均计入保留的 `manual` 阶段，最多 8 次。实验不能用它补样；普通无配置启动仍使用固定客户端。旧 `first-slice-v1` 配置保持旧预算及玩家真实生成保护，不自动升级为完整 Demo。

`experiment` 与旧 `legacy` 严格核对登记指纹。`manual` 在返回型号、终态、完整用量及登记计价边界有效时，将指纹变化记录为警告，完成原尝试结算；模型文本仍须通过各自业务结构与世界规则。用途由命令参数解析后设置，本机配置不能改变这项策略。到达记录仅保留版本化响应投影，思考字段、无关提供方字段及 HTTP 错误正文不进入日志或导出包；畸形、重复字段和其他失败仍记录具体代码。

已有停止原因、未知费用预留和物理槽不会自动清除。宿主在已知永久停用后让居民明确采用独立规则，玩家生成按钮显示原因并停止提交新的准入；在途回复保留原有接收流程。临时在途占用不锁死后续请求，实际发送仍由持久账本最终核准。

真实评测需另外报告 HTTP 发送、传输、结构解析、合法采用、覆盖、失败和费用。v1 的 82 次预算占用、80 次 HTTP 和失败样本保持原样，未使用的 `manual` 随旧账本停止。新授权只允许 v2 每组执行一轮；仍未达标时保留全部失败，不自动付费重跑或补样。
