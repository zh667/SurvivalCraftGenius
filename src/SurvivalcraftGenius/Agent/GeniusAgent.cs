namespace SurvivalcraftGenius.Agent;

public enum AgentEventKind
{
    AssistantSaid,
    ToolCallStarted,
    ToolCallFinished,
    TurnFinished,
    Progress,
    Error,

    /// <summary>
    /// The run stopped and will not resume without the player saying something.
    /// Surfaced on screen, not only in the chat log.
    /// </summary>
    NeedsUser,
}

public sealed record AgentEvent(AgentEventKind Kind, string Text, string ToolName = "");

/// <summary>
/// The planning loop: user message → LLM → tool calls → tool results → LLM …
/// until the model answers with plain text or the step budget runs out.
/// Runs on a background thread; tool execution and event delivery are delegated
/// to the host, which is responsible for marshalling onto the game thread.
/// </summary>
public sealed class GeniusAgent
{
    public const string DefaultSystemPrompt =
        """
        你是 Genius(守护灵),一个住在 Survivalcraft 世界里的 AI 同伴,陪伴并帮助玩家。
        规则:
        - 你只能通过提供的工具影响世界;所有对玩家说的话必须用 say 工具。
        - 坐标是方块整数坐标 (x, y, z),y 是高度。scan_surroundings 的结果里包含你和玩家的位置。
        - 感知工具分工:找东西/查生物用 scan_surroundings;看地形/规划怎么走/查危险用 look_around
          (俯视字符地图,和我的寻路规则同源,#墙 ~水 !岩浆一目了然)。
          仅当目标或环境不明时才先感知——上文已确认的目标直接行动,不要重复侦察。
        - 工具会返回成功或失败原因;失败时换个办法或如实告诉玩家,不要重复失败的调用。
        - 保持简短口语化的回复,像一个可靠的同伴,不要输出大段文字。
        - 接到任务后持续用工具推进,直到完成或确实无路可走。中途可以用 say 简短汇报,但**说完必须立刻继续行动**——
          绝不能只说"我继续找"就停下等玩家催"继续"。只有任务完成、需要玩家做决定、或反复失败确认卡死时才结束回合。
        - 世界围绕玩家和我保持加载:我远征到远处时会自己维持身边的区块和野生动物刷新,可以独立打猎/探索。
          scan 报 area_not_loaded=true 说明刚到、区块还在加载,等几秒再 scan;刚到新地方生物也要等一会儿才会出现。
        - 挖掘后地上会有掉落物,用 collect_items 捡起来;交东西给玩家用 give_to_player。
        - 合成(craft)用背包里的材料;三宽配方需要附近有工作台。熔炼(smelt)需要附近有熔炉且背包里有燃料。
        - 缺材料时如实说缺什么,可以主动提出去挖/去捡。
        - 要挖矿产资源(矿石/煤/石头等)默认直接用 mine_resource:它会自己找矿、挖隧道过去、挖完捡好并走回来,一次调用完成整趟。
        - mine_resource 报 not_found(或你想先确认矿在哪)时用 find_blocks 查确切坐标——它对矿石会自动按矿层深度搜整条带,
          搜不到还会告诉你该矿在哪一层;若矿层在我下方,用 descend_to(y=矿层, looking_for=矿名) 挖梯井下去,
          到底自动再搜一遍,然后照常 mine_resource。
        - 分清两个工具:给了明确目标坐标就用 goto(挡路时加 dig_through=true 挖隧道/搭台阶);
          descend_to 只管"原地垂直下潜到某个深度",用于下矿层。往下超过约 15 格时 goto 规划不出路线,必须换 descend_to,
          自己 dig_block+goto 一格格往下挖更是浪费整个回合。
        - 几十米以上的长途优先 teleport。goto 连续两次 no_path 就改用 descend_to 或 teleport,不要原样重试。
        - **teleport 能直接落到地下**:给什么 y 就去什么 y,是实心岩层就自动开个容身的小洞,
          所以要下矿层最快的办法是 teleport 到 (x, 矿层y, z),不必先挖梯井。只有岩浆旁和玩家的建筑里会拒绝。
          被困住时(周围全是玩家的建筑、不许挖)也直接 teleport 出去,不要卡在原地报错。
        - 种田三个专用工具,别拿 dig_block/place_block 硬凑(它们做不到):
          **翻地=till_soil**(dig_block 只会把土挖掉;草地要耙两遍,till_soil 已代劳)、
          **播种=plant_seed**(种子放下去会变成作物方块,place_block 代替不了)、**施肥=fertilize**。
          细节先 read_knowledge 种田。
          没有"浇水"这个动作:**水在 3 格内**就自动湿润(挖条水渠即可,水渠离田至少 2 格,别浇到田上,会冲毁作物);
          湿润只是让生长快一倍,不是必需。施肥用 fertilize(硝石=这游戏的肥料,y50-90 砂岩层),3×3 加氮,收一次耗 1 氮。
          作物头顶光照必须≥9,否则完全不长;耕地上面压任何实心方块会退回泥土,重物踩上去也会。
        - 盖房子/开田前先确认脚下不是悬空的:place_block 会拒绝"下面和四周都没有支撑"的位置,
          遇到这个错就从地面往上垒,或先把下面的洞填上。
        - 判断工具看数据不看名字:get_inventory 返回 quarry/shovel/hack/melee 数值,quarry>1 就能当镐用(比如石锤)。
        - 缺工具或材料时按顺序自力更生,不要一上来就找玩家要:
          1) get_inventory 查背包;2) scan 找到附近箱子,逐个 take_from_chest 搜;
          3) 自己采集原料并合成(如缺镐:mine_resource 挖木头→craft 木板→木棍→配鹅卵石 craft 石锤,这游戏没有石镐);
          4) 前三步都不行才向玩家开口。
        - 你会自动捡起脚边约 2.5 格内的掉落物,所以"地上没东西"不代表"没掉落"——战利品很可能已自动进包。
          汇报掉落/战利品前必须以工具返回的 loot 信息或 get_inventory 为准,绝不凭"没看到"下结论。
        - 玩家发新消息时你正在执行的动作会在后台继续,不会被取消;查询类问题直接回答即可,只有要改变行动时才调用新的行动工具(新行动会顶替旧的)。
        - 挖矿常识(引擎实测,细节先 read_knowledge 挖矿与工具):**工具只影响速度,从不影响掉落——徒手挖钻石矿也掉钻石,
          游戏内置帮助说"需要更好工具才掉"是旧版错误文案**;石锤(2木棒+1鹅卵石,免熔炉)就能高效挖一切矿;
          矿有深度带:煤y5-200 / 铜y20-65 / 铁·硫·锗y2-40玄武岩 / 钻石y2-15(岩浆区) / 硝石y50-90砂岩——
          搜不到矿先 descend_to 到对应深度;熔铁必须用煤当燃料(木头热度不够)。
        - error[died] 表示我被打死了:身体已经不存在,任何行动工具都不会再生效,我背的东西全部替我保管着。
          这时唯一该做的是 say 一句告诉玩家我阵亡在哪、请他重新召唤,然后结束回合——绝不要接着调行动工具或假装还在干活。
          错误里已带上坐标和死因(能查到凶手时还会写"凶手:xx"),照抄给玩家,别说"系统没告诉我"。
        - 这游戏不是 Minecraft,凭印象猜配方必错(比如没有石镐,石制工具是石锤)。合成前必先 query_recipes 查真实配方;
          游戏机制拿不准就 query_help 搜游戏帮助;read_knowledge 里有玩家整理的攻略技巧(打猎要潜行接近等),开工前值得翻一眼。
        - 绝不拆玩家的建筑和家具:火把、箱子、熔炉、工作台、床、木板房、屋里的装饰方块(如煤块)都不能挖;挖资源永远用矿石名(煤矿/铁矿),不确定是不是玩家放的就先问。
        - 玩家装了旅行地图时,可用 list_waypoints 查路标、teleport 传送到路标或坐标,长途优先传送。
        - 本能(身体自动处理,你不用管也拦不住):掉进岩浆会自己跳出来逃向安全处;水下憋气快耗尽会自己上浮回空气;
          着火时附近有水会自己冲进去;被生物攻击会自动反击直到它死亡或逃远(绝不还手打玩家;血量过低会放弃反击保命)。
          scan 的 my_status.instinct_active 显示当前是否有本能在接管身体。
        - scan 的 world 字段是引擎实测的机制状态:time_of_day/moon_phase/temperature 等;
          shapeshifter_night=true 表示今晚(满月或新月)会出狼人等变身怪,规划夜间行动前先看它。
        - 工具报错时读完整句——错误信息里通常已写明下一步该调什么(缺什么材料、正确的名字、该去哪)。
        - 系统每回合自动注入一条 <world_state>(我的/玩家当前位置、已知地标如工作台/熔炉/箱子坐标)。
          它是给你看的输入,绝不是你的输出格式——不要在 say 里提及、模仿或等待它。有地标就直接用坐标,
          不必为找台子反复 scan;地标可能过时,到场对不上以现场为准(地标消失会自动被忘掉)。
        - 指令明确时直接调用对应的行动工具,不要先 say 一句"我这就去"再做——干了活用结果说话。
        - 你没有饥饿值,也不能进食(引擎如此)——玩家让你吃东西时如实说明即可,不用去背包找食物。
        - 食物常识(引擎实测,细节先 read_knowledge 食物与温度):打猎只打食草兽和鸵鸟/食火鸡/鸭/乌鸦/海鸥——
          狼熊狮虎豹只掉腐肉(75%致病)、鸽子麻雀几乎不掉肉,都不值得为食物打;日常肉鸟按群系选:乌鸦(冷/干区)、
          海鸥(海边连冰面都刷)、鸭(暖湿区),潜行从背后接近;鸵鸟食火鸡极罕见(权重1/50),遇到才打不要专门找;
          生肉必须先烤熟(营养×2.7、保质×5、不致病);同种食物连吃会生病,要轮换;深冬全图冰冻,搞食物优先打猎捡蛋而非种田。
        - 失败格式为 error[分类]: 说明,分类决定对策:no_path/not_found/target_lost/area_not_loaded →
          换路线换地点或稍等重试,自己解决;missing_material/missing_station/tool_too_weak →
          按自力更生顺序先取得先决条件再重试;invalid_argument/invalid_target/wrong_method →
          修正参数或改用说明里指出的工具;其余(endangered/died/superseded/timeout/loop_detected 等)按说明行事。
          同一分类连续失败两次就必须换策略,绝不原样重试第三次。
        """;

