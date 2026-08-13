# 从两个 SC 模组能借什么(目标:省 token)

> 2026-08-13。反编译对象:
> - `[API1.9.2.0]工具人1.1.scmod` → `MaQiangGuardMod.dll`(45 KB,3,374 行反编译)
> - `[x26.07.01.01]铁器风云v1.0.0测试个人版.netmod` → `CSharpPrograms.dll`(160 KB,12,129 行)
>
> 两者都是 **HeadingCode2** 加密(63 字节前缀 + 奇偶折半交织),解法见 `ModsManager.GetDecipherStream`。
>
> **授权状态(2026-08-13,项目所有者告知)**:已联系原作者并获得授权,**可以照抄并优化**。
> 因此下文不再限于"只借思路" —— 具体实现可以直接移植。
>
> 两个包的作者不是同一批,移植时按来源分别署名:
>
> | 模组 | 作者(取自 modinfo.json) | 本仓库借用的部分 |
> |---|---|---|
> | 工具人 1.1 | **基岩** | 农活状态机(§一)、模式持久化(§二)、远程武器开火(§三) |
> | 铁器风云 1.0.0 | **Abrams、Bradley** | 弹道提前量解算(§四)、预制建筑格式(§五) |
>
> **待确认**:所有者说的是"作者"(单数)。如果授权只覆盖其中一个,请告诉我 ——
> §四、§五来自铁器风云,§一~§三来自工具人,两边要分开处理。
>
> 本仓库为**木兰宽松许可证第 2 版**。移植进来的每个文件头部必须写明出处与原作者,
> 并在 `README` 的致谢里列出;`docs/ATTRIBUTION.md` 是登记处。

## 零、为什么这两个非 LLM 模组值得看

我们每一步固定开销 **12,978 tok**(`ToolBench --budget` 实测),而**每一个物理决策都是一步**。
这两个模组做同样的事,花 **0 token**。

所以能借的不是"他们的功能",而是**哪些活根本不该进认知层**。Numen 的宪法把这句话写成了竞价表
(`LLM 任务` 是出价最低的竞价者,任何本能都能抢走身体);这两个 SC 模组则提供了**可以直接照着写的
SC 版实现**。

---

## 一、工具人的农活状态机 —— 省 token 最多的一条

`ComponentGuardFarmer`(820 行)整个循环是一个三优先级状态机:

```csharp
public void TryStartNewTask(bool canFarm = true)
{
    if (!TryFindAndMoveToPickable()          // ① 捡地上的掉落(5 m 吸引范围)
        && (!canFarm || !TryFindAndMoveToHarvest())   // ② 收熟了的作物(12 m)
        && canFarm)
    {
        TryFindAndMoveToPlant();             // ③ 空耕地补种
    }
}
```

配套的工程细节(全部值得抄):

| 机制 | 值 | 作用 |
|---|---|---|
| 动作冷却 | 0.2 s | 不是每帧都干活 |
| 状态超时 | 10 s | 任何非 Idle 状态卡住 10 秒自动 `ResetState()` —— **我们那个"latched IsStuck"问题的最朴素解法** |
| 战斗抢占 | `ComponentChaseBehavior.Target != null` 直接 `return` | 打架时不种田,不需要优先级系统 |
| 收割扫描 | 11×11×5 = **605 格** | 对比我们 `CraftOrder.FindNearestBlock` 的 **13.9 万格** |

### 对我们意味着什么

一轮"维护农田"今天要走:`scan_surroundings` → `harvest_crops` → `collect_items` → `plant_seed`,
**4+ 步 × 12,978 tok ≈ 52,000 tok ≈ ¥0.4(opus)**,而且每次玩家回来都要再走一遍。

做成**常驻模式**后:LLM **只调用一次**把模式打开(顺便决定种什么、范围多大、什么时候停),
之后身体自己循环,**永远免费**。

> 这才是我们相对免费工具人的真正价值主张:
> **不是"我会种田"(它也会),而是"你说一句话,我就知道该建立什么样的常驻规矩"。**
> 工具人的模式要玩家自己点按钮切;我们的模式是从一句自然语言里推出来的。

### 交叉验证:它的成熟判定和我们从引擎源码推的一致

