# NPC 回放绑定修复的离线证据

本目录属于运行时 `df624084ee7baafa61162733ac4323442d359e11`，结果见[复核报告](../../replay-outcome-binding-2026-10-12.md)。`summary.json` 绑定完整门的 681 项单元测试、33 个宿主步骤、39 项矩阵预期结果、四份新固定包的 12 次回放及旧真实包的预期拒绝；`candidate-summary.json` 绑定九入口参数、四个包内链接与 215 个文件未变复核。全部运行验证为零 HTTP、headless，不冒充画面与键鼠观察。

仓库授权账号从[Releases 列表](https://github.com/qinjunw/CozyTown-Godot/releases)查找 tag `full-demo-replay-binding-candidate-20261012`，Release ID 为 `409812159`，保持 draft/prerelease，目标为 `df62408`。[远端收据](remote-assets.json)确认全部 18 个附件的远端长度与 SHA-256 匹配本地。下载后按收据核对附件，再分别解压。

- `CozyTown-df62408-windows-candidate.zip`：根目录含程序、完整运行时、九入口、README、两份手册、占位符配置模板、来源收据与两份本轮场景示例。`package-manifest.json` 列 214 个其他文件，`package-receipt.json` 另绑定清单自身，全部 215 个成员已逐项核对。
- `CozyTown-df62408-offline-evidence.tar.gz`：根目录保留 `artifacts/full-demo-verification/`、`artifacts/verification/`、`artifacts/scene-replay/` 三套完整证据，以及 `preparation/`、`old-real-rejection/`、`entry-smoke/`、`red-green/`、`candidate-final/` 收据及 `manual-input/`。`archive-metadata/raw-manifest.json` 列 877 个原件，归档收据另绑定清单自身，共 878 个 tar 成员，已逐成员核对长度与 SHA-256。

归档不重复收录 Windows 目录与原 f955 真实输入 ZIP。198 个新运行文件由候选 ZIP 和 `windows-manifest.json` 保留。新程序对未改写的 f955 真实包返回 `incompatible_dependency`，退出 2，碰撞查询为零；原报告与日志在 `old-real-rejection/`。f955 真实输入和匹配程序仍在 tag `full-demo-evaluation-v2-20261011`，576 匹配程序仍在 `full-demo-budget-ui-candidate-20261011`。

`red-green/` 保留首次虚假成功红例、直接比较错误误拒 schema 的第二红例，以及三次后续通过的原始 TRX、stdout/stderr 和退出收据。`red-green-summary.json` 逐文件绑定这些原件，列出最终 37 个定向用例；前四份原退出收据仅捕获退出码，复现命令另列且明确不是原始命令收据。

`preparation/` 保留来源差异、范围等同性、统计脚本、两轴源码审查、命令与扫描收据。`entry-smoke/` 保留九组公开 CLI 参数检查的独立临时数据，没有写入候选目录，也未执行可见 CMD。两份匹配本轮摘要的场景示例位于候选 `examples/`，包内 README 提供 headless 回放命令。

`manual-scope-equivalence.json` 逐项比较 576 与 df 的 `src/game/tools` Git 对象及执行权限：只有 `TownReplay.cs` 改变，其余 172 个文件相同。原 576 的七组与 M01 人工通过仍归原源码；本轮重核受影响的离线入口，不重复整套人工或新增真实调用。

`summary.json` 将本机路径替换为占位符，同时保留未改写报告的 SHA-256。其他清单、收据与源差异复制原字节；`.gitattributes` 关闭行尾转换。[扫描绑定](secret-scan-binding.json)关联包、归档、两份清单和三个通过的实际密钥扫描报告：gate 为 1,130 文件/42 ZIP 成员/380,126 份解码检查点，candidate 为 218/215/2,432，extra 为 144 文件/646 份检查点。范围有重叠，不合计为唯一文件数。交付记录不代替 CI 制品上传成功证据。
