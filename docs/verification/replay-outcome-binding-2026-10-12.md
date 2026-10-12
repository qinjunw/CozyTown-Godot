# NPC 回放结果绑定与 Windows 候选复核

本记录对应[修复最终审查的模型回放绑定与验收说明](https://github.com/qinjunw/CozyTown-Godot/issues/44)。运行时冻结为 `df624084ee7baafa61162733ac4323442d359e11`，基线为 `20ebfba2186d59112d8efba5c85b66dda11ab18f`，模拟源码摘要为 `03cc1a819e8ae1d7f9adfe38faf13f3defc514265637444283432afc93c88ea1`。运行时差异仅在 `src/CozyTown.Core/Town/TownReplay.cs`；游戏/UI、世界规则、模型请求、派发、预算授权和账本行为未改。

## 修复与红绿证据

原回放只在完成记录和世界结果均无错误时核对候选。把成功包的封存 `failureCode` 改为 `request_timeout` 并重算输入指纹后，世界仍收到成功候选，原实现却报告通过。新增公开 `TownReplay.Run` 测试先出现 1 项预期失败。

修复按宿主协议推导世界应收到的候选和错误，再分别核对 `model.npcError`、`model.worldCandidate`：失败优先保留原错误并丢弃未采用的候选；成功响应才解析八字段候选，解析失败映射为 `candidate_schema_invalid`；缺候选保留空候选。协议的 200 字符检查和世界的 160 字符合法性检查仍分离。

直接比较两个错误字段会误拒合法解析失败，该中间实现的第二项红例亦保留。最终定向套件 37/37 通过，新增 14 项覆盖失败码改写、虚构错误、失败带候选、失败仍含 JSON、缺候选及 161/200/201、空/控制字符文本。五次开发运行依次为失败 1、通过 24、失败 1、通过 25、通过 37；原始 TRX、stdout/stderr 和退出收据完整保留。前四份原退出收据未捕获完整 argv；复现命令另列，不冒充原始命令收据。

## 干净离线验证

在新建 detached 检出中运行一次 `tools/windows/verify-full-demo.ps1 -ExpectedCommit <source-commit> -ToolsRoot <locked-toolchain-root>`，共享锁定工具与 NuGet 包，不复制 Godot 或构建缓存。完整门退出 0，原报告 SHA-256 为 `c47eb74137ccca84d89ea9c1ec3e44673cf23f1538ea8d594d88013784a6f3bf`，复算摘要与原始报告绑定见[离线证据](evidence/replay-outcome-binding/README.md)。

| 检查 | 结果 |
| --- | --- |
| 单元测试 | Core 338、Model 92、Host 251，共 681，通过且无跳过 |
| 宿主统一门 | 33 步通过 |
| 场景矩阵 | 13 包 × Core/源场景/Windows，共 39 项预期结果匹配 |
| 完整固定记录 | 两组共四包 × 三入口，共 12 次回放通过；Windows 默认输入亦通过 |
| Windows 文件 | 198 个，197,402,430 字节；随包运行时隔离已安装 .NET |
| 候选包入口 | 九组离线参数经公开 CLI 启动通过；未执行可见 CMD 或图形操作 |
| 包静态检查 | 全部 215 成员及 ZIP 再校验未变，四个包内相对 Markdown 链接存在 |

统一门开始时源码干净。Godot 导入后矩阵记录 `sourceDirty=true`；冻结检查仅允许生成的美术 `.import` 元数据变化，其他源码和输入保持冻结。

新候选 ZIP 为 81,649,404 字节，SHA-256 为 `0322748e44d02f84eb727019cfd5d938e3db3b4d90a587517878046acbc148b4`。完整离线归档为 1,537,789,490 字节，SHA-256 为 `a1e9c9a89f5c57e5b1b9d367f60809328e29d0ef473ebb6fdfca82ab18b509a1`；877 份原件及清单自身共 878 个 tar 成员已逐项复核，含全部红绿记录，不重复收录 Windows 文件或旧真实输入 ZIP。

实际密钥扫描全部通过：gate 为 1,130 文件、42 个 ZIP 成员及 380,126 份解码检查点；candidate 为 218 文件、215 个 ZIP 成员及 2,432 份检查点；extra 为 143 份非 gate 原件加清单、646 份检查点。范围有重叠，不合计为唯一总数。[扫描绑定](evidence/replay-outcome-binding/secret-scan-binding.json)关联包、归档、两份清单与三个原始扫描报告。私人草稿位于 [Releases 列表](https://github.com/qinjunw/CozyTown-Godot/releases)，tag 为 `full-demo-replay-binding-candidate-20261012`，ID 为 `409812159`，目标为冻结运行时，保持 draft/prerelease；[远端收据](evidence/replay-outcome-binding/remote-assets.json)确认全部 18 个附件的长度和 SHA-256 与本地匹配。

旧 `f955` 真实包在新 Windows 程序返回 `incompatible_dependency`、退出 2，实际碰撞查询为零。其[真实评测](full-demo-real-v2-2026-10-11.md)与[匹配程序的干净回放](full-demo-clean-2026-10-11.md)保持原归属；不修改摘要绕过保护。

## Standards / Spec

源码审查固定在 `20ebfba...df62408`，两个独立审查者均未发现阻断问题。Standards 未发现规范违规；新增局部 NPC 解析检查与宿主重复属于可选整理：Core 不能依赖 Godot Host，本票沿现有玩家回放模式，以公开回归约束一致性，不移动公共协议。

Spec 确认完成→结果绑定、合法失败、缺候选和协议/世界长度分离符合合同；当前说明也已更新 `f955` 真实评测和 `576ae64` 人工验收状态。原审查另提的检查点 codec 与验证进程运行器重复均后移：本票没有这两处的行为失败证据，抽取会扩大编码兼容或验证框架范围。

## 验收范围

用户已确认 [576ae64 候选](budget-ui-candidate-2026-10-12.md)的[七组操作及可选真实 M01](https://github.com/qinjunw/CozyTown-Godot/issues/26#issuecomment-6111460458)通过。两版 `src/game/tools` 的 Git 对象和执行权限逐项比较，仅 `TownReplay.cs` 改变，其余 172 个文件相同，见[范围核对](evidence/replay-outcome-binding/manual-scope-equivalence.json)。本轮仅重核回放变化影响的离线入口，不要求重复整套人工操作，不把原反馈或 `f955` 真实样本记为新源码实机或真实评测通过。运行与回放验证不配置真实客户端、不发送 HTTP、不打开图形窗口。验证及封装未访问真实预算账本；凭据仅由扫描进程读入内存进行字节比对，不进入归档或报告。CI 制品上传仍受配额阻塞。
