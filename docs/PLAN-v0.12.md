# v0.12 计划:该删的、该改的、该加的

> 2026-08-13。基于 `docs/GAP-ANALYSIS-NUMEN.md` 第二版 + 一次全量代码审计。
> 每一条都附了核实过的证据行,没有"我觉得"。

## 零、先更正上一份报告的一个错误

上一份报告说 `GeniusKnowledge.cs` 是"980 行硬编码,改一句攻略要重新编译,玩家一个字也改不了"。

**这是错的。** 实际读代码后:`Read(topic)` 从磁盘知识文件夹读 `.md`/`.txt`,做**章节级检索**(只返回匹配的 `##` 段,不返回整文件);`WriteOrUpgrade` 只在文件缺失或磁盘上是"未经修改的旧版"时才覆盖,**玩家编辑过的文件永不动**。那 980 行里的字符串常量是**随包发的默认攻略**,不是唯一来源。

所以我们和 Numen 技能系统的真实差距比我写的小得多,只剩两条(见 §二.3)。这个错误影响了优先级排序,已在下面修正。

---

## 一、删

### D1. 系统提示词 —— 唯一一处"删了直接省钱",但最大的省法不是删

**先澄清一件事:D2 那个 421 行的 switch 一个 token 都不花。** 它是游戏侧 C# 代码,永远不进请求。全项目**只有系统提示词和工具表这两块**是逐步重发、逐步计费的。所以 D1 和 D2 是两个完全不同的问题,别放在一起看。

**证据**(`dotnet run --project tools/ToolBench -- --budget`):

```
system prompt      5117 chars  ~  6446 tok   ← 最大的一块
tool schemas      13032 chars  ~  2137 tok   (31 个工具)
中转站基线                        4395 tok
= 每步实际计费                  ~ 12978 tok/步
```

#### D1a. 先做:提示词改用英文写 —— 省 61%,一个字的内容都不删

把两边的提示词逐字符量了一遍:

| | 字符数 | 中文字符 | 估算 token |
|---|---|---|---|
| Numen | 4,825 | 113(只在示例对话里) | **~2,288** |
| Genius | 5,879 | **2,770(47%)** | **~6,631** |

**我们的提示词只比他们长 22%,token 却是他们的 2.9 倍。**

原因在 `docs/TOKEN-COST.md` 那张拟合表里:**中文 1.90 tok/字,英文散文 0.44 tok/字符 —— 差 4.3 倍。**
我们 6,631 tok 里,**5,263 tok(79%)来自那 2,770 个汉字。**

Numen 的提示词通篇英文,只在示例对话里留了几句中文,然后用一行指令把语言问题解决掉:

> Your text is spoken aloud to the owner — **reply in the owner's language**, one short natural paragraph of plain spoken prose.

**做法**:提示词正文译成英文,保留"用玩家的语言回复"这一行;SC 的方块/物品名(耕地、硝石、油菜)保持中文原名不译 —— 那些要和游戏里的显示名对得上。
**预计**:6,631 → 约 2,600 tok,**省 ~4,000 tok/步,降 61%**,而**内容一个字都没少**。
**为什么比删内容优先**:删内容会改变行为(模型可能因此不知道某条机制),翻译不会。风险低一个量级,收益反而大一倍。

#### D1b. 再做:删掉与知识文件重复的攻略正文

提示词里躺着大段散文攻略:

- `GeniusAgent.cs:94` 挖矿常识(工具只影响速度不影响掉落……)
- `GeniusAgent.cs:116` 食物常识(只打食草兽和鸵鸟/食火鸡/鸭/乌鸦/海鸥……)
- `GeniusAgent.cs:62` 种田(拿不准细节再 read_knowledge 种田)

**这些内容 `read_knowledge` 的知识文件里已经有一份了。** 提示词里再抄一遍,等于每一步都为同一段知识付费。

Numen 在这件事上有一条写进代码注释的原则,值得整段抄:

> Deliberately keeps the per-tool how-to **OUT** of here (**it rots**) — that lives in each tool's description, which rides on every request. The one exception is a single routing hint the schemas structurally can't give: which tool to START with for crafting/smelting (**the tool-call benchmark regressed when this was removed**).

两个点:
1. **每个工具怎么用,写在那个工具的 description 里,不写在提示词里** —— 理由是"it rots":提示词里的用法说明会和工具实现脱节,而 description 就贴在工具旁边。
2. 他们**破例保留了一条**路由提示(合成/熔炼要先 `lookup_recipe`),理由是**删掉后 benchmark 掉分了**。破例有证据,不是拍脑袋。

