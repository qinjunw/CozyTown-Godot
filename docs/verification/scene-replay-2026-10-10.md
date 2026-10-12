# Godot 场景回放验证：2026-10-10

[实现 Godot 场景回放与空间分歧验证](https://github.com/qinjunw/CozyTown-Godot/issues/23)的 13 个输入包分别通过核心 CLI、Godot 源场景 headless 和 Windows 导出 headless 执行，39 次命令均得到预期结果。正常包完成回放；故障包在预期的依赖或空间边界拒绝继续。机器报告见 [verification.json](evidence/scene-replay/reports/verification.json)。

## 冻结输入

本记录只证明以下候选。后续源码会改变模拟摘要；用新构建读取这些归档包时，`incompatible_dependency` 可能是正确结果。

| 项目 | 值 |
| --- | --- |
| 源码提交 | `377f670c92a1fba022d773f69f9704ac4b3de94e`，运行前工作树干净 |
| 模拟源码 SHA-256 | `f62b1db63d11e02ec6f3162b613c6b3cc23a7b018a03db0ddf6b751be9e705d9` |
| 工具链锁文件 SHA-256 | `d743b7f66af0db06cca87b4f11bdd381f6319e3afef9a4333695d1839cb1c4ee` |
| .NET SDK / 导出运行时 | `10.0.401` / `10.0.12` |
| Godot | `4.7.2.stable.mono.official.ed1daf0bf` |
| 引擎构建 | `ed1daf0bf001b61586d9930840f2f1394092c079` |
| 物理后端设置 | `DEFAULT` |
| 内容指纹 | `659112976f925aedd7b442aaae2258790e91d2d99b1b87194cf05f11aed572a7` |
| 几何指纹 | `0a879e8e3a35e9ad038211abb49fa79b0b5b7a5a1faaa5d23efa83419b919d4e` |
| 源运行游戏程序集 SHA-256 | `906c2347601d2287e410d51bfab4dd2124938e7251d6a1026c5306971d02afd0` |
| 导出游戏程序集 SHA-256 | `91b8d2a17917eba13e63b579f49006794be1ef11cb459e222129e583b41bd35a` |

源运行和 ExportRelease 的程序集哈希分别记录，模拟源码摘要相同。导出验证进程将外部 .NET 环境指向不存在的位置，并核对运行时文件来自导出目录。

## 结果

表中的“场景”同时指源工程和导出程序；两者代码、首分歧位置和实际查询次数一致。完整预期见 [fixtures-manifest.json](evidence/scene-replay/reports/fixtures-manifest.json)。

| 输入包 | 核心结果 | 场景结果 |
| --- | --- | --- |
| gameplay | `passed` | `passed` |
| player-dialogue | `passed` | `passed` |
| player-dialogue-retired-restore | `passed` | `passed` |
| missing-scene / missing-resource | `passed` | `missing_dependency` |
| wrong-engine / wrong-radius / wrong-query / wrong-geometry / wrong-source | `passed` | `incompatible_dependency` |
| tampered-motion-fraction | `passed` | `divergence`，边界 2，`queries[0].evidence.safeFraction`，记录 `0.25`、实际 `1` |
| missing-parent | `missing_parent_prefix` | `missing_parent_prefix` |
| runtime-obstacle | `passed` | `divergence`，边界 1，`queries[0].clear`，记录 `true`、实际 `false` |

核心路径按记录中的通行事实重算规则，不承诺 Godot 物理结果。篡改 fraction 及加入运行时碰撞体的包仍通过核心事实回放，但两种实际场景入口都定位了首个空间分歧。

| 正常包 | 子边界 / 父边界 | 记录 motion 查询 | 初态身体查询 | 身体同步边界 | `IntersectShape` / `CastMotion` 调用 |
| --- | --- | --- | --- | --- | --- |
| gameplay | 949 / 3 | 3,071 | 10 | 954 | 各 3,081 |
| player-dialogue | 98 / 0 | 22 | 5 | 99 | 各 27 |
| player-dialogue-retired-restore | 4 / 27 | 19 | 10 | 32 | 各 29 |

gameplay 覆盖路线中途保存与父前缀恢复、点击路线被键盘取消、阻塞操作、四居民决定、34 次规则会话，以及商店、农田、睡眠跨日、鸡、钓鱼点和厨房。玩家文本包通过固定离线客户端覆盖四居民、菜单与失焦封存、仅对话暂停展示；旧等待存档在终结登记后恢复，不重新生成或导入未来文本。构造记录时调用实际 Godot 查询，见 [fixture-report.json](evidence/scene-replay/reports/fixture-report.json)。

统一 `verify.ps1` 通过核心 250、模型适配器 46、宿主 141 项测试，零失败、零跳过；源工程及导出的切片、经济、农田容量、农田流程、生产流程和生产容量 headless 检查全部通过。见 [回归报告](evidence/scene-replay/reports/regression-verification.json)。该次回归的 `sourceDirty=true` 来自前一轮 Godot 导入产生的 `.import` 元数据；模拟源码保持上述冻结摘要。没有执行 graphical 检查。

## 归档与复现

[archive-manifest.json](evidence/scene-replay/archive-manifest.json) 记录 ZIP 及其中原始 `replay.json` 的 SHA-256，并记录每份原始和归档报告的哈希。四个 ZIP 保留原始输入字节；故障副本由公开 fixture 命令重建，避免提交多份完整重复记录。

归档后，gameplay ZIP 再经核心、源场景和导出回放通过；runtime-obstacle ZIP 在两种场景入口均定位边界 1 的实际碰撞分歧。见 [ZIP 读取检查](evidence/scene-replay/reports/zip-verification.json)。

- [gameplay.zip](evidence/scene-replay/gameplay.zip)
- [player-dialogue.zip](evidence/scene-replay/player-dialogue.zip)
- [player-dialogue-retired-restore.zip](evidence/scene-replay/player-dialogue-retired-restore.zip)
- [runtime-obstacle.zip](evidence/scene-replay/runtime-obstacle.zip)

报告中的本机路径替换为 `<source-root>`、`<toolchain>`、`<verification-output>`、`<windows-export>` 或 `<regression-output>`，文件换行统一为 LF；数值、代码、版本、输入哈希及程序集哈希保留。原始报告和导出构建保留在 ignored 验证输出中。

要重建本记录，在独立 checkout 中检出冻结提交，按仓库锁文件准备工具链，再运行：

```powershell
tools/windows/verify-scene-replay.ps1 -ToolsRoot <installed-toolchain>
tools/windows/verify.ps1 -ToolsRoot <installed-toolchain>
```

归档文件来自证据提交；将它们作为外部输入交给冻结构建，使用 [场景回放命令](../SCENE_REPLAY.md#命令)。`runtime-obstacle.zip` 需要同时提供 `--scene-replay-obstruct` 才产生表中的诊断分歧。验证当前候选时，直接在当前源码运行脚本生成新包；不要将旧包通过与否当成新候选的唯一结果。

本次运行不调用真实模型，也不读写真实费用账本。自动结果覆盖规则、空间查询和节点绑定；画面与键鼠操作仍按人工清单验收。
