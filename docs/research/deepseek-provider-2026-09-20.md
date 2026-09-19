# DeepSeek 接入事实与预算边界

核验日期：2026-09-20。范围为官方公开文档及文档直接链接的 tokenizer 包；未读取密钥、未调用模型、未查询账户。本文为接入合同的研究依据，不代表服务连通或四人模型路径已验收。

## 模型与请求

官方目前推荐 `deepseek-flash`，对应 DeepSeek-V4.1-Flash。Unity 参考代理使用的 `deepseek-v4-flash` 仍被接受，但原模型已下线，旧名称临时路由到 V4.1 Flash。因此固定代理提交和请求名称不能固定远端模型版本。[现行模型表](https://api-docs.deepseek.com/quick_start/pricing/)、[2026-09-10 更新记录](https://api-docs.deepseek.com/updates/)

`POST https://api.deepseek.com/chat/completions` 仍是官方接口。`max_tokens` 限制生成 token 数，范围为 1–393216；输入与输出总长还受模型上下文限制。当前表列上下文为 1M。`max_tokens: 512` 在允许范围内。[Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/)、[模型表](https://api-docs.deepseek.com/quick_start/pricing/)

非思考模式须显式发送 `thinking: {"type":"disabled"}`，因为默认启用思考；使用 OpenAI SDK 时该字段放在 `extra_body` 中。接入合同应保留实际请求参数，避免 SDK 默认值改变模式。[思考模式](https://api-docs.deepseek.com/guides/thinking_mode/)

## 价格与用量

下表均为每百万 token 的 Flash 标价，分别抄录官方人民币和美元表；两套价格不能由客户端自行按汇率互换。[人民币价格](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)、[美元价格](https://api-docs.deepseek.com/quick_start/pricing/)

| 计价项 | CNY 高峰 | CNY 空闲 | USD 高峰 | USD 空闲 |
| --- | ---: | ---: | ---: | ---: |
| 输入缓存命中 | 0.04 | 0.02 | 0.006 | 0.003 |
| 输入缓存未命中 | 2 | 1 | 0.30 | 0.15 |
| 输出 | 8 | 4 | 1.20 | 0.60 |

高峰为北京时间周一至周五 09:00–12:00、14:00–18:00，不含中国法定节假日；其余时段为空闲。官方保留调价权。预算配置须记录币种、费率和核验日期。账户余额接口区分 `CNY` 与 `USD`，本次未查询实际账户币种。[价格规则](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)、[余额接口](https://api-docs.deepseek.com/api/get-user-balance/)

缓存默认启用，但命中属于尽力提供；预留不能假定必然命中。相同输入也不保证相同模型输出。[上下文缓存](https://api-docs.deepseek.com/guides/kv_cache/)

完整响应提供 `id`、`model`、`system_fingerprint` 及 `usage`。其中 `prompt_tokens = prompt_cache_hit_tokens + prompt_cache_miss_tokens`，`completion_tokens` 为生成用量，`total_tokens` 为输入与输出之和。流式请求的最终块才携带总用量。文档中的 `usage` 没有扣款金额、币种或实际适用的峰闲费率字段。[响应字段](https://api-docs.deepseek.com/api/create-chat-completion/)

响应 `model` 的 schema 是字符串，官方示例为 `deepseek-flash`；文档没有给出完整枚举，也没有承诺始终等于请求名称。实验可将该示例值登记为本轮预期，并在不符时停止核验，但不能据此称其他返回名违反官方协议。[响应定义与示例](https://api-docs.deepseek.com/api/create-chat-completion/)

因此，`命中量 × 命中价 + 未命中量 × 未命中价 + 输出量 × 输出价` 是按用量和选定价格计算的金额。应用可始终用峰时价保守扣减预算，但不能把该计算值称为已核对的供应商实际扣款。本次未找到跨峰闲边界请求的计价时刻保证；准确账单需另行核对。

## JSON 与失败边界

`response_format: {"type":"json_object"}` 要求提示中出现 `json` 并给出格式示例；官方仍提示可能返回空内容，输出限额过低会截断 JSON。这不是业务 schema 或行动合法性保证，接收端须检查结束原因、JSON 解析、必填字段及行动授权，失败走明确回退。[JSON Output](https://api-docs.deepseek.com/guides/json_mode/)

Chat Completions 文档列有 `finish_reason: aborted`，仅说明生成被中断。本次检查接口定义、导航和官方站内检索后，未找到以下保证：HTTP 断连必定终止推理或计费；按请求 ID 查询完成状态与费用的接口；幂等键对重复发送去重收费的协议。结论是这些能力尚未核实，不能推定取消成功或补发免费。[Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/)

另一个 Responses 接口明确不在服务端存储响应和会话；改用该接口也不能据此获得持久请求查询。OpenAI 格式兼容不等于继承其全部服务能力。[Responses API](https://api-docs.deepseek.com/api/create-response/)

## 输入计数与保守预留

官方将字符与 token 的换算标为近似，实际用量以返回值为准，并提供离线 tokenizer。[Token 用量](https://api-docs.deepseek.com/quick_start/token_usage/)

本次在内存检查该页链接的 [tokenizer ZIP](https://cdn.deepseek.com/api-docs/deepseek_v4_tokenizer.zip)：示例只调用 `encode("Hello!")`，包中含 tokenizer 和聊天模板，配置的 `model_max_length` 为 16384。它不是当前 API 的 1M 上下文上限声明；本次未找到该模板与现行 V4.1 API 完整提示封装、隐藏开销完全一致的保证，也未找到 Chat Completions 的预检计数接口。完整提示文本限为 16 KiB 可以约束应用输入，但现有证据不足以由此证明小额计费上界。

可执行的保守方案是每次物理发送都按全上下文容量、全部输入未命中、峰时价预留。以下是从公布规格推导的应用预算计算，不是供应商提供的账户硬封顶：

```text
P = 1,048,576       # 覆盖文档 1M 的十进制及二进制解释，作为输入预留
O = 512            # 显式 max_tokens，thinking disabled
预留 = (P × 输入未命中峰时单价 + O × 输出峰时单价) / 1,000,000
CNY 预留 = (1,048,576 × 2 + 512 × 8) / 1,000,000
         = 2.101248 元，可向上取整为 2.11 元/次
```

该计算有意将整个上下文作为输入预留后再加输出，留出余量。它以当前公布的模型容量、输出限制和价格有效为前提；它不控制同账户其他程序的花费，也不能锁住未来价格。协议应在模型、计费币种或费率改变时暂停发送并重新核验，而非继续套用旧额度。

应用账本在发送前原子占用预留；所有重试和恢复补发分别占用。完整且自洽的 `usage` 到达后，可按峰时价计算预算扣减并释放多余预留；缺少用量、超时、断连或进程退出时保留未知占用。结束原因或业务 JSON 失败不等于免费。若用量超过假设或出现无法解释的计费差异，应停止后续真实调用并记录差异。此策略延续[存档合同的独立账本与未知费用规则](../SAVE_REPLAY_CONTRACT.md)，具体总额度及实验次数由接入合同决定。