**做法**:每个领域只留一行"该查哪个文件 + 一句最容易踩的坑",正文留给 `read_knowledge`;真正属于单个工具的用法,挪进那个工具的 description。
**预计**:再省 800–1,500 tok/步。
**风险**:模型可能不去查。**删之前先跑一遍 `ToolBench` 存基线,删之后再跑;选中率掉了就是删过头。** 这正是 Numen 破例那条的做法。

#### D1c. 顺带抄:提示词本身是可测试的产物

Numen 把提示词从 agent 循环里抽出来单独成类,注释写明理由:

> extracted from the client agent loop so it is a first-class, testable artifact: the offline tool-call benchmark composes **the exact same system prompt** the live loop sends, so a prompt edit and its measured effect travel together **instead of the benchmark drifting against a copy**.

我们的 `ToolBench` 已经在用真实的 `GeniusAgent.DefaultSystemPrompt`,这条**我们本来就是对的** —— 记下来,是为了在 D1a/D1b 改动时别把它改坏。

### D2. `ExecuteToolOnMainThread` 的 421 行 / 30 分支巨型 switch(0 token,但是纯负债)

`GeniusPlayerComponent.cs:946–1367`。整个类 **1594 行** —— 第一版报告记录它是 889 行,**八天涨了 79%**。

**再强调一次:这里省不出一个 token。** 它是维护成本问题:每加一个工具就往这个类里再塞一段,而下面要加的 `todowrite`、`task_status`、`task_stop` 又是三段。现在拆一次,拖三个版本就是三倍。

**Numen 根本没有这个 switch。** 他们的工具是一个接口 + 一张表:

```java
public interface NumenTool extends IToolSpec {
    // name() / description() / parameterSchema() 继承自 IToolSpec
    default void invoke(ToolCall call) { ServerToolTransport.ship(call); }   // 默认:发往服务端身体
    default void onServerCall(String id, JsonObject args, NumenPlayer body, Consumer<String> reply) { … }
}
```

每个工具是一个独立的类文件(39 个工具 = 39 个文件,按域分目录 perception/work/inventory/interact/locate/agent),`ToolRegistry.register(tool)` 塞进一张 `name → tool` 的 map,**派发就是一次 map 查找,没有 switch**。

绝大多数工具什么都不覆写:默认的 `invoke` 自动把调用运到服务端身体上,工具只实现 `onServerCall`。少数不走身体的(纯客户端的 `todowrite`、自带协议的 MCP 工具)才覆写 `invoke`。**引擎对"工具怎么干活"保持全盲。**

两个细节值得一并抄:

- **注册时校验工具名形状**(`[a-zA-Z0-9_-]{1,64}`),非法就当场抛异常。理由写在注释里:工具清单每轮都随请求发出去,**一个非法名字换来的是整轮 400**,而不是"这个工具用不了"—— 所以宁可在初始化那一刻炸掉。
- **`resolve()` 做大小写兜底**:模型把 `move_to` 写成 `Move_to` 是最常见的笔误,一次 `toLowerCase` 比较就能全接住,而不是让整轮崩掉。

**做法**:照这个形状拆 —— 定义一个 `IGeniusToolHandler`(名字 + 执行),每个工具一个类按域分目录,`GeniusPlayerComponent` 只留一张表和路由。顺带加上名字校验和大小写兜底。纯机械重构,215 个测试是安全网。

### D3. `teleport` 的无代价 —— 删的是能力,不是代码

`GeniusToolDefinition.cs:284`。现状:无冷却、无次数上限、无消耗,而且描述里**主动教模型**:

> "The y is honoured underground too — solid rock is opened into a pocket to stand in, **so this is the fastest way down to an ore band**, and the way out when walled in."

两个后果:一是违背"AI 同伴只做玩家做得到的事"的产品设定;二是模型学会了用传送绕开所有寻路失败,于是寻路的真实问题永远暴露不出来 —— 你在 playtest 14 抱怨的"让他灌溉又说附近没水,传送是干什么吃的",背后是同一件事的两面。

**做法**:加冷却(建议 60 秒)+ 每次消耗一点饱食度或耐久类代价,描述改成"应急手段,不是通勤方式"。**不删功能,删的是它的免费。**

### D4. (打折)`GeniusKnowledge` 的 339 行历史版本常量

`CombatGuideV062` / `EnvironmentGuideV062` / `FoodGuideV082` / `FoodGuideV083` / `MiningGuideV084` / 两个 Legacy —— 全部有引用,**不是死代码**:它们是"这份磁盘文件是不是未经修改的旧版"的比对样本,是 D 零那条"永不覆盖玩家编辑"逻辑的一部分。