    /// <summary>Marks the compacted-memory message so trims never drop it.</summary>
    public const string SummaryPrefix = "(记忆摘要——之前对话的压缩记录)";

    private const string SummarizerPrompt =
        """
        你是记忆压缩器。把下面游戏内玩家与AI同伴的对话历史压缩成一份简明备忘录,供同伴在后续对话中延续记忆。

        **第一行必须是**「当前任务:xxx」——取玩家**最后一条**明确指派的活儿,以及它做到哪一步了。
        玩家换了任务就以新的为准,旧任务写进「已搁置」,绝不能让旧任务climb回当前任务的位置
        (实测事故:玩家中途改让盖房子,压缩后同伴却跑回去挖铁矿了)。
        再写「已完成」「已搁置」两节,然后才是其余信息。

        必须保留:玩家的称呼/偏好/长期目标;已完成与未完成的任务及进度;重要坐标和地点;背包/装备的关键变化;
        踩过的坑和学到的教训;玩家明确的禁令。
        丢弃:寒暄、失败重试的过程细节、一次性的琐碎操作。
        直接输出备忘录正文,不要开场白。
        """;

    /// <summary>Above this size the oldest turns get summarized into one message.</summary>
    private const int CompactTriggerCount = 60;

    /// <summary>Recent messages kept verbatim through a compaction.</summary>
    private const int KeepRecentCount = 20;

