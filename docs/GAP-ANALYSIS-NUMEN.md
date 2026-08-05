# Genius vs Numen 差距拷打报告

> 2026-08-05,基于对两个仓库的全量代码探查。对比对象:`minecraft-numen`(numen-core,分支 1.21.1)。
> 注意:Numen 仓库只含"内容包",Agent 循环/记忆压缩/UI/联机传输在独立的 numen-api 仓库中,
> 本报告对比的只是它的半个身子——真实差距比下文更大。

## TLDR

工具数量差距约 30%(21 vs 31),工程成熟度差距约一个量级。
Numen:590 commits / 2.5 个月、76 个 release tag、11 个 MC 版本、230 单测 + 4 GameTest + 工具选择 benchmark、
16 值失败分类学、全局性能预算、对话持久化 + 自动摘要压缩、服务端 fake-player 联机。
Genius:16 commits 挤在两天、约 1/3 代码(含整个 Nav/ A\*)未提交、三个版本号互相矛盾、
零游戏侧测试、无重试无流式无持久化、主线程 10 万格级同步扫描。

差距不是"再写 1 万行"能追的——是从"功能都实现了"到"系统能被信任"的距离。

## 一、工程纪律(最疼)

- **版本号三个互相矛盾**:csproj `0.1.0` / modinfo.json `0.7.0` / GeniusModLoader 日志 `v0.5.3`。
  Numen:76 个 tag,conventional commits,tag 触发 CI 自动发布 Modrinth。
- **约 1/3 代码未提交**(+1187 行,含 907 行 Nav/ A\* 重写和一半测试文件)。硬盘一坏全没。
- **README 与实现不符**:声称"走玩家同款 ComponentMiner 逻辑",实际 Dig/Place 直接
  `SubsystemTerrain.DestroyCell`;`smelt` 不碰真熔炉槽位,5 秒计时器模拟。
  (注:DestroyCell 是刻意规避——引擎的 `ComponentMiner.Dig/Place` 在无 ComponentPlayer 的 NPC
  上会 NPE;耗时/工具/耐久仍走 ComponentMiner 数据。错在文案夸大,已改 README/ROADMAP 措辞。)
- **`teleport` 无条件无上限无成本**——违反"AI 同伴只做玩家做得到的事"的产品设定;
  Numen 把这条写成原则且代码兜得住。

## 二、架构:有代码 vs 有宪法

- Numen 有 142 行"架构宪法"(新功能 4 问准入、带日期的裁决记录),引擎/内容经 4 个注册点解耦,
  生态已拆 5 仓库。Genius 是 889 行的 `GeniusPlayerComponent` 上帝类,ROADMAP 声称的
  `Perception/`、`Actions/` 目录不存在。
- **异步任务协议**:Numen 有契约(`{task_id, async:true}` 立即返回、一身体一任务、第二个派发拒绝、
  `task_finished` 事件回报、禁轮询、中断任务从当前世界状态重解)。Genius 是 ad hoc 的
  TaskCompletionSource + 晚报机制,精神相似但无协议。
- **失败处理**:Numen 16 值 `FailureType` 枚举,切分"梯子内自愈"vs"踢回 LLM",配 `RecoveryLadder`
  (不变量:只重试同一有界目标,永不扩大范围)。Genius 是 21 份自由散文 error 字符串——
  教学式报错(did-you-mean/缺料清单/autopsy)方向对,但不是类型系统,无法统计与回归。
- `_ = Task.Run(...)` 阅后即焚;`LastDeathPosition` 曾是 static(全进程一坑位,多 NPC 会找错坟)。

## 三、性能:重犯 SCTM 修过的错

- `MineResourceOrder.FindNearestMatch` 每轮挖矿迭代主线程同步扫 ~8.8 万格且全量重扫(ROADMAP
  自认缓存未做);`CraftOrder.FindNearestBlock` ~13.9 万格;`ScNavWorld` 规划线程读活体 Terrain。
- Numen:全局 `SearchBudget`(所有同伴共享,每 tick 128 检查 / 2 chunk 加载封顶)、扫描按 tick 切片、
  超时返回诚实 partial、A\* 独立线程池读冻结快照、`NavProfiler` + `/numen profile`。
  人家把性能当子系统建,我们把它当没爆炸的隐患留着。

## 四、感知:交 JSON vs 交论文

- `scan_surroundings` 合格(紧凑 JSON、chunk 未加载诚实标注、机制优先于名字),但零测量数据支撑。
- Numen `look_around`:带 costmap 风险膨胀的自我中心 ASCII 俯视语义网格,4 篇文献支撑
  (VLN 文本网格 1.1%→15%、VoT 导航 +27%),动机来自真实日志测量(一次任务 38 次 inspect_block),
  网格谓词与寻路器完全同源,感知与导航永不打架。还调研确认 Voyager/Mindcraft/GITM/JARVIS-1 均无此设计。