**所以不删。** 但可以瘦身:把默认攻略挪成嵌入资源(`.md` 文件进 csproj `EmbeddedResource`),历史版本只存 SHA-256 哈希而不是全文。980 行 → 约 150 行,行为不变。

**优先级最低**,它不花 token,也不影响玩家体验。列在这里是为了不让上一份报告的错误结论留在计划里。

---

## 二、改

### C1. 重复指令:拒绝式 → 替换式 【v0.11.7 的方向是反的】

v0.11.7 我加的是:同签名任务已在跑就拒绝新指令。Numen 明确裁定相反:

> 派新活直接顶掉旧活,不必先 task_stop —— 主人改主意是常态,"她在挖矿所以不理你"是最直观的一种出戏。唯一拒绝的情形是槽里那件**同一批工具调用里刚受理**的。

他们的口径更准。该拒的是"模型在一个回合里连派两件活"(那说明它没等第一件的结果),不是"主人五分钟后改主意"。

**做法**:`GeniusOrder` 记录受理时的 turn id;新指令只有在 `turnId == 当前 turn` 且已有活时才拒绝,否则直接替换。

### C2. `attack` 内部学会用弓 —— 不加新工具,而且现在是"照着写"

> **2026-08-13 更新**:反编译工具人后,这条从"要研究"变成"照着写"。
> `SubsystemProjectiles.FireProjectile(value, muzzle, velocity, Vector3.Zero, creature)`
> **在非玩家实体上可用** —— 我们一直被 `ComponentMiner.Place` 需要 `ComponentPlayer` 卡着,
> 但开火这条路没有这个限制。枪口/初速/散布/开火窗口/保持距离的全套实测参数,
> 以及铁器风云那个纯数学的弹道提前量解算器(打飞鸟必需,且 Linux 可单测),
> 都记在 `BORROW-FROM-SC-MODS.md` §三、§四。

上次我跟你说"远程武器是猎鸟唯一的出路",隐含方案是加一个射击工具。**看了 Numen 的实现后,那是错的。** 他们的 `attack` 描述开头:

> **不问模型用什么武器** —— 那要看走到跟前时还有多远、有没有视线、还剩几支箭,全是模型在派发那一刻看不到的东西。

**做法**:`attack` 签名一个字不改,`AttackOrder` 内部在"够不着 / 目标在飞"时改用弓箭。工具表零增长,提示词零增长,鸟的问题解决。

**这是本计划里唯一直接回应"打猎不行"的一条。**

### C3. 知识清单进系统提示词(省一次白跑的往返)

`read_knowledge` 的描述是"No topic lists the files"—— 也就是模型想知道有哪些攻略,必须**先空参数调一次**。每个需要查攻略的任务都白花一个往返。

Numen 的做法是系统提示词里一个 `<available_skills>` 块,每篇一行 name + description,模型直接知道有什么、该 load 哪个。

**做法**:启动时扫知识文件夹,把"文件名 + 首行摘要"拼成一行索引进提示词。**这条和 D1 是配套的** —— D1 删掉正文,C3 补上索引,两个一起做才不会让模型抓瞎。

### C4. `GeniusInstincts` 自述进提示词

`GeniusInstincts.cs` 223 行(岩浆/溺水/着火/反击)全在跑,但**模型不知道它们存在**。于是它会为身体已经自动处理的事花 token 做决策,或者对身体"自己动了"感到困惑。

Numen 的 `Reflex` 接口强制每个机制自报一行 `describe()`,自动汇入提示词,并且明确告诉模型 LLM 是**出价最低的竞价者**(任何本能都能随时抢走身体,完事归还)。

**做法**:四条本能各加一行自述,拼进提示词。约 60 tok,换掉模型的困惑,划算。

### C5. `craft` 找工作台:地标记忆是**只写不读**的

`GeniusWorkOrders.cs:282`:

```csharp
var table = FindNearestBlock<CraftingTableBlock>(brain, 32);   // 65×65×33 ≈ 13.9 万格
if (table is { } tableCell)
{
    brain.Landmarks?.Record("工作台", tableCell.X, tableCell.Y, tableCell.Z);
}
```

我们**建了**地标记忆、每回合把它注入提示词、还做了持久化 —— 然后代码本身从来不查它,照样硬扫 13.9 万格。

**做法**:扫之前先问 `Landmarks`,命中就复核那一格是否还是工作台,是就直接用。三行代码。第一版报告把这条标成"非每帧热点,可后做",但既然记忆已经建好了,现在它是**三行的免费收益**。

---

## 三、增