    /// <summary>Last-resort truncation cap if summarization keeps failing.</summary>
    private const int HardCapMessages = 160;

    private readonly LlmClient _client;
    private readonly ToolRegistry _registry;
    private readonly Func<string, string, Task<string>> _executeTool;
    private readonly Action<AgentEvent> _onEvent;
    private readonly GeniusSettings _settings;
    private readonly Action<IReadOnlyList<ChatMessage>>? _persistHistory;
    private readonly Func<string?>? _turnContext;
    private readonly List<ChatMessage> _history = [];
    private readonly object _gate = new();
    private bool _busy;

    public GeniusAgent(
        LlmClient client,
        ToolRegistry registry,
        Func<string, string, Task<string>> executeTool,
        Action<AgentEvent> onEvent,
        GeniusSettings settings,
        IReadOnlyList<ChatMessage>? restoredHistory = null,
        Action<IReadOnlyList<ChatMessage>>? persistHistory = null,
        Func<string?>? turnContext = null)
    {
        _turnContext = turnContext;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _persistHistory = persistHistory;
        var prompt = DefaultSystemPrompt;
        if (!string.IsNullOrWhiteSpace(settings.SystemPromptExtra))
        {
            prompt += "\n" + settings.SystemPromptExtra;
        }

        _history.Add(ChatMessage.System(prompt));
        if (restoredHistory is not null)
        {
            // The persisted file never contains a system prompt (it evolves
            // with the mod); everything else resumes where the last session
            // left off.
            _history.AddRange(restoredHistory.Where(message => message.Role != "system"));
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _busy;
            }
        }
    }

    /// <summary>Returns false if a previous turn is still running.</summary>
    public bool TryBeginTurn()
    {
        lock (_gate)
        {
            if (_busy)
            {
                return false;
            }

            _busy = true;
            return true;
        }
    }

    /// <summary>Call only after TryBeginTurn returned true.</summary>
    public async Task RunTurnAsync(string userText, CancellationToken cancellationToken)
    {
        // World-state context is rebuilt per turn, Numen-style: inserted just
        // before the user message, removed again in the finally — it never
        // accumulates in (or persists with) the history.
        ChatMessage? contextMessage = null;
        try
        {
            AppendAndTrim(ChatMessage.User(userText));
            if (_turnContext?.Invoke() is { Length: > 0 } context)
            {
                contextMessage = ChatMessage.System(context);
                _history.Insert(_history.Count - 1, contextMessage);
            }
            var stepsThisRound = 0;
            var autoContinues = 0;
            var maxSteps = Math.Max(8, _settings.MaxToolSteps);
            string? lastSignature = null;
            var repeatCount = 0;
            while (true)
            {
                await CompactHistoryIfNeededAsync(cancellationToken).ConfigureAwait(false);
                var response = await _client
                    .CompleteAsync(_history, _registry.Tools, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.HasToolCalls)
                {
                    AppendAndTrim(ChatMessage.Assistant(response.Content));
                    if (!string.IsNullOrWhiteSpace(response.Content))
                    {
                        _onEvent(new AgentEvent(AgentEventKind.AssistantSaid, response.Content));
                    }

                    return;
                }

                AppendAndTrim(ChatMessage.Assistant(response.Content, response.ToolCalls));
                foreach (var call in response.ToolCalls)
                {
                    stepsThisRound++;
                    // Loop breaker: a verbatim-identical call repeated over and
                    // over gets refused instead of burning the budget.
                    var signature = call.Name + "\n" + call.ArgumentsJson;
                    if (signature == lastSignature)
                    {
                        repeatCount++;
                    }
                    else
                    {
                        repeatCount = 0;
                        lastSignature = signature;
                    }

                    if (repeatCount >= 3)
                    {
                        AppendAndTrim(ChatMessage.ToolResult(
                            call.Id,
                            "error[loop_detected]: this exact call was repeated 4 times in a row — the approach is not working, change strategy or report to the player"));
                        continue;
                    }

                    _onEvent(new AgentEvent(AgentEventKind.ToolCallStarted, call.ArgumentsJson, call.Name));
                    var result = await ExecuteWithTimeoutAsync(call, cancellationToken).ConfigureAwait(false);
                    _onEvent(new AgentEvent(AgentEventKind.ToolCallFinished, result, call.Name));
                    AppendAndTrim(ChatMessage.ToolResult(call.Id, result));
                }

                if (stepsThisRound < maxSteps)
                {
                    continue;
                }

                // Budget exhausted: extend automatically instead of freezing
                // mid-task and waiting for the player to type 继续.
                if (autoContinues < 2)
                {
                    autoContinues++;
                    stepsThisRound = 0;
                    AppendAndTrim(ChatMessage.User(
                        "(system: 预算已自动续期,继续完成当前任务;若已完成或需要玩家决定,用 say 汇报后停止)"));
                    _onEvent(new AgentEvent(AgentEventKind.Progress, $"步数预算用完,自动续期(第 {autoContinues} 次)…"));
                    continue;
                }

                AppendAndTrim(ChatMessage.User(
                    "(system: tool step budget fully exhausted — summarize progress to the player via say next time)"));
                // NeedsUser, not Error: the run stopped cleanly and will not
                // restart on its own, so this has to reach the player even when
                // the chat window is closed. Buried in the log it reads as one
                // more grey line and the companion just looks idle.
                _onEvent(new AgentEvent(
                    AgentEventKind.NeedsUser, "步数用完,任务没做完就停下了;对我说「继续」我就接着干。"));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            _onEvent(new AgentEvent(AgentEventKind.Error, "本回合已取消。"));
        }
        catch (Exception exception)
        {
            _onEvent(new AgentEvent(AgentEventKind.Error, exception.Message));
        }
        finally
        {
            if (contextMessage is not null)
            {
                _history.Remove(contextMessage);
            }

            lock (_gate)
            {
                _busy = false;
            }

            try
            {
                _persistHistory?.Invoke([.. _history]);
            }
            catch (Exception)
            {
                // Persistence is best-effort; never let it break the turn.
            }

            _onEvent(new AgentEvent(AgentEventKind.TurnFinished, ""));
        }
    }

    /// <summary>
    /// Backstops only — each order enforces its own tighter deadline (frozen
    /// while the async planner thinks), so these must outlast the worst case,
    /// not race it: resilient mining chains up to three 1500s lives plus
    /// revive/travel/recover legs; goto's 240s dig deadline excludes planning.
    /// </summary>
    private static readonly Dictionary<string, int> LongToolTimeoutsSeconds = new(StringComparer.Ordinal)
    {
        ["mine_resource"] = 5400,
        ["goto"] = 600,
    };

    private async Task<string> ExecuteWithTimeoutAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(call.Name, out _))
        {
            return $"error[invalid_argument]: unknown tool '{call.Name}'";
        }

        try
        {
            var timeoutSeconds = LongToolTimeoutsSeconds.TryGetValue(call.Name, out var longTimeout)
                ? longTimeout
                : Math.Max(5, _settings.ToolTimeoutSeconds);
            var work = _executeTool(call.Name, call.ArgumentsJson);
            var timeout = Task.Delay(
                TimeSpan.FromSeconds(timeoutSeconds),
                cancellationToken);
            var winner = await Task.WhenAny(work, timeout).ConfigureAwait(false);
            if (winner != work)
            {
                return cancellationToken.IsCancellationRequested
                    ? "error[superseded]: cancelled by a newer instruction"
                    : "error[timeout]: tool timed out";
            }

            return await work.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return $"error[internal]: {exception.Message}";
        }
    }

    /// <summary>
    /// Compacts the oldest turns into one summary message once the history
    /// outgrows <see cref="CompactTriggerCount"/> (Numen-style auto-compaction:
    /// the previous summary sits inside the compacted range, so it gets folded
    /// into the new one). Falls back to hard truncation when the summarizer
    /// call fails, so the turn always proceeds.
    /// </summary>
    private async Task CompactHistoryIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_history.Count <= CompactTriggerCount)
        {
            return;
        }

        // Keep the tail verbatim, but never split an assistant tool_calls
        // message from its tool results.
        var tailStart = _history.Count - KeepRecentCount;
        while (tailStart < _history.Count && _history[tailStart].Role == "tool")
        {
            tailStart++;
        }

        var chunkCount = tailStart - 1;
        if (chunkCount < 1)
        {
            return;
        }

        try
        {
            _onEvent(new AgentEvent(AgentEventKind.Progress, "对话变长,正在压缩记忆…"));
            var transcript = BuildTranscript(_history.GetRange(1, chunkCount));
            var response = await _client.CompleteAsync(
                [ChatMessage.System(SummarizerPrompt), ChatMessage.User(transcript)],
                [],
                cancellationToken).ConfigureAwait(false);
            var summary = response.Content.Trim();
            if (summary.Length == 0)
            {
                throw new LlmException("summarizer returned empty content");
            }

            _history.RemoveRange(1, chunkCount);
            _history.Insert(1, ChatMessage.User(SummaryPrefix + "\n" + summary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            HardTrim();
        }
    }

    /// <summary>Serializes a history chunk into compact text for the summarizer.</summary>
    private static string BuildTranscript(IReadOnlyList<ChatMessage> chunk)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var message in chunk)
        {
            switch (message.Role)
            {
                case "user":
                    builder.AppendLine("玩家: " + Truncate(message.Content, 400));
                    break;
                case "assistant":
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        builder.AppendLine("同伴: " + Truncate(message.Content, 400));
                    }

                    foreach (var call in message.ToolCalls)
                    {
                        builder.AppendLine($"同伴调用 {call.Name}({Truncate(call.ArgumentsJson, 120)})");
                    }

                    break;
                case "tool":
                    builder.AppendLine("工具结果: " + Truncate(message.Content, 240));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private void AppendAndTrim(ChatMessage message)
    {
        _history.Add(message);
        if (_history.Count <= HardCapMessages)
        {
            return;
        }

        HardTrim();
    }

    /// <summary>
    /// Last-resort truncation (summarization failed or the cap was hit
    /// mid-turn): keep the system prompt and any memory summary, drop the
    /// oldest turns. Never let a dangling tool result open the window (APIs
    /// reject tool messages without their originating assistant tool_calls).
    /// </summary>
    private void HardTrim()
    {
        var first = _history.Count > 1 && _history[1].Role == "user"
            && _history[1].Content.StartsWith(SummaryPrefix, StringComparison.Ordinal) ? 2 : 1;
        var removable = _history.Count - CompactTriggerCount;
        if (removable <= 0 || first >= _history.Count)
        {
            return;
        }

        _history.RemoveRange(first, Math.Min(removable, _history.Count - first));
        while (_history.Count > first && _history[first].Role == "tool")
        {
            _history.RemoveAt(first);
        }
    }
}
