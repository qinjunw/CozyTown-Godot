# DeepSeek 接口探针实测

2026-09-20 04:38:34–04:38:36（UTC+8），使用提交 [`a672eb58c8b1d0e8db35272677dc17cf55c66ead`](https://github.com/qinjunw/CozyTown-Godot/commit/a672eb58c8b1d0e8db35272677dc17cf55c66ead)中的独立 C# 探针执行一次实验。调用前已登记[配置与预算](real-model-probe-plan-2026-09-20.md)，每份输入都是人工编写的居民状态与可见对象，没有接入游戏世界。

## 实测结果

先执行一次只读余额查询，返回 HTTP 200、账户可用、币种 CNY；未保存余额。随后顺序执行 4 次 Chat Completions，无重试、失败或中断。请求与返回模型均为 `deepseek-flash`，四份响应的系统指纹均为 `aeb56401ca74e127821c4f9126dcb669`，结束原因为 `stop`。

| 合成居民 | HTTP | 候选 | 输入 token | 输出 token | 完整响应耗时 | 保守预算扣减 |
| --- | ---: | --- | ---: | ---: | ---: | ---: |
| Mina | 200 | wait | 194 | 21 | 571 ms | CNY 0.000556 |
| Eli | 200 | wait | 194 | 20 | 585 ms | CNY 0.000548 |
| Ren | 200 | wait | 192 | 20 | 553 ms | CNY 0.000544 |
| Sora | 200 | wait | 194 | 22 | 375 ms | CNY 0.000564 |
| 合计 | — | 4 份合法候选 | 774 | 83 | — | CNY 0.002212 |

四份响应通过 JSON、操作枚举、目标及文本限制检查，报告缓存命中均为零。耗时只覆盖此短输入的单次传输与完整响应读取，不能推导场景性能或长期服务可靠率。

全部候选选择等待。提示中的休息状态和等待示例可能影响选择，本轮没有比较提示设计或测量因果影响；未观察到接近、邀请或交换。候选中的“水边”等描述也不建立空间或经历事实。本轮通过的是接口与候选结构验证，不是四人同场、自主社交或行动执行验收。

## 预算与证据

共用预算 `cozytown-godot-first-slice-20260920-v1` 已用 4/48 次，探针阶段 4/4 次，基准与对照阶段各保留 22 次上限。按全部输入未命中峰时价扣减 CNY 0.002212，剩余 CNY 4.997788；没有未结算费用或未知在途槽。该金额是应用保守扣减，未核对提供方账户实际扣款。

[完整报告与预算快照](evidence/real-model-probe-2026-09-20/report.json)保留全部 4 份样本、候选正文、usage、请求身份、时间与指纹。归档文件的外层 JSON 换行统一为 LF，JSON 值不变；[文件清单](evidence/real-model-probe-2026-09-20/manifest.json)分别给出 10 份原输出与归档文件的 SHA-256。对应请求和结果：

| 样本 | 完整请求 | 非秘密结果 |
| --- | --- | --- |
| Mina | [输入](evidence/real-model-probe-2026-09-20/01-request.json) | [结果](evidence/real-model-probe-2026-09-20/01-result.json) |
| Eli | [输入](evidence/real-model-probe-2026-09-20/02-request.json) | [结果](evidence/real-model-probe-2026-09-20/02-result.json) |
| Ren | [输入](evidence/real-model-probe-2026-09-20/03-request.json) | [结果](evidence/real-model-probe-2026-09-20/03-result.json) |
| Sora | [输入](evidence/real-model-probe-2026-09-20/04-request.json) | [结果](evidence/real-model-probe-2026-09-20/04-result.json) |

证据不含密钥、认证头、余额数值或思考字段。提交的是历史预算快照；实时账本及锁标记保留在忽略的本机实验目录，路径由本机配置登记。后续场景必须延续实时账本和已发生次数，不得用此快照回滚或新建账本刷新额度。

## 离线验证与后续条件

同一源版本的 Release 构建为 0 警告、0 错误，21 组离线检查通过，离线检查没有远端请求。检查覆盖持久占额、总量与阶段上限、金额上限、重复结算、未知占用、恢复额度、独占文件句柄、账本丢失、假凭据读取、候选约束和脱敏，以及固定 HTTP 响应下的成功、网关错误、缺失 usage、截断、型号/指纹变化、币种不符和非法动作。执行命令见[探针说明](../../tools/experiments/CozyTown.ModelProbe/README.md)。

独立代码审查发现的型号核验顺序和嵌套 JSON 脱敏问题均在真实调用前修复。文件句柄排斥检查不等于已完成游戏的两进程竞争恢复测试；队列、公平性、保存恢复、迟到结果、世界行动和场景故障验收仍由[真实模型接入合同](../REAL_MODEL_CONTRACT.md)与[首切片合同](../FIRST_PLAYABLE_SLICE.md)约束。

后续[用垂直切片验证 Godot 场景、像素交互与调试体验](https://github.com/qinjunw/CozyTown-Godot/issues/7)须交付实际世界中的两组四人实验、规则失败样本、Windows 操作及用户画面反馈。接口验证结果不能替代这些条件。
