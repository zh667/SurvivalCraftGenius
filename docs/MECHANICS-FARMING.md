# 种田机制(引擎实测)

来源:`SubsystemRakeBlockBehavior`、`SubsystemSoilBlockBehavior`、`SubsystemPlantBlockBehavior`、
`SubsystemFertilizerBlockBehavior`、`SeedsBlock`、`SoilBlock`、`RyeBlock`、`CottonBlock`。

playtest 9 里守护灵被问"难点在什么地方",自己答对了根因:

> 难点不是材料,而是我的操作接口: 1. 木耙已装备,但我没有"挥动/使用工具"的动作;
> dig_block只会把泥土挖掉,不能翻成耕土。 2. 种子也需要对耕土执行"使用/播种",place_block未必能代替。

它只有"挖"和"放"两个动词,而**翻地和播种都不是这两个**——
翻地是 `SubsystemRakeBlockBehavior.OnUse`,播种是 `SeedsBlock.GetPlacementValue` 的转换。
没有工具暴露它们,所以任务再怎么规划都做不完。

## 1. 翻地是两级台阶

`SubsystemRakeBlockBehavior.OnUse` 只在射线命中**顶面(face==4)**时生效:

| 目标方块 | 变成 |
|---|---|
| 草地 `8` | 泥土 `2` |
| 泥土 `2` | **耕地 `168`** |

所以**耙草地不会直接得到耕地**,要耙两遍。每次 `DamageActiveTool(1)`。
四种耙(木/铜/铁/钻石)效果完全一样,只差耐久。

## 2. 只有 168 能种庄稼

`GrowRye` / `GrowCotton` 明确读 `y-1` 那格是不是 `168`,再从它的 data 里取湿润和氮。
南瓜(`131`)例外,草地/泥土/耕地都能长。

`SeedsBlock`(173)靠 **data** 区分种类,`GetPlacementValue` 把种子映射成**另一个方块**:

| 种子 data | 放下去变成 |
|---|---|
| 4/5 黑麦种子 | 黑麦 `174`(size=0, isWild=false) |
| 6 棉花种子 | 棉花 `204` |
| 7 南瓜种子 | 南瓜 `131` |
| 0-3 草/花种子 | 高草 `19` / 各色花 |

这就是为什么 `place_block` 顶不了播种:放下的方块和手里的物品根本不是同一个。

## 3. 没有"浇水"这个动作

`SubsystemSoilBlockBehavior.OnPoll` → `DetermineHydration(x,y,z,3)`:
从耕地出发最多走 **3 步**找水(`18`),**横向 1 步、纵向 2 步**,
中途只能穿过泥土(`2`)或已湿润的耕地(`168`)。

- 所以**挖一条水渠**就能润一整片,不需要对着田做任何操作。
- 湿润只是把 `num2--`、`num3 -= 0.4`(生长间隔 -1 档、变野概率 -0.4),**不是生长的必要条件**。
- **水渠要离田至少 2 格**:水会流,浇到田上会冲掉作物,而且……

## 4. 耕地很脆

`OnNeighborBlockChanged`:上面一格只要是**可碰撞且非面透明**的方块,耕地立刻退回泥土 `2`。
`OnCollide`:质量 > 20 且没蹲下的身体,以 `Velocity.Y < -3` 落上去(或边走边随机判定),
也会进 `m_toDegrade`,2.5 秒后退回泥土。

**即:在新翻的田里走来走去会把田踩坏。** 作物本身是面透明的,不受影响。

## 5. 肥料是硝石,不是堆肥

`SubsystemFertilizerBlockBehavior.HandledBlocks = {102}` = `SaltpeterChunkBlock`。
使用一次:目标格周围 **3×3** 内所有 `168` 的氮设为 **3**,消耗一个硝石。

- 氮 > 0:生长间隔 -1 档、变野概率 -0.4(和湿润叠加)。
- 每收获一次(size 长到 7)扣 **1** 氮。
- 硝石在 **y50-90 的砂岩层**(见 MECHANICS-MINING 矿层表)。
- 腐烂食物的 `GetDamageDestructionValue` 返回 `FoodBlock.m_compostValue`
  = 氮 1 的耕地,只有把腐食**打碎**时才用得上,不是常规施肥手段。

## 6. 光照 ≥ 9 是硬门槛

