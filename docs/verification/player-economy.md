# 玩家经济验证记录

2026-09-23，对应[实现玩家资产、商店买卖与五格快捷栏](https://github.com/qinjunw/CozyTown-Godot/issues/17)。本机自动验证全部通过；真实 Windows 键鼠操作仍待验证，当前桌面锁屏。

## 输入与范围

源提交为 `fa75ab35813f2f7dc5b67a4e82b7fe1a07823298`。验证在现有工作目录中运行，开始时工作树干净，报告保留 `sourceDirty=false`。

使用 Godot `4.7.2.stable.mono.official.ed1daf0bf` 和 .NET SDK `10.0.401`，执行：

```powershell
pwsh -NoProfile -File tools/windows/verify.ps1 -Graphical
```

原始产物目录为 `artifacts/verification/20260923T111630Z-0ef2f747/`。运行从 11:16:30.302 UTC 到 11:17:26.947 UTC，用时 **56.65 秒**，包含测试、导入、构建、场景检查、Windows 导出和自动截图，不包含人工操作时间。结果见[验证摘要](evidence/player-economy/verification.json)，归档来源与文件摘要见[证据清单](evidence/player-economy/manifest.json)。

普通新档为 **300 金币、24 格空背包**。双向交易场景使用公开的 `economy-fixture`：背包包含土豆 2、鸡蛋 3、鲤鱼 1、烤土豆 1，界面标明验证初态；该初态只允许规则模式，并使用独立内容指纹和存档阶段。普通新档没有这些测试产物，生产与产物领取仍由后续玩法票交付。

## 自动验证结果

| 检查层 | 结果 | 覆盖范围 |
| --- | --- | --- |
| C# 核心测试 | 100 / 100 | 原社会规则，以及经济资产、交易拒绝、去重、快捷栏和快照校验 |
| 模型适配测试 | 45 / 45 | 离线传输、持久预算、恢复限额和响应边界回归 |
| 宿主持久化测试 | 55 / 55 | 日志、保存发布、坏档拒绝、经济提交前失败和导出 |
| 四人场景回归 | 五次各 38 / 38 | 源场景基准与对照、导出包基准与对照 headless、导出包基准图形运行 |
| 玩家经济场景 | 三次各 22 / 22 | 源场景 headless、导出包 headless、导出包图形运行 |

三组 C# 测试共 200 项，全部执行，没有失败或跳过。经济场景分别保留[源场景报告](evidence/player-economy/economy-headless.json)、[导出 headless 报告](evidence/player-economy/export-economy-headless.json)和[导出图形报告](evidence/player-economy/export-economy-graphical.json)。三者的经济内容指纹一致；导出验证限制运行时从导出目录加载。

[Windows CI](https://github.com/qinjunw/CozyTown-Godot/actions/runs/35853723414)也通过了 PR 合并候选的干净检出、构建、测试及导出包 headless 验证；[CI 工件](https://github.com/qinjunw/CozyTown-Godot/actions/runs/35853723414/artifacts/10746817113)按工作流保留 14 天。

经济场景经实际 Godot 路线移动到商店，通过 UI 回调买入胡萝卜种子 2，钱包从 300 变为 250；再出售土豆 1，钱包变为 270。每次成功交易检查玩家与商店的物品、金币总量守恒。超出商店库存和玩家持有数量的请求均被拒绝，双方资产保持原样，界面显示对应原因。

场景检查还覆盖数量文本未按 Enter 即确认、商店暂停、1～5 选格、B 打开只读背包、Esc 关闭、保存后继续交易再恢复，以及店门口独立邻居按钮和对话暂停。合成按键使用 `Godot.Input.ParseInputEvent`；操作系统键鼠验证状态见下文。

## 持久化与输入记录

经济命令采用同步的先记录再提交：宿主先把 `player_economy_command` 写入并刷新运行日志，再发布候选经济状态。测试覆盖日志已关闭、日志已故障及回调中的磁盘发布失败，确认失败时完整核心状态不变，包括双方资产、操作回执和序号。记录写入成功之后的故障不再承诺回滚已经提交的交易，宿主停止后续推进与派发。

核心快照结构为 **schema 2**，宿主封套仍为 schema 1。旧核心结构、缺失经济字段、非法余额、错误商店身份、回执身份或序号不一致均拒绝，不以初态资产补齐旧档。坏档预检或文件替换失败时，当前存档和 `.previous` 均保持不变。成功买卖及快捷栏选择经过保存、读取和恢复后保持一致。重复操作可新增输入记录，但不再次转移资产、不覆盖原回执；使用新操作身份仍可继续交易。

图形场景报告核对了两个运行分支中的 **11 条经济输入**：3 次成功交易、2 次拒绝、6 次快捷栏选择。读取早前快照后保留当时的 9 份回执；保存之后的原分支记录仍保留，不混入恢复后的资产。每条记录包含请求、结果和完整经济检查点，样例见[输入与检查点](evidence/player-economy/input-checkpoints.json)。宿主测试另核对 ZIP 导出中的成功与拒绝记录、检查点和恢复去重。

## 画面与真实 Windows 操作

1280×720 自动图形运行生成了[商店](evidence/player-economy/shop-1280.png)、[背包](evidence/player-economy/bag-1280.png)和[失败提示](evidence/player-economy/failure-1280.png)截图。验证脚本要求三份截图对应本次指定路径，并检查文件存在和 PNG 标记。

**真实 Windows 键鼠验证：pending。** 当前桌面锁屏，尚未取得本候选导出包的实际键鼠操作结果。解锁后需使用同一候选验证移动到店、键入数量后点击确认、买卖与失败反馈、背包和五格选择、F5/F9、店门口交谈及窗口布局，并据实记录结果。自动截图不代表用户完成演示验收。

## 尚未覆盖的能力

本次全部运行使用规则模式或固定替身，**没有新增真实模型调用**。完整回放由[实现版本化输入记录与离线核心回放](https://github.com/qinjunw/CozyTown-Godot/issues/22)和[实现 Godot 场景回放与空间分歧验证](https://github.com/qinjunw/CozyTown-Godot/issues/23)承接。种植、养鸡、钓鱼、烹饪和晨间补给仍属后续玩法工作。
