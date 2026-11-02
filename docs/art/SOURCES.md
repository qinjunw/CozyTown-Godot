# 美术来源、许可状态与图集布局

本仓库已包含 [game/Art/](../../game/Art/) 中的 16 张 PNG；[catalog.json](../../game/Art/catalog.json) 登记纹理路径、角色绑定和物品格位映射。使用这些已入库资源无需访问 Unity 参考仓库。本页保留来源版本、逐文件 SHA-256、图集规格及历史核查范围。

## 引入记录

2026-10-08 生产增补：鸡舍复用 `bld_town_functions_64.png` 和匹配屋顶的 `(64,64,64,64)` 区域。复制固定参考提交的 `Assets/CozyTown/Art/Production/Props/prop_hen_states_16.png` 到 `game/Art/Props/prop_hen_states_16.png`，实际 PNG 为 48×16、1,262 字节，SHA-256 `430f2761df7a932dcd734abf178c950525bac66bf562fd062afc552c2da661a8` 与固定 Git LFS 对象一致。三格从左到右为未喂食、已喂食、待领产物；`ChickenView.cs` 按已提交的母鸡状态选择一格，以最近邻采样显示，没有重采样或改色。池塘与厨房沿用已有图集及入口。

2026-09-25 种植与睡眠增补：玩家住宅复用 `bld_town_functions_64.png` 和匹配屋顶的 `(64,0,64,64)` 区域；农田沿用空土底图。`FarmPlotView.cs` 根据已提交的地块状态绘制幼苗、叶丛和湿土，成熟时显示已有物品图集的对应作物格；`TownBackdrop.cs` 绘制家门边的床形休息路牌。这些表现没有新增或修改 PNG，位置由 `TownMap.tscn` 中的地点标记提供。

2026-09-23 玩家经济增补：复制固定参考 `68a1dde7c7099f3e9ee7e975141d0bf9d18099aa` 的 `Assets/CozyTown/Art/Production/Items/item_mvp_16.png` 到 `game/Art/Items/item_mvp_16.png`。实际 PNG 为 96×48、5,078 字节，SHA-256 `956ef71f0aab2b1ee461ffa964025f766fe88ec27a81f8ad68845d5f8c901e2e` 与固定 Git LFS 对象一致。18 格按左上起逐行、每格 16×16 映射，资源顺序见 `game/Art/catalog.json` 的 `items.resourceIds`；以最近邻采样显示，未重采样或改色。

本批于 2026-09-20 从 CozyTown Unity 参考项目复制 14 张生产 PNG 到 `game/Art/`。文件逐字节复制，未重新采样、改色或修改 Alpha；Unity 的 `.meta`、场景和编译器没有成为 Godot 运行依赖。

## 来源与使用记录