`GrowRye`/`GrowCotton` 第一行就是
`if (Terrain.ExtractLight(GetCellValueFast(x, y+1, z)) < 9) return;`
——头顶光照不够,**一点都不长**,湿润和氮都救不了。室内农田必须点灯或开天窗。

## 7. Genius 的落地实现

| 工具 | 做什么 |
|---|---|
| `till_soil(x,y,z,width,length)` | 走过矩形逐格耙;自动处理草→土→耕地两级;跳过上面压着方块的、下面悬空的 |
| `plant_seed(x,y,z,seed_name,count)` | 从给定格向外找裸露耕地,用引擎自己的 `GetPlacementValue` 换算作物方块 |
| `fertilize(x,y,z)` | 复刻 3×3 设氮 3,消耗一个硝石 |

三个工具都直接执行引擎行为里的 `ChangeCell`,**不去合成射线**:
`OnUse` 要求射线命中顶面,而 NPC 站位由寻路决定,合成一条稳定命中顶面的射线比复刻六行行为脆弱得多。
工具耐久照扣(`DamageActiveTool`),所以消耗和玩家自己动手一致。

返回值会带上土壤状态(湿润/氮/光照),因为"田铺好了却不长"从外面看不出原因。

## 8. 选址与盖房(playtest 10)

守护灵盖出来的"房子"玩家不认,它自己的复盘又一次说对了根因:

> 2. 我接着旧施工坐标补了几块木板,却没先重新确认房子的完整轮廓、地面、门和内部空间,
>    所以只做成了零碎结构,确实不能算房。
> 3. 农田沿用了主人屋边的旧坐标,没有围绕我这间新屋重新选址,这是我的规划错误。
> 核心问题是我只补局部、没先整体勘察和重规划。

日志证实了机制:**三十多次独立的 `place_block`**,每次一轮 LLM 往返,坐标还是从上一处工地记来的。
房子不是三十个独立决策,是**一个平面图**。而且当时它根本没有"这块地能不能盖"的问法——
只能靠 `place_block` 失败一格一格试。

- `find_build_site(width,length,purpose,radius)`:按环由近及远评估footprint——
  逐列求真实地面高度、查高差、查岩浆/水、查是不是玩家的建筑;`purpose="farm"` 还要求
  全是草地/泥土且**光照≥9**(上一块田正是栽在"鹅卵石+空洞+光照不够"上)。
- `build_shelter(...)`:先勘察,再**一次算出整张平面图**铺下去——
  地基(逐列填满,这就是不会悬空的保证)→ 内部掏空 → 四墙 → 两格高门洞 → 屋顶。
  给了坐标但那块地撑不住时**直接拒绝并给出最近可用坐标**,不会"照盖不误"。
- `till_soil` 现在把每一列**吸附到真实地面高度**:playtest 10 连叫三次,每次九格全是
  "下面是空的",因为模型传的是自己站着的那格空气的 y。列是明确的,y 不是。

平面图几何抽到了 `GeniusShelterPlan`(不依赖引擎),`ShelterPlanTests` 断言
地基无洞、屋顶完整、每层墙都合围、门洞正好两格、内部每层都是空的、同一格不会既实心又空。
把门洞改成三格高,四个测试立刻红。

## 9. 它一直在踩坏自己刚翻的田(v0.11.0 修)

第 3 节记过 `SubsystemSoilBlockBehavior.OnCollide`,但只当成一条注意事项写在文档里,
代码里从来没处理过。而 `till_soil` 是**逐列走过去**翻的——它一路把自己刚翻好的地踩回泥土。

```csharp
// SubsystemSoilBlockBehavior.OnCollide
if (componentBody.Mass > 20f && componentBody.CrouchFactor == 0f) { ... m_toDegrade[...] = true; }
```

守护灵 `Mass=80`,`CrouchFactor` 恒为 0。为什么恒为 0:

```csharp
// ComponentBody.Load:349
CanCrouch = base.Entity.FindComponent<ComponentPlayer>() != null;
```

`TargetCrouchFactor` 和 `CrouchFactor` 两个 setter 都会在 `!CanCrouch` 时把值夹回 0,
**所以任何 NPC 根本蹲不下去**。「工具人」绕开这一点的办法是钩 `TerrainChangeCell`,
把 5 格内的 168→2 一律 `skip=true` 取消掉——有效,但那是背着引擎改结果。

