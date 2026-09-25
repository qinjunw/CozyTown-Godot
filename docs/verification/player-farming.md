# 种植与睡眠验证记录

2026-09-25，对应[贯通种植、05:00 结算与按小时睡眠](https://github.com/qinjunw/CozyTown-Godot/issues/18)。代码候选 `897518b9cb682abdd882db964cd8c6878013e474` 的 C# 测试、Godot 场景检查、Windows 导出检查及实际键鼠验收均已通过。实际操作完成普通新档生产闭环，并验证满包拒绝、腾格后收获和保存恢复。

## 输入与范围

本机在现有项目目录运行，开始时工作树干净，报告记录 `sourceDirty=false`。使用 Godot `4.7.2.stable.mono.official.ed1daf0bf`、.NET SDK `10.0.401`，执行：

```powershell
pwsh -NoProfile -File tools/windows/verify.ps1 -Graphical
```

运行目录为 `artifacts/verification/20260925T124151Z-fdedfbcd/`，开始于 12:41:51.256 UTC，结束于 12:44:22.316 UTC，共 **151.06 秒**。结果见[验证摘要](evidence/player-farming/verification.json)和[逐项测试结果](evidence/player-farming/test-results.json)，文件来源与哈希见[证据清单](evidence/player-farming/manifest.json)。

普通新档保留 **300 金币、24 格空背包**，从第一日 09:00 开始。本次加入三种作物、六块独立地块、每日 05:00 的生长和库存替换，以及 1～12 个整小时的睡眠。操作和内容字段见[种植、晨间结算与睡眠](../PLAYER_FARMING.md)。

满包验收另使用 `farming-capacity-fixture`：300 金币、五格背包，包含胡萝卜种子 2、土豆 1、鸡饲料 1、盐 1、面粉 1。该初态在界面中标明，仅允许离线规则模式，具有独立内容指纹与存档阶段；普通新档不获得这些物品。

| 初态 | 模拟内容指纹 |
| --- | --- |
| 普通新档 `baseline` | `9fd649bced7e96cbfdd10d2cd78dae76fa6953c7967ab4806f6ed3b01a5fd1e4` |
| 四人对照 `counterfactual` | `83a3bc2a207cad2d7754073a62abf9cdb48e554ac04cc10f7ce679b83c79c5fd` |
| 交易验证 `economy-fixture` | `aef69b88657498cf291284545b6219b3db9db6cc3b8a1c204df37d390e11f182` |
| 满包验证 `farming-capacity-fixture` | `1891060d72fe10d1c76f6b161bf663e989833b57166257e9bccfd3a401ed0280` |

四种初态均通过内容检查，使用同一组 22 个地点和四名居民。源场景与导出包中的同一初态指纹一致。

## 自动验证结果

| 检查层 | 结果 | 覆盖范围 |
| --- | --- | --- |
| C# 核心测试 | 130 / 130 | 既有社会与经济规则、三种作物、晨间结算、睡眠、原子推进、身体阻挡、快照与去重 |
| 模型适配测试 | 45 / 45 | 固定传输、持久预算、恢复限额及响应边界回归 |
| 宿主持久化测试 | 74 / 74 | 日志与文件发布、农场和睡眠检查点、失败保护、恢复、导出及验证初态阶段 |
| 四人场景回归 | 五次各 38 / 38 | 源场景基准与对照 headless、导出包基准与对照 headless、导出包基准图形运行 |
| 玩家经济场景回归 | 三次各 23 / 23 | 源场景 headless、导出包 headless、导出包图形运行 |
| 普通新档种植场景 | 三次各 24 / 24 | 源场景 headless、导出包 headless、导出包图形运行 |
| 满包种植场景 | 三次各 15 / 15 | 源场景 headless、导出包 headless、导出包图形运行 |

三组 C# 测试共 **249 项**，全部执行，无失败或跳过。Windows 包通过独立启动与存读档检查，验证脚本限制其从导出目录加载随包运行时。

普通新档场景完成购买胡萝卜种子、种植、浇水、三次成长、收获、出售和再购买种子。钱包由 300 降至 275，出售两根胡萝卜后为 325，再投资按当日种子报价扣款。场景先故意遗漏一次浇水，确认地块停留在原成长阶段，而时间、晨间游标与第二日库存正常更新；种子不足和未成熟收获分别显示原因且保留状态。

普通场景报告：[源场景](evidence/player-farming/scene-farming-headless.json)、[导出 headless](evidence/player-farming/export-farming-headless.json)、[导出图形](evidence/player-farming/export-farming-graphical.json)。成熟存档检查实际读取已写入的文件，恢复后确认分支身份改变且父分支匹配，再核对未领取作物、资产和游标。

满包场景种下一颗胡萝卜种子后仍占用五格。三次成长后收获被拒，成熟作物和资产保持原样；保存、恢复仍保持该状态。出售一颗土豆后，钱包为 320、背包腾出一格，再次收获成功并得到两根胡萝卜。报告分别为[源场景](evidence/player-farming/scene-farming-capacity-headless.json)、[导出 headless](evidence/player-farming/export-farming-capacity-headless.json)和[导出图形](evidence/player-farming/export-farming-capacity-graphical.json)。该恢复检查同时要求存档文件存在、阶段正确、新分支及父分支关系正确，不能只凭资产未变认定恢复成功。

## 时间、原子性与模型边界

核心测试比较连续、分段及整小时睡眠跨越午夜、05:00 和多个晨间后的作物、双方资产、居民路线、路点游标、身体位置、日程及承诺阶段。移动仍执行逐段空间查询；正常宿主 `.025` 世界分钟步长与长推进遇到同一阻挡时，以 `1e-7` 像素的可行前缀精度确定停点。

第二个晨间结算点注入失败时，整个推进请求的时间、生产、库存、游标、居民、待办和事件状态恢复为请求前状态；此前独立完成的推进保留。睡眠失败记录拒绝结果，回调不能重入修改世界。未浇水只阻止对应地块成长，不阻止其余结算。

已接受但未消费的推进量通过存档恢复后继续消费，05:00 不漏算或重复处理。睡眠保持 1～12 小时及七日总推进上界；暂停阻止新的睡眠推进。睡眠的世界小时数不转成模型现实等待秒数，暂停中的现实等待仍按独立时间输入扣减，模型派发继续经过正常宿主门控。模型适配回归使用固定客户端，未新增真实服务调用。

## 持久化与输入记录

核心快照升级为 **schema 3**，宿主封套仍为 schema 1。规则版本为 `town-social-farming-v3`，数值版本为 `pixels-double-grid16-radius5-morning-motion-prefix-v3`。旧版本、缺失字段、错误作物或地块状态、缺失晨间来源、冲突操作身份以及时间重叠的睡眠回执均拒绝恢复。

种植消耗、收获产出和商店整份库存替换分别保留来源。恢复按共享输入序号重建玩家与商店资产、地块状态和晨间游标；普通交易仍保持双方守恒。农场与睡眠命令在发布候选世界前写入并刷新运行日志。宿主故障用例覆盖关闭日志、故障日志、回调写盘失败、坏档预检和重复操作，确认完整世界、现存档及 `.previous` 保留。

满包初态的宿主回归另验证规则模式能真实写盘并恢复新分支，真实模型模式在写入或读取时被拒绝。

导出图形运行保留普通生产场景两个分支的 **21 条命令**，以及满包场景两个分支的 **13 条命令**。样例见[生产输入与检查点](evidence/player-farming/farming-input-checkpoints.json)及[满包输入与检查点](evidence/player-farming/farming-capacity-input-checkpoints.json)。其中 `player_farm_command` 和 `player_sleep_command` 包含请求、成功或拒绝结果、完整世界检查点及宿主上下文；既有交易命令保留经济检查点。

## 图形检查与实际 Windows 操作

1280×720 自动图形运行提供了[休息面板](evidence/player-farming/farming-sleep-1280.png)、[缺水与未成熟提示](evidence/player-farming/farming-dry-1280.png)、[成熟作物](evidence/player-farming/farming-mature-1280.png)、[菜园画面](evidence/player-farming/farming-field-1280.png)、[满包拒绝](evidence/player-farming/capacity-failure-1280.png)和[腾格后收获](evidence/player-farming/capacity-success-1280.png)截图。

自动场景使用真实 Godot 路线、界面按钮回调及合成键事件。另对同一候选导出包使用实际 Windows 鼠标和键盘完成下列操作，数据记录于独立目录，汇总见[实际操作核验](evidence/player-farming/manual-farming-summary.json)。

| 普通新档实际操作 | 核验结果 |
| --- | --- |
| 买入胡萝卜种子 1 | 钱包 300 → 275，背包增加种子 1 |
| 尝试种植没有种子的土豆，再种下胡萝卜 | 前者显示种子不足，资产不变；后者消耗胡萝卜种子 |
| 未成熟时收获 | 显示尚未成熟，作物和资产保持原样 |
| 浇水三次，每次成长之间回家睡眠 | 共六次 12 小时睡眠，跨过三个 05:00；胡萝卜成长至 3 / 3 |
| 成熟时 F5，收获，再 F9 | 首次收获得到胡萝卜 2；恢复到成熟未收获状态、275 金币和空背包 |
| 恢复后收获，出售胡萝卜 2，再买土豆种子 1 | 钱包 275 → 325 → 305，最终持有土豆种子 1 |
| 再次 F5 | 保存最终资产和已收获的空地状态 |

普通新档的实际操作跨两个分支，共 **17 条玩家命令**：8 条农场命令、6 条睡眠命令和 3 条交易命令；其中包括种子不足与未成熟收获两次拒绝。两次成功收获位于恢复前后不同分支，最终地块的已收获次数为 1。记录包含 **2 次保存、1 次恢复**，没有保存或恢复失败。

成熟 `.previous` 存档与恢复分支初态的双方经济状态、农场状态和世界分钟逐字段相同，父分支、来源快照及记录前缀匹配。最终存档为 `5235.799999999488` 世界分钟、晨间游标 3、未消费推进量 0。正常帧记录为 16,020 次 `(.025 世界分钟, .05 现实秒)`，暂停帧为 8,043 次 `(0, .05)`，另有六次睡眠 `(720, 0)`。一个隐藏启动分支没有玩家命令，保留在归档中，但不计入实际操作数量。

画面证据：[种子不足](evidence/player-farming/manual-baseline-missing-seed.jpg)、[未成熟拒绝](evidence/player-farming/manual-baseline-immature.jpg)、[输入 12 小时](evidence/player-farming/manual-baseline-sleep-typed12.jpg)、[恢复前收获](evidence/player-farming/manual-baseline-harvest-before-restore.jpg)、[恢复成熟作物](evidence/player-farming/manual-baseline-restored-mature.jpg)、[出售产物](evidence/player-farming/manual-baseline-sold.jpg)、[再购买种子](evidence/player-farming/manual-baseline-reinvest.jpg)。

满包实测使用同一版本 `farming-capacity-fixture` 自动场景留下的成熟存档，复制相应的两个原始运行分支以保留恢复来源。复制文件与自动场景原始文件逐字节相同；自动准备阶段的种植、浇水、睡眠及 13 条命令不计为本次实际键鼠操作。

| 满包初态实际操作 | 核验结果 |
| --- | --- |
| F9 读取准备好的成熟存档，点击收获 | 返回 `inventory_full`，300 金币、五格物品与成熟作物保持原样 |
| F5 保存拒绝后的状态，再 F9 | 恢复分支的经济、农场和世界时间与保存状态逐字段相同 |
| 出售土豆 1 | 钱包 320，背包从五格降为四格 |
| 返回菜地收获，再 F5 | 得到胡萝卜 2，背包占五格，地块清空且已收获次数为 1 |

此次新增 **3 条玩家命令、2 次保存和 2 次恢复**，无保存或恢复失败。最终存档为 `4941.724999998811` 世界分钟、未消费推进量 0。一个新启动分支未执行玩家命令，同样保留但不计入操作数量。画面见[满包拒绝](evidence/player-farming/manual-capacity-full-rejected.jpg)、[恢复成熟状态](evidence/player-farming/manual-capacity-restored-mature.jpg)、[出售腾格](evidence/player-farming/manual-capacity-slot-freed.jpg)和[收获后的背包](evidence/player-farming/manual-capacity-harvest-inventory.jpg)。

两组实际操作合计 **20 条新玩家命令、4 次保存、3 次恢复**，均记录构建 `897518b` 和规则模式，无模型派发记录。以 `--resolution 1920x1080` 启动，截图 API 返回 **962×570 JPEG**（含窗口栏），归档保留全部 24 张原始捕获，本文链接 11 张。

## 归档与限制

以下归档位于本次原始产物目录。[清单](evidence/player-farming/manifest.json)记录 ZIP CRC 和逐成员 SHA-256 校验通过。提交到仓库的 JSON 将本机项目路径替换为 `<project-root>`，原始归档保留原始字节，模拟值和身份未改变。

| 文件 | 字节 | 文件数 | SHA-256 |
| --- | ---: | ---: | --- |
| `CozyTown-897518b-Windows.zip` | 75,812,984 | 198 | `597149cb4c791a891b49d50880b132b8c3a1cd9f6dccf03c180692f32311117f` |
| `offline-run-records-897518b.zip` | 4,625,281 | 219 | `627853399311a134929e5e9c888a8d3e7e9bcf3e66a93aac80bd3a4bea1f39f1` |
| `manual-run-records-897518b.zip` | 4,173,337 | 66 | `00bb3763d5f8926152cf809762ca515e7d0f6d3fa1c66fc88f10b8b20931cea9` |

本票交付版本化命令、记录和检查点，完整回放执行器仍由[实现版本化输入记录与离线核心回放](https://github.com/qinjunw/CozyTown-Godot/issues/22)与[实现 Godot 场景回放与空间分歧验证](https://github.com/qinjunw/CozyTown-Godot/issues/23)承接。本次没有将记录导出报告为回放通过。养鸡、钓鱼和烹饪由后继玩法票继续实现。
