# DeepSeek 接口探针

此独立 .NET 控制台程序验证首轮登记的 DeepSeek 请求配置和结构化候选，不连接 Godot 世界。四个合成输入分别提供居民自身状态及可见对象，模型可以等待、接近或邀请；程序只检查候选，不执行游戏行动。

## 离线检查

使用仓库安装的 SDK，在仓库根目录运行：

```powershell
& ./.local/tools/dotnet/dotnet.exe run --project tools/experiments/CozyTown.ModelProbe --configuration Release -- --self-test
```

离线检查使用临时账本、假凭据和固定响应，不访问真实服务。程序不依赖外部 NuGet 包。

## 已登记的真实实验

首轮配置与准入规则见[真实模型接入合同](../../../docs/REAL_MODEL_CONTRACT.md)。本程序固定预算身份、人民币 5 元、48 次总上限和探针阶段 4 次上限；只运行探针阶段。首个失败停止运行，已开始的探针阶段拒绝再次执行。新建输出目录、账本或进程不构成重新实验的授权。

真实运行需使用本机配置中登记的同一账本，以下参数为占位符：

```powershell
& ./.local/tools/dotnet/dotnet.exe run --project tools/experiments/CozyTown.ModelProbe --configuration Release -- --credential-file '<private-credentials.json>' --ledger-file '<registered-budget.json>' --output-dir '<new-evidence-directory>'
```

凭据文件在仓库外，根对象中恰好一个子对象具有 `Todo=CozyTown` 与非空 `testingAPIKey`。程序只输出错误代码；余额查询只保留账户可用状态与币种。记录包含合成输入、候选、用量、尝试身份和预算快照。共享证据前检查其内容，保留全部成功、失败及中断样本。

发送前持久占额并写入请求记录。账本使用独占锁和原子替换，已占用费用不会因重启回退。未知用量继续占额；不明在途继续占槽。探针对任何失败停止后续发送，其策略比游戏合同中仍允许其他合格请求排队的情况更严格。该辅助程序不实现游戏的队列、公平轮转、保存恢复、迟到结果受理或世界行动验证。

账本及其 `.lock` 标记必须共同保留。账本丢失或损坏时停止核验，不从 `.previous` 或游戏旧档自动恢复；旧副本可能缺少已经发送的尝试。后续场景实验须承接同一预算记录及已发生次数，不能将探针证据中的快照当作可回滚的实时账本。
