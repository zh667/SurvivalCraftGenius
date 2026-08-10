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
