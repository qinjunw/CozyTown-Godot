# 预算显示候选的离线证据

本目录属于运行时 `576ae64e38f854bb22b13186e6a9d2ecc9799d72`，结果见[候选报告](../../budget-ui-candidate-2026-10-12.md)。`summary.json` 记录干净完整门、667 个测试、39 项矩阵预期结果、四份新固定包的 12 次回放及旧真实包的预期拒绝；`candidate-summary.json` 记录最终包的九入口检查、链接检查与文件未变核对。全部为零 HTTP、headless，人工画面与键鼠验收仍待反馈。

仓库授权账号从[Releases 列表](https://github.com/qinjunw/CozyTown-Godot/releases)查找 tag `full-demo-budget-ui-candidate-20261011`，Release ID 为 `409783424`，保持 draft/prerelease，目标提交为 `576ae64`。[远端收据](remote-assets.json)确认 18 个附件的远端长度与 SHA-256 匹配本地。下载后按收据核对附件，再分别解压：

- `CozyTown-576ae64-windows-candidate.zip`：根目录含程序、完整运行时、九入口、README、两份手册、配置模板、来源收据与两份场景示例。`package-manifest.json` 列 214 个其他文件，`package-receipt.json` 另绑定清单自身，全部 215 个成员已逐项核对。
- `CozyTown-576ae64-offline-evidence.tar.gz`：根目录保留 `artifacts/full-demo-verification/`、`artifacts/verification/`、`artifacts/scene-replay/` 三套本轮完整证据，以及 `preparation/`、`old-real-rejection/`、`entry-smoke/`、两次包装收据和手册输入。`archive-metadata/raw-manifest.json` 列 854 个入档原件；归档收据另绑定清单自身，共 855 个 tar 成员。

原始归档不重复收录 Windows 目录与原 f955 真实输入 ZIP。198 个新运行文件由候选 ZIP 和 `windows-manifest.json` 保留。f955 真实输入及匹配程序仍在 tag `full-demo-evaluation-v2-20261011`；新程序对其中一份未改写真实包返回 `incompatible_dependency`，该拒绝的原报告与日志在 `old-real-rejection/`。

`artifacts/` 内保留本轮原报告、TRX、全部进程 stdout/stderr、固定运行记录与导出包；`preparation/` 保存命令、来源、脚本、扫描收据及退出码。`entry-smoke/` 保存九组公开 CLI 参数检查的独立临时数据，没有进入试玩目录。两份新场景示例在候选的 `examples/`；README 提供匹配新摘要的回放命令。

首包手册的历史报告相对链接缺少目标，`package-initial-link-review.json` 保留失败。归档中 `candidate/`、`manual-input/` 及初版脚本/日志属于首包；`candidate-final/`、`manual-input-final/` 属于最终包。首 ZIP 原样留在本地，未上传或作为最终候选；其长度、SHA-256、逐成员清单和收据仍入档。最终包链接指向固定历史提交，四个包内相对 Markdown 链接均存在。

`summary.json` 将本机路径替换为占位符，并保留未改写报告的 SHA-256。其他清单、收据与源差异复制原字节；`.gitattributes` 关闭行尾转换，避免改变证据哈希。大归档的扫描绑定和全部远端附件核验见对应收据；这些交付记录不代替[验收并交付完整 Demo Windows 候选](https://github.com/qinjunw/CozyTown-Godot/issues/26)或 CI 制品上传成功证据。
