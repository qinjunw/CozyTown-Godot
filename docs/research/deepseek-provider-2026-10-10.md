# DeepSeek 供方事实与预算预留依据

核验日期：2026-10-10。来源为当日读取的 DeepSeek 官方公开文档；页面未提供可固定的发布版本。未读取凭据、调用模型、查询账户或发生付费发送。本文更新供方证据；[2026-09-20 笔记](deepseek-provider-2026-09-20.md)保留为历史记录。

## 当前模型与请求参数

官方要求使用 `deepseek-flash`，价格表对应版本为 `DeepSeek-V4.1-Flash`。旧名 `deepseek-v4-flash` 与 `deepseek-v4-flash-vision-exp` 仍被接受，但原模型已下线，调用转由 V4.1 Flash 服务并按 Flash 计费。模型别名不能固定远端版本。[模型与价格](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)

`POST https://api.deepseek.com/chat/completions` 接受以下参数；这是文档支持状态，本次没有验证实际响应。[Chat Completions API](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion/)

| 参数 | 本轮拟用值与依据 |
| --- | --- |
| `model` | `deepseek-flash`，为允许的模型 ID |
| `thinking` | `{"type":"disabled"}`，显式关闭默认开启的思考模式 |
| `response_format` | `{"type":"json_object"}`，启用 JSON 模式 |
| `max_tokens` | `512`，处于允许范围 `1–393216` 内 |
| `stream` | `false`，接收完整响应以核对用量 |

