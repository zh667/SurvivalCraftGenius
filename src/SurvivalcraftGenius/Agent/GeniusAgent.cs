namespace SurvivalcraftGenius.Agent;

public enum AgentEventKind
{
    AssistantSaid,
    ToolCallStarted,
    ToolCallFinished,
    TurnFinished,
    Error,
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
        - 行动前先感知:位置不明时先 scan_surroundings,再决定移动或挖掘。
        - 工具会返回成功或失败原因;失败时换个办法或如实告诉玩家,不要重复失败的调用。
        - 保持简短口语化的回复,像一个可靠的同伴,不要输出大段文字。
        - 一次回合最多几步工具调用;复杂任务先做第一步并汇报。
        - 挖掘后地上会有掉落物,用 collect_items 捡起来;交东西给玩家用 give_to_player。
        - 合成(craft)用背包里的材料;三宽配方需要附近有工作台。熔炼(smelt)需要附近有熔炉且背包里有燃料。
        - 缺材料时如实说缺什么,可以主动提出去挖/去捡。
        - 要挖矿产资源(矿石/煤/石头等)优先用 mine_resource:它会自己找矿、挖隧道过去、挖完捡好并走回来,一次调用完成整趟。
        - goto 走不通时可以带 dig_through=true 让我挖隧道/搭台阶过去。
        - 判断工具看数据不看名字:get_inventory 返回 quarry/shovel/hack/melee 数值,quarry>1 就能当镐用(比如石锤)。
        - 缺工具或材料时按顺序自力更生,不要一上来就找玩家要:
          1) get_inventory 查背包;2) scan 找到附近箱子,逐个 take_from_chest 搜;
          3) 自己采集原料并合成(如缺镐:mine_resource 挖木头→craft 木板→木棍→配圆石 craft 石镐);
          4) 前三步都不行才向玩家开口。
        - 你会自动捡起脚边约 2.5 格内的掉落物,玩家丢东西给你走近就行,不需要专门命令。
        - 玩家发新消息时你正在执行的动作会在后台继续,不会被取消;查询类问题直接回答即可,只有要改变行动时才调用新的行动工具(新行动会顶替旧的)。
        - 这游戏不是 Minecraft,凭印象猜配方必错(比如没有石镐,石制工具是石锤)。合成前必先 query_recipes 查真实配方;
          游戏机制拿不准就 query_help 搜游戏帮助;read_knowledge 里有玩家整理的攻略技巧(打猎要潜行接近等),开工前值得翻一眼。
        - 绝不拆玩家的建筑和家具:火把、箱子、熔炉、工作台、床、木板房、屋里的装饰方块(如煤块)都不能挖;挖资源永远用矿石名(煤矿/铁矿),不确定是不是玩家放的就先问。
        - 玩家装了旅行地图时,可用 list_waypoints 查路标、teleport 传送到路标或坐标,长途优先传送。
        """;

    private readonly LlmClient _client;
    private readonly ToolRegistry _registry;
    private readonly Func<string, string, Task<string>> _executeTool;
    private readonly Action<AgentEvent> _onEvent;
    private readonly GeniusSettings _settings;
    private readonly List<ChatMessage> _history = [];
    private readonly object _gate = new();
    private bool _busy;

    private const int MaxHistoryMessages = 60;

    public GeniusAgent(
        LlmClient client,
        ToolRegistry registry,
        Func<string, string, Task<string>> executeTool,
        Action<AgentEvent> onEvent,
        GeniusSettings settings)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var prompt = DefaultSystemPrompt;
        if (!string.IsNullOrWhiteSpace(settings.SystemPromptExtra))
        {
            prompt += "\n" + settings.SystemPromptExtra;
        }

        _history.Add(ChatMessage.System(prompt));
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
        try
        {
            AppendAndTrim(ChatMessage.User(userText));
            for (var step = 0; step < Math.Max(1, _settings.MaxToolSteps); step++)
            {
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
                    _onEvent(new AgentEvent(AgentEventKind.ToolCallStarted, call.ArgumentsJson, call.Name));
                    var result = await ExecuteWithTimeoutAsync(call, cancellationToken).ConfigureAwait(false);
                    _onEvent(new AgentEvent(AgentEventKind.ToolCallFinished, result, call.Name));
                    AppendAndTrim(ChatMessage.ToolResult(call.Id, result));
                }
            }

            AppendAndTrim(ChatMessage.User(
                "(system: tool step budget exhausted this turn — summarize progress to the player via say next time)"));
            _onEvent(new AgentEvent(AgentEventKind.Error, "本回合工具步数用完,已停下。"));
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
            lock (_gate)
            {
                _busy = false;
            }

            _onEvent(new AgentEvent(AgentEventKind.TurnFinished, ""));
        }
    }

    /// <summary>Long-running expedition tools get generous timeouts regardless of settings.</summary>
    private static readonly Dictionary<string, int> LongToolTimeoutsSeconds = new(StringComparer.Ordinal)
    {
        ["mine_resource"] = 1900,
        ["goto"] = 300,
    };

    private async Task<string> ExecuteWithTimeoutAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(call.Name, out _))
        {
            return $"error: unknown tool '{call.Name}'";
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
                    ? "error: cancelled by a newer instruction"
                    : "error: tool timed out";
            }

            return await work.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return $"error: {exception.Message}";
        }
    }

    private void AppendAndTrim(ChatMessage message)
    {
        _history.Add(message);
        if (_history.Count <= MaxHistoryMessages)
        {
            return;
        }

        // Keep the system prompt; drop the oldest turns. Never let a dangling
        // tool result open the window (APIs reject tool messages without their
        // originating assistant tool_calls message).
        var removable = _history.Count - MaxHistoryMessages;
        _history.RemoveRange(1, removable);
        while (_history.Count > 1 && _history[1].Role == "tool")
        {
            _history.RemoveAt(1);
        }
    }
}