### A1. `todowrite` + 每回合注入 `<current_task>` 【最高优先】

Genius 现在**没有"计划"这个对象**。每一步都在从对话历史里重新推导"我在干嘛"。做到一半被打断、被抢占、超时回来,只能靠读历史猜 —— 猜错就重来,而重来看起来就是**转圈**。

你从 playtest 11 到 15 反复抱怨的"盖房子最后老是在房子里卡住转圈""建造打猎种田样样不精通",我现在认为主因在这里:不是模型笨,是它手里没有一个可以放状态的地方。

Numen 的合同(值得照抄):

- 动手前先写表,每次结果回来后**恰好推进一项**到 in_progress
- 每次调用**整表替换**
- 绝不在"继续/恢复"时重置已完成项;绝不因为"派发了"就标完成
- 卡住就保持 in_progress 并加一条具体的补救项
- 表随存档持久化;每个用户回合注入 `<current_task>`

**约 200 行,不碰任何身体代码,全部可 Linux 单测。**

### A2. 异步任务协议:`task_status` / `task_stop`

现状是 ad hoc 的:模型看不到任务号、查不到进度、停不掉正在跑的活,只能靠超时。v0.11.7 那句"已经在盖同一栋了"是这个缺口的补丁。

**做法**:长任务受理即回执 `{task_id, async:true}`;加 `task_status`(拉现场)和 `task_stop`(叫停);完成时主动送达而不是让模型轮询。提示词里写死**禁止轮询**。

和 C1 是同一块地基,一起做。

### A4. 农活/捡拾常驻模式 【省 token 的最大杠杆,详见 `BORROW-FROM-SC-MODS.md`】

反编译工具人 1.1 之后新增的一条。它的 `ComponentGuardFarmer` 是个三优先级状态机
(捡掉落 → 收熟作物 → 补种空耕地),**花 0 token**;而我们同样一轮维护要走
`scan_surroundings` → `harvest_crops` → `collect_items` → `plant_seed`,
**4+ 步 × 12,978 tok ≈ 52,000 tok ≈ ¥0.4(opus)**,而且玩家每次回来都要重走一遍。

**做法**:LLM **只调用一次**打开模式(顺便决定种什么、半径多大、库存满了怎么办),
之后身体自己循环。照抄工具人的工程细节:0.2 s 动作冷却、**10 s 状态超时自动 reset**
(正好是我们那个 latched `IsStuck` 的朴素解法)、`ComponentChaseBehavior.Target != null`
时直接让位给战斗。

**和 D1a/D1b 的区别**:那两条降的是**单价**,这条砍掉的是**步数本身** —— 上限更高。

**产品含义**:免费的工具人也会种田,但它的模式要玩家自己点按钮切。
我们的差异化不是"会种田",而是**"你说一句话,我就知道该立什么规矩"**。

### A3. 建造:改做预制件,ops 流降级到 v0.13 【改自铁器风云】

原计划是抄 Numen 的 `build` ops 流。反编译铁器风云后建议改路 —— 它的预制建筑格式是
`x,y,z,blockValue` 每行一格的纯文本(5×5 店铺 643 字节,37 格村庄 27 KB),
外加一个声明 `Width/Height/Cells/FillTerrain` 的 XML。

**为什么改**:ops 流把**设计**放进模型,而设计正是我们成本最高、方差最大的地方 ——
你说的"第一次盖得挺好,后来的都难看"就是每次重新设计的方差。预制件把设计移出模型:
一栋房 = 一个名字 + 一个坐标,**外观可以事先做好看,玩家还能自己改那个 txt**。

顺带:`FillTerrain="true"` 正是我们 v0.11.4 手写的整地逻辑,他们做成了预制件的一个开关。

**做法**:v0.12 做预制件(`build_shelter("木屋", x, z)`);ops DSL 降为 v0.13 的后备,
留给"玩家点名要个奇怪东西"的场合。原 ops 流设计如下,保留备查:

`build_shelter(width, length)` 只有一种形状,而"这块地合不合适"的判定写死在 `GeniusBuildOrders.cs`(450 行)里 —— 模型插不上手。你说的"第一次盖得挺好,怎么就判定不合适了",那个"判定"就是我们代码里的。

Numen 的 `build` 收一条有序 ops 流(`set`/`box`/`walls`/`line`/`cylinder`/`sphere`/`roof`/`scatter`/`set_door`),后写覆盖先写,单次最多 16384 格,生存模式材料不够就整单拒绝一格不放,加权调色板混色防止"一面纯色显假"。

**决策放在哪**才是关键:他们把"盖成什么样"交给模型,代码只负责把格子放对。