使用 OpenAI SDK 时，`thinking` 须放入 `extra_body`。应保存实际发送参数，不能只保存客户端预设。[思考模式](https://api-docs.deepseek.com/guides/thinking_mode/)

JSON 指南要求 system 或 user 提示包含 `json` 并给出目标格式示例，同时提醒可能出现空内容或因输出限额被截断。`512` 是允许的上限配置，不能保证业务 JSON 一定完整；接收端仍需检查结束原因、解析结果、业务字段与行动合法性。[JSON Output](https://api-docs.deepseek.com/guides/json_mode/)

## 人民币费率

下表单位为人民币元／百万 token。峰时为北京时间周一至周五 `09:00–12:00`、`14:00–18:00`，不含中国法定节假日；其余时段为空闲。官方保留调价权。[人民币价格与扣费规则](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)

| 计价项 | 高峰 | 空闲 |
| --- | ---: | ---: |
| 输入缓存命中 | 0.04 | 0.02 |
| 输入缓存未命中 | 2 | 1 |
| 输出 | 8 | 4 |

缓存默认开启，但官方说明命中为尽力提供，不能假定命中。预留采用全部输入未命中及峰时价格。[上下文硬盘缓存](https://api-docs.deepseek.com/zh-cn/guides/kv_cache/)

## 1M 的确切容量与 ¥2.11 预留

官方模型列表文档的具体示例给出 `deepseek-flash` 的 `name: DeepSeek-V4.1-Flash`、`context_window: 1048576`、`max_output_tokens: 393216`；schema 明确定义 `context_window` 为输入与输出合计容量。因此当前公开证据支持将价格表的 `1M` 解释为 **1,048,576 token 总上下文**。这是公开文档示例，尚未通过当前账户的 `GET /models` 实测。[Lists Models](https://api-docs.deepseek.com/api/list-models/)

以下为应用预算推导，前提是当前公开模型容量、输出上限及费率有效，且供应商按文档用量计费：

```text
输入预留 P = 1,048,576 token
输出预留 O = 512 token
预留 = (P × 2 + O × 8) / 1,000,000
     = 2.101248 CNY
向上取整 = 2.11 CNY／每次物理发送
```

该公式将总上下文容量全部算作输入后，再额外加入 512 输出，故比遵守输入加输出总容量约束的计算更宽。它是应用的保守预留依据；上述规格和费率不足以证明供方账户存在硬封顶。字符或 UTF-8 字节限制可约束应用载荷，但官方字符/token 换算只是近似，实际 token 数以返回 `usage` 为准，不能由字符比例证明更小的费用上界。[Token 用量计算](https://api-docs.deepseek.com/zh-cn/quick_start/token_usage/)

本轮用户批准的上限为 **¥5 与 96 次物理发送，先到者停止**；阶段配额为 `40 baseline + 40 counterfactual + 8 player_dialogue + 8 manual`，两组各运行相邻两天、跨一次晨间。上述实验安排来自本轮任务约束，供方资料不验证其游戏效果或调用成功率。旧 48 次实验账本须保留，新轮次另记账。

按每次 ¥2.11 预留，两个未结算请求占用 ¥4.22，第三个将需 ¥6.33，超过 ¥5。完整、自洽的用量到达后才可按峰时价结算并释放差额；96 次是数量上限，并非 ¥5 能保证完成的次数。每次重试或恢复补发都应另占预留和发送名额。超时、断连、进程退出或缺失用量时保留未知占用；预算条件不满足即停止发送。该规则是应用预算建议，不是远端停费保证。

## 响应中可核对的事实

Chat Completions schema 提供 `id`、返回 `model`、`system_fingerprint`、结束原因与 `usage`；`system_fingerprint` 表示后端配置。可检查 `prompt_tokens = prompt_cache_hit_tokens + prompt_cache_miss_tokens`、`total_tokens = prompt_tokens + completion_tokens`，以及 `prompt_tokens_details.cached_tokens` 与命中量一致。流式请求的全量用量位于 `[DONE]` 前最后一个块。[响应 schema 与示例](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion/)

返回 `model` 为字符串，官方示例为 `deepseek-flash`；未见返回名称完整枚举、必与请求名称相同或 fingerprint 可映射到固定权重版本的保证。实际调用时应记录并比对返回名和 fingerprint；本次没有可报告的实测值。[返回型号与 fingerprint](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion/)

按 `(命中量 × 0.04 + 未命中量 × 2 + 输出量 × 8) / 1,000,000` 可估算供方峰时费用；应用账本仍将全部输入按未命中价 2 计算保守扣减，不预支缓存折扣。`usage` 没有实际扣款、币种或适用峰闲费率字段，因此上述估算不能称为已对账的供方扣款。跨峰闲边界时采用哪个时刻计价，本次未核实。[用量字段](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion/)、[扣费规则](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)

## 取消、请求查询与幂等未知项

官方仅把 `finish_reason: aborted` 解释为生成被中断；它不构成“本地取消、HTTP 断连或超时必然停止远端推理及计费”的保证。[结束原因](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion/)

本次检查 Chat Completions、Responses API、兼容性表、限速与保活说明，并在官方文档域检索 `idempotency`、`disconnect`、`cancel`，未核实以下能力：按请求 ID 查询最终状态与费用；幂等键保证重复发送去重或只收费一次；客户端断连后的远端停费。应保留为未知，不能将补发视为免费。

Responses API 明确不存储响应或会话；兼容性表也标注 `previous_response_id`、`conversation`、`store`、`background` 不支持。返回对象中的 `status` 只是该响应字段，不能据此推定存在后续查询接口。[Responses API](https://api-docs.deepseek.com/zh-cn/api/create-response/)、[兼容性表](https://api-docs.deepseek.com/guides/responses_api/)

保活文档仅说明：请求尚未开始推理而等待超过 10 分钟时服务器关闭连接。这不覆盖客户端自行断连后的计费结果。[限速与保活](https://api-docs.deepseek.com/zh-cn/quick_start/rate_limit/)

这些未核实项要求应用保留未知费用占用并限制物理发送；它们不证明服务必然继续收费，也不证明请求状态查询或幂等能力永远不存在。若模型、容量、计费币种或费率改变，应暂停真实发送并重新核验预算依据。
