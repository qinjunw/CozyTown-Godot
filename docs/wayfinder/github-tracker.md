# GitHub tracker

本项目使用 `qinjunw/CozyTown-Godot` 的 GitHub Issues。地图、子任务、领取、阻塞和结论只在 GitHub 维护；仓库保存产品目标、术语、研究与验证证据。

## Wayfinding operations

| 操作 | 表达 |
| --- | --- |
| 地图 | 带 `wayfinder:map` 标签的 Issue，按 Destination、Notes、Decisions so far、Not yet specified、Out of scope 组织 |
| 票据 | 地图的原生子任务，使用 `wayfinder:research`、`wayfinder:prototype`、`wayfinder:grilling` 或 `wayfinder:task` |
| 领取 | 开始前分配给执行开发者；未分配表示尚未领取 |
| 阻塞 | GitHub 原生 blocked-by 关系 |
| 前沿 | 开放、未分配、且所有阻塞票据已关闭的子任务 |
| 结论 | `Resolution` 评论保存回答，链接研究、原型或验证，再关闭票据 |
| 地图索引 | 每个已解决票据一行摘要和以标题命名的链接；开放票据通过子任务查询 |

使用 `gh issue create --parent` 创建子任务，待所有相关票据存在后，以 `gh issue edit --add-blocked-by` 连接依赖。开始前运行 `gh issue edit --add-assignee '@me'` 领取。

```powershell
gh issue view <map-number> --repo qinjunw/CozyTown-Godot --json title,body,state,url
gh api repos/qinjunw/CozyTown-Godot/issues/<map-number>/sub_issues --paginate
gh api repos/qinjunw/CozyTown-Godot/issues/<ticket-number>/dependencies/blocked_by --paginate
```

正文和评论先写到 UTF-8 文件，再以 `--body-file` 提交。更新地图前重新读取正文，保留其他会话的修改。重复执行前按标题与父子关系检查，避免重复建票。

Wayfinder 建图阶段建立路线和研究证据。未获得人类回答的 HITL 票据保持开放，不由代理模拟用户作答。研究票据可并行，在 `research/<name>` 分支保存研究文档；Resolution 链接该分支的确定提交，并注明是否已合入主分支。

包含实施的地图应明确范围和验收条件；建图本身不表示整套 Demo 已完成。每次领取当前可执行票据，保留公开验收条件和交付证据。

实机验收由用户人工执行。先完成自动检查和可运行候选，再交付列表，逐项写明前置状态、操作与预期结果，并保留已经取得的实机证据。需要人工验收的项目在用户反馈前保持待测；反馈通过后补证据与 Resolution，再关闭对应票据。

实施使用单一集成分支，按原生依赖前沿并行使用独立 worktree；决策待答时继续处理独立任务。Resolution 标明已验证内容及其所在的确定提交，区分集成分支成果与主分支交付。最终 PR 在全部依赖、自动检查及人工验收完成后合并。
