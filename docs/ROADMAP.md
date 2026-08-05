# Genius 路线图

目标分两档:先做 **MVP**(会走路、会挖块、能对话的 LLM NPC),验证 agent 闭环;
再逐步做到 Numen 级别的 **自主远征**(说一句"去挖铁",全程不用管)。

## 总体架构

```
src/SurvivalcraftGenius/
├── Agent/        纯 C# agent 层:LLM 客户端、工具注册表、规划循环、提示词
│                 —— 不引用游戏类型,Linux 上可完整单测
├── Npc/          守护灵实体:组件拼装(人形模型/背包/寻路/Miner)、召唤与存活管理
├── Perception/   感知工具实现:扫描方块、枚举生物、查询配方/背包
├── Actions/      行动工具实现:走到、挖掘、放置、跟随 —— 主循环内执行的确定性任务
├── Mod/          ModLoader 入口、设置界面、聊天 UI 接线
└── Assets/       语言文件、图标
```

关键决策(定案,后续里程碑不再重议):

1. **LLM 只做规划,不做操作**。LLM 通过 tool-calling 下发意图(`goto`/`dig`/`scan`…),
   每个工具是主循环内的确定性任务,带成功/失败回报。与 Numen、mc_aiplayer 同路线。
2. **线程模型**:HTTP 调用在后台 `Task` 执行;结果入队,由组件的 `Update()` 在主循环消费。
   游戏状态只在主循环读写,agent 层与游戏层之间只传不可变消息。
3. **NPC 不是假人**。SC 实体本就是组件拼装:`ComponentHumanModel` + `ComponentInventory` +
   `ComponentPathfinding` + 自持有的 `ComponentMiner`。挖/放按 `ComponentMiner` 的真实耗时/
   工具选择/耐久执行(攻击走 `Miner.Hit`);落地的地形改动经 `SubsystemTerrain.DestroyCell`,
   因为引擎的 `ComponentMiner.Dig/Place` 在无 ComponentPlayer 的实体上会 NPE——不是作弊,是绕坑。
4. **配置与密钥仅存本地**(设备级 settings,不入存档、不入网络包),兼容任意 OpenAI 格式后端。
5. **每世界记忆按种子隔离**(沿用 TravelMap 的 seed-guard 经验,防世界目录名复用串档)。

## 里程碑

### M0 — 骨架(已完成)

- [x] 项目结构、构建约定(与 SCTM 一致:`SurvivalcraftDir` 注入 DLL 路径)
- [x] ModLoader 入口可加载、日志可见
- [x] 纯 C# agent 层的测试基线(Linux `dotnet test` 全绿)


### M1 — MVP:会听话的守护灵(代码已完成,待 Windows 实机验证)

**验收场景**:游戏内召唤 Genius → 聊天说"过来,看看周围有什么,把你脚下的草挖了" →
它走过来、汇报周围方块/生物、挖掉目标块,全程聊天窗可见它的"想法"。

- [x] 召唤/收回:聊天窗按钮生成/收回 NPC 实体(继承 LandAnimal 模板 + 人形模型,AutoDespawn 关闭)
- [x] 聊天 UI:按 G 打开对话窗;玩家输入 → agent 循环;发言/工具动态回显
- [x] LLM 客户端:OpenAI 兼容 chat/completions + tool-calling,可配 baseURL/key/model
- [x] 工具 v1:`say` / `scan_surroundings` / `goto(x,y,z)` / `follow_player` /
      `dig_block(x,y,z)` / `place_block(x,y,z,slot_index)` / `get_inventory`
- [x] 寻路先用引擎自带 `ComponentPathfinding`(平地/缓坡够用,复杂地形允许失败并汇报)
- [x] 失败回报:每个工具超时/失败原因回传 LLM,让它重试或换策略
- [x] 设置界面:后端地址、密钥、模型名(密钥仅存本机 data:/SurvivalcraftGenius)

实机验证清单(M1 收尾):召唤出现人形 NPC / G 键对话 / scan 汇报 / goto 走到 / dig 真挖出掉落 / follow 跟随 / 设置保存生效。发现问题记 issue 修复。

### M2 — 干真活(代码已完成,待实机验证)

**验收场景**:"帮我做一把石斧"——它检查背包、缺啥采啥(附近有料的前提下)、合成、交给玩家。

- [x] 工具 v2:`craft` / `smelt` / `give_to_player` / `equip_tool` / `attack` +
      `collect_items` / `take_from_chest` / `put_into_chest`(拾取与箱子)
- [x] 合成走 `CraftingRecipesManager` 配方(3 宽配方要求附近有工作台);熔炼要求附近有熔炉+背包燃料(简化模拟,不占用真实熔炉槽位)
- [x] 挖掘自动选背包里最快的工具、尊重硬度(挖不动会如实汇报)、消耗耐久
- [x] 自保:受伤逃跑(继承 LandAnimal 的 RunAwayBehavior);`attack` 按令战斗
- [x] 多步任务的中断:玩家新指令会取消当前回合和行动,立即执行新指令
- 说明:NPC 无饥饿系统(未挂 VitalStats),`eat` 无意义故未实现

### M3 — 自主远征(核心已实现 v0.3.0,持续打磨)

**验收场景**:"去挖 10 个铁矿回来"——自己出门、下矿、挖矿、返程交付,全程不用管。

