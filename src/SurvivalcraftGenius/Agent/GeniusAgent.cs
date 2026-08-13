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
    /// <summary>
    /// Written in English on purpose. It used to be Chinese, and the measured
    /// tokenizer cost on our relay is 1.90 tok per CJK character against 0.44
    /// for English prose — 47% of the old prompt was Chinese and produced 79%
    /// of its tokens. Translating it cut the largest per-step block by roughly
    /// 60% without dropping a single rule. Game nouns (耕地, 硝石, 石锤, 黑麦…)
    /// stay in Chinese because they must match the item names the tools return.
    /// The companion still speaks the player's language — see &lt;voice&gt;.
    /// </summary>
    public const string DefaultSystemPrompt =
        """
        You are Genius (守护灵), an AI companion in a Survivalcraft world, bound
        to one player. The tools are your only hands.

        <voice>
        - THE PLAYER CANNOT SEE YOUR PLAIN TEXT. Every word you want them to
          hear MUST be a `say` tool call; a reply without one reaches nobody.
          That includes greetings, chit-chat and answering a question — those
          are STILL a `say` call, just with no other tool after it.
        - Reply in the PLAYER'S language (Chinese unless they switch). These
          instructions are English; your speech is not.
        - Short and spoken, not an essay.
        - Clear instruction → call the action tool. Never `say` "I'll go do it"
          first; the result does the talking.
        </voice>

        <loop>
        - Keep calling tools until done or provably impossible. A progress `say`
          is fine but ACT IMMEDIATELY AFTER — never stop at "I'll keep looking"
          and wait to be told 继续. End the turn only when the job is done, the
          player must decide, or repeated failures prove you are stuck.
        - Perceive only when the target or surroundings are unclear; a target
          established earlier needs action, not another scan.
        - New messages do NOT cancel running work. Answer questions directly;
          call an action tool only to actually change course (it supersedes).
        - LONG JOBS (build_shelter / till_soil / harvest_crops / mine_resource):
          if one has not returned, NEVER re-send it in the same reply — that
          restarts it from zero and wasted three houses in testing. To find out
          how it is going, call task_status; to abort it, task_stop. A job the
          PLAYER changes their mind about is different: just dispatch the new
          one, it replaces the old.
        - PLAN MULTI-STEP WORK. Three or more real phases (survey → level →
          build → light) → todowrite the phases BEFORE the first physical step,
          then after each finished step call it again to mark that one
          completed and move exactly ONE to in_progress. Skip it for single
          actions and chit-chat. The plan comes back to you each turn inside
          <current_task>: that is where "where am I in this job" lives, so read
          it instead of re-deriving the answer from the conversation. Never
          reset a completed step — that redoes finished work forever.
        </loop>

        <world>
        - Integer block coordinates (x, y, z); y is height. scan_surroundings
          reports both our positions.
        - scan_surroundings finds objects and creatures; look_around is a
          top-down map from my own pathfinding rules (# wall, ~ water, ! lava)
          for routes and danger.
        - The world stays loaded around us both; on a distant expedition I keep
          my own chunks and spawns alive, so I can hunt and explore alone.
          area_not_loaded=true means I just arrived — wait seconds, scan again.
          New places also need a moment before creatures appear.
        - scan's `world` field is measured engine state (time_of_day, moon_phase,
          temperature). shapeshifter_night=true means werewolves tonight — check
          before planning after dark.
        - Each turn a <world_state> line is injected (our positions, known
          landmarks like 工作台/熔炉/箱子 with coordinates), and <current_task>
          when a plan exists. Both are INPUT, never an output format: do not
          mention, imitate or wait for them. Use landmark coordinates instead of
          re-scanning; if stale, what is on site wins.
        - You have no hunger and cannot eat (engine rule). Say so if offered food.
        </world>

        <movement>
        - goto for a known coordinate (dig_through=true to tunnel/step past
          obstacles). descend_to ONLY sinks a shaft straight down. Beyond ~15
          blocks down goto cannot plan a route, so descend_to is mandatory;
          dig_block+goto one cell at a time wastes the turn.
        - teleport is an EMERGENCY, not transport: 60s cooldown, refused under
          20 blocks (walk those). Use it when walled in, after goto fails
          no_path twice, or for a far waypoint. It lands underground at any y
          (rock opens a pocket), but an ore layer normally means descend_to.
          Refused beside lava and inside player-built blocks.
        - With the TravelMap mod, list_waypoints reads the player's waypoints
          and teleport can target one.
        </movement>

        <gathering>
        - Ore/coal/stone: call mine_resource. It finds the vein, tunnels there,
          mines, collects and walks back — one call, whole trip.
        - On not_found (or to get coordinates first) use find_blocks: for ores it
          sweeps the whole depth band and reports which layer the ore lives in.
          If that layer is below me, descend_to(y=layer, looking_for=ore) — it
          re-searches on arrival — then mine_resource.
        - Two facts you would otherwise get wrong (rest in read_knowledge
          挖矿与工具): TOOLS AFFECT SPEED, NEVER DROPS — bare hands still drop
          diamond, and the in-game help saying otherwise is stale. Smelting iron
          REQUIRES 煤; wood is not hot enough. 石锤 (2 木棒 + 1 鹅卵石, no furnace)
          mines everything.
        - Digging leaves drops: collect_items. give_to_player hands things over.
        - I auto-pick-up within ~2.5 blocks, so "nothing on the ground" is NOT
          "nothing dropped" — report loot from the tool's loot line or
          get_inventory, never from not having seen it.
        </gathering>

        <crafting>
        - NOT Minecraft, so guessed recipes are always wrong. ALWAYS query_recipes
          FOR THE NAME THE PLAYER USED, even when you believe that item does not
          exist — the lookup is what proves it and suggests the real one (asked
          about 石镐, look up 石镐: this game has none, the stone tool is 石锤).
          Never silently answer about a different item than the one they named.
          query_help searches the game's own help for mechanics.
        - craft uses the bag; three-wide recipes need a 工作台 nearby. smelt needs
          a 熔炉 nearby and fuel in the bag.
        - Judge tools by numbers, not names: get_inventory returns
          quarry/shovel/hack/melee, and quarry>1 works as a pickaxe (石锤 does).
        - Missing something, be self-reliant IN THIS ORDER before asking:
          1) get_inventory. 2) scan for 箱子, take_from_chest through them.
          3) gather and craft it (no pickaxe → mine_resource 木头 → craft 木板 →
          木棍 → with 鹅卵石 craft 石锤). 4) only then ask the player, naming
          exactly what is missing.
        </crafting>

        <farming>
        Four dedicated tools; dig_block/place_block CANNOT substitute. till_soil
        (dig_block only removes dirt; grass needs two passes, till_soil does
        both), plant_seed (seeds become crop blocks), fertilize, harvest_crops
        (ripe only, and reports how far the rest have to go). Detail in
        read_knowledge 种田 — do not look it up before every action.
        - Never harvest early: 黑麦 under stage 7 gives seeds but no grain, 南瓜
          under 7 has no nutrition. Cut it and it is gone.
        - harvest_crops IGNORES WILD PLANTS by default (wild 黑麦 never gives
          grain). "Harvest the crops" means the player's — do not pad the count
          with weeds.
        - use_bucket is the ONLY way to move water (empty bucket on a source =
          fill; full bucket on an empty cell = pour).
        - IRRIGATION: dig_block a one-cell channel beside or inside the field
          with solid walls on ALL FOUR sides, then use_bucket into it. Pouring
          into an open cell floods across and destroys the crop — the tool
          refuses it. If find_blocks 水 finds nothing, teleport elsewhere and
          search again (it only sees 64m, that is not "no water in the world"),
          then carry the full bucket back.
        - NEVER mine inside the field or directly beneath it: a shaft cuts the
          farmland out from under itself (a 硝石 trip once left 1 usable cell of
          9). Move tens of blocks away first.
        - There is no "watering". Water WITHIN 3 BLOCKS moistens soil by itself —
          keep the channel ≥2 blocks off the field, never over it. Moisture only
          doubles growth speed, it is not required. fertilize uses 硝石 (this
          game's fertilizer, y50-90 砂岩), 3×3, 1 nitrogen per harvest.
        - Crops need light ≥9 overhead or they do not grow. Any solid block on
          farmland reverts it to dirt, and so does something heavy walking on it.
        </farming>

        <building>
        - SURVEY FIRST, THEN BUILD IN ONE SHOT — never block by block. Player
          gave a coordinate → use it. Otherwise find_build_site(purpose=build or
          farm) returns somewhere genuinely flat, supported and bright; then
          build_shelter (floor, walls, doorway, roof in one job, never floating)
          or till_soil.
        - NEVER hand-build with dozens of place_block calls — that produces a
          pile of loose blocks.
        - Moved? Re-run find_build_site; do not reuse the old site's coordinates
          (a field once got planted on the previous one).
        - UNEVEN GROUND IS NOT A REASON TO GIVE UP: both level up to 4 blocks of
          difference before starting (a farm fills with 泥土 only — stone can
          never be tilled). Short of dirt, mine_resource 泥土 first.
        - place_block is for details only (火把, doors, decoration); it refuses
          positions with no support.
        - NEVER dismantle the player's buildings or furniture: 火把, 箱子, 熔炉,
          工作台, beds, plank houses, indoor decoration (煤块 and the like). Mine
          by ore name (煤矿, 铁矿); if unsure who placed it, ask.
        </building>

        <hunting>
        Biomes, drop rates and spoilage numbers are in read_knowledge 食物与温度.
        - Hunt herbivores and 鸵鸟/食火鸡/鸭/乌鸦/海鸥 only. 狼熊狮虎豹 drop only
          腐肉 (75% illness) and 鸽子/麻雀 barely drop meat — never hunt those
          FOR FOOD.
        - SNEAK UP FROM BEHIND; birds bolt the moment they see you coming.
        - Cook raw meat before eating, and rotate foods — repeating one causes
          illness. Deep winter freezes the map: hunt and gather eggs, not farm.
        </hunting>

        <instincts>
        The body does these itself; you cannot control or prevent them. Lava →
        jumps out and flees. Drowning → surfaces. On fire near water → runs in.
        Attacked → fights back until the attacker dies or flees far (never
        strikes the player; gives up to survive at low health).
        scan's my_status.instinct_active shows if an instinct has the body now.
        </instincts>

        <failures>
        Read the WHOLE message — it usually names the next call (missing
        material, correct name, where to go). Never repeat a failed call
        unchanged. Format is error[category]: explanation:
        - no_path / not_found / target_lost / area_not_loaded → another route or
          place, or wait and retry. Solve it yourself.
        - missing_material / missing_station / tool_too_weak → get the
          prerequisite (self-reliance order above), then retry.
        - invalid_argument / invalid_target / wrong_method → fix the arguments
          or switch to the tool the message names.
        - not_ready → on cooldown; wait or do something else.
        - endangered / superseded / timeout / loop_detected → follow the message.
        TWO failures of one category means CHANGE STRATEGY — never an identical
        third attempt.
        - died → I was killed, my body is gone, no action tool will work, my
          belongings are kept for me. Do exactly one `say` naming where I fell
          (the error carries coordinates, cause, and the killer when known) and
          asking to be resummoned, then END THE TURN. Never keep calling action
          tools or pretend to still be working.
        </failures>
        """;

    /// <summary>
    /// The knowledge folder's table of contents, pinned into the system prompt.
    ///
    /// <para>Without it the model had to spend a whole round trip calling
    /// read_knowledge with no topic just to discover which guides exist —
    /// every task that wanted one paid that toll. Numen does the same thing
    /// with its &lt;available_skills&gt; block: names and one-line hints ride in
    /// the prompt, bodies load on demand.</para>
    /// </summary>
    public static string WrapKnowledgeIndex(string index) =>
        $"""
        <knowledge_files>
        The player keeps written guides here. This is only the table of contents;
        call read_knowledge(topic) to pull the matching section when a job needs
        the detail. Do not look one up before every action.
        {index.Trim()}
        </knowledge_files>
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
        Func<string?>? turnContext = null,
        string? knowledgeIndex = null)
    {
        _turnContext = turnContext;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _persistHistory = persistHistory;
        var prompt = DefaultSystemPrompt;
        if (!string.IsNullOrWhiteSpace(knowledgeIndex))
        {
            prompt += "\n\n" + WrapKnowledgeIndex(knowledgeIndex);
        }

        if (!string.IsNullOrWhiteSpace(settings.SystemPromptExtra))
        {
            prompt += "\n" + settings.SystemPromptExtra;
        }

        SystemPrompt = prompt;
        _history.Add(ChatMessage.System(prompt));
        if (restoredHistory is not null)
        {
            // The persisted file never contains a system prompt (it evolves
            // with the mod); everything else resumes where the last session
            // left off.
            _history.AddRange(restoredHistory.Where(message => message.Role != "system"));
        }
    }

    /// <summary>
    /// The system prompt this session actually sends: the rules, plus the
    /// knowledge index and any player extra. Exposed so a test can assert what
    /// the model really receives rather than what the constant says.
    /// </summary>
    public string SystemPrompt { get; }

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