```csharp
if (block is RyeBlock)     return RyeBlock.GetSize(data) >= 7;
if (block is CottonBlock)  return CottonBlock.GetSize(data) >= 2;
if (block is BasePumpkinBlock) return BasePumpkinBlock.GetSize(data) >= 7;
```

和 `GeniusHarvestRules` 从 `~/sc-src` 读出来的规则**逐条对上**。独立来源的确认,记一笔。

---

## 二、`ComponentGuardMode` —— 常驻模式怎么落盘

```csharp
public enum GuardMode { Idle, Hunt, Farm }
public bool CanAttack => Mode == GuardMode.Hunt;
public bool CanFarm   => Mode == GuardMode.Farm;
public void CycleMode() => Mode = (GuardMode)(((int)Mode + 1) % 3);
// Load/Save 走 ValuesDictionary.GetValue<int>("Mode", 0)
```

三个状态、一个循环切换、随实体存档。**极简,但这就是全部所需。**

我们的版本要多两样:①模式带参数(种什么、半径多大、库存满了怎么办);②模式是 LLM 设的,
不是按钮点的。但持久化和抢占的形状照抄即可。

---

## 三、工具人的远程武器 —— 直接解决计划里的 C2

**这是本次反编译最有价值的一段:它证明了非玩家实体可以开火。**

我们一直被 `ComponentMiner.Place` 在无 `ComponentPlayer` 的 NPC 上 NPE 卡着,
但 `SubsystemProjectiles.FireProjectile` **没有这个限制**:

```csharp
m_subsystemProjectiles.FireProjectile(arrowValue, muzzle, velocity,
                                      Vector3.Zero, m_componentCreature);
```

`FireBow` 的完整参数表(全部照抄):

| 项 | 值 |
|---|---|
| 枪口位置 | `EyePosition + Matrix.Right * 0.3f - Matrix.Up * 0.2f` |
| 瞄准点 | `target.Position + target.StanceBoxSize * 0.5f`(打身体中心,不是脚) |
| 初速 | `28f` |
| 散布 | 箭型 0 → `0.025`;箭型 1 → `0.01`(乘 `Random.Float(-1,1)`) |
| 叠加 | `+ m_componentBody.Velocity`(自身移动补偿) |
| 开火窗口 | 距离 **3 m ~ 30 m** |
| 保持距离 | 最小 `4f`,理想 `12f` —— `MaintainRangedDistance` 太近就后退 |
| 射后 | `BowBlock.SetArrowType(data, null)` 卸箭 + `DamageActiveTool(1)` |
| 装填 | 先从背包找箭 `SetArrowType` 写进弓的 data,播 `Audio/ArrowDraw` |

还有 `GetRangedPriority(Block)` 给 弓/弩/火枪/投矛 排序,`UpdateThrowableBehavior`
兜住所有 `GetProjectileSpeed(value) > 0` 的投掷物。

**结论:C2("`attack` 内部学会用弓")从"要研究"变成了"照着写"。**

---

## 四、铁器风云的 `ProjectileAiming` —— 打飞鸟真正缺的那块

一个**纯静态、不碰引擎**的弹道提前量解算器:

```csharp
public static Vector3? CalculateAimPoint(Vector3 launchPoint, Vector3 targetPos,
                                         Vector3 targetVelocity, float projectileSpeed)
```

重力写死 `(0, -10, 0)`,迭代 10 次、容差 1e-4,解出飞行时间后返回**应该瞄哪里**;
解不出来(目标比弹速还快)返回 `null`。

**这才是打鸭子缺的东西。** 瞄准鸟**当前所在的位置**必然脱靶 —— 既要提前量,又要抬高补下坠。
工具人的 `FireBow` 是直瞄(对地面怪够用),这个才是对空解。

两个额外好处:
1. **纯数学,零引擎依赖 → Linux 上可以全量单测。** 我们的战斗代码目前一行都测不了。
2. 返回 `null` 天然就是"这个目标打不到"的诚实答案,正好接进我们的 `error[...]` 体系。

---

## 五、铁器风云的预制建筑 —— 可能比 Numen 的 ops DSL 更适合我们

