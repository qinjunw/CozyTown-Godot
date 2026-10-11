# 完整 Demo 第二轮真实评测（v2）

对应[执行完整 Demo 固定输入、回放与跨日真实评测](https://github.com/qinjunw/CozyTown-Godot/issues/25)。基准和资源对照各执行一次，两组真实指标均达到[已批准合同](../FULL_DEMO_EVALUATION_CONTRACT.md)，四份真实包的 12 次离线回放通过。离线及真实原始归档已在私人 Release 草稿封存，远端长度与 SHA-256 核对通过。第一轮[未达标结果](full-demo-real-2026-10-11.md)及其全部样本保持原归属；最终 Windows 人工及 CI 制品交付验收仍待完成。

## 冻结输入与范围

源候选为 `f9551c19eb1866ba79cc1312ca7821004fbb7fa1`，模拟源码摘要为 `bb54adc96757808ef36220c46c85539635faa244c92744b9a19bb9277df946c6`。工具链为 Godot `4.7.2.stable.mono.official.ed1daf0bf` 和 .NET SDK `10.0.401`。[冻结配置](evidence/full-demo-real-v2/freeze.json)保存源码、内容、预算、工具链及付费前离线门摘要；原文件 SHA-256 为 `6a93f64f6d10496b24426a208acbf6e904482c8a8c7b64c37cca739348889452`。

付费前同一候选通过[完整离线门](evidence/full-demo-real-v2/offline-summary.json)：Core 324/324、Model 92/92、Host 251/251，共 667 个单元测试，无失败或跳过；33 个业务进程、13 包三边界的 39 项故障矩阵预期检查、两个固定场景四包的 12 次同包回放及导出默认输入检查均通过。门禁记录零真实 HTTP、未请求画面验收。冻结 [Windows 清单](evidence/full-demo-real-v2/windows-manifest.json)包含 198 个文件、197,400,770 字节。原始完整门报告 SHA-256 为 `713793834b40915056cbbf46a53fb56cb7cb4188c20584dfcaf7897e08375163`；本目录摘要把机器路径替换为占位符，原报告归入关联制品。

输入计划为 `full-demo-input-v1`，文件 SHA-256 为 `8665d50350cdc5de71b042ff9a49ce0bd56c3a888f3ede453d1c5ecb7e8d15e6`，规范化输入摘要为 `b543a4405f749313044eae707f61a52f0bc9e1f1802297746b067e48ea17f4fe`。两组地图、日程、人格、提示词、生产规则及正常世界时速相同；对照只改变居民初始资源分布，初始总资源保持。四项对照检查和输入、源码、构建检查均通过。

真实服务为 `deepseek-flash`，关闭思考、非流式 JSON object、最多 512 输出 token。费用沿冻结登记的峰时输入全部未命中 CNY 2/百万 token、输出 CNY 8/百万 token 保守计算，表示预算扣减，不是提供方发票。

每组完成 21 个命令，贯穿普通购买、种植浇水、喂鸡、两个日间窗口、两次 12 小时睡眠、一次晨间、领蛋再喂食、保存读取及四人玩家对话。基准接受模拟现实时间 250.445 秒、进程墙钟 271.214 秒；对照分别为 395.794 秒、420.535 秒。两组进程退出 0；宿主流程完成与本次逐样本真实判定分别记录。

## 真实指标与覆盖

统计使用完整宿主报告及四份 ZIP，公开解析器编译自冻结 `f9551c1`。传输分母为已确认的实际 HTTP，结构分母为完整生成响应。合同合法采用按可解析逻辑请求去重；另列全部 HTTP 的采用比例，保留无效 JSON 对结果的影响。[完整统计](evidence/full-demo-real-v2/analysis.json)与[并列分母摘要](evidence/full-demo-real-v2/acceptance-summary.json)可追溯到同一 attempt 和逻辑请求。

| 指标 | 基准 NPC | 对照 NPC | 两组玩家 | 全部合计 |
| --- | ---: | ---: | ---: | ---: |
| 完整响应 / HTTP | 36/36 | 26/26 | 8/8 | 70/70 |
| 结构成功 / 完整响应 | 36/36 | 25/26 | 8/8 | 69/70 |
| 真实采用 / 可解析逻辑请求 | 34/36 | 23/25 | 8/8 | 65/69 |
| 合同合法采用率 | 94.44% | 92% | 100% | 94.20% |
| 真实采用 / 全部 HTTP | 34/36 | 23/26 | 8/8 | 65/70 |
| 全部 HTTP 的采用比例 | 94.44% | 88.46% | 100% | 92.86% |
| 规则回退事件 | 0 | 1 | 0 | 1 |

NPC 合计传输为 `62/62 = 100%`，结构为 `61/62 = 98.39%`，合同采用为 `57/61 = 93.44%`，全部 HTTP 的采用比例为 `57/62 = 91.94%`。两组分别的 NPC、玩家及合计均达到传输和结构 90%、合同采用 80% 的门槛。规则回退不算真实采用。

| 场景 / 窗口 | Mina | Eli | Ren | Sora | 窗口秒数 / 普通准入 | 停止原因 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 基准第 1 天 | 4 | 5 | 4 | 5 | 113.667 / 18 | 普通准入上限 |
| 基准第 2 天 | 2 | 2 | 6 | 6 | 84.829 / 18 | 普通准入上限 |
| 对照第 1 天 | 3 | 3 | 2 | 3 | 180 / 12 | 时长上限 |
| 对照第 2 天 | 4 | 4 | 2 | 2 | 180 / 14 | 时长上限 |

四个窗口中每人至少采用一次，宿主覆盖与世界事件重算一致；在途收尾均少于 60 秒。对照达到时长上限后未补足 18 次，也未补样。两组各四人的玩家文本在正确居民、逻辑请求和会话中展示，合计八段；固定欢迎文本不计入真实样本。

## 全部失败、资源与恢复

[六条例外记录](evidence/full-demo-real-v2/rejected-samples.json)包括四条已解析但未采用的逻辑请求、一条无效 JSON HTTP 响应，以及一条发送前退役尝试；未删除失败样本或增加真实运行。

| 场景 / 逻辑请求末尾 | 居民 | 结果 |
| --- | --- | --- |
| 基准 `decision:26`、`decision:41` | Sora | 等待 invite 回复时收到邀请；记录 `invitation_received` 终结及 `activity_changed` 拒绝 |
| 对照 `decision:1` | Eli | HTTP 200、完整生成且 `finishReason=stop`，候选 JSON 缺少对象闭合符；`invalid_json`，规则回退一次 |
| 对照 `decision:17` | Mina | 等待 approach 回复时收到邀请；`invitation_received` 终结及 `activity_changed` 拒绝 |
| 对照 `decision:28` | Sora | 日程边界已终结待办，迟到回复以 `terminal_request` 拒绝 |

无效 JSON 的[原始到达正文](evidence/full-demo-real-v2/invalid-json-arrival.json)来自完整父包的 `model_response_arrived`，按 attempt ID 精确匹配；提供方 content 在 text 结束引号处结束，缺少 `}`。宿主完成记录的 `candidateJson=null` 是非法候选过滤结果；该响应保留在 HTTP、传输及结构分母中，不计可解析候选或真实采用。其保守扣减为 CNY 0.003628。

基准 `decision:46` 的原尝试在 HTTP 前以 `host_request_retired_before_send` 退役，结算为零但预算次数保留。恢复保留原逻辑 ID、阶段及冻结正文，成为该逻辑请求唯一的 HTTP，respond 仅采用一次；旧会话另有一次 `retired_session` 拒绝。对照未发生恢复发送，不据此宣称对照真实恢复路径已覆盖。

基准与对照分别记录 8、6 条 `candidate_rejected` 事件，包含拒绝邀请、活动终结、日程边界等正常终止；同一逻辑请求可关联多个事件，不能相加成 HTTP 失败。完整原因计数和全部世界结果均在统计文件中。

基准有 3 笔资源交付，对照有 1 笔；逐人从初始资产沿活动历史的交付重建，金币及每种库存均与结束快照一致。重复逻辑执行、重复交付、重复玩家经历、重复居民经历及错误会话显示均为零。会面到场、交谈和生产来源分别保留；台词未作为交付或产出事实。

## 同包回放与独立核对

四个真实 ZIP 在核心 CLI、源 Godot headless 场景和冻结 Windows 导出 headless 中各回放一次，共 **12/12 通过**，全部退出 0。核心重算本分支及递归保存前缀，场景入口执行实际碰撞查询。各包的完整 motion 查询包含其保存父前缀：

| 包 | 本分支边界 | 保存父前缀边界 | 完整 motion 查询 | 初态身体查询 |
| --- | ---: | ---: | ---: | ---: |
| 基准父包 | 32428 | 0 | 2463 | 5 |
| 基准子包 | 1823 | 32423 | 2586 | 10 |
| 对照父包 | 45775 | 0 | 2866 | 5 |
| 对照子包 | 14802 | 45774 | 3785 | 10 |

源场景和 Windows 的 motion、初态身体及实际 `IntersectShape/CastMotion` 次数一致，后两者各等于 motion 加初态身体查询数。内容、几何、源码摘要、构建提交、引擎及物理后端匹配。源 Debug 与 ExportRelease 的程序集字节哈希不同并分别保留，同一模拟源码的跨构建验证符合[场景依赖合同](../SCENE_REPLAY.md)。Windows 记录加载的运行时程序集来自冻结导出目录。回放入口记录零真实 HTTP、未请求画面验收。

[回放摘要](evidence/full-demo-real-v2/replay-summary.json)保留每份输入及 12 份原报告的 SHA-256、实际查询、父前缀数和运行时路径占位符。原始汇总 SHA-256 为 `78cbbf161e6e7de8caecabb20602a0f4cd740f74b0864cd67932884bdddd4086`；原报告与执行记录归入原始制品，没有再次请求模型。

[独立验收审计](evidence/full-demo-real-v2/independent-acceptance-audit.json)从公开报告、ZIP 和协议结果重新核对分母、逐日覆盖、全部六条例外、实际派发、资产及预算算术，结果通过。该审计未读取私密账本；现实账本与公开快照是否逐字段一致以唯一执行者的核对收据为据。

## 预算、证据与限制

预算为 `full-demo-v2`／`cozytown-godot-full-demo-20261011-v2`，独立上限 CNY 5、96 次。公开结束快照共有 71 次预算尝试：baseline 37、counterfactual 26、player_dialogue 8、manual 0，其中 70 次发生 HTTP。基准和对照保守扣减分别为 CNY 0.166792、0.124044，累计 **CNY 0.290836**。预留、未知项和占槽均为零，停止原因为空；金额余量 CNY 4.709164，次数余量 25，不授权代理追加样本。`manual` 八次保留给用户最终 Windows 试玩。

[唯一执行者的预算核对收据](evidence/full-demo-real-v2/budget-final-verification.json)记录公开结束快照的 attempt 逐字段匹配现实账本，已停止 v1 的 SHA 未变，人工阶段未使用；收据不包含私密账本。v1 的旧费用和余量未转移。

付费前离线原始材料已完成[归档逐成员核对](evidence/full-demo-real-v2/offline-archive/full-demo-v2-offline-20261011-receipt.json)：`full-demo-v2-offline-20261011.tar.gz` 为 1,612,417,468 字节，SHA-256 为 `fb253cc4971c21571801ac498c9a75c75d86ee0967f98b79def84c6085f5bad2`。[清单](evidence/full-demo-real-v2/offline-archive/raw-manifest.json)含 926 个原始文件，归档另含清单和扫描绑定收据，共 928 个成员，全部长度和哈希匹配且无额外成员。[实际凭据扫描绑定](evidence/full-demo-real-v2/offline-archive/secret-scan-binding.json)记录 1124 个文件、42 个 ZIP 解压成员及 380126 份检查点载荷扫描通过；重复 Windows 目录在归档中省略，但参与扫描。原始扫描报告及文件清单的 SHA 保留，绑定收据没有凭据正文。

真实运行的[归档收据](evidence/full-demo-real-v2/archive-receipt.json)记录 `full-demo-real-v2-20261011.tar.gz`：709,710,553 字节，SHA-256 为 `e39d87e0a52dd90342e1891dea34495444be59becd2d8ea9510d80ce675ec6f3`。[158 文件清单](evidence/full-demo-real-v2/raw-manifest.json)的 SHA-256 为 `298c71359010ba22388d589602f18e7b90eaba52066f9cae508c39e7d4a4b2c5`；归档另含清单自身，共 159 个文件，逐项长度与哈希核对通过。[实际凭据扫描结果](evidence/full-demo-real-v2/secret-scan.json)记录 157 个原始输入、232 个 ZIP 解压成员及 857764 份检查点载荷通过，扫描报告作为第 158 个文件保存，清单在扫描后生成。归档保留两组报告、全部尝试、四份 ZIP、保存、回放、分析输入与原始构建输出、独立审计和匹配 198 文件清单的 Windows 离线回放程序；没有私密配置或账本。

[远端资产回执](evidence/full-demo-real-v2/remote-assets.json)确认全部 15 份附件长度与 SHA-256 与本地相同，包括两个完整归档、清单、收据、冻结及预算登记材料。[私人 Release 草稿](https://github.com/qinjunw/CozyTown-Godot/releases/tag/untagged-f311139ec8e4b0da5d59)仍为 draft，目标是冻结 `f9551c1`，需有权限的 GitHub 账号访问。远端证据归档可取与 CI 制品上传状态分别记录。

复算入口见[证据说明](evidence/full-demo-real-v2/README.md)，只处理公开报告和完整 ZIP；本次统计不发送 HTTP，不读取凭据或私密账本。v1 和 v2 使用各自冻结源码及归档，不能用新的源码摘要重标或复验旧包。

冻结候选的 [CI 运行](https://github.com/qinjunw/CozyTown-Godot/actions/runs/38145357715)通过离线预算探针、构建、测试和 Windows 导出步骤；制品上传仍受 GitHub Actions 存储配额阻止，整次 CI 未通过。本机离线门与 CI 上传失败分别保留。

本次真实样本只覆盖两天、一次晨间；作物成熟和全部配方由固定门另行覆盖。两组自主动作和交付不同，同时存在模型输出与到达时序的非确定性，不能将全部差异归因于资源分布，也不从本次小样本推断长期可靠率。headless 结果不替代新的画面、键鼠或最终 Windows 人工验收。