`CanCrouch` 是个**公开字段**,不是只读属性。所以我们在 `ComponentGeniusBrain.Load` 里直接
`ComponentBody.CanCrouch = true`,然后 `CrouchOverFarmland()` 每帧检查脚下那格:是 168 就
`IsSneaking = true`(这个引擎里 `IsSneaking` 就是蹲下,它只读 `CrouchFactor`)。走出田就取消,
且只取消**自己设的**那次——潜行接近猎物的 order 有自己的 stance,不能被它清掉。

附带好处:`ComponentBody.cs:558` 那条"挤不出去就蹲下再试"的脱困路径,现在守护灵也吃得到。

## 10. 收割:各作物照自己的掉落表,提前割纯亏

株一样会消失,所以早割不是"慢一点",是**换来更差的东西**。阈值全部读自各 `GetDropValues`:

| 作物 | 阈值 | 依据 |
|---|---|---|
| 黑麦(种) | size **7** | 5→1 颗种子,6→1-2 颗种子,**7 才出麦**(data 5 而非种子的 data 4),外加 50% 概率多掉一样 |
| 黑麦(野) | size **>2** | 没有出麦这一档;33% 概率掉 1 颗种子 |
| 棉花 | size **2** | `GetDropValues` 的判断是 `GetSize(data) == 2`,不到就什么都没有;2 也是上限。种的还 1-2 颗种子,野的不还 |
| 南瓜 | size **7** | 任何 size≥1 都掉,但 `GetNutritionalValue` 在 size!=7 时返回 0 |

「工具人」的对应函数里,判断野生作物的那一段是**死代码**——前面三个 `if` 已经 return 了,
野黑麦和野棉花永远收不到。别照抄。

收割走各方块自己的 `GetDropValues` 再 `AddPickable`,不是 `DestroyCell`,这样 7 级黑麦的
麦粒和那次 50% 的加掉都和玩家自己割一模一样。(本版引擎的 `AddPickable` 没有 ownerEntity
参数——那是更新的重载——所以掉落是普通掉落物,靠边割边捡。)

## 11. 取水:守护灵缺的不是知识,是动词(v0.11.2 补)

playtest 11,它自己把问题说得比错误信息还准:

> 我确认了正确方法:空桶要握着「轻击水源块」,但我现有工具接口只有挖掘和放置,
> 无法执行轻击交互,所以没法替你装水。

`SubsystemBucketBlockBehavior.OnUse` 两半:

- **装水**(方块 90 空桶):raycast 必须打中 `WaterBlock` 且 `FluidBlock.GetLevel(data) == 0`
  ——**必须是水源,流动的边缘舀不起来**。然后槽位变成 91,水格被 `DestroyCell`。
- **倒水**(方块 91 水桶):`componentMiner.Place(raycast, MakeBlockValue(18))`,槽位变回 90。

两半 NPC 都够不着:`OnUse` 要一条玩家相机瞄出来的 `Ray3`,而 `ComponentMiner.Place` 在没有
`ComponentPlayer` 的实体上会 NPE(本项目一直以来的老规矩)。所以 `use_bucket` 直接做同样的
状态变更,和 `till_soil` 对耙子的处理一个路子。

倒水时会拒绝紧邻耕地/作物的格子:水会流,浇到田上冲毁作物、耕地退回泥土。
田从 3 格内吸水,所以水渠**不需要挨着田**。

## 12. 「收农作物」不等于「把附近的草都割了」

playtest 11:玩家说"去收一下农作物",`harvest_crops` 半径 8 割了 **7 株,只得到 2 颗野生小麦种子**——
玩家当场看穿:"那是你把没成熟或者生长失败的小麦收了"。

对账:野生黑麦的掉落是 `size > 2 && Random < 0.33` → 1 颗种子,**永远不出麦**。
7 × 33% ≈ 2.3,和实际拿到的 2 颗吻合 ⇒ 它割的全是**野生**株,而玩家种的那批还没熟。

所以 `harvest_crops` 现在**默认跳过野生作物**(`include_wild` 显式打开),并且把
"另有 N 株野生的没动"写进返回值。玩家说"收我的田"时,他要的是他种的那批的结果,
不是一个被野草凑大的数字。

**顺带纠正一处**:`SeedsBlock.GetPlacementValue` 里 data 4(野生小麦种子)和 data 5(小麦种子)
**种下去都是 `SetIsWild(false)` 的普通黑麦**。「工具人」那份映射把 4 当成野生,是错的;
我一开始也这么怀疑过。两个名字的区别只在掉落,不影响种植。