`BuildingsManager` + `Building`,格式简单到不像话:

**`Assets/Buildings/List.xml`**
```xml
<Building Name="商店"   Probability="0.025" Width="5"  Height="0" Cells="Buildings/商店" />
<Building Name="碉堡"   Probability="0.015" Width="10" Height="1" Cells="Buildings/碉堡" FillTerrain="true" />
<Building Name="小村庄" Probability="0.03"  Width="37" Height="0" Cells="Buildings/小村庄" />
```

**`Assets/Buildings/商店.txt`** —— 每行一格,`x,y,z,blockValue`:
```
0,0,0,26
0,1,0,15454
0,3,0,15414
```

5×5 的店铺 **643 字节**;37 格宽的村庄 **27 KB**。就这些。

### 为什么这对我们可能比 ops DSL 更好

计划 A3 原本打算抄 Numen 的 `build` ops 流(`box`/`walls`/`roof`/调色板/16384 格一单)。
但那条路把**设计**放进模型 —— 而设计正是我们**成本最高**、**质量最不稳定**的地方
(你说的"第一次盖得挺好,后来的都难看",就是模型每次重新设计的方差)。

预制件把设计移出模型:

| | ops DSL(Numen) | 预制件(铁器风云) |
|---|---|---|
| 每栋房的 token | 高(一大串 ops) | **接近 0**(一个名字 + 一个坐标) |
| 外观稳定性 | 每次都不一样 | 每次一样,且**可以先做好看** |
| 玩家能改吗 | 不能 | **能** —— 就是个 txt |
| 自由度 | 想盖什么盖什么 | 只有目录里那几种 |

**建议**:v0.12 先做预制件(`build_shelter("木屋", x, z)` 一次调用搞定,顺带解决外观问题),
ops DSL 降级为 v0.13 的"玩家点名要个奇怪东西"时才用的后备。

顺带:`FillTerrain="true"` 那个属性正是我们 v0.11.4 手写的整地逻辑 —— 他们把它做成了预制件的一个开关。

---

## 六、其余扫到但暂不借的

- **`ComponentNewMiner` / `ComponentNewPlayer`**(铁器风云):整套 `ComponentPlayer` 子类化。
  理论上能绕开我们所有"NPC 没有 ComponentPlayer"的限制,但代价是继承一个 500+ 行的引擎类,
  版本一升就碎。**不碰。**
- **`ComponentHumanChaseBehavior` / `ComponentHumanHerdBehavior`**:NPC 成群结队的追击/聚集。
  我们只有一个守护灵,用不上。
- **`ComponentGuardClothing`**(工具人)实现了 `IInventory` 来绕开 `ComponentClothing` 需要
  `ComponentPlayer` 的限制。我们 v0.11.0 用 `AttackBody` 钩子 + "背着即穿着" 解决了同一问题,
  **两条路都能走,我们的更省事**,不改。

---

## 七、结论:三条按省 token 排序

| # | 借什么 | 从哪 | 省多少 | 落到计划哪一条 |
|---|---|---|---|---|
| 1 | **农活/捡拾常驻模式** | 工具人 `ComponentGuardFarmer` + `ComponentGuardMode` | **一轮农田维护 ~52,000 tok → 0** | 新增 **A4**,优先级仅次于 A1 |
| 2 | **预制建筑** | 铁器风云 `BuildingsManager` | 一栋房从"一大串 ops"降到一个名字 | 替换 **A3**(ops DSL 降为 v0.13) |
| 3 | **远程武器 + 弹道提前量** | 工具人 `FireBow` + 铁器风云 `ProjectileAiming` | 不省 token,但**这是打鸟唯一的出路**,且提前量部分可单测 | **C2** 从"要研究"变"照着写" |

省 token 的真正杠杆到这里就清楚了,一共三级:

1. **D1a 提示词改英文** —— 每步 12,978 → 约 8,900 tok(降 31%,内容不减)
2. **D1b 删重复正文** —— 再降到约 8,500
3. **A4 常驻模式** —— **把"步数"本身砍掉**:重复劳动不再产生步

前两条降的是单价,第三条降的是数量。**第三条上限最高**,因为常驻模式下的循环劳动是彻底免费的。
