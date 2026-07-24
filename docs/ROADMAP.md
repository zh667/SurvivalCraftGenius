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
   `ComponentPathfinding` + 自持有的 `ComponentMiner`,挖/放走玩家同款逻辑,不直改地形作弊。
4. **配置与密钥仅存本地**(设备级 settings,不入存档、不入网络包),兼容任意 OpenAI 格式后端。
5. **每世界记忆按种子隔离**(沿用 TravelMap 的 seed-guard 经验,防世界目录名复用串档)。

## 里程碑

### M0 — 骨架(已完成)

- [x] 项目结构、构建约定(与 SCTM 一致:`SurvivalcraftDir` 注入 DLL 路径)
- [x] ModLoader 入口可加载、日志可见
- [x] 纯 C# agent 层的测试基线(Linux `dotnet test` 全绿)

实机验证清单(M1 收尾):召唤出现人形 NPC / G 键对话 / scan 汇报 / goto 走到 / dig 真挖出掉落 / follow 跟随 / 设置保存生效。发现问题记 issue 修复。

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

### M2 — 干真活

**验收场景**:"帮我做一把石斧"——它检查背包、缺啥采啥(附近有料的前提下)、合成、交给玩家。

- [ ] 工具 v2:`craft(recipe)` / `smelt` / `give_to_player` / `equip_tool` /
      `attack(entity)` / `eat`
- [ ] 合成走 `CraftingRecipesManager`,熔炼走 `ComponentFurnace` 交互
- [ ] 挖掘选择正确工具、尊重耐久与硬度(不能用手瞬挖石头)
- [ ] 战斗与自保:被攻击反击/撤退,饥饿自动进食
- [ ] 多步任务的中断与恢复:玩家新指令可打断当前计划

### M3 — 自主远征(Numen 级核心)

**验收场景**:"去挖 10 个铁矿回来"——自己出门、下矿、挖矿、返程交付,全程不用管。

- [ ] 自研 3D A* 寻路:代价含挖方/填方,支持挖隧道、搭柱、造楼梯下矿(工程量最大的一块)
- [ ] 长任务规划循环:目标分解 → 执行 → 观察 → 修正;上下文窗口管理(摘要化历史)
- [ ] 脱困与求生:卡住检测、落水上岸、避岩浆、夜间怪物应对
- [ ] 死亡恢复:记住死亡点,重生后可回收物品
- [ ] Token 经济:感知结果结构化压缩,避免每步全量扫描喂给 LLM

### M4 — 记忆与生态

- [ ] 每世界持久记忆:对话摘要、已知地标(家、矿点、熔炉位置),按种子隔离存储
- [ ] Markdown 技能库:玩家用纯文本教它新流程(参考 Numen"调教"/Voyager 技能库)
- [ ] 联机(netmod)支持:多人世界中归属、权限与同步
- [ ] 多语言(沿用 SCTM 的 Lang 资源体系)

## 风险与预案

| 风险 | 预案 |
| --- | --- |
| 引擎寻路太弱导致 M1 体验差 | M1 允许"走不过去"作为合法失败回报;复杂地形能力归 M3 |
| SC NPC 持有 ComponentMiner 的行为与玩家路径有差异 | M1 早期先在 Windows 实机验证挖/放最小用例,再铺开 |
| LLM 延迟破坏游戏体验 | 行动确定性执行不等 LLM;LLM 只在决策点介入;支持流式 say |
| 上下文膨胀 | 感知工具返回紧凑 JSON;历史摘要化;技能文本按需加载 |
