# 生存战争联机版网络机制备忘

> 2026-08-06,基于反编译源码(~/sc-src)全量侦察 + SCTM(TravelMap)实战模式总结。
> 供本项目及其他 SC 模组的联机开发复用。Genius 自身的用法见文末。

## 1. 包系统(Game.NetWork)

- 一切网络消息实现 **`IPackage` 接口**(无基类):`byte ID`、`Client To/Except/From`、
  `ClientState MinNeedState`、`WriteData/ReadData`、`Handle(ProjectNet, NetNode, bool isServer)`。
  必须有**公共无参构造**(注册和每次解码都走 `Activator.CreateInstance`)。
- **手动逐字段序列化**,无反射无 schema。`PackageStreamWriter/Reader : BinaryWriter/Reader`
  附带大量扩展(Vector3/Point3/Guid/ValuesDictionary(MessagePack)/WriteBuff(带长度)/WriteEnum(1字节) 等)。
- **⚠ 批次没有按包长度分隔**:一个数据报 = deflate 压缩的多包串联,每包前只有 1 字节校验值 0x88 + 1 字节 ID。
  `ReadData` 必须**精确消费** `WriteData` 写出的字节数,多读少读都会毁掉本批剩余全部包
  (解码器报"数据包校验失败"后 break)。对策:所有字段自定界(定长或带长度前缀),编解码写单元测试断言
  `stream.Position == stream.Length`。
- `Handle` 抛异常只丢本包(有 per-package try/catch);`ReadData` 抛异常丢整批。

## 2. 注册与 ID 空间

- **游戏对 mod 包的自动注册是死代码**:`ModEntity.LoadDllLogic` 用 `type.IsSubclassOf(typeof(IPackage))`,
  对接口恒为 false。必须在 `ModLoader.__ModInitialize()` 里手动 `PackageManager.RegisterPackage(new X())`。
- 注册表是 `IPackage[255]`,按 ID 索引;**冲突直接 throw**——务必 try/catch 包住,冲突时降级对应功能而非炸整个 mod
  (SCTM 的 TryRegisterOne 模式)。
- **ID 占用现状**:原版 0–40、56–59、250–253、255;SCTM 41 和 217;流行反作弊 mod 占 61
  (SCTM 撞过车的教训);**Genius 占 219**。新包请避开以上并在此文档登记。
- 注意:加入带 mod 的服务器会**自动把服务器的 mod 装进本地**(见 §7),别人的包 ID 会随之进入你的环境。

## 3. 收发模型

- 发送:`CommonLib.Net.QueuePackage(pkg)`(线程安全,内部加锁)。队列每 **50ms** 批量刷出
  (`ReliableOrdered`),即每次 QueuePackage 有最多 50ms 延迟。
- 寻址:服务端广播=不设 To;单播=`To = client`;除某人外广播=`Except = pkg.From`;
  客户端发出的一律到服务器,服务端解码时自动填 `From`。
- `MinNeedState`:服务端只把包发给 `client.State >= MinNeedState` 的客户端,不满足**静默丢弃**。
  `ClientState` 递进:NotConnected → Connected → ProjectLoaded → LoadTerrain → Playing。
  自定义包选 `NotConnected` 最宽松(SCTM/Genius 均如此)。
- **`Handle` 在主线程执行**(NetNode.Update 挂在 Window.Frame,LiteNetLib PollEvents 同步派发)——
  可直接摸实体/UI,但不能阻塞;异步续体要自己 marshal 回主线程。
- `Handle` 的 `projectNet` 参数在握手期/广播路径上**可能为 null**,必须判空。
- 世界加载期间客户端只即时处理白名单包(Client/Project/Mods 系列),其余**延迟**到下一个入站数据报才派发——
  早发的自定义包会迟到,不会丢(除非 10000 条待处理上限爆了)。

## 4. 身份与安全

- `Client`:`byte ID`(0=主机)、`Guid PlayerGuid`、`Guid TokenId`、`PlayerData`。
  **byte ID 断线后会复用**——跨时间引用客户端必须按 `(PlayerGuid, TokenId)` 匹配,不能存裸 ID
  (Genius 的长任务结果就是完成时重查再投递)。
- 服务端定位请求者:`package.From.PlayerData?.ComponentPlayer`(**身份只信 From,绝不信包体里自称的身份**——
  原版 ComponentPlayerPackage 的防伪造先例)。
- 客户端知道自己:`CommonLib.Net.Self`(ID/PlayerGuid);`CommonLib.Net.Server` 是临时包装,
  真实服务器条目用 `GetClientByID(0)`。
- **GUID 混淆**:服务器发给客户端的数据里,**其他玩家**的 PlayerGuid 被替换成其 TokenId。
  客户端报上来的"别人的 GUID"在服务端要同时按 PlayerGuid 和 TokenId 两路匹配(SCTM FindPlayer 模式)。
