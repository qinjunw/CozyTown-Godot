# v2 真实评测证据复算

本目录归属冻结源码 `f9551c19eb1866ba79cc1312ca7821004fbb7fa1` 和预算 `full-demo-v2`／`cozytown-godot-full-demo-20261011-v2`。两组真实指标及真实包的 12 次离线回放通过，完整结果及限制见[评测报告](../../full-demo-real-v2-2026-10-11.md)。两个完整原始归档已逐成员及远端 SHA-256 核对；旧 v1 目录保持原样。

仓库授权账号可在[私人 Release 草稿](https://github.com/qinjunw/CozyTown-Godot/releases/tag/untagged-f311139ec8e4b0da5d59)下载附件，或执行：

```powershell
gh release download full-demo-evaluation-v2-20261011 --repo qinjunw/CozyTown-Godot --dir '<download-directory>'
```

先按 `remote-assets.json` 与两份归档收据核对文件名、长度和 SHA-256，再解压到独立目录。真实 tar 根目录保留 `full-demo-real/20261011-v2/`，因此下文 `<evidence-root>` 是 `<unpack-directory>/full-demo-real/20261011-v2`；158 个原始文件的清单路径相对于该目录。冻结 Windows 离线回放程序位于其中的 `windows-replay-program.zip`，198 个文件须匹配 `windows-manifest.json`。

离线 tar 根目录保留 `artifacts/full-demo-verification/`、`artifacts/verification/`、`artifacts/scene-replay/` 三棵原始目录，以及 `raw-manifest.json` 和 `secret-scan-binding.json`。其清单路径相对于解压根目录；重复 Windows 目录省略，匹配的程序由真实 tar 提供。两个归档分别核对，不将 v1 包混入 v2 复算输入。

`analysis.json` 由既有 `full-demo-real/summarize.py` 生成，脚本 SHA-256 为 `9e69c986c7847164a6dfd5202669c5a1a8ca433622c93525e01539dc5b78439b`。它读取两组完整公开宿主报告和四份 ZIP，核对 ZIP 哈希，递归裁切活动祖先保存前缀，保留全部物理尝试，重建居民资产。`protocol-input.json` 包含全部 71 次预算尝试；公开 parser 通过 69 条，另两条分别是 HTTP 无效 JSON 及 HTTP 前取消的空候选。空候选不算完整响应或结构成功。

协议程序复用 `full-demo-real/ProtocolAudit/Program.cs`，源码 SHA-256 为 `245e49fe290c9788dda5a8678acf36e291436faae8384261b285aaf928d10206`，调用冻结候选的 `TownModelProtocol.Parse` 与 `TownPlayerDialogueProtocol.Parse`。外部分析工程直接编译冻结 Core 源文件及这两个 parser，零警告、零错误，未引用 Model 客户端。下列仓库内工程引用同一 Core 与 parser，可以用于复算。

在冻结 `f9551c1` 的独立检出中执行。`<evidence-root>` 是原始制品中 `full-demo-real/20261011-v2` 的绝对路径，`<output>` 为忽略目录。命令只解析证据，不启动游戏或模型客户端：

```powershell
python docs/verification/evidence/full-demo-real/summarize.py `
  --artifact-root '<evidence-root>' `
  --reports '<evidence-root>/baseline/report.json' '<evidence-root>/counterfactual/report.json' `
  --protocol-input '<output>/protocol-input.json'

& '<locked-dotnet>' run --project docs/verification/evidence/full-demo-real/ProtocolAudit -- `
  '<output>/protocol-input.json' '<output>/protocol-results.json'

python docs/verification/evidence/full-demo-real/summarize.py `
  --artifact-root '<evidence-root>' `
  --reports '<evidence-root>/baseline/report.json' '<evidence-root>/counterfactual/report.json' `
  --protocol-input '<output>/protocol-input.json' `
  --protocol-results '<output>/protocol-results.json' --output '<output>/analysis.json'
```

另核对两组报告的 `budgetProfileVersion` 和 `budgetId` 与 `freeze.json` 中的 v2 身份完全相同；只比较两组彼此相同不足以识别旧实验。规范化输入、源码摘要和构建提交由脚本检查。只有两组均通过、预算和冻结检查均通过，`analysis.passed` 才为 true；此字段不包含执行者另行核对的 12 次回放或人工验收。

合同采用率为采用的真实逻辑请求与可解析逻辑请求的交集数量，除以可解析逻辑请求数。`acceptance-summary.json` 另列相同采用分子除以全部实际 HTTP 的比例：基准 NPC `34/36`，对照 NPC `23/26`，NPC 合计 `57/62`，玩家 `8/8`，总计 `65/70`。该补充比例保持无效 JSON 在分母中。恢复是独立物理尝试；逻辑采用只计一次。所有 `worldOutcomes` 按逻辑 ID 关联，不能理解为同逻辑的每个物理尝试各执行一次。

`rejected-samples.json` 是从完整统计派生的六条例外：四条已解析未采用、一条无效 JSON、一条 HTTP 前取消。`invalid-json-arrival.json` 保存失败 attempt 的原始提供方到达正文，按顶层 `kind=model_response_arrived` 与 `data.dispatch.attemptId` 精确提取；候选缺少末尾对象闭合符。该条保留计费、HTTP 及结构失败，不以规则回退补入模型成功。

共享预算按结束快照的唯一 attempt ID 集合统计，早期快照必须是最终集合的子集；不能把两个累计快照的费用相加。现实账本不作为本目录分析工具的输入。`budget-final-verification.json` 仅保存唯一执行者核对公开快照与现实账本的结果，以及 v1 停止后 SHA 未变的结果。

本目录 `.gitattributes` 对 JSON 关闭行尾转换，保留各文件的原字节哈希，包括生成时的 CRLF；Git 空白检查按 CR 行尾处理。统计程序输出 UTF-8 LF，协议结果及原始收据保持各自生成字节。完整原始包保留请求、响应、事件、保存及父前缀；本目录的统计摘要不替代完整包。

`offline-summary.json` 保留付费前最终候选的完整门检查与子门摘要，机器路径替换为占位符；其中 `rawReportSha256` 指向未改写的原报告，`windows-manifest.json` 是冻结 Windows 文件清单原件。故障矩阵的通过表示实际拒绝或通过结果符合每包预期，不表示故障包都被接受。固定门的 12 次回放与后续四份真实包的 12 次回放分别记录。

`replay-summary.json` 是四份真实包的完整回放汇总摘要，保留原汇总哈希、12 份原报告哈希、实际碰撞调用、父前缀边界和全部场景依赖。机器路径换为占位符，原始报告保留在关联制品。`independent-acceptance-audit.json` 保存另一审计者对公开原始记录及统计的独立核对结果，未检查私密账本。

`offline-archive/` 原样保存离线归档的收据、926 文件清单和实际凭据扫描绑定；该目录关闭 JSON 行尾转换。收据核对 928 个成员的长度与哈希。`archive-receipt.json` 和 `raw-manifest.json` 对应 159 成员的真实归档；`secret-scan.json` 的哈希由该清单绑定。`remote-assets.json` 记录两归档和全部 15 附件的远端长度、SHA-256 及核验结果，Release 保持 draft。
