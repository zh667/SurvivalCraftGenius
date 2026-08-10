# 死亡机制(引擎源码实测)

写给"死亡不掉落"这类改动:生存战争的死亡不是一个事件,而是**四件事捆在一起**,
拆开任何一件都会留下诡异状态。全部结论来自反编译源码 `~/sc-src`,并由 v0.9.0-v0.9.4 的四次实测验证。

## 1. 死亡瞬间做了什么(`ComponentHealth.Update`)

血量归零那一帧(`Health == 0f && HealthChange < 0f`)会触发 `DeadBeforeDrops` 钩子,
`Skip=false` 时接着做四件事:

```csharp
ModsManager.HookAction("DeadBeforeDrops", loader => { loader.DeadBeforeDrops(this, out var Skip); pass |= Skip; ... });
if (!pass) {
    AddParticleSystem(new KillParticleSystem(...));          // ① 死亡粒子
    foreach (IInventory item in Entity.FindComponents<IInventory>())
        item.DropAllItems(position);                          // ② 掉落
    DeathTime = m_subsystemGameInfo.TotalElapsedGameTime;      // ③ 记录死亡时刻
    if (WorkType == Server && m_componentPlayer != null)
        m_componentPlayer.SavePlayerEntity();                 // ④ 存档
}
// 下面一行是尸体清理:
if (Health <= 0f && CorpseDuration > 0f && TotalElapsedGameTime - DeathTime > CorpseDuration)
    m_componentCreature.ComponentSpawn.Despawn();
```

### ⚠ `Skip=true` 的陷阱(v0.9.0-v0.9.2 踩了三个版本)

`DeathTime` 是 `double?`,默认 **null**。`Skip=true` 跳过了 ③,尸体清理那行的
`TotalElapsedGameTime - DeathTime > CorpseDuration` 就变成 `null > double` = **永远 false**,
于是**尸体永不消失**。

雪上加霜的是 `ComponentBehaviorSelector.Update`:

```csharp
if (m_componentCreature.ComponentHealth.Health > 0f) { /* 选出最高 ImportanceLevel 的行为 */ }
// Health == 0 时一个行为都不选 → 所有 behavior.IsActive = false
```

两者叠加 = **0血僵尸**:身体还在、还能对话、但任何 behavior(包括 Genius 的大脑,
ImportanceLevel 310)永远 `IsActive == false`,所有指令只会回 "endangered"。
玩家日志里守护灵就这样在 0% 血量卡了 20 分钟。

**正确姿势:不要 Skip,而是在钩子里先把背包掏空,再让引擎跑完整套死亡流程。**
掉落遍历到的是空背包,自然什么都不掉,而 ③④ 和尸体清理全部照常。

## 2. 掉落遍历会碰到哪些组件

`Entity.FindComponents<IInventory>()` 在玩家身上能找到**四个**:

| 组件 | 内容 | DropAllItems |
|---|---|---|
| `ComponentInventory` | 真正的背包 | 正常掉落 |
| `ComponentClothing` | 身上穿的衣服(每部位可叠穿多层) | 正常掉落 |
| `ComponentCreativeInventory` | **整个方块图鉴,每格 `m_largeNumber = 9999`** | **空方法,什么都不做** |
| `ComponentFurnitureInventory` | 家具设计 | — |

自己写封存逻辑时**绝不能照抄这个遍历**:创造背包会贡献 1596 万件(v0.9.2 的
"已归还 15968537 件物品" 就是它)。只处理 `ComponentInventory` + `ComponentClothing`。

衣服还有两个坑:
- `ComponentClothing.AddSlotItems` 在引擎里是**空方法**,调了等于没调;
- `GetSlotValue(slot)` 只返回该部位**最外层**一件。

必须走 `GetClothes(ClothingSlot)` / `SetClothes(ClothingSlot, layers)`,才能连叠穿顺序一起存取。

## 3. 经验/等级:不掉经验球,但等级直接砍半

`SurvivalCraftModLoader.OnPlayerDead` 最后一行:

```csharp
playerData.Level = MathUtils.Max(MathUtils.Floor(playerData.Level / 2f), 1f);
```

所以玩家的说法是对的:**死亡不掉经验球,但等级永久砍半**,等级门槛的配方(铜镐等)会重新锁上。
`ComponentLevel` 里只有加经验的代码,只盯着它会得出"死亡不扣等级"的错误结论。

### 钩子顺序决定能不能救回来

`ModsManager.HookAction` 按注册顺序遍历 `ModHook.Loaders`,游戏自带的
`SurvivalCraftModLoader` 在启动时就注册,**永远排在 mod 前面**。
也就是说在自己的 `OnPlayerDead` 里读 `playerData.Level` 读到的**已经是砍半后的值**。

正确姿势:在 `DeadBeforeDrops`(血量归零那一帧,比 `PlayerData` 状态机进入 "PlayerDead" 早至少一帧)
里快照等级,在 `OnPlayerSpawned(SpawnMode.Respawn)` 里写回。

## 4. 重生不是"复活",是造一个新实体

引擎会销毁旧的玩家实体,重生时从模板建一个全新的空实体。
所以"跳过掉落"根本不足以保住东西——东西不掉在地上,而是**跟着旧实体一起消失**。
必须在死亡瞬间把物品**复制到 mod 自己的容器**里,重生后再塞回去。

## 5. 钩子必须注册(通用坑,不限死亡)

`ModsManager.HookAction` 只遍历**为该钩子名注册过**的 loader(`ModsManager.cs:156`)。
`override` 了 `DeadBeforeDrops` 但没在 `__ModInitialize` 里写
`ModsManager.RegisterHook("DeadBeforeDrops", this)`,那份代码就是死代码——
v0.9.0 的"死亡不掉落完全没生效"就是这个原因。详见 `NETWORKING.md` §10。

## 6. Genius 的落地实现

- `GeniusKeepInventory.ShouldKeepInventory` —— 恒返回 `false`;职责是在掉落前掏空背包/衣服并快照等级。
- `ComponentGeniusBrain.StashCarriedItems()` —— 守护灵死亡与**被收回**共用的封存入口(合并而非覆盖)。
- `StashStore` —— 封存内容按世界种子存盘,收回后退出游戏再回来也不会丢。
- `ComponentGeniusBrain.CompanionDied` 事件 —— 阵亡时主动告诉主人,并附上阵亡坐标。
- 大脑 `UpdateCore` 在 `Health <= 0` 时直接以 `error[died]` 结束指令,
  不再报会被误读成"稍等就好"的 `endangered`。