- 主机玩家没有 NetPeer:`QueuePackage{To=主机}` 会静默丢。给主机的消息要走本地直调(SCTM Deliver 模式;
  Genius 因主机永远走本地执行路径而天然规避)。

## 5. WorkType 语义

`Local | Server | Client`(`CommonLib.WorkType`)。

- 单机世界:设置里开了"允许局域网连接"就是 **Server**,没开是 Local(Local 下 QueuePackage 是无害空操作)。
- 多人房主 = Server(权威端);加入者 = Client。
- 惯用法:权威逻辑 `!= Client`;发包 `== Server`;客户端表现层 `== Client`。

## 6. 实体与组件复制(mod 组件的头号大坑)

- 服务端 AddEntity → `EntityPackage` 全量模板发给所有客户端;客户端用**同一个模板**构造实体——
  **mod 挂在实体模板上的组件也会在客户端被构造并每帧 Update**。
- 原版三层防双重模拟:① `ComponentBehaviorSelector` 在客户端一律 `IsDisableBehavior=true`;
  ② 各行为组件 Update 开头 `if (WorkType.Client) { 同步表现; return; }`;③ 刷怪/掉落物等子系统整体客户端禁跑。
- **mod 组件必须自己加同款守卫**(Genius:brain 的 Update/OnEntityRemoved 客户端直接 return)。
- 位置同步:`SubsystemBodyPackage` 每 100ms 按可视距离增量下发,客户端 ~125ms 插值收敛;
  未知实体 ID 会自动请求重同步。

## 7. 版本与 mod 一致性

- 协议版本串校验(`x26.06.19` 前缀匹配)。
- **mod 列表按 MD5 集合比对**:不一致时,支持自动下载的客户端会被服务器**推送整套 mod 并自动安装**
  (本地不匹配的挪进 ModsCache)。结论:客户端有本 mod ⇒ 服务器也有 ⇒ 双端协议版本天然一致,
  不需要自己做能力协商(SCTM 做了 CapabilityRequest 是因为要兼容旧版服务器插件,新 mod 可省)。

## 8. 其他坑

- **反矿透混淆**(AntiXrayObfuscator):服务器发给客户端的地形里矿物方块值可能是假的
  (contents 16/39/41/100/101/112/148)。**凡是要"真相"的世界读取必须在服务端执行**——
  这是 Genius 把 scan/look_around 全放服务端跑的硬理由。
- `NetNode.StopImmediate` 会清空 `OnRecieve`/`OnClientStateChanged` 订阅——断线后 mod 若订阅过这些事件要重挂。
- 大传输原版手动按 32KB 分块(ModsChunkPackage 先例);列表类字段常见 255 上限静默截断。
- 网络统计按包类型自动计入 `NetNode.GetStatisticsReport()`,自定义包免费获得可观测性。

## 9. Genius 的用法速查

- 架构:**客户端大脑 / 服务端身体**。LLM 循环、对话与地标记忆、API key 全在客户端;
  一切改世界/读世界的工具经 `GeniusToolPackage`(ID **219**)中转到服务端执行。
- 文件:`Mod/GeniusToolCodec.cs`(线格式,纯 BinaryWriter,可测)、`Mod/GeniusToolPackage.cs`、
  `Mod/GeniusNetwork.cs`(路由/注册/客户端重绑)、`GeniusPlayerComponent.ExecuteToolOverNetworkAsync /
  ExecuteNetToolAsync / CompleteNetTool`、`ComponentGeniusBrain.OwnerPlayerId`(归属)。
- 客户端本地执行的工具:say、query_recipes、query_help、read_knowledge、list_waypoints;
  teleport 的路标名在客户端解析成坐标后再上网。
- 召唤/召回作为伪工具 `_summon`/`_dismiss` 走同一条中转。

## 10. ModLoader 钩子:**必须显式注册,否则 override 是死代码**

`ModsManager.HookAction(name, ...)`(ModsManager.cs:156)**只遍历注册过该钩子名的 loader**;
没注册的 mod,即使正确 override 了方法也永远不会被调用(v0.9.0 的死亡不掉落就是这么静默失效的)。

```csharp
public override void __ModInitialize()
{
    ModsManager.RegisterHook("DeadBeforeDrops", this);   // 缺这行 = 功能不存在
    ModsManager.RegisterHook("OnPlayerDead", this);
    ModsManager.RegisterHook("OnPlayerSpawned", this);
}
```

原版自己也这么写(`SurvivalCraftModLoader.__ModInitialize` 注册了 4 个钩子)。
**新增任何 ModLoader override 时,第一件事是回到 __ModInitialize 补注册,并加一条生效日志便于实测验证。**

常用钩子:`DeadBeforeDrops(ComponentHealth, out bool Skip)`(死亡瞬间,服务端,Skip=true 取消全部掉落)、
`OnPlayerDead(PlayerData)`、`OnPlayerSpawned(SpawnMode, ComponentPlayer, Vector3)`、`OnCreatureInjure`、`OnLevelUpdate`。
