# ToolBench — 工具选择基准

回答一个问题:**改了 prompt / 工具描述 / 换了模型之后,25 个冻结场景里模型还会选对工具、给对参数吗?**
没有它,每次改 prompt 都是盲改(差距报告第 5 项,学自 Numen 的 toolBench)。

场景 × 真实 `ToolCatalog` × 真实 `GeniusAgent.DefaultSystemPrompt` × 真实 LLM。
不进游戏世界、不执行工具——只判第一跳调用。

## 跑法

```bash
# 配置(一次):写 benchmark/.env(已 gitignore,别提交 key)
echo "GENIUS_BENCH_API_KEY=sk-..." >> benchmark/.env

dotnet run --project tools/ToolBench -p:SurvivalcraftDir=$HOME/sc-libs/
# 快速冒烟:--samples 1;只跑部分:--filter mine
```

环境变量(shell 优先于 .env):`GENIUS_BENCH_API_KEY`(缺省时只校验用例、跳过实跑)、
`GENIUS_BENCH_BASE_URL`(默认 DeepSeek)、`GENIUS_BENCH_MODEL`(默认 deepseek-chat)、
`GENIUS_BENCH_SAMPLES`(默认 3)。

## 指标

- **selection**:首个工具调用命中 `accept_tools`(样本级)
- **args_valid**:参数可解析且含 schema 必填键(对实际调用的工具判)
- **args_match**:`expect_args` 钉住的参数吻合(宽松匹配:忽略大小写,双向包含,"煤"≈"煤矿")
- **pass@k**:该用例任一样本三项全中(用例级)

每次完整运行追加一行到 `history.csv`(git 跟踪)——改动前后各跑一次,对比行就是结论。

## 用例维护

`cases.json`;`name` 唯一,`accept_tools` 必须是 ToolCatalog 里的真名(加载时校验,
单元测试也守着)。新用例两类:混淆边界的回归守卫(craft↔smelt、无坐标先 scan),
和从真实游玩日志里挖出的翻车现场(游戏日志里 `[Genius] tool` 行 + 会话失败统计
`session failure stats` 指向哪类就补哪类)。
