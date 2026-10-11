# 冻结版干净构建证据

本目录属于运行时 `f9551c19eb1866ba79cc1312ca7821004fbb7fa1`，结果见[构建报告](../../full-demo-clean-2026-10-11.md)。`summary.json` 只汇总本轮新构建、四次 Windows 场景回放和九组离线参数检查；真实模型指标仍归[第二轮评测](../../full-demo-real-v2-2026-10-11.md)，本轮未发送真实模型请求。

仓库授权账号从[Releases 列表](https://github.com/qinjunw/CozyTown-Godot/releases)查找 tag `clean-demo-f955-20261011`，Release ID 为 `409773282`，保持 draft/prerelease。`remote-assets.json` 核对全部 12 个附件的远端长度及 SHA-256。下载后先按收据核对附件长度及 SHA-256，再分别解压：

- `CozyTown-f9551c1-clean-windows.zip`：根目录是程序、完整运行时、九个离线入口、说明、手册和配置模板。`package-manifest.json` 列 211 个其他文件，`package-receipt.json` 另绑定清单自身，逐成员核对全部 212 个文件。
- `CozyTown-f9551c1-clean-evidence.tar.gz`：根目录保留 `clean-verification/`、`replay/`、`entry-smoke/`、`preparation/`、`package/` 及 `archive-metadata/clean-raw-manifest.json`。`raw-manifest.json` 列 579 个原始文件，路径相对于此解压根；`archive-receipt.json` 另绑定清单自身，共 580 个成员。

原始归档排除重复 Windows 二进制与已封存的四个真实输入 ZIP。新 Windows 的 198 文件必须匹配 `windows-manifest.json`；不能换成旧 `windows-replay-program.zip` 来证明本轮干净构建。原始真实 ZIP 从 tag `full-demo-evaluation-v2-20261011` 的真实归档取得，按 `summary.json` 中四项 `inputSha256` 核对，保留完整包及其递归父前缀。

`clean-verification/verification.json`、TRX、全部进程日志和运行目录来自本轮独立检出；`replay/` 保存四个新 Windows 报告及 stdout/stderr；`preparation/` 保存前置状态、命令模板、执行脚本和退出码。九组入口检查的临时数据留在 `entry-smoke/`，未混入候选 ZIP 的普通试玩目录。可见 CMD、键鼠、窗口尺寸及文字渲染没有被自动检查。

`summary.json` 把机器路径换成占位符，并保留未改写报告的 SHA-256；其他清单与收据复制原字节。本目录 `.gitattributes` 对 JSON 关闭行尾转换，避免改变这些哈希。原始完整记录和启动数据仍由关联制品保留，摘要不替代原文件。

`secret-scan-binding.json` 绑定两个归档与清单，嵌入完整门及包装补充扫描结果。检查包含 ZIP 解压成员和 JSON 内 base64/Brotli 检查点；扫描源文件与 tar 内容通过逐成员收据关联。实际凭据不进入制品。原始扫描收据作为单独 Release 附件保存，不因此重写已冻结归档。

人工主路径及故障附录随包提供，均待用户反馈。冻结版的预算分母显示问题已指向[显示实际预算上限与人工阶段用量](https://github.com/qinjunw/CozyTown-Godot/issues/43)，最终用户使用其新版候选；本归档保持冻结版构建和回放的原归属。
