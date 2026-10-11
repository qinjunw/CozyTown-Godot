# Windows CI 制品收集验证

## 输入与问题

[限制 Windows CI 成功制品体积并保留失败诊断](https://github.com/qinjunw/CozyTown-Godot/issues/38)调整 Windows toolchain 的上传范围。提交 `99dbe5e` 的[线上运行](https://github.com/qinjunw/CozyTown-Godot/actions/runs/38134840335)完成构建、测试与导出，上传步骤因 GitHub Actions 存储配额失败。

本次选择演练读取已保留的完整离线验证目录 `artifacts/verification/20261010T083841Z-80f1068d`。该目录的 `verification.json` 记录源码为 `71be140d24ade8e78d96ab68113221dfa760e08e`、`passed=true`、`graphicalRequested=false`。它用于检查文件选择，不能代替新提交的构建或线上上传。

## 实现与条件

[workflow](../../.github/workflows/windows-toolchain.yml)保留原验证命令、检查范围、固定工具与上传 action 提交，以及 14 天保留期。

- 前续步骤成功时，`windows-toolchain-<sha>` 保留每次验证的 Windows 程序全部文件，以及运行目录顶层的 JSON、TRX、log；空收集作为错误。
- 验证步骤返回失败时，`windows-toolchain-failure-<sha>` 上传原来的完整 `artifacts/verification/` 诊断目录。判断直接读取验证步骤的 `outcome`，候选上传的配额错误不会额外触发整目录上传。
- 安装或前置模型探针失败、导致验证步骤未执行时，没有验证目录可供诊断收集。

多路径、通配符与缺文件处理符合所用固定提交的[上传 action 文档](https://github.com/actions/upload-artifact/blob/043fb46d1a93c77aae656e7c1c64a875d1fc6a0a/README.md#upload-using-multiple-paths-and-exclusions)。演练使用该提交 [package-lock.json](https://github.com/actions/upload-artifact/blob/043fb46d1a93c77aae656e7c1c64a875d1fc6a0a/package-lock.json) 锁定的 `@actions/glob 0.6.1`，并采用 [search.ts](https://github.com/actions/upload-artifact/blob/043fb46d1a93c77aae656e7c1c64a875d1fc6a0a/src/shared/search.ts) 的默认选项：跟随符号链接、隐式递归、排除点前缀隐藏文件，然后过滤目录。`success()` 和 `always()` 的含义见 [GitHub 状态检查文档](https://docs.github.com/en/actions/reference/workflows-and-actions/expressions#status-check-functions)。

## 文件与体积结果

[选择报告和逐文件 SHA-256](evidence/ci-artifact-selection/selection.json)记录所有输入文件与候选归属。实际 glob 结果与独立目录枚举逐项一致：

| 收集范围 | 文件数 | 未压缩字节数 |
| --- | ---: | ---: |
| 原完整验证目录／失败诊断 | 661 | 2,146,193,556 |
| 成功候选 | 291 | 198,549,173 |
| 成功时省去的运行数据与嵌套导出诊断 | 370 | 1,947,644,383 |

候选保留 198 个 Windows 程序文件、24 个顶层 JSON、3 个 TRX 和 66 个 log。成功收集的未压缩体积减少 90.75%。失败收集仍匹配全部 661 个文件。这里测量文件字节数；GitHub 上传后的 ZIP 大小尚未测量。

## 复现

从项目根目录安装一次性审计依赖到忽略目录，然后传入保留的完整验证目录：

```powershell
npm install --prefix .local/artifact-audit --ignore-scripts --no-audit --no-fund --save-exact '@actions/glob@0.6.1'
node docs/verification/evidence/ci-artifact-selection/audit.mjs `
  '<verification-run-directory>' `
  '.github/workflows/windows-toolchain.yml' `
  '.local/artifact-audit/node_modules/@actions/glob/lib/glob.js' `
  '<report-file>'
```

[审计脚本](evidence/ci-artifact-selection/audit.mjs)从 workflow 读取成功和失败路径，将验证目录前缀绑定到输入目录的父目录，按单次运行核对选择结果、文件大小与 SHA-256。脚本不启动游戏或模型请求，也不改变输入目录。YAML 已用 PyYAML 6.0.3 解析，`git diff --check` 通过。

## 待验证

线上制品上传尚未取得新成功证据，`remoteUploadVerified=false`。存储配额的重新计算独立于本次路径修改；此报告没有删除旧制品或改变原始评测归档。最终 PR 仍需确认线上构建、检查与制品上传全部成功。