## 五、记忆与联机:整块缺失

- 对话:内存 60 条硬截断,关游戏全失忆。Numen:JSONL 按世界持久化、超长自动压缩摘要、
  `known_blocks`(记住见过的工作台/熔炉/箱子)每轮注入。ROADMAP M4 的 seed 隔离记忆:零行代码。
- 联机:Numen 服务端 fake ServerPlayer、动作服务端校验、每客户端自带 key。Genius:客户端直接拒绝。
- LLM 客户端:曾无重试无退避、无流式、无 temperature/max_tokens、无 token 计量,一个 5xx 吃掉整轮。
  Numen 连工具注册顺序都刻意固定以稳定命中 prompt cache。

## 六、验证:50 个测试挡住了什么

- 50/50 全绿但零个测试碰游戏侧代码(11 个 Order、Brain、Perception、Instincts、ExpeditionKeeper、UI
  全靠 Windows 手测)。Numen:230 单测 + 4 个真实地形 GameTest + 工具调用 benchmark
  (冻结场景回放,选中率 94% / 参数合法 100%,结果进 git)。
- 我们没有任何手段回答:"换模型/改 prompt,21 个工具还选得对吗?"

### 小刀合集(代码卫生)

- `_sawToolLimit` 永远不为 true → autopsy 分支不可达(NavPlan.ToolLimited 算了没人读)
- `NavPlan.ReachesGoal/ToolLimited/PlacesNeeded` 写了没人读
- `EquipBestToolFor` 逐字节复制两份(DigOrder / TimedDigger)
- 捡拾循环写三遍(Brain vacuum / MineResourceOrder / CollectItemsOrder)
- `GeniusKnowledge.cs` 三代指南以字符串常量共存(~370 行内容当代码)
- 超时两处不一致(order deadline 总是先赢,agent 侧 LongToolTimeouts 形同虚设)
- `follow_player` 状态不随 StartOrder 清理 → 订单结束幽灵跟随
- 全部 UI/prompt/报错硬编码中文,零 Lang 资源
- 未使用成员:`ChestOrderBase.ChestPoint`、`HasActiveOrder`、`IsActive`

## 七、公平地说

- 人均速度不输:Numen 是一人 2.5 个月全职级 590 commits;Genius 两天 burst + 一周增量做出
  7.3k 行、21 个全部落地的工具、能过 12 项行为测试的 A\*,仓库零 TODO/HACK 糊墙。
- 领先项:`mine_resource` 死亡-复活-捡尸-续挖三条命(Numen 无对应物)、`GeniusExpeditionKeeper`
  远征 chunk 保活 + 刷怪模拟、专用 `follow_player` 工具(Numen 靠反复 goto 涌现)、
  TravelMap 反射软依赖桥接。且 SC 引擎文档/社区资源比 MC 差几个量级,地基更烂。

## 修复计划(按优先级)

1. **[纪律] commit + tag + 统一版本号**——907 行未提交 A\* 是全项目最好的代码,只存在于一块硬盘上。
2. **[性能] 干掉主线程全量扫描**:mine_resource 候选缓存(弹出时复核),craft 找台同理。
3. **[记忆] 对话持久化 + 摘要压缩**(ROADMAP"历史摘要化"自己标了):60 条硬截断 = NPC 每小时失忆。
4. **[架构] 失败分类枚举化**,替换 21 份散文 error;同时是未来 benchmark 的地基。
5. **[健壮] LLM 客户端重试/退避 + 最小工具选择 benchmark**(20 个冻结场景即可),否则改 prompt 全是盲改。

### 状态跟踪(2026-08-05 第一轮修复)

- [~] 1. 版本号已统一(csproj=modinfo=0.7.0,loader 日志改读程序集版本);**commit + tag 仍待做——最高优先级**
- [~] 2. mine_resource 扫描缓存已做(一次扫描建候选表、弹出复核、走远 16 格或耗尽才重扫);
      CraftOrder 找工作台的 13.9 万格扫描未改(每次 craft 只扫一次,非每帧热点,可后做)
- [ ] 3. 对话持久化 + 摘要
- [ ] 4. FailureType 枚举
- [~] 5. HTTP 重试已做(408/429/5xx/超时/连接错误,3 次尝试 + 线性退避,+3 测试);benchmark 未做
- [x] 小刀合集:EquipBestToolFor 去重、_sawToolLimit 接通 NavPlan.ToolLimited、
      DeathPosition 去 static(改实例 + 死前捕获 brain 引用)、StartOrder 清 follow(工具描述同步)、
      agent 侧超时改为真 backstop(mine_resource 5400s 覆盖三条命、goto 600s)、
      删 HasActiveOrder/ChestPoint/ExpeditionKeeper.IsActive
- 验证:dotnet build 0 错误;dotnet test 53/53 通过(含 3 个新重试测试)