固定来源为 [CozyTown `68a1dde7c7099f3e9ee7e975141d0bf9d18099aa`](https://github.com/qinjunw/CozyTown/tree/68a1dde7c7099f3e9ee7e975141d0bf9d18099aa)。下表的“固定提交”通过该提交的 Git LFS `oid sha256` 与实际 PNG 校验；“工作区”是参考项目中尚未提交的文件，以表内 SHA-256 固定本次输入。所有源路径均为 `Assets/CozyTown/Art/Production/` 加上表内路径，目标路径为 `game/Art/` 加上相同路径。

Unity 参考仓库为私有仓库，本页指向它的提交及文件链接需要该仓库的访问权限。以下来源说明和参考记录哈希保留引入时的登记；参考源稿与工作区说明文件未包含在本仓库中，公开读者可核对已入库 PNG 的字节身份，但无法仅凭本仓库重新核验源稿内容或生成过程。

源项目的 [ArtSource 说明](https://github.com/qinjunw/CozyTown/blob/68a1dde7c7099f3e9ee7e975141d0bf9d18099aa/ArtSource/README.md)记载生成式源稿和原生像素编译流程。[T1 生成记录](https://github.com/qinjunw/CozyTown/blob/68a1dde7c7099f3e9ee7e975141d0bf9d18099aa/ArtSource/Generated/T1/GENERATION_LOG.md)记载四位居民与住宅源图由项目使用内置图像生成工具制作；[路灯说明](https://github.com/qinjunw/CozyTown/blob/68a1dde7c7099f3e9ee7e975141d0bf9d18099aa/ArtSource/Authored/L1/README.md)记载路灯直接使用原生像素源制作。

本仓库未提供这些素材的独立许可证。来源登记及文件哈希用于标识素材，公开可访问这些文件不构成素材的许可声明；本页不授予额外使用或分发权利。

| 路径 | 来源 | SHA-256 |
| --- | --- | --- |
| `Buildings/bld_npc_home_roofs_64.png` | 固定提交 | `54c6173924a0478c47b25436282100f886d9fdbb286f07031c28fb9a3a16aefa` |
| `Buildings/bld_npc_homes_64.png` | 固定提交 | `63b81137179751dff62d68fc495eef986581209d36ebd5cf7d1d57b0ac2ac3fe` |
| `Buildings/bld_town_functions_64.png` | 固定提交 | `2ef6211c75467296a8900b379f08f41f325721126d96b3fc209b5eccb9c8ee27` |
| `Buildings/bld_town_roof_foregrounds_64.png` | 固定提交 | `40b45ed71f6bd6207e9397602c308310fa03b3e4c84caed33128a9b955ceacd2` |
| `Characters/chr_player_move_24x32.png` | 工作区 | `d6b3ba948f8c064cc69e78ca39e0f293d00ebab20c3404ca2b425f4fc8774842` |
| `Characters/npc_cook_sora_move_24x32.png` | 固定提交 | `9570ac27bb637049bd01ceafb37d85cf051477a41687ebe9d5f49bfb33cceabe` |
| `Characters/npc_farmer_eli_move_24x32.png` | 固定提交 | `cbd93d9f142960260de4a47fec443ccbc29f1d0cea361d7709fb1b8980b6c979` |
| `Characters/npc_fisher_ren_move_24x32.png` | 固定提交 | `4056d3000324b3c6d9c53fac96309b8b4e9bbbb4b9821306110a4c8a1239bf66` |
| `Characters/npc_portraits_48.png` | 工作区 | `f6fb5d304a408132fffb06640b021ace680639b5b9c740cc6236fc7052dc4ebc` |
| `Characters/npc_shopkeeper_mina_move_24x32.png` | 固定提交 | `461cb20d95fe3852f7cf093da53d474c5bccbeb83d4b7c82492ac8d6d30892b2` |
| `Environment/Tiles/tile_town_base_16.png` | 工作区 | `edb51bdfd03c89b6f2ce10d71b08fdda2387b5b5a00e9c30253d101a7a64a3b6` |
| `Props/prop_town_decor_16x32.png` | 工作区 | `1cc9202e4f8cd70dfbd4ecfead43133c3784d2361a4cfacf563707789954d844` |
| `Props/prop_hen_states_16.png` | 固定提交 | `430f2761df7a932dcd734abf178c950525bac66bf562fd062afc552c2da661a8` |
| `Props/prop_town_functions_96x64.png` | 固定提交 | `87ca3014a699cf597cdfa76a48087cd623ecb56701e44b3b5c602dc0b4c9f2f8` |
| `Props/prop_town_lamp_16x32.png` | 固定提交 | `53ab31dd4073eda9676ac249656b1120d9d14953c4a31fff7b0c0902d3e9d5cd` |

在本仓库根目录运行以下 PowerShell 命令，可计算资源目录中全部已登记纹理的 SHA-256；与上表及引入记录中的物品图集哈希对照：

```powershell
(Get-Content game/Art/catalog.json -Raw | ConvertFrom-Json).textures | ForEach-Object {
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path game ($_.Substring(6)))
}
```

工作区更新的说明保留在参考项目，下面的哈希标识引入时读取的记录；它们不属于上述固定提交，当前本仓库也不包含这些说明文件的正文。哈希登记不能替代对原文件的核查。

| 参考项目内路径 | 记录内容 | SHA-256 |
| --- | --- | --- |
| `ArtSource/Generated/A2/PROMPTS.md` | 地表材质生成输入 | `ca5a27eb83e67983ba7f00ab2d3289d31455f4dbe9ae3d3dc385e56bf9c8c0de` |
| `ArtSource/Authored/A2/Characters/README.md` | 主角步行修复、头像清理与人物细节恢复 | `a1657802545b6f91ada3026ba941a4a4356c0b7cde27c74333f32787c5d3f4a8` |
| `ArtSource/Authored/A2/Decor/README.md` | 独立绘制装饰单元，清理跨格残片 | `ede604d081baf7d34cddd5c11e5a14f65f63a1170d50ef0b6de9984f794cd915` |
| `docs/audits/characters-2026-09-10/detail-restore/README.md` | 恢复人物细节的范围与参考项目验证 | `cc03d628f57eab8cc1feabca956e016344cf9e30749e18f6fcd42cae5dfc1761` |

## 切片与显示

下列行列均从 PNG 左上角开始，列向右、行向下。源矩形为 `(列 × 单元宽, 行 × 单元高, 单元宽, 单元高)`，不要采用 Unity 的底部原点换算。

| 图集 | 画布 | 单元与顺序 |
| --- | --- | --- |
| 玩家及四位居民的 `*_move_24x32.png` | 72×128 | 24×32，3 列为 idle、walk00、walk01；4 行为 down、left、right、up |
| `npc_portraits_48.png` | 192×48 | 48×48，从左到右 Mina、Eli、Ren、Sora |
| `bld_npc_homes_64.png` 及匹配屋顶 | 128×128 | 64×64，按行依次 Mina、Ren、Sora、Eli；与头像顺序不同 |
| `bld_town_functions_64.png` 及匹配屋顶 | 128×128 | 64×64，按行依次 shop、player home、kitchen/cafe、coop |
| `prop_town_functions_96x64.png` | 192×64 | 96×64，左为农田底图，右为池塘 |
| `prop_hen_states_16.png` | 48×16 | 16×16，从左到右 idle、fed、product_ready；脚底中心定位 |
| `prop_town_decor_16x32.png` | 64×64 | 16×32，上行树、灌木、红花、黄花；下行横篱笆、竖篱笆、路牌、石头 |
| `prop_town_lamp_16x32.png` | 32×32 | 16×32，左灯柱、右发光玻璃叠层；共用完整画布 |
| `tile_town_base_16.png` | 64×80 | 16×16，上行为 4 个草地变体；其余 4 行为道路连接片 |

道路格从第二行开始：孤立、东西、南北、十字；东北角、东南角、西南角、西北角；缺南、缺西、缺北、缺东的三叉；朝北、朝东、朝南、朝西的端点。每条接通边的道路只占格内第 5～10 像素，逻辑路宽不应直接从整个透明/背景单元推断。

Mina 使用棕发与棕围裙，Eli 使用草帽与绿袖，Ren 使用蓝帽与蓝衣，Sora 使用白厨师帽与橙发。角色以脚底中心定位，绘制矩形相对脚底为 `(-12,-32,24,32)`。住宅与屋顶共用 `(-32,-64,64,64)`；屋顶图集底部 38 行透明，不能独立裁边后重新居中。

纹理采用最近邻过滤，不生成 Mipmap。地图采用 16 像素格、768×512 原生世界；人物、建筑和地面保持相同像素密度。建议相机以整数比例显示，UI 保持独立字号。世界角色与建筑按脚底纵坐标排序，角色阴影位于脚底；碰撞取实体底部范围，而不是整张图片的矩形。

## 字体与验证边界

参考项目没有随项目提供的 `.ttf`、`.otf` 或 `.ttc` 字体。其 UI 代码读取 Unity 内置 `LegacyRuntime.ttf`，本批未复制该引擎字体。Windows 宿主另行配置中文系统字体并核验实际文本渲染。

复制时核对了全部 14 张图片的源/目标 SHA-256、画布尺寸、Alpha 和不透明色数：地表全部不透明，其余 Alpha 仅为 0/255；单图不透明颜色最多 29 种。检查了地表、功能建筑、住宅、五人动作、头像、装饰、池塘及路灯的现有整数倍预览。此记录证明输入与复制一致，不替代 Godot 导入、行走动画、遮挡关系或最终演示画面的验收。