- [x] 地形改造式导航 `TunnelNavigator`(v0.6.0 重写为完整 3D A*,参考 Numen/Baritone 设计,
      算法级移植、C# 重新实现):后台线程代价搜索,移动原语含走/对角/跳跃/受限坠落/落水/搭桥/
      垫柱/挖穿/开门;可站立性按 `IsCollidable` 语义判定(花草雪层不再当地板);岩浆格与
      危险方块(仙人掌/火/尖刺)硬禁入、支撑面贴岩浆禁站;水按游泳代价通行、潜水重罚;
      预算耗尽返回"确实朝目标推进"的部分路径(七档 bestSoFar);搜索超时时冻结订单 deadline;
      憋气见底自动沿来路撤回空气并绕开该水域重规划;玩家的门/箱子/工作台/熔炉挖掘代价 ×10
      (优先开门/绕行而不是拆家)。引擎无关内核 `Npc/Nav/` 配 12 个 Linux 单测(L 形绕行、
      架桥、岩浆禁入、坠落上限、部分路径等)
- [x] `mine_resource` 自主挖矿远征:找矿(24m 半径、向下 28 格)→ 隧道抵达 → 挖掘 →
      吸取掉落 → 循环到数量达标 → 原路返回,一次工具调用完成全程
- [x] Token 经济:远征是确定性循环,LLM 只收到最终摘要;长工具独立超时(11 分钟)
- [x] 脱困与求生(基础):重伤(<40%)中止远征返回;被高优先级逃跑行为压制 8 秒即快速失败并汇报;
      隧道遇液体安全止步
- [x] 死亡恢复(基础):NPC 死亡时掉落全部背包物品在原地,日志记录死亡坐标
- [x] 完整 3D A*(含代价搜索)与岩浆/危险方块提前避让(v0.6.0,见上)
- [x] 远征维持器 `GeniusExpeditionKeeper`(v0.7.0):离玩家 >40 格时自动激活——
      注册额外地形加载点(区块跟随 NPC 加载,`TerrainUpdater.SetUpdateLocation`)、
      对周围 5×5 区块复刻引擎的玩家相机逻辑(唤醒休眠实体 SpawnsData、初刷+按原版周期野刷,
      同一套配额和权重表不会刷爆)、把身边 48 格内生物的 AutoDespawn 暂时关闭
      (防每 2 秒的"远离玩家清除"扫掉猎物,离开后恢复);靠近玩家 <32 格自动停用并清理。
      导航配合改为"起点未加载则等待加载(15s 超时)、目标未加载允许分段推进";
      scan/teleport/attack 文案与知识库同步更新为新规则
- [ ] 打磨:夜间怪物应对策略、返程交付到玩家、`mine_resource` 搜索结果缓存(现每轮全量重扫)

### M4 — 记忆与生态

- [x] 旅行地图(SCTM)集成:`list_waypoints` 反射读取路标(无硬依赖,未装不受影响)、
      `teleport` 自由传送到路标或坐标(v0.4.0)
- [ ] 每世界持久记忆:对话摘要、已知地标(家、矿点、熔炉位置),按种子隔离存储
- [x] 知识体系(v0.5.0):`query_recipes` 查游戏真实配方(含模组)、`query_help` 搜游戏内帮助页、
      `read_knowledge` 读本地攻略库(data:/SurvivalcraftGenius/knowledge,玩家可放 .md 攻略,Numen"调教"式)
- [x] 机制知识分层(v0.6.1,Numen 五层模型):
      **教学式报错**——craft/smelt 报缺料明细(缺 x3/现有 1)、烧炼合成互相路由("这是烧的,用 smelt")、
      没工作台但能现做时直接给出做法;mine_resource/attack/take_from_chest 找不到时列出实际存在的名字;
      **did-you-mean**——`NameSuggest`(编辑距离+包含,中英文通用)让拼错的方块/生物/物品名不再是死路;
      **感知直出机制状态**——scan 新增 world 字段(时段文本/月相/满月新月变身怪之夜警告/降水/体感温度)
      与 my_status(血量/氧气/着火/本能接管中),引擎实测,不靠模型记忆;
      **本能兜底层** `GeniusInstincts`——岩浆逃生、溺水上浮、着火寻水,每帧最后运行压过订单移动
      (LLM 是身体的最低出价者),系统提示词带一行自述
- [x] 知识库重组 + 潜行狩猎(v0.6.2):内置攻略按主题拆为 5 篇(战斗与狩猎/环境与生存/矿物与科技/
      农牧渔与陷阱/机关电路),每篇开头"何时读我";`read_knowledge` 改为**章节级返回**(命中 `##` 章节
      而非整篇,附同文件目录),未命中给 did-you-mean;旧两篇未被玩家改动时自动删除迁移;
      收录纪律:只写"引擎读不到+游戏帮助没有+反直觉"的内容(45 个游戏帮助主题归 query_help 管)。
      `attack` 新增 `sneak` 参数(L1 通用能力):潜行接近(人形模型潜行脚步无噪音)+ 绕到目标背后
      (鸟的视觉只有正面半球 16m)+ 潜行禁跳;鸟类狩猎机制为引擎实测(受惊三条件:正面视觉/
      ≥0.25 噪音·脚步 8m·跳跃 10m/碰撞),NPC Category=LandOther 在鸟眼里算捕食者,
      而食草动物"躲人"行为只认玩家、不躲 NPC——已写进《战斗与狩猎》知识文件
- [ ] 联机(netmod)支持:多人世界中归属、权限与同步
- [ ] 多语言(沿用 SCTM 的 Lang 资源体系)

## 风险与预案

| 风险 | 预案 |
| --- | --- |
| 引擎寻路太弱导致 M1 体验差 | M1 允许"走不过去"作为合法失败回报;复杂地形能力归 M3 |
| SC NPC 持有 ComponentMiner 的行为与玩家路径有差异 | M1 早期先在 Windows 实机验证挖/放最小用例,再铺开 |
| LLM 延迟破坏游戏体验 | 行动确定性执行不等 LLM;LLM 只在决策点介入;支持流式 say |
| 上下文膨胀 | 感知工具返回紧凑 JSON;历史摘要化;技能文本按需加载 |
