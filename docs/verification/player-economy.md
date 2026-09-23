# 玩家经济验证记录

2026-09-23，对应[实现玩家资产、商店买卖与五格快捷栏](https://github.com/qinjunw/CozyTown-Godot/issues/17)。最终代码候选为 `6f9358e25511ad6693fd29d2e3d81e2fdb2c3ffe`，完整自动验证与实际 Windows 买卖、保存恢复、自然跨日程和居民交谈操作均通过。

## 输入与范围

本机验证在现有工作目录中运行，开始时工作树干净，报告保留 `sourceDirty=false`。最终版本的 [Windows CI](https://github.com/qinjunw/CozyTown-Godot/actions/runs/35867292040)已通过。

使用 Godot `4.7.2.stable.mono.official.ed1daf0bf` 和 .NET SDK `10.0.401`，执行：

```powershell
pwsh -NoProfile -File tools/windows/verify.ps1 -Graphical
```

原始产物目录为 `artifacts/verification/20260923T132755Z-00866ad5/`。运行从 13:27:55.048 UTC 到 13:28:58.353 UTC，用时 **63.30 秒**，包含测试、导入、构建、场景检查、Windows 导出和自动截图。结果见[最终验证摘要](evidence/player-economy/final/verification.json)，归档来源与文件摘要见[最终证据清单](evidence/player-economy/final/manifest.json)。

普通新档为 **300 金币、24 格空背包**。双向交易场景使用公开的 `economy-fixture`：背包包含土豆 2、鸡蛋 3、鲤鱼 1、烤土豆 1，界面标明验证初态；该初态只允许规则模式，并使用独立内容指纹和存档阶段。普通新档没有这些测试产物，生产与产物领取仍由后续玩法票交付。

## 首次实测失败与修复

旧候选 `fa75ab35813f2f7dc5b67a4e82b7fe1a07823298` 曾通过自动检查，但实际 Windows 操作中 F5 保存失败。[失败记录](evidence/player-economy/manual-initial-failure.json)显示世界分钟停在 `659.9999999998909`，未消费的 `acceptedWorldMinutes` 达到 `3445.775000008153`，超过快照允许的 1440 上限。

原因是到日程边界的微小正步长被跳过，后续接受的时间不断累积。最终候选消费所有正的边界余量，并在到达时将世界时间精确设置为边界；新增核心回归和 `fixed_step_boundary_preserves_save` 场景检查。`NumericVersion` 升级为 `pixels-double-grid16-radius5-clock-boundary-v2`，保留原快照余量上限，没有放宽校验绕过失败。

旧候选的[自动结果](evidence/player-economy/verification.json)、[归档清单](evidence/player-economy/manifest.json)和失败证据保留在父目录，用于追溯，**不计入最终候选验收**。

## 最终自动验证结果

| 检查层 | 结果 | 覆盖范围 |
| --- | --- | --- |
| C# 核心测试 | 103 / 103 | 社会规则、交易、去重、快捷栏、快照与时间边界回归 |
| 模型适配测试 | 45 / 45 | 离线传输、持久预算、恢复限额和响应边界回归 |
| 宿主持久化测试 | 55 / 55 | 日志、保存发布、坏档拒绝、经济提交前失败和导出 |
| 四人场景回归 | 五次各 38 / 38 | 源场景基准与对照、导出包基准与对照 headless、导出包基准图形运行 |
| 玩家经济场景 | 三次各 23 / 23 | 源场景 headless、导出包 headless、导出包图形运行 |

三组 C# 测试共 **203 项**，全部执行，没有失败或跳过。经济场景分别保留[源场景报告](evidence/player-economy/final/economy-headless.json)、[导出 headless 报告](evidence/player-economy/final/export-economy-headless.json)和[导出图形报告](evidence/player-economy/final/export-economy-graphical.json)。三者的经济内容指纹一致；导出验证限制运行时从导出目录加载。成功交易检查玩家与商店的物品、金币总量守恒；缺货请求被拒绝，双方资产保持原样。

场景检查还覆盖数量文本未按 Enter 即确认、商店暂停、1～5 选格、B 打开只读背包、Esc 关闭、保存后继续交易再恢复，以及店门口独立邻居按钮和对话暂停。合成按键使用 `Godot.Input.ParseInputEvent`；操作系统键鼠验证状态见下文。

## 持久化与输入记录

经济命令采用同步的先记录再提交：宿主先把 `player_economy_command` 写入并刷新运行日志，再发布候选经济状态。测试覆盖日志已关闭、日志已故障及回调中的磁盘发布失败，确认失败时完整核心状态不变，包括双方资产、操作回执和序号。记录写入成功之后的故障不再承诺回滚已经提交的交易，宿主停止后续推进与派发。

核心快照结构为 **schema 2**，宿主封套仍为 schema 1。旧核心结构或数值版本、缺失经济字段、非法余额、错误商店身份、回执身份或序号不一致均拒绝，不以初态资产补齐旧档。坏档预检或文件替换失败时，当前存档和 `.previous` 均保持不变。重复操作可新增输入记录，但不再次转移资产、不覆盖原回执；使用新操作身份仍可继续交易。

图形场景报告核对了两个运行分支中的 **11 条经济输入**：3 次成功交易、2 次拒绝、6 次快捷栏选择。读取早前快照后保留当时的 9 份回执；保存之后的原分支记录仍保留，不混入恢复后的资产。每条记录包含请求、结果和完整经济检查点，样例见[最终输入与检查点](evidence/player-economy/final/input-checkpoints.json)。宿主测试另核对 ZIP 导出中的成功与拒绝记录、检查点和恢复去重。

## 画面与真实 Windows 操作

1280×720 自动图形运行生成了[商店](evidence/player-economy/final/shop-1280.png)、[背包](evidence/player-economy/final/bag-1280.png)和[失败提示](evidence/player-economy/final/failure-1280.png)截图。验证脚本要求三份截图对应本次指定路径，并检查文件存在和 PNG 标记。

最终导出包的实际 Windows 鼠标与键盘操作已得到以下结果：

| 操作 | 可观察结果 |
| --- | --- |
| 买入胡萝卜种子 2，再出售土豆 1 | 钱包 300 → 250 → 270 |
| 尝试出售未持有的胡萝卜 | 显示失败，钱包保持 270 |
| 数字键 5、B、F5 | 选中第五格、显示背包，保存成功 |
| 再买种子 1，按数字键 1 | 钱包 245、种子 3，选中第一格 |
| F9 恢复 | 钱包 270、种子 2、选中第五格；商店金币 10,030、种子库存 2 |
| 正常时速跨过 11:00，点击“和邻居聊聊” | 米娜到店并打开对话，世界暂停；关闭对话后 E 仍可打开商店 |
| 11:15 再按 F5、F9 | 跨日程后的保存与读取成功，经济状态保持一致 |

画面证据：[买入](evidence/player-economy/final/manual-1920-buy.png)、[失败提示](evidence/player-economy/final/manual-1920-failure.png)、[保存](evidence/player-economy/final/manual-1920-save.png)、[恢复](evidence/player-economy/final/manual-1920-restore.png)、[到店交谈](evidence/player-economy/final/manual-1920-neighbor.png)、[跨界后读取](evidence/player-economy/final/manual-1920-boundary-restore.png)。游戏客户区为 1920×1080；Windows 200% 缩放下，窗口捕获图为 962×570 逻辑像素，包含标题栏。

[实测记录](evidence/player-economy/final/manual-verification.json)核对了 6 条经济输入、2 次保存、2 次恢复和 1 次米娜交谈，无保存或恢复失败。结束存档的世界分钟为 `675.199999999877`，未消费时间小于 `1e-8`；双方经济状态与较早存档及恢复初态一致。全过程从公开验证初态进入，按正常时速运行，没有向游戏注入合成 Godot 事件或修改运行中资产。

## 最终归档

以下文件位于最终原始产物目录；[清单](evidence/player-economy/final/manifest.json)记录 CRC 和逐文件哈希检查通过。

| 文件 | 字节 | SHA-256 |
| --- | --- | --- |
| `CozyTown-6f9358e-Windows.zip` | 75,761,793 | `42a7d2b79d95a0bbd19298c69ac5a794d6e6e19c758caff19a0f8ac8986133d7` |
| `offline-run-records-6f9358e.zip` | 1,095,986 | `6b20283e24f46184f466dc9a211f2c77ad7fcd70931eca5d569c51a93a3491da` |
| `manual-run-records-6f9358e.zip` | 534,037 | `635032befdd9646f0f3dfc5321a55d71cfc901a0a41eabf1f18ae1a551739fdf` |

## 尚未覆盖的能力

本次全部运行使用规则模式或固定替身，**没有新增真实模型调用**。完整回放由[实现版本化输入记录与离线核心回放](https://github.com/qinjunw/CozyTown-Godot/issues/22)和[实现 Godot 场景回放与空间分歧验证](https://github.com/qinjunw/CozyTown-Godot/issues/23)承接。种植、养鸡、钓鱼、烹饪和晨间补给仍属后续玩法工作。
