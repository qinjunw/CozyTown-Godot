# 真实评测证据复算

本目录记录 2026-10-11 冻结候选的失败结果。`analysis.json` 的 `passed=false` 来自资源对照的合法采用率；完整指标及限制见[评测报告](../../full-demo-real-2026-10-11.md)。

`summarize.py` 只读取宿主公开报告和导出 ZIP。它核对 ZIP 哈希，裁切活跃父前缀，保留全部物理尝试，重建居民资产，并生成每次尝试的协议审计输入。`ProtocolAudit` 链接该检出的两个既有公开协议 Parse；它不引用模型客户端、凭据读取器或账本。复算应使用冻结源提交，后续协议修改不能覆盖本轮结果。

从仓库根目录执行；`<evidence-root>` 为关联原始制品中 `full-demo-real/20261011` 的绝对路径，所有 `<output>` 路径均置于忽略目录。命令不启动游戏或发送 HTTP。

仓库授权用户可通过 `gh release download full-demo-evaluation-20261011 --repo qinjunw/CozyTown-Godot --dir '<download-directory>'` 获取归档，先按 [archive-receipt.json](archive-receipt.json) 验证体积与 SHA-256，再解压并按 [raw-manifest.json](raw-manifest.json) 核对原始文件。Windows 回放程序为归档中的 `windows-replay-program.zip`，只用于冻结候选的离线回放。

原始 manifest 和归档收据保留上传时的字节，包括行尾；本目录的 `.gitattributes` 只对这两个文件关闭行尾转换，使本地哈希与远端资产 digest 一致。

```powershell
python docs/verification/evidence/full-demo-real/summarize.py `
  --artifact-root '<evidence-root>' `
  --reports '<evidence-root>/baseline-report.json' '<evidence-root>/counterfactual-report.json' `
  --protocol-input '<output>/protocol-input.json'

& '<locked-dotnet>' run --project docs/verification/evidence/full-demo-real/ProtocolAudit -- `
  '<output>/protocol-input.json' '<output>/protocol-results.json'

python docs/verification/evidence/full-demo-real/summarize.py `
  --artifact-root '<evidence-root>' `
  --reports '<evidence-root>/baseline-report.json' '<evidence-root>/counterfactual-report.json' `
  --protocol-input '<output>/protocol-input.json' `
  --protocol-results '<output>/protocol-results.json' --output '<output>/analysis.json'
```

每组 `model_dispatched` 记录 41 次预算占用，但只有 40 次完成记录含 `dispatchedAtUtc`。预发送记录不能独立证明 HTTP。脚本保留派发前取消及派发不确定项；本轮没有缺完成记录的不确定派发。结构成功要求完整响应、`finishReason=stop` 和公开协议解析成功，空内容或截断不能补入成功。

世界采用按逻辑 ID 去重；同一逻辑 ID 的 `worldOutcomes` 列表关联它的全部活跃世界结果，不能解释成每个物理尝试各执行一次。玩家显示与居民/会话/逻辑请求及冻结身份逐项绑定。玩家经历按显示 ID 去重，居民经历按该居民的 `(sequence, source, kind, text)` 去重，不把两个居民共有的经历算作重复。

脚本已用前一轮固定包复算，得到基准 33 次规则采用、7 次交付，对照 34 次规则采用、10 次交付；各四段固定文本、零 HTTP，逐人资产全部匹配。该核对用于检查证据口径，没有被计入真实样本。真实解析审计的 82 条输入中 80 条通过；另外两条为 HTTP 前取消、候选为空，不进入完整响应或结构解析分母。

共享预算使用结束快照中的唯一 attemptId 集合，先前快照必须是其子集；不把两个累计快照的费用求和。实际私密账本不在复算输入中。`freeze.json`、`protocol-results.json`、`analysis.json` 与 `rejected-samples.json` 均不包含凭据或机器私密路径。

`replay-summary.json` 是完整原始回放汇总的摘要，保留原汇总哈希、12 次检查、四包输入哈希及实际查询计数，机器路径替换为占位符。原报告、日志、Core metrics 与启动脚本位于关联制品的 `replay/`。回放没有启动模型客户端；零 HTTP 的依据为隔离入口合同与保留命令，未进行网络抓包。真实采用率失败不会因 12 次回放通过而改成通过。
