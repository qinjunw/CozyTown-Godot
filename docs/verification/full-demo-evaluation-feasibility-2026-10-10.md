# 完整 Demo 跨日输入可行性

2026-10-10，对应[确定跨日真实模型评测与玩家对话共用预算](https://github.com/qinjunw/CozyTown-Godot/issues/24)。本次使用 `29c3c1c55fa63a3d09f049f1d43a4450314afa42` 的已实现核心、固定规则候选及场景提取内容，验证缩减为两个日间窗口的输入能否运行。没有调用真实模型。

## 输入与方法

Godot `4.7.2.stable.mono.official.ed1daf0bf` 以 headless `--validate-content --scenario=baseline` 提取当前场景。内容指纹为 `659112976f925aedd7b442aaae2258790e91d2d99b1b87194cf05f11aed572a7`，manifest 与 definitions 分别和已归档的[生产内容](evidence/player-production/content-baseline.json)一致。完整提取文件 SHA-256 为 `61d0a3e7ff3cea692a527e59e1cc16de3c319c601ae96c6a22df594292468d2d`。

诊断程序使用 .NET `10.0.401`，只引用独立核心。基准采用普通初态；对照将四名居民的初始库存替换为 `game/Content/counterfactual.json`，其余定义不变。行走使用内容路线和 `SetPlayerPosition`，每步最多 3.1 像素，接受 0.05 秒现实时间与 0.025 世界分钟。两个决定窗口各接受 180 秒模拟现实时间；候选来自普通 `GetDecisionRequests` 和 `CreateRuleCandidate`，不指定配对。

程序通过普通入口买饲料 2、胡萝卜种子 1，种植浇水并喂鸡；第一个窗口后走到床，睡眠 12 小时、保存恢复，再睡眠 12 小时；跨晨间后领取一份鸡蛋并再次喂鸡，随后运行第二个窗口。[诊断源文件](evidence/full-demo-evaluation/feasibility-source.cs.txt)、[项目文件](evidence/full-demo-evaluation/feasibility-project.csproj.txt)和[完整结果](evidence/full-demo-evaluation/feasibility.json)保留输入及结果。

复核时将源文件、项目文件分别复制为 `.local/evaluation-feasibility/Program.cs` 与 `Feasibility.csproj`；将当前 headless 提取结果写入同目录 `content-baseline.json`，再运行 `dotnet run --project .local/evaluation-feasibility/Feasibility.csproj --configuration Release`。这份诊断不是正式回放包，路径约定仅用于复核本次可行性输入。

## 结果

两组均通过程序内的操作成功、可达路线和每窗口四人均取得机会检查。每组接受 386.5 秒模拟现实时间；世界从首日 09:00 推进至次日约 12:13，执行一次晨间结算与一次恢复。两个窗口的世界区间约为首日 09:08～10:38、次日 10:43～12:13。

| 初态与窗口 | 艾里 | 米娜 | 阿仁 | 小空 |
| --- | --- | --- | --- | --- |
| 基准首日 | 21 | 19 | 18 | 19 |
| 基准次日 | 5 | 4 | 3 | 3 |
| 对照首日 | 19 | 19 | 17 | 18 |
| 对照次日 | 6 | 4 | 5 | 5 |

表内为固定规则客户端取得的请求次数，未应用真实阶段发送上限，不是付费样本或真实覆盖率。两组最终钱包均为 255，背包有鸡蛋 1；胡萝卜增长 1 天、尚未成熟，母鸡已领取一次并再次喂食。

## 限制与后续

诊断中的现实时间由输入累计，程序 CPU 运行时间不代表实际 Godot 试玩时长。Godot 仅负责本次内容提取，行走未使用真实物理查询。玩家模型对话、完整输入回放、付费请求延迟和阶段配额尚未参与本次诊断；正式固定输入必须在这些功能合入后重新验证并冻结。

这组结果证明已有玩法可支撑两天、一次晨间的评测安排。成熟收获、钓鱼、配方及出售由完整固定输入门覆盖，不从本次一日生长推定通过。模型是否在发送额度内满足四人覆盖和质量阈值，仍由实际评测决定；失败不以固定候选补数。
