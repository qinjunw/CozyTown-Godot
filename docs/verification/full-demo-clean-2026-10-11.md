# 冻结版完整 Demo 的干净 Windows 构建

对应[准备完整 Demo 干净构建与人工验收包](https://github.com/qinjunw/CozyTown-Godot/issues/42)。实际构建使用独立、初始无修改的检出，固定在真实评测源 `f9551c19eb1866ba79cc1312ca7821004fbb7fa1`。本轮没有复制 `.godot`、`bin/obj` 或 Unity 缓存；构建、回放与包装进程没有读取实际凭据/预算账本，没有请求图形验收或发送真实模型 HTTP。文档与包装工作使用最新集成分支的另一棵 worktree。

## 输入与独立构建

使用既有锁定工具链 Godot `4.7.2.stable.mono.official.ed1daf0bf`、.NET SDK `10.0.401`，复用工具安装目录及 NuGet 包缓存，项目导入、构建输出和导出均在新检出生成。入口为：

```powershell
git worktree add --detach '<clean-checkout>' f9551c19eb1866ba79cc1312ca7821004fbb7fa1
pwsh -NoProfile -File tools/windows/verify.ps1 -ToolsRoot '<shared-tools-root>'
```

新门目录为 `artifacts/verification/20261011T154049Z-f2e88d5a/`。Core `324/324`、Model `92/92`、Host `251/251`，共 667 个测试通过，无失败或跳过；33 个进程全部退出 0、无超时。构建、五类内容、源场景及 Windows 导出中的移动、经济、种植、生产与容量检查通过，`graphicalRequested=false`。原门报告 SHA-256 为 `0ac6c2154a1384db0c07e981fedaf4464a74c4ed02258cb22a365147f6839102`。

Windows 检查使用不存在的 `DOTNET_ROOT`、`DOTNET_ROOT_X64`，`PATH` 仅保留 Windows 系统目录，加载的 `System.Private.CoreLib.dll` 来自本次导出。新程序由 198 个文件组成，共 197,401,682 字节；它与此前封存的 Windows 程序分别保留清单，不能把旧 ZIP 当作本轮干净构建产物。导入后仅已有美术 `.import` 元数据被 Godot 重写，实际模拟源码没有改动。

## 四份真实记录的新程序回放

输入仍为[已封存 v2](full-demo-real-v2-2026-10-11.md)的四份完整 ZIP，本轮只新增 Windows 场景回放，不重跑原 Core/source 回放，不补付费样本。入口为：

```text
<new-windows>/CozyTown.exe --headless -- --scene-replay=<frozen-real-package.zip> --report=<new-report.json>
```

回放包装脚本复用冻结 `verify-full-demo.ps1` 中的公开校验函数，核对输入 ZIP 哈希、全部本分支与父前缀边界、每段初态身体查询、motion 及实际引擎查询次数，并再次核对新 Windows 清单未变。过程使用独立日志和不存在的系统 .NET 根目录。

四包均退出 0、通过，首次执行的全部报告和日志保留。模拟源码摘要为 `bb54adc96757808ef36220c46c85539635faa244c92744b9a19bb9277df946c6`；内容、几何、引擎构建、物理后端和源码提交与记录匹配。新 ExportRelease 程序集 SHA-256 为 `9553e29f88a538aefb7a23ad5179002df335257310847d33fa434270711c8390`，与旧构建分别记录；允许同源码的跨构建检查遵循[场景回放合同](../SCENE_REPLAY.md)。

| 输入 | 本分支边界 | 含保存父前缀边界 | motion | 初态身体 | IntersectShape / CastMotion 各 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 基准父包 | 32428 | 32428 | 2463 | 5 | 2468 |
| 基准子包 | 1823 | 34246 | 2586 | 10 | 2596 |
| 对照父包 | 45775 | 45775 | 2866 | 5 | 2871 |
| 对照子包 | 14802 | 60576 | 3785 | 10 | 3795 |

完整新回放汇总原文件 SHA-256 为 `0d1c505885d9bb7a1caf54d1449ed6bc184f31c4b268d544193b6ebb04191a9e`。仓库内[验证摘要](evidence/full-demo-clean/summary.json)保存四包输入和报告哈希、查询计数及新构建依赖，机器路径使用占位符；未改写的报告保存在原始归档。

## 包装、人工清单与限制

候选保留完整 198 文件导出，随包 .NET；包内根目录的 `Start.cmd` 固定启动普通离线世界，`Start-1920.cmd` 共用普通档。资源对照、四类异常对话及两类容量入口分别写入独立 `user-data/<mode>/`，不自动读取旧档，不转发额外命令参数。保存、运行记录、观察导出与启动日志均可定位。

包内说明链接[七组主路径](full-demo-clean-manual.md)及[详细步骤](full-demo-clean-manual-details.md)。默认 NPC 使用规则，玩家生成使用固定客户端。可选真实模式只有外部配置模板与显式 `--execution-purpose=manual` 命令，没有真实启动脚本；用户复用现存共享 v2 账本，NPC/玩家/失败/恢复共用保留八次，不初始化、不返还、不扩额。

冻结版已知把观察预算总次数分母写成 48；v2 实际总上限为 96，manual 阶段共用 8。后继[显示实际预算上限与人工阶段用量](https://github.com/qinjunw/CozyTown-Godot/issues/43)提供新版用户候选，最终只对新版做一次综合人工验收。本包用于冻结版清洁构建与真实回放证明；没有人工通过结论，也不替代[限制 Windows CI 成功制品体积并保留失败诊断](https://github.com/qinjunw/CozyTown-Godot/issues/38)。

候选 ZIP 为 `CozyTown-f9551c1-clean-windows.zip`，76,040,927 字节，SHA-256 为 `006c7c0051332e211d9c612bb47117d774c7208928d23c62f9ad3a5a138a0a95`。[包清单](evidence/full-demo-clean/package-manifest.json)列出除其自身外的 211 个文件，清单自身 SHA-256 为 `a5018e338af8b80d1739a66bab7eeecf9f11706f47387f980a88b760f17ddbb6`。[打包收据](evidence/full-demo-clean/package-receipt.json)逐成员验证 ZIP 的全部 212 个文件，无额外或重复成员。源 Windows 的 198 文件清单在打包前后保持一致。

九组包内离线参数在导出 CLI 中各执行一次 `--headless --quit-after 30`，使用独立临时数据目录，全部退出 0 并生成 `rules` 模式运行记录。此检查不执行可见 CMD、不验证窗口尺寸、点击或文字渲染；这些仍由人工清单承担。

原始归档 `CozyTown-f9551c1-clean-evidence.tar.gz` 为 1,062,395,708 字节，SHA-256 为 `480488df8a9ec9d4aeb4665ac35f469c35d1b3f9b7c459114ed9982961a5b3f5`。[原始清单](evidence/full-demo-clean/raw-manifest.json)列 579 个文件、1,950,665,240 字节；[归档收据](evidence/full-demo-clean/archive-receipt.json)另绑定清单自身，全部 580 个 tar 成员长度与哈希通过，入档原文件在打包前后未变。归档包含完整新门目录、四次回放、九组入口检查和执行脚本，排除重复 Windows 二进制及已封存真实输入 ZIP；解压布局见[证据说明](evidence/full-demo-clean/README.md)。

[凭据扫描绑定](evidence/full-demo-clean/secret-scan-binding.json)关联两个归档与清单的 SHA-256。扫描覆盖完整干净门的 665 个文件和 99,000 个解码检查点，补充扫描覆盖新原始文件及候选文件共 328 个文件、212 个 ZIP 解压成员和 674 个解码检查点；两范围有重复文件，不合计为唯一文件数。JSON 内 base64/Brotli 世界与宿主检查点均解码检查；tar 内容由逐成员收据绑定已扫描源文件。实际凭据只由扫描执行者在内存中用于比对，不写入收据或制品。

[远端回执](evidence/full-demo-clean/remote-assets.json)确认全部 12 个附件的长度及远端 SHA-256 与本地相同。仓库授权账号可从[Releases 列表](https://github.com/qinjunw/CozyTown-Godot/releases)查找 tag `clean-demo-f955-20261011`，Release ID 为 `409773282`，保持 draft/prerelease，目标为冻结 `f9551c1`。附件可取与线上 CI 制品上传状态分别记录。
