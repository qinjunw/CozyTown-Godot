# 完整 Demo 运行与离线验证

完整 Demo 驱动使用[跨日评测合同](FULL_DEMO_EVALUATION_CONTRACT.md)和注册输入 [full-demo-input.json](../game/Content/full-demo-input.json)。`fixed` 与 `real` 执行同一顺序：购买种子和饲料、种植浇水、喂鸡、首日窗口、两次 12 小时睡眠、领蛋再喂食、次日窗口中的保存读取，以及依次与 Mina、Eli、Ren、Sora 交谈。

代码候选 `71be140d24ade8e78d96ab68113221dfa760e08e` 已通过完整离线门，结果与完整包哈希见[宿主验证记录](verification/full-demo-host-2026-10-10.md)。用户已于 2026-10-11 通过[玩家对话人工验收](verification/player-dialogue-manual.md)，真实评测仍待执行。

输入版本为 `full-demo-input-v1`。外部输入只允许与已注册命令和参数相同的内容，不能通过修改 JSON 扩大窗口、调用数或改变操作。默认输入使用 Godot 资源路径，源工程和 Windows 导出包都能读取。

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

## 真实评测与最终试玩配置

真实模式需要仓库外的显式本机配置；模板见 [full-demo.example.json](../src/CozyTown.Model/full-demo.example.json)。配置包含 `credentialFile`、`ledgerFile`、`expectedSystemFingerprint`、`budgetProfileVersion`，其中路径必须为绝对路径，完整 Demo profile 必须为 `full-demo-v1`。凭据、实际路径和真实账本不进入输入包或 Git。

独立账本的创建是单独的显式操作。仅在离线门、人工验收和真实执行条件满足后，使用一个尚不存在的路径：

```powershell
dotnet run --project tools/CozyTown.Budget -- `
    --initialize --profile full-demo-v1 --ledger-file '<new-absolute-ledger.json>'
```

父目录须已存在。账本、锁标记或历史文件已经存在时，初始化拒绝覆盖；游戏只打开现有账本。离线验证脚本不执行该命令，也不创建本轮真实预算。

完成冻结后，单组真实评测入口为：

```text
<godot-console> --headless --path game -- --full-demo-evaluation=real --scenario=baseline --execution-purpose=experiment --model-config=<absolute-local-model.json> --report=<absolute-report.json> --data-dir=<new-absolute-data-directory>
```

资源对照使用 `--scenario=counterfactual`，并引用同一独立预算账本。两组 NPC 新请求分别计入 `baseline`、`counterfactual`，每阶段最多 40 次；两组玩家文本共用 `player_dialogue`，最多 8 次。完整预算总上限人民币 5 元、96 次发送，NPC、玩家及恢复共享两个物理槽。恢复保留冻结请求的原阶段，阶段额度不互借。

最终用户真实试玩使用单独用途：

```text
<windows-export>/CozyTown.exe -- --model-config=<absolute-local-model.json> --execution-purpose=manual --data-dir=<new-absolute-manual-directory>
```

该用途中的 NPC 与玩家新请求均计入保留的 `manual` 阶段，最多 8 次。实验不能用它补样；普通无配置启动仍使用固定客户端。旧 `first-slice-v1` 配置保持旧预算及玩家真实生成保护，不自动升级为完整 Demo。

真实评测需另外报告 HTTP 发送、传输、结构解析、合法采用、覆盖、失败和费用。未达到合同标准时保留原候选及全部失败，不自动付费重跑或补样。本文提供操作入口，不声明已初始化真实账本或通过真实模型评测。
