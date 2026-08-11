# Token 成本

守护灵每做一步动作就是一次 LLM 往返,而每次往返都要把**系统提示词 + 全部工具 schema**
原样重发一遍。所以固定开销不是付一次,是 **× 任务步数**。

免费的对手(如「工具人」那类硬编码 FSM)每步花 0 元。我们要卖钱,就必须知道钱花在哪。

## 怎么量

```bash
dotnet run --project tools/ToolBench -p:SurvivalcraftDir=$HOME/sc-libs/ -- --budget
```

不需要 API key——它只把真实的 `ToolCatalog` + `GeniusAgent.DefaultSystemPrompt` 序列化后
数一遍。token 数是估算(仓库里没有 tokenizer,按 CJK≈0.75 tok/字、其余≈0.30 折算,误差约
±20%),但**改动前后用同一把尺子**,差值是可信的。

## 2026-08-11 实测

| | 改之前 | 改之后 |
|---|---|---|
| system prompt | 2,442 tok | 2,442 tok |
| tool schemas(29 个工具) | **5,745 tok** | **3,508 tok** |
| 固定小计 | **8,187 tok/步** | **5,950 tok/步** |

两处改动:

1. **`Formatting.None`** —— `JObject.ToString()` 的默认值是 `Indented`,我们一直在发
   带缩进和换行的 JSON。光空白就 ~1,850 tok/步。这是最便宜的一刀。
2. **描述瘦身** —— 工具描述里只保留三件事:干什么、为什么选它而不是那个容易混的兄弟、
   哪个坑真的害我们翻过车。删掉的是复述参数名、重复提示词里已有的话。~370 tok/步。

## Prompt caching

`GeniusSettings.PromptCache`:`auto`(默认)/ `on` / `off`。

`auto` 只对 Claude 系模型加 `cache_control` 标记——OpenAI 自己会缓存、不需要标记,而未知
网关可能直接拒收带标记的请求。打两个断点(Anthropic 对工具循环的推荐姿势):

- 系统提示词后一个(工具 schema 在前缀里,一起被缓存)
- 最新一条消息上一个(这样下一步能读到上一步写进去的全部历史)

网关若不认 `cache_control`,客户端会**当场去掉标记重发一次**并记住,不会让守护灵哑掉
(`LlmClient` 的 400/422 + "cache" 分支)。

缓存读价通常是输入价的 1/10:5,950 tok/步的固定前缀命中后按 ~595 tok 计费。

## 还没做的两件

- **按情境只挂载工具子集**——固定开销的大头仍是 schema。战斗时不需要 `smelt`/`query_recipes`。
  能再省 ~1,500 tok/步,但模型看不见的工具就不会调用,ToolBench 那点采样噪声压不住这种
  行为改动,**要先有更强的验证手段再动**。
- **把描述改成中文**——同样意思中文 token 更少。同上,属于不可验证的大改。

## 真正的大头仍是步数

上面全部是**每步**的钱;总账 = 每步 × 步数。playtest 10 里盖一间房用了
**107 次 `place_block`**——换成一次 `build_shelter` 就是百倍。

> 凡是有唯一正确答案的动作,就不该经过大模型:一次经过就是一次钱。

参见 [ROADMAP.md](ROADMAP.md) 的反射层。
