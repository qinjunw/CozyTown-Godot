# Godot 场景回放

场景回放通过游戏命令加载[同一输入包](REPLAY.md)，重新执行 C# 规则和 Godot `IntersectShape`、`CastMotion`，在首个依赖错误或过程分歧停止。它与核心事实回放使用同一命令分派器、父分支校验和完整状态比较器。报告的 `mode` 分别为 `godot_scene_replay` 和 `core_fact_replay`。

## 命令

先用仓库固定工具链构建并导入项目，再使用源场景或 Windows 导出程序。输入接受 `replay.json`、运行目录或导出 ZIP。

```powershell
<godot-console> --headless --path game -- --scene-replay=<package> --report=<absolute-report.json>
<windows-export>/CozyTown.exe --headless -- --scene-replay=<same-package> --report=<absolute-report.json>
```

通过返回 0，空间或状态分歧返回 1，缺依赖、版本不符或无效输入返回 2。报告包含输入文件 SHA-256、实际命令、预期及实际场景依赖、首个分歧的边界/命令/字段、实际查询次数和运行时位置。父前缀失败保留该前缀的边界与命令，字段以 `parent.` 标识所属层级。

命令在创建普通运行目录、模型客户端和预算账本之前执行。提供模型配置会被拒绝。录制的模型响应仍作为普通回放输入验证，不产生网络请求或现实预算更新。

## 场景依赖

新 Godot 记录的 `Scene` 描述包括场景与地图资源路径、初态名、内容和提取几何指纹、Godot 版本与引擎构建哈希、物理后端、身体半径、查询配置、模拟源码摘要和构建来源。回放要求对应资源存在于当前加载的源工程或导出包中；输入包不代替引擎和场景资源。

查询合同 `godot-circle-intersect-cast-v1` 使用半径 5 的圆，mask 1，只碰撞身体，margin 为 Godot float `0.001f`。起点 `IntersectShape` 必须无重叠，`CastMotion` 的 safe fraction 必须至少为 `0.9999f`。记录每次调用的实体、起终点、半径、顺序、配置、起点是否无重叠、safe fraction 和最终通行结果。场景路径使用实际查询结果继续规则，不能以包内 `clear` 代替查询。浮点坐标沿用包内数值版本的绝对容差 `0.00001`，查询配置、布尔值和 motion fraction 精确比较。

构建目标根据游戏脚本、独立核心、模型适配器及构建配置生成 `SimulationSourceDigest`。预检比较该摘要，构建提交及程序集 SHA-256 用于记录实际来源；读取程序集哈希前核对文件的 MVID 与已加载模块一致。仅修改文档的提交不改变摘要；Debug 源运行和 ExportRelease 可验证同一模拟源码。源码摘要相同不保证任意工具链等价，工具链与引擎仍由仓库锁文件和场景依赖约束。

旧核心包缺少场景描述或逐次查询详情时，仍可运行其支持的核心回放；场景命令报告 `missing_dependency`。内容/几何、引擎、身体尺寸、物理后端、查询配置或源码不符报告 `incompatible_dependency`。这些结果不能计作场景通过。

## 身体与显示

玩家和居民的权威位置保存在独立核心的 double 坐标中。`TownActor` 是 `Node2D`；场景使用临时圆查询移动，而显示节点按现有规则取整到像素。回放按稳定实体身份唯一绑定节点，每个状态边界同步并核对显示位置；每段初态及恢复初态额外做五个零位移实际查询，拒绝身体与地图重叠。

报告分别计数记录中的 motion 查询、初态身体查询和身体同步边界。显示节点的像素取整不参与核心坐标容差，也不充当物理查询结果。场景结果验证逻辑、空间过程和节点绑定；画面完成度及键鼠手感按人工清单验收。

## 自动验证

```powershell
tools/windows/verify-scene-replay.ps1 -ToolsRoot <installed-toolchain>
```

脚本从公开 `--scene-replay-fixture` 入口生成实际 Godot 记录，对相同包执行核心 CLI、源场景 headless、Windows 导出 headless。样例覆盖玩家路线中途保存和父前缀恢复、键盘取消点击路线、阻塞操作、四居民规则互动，以及商店、农田、床、鸡、钓鱼点和厨房。玩家文本包覆盖四居民固定客户端、菜单/失焦封存、仅对话暂停展示，以及旧等待存档读取后终结旧生成且不导入未来文本。生成的 manifest 固定正常和故障包的预期报告；输出位于 ignored `artifacts/scene-replay/`。

故障包分别缺少场景描述、资源或父前缀，并更改引擎、几何、半径、查询配置、源码摘要和记录中的 motion fraction。`--scene-replay-obstruct` 仅用于此验证：预检后在第一段较长移动的终点加入实际静态碰撞体，证明查询路径能在首个受影响的空间边界停止。它不会修改源地图文件。