本版建议**只出设计文档 + ops 数据结构 + 单测**,执行层放 v0.13。

---

## 四、执行顺序

一次一个 PR,每个都能单独发版:

| # | 事项 | 类型 | 规模 | 能单测 |
|---|---|---|---|---|
| 1 | A1 `todowrite` + `<current_task>` | 增 | ~200 行 | ✅ |
| 2 | **A4 农活常驻模式** | 增 | ~300 行 | ✅ 状态机可测 |
| 3 | C1 改替换式 + A2 task_status/stop | 改+增 | ~150 行 | ✅ |
| 4 | C2 `attack` 学会用弓 + 弹道提前量 | 改 | ~150 行 | ✅ 提前量;实机验开火 |
| 5 | **D1a 提示词改英文** | 删 | 翻译,0 新逻辑 | ✅ bench 把关 |
| 6 | D1b 删重复正文 + C3 知识索引 + C4 本能自述 | 删+改 | ~80 行 | ✅ bench 把关 |
| 7 | C5 craft 查地标 | 改 | 3 行 | ✅ |
| 8 | A3 预制建筑 | 增 | ~200 行 | ✅ |
| 9 | D2 拆 1594 行上帝类 | 删 | 纯重构 | ✅ 215 测试兜底 |
| 10 | D3 teleport 加代价 | 删 | ~40 行 | ✅ |
| 11 | D4 知识常量挪嵌入资源 | 删 | ~100 行 | ✅ |

**1–8 是本版的主体**,对着你 playtest 11–15 里反复出现的三件事(转圈、打不到鸟、任务自相顶替),
外加一次大幅成本下降。9–11 是还债,可以往后放,但 D2 每拖一版成本都在涨。

### 省钱的三级杠杆

| 级 | 做什么 | 效果 |
|---|---|---|
| 1 | **D1a 提示词改英文** | 每步 12,978 → 约 8,900 tok(**降 31%**,内容一字不减) |
| 2 | **D1b 删重复正文** | 再降到约 8,500 tok |
| 3 | **A4 常驻模式** | **把"步数"本身砍掉** —— 重复劳动不再产生步 |

前两级降的是**单价**,第三级降的是**数量**。第三级上限最高:常驻模式下的循环劳动彻底免费。
按盖一间房 40 步算,仅前两级就把 opus 的固定开销从 ¥3.98 压到约 ¥1.5。

## 四点五、授权变更带来的提速(2026-08-13)

项目所有者已联系两个模组的原作者并取得授权,**可以照抄并优化**。这把三条的性质从
"照着思路重写"变成"移植 + 改进":

| 条目 | 原估 | 现在 | 差别 |
|---|---|---|---|
| A4 农活常驻模式 | ~300 行,自己设计状态机 | 移植 `ComponentGuardFarmer` 三优先级循环后改造 | 状态机形状不用自己试错 |
| C2 远程武器 | ~150 行,开火参数靠实机试 | 移植 `FireBow` + `MaintainRangedDistance` + `ProjectileAiming` | **开火参数全是实测过的**,不用拿实机当试验场 |
| A3 预制建筑 | ~200 行,格式自己定 | 移植 `BuildingsManager` 的加载 + `x,y,z,value` 格式 | 顺带兼容他们已有的建筑文件 |

**移植不是终点,是起点。** 三处已经看到的可改进项:

- 工具人的收割扫描是**每次重扫** 11×11×5;我们有 `mine_resource` 的候选表模式,应该套上去。
- 它的状态超时是**固定 10 秒**;我们的 `GeniusApproach` 已经会清 latched `IsStuck` 并重发目标,
  两者应该合并成"先重试再放弃",而不是一刀切超时。
- `ProjectileAiming` 解不出来时返回 `null`,我们要把它接进 `GeniusFailure` 的分类里
  (`error[unreachable]: 那只鸭子飞得比我的箭快`),而不是静默不开火。

**纪律**:每移植一处,`docs/ATTRIBUTION.md` 加一行,移植文件头部写明出处与原作者。
本仓库是木兰宽松许可证第 2 版,署名不是可选项。

## 五、两条纪律

- **D1 动提示词之前先跑 `ToolBench` 存一行基线**,删完再跑。选中率掉了就是删过头 —— 这是我们唯一能回答"改 prompt 到底变好还是变坏"的手段,不用它就是盲改。
- **实机验证前先确认版本号。** playtest 13/15 两次拿的都是 N-1 版的日志,导致我在错误的前提上找了两轮根因。进游戏后 `Game.log` 第一行必须对得上要测的版本。
