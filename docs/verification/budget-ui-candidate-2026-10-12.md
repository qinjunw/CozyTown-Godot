# 预算显示修复与新 Windows 候选

2026-10-12。对应[显示实际预算上限与人工阶段用量](https://github.com/qinjunw/CozyTown-Godot/issues/43)。新候选运行时源码为 `576ae64e38f854bb22b13186e6a9d2ecc9799d72`，模拟源码摘要为 `301c4b46357a6ccfec5480a42d0f1d10ca2c4f9f8beebc5381f85a5bb55ed1cf`。本页记录该候选的离线检查；后续[七组操作及可选真实 M01 的用户通过反馈](https://github.com/qinjunw/CozyTown-Godot/issues/26#issuecomment-6111460458)仍归属该候选，不覆盖后续源码。

## 变更与来源

原观察手册将共用次数写成 `/ 48 次`，完整 Demo v1/v2 的实际总上限为 96。`TownGame.Interface.cs` 现在读取当前注册 `Profile.TotalLimit`；`manual` 用途另列同一预算快照中人工阶段的尝试数与 `Profile.StageLimits[manual]`，注明 NPC 与玩家共享。预留、保守扣减与停止原因继续读取原预算快照字段。

| 注册预算 | 用途 | 总次数分母 | 人工阶段 |
| --- | --- | ---: | --- |
| `first-slice-v1` | `legacy` | 48 | 无人工用途 |
| `full-demo-v1` / `full-demo-v2` | `experiment` | 96 | 不显示人工用途行 |
| `full-demo-v1` / `full-demo-v2` | `manual` | 96 | 账本快照中 `stage=manual` 的全部尝试数 / 8；包含 NPC、玩家、失败与恢复 |

相对原真实候选 `f9551c19eb1866ba79cc1312ca7821004fbb7fa1`，运行时仅该宿主文件一行替换、两行显示条件。世界/生产/经济规则、内容、地图、提示词与冻结输入、时速、模型派发、预算准入/授权、费用和保存恢复均未改。[完整源差异](evidence/budget-ui-candidate/runtime-source.diff)与[来源收据](evidence/budget-ui-candidate/source-provenance.json)绑定此范围。该文件参与模拟源码摘要，旧摘要保护保持启用。

原[完整 v2 真实结果](full-demo-real-v2-2026-10-11.md)仍只属于 f955：70 次 HTTP、NPC 57/61 可解析候选采用与 57/62 全 HTTP 采用、玩家 8/8、保守扣减 ¥0.290836。本次未测量新候选的真实采用率；源码差异不能保证未来随机模型输出或到达时序相同。原四包与匹配程序保留在原私人草稿 Release，新程序不接受旧真实包。

## 干净检出与离线验证

新提交由 Git 检出到独立空目录，不复制旧 `.godot`、bin/obj 或 Unity 缓存。只共用锁定工具链与 NuGet 包目录：Godot `4.7.2.stable.mono.official.ed1daf0bf`，.NET SDK `10.0.401`。从该干净检出运行：

```powershell
pwsh -NoProfile -File tools/windows/verify-full-demo.ps1 `
  -ExpectedCommit 576ae64e38f854bb22b13186e6a9d2ecc9799d72 `
  -ToolsRoot '<locked-toolchain-root>'
```

[验证摘要](evidence/budget-ui-candidate/summary.json)关联原报告 SHA-256、工具链/输入/源码摘要与检查结果。2026-10-12 本地时间完成的门通过 667 项单元测试（Core 324、Model 92、Host 251）、33 项统一检查、13 组场景包在 Core/源场景/Windows 三入口的 39 项预期结果，以及新基准和资源对照四包的 12 次回放。故障矩阵含预期拒绝，39 项不表示所有故障包均被接受。Windows 默认入口使用固定客户端；全部检查为 headless、零 HTTP，未评估真实采用率。

主门原报告 SHA-256 为 `12a847200654902d37684237535516e9f4de1c9009cbfc5684fd7a5e4db02527`。Windows 运行时包含 198 个文件、197,401,726 字节；[运行时清单](evidence/budget-ui-candidate/windows-manifest.json)绑定逐文件长度与 SHA-256。完整原始日志、报告、固定记录及分析脚本在离线证据归档中，运行时另由候选 ZIP 完整保留。

已有公开 Host/Model 检查验证预算注册、人工阶段路由与共用账本；本次未新增仅复述展示表达式的测试。真实预算文案只在真实客户端存在时可见，本轮固定/headless 门不代表实际预算文字与面板可读性已经人工确认。

## 严格拒绝旧真实包

对未改写的 f955 基准父包 `b6d4f36167d903d25f312163ece400a3eb5cc208cf2ea789f2f0d0da070e213e`，新 Windows 程序通过公开 `--scene-replay` 命令返回退出码 2、`incompatible_dependency`。报告分别保留旧 `bb54adc96757808ef36220c46c85539635faa244c92744b9a19bb9277df946c6` 与新 `301c4b46357a6ccfec5480a42d0f1d10ca2c4f9f8beebc5381f85a5bb55ed1cf` 摘要，物理与身体查询均为零；使用包内运行时，不创建模型客户端。原拒绝报告 SHA-256 为 `00d43d88d3ad1cbb39c0d113ce4e3483b32bba5c993d9d68a045c7040d844f37`。拒绝是预期依赖保护，不能计作旧包回放成功。

## 包与人工反馈

最终包为 `CozyTown-576ae64-windows-candidate.zip`，81,616,378 字节，SHA-256 为 `cb5d9eecd8b07b849822c08833b9de7d542a91d83b2aba9598cd713713e4aea6`。[包清单](evidence/budget-ui-candidate/package-manifest.json)列 214 个其他文件，[收据](evidence/budget-ui-candidate/package-receipt.json)另绑定清单自身，全部 215 个 ZIP 成员的长度和 SHA-256 匹配。包含完整 198 文件运行时、九个离线入口、README、两份手册、占位符配置模板、来源收据及两份新场景示例。

默认居民使用规则、玩家生成使用固定客户端。九入口覆盖 1280×720、1920×1080、资源对照、延迟/错误/超时/额度对话及两种容量样例，参数在导出 CLI 中各执行一次 `--headless --quit-after 30`，全部退出 0。该检查没有执行可见 CMD；[包复核](evidence/budget-ui-candidate/candidate-summary.json)确认独立测试数据未进入候选目录，入口检查后 215 个文件及 ZIP 仍匹配原哈希。

`examples/scene-gameplay.json` 与 `scene-player-dialogue.json` 分别为本轮矩阵的玩法、玩家对话包，已通过 Core/源场景/同一 Windows 三边界，长度与 SHA-256 见包复核。README 提供直接从包目录执行的 headless 命令。两个示例只含固定输入与回复，不产生真实调用，不代替画面验收。

首包自查发现 MANUAL 的相对历史报告链接未随包提供，原首包、清单、收据、日志与失败摘要均保留。最终包将该链接改为固定 GitHub 提交地址，并核对全部本地 Markdown 链接；[原失败摘要](evidence/budget-ui-candidate/package-initial-link-review.json)不能当作最终包通过证据。

可选真实仅由用户以外部现有 v2 配置和 `--execution-purpose=manual` 显式启动，人工八次不供代理补测。包内无真实启动脚本、实际配置值或账本。

使用同一份[七组人工清单](full-demo-clean-manual.md)及[详细步骤](full-demo-clean-manual-details.md)；新 UI 项核对总分母 96、人工已用/8、NPC 与玩家共享，以及费用/预留/停止原因的可读性。旧 f955 包保留干净构建与真实回放证据，不要求用户重复两次完整清单。包内手册、源提交和清单哈希绑定新候选。

## 原始归档与取回

离线归档 `CozyTown-576ae64-offline-evidence.tar.gz` 为 1,537,671,877 字节，SHA-256 为 `ffd938d334bd0645ae02b3e8d65dab24b78867d0a6f1f9d1af82953682ed61c1`。[原始清单](evidence/budget-ui-candidate/raw-manifest.json)列 854 个原件、2,651,835,878 字节，[归档收据](evidence/budget-ui-candidate/archive-receipt.json)另绑定清单自身，逐项验证全部 855 个 tar 成员。入档源文件在归档前后未变；解压布局、首包失败记录及运行时保留位置见[证据说明](evidence/budget-ui-candidate/README.md)。

[凭据扫描绑定](evidence/budget-ui-candidate/secret-scan-binding.json)关联最终 ZIP、tar、两个清单及三份原扫描收据。完整门扫描覆盖 1,128 文件、42 ZIP 成员和 380,126 个解码检查点；最终候选扫描覆盖 218 文件、215 ZIP 成员和 2,432 个解码检查点；非门原件及原始清单扫描覆盖 123 文件和 663 个解码检查点，均通过。范围有重复，不相加为唯一文件数。JSON 中 base64/Brotli 世界与宿主检查点已解码比对，扫描源与归档通过逐成员收据关联；实际凭据不写入制品。

仓库授权账号可在[Releases 列表](https://github.com/qinjunw/CozyTown-Godot/releases)查找私人草稿 `full-demo-budget-ui-candidate-20261011`，Release ID 为 `409783424`，目标为运行时 `576ae64`；首包未上传。[远端收据](evidence/budget-ui-candidate/remote-assets.json)确认全部 18 个附件已上传，长度及远端 SHA-256 与本地相同。

本票的自动验证与候选准备不关闭最终人工验收票。[限制 Windows CI 成功制品体积并保留失败诊断](https://github.com/qinjunw/CozyTown-Godot/issues/38)仍须取得线上成功证据，私人草稿 Release 不能代替。
