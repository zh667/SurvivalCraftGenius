using System.Collections.Concurrent;
using Engine;
using Game;
using Game.NetWork;
using GameEntitySystem;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.Npc;
using SurvivalcraftGenius.UI;
using TemplatesDatabase;

namespace SurvivalcraftGenius.Mod;

public enum GeniusChatRole
{
    Player,
    Genius,
    Info,
}

public sealed record GeniusChatLine(GeniusChatRole Role, string Text);

/// <summary>
/// Player-side hub: owns the chat log, the LLM agent, the summon/dismiss logic
/// and the main-thread queue that bridges the background agent loop into the
/// game loop. Injected into the Player entity template via mod.netxdb.
/// </summary>
public sealed class GeniusPlayerComponent : Component, IUpdateable
{
    private const string NpcTemplateName = "GeniusNpc";
    private const int MaxLogLines = 200;

    private SubsystemBodies m_subsystemBodies = null!;
    private SubsystemTerrain m_subsystemTerrain = null!;
    private ComponentPlayer m_componentPlayer = null!;
    private WorkType _workType;

    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private readonly List<GeniusChatLine> _chatLog = [];
    private readonly CancellationTokenSource _lifetime = new();
    private GeniusSettingsStore _settingsStore = null!;
    private GeniusKnowledgeStore _knowledgeStore = null!;
    private GeniusSettings _settings = null!;
    private ConversationStore? _conversationStore;
    private StashStore? _stashStore;
    private string? _worldKey;
    private int _worldSeed;
    private bool _restoreAnnounced;
    private readonly Dictionary<FailureType, int> _failureCounts = [];
    private readonly LandmarkMemory _landmarks = new();
    private bool _landmarksRestored;
    private volatile string? _turnContextCache;
    private double _nextContextUpdateTime;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<string>> _pendingNetTools = new();
    private int _nextNetRequestId;
    private double _autoResumeDeadline;
    private bool _autoResumeChecked;
    private LlmClient? _llmClient;
    private GeniusAgent? _agent;
    private GeniusChatDialog? _dialog;
    private CancellationTokenSource? _turnCts;
    private string? _pendingMessage;
    private LabelWidget? _statusHud;
    private double _nextStatusUpdateTime;

    private static readonly Dictionary<string, string> OrderLabels = new(StringComparer.Ordinal)
    {
        ["GotoOrder"] = "移动",
        ["DigOrder"] = "挖掘",
        ["PlaceOrder"] = "放置",
        ["CollectItemsOrder"] = "捡拾",
        ["TakeFromChestOrder"] = "取物",
        ["PutIntoChestOrder"] = "存物",
        ["CraftOrder"] = "合成",
        ["SmeltOrder"] = "熔炼",
        ["GiveToPlayerOrder"] = "交付",
        ["AttackOrder"] = "战斗",
        ["MineResourceOrder"] = "挖矿远征",
        ["DescendOrder"] = "下潜挖井",
        ["TillSoilOrder"] = "翻地",
        ["HarvestCropsOrder"] = "收割",
        ["UseBucketOrder"] = "取水",
        ["PlantSeedOrder"] = "播种",
        ["BuildShelterOrder"] = "盖房",
    };

    /// <summary>Set when the companion dies; cleared by the next summon.</summary>
    private bool _companionDead;

    private static readonly Color LiveColor = new(168, 230, 160);
    private static readonly Color DeadColor = new(240, 140, 130);
    private static readonly Color WaitingColor = new(240, 205, 120);

    /// <summary>When the run last stopped waiting on the player.</summary>
    private double _needsUserSince = double.NegativeInfinity;

    /// <summary>How long the HUD keeps saying "say 继续" after that.</summary>
    private const double NeedsUserNoticeSeconds = 600.0;

    /// <summary>When the companion was last summoned; drives the "I'm alive again" note.</summary>
    private double _resummonedAt = double.NegativeInfinity;

    /// <summary>How long that note keeps overriding a stale error[died] in the history.</summary>
    private const double ResummonNoteSeconds = 300.0;

    public UpdateOrder UpdateOrder => UpdateOrder.Default;

    public IReadOnlyList<GeniusChatLine> ChatLog => _chatLog;

    /// <summary>Bumped whenever the log changes; the dialog polls this.</summary>
    public int ChatLogVersion { get; private set; }

    public GeniusSettings Settings => _settings;

    public bool IsAgentBusy => _agent?.IsBusy ?? false;

    public bool IsNpcSummoned => FindBrain() is not null;

    public void Update(float dt)
    {
        while (_mainThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Log.Warning($"[Genius] Main-thread action failed: {exception.Message}");
            }
        }

        // Refresh the per-turn world-state context off the game state (read
        // by the agent's background thread via the volatile cache).
        if (Time.FrameStartTime >= _nextContextUpdateTime)
        {
            _nextContextUpdateTime = Time.FrameStartTime + 1.0;
            _turnContextCache = BuildTurnContext();
        }

        if (!m_componentPlayer.PlayerData.IsMainPlayer)
        {
            return;
        }

        var input = m_componentPlayer.GameWidget.Input;
        if (input.IsKeyDownOnce(Engine.Input.Key.G) && _dialog is null)
        {
            OpenChatDialog();
        }

        // After a death/respawn (or world reload) with work cut mid-flight,
        // nudge the fresh agent to pick the thread back up instead of
        // standing idle until spoken to (playtest 2: "守护灵直接停止了").
        if (_autoResumeDeadline == 0.0)
        {
            _autoResumeDeadline = Time.FrameStartTime + 2.0;
        }
        else if (!_autoResumeChecked && Time.FrameStartTime >= _autoResumeDeadline)
        {
            _autoResumeChecked = true;
            TryAutoResumeInterruptedWork();
        }

        UpdateStatusHud();
    }

    /// <summary>
    /// Persistent on-screen status so the player always knows whether the
    /// companion is thinking, working, or has stopped.
    /// </summary>
    private void UpdateStatusHud()
    {
        if (_statusHud is null)
        {
            _statusHud = new LabelWidget
            {
                Text = "",
                // Was 0.62. The status line is Chinese, and the bundled font is
                // a BITMAP atlas — shrinking it resamples every glyph, which
                // dense characters do not survive: 思/考 collapsed into one
                // smear the player read as overlapping text. Measured advance
                // was a uniform 17px with no glyph sharing a column, so the
                // problem was resolution, not layout. The engine's own HUD
                // labels sit at 0.7-1.0; stay inside that range.
                FontScale = 0.85f,
                Color = LiveColor,
                HorizontalAlignment = WidgetAlignment.Near,
                VerticalAlignment = WidgetAlignment.Near,
                Margin = new Vector2(12f, 96f),
                IsVisible = false,
            };
            m_componentPlayer.GuiWidget.Children.Add(_statusHud);
        }

        if (Time.FrameStartTime < _nextStatusUpdateTime)
        {
            return;
        }

        _nextStatusUpdateTime = Time.FrameStartTime + 0.25;
        string? status = null;
        var color = LiveColor;
        var brain = FindBrain();
        // Death wins over everything. A corpse still holds its last order for
        // the few seconds before the entity is removed, and the agent keeps
        // "thinking" while it composes the death report — both used to render
        // as "挖掘中"/"思考中" next to a companion that no longer exists
        // ("阵亡之后旁边还显示守护灵在干什么,容易让用户以为他还在工作").
        if (_companionDead || brain?.IsDead == true)
        {
            status = "✖ 守护灵:已阵亡 — 需重新召唤";
            color = DeadColor;
        }
        else if (brain?.CurrentOrderLabel is { } orderName)
        {
            status = OrderLabels.TryGetValue(orderName, out var label)
                ? $"◆ 守护灵:{label}中"
                : "◆ 守护灵:工作中";
        }
        else if (IsAgentBusy)
        {
            status = "◆ 守护灵:思考中…";
        }
        else if (Time.FrameStartTime - _needsUserSince < NeedsUserNoticeSeconds)
        {
            // A blinking message vanishes after a few seconds; the companion
            // then sits there looking idle with no sign it is waiting on you.
            status = "⏸ 守护灵:已停下 — 说「继续」";
            color = WaitingColor;
        }
        else if (brain?.IsFollowing == true)
        {
            status = "◆ 守护灵:跟随中";
        }

        _statusHud.IsVisible = status is not null;
        _statusHud.Color = color;
        _statusHud.Text = status ?? "";
    }

    public void OpenChatDialog()
    {
        if (_dialog is not null)
        {
            return;
        }

        _dialog = new GeniusChatDialog(this, () => _dialog = null);
        DialogsManager.ShowDialog(m_componentPlayer.GuiWidget, _dialog);
    }

    public void SendChat(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        AppendLog(GeniusChatRole.Player, text);
        if (!_settings.IsConfigured)
        {
            AppendLog(GeniusChatRole.Info, "尚未配置 API Key,请先打开设置填写后端地址和密钥。");
            return;
        }

        EnsureAgent();
        if (_agent is null)
        {
            return;
        }

        StartOrQueueTurn(text);
    }

    /// <summary>
    /// Starts a turn now, or — if one is running — cancels it and queues this
    /// message to run as soon as the old turn unwinds (M2: interruption).
    /// </summary>
    private void StartOrQueueTurn(string text)
    {
        _needsUserSince = double.NegativeInfinity;
        if (!_agent!.TryBeginTurn())
        {
            // Free the LLM loop but keep the running order working in the
            // background — a status question must not kill a mining trip. The
            // new turn can supersede the order by issuing a new action tool.
            _pendingMessage = text;
            _turnCts?.Cancel();
            AppendLog(GeniusChatRole.Info, "已切到新指令(正在执行的动作会在后台继续)…");
            return;
        }

        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _turnCts.Token;
        var agent = _agent;
        _ = Task.Run(() => agent.RunTurnAsync(text, token), token);
    }

    public void SummonNpc()
    {
        if (_workType == WorkType.Client)
        {
            // M4: entities are server-authoritative — relay the request.
            RequestRemoteOp(GeniusNetwork.SummonOp, "召唤");
            return;
        }

        if (FindBrain() is not null)
        {
            AppendLog(GeniusChatRole.Info, "Genius 已经在这个世界里了。");
            return;
        }

        try
        {
            var entity = DatabaseManager.CreateEntity(Project, NpcTemplateName, throwIfNotFound: true);
            // Ownership: in multiplayer each player commands only their own
            // companion; FindBrain filters on this.
            var ownerId = m_componentPlayer.PlayerData.PlayerGUID.ToString("N");
            _companionDead = false;
            _resummonedAt = Time.FrameStartTime;
            var newBrain = entity.FindComponent<ComponentGeniusBrain>(throwOnError: false);
            if (newBrain is not null)
            {
                newBrain.OwnerPlayerId = ownerId;
            }

            // Keep-inventory on death: give back whatever it carried when it fell.
            var restoredItems = 0;
            if (ComponentGeniusBrain.TakeDeathStash(ownerId) is { } stash
                && entity.FindComponent<ComponentInventory>(throwOnError: false) is { } npcInventory)
            {
                foreach (var (value, count) in stash)
                {
                    restoredItems += count - ComponentInventoryBase.AcquireItems(npcInventory, value, count);
                }
            }

            var body = entity.FindComponent<ComponentBody>(throwOnError: true)!;
            var playerBody = m_componentPlayer.ComponentBody;
            var spawnPosition = FindSpawnPositionNear(playerBody);
            body.Position = spawnPosition;
            body.Rotation = playerBody.Rotation;
            entity.FindComponent<ComponentSpawn>(throwOnError: true)!.SpawnDuration = 0.5f;
            Project.AddEntity(entity);
            Log.Information(
                $"[Genius] NPC spawned at {spawnPosition} (player at {playerBody.Position}, workType={_workType}).");
            AppendLog(GeniusChatRole.Info, restoredItems > 0
                ? $"Genius 已召唤,死亡前背包里的 {restoredItems} 件物品已原样带回。按 G 打开对话。"
                : "Genius 已召唤。按 G 打开对话,直接下指令吧。");
        }
        catch (Exception exception)
        {
            AppendLog(GeniusChatRole.Info, $"召唤失败:{exception.Message}");
            Log.Error($"[Genius] Summon failed: {exception}");
        }
    }

    /// <summary>
    /// Prefers a spot in front of the player with two air cells; falls back to
    /// the player's own position (bodies push apart) so we never spawn in rock.
    /// </summary>
    private Vector3 FindSpawnPositionNear(ComponentBody playerBody)
    {
        var forward = playerBody.Rotation.GetForwardVector();
        foreach (var distance in new[] { 1.8f, -1.8f })
        {
            var candidate = playerBody.Position + distance * forward;
            var cell = Terrain.ToCell(candidate);
            var blocked = false;
            for (var dy = 0; dy < 2 && !blocked; dy++)
            {
                var value = m_subsystemTerrain.Terrain.GetCellValue(cell.X, cell.Y + dy, cell.Z);
                blocked = BlocksManager.Blocks[Terrain.ExtractContents(value)].IsCollidable;
            }

            if (!blocked)
            {
                return candidate;
            }
        }

        return playerBody.Position;
    }

    public void DismissNpc()
    {
        if (_workType == WorkType.Client)
        {
            RequestRemoteOp(GeniusNetwork.DismissOp, "召回");
            return;
        }

        var brain = FindBrain();
        if (brain is null)
        {
            AppendLog(GeniusChatRole.Info, "Genius 不在附近。");
            return;
        }

        brain.StopMoving();
        // Keep the backpack with the companion instead of dumping it on the
        // ground (playtest: "召回他他的物品留在原处"). The stash is saved to the
        // world file, so it survives quitting the game too.
        var kept = brain.StashCarriedItems();
        brain.Creature.ComponentSpawn.Despawn();
        AppendLog(GeniusChatRole.Info, kept > 0
            ? "Genius 已收回,背包里的东西替它收着,下次召唤原样带回。"
            : "Genius 已收回。");
    }

    /// <summary>Persists the kept-gear table whenever it changes (server side).</summary>
    private void SaveStashes()
    {
        if (_stashStore is null || _worldKey is null)
        {
            return;
        }

        try
        {
            _stashStore.Save(_worldKey, _worldSeed, ComponentGeniusBrain.SnapshotStashes());
        }
        catch (Exception exception)
        {
            Log.Warning($"[Genius] stash save failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Tells the owner their companion is gone. Without this the death is
    /// invisible: the body just stops existing, and the chat window keeps
    /// answering as if nothing happened.
    /// </summary>
    private void AnnounceCompanionDeath(string ownerId, Vector3 deathPosition, string cause)
    {
        if (m_componentPlayer?.PlayerData is not { } playerData
            || !string.Equals(ownerId, playerData.PlayerGUID.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _companionDead = true;
        var cell = Terrain.ToCell(deathPosition);
        AppendLog(
            GeniusChatRole.Info,
            $"⚰ Genius 阵亡于 ({cell.X},{cell.Y},{cell.Z}),{cause}。" +
            "它带的东西都替它收好了,重新召唤即可原样带回。");
        m_componentPlayer.ComponentGui?.DisplaySmallMessage(
            $"Genius 阵亡({cause}),请重新召唤",
            Color.White,
            blinking: true,
            playNotificationSound: true);
    }

    public void SaveSettings(GeniusSettings settings)
    {
        _settings = settings;
        _settingsStore.Save(settings);
        if (_workType != WorkType.Client)
        {
            GeniusKeepInventory.Mode = settings.KeepInventoryMode;
        }

        RebuildAgent();
        AppendLog(GeniusChatRole.Info, "设置已保存。");
    }

    public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        m_subsystemBodies = Project.FindSubsystem<SubsystemBodies>(throwOnError: true);
        m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(throwOnError: true);
        m_componentPlayer = Entity.FindComponent<ComponentPlayer>(throwOnError: true)!;
        _workType = CommonLib.WorkType;
        _settingsStore = new GeniusSettingsStore(Storage.GetSystemPath("data:/SurvivalcraftGenius"));
        _settings = _settingsStore.Load();
        // Death rule is world-global and server-authoritative; the local
        // config drives it on whichever machine runs the world.
        if (_workType != WorkType.Client)
        {
            GeniusKeepInventory.Mode = _settings.KeepInventoryMode;
        }
        _knowledgeStore = new GeniusKnowledgeStore(
            Storage.GetSystemPath("data:/SurvivalcraftGenius/knowledge"));
        _knowledgeStore.EnsureStarter();
        // Per-world conversation memory, seed-guarded because the game
        // recycles world folder names (same lesson as TravelMap's map cache).
        var gameInfo = Project.FindSubsystem<SubsystemGameInfo>(throwOnError: false);
        if (gameInfo is not null && !string.IsNullOrEmpty(gameInfo.DirectoryName))
        {
            _conversationStore = new ConversationStore(
                Storage.GetSystemPath("data:/SurvivalcraftGenius/conversations"));
            _worldKey = gameInfo.DirectoryName;
            _worldSeed = gameInfo.WorldSeed;
            if (_workType != WorkType.Client)
            {
                _stashStore = new StashStore(
                    Storage.GetSystemPath("data:/SurvivalcraftGenius/conversations"));
                ComponentGeniusBrain.LoadStashes(_stashStore.Load(_worldKey, _worldSeed));
                ComponentGeniusBrain.StashesChanged += SaveStashes;
            }
        }

        if (_workType != WorkType.Client)
        {
            ComponentGeniusBrain.CompanionDied += AnnounceCompanionDeath;
        }

        Log.Information($"[Genius] Player component loaded (workType={_workType}).");
    }

    public override void OnEntityRemoved()
    {
        ComponentGeniusBrain.StashesChanged -= SaveStashes;
        ComponentGeniusBrain.CompanionDied -= AnnounceCompanionDeath;
        _lifetime.Cancel();
        _dialog = null;
        _statusHud?.ParentWidget?.Children.Remove(_statusHud);
        _statusHud = null;
        base.OnEntityRemoved();
    }

    /// <summary>
    /// Runs once ~2s after this component comes alive (main player only, so
    /// never on the server's copies for remote players). Resumes only when
    /// something was genuinely interrupted: the NPC still runs an order, or
    /// the persisted conversation ends mid-turn (last entry not a finished
    /// assistant reply). A clean previous session stays silent and free.
    /// </summary>
    private void TryAutoResumeInterruptedWork()
    {
        if (!_settings.IsConfigured || _agent is not null)
        {
            return;
        }

        var memory = LoadPersistedMemory();
        var last = memory.Messages?.LastOrDefault();
        var orderActive = FindBrain()?.CurrentOrderLabel is not null;
        if (!orderActive && (last is null || last.Role == "assistant"))
        {
            return;
        }

        EnsureAgent();
        if (_agent is null)
        {
            return;
        }

        Log.Information("[Genius] Auto-resuming interrupted work after respawn/reload.");
        StartOrQueueTurn(
            "(system: 会话因玩家死亡重生或重新进入世界而重启。结合上文和 <world_state>:" +
            "若有未完成的任务,继续推进或重新下达行动工具;若确实无事可做,用 say 一句话告知玩家你已就绪即可)");
    }

    /// <summary>Client-side summon/dismiss go through the tool relay.</summary>
    private void RequestRemoteOp(string op, string label)
    {
        if (!GeniusNetwork.PackageRegistered)
        {
            AppendLog(GeniusChatRole.Info, "联机中转不可用(网络包 ID 被其他模组占用),此客户端无法使用同伴。");
            return;
        }

        AppendLog(GeniusChatRole.Info, $"已向服务器请求{label}…");
        _ = ExecuteToolOverNetworkAsync(op, []).ContinueWith(task =>
            _mainThreadQueue.Enqueue(() =>
                AppendLog(GeniusChatRole.Info, $"{label}:{Truncate(task.Result, 120)}")));
    }

    public override void Dispose()
    {
        _lifetime.Cancel();
        _llmClient?.Dispose();
        foreach (var pending in _pendingNetTools.Values)
        {
            pending.TrySetResult(GeniusFailure.Format(FailureType.Unavailable, "session ended before the server replied"));
        }

        _pendingNetTools.Clear();
        lock (_failureCounts)
        {
            if (_failureCounts.Count > 0)
            {
                var stats = string.Join(", ", _failureCounts
                    .OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{GeniusFailure.Slug(pair.Key)}×{pair.Value}"));
                Log.Information($"[Genius] session failure stats: {stats}");
            }
        }

        base.Dispose();
    }

    private void EnsureAgent()
    {
        if (_agent is null)
        {
            RebuildAgent();
        }
    }

    private void RebuildAgent()
    {
        _llmClient?.Dispose();
        _llmClient = new LlmClient(_settings);
        var restored = LoadPersistedMemory();
        if (!_landmarksRestored)
        {
            _landmarksRestored = true;
            _landmarks.Restore(restored.Landmarks);
        }

        _agent = new GeniusAgent(
            _llmClient,
            ToolCatalog.CreateDefaultRegistry(),
            ExecuteToolAsync,
            OnAgentEvent,
            _settings,
            restored.Messages,
            PersistConversation,
            () => _turnContextCache);
        if (restored.Messages is not null && !_restoreAnnounced)
        {
            _restoreAnnounced = true;
            // After a player death/respawn the component is rebuilt but the
            // NPC (and its running order) survives — say so, or the reload
            // message reads like the companion lost its task.
            var ongoing = FindBrain()?.CurrentOrderLabel is { } orderLabel
                ? $";同伴仍在执行:{OrderLabels.GetValueOrDefault(orderLabel, orderLabel)}"
                : "";
            AppendLog(GeniusChatRole.Info,
                $"已衔接这个世界的记忆(对话 {restored.Messages.Count} 条,地标 {restored.Landmarks.Count} 个){ongoing}。");
        }
    }

    private WorldMemory LoadPersistedMemory()
    {
        if (_conversationStore is null || _worldKey is null)
        {
            return WorldMemory.Empty;
        }

        try
        {
            return _conversationStore.Load(_worldKey, _worldSeed);
        }
        catch (Exception exception)
        {
            Log.Warning($"[Genius] Failed to restore memory: {exception.Message}");
            return WorldMemory.Empty;
        }
    }

    /// <summary>Called by the agent at each turn's end, on its background thread.</summary>
    private void PersistConversation(IReadOnlyList<Agent.ChatMessage> history)
    {
        if (_conversationStore is null || _worldKey is null)
        {
            return;
        }

        try
        {
            _conversationStore.Save(_worldKey, _worldSeed, history, _landmarks.Snapshot());
        }
        catch (Exception exception)
        {
            Log.Warning($"[Genius] Failed to persist memory: {exception.Message}");
        }
    }

    /// <summary>
    /// The Numen-style per-turn world-state block: positions + known
    /// landmarks. Built on the game thread, consumed on the agent thread.
    /// </summary>
    private string? BuildTurnContext()
    {
        var brain = FindBrain();
        var npcCell = brain is null
            ? (Point3?)null
            : Terrain.ToCell(brain.Creature.ComponentBody.Position);
        var playerCell = Terrain.ToCell(m_componentPlayer.ComponentBody.Position);
        var lines = new List<string>(4);
        if (npcCell is { } npc)
        {
            var distance = Vector3.Distance(
                brain!.Creature.ComponentBody.Position, m_componentPlayer.ComponentBody.Position);
            lines.Add($"我的位置: ({npc.X},{npc.Y},{npc.Z});玩家位置: " +
                $"({playerCell.X},{playerCell.Y},{playerCell.Z});相距 {distance:0}m");
            // The history still holds the error[died] from the last life, and
            // the model kept believing it: 20 seconds after being re-summoned
            // it answered "我现在还是阵亡状态,无法行动。请先重新召唤我"
            // (playtest 8). A position line alone was not enough to override an
            // explicit tool error, so say it outright for a while.
            if (Time.FrameStartTime - _resummonedAt < ResummonNoteSeconds)
            {
                lines.Add("我刚被重新召唤,现在活着、可以正常行动——" +
                    "上文里的 error[died] 属于上一条命,已经失效,不要再说自己阵亡或要求玩家召唤");
            }
            // Survives player respawns: the new agent instance immediately
            // sees what the (still running) body is doing.
            if (brain.CurrentOrderLabel is { } orderLabel)
            {
                lines.Add($"我正在执行中的任务: {OrderLabels.GetValueOrDefault(orderLabel, orderLabel)}" +
                    "(后台继续,无需重下指令)");
            }
            else if (brain.IsFollowing)
            {
                lines.Add("我正在跟随玩家");
            }

            var surfaceY = m_subsystemTerrain.Terrain.GetTopHeight(npc.X, npc.Z);
            if (npc.Y < surfaceY - 2)
            {
                lines.Add($"注意:我在地下(地表 y={surfaceY})——地表活动(打猎/找动物)需先上去");
            }
        }
        else
        {
            lines.Add($"我未被召唤;玩家位置: ({playerCell.X},{playerCell.Y},{playerCell.Z})");
        }

        var landmarks = _landmarks.Describe(npcCell is { } cell
            ? (cell.X, cell.Y, cell.Z)
            : (playerCell.X, playerCell.Y, playerCell.Z));
        if (landmarks.Length > 0)
        {
            lines.Add("已知地标(可能过时): " + landmarks);
        }

        return "<world_state>\n" + string.Join("\n", lines) + "\n</world_state>";
    }

    private void OnAgentEvent(AgentEvent agentEvent)
    {
        _mainThreadQueue.Enqueue(() =>
        {
            switch (agentEvent.Kind)
            {
                case AgentEventKind.AssistantSaid:
                    AppendLog(GeniusChatRole.Genius, agentEvent.Text);
                    break;
                case AgentEventKind.ToolCallStarted:
                    if (agentEvent.ToolName != "say")
                    {
                        AppendLog(GeniusChatRole.Info, $"⚙ {agentEvent.ToolName} {Truncate(agentEvent.Text, 60)}");
                    }

                    break;
                case AgentEventKind.ToolCallFinished:
                    if (agentEvent.ToolName != "say" && GeniusFailure.IsError(agentEvent.Text))
                    {
                        AppendLog(GeniusChatRole.Info, $"✗ {agentEvent.ToolName}: {Truncate(agentEvent.Text, 80)}");
                    }

                    break;
                case AgentEventKind.Progress:
                    AppendLog(GeniusChatRole.Info, agentEvent.Text);
                    break;
                case AgentEventKind.Error:
                    AppendLog(GeniusChatRole.Info, $"出错:{agentEvent.Text}");
                    break;
                case AgentEventKind.NeedsUser:
                    AppendLog(GeniusChatRole.Info, $"⏸ {agentEvent.Text}");
                    _needsUserSince = Time.FrameStartTime;
                    m_componentPlayer.ComponentGui?.DisplaySmallMessage(
                        $"Genius:{agentEvent.Text}",
                        Color.White,
                        blinking: true,
                        playNotificationSound: true);
                    break;
                case AgentEventKind.TurnFinished:
                    if (_pendingMessage is not null)
                    {
                        var pending = _pendingMessage;
                        _pendingMessage = null;
                        StartOrQueueTurn(pending);
                    }

                    break;
            }
        });
    }

    /// <summary>
    /// Runs on the agent's background thread. Parses arguments there, then
    /// either hops onto the game thread (authoritative side) or relays over
    /// the network (multiplayer client); long-running orders complete later
    /// via their TaskCompletionSource either way.
    /// </summary>
    private Task<string> ExecuteToolAsync(string name, string argumentsJson)
    {
        JObject arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson) ? [] : JObject.Parse(argumentsJson)!;
        }
        catch (Exception exception)
        {
            return Task.FromResult($"error[invalid_argument]: bad tool arguments ({exception.Message})");
        }

        Log.Information($"[Genius] tool {name} {Truncate(argumentsJson, 160)}");
        var work = _workType == WorkType.Client && !GeniusNetwork.IsClientLocalTool(name)
            ? ExecuteToolOverNetworkAsync(name, arguments)
            : ExecuteToolLocallyAsync(name, arguments);
        return work.ContinueWith(task =>
        {
            var result = task.IsFaulted
                ? $"error[internal]: {task.Exception?.GetBaseException().Message}"
                : task.Result;
            Log.Information($"[Genius] tool {name} -> {Truncate(result, 240)}");
            // Failure telemetry: which categories bite most decides what to
            // fix next (and feeds the future tool benchmark's case mining).
            if (GeniusFailure.TryParse(result) is { } failureType)
            {
                lock (_failureCounts)
                {
                    _failureCounts[failureType] =
                        _failureCounts.TryGetValue(failureType, out var seen) ? seen + 1 : 1;
                }
            }
            // If the owning turn was cancelled, the model never sees this
            // result — surface it in chat so the player still gets the outcome.
            // (The agent-null check keeps the server's copy for remote players
            // from logging into a chat nobody reads.)
            if (LongRunningTools.Contains(name) && _agent is { IsBusy: false })
            {
                _mainThreadQueue.Enqueue(
                    () => AppendLog(GeniusChatRole.Info, $"(后台完成) {name}: {Truncate(result, 140)}"));
            }

            return result;
        });
    }

    private Task<string> ExecuteToolLocallyAsync(string name, JObject arguments)
    {
        var completion = new TaskCompletionSource<Task<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _mainThreadQueue.Enqueue(() =>
        {
            try
            {
                completion.TrySetResult(ExecuteToolOnMainThread(name, arguments));
            }
            catch (Exception exception)
            {
                completion.TrySetResult(Task.FromResult($"error[internal]: {exception.Message}"));
            }
        });
        return completion.Task.Unwrap();
    }

    /// <summary>
    /// Multiplayer client: relay the call to the server, which executes it
    /// against this player's companion and replies. The wait is bounded by the
    /// agent's per-tool timeouts and failed wholesale on dispose.
    /// </summary>
    private Task<string> ExecuteToolOverNetworkAsync(string name, JObject arguments)
    {
        if (!GeniusNetwork.PackageRegistered)
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.Unavailable,
                "multiplayer relay unavailable on this install (network package ID conflict)"));
        }

        // TravelMap waypoints live on THIS device — resolve before the wire.
        if (name == "teleport" && (string?)arguments["waypoint_name"] is { Length: > 0 } waypointName)
        {
            var waypoints = TravelMapBridge.TryReadWaypoints(m_componentPlayer);
            var match = waypoints?.FirstOrDefault(waypoint =>
                waypoint.Name.Contains(waypointName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return Task.FromResult(GeniusFailure.Format(FailureType.NotFound,
                    $"no waypoint matching '{waypointName}'"));
            }

            arguments = new JObject
            {
                ["x"] = (int)match.Position.X,
                ["y"] = (int)match.Position.Y,
                ["z"] = (int)match.Position.Z,
            };
        }

        var requestId = (uint)Interlocked.Increment(ref _nextNetRequestId);
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingNetTools[requestId] = pending;
        var payload = arguments.ToString(Newtonsoft.Json.Formatting.None);
        _mainThreadQueue.Enqueue(() => CommonLib.Net.QueuePackage(new GeniusToolPackage(
            new GeniusToolMessage(GeniusToolMessageKind.Request, requestId, name, payload))));
        return pending.Task;
    }

    /// <summary>Called from the package handler when the server's result lands.</summary>
    public void CompleteNetTool(uint requestId, string result)
    {
        if (_pendingNetTools.TryRemove(requestId, out var pending))
        {
            pending.TrySetResult(result);
        }
    }

    /// <summary>Marshals arbitrary work onto this component's game-thread queue.</summary>
    public void RunOnMainThread(Action action) => _mainThreadQueue.Enqueue(action);

    /// <summary>Server-side entry for a remote client's relayed request.</summary>
    public Task<string> ExecuteNetToolAsync(string name, string payload)
    {
        switch (name)
        {
            case GeniusNetwork.SummonOp:
                return OnMainThread(() =>
                {
                    SummonNpc();
                    return IsNpcSummoned
                        ? "summoned"
                        : GeniusFailure.Format(FailureType.Internal, "summon failed (see server log)");
                });
            case GeniusNetwork.DismissOp:
                return OnMainThread(() =>
                {
                    DismissNpc();
                    return "dismissed";
                });
            default:
                return GeniusNetwork.IsClientLocalTool(name)
                    ? Task.FromResult(GeniusFailure.Format(FailureType.InvalidArgument,
                        "client-local tool relayed to the server"))
                    : ExecuteToolAsync(name, payload);
        }
    }

    private static readonly HashSet<string> LongRunningTools = new(StringComparer.Ordinal)
    {
        "mine_resource", "goto", "craft", "smelt", "collect_items", "dig_block", "take_from_chest",
        "descend_to", "till_soil", "plant_seed", "build_shelter", "harvest_crops", "use_bucket",
    };

    /// <summary>
    /// Routes one tool call to its handler. This used to be a 421-line switch
    /// that every new tool had to be threaded into; the bodies now live in
    /// <c>Mod/Tools/</c>, one file per domain, and this only decides whether the
    /// companion needs to exist first.
    /// </summary>
    private Task<string> ExecuteToolOnMainThread(string name, JObject arguments)
    {
        if (Tools.GeniusToolTable.Resolve(name) is not { } handler)
        {
            return Task.FromResult($"error[invalid_argument]: unknown tool '{name}'");
        }

        ComponentGeniusBrain? brain = null;
        if (Tools.GeniusToolTable.NeedsBrain(name))
        {
            brain = FindBrain();
            if (brain is null)
            {
                return Task.FromResult(
                    "error[not_summoned]: the companion is not summoned — ask the player to summon it first");
            }

            // World-scoped landmark memory rides on the player component; hand
            // the brain a reference so orders and perception can record into it.
            brain.Landmarks = _landmarks;
        }

        var context = new Tools.GeniusToolContext(
            this, m_componentPlayer, m_subsystemTerrain, m_subsystemBodies, _knowledgeStore, brain);
        return handler(context, arguments);
    }


    private const string DeathMarker = "I died on the job";

    /// <summary>
    /// Result-oriented mining: if the NPC dies mid-expedition, resummon it,
    /// tunnel back to the death spot, recover the dropped gear, and restart the
    /// job. Gives up after three deaths.
    /// </summary>
    internal async Task<string> RunResilientMiningAsync(string resource, int count)
    {
        var notes = "";
        for (var life = 0; life < 3; life++)
        {
            // Capture the brain up front: after a death the revived NPC is a
            // fresh entity, and only this instance knows where it fell.
            var brain = await OnMainThread(FindBrain).ConfigureAwait(false);
            var result = await StartOrderAsync(() => new MineResourceOrder(resource, count))
                .ConfigureAwait(false);
            if (result is null)
            {
                return notes + "error[not_summoned]: the companion is not summoned";
            }

            if (!result.Contains(DeathMarker, StringComparison.Ordinal))
            {
                return notes.Length == 0 ? result : $"{notes}最终:{result}";
            }

            var deathPosition = brain?.DeathPosition;
            var revived = await ReviveAsync().ConfigureAwait(false);
            if (!revived)
            {
                return notes + "error[died]: I died and could not be revived";
            }

            if (deathPosition is { } position)
            {
                var deathCell = Terrain.ToCell(position);
                await StartOrderAsync(() => new GotoOrder(deathCell, digThrough: true))
                    .ConfigureAwait(false);
                var recovered = await StartOrderAsync(() => new CollectItemsOrder())
                    .ConfigureAwait(false);
                notes += $"(第{life + 1}次阵亡于({deathCell.X},{deathCell.Y},{deathCell.Z}),已复活并回收:{recovered})";
            }
        }

        return notes + "error[died]: 反复阵亡,任务中止 —— 那片区域太危险了";
    }

    private async Task<bool> ReviveAsync()
    {
        await OnMainThread(() =>
        {
            SummonNpc();
            return true;
        }).ConfigureAwait(false);
        for (var i = 0; i < 30; i++)
        {
            if (await OnMainThread(() => IsNpcSummoned).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Starts an order on the game thread and awaits its completion.</summary>
    private async Task<string?> StartOrderAsync(Func<GeniusOrder> factory)
    {
        var completion = await OnMainThread<Task<string>?>(() =>
        {
            var brain = FindBrain();
            if (brain is null)
            {
                return null;
            }

            var order = factory();
            brain.StartOrder(order);
            return order.Completion;
        }).ConfigureAwait(false);
        if (completion is null)
        {
            return null;
        }

        return await completion.ConfigureAwait(false);
    }

    private Task<T> OnMainThread<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mainThreadQueue.Enqueue(() =>
        {
            try
            {
                completion.TrySetResult(func());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private ComponentGeniusBrain? FindBrain()
    {
        ComponentGeniusBrain? nearest = null;
        var nearestDistance = float.MaxValue;
        var count = 0;
        var myPlayerId = m_componentPlayer.PlayerData.PlayerGUID.ToString("N");
        foreach (var body in m_subsystemBodies.Bodies)
        {
            var brain = body.Entity.FindComponent<ComponentGeniusBrain>();
            if (brain is null
                || body.Entity.FindComponent<ComponentSpawn>() is { IsDespawning: true })
            {
                continue;
            }

            // Owned by someone else → invisible to me (empty = legacy/unowned).
            if (brain.OwnerPlayerId.Length > 0
                && !string.Equals(brain.OwnerPlayerId, myPlayerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
            var distance = Vector3.Distance(body.Position, m_componentPlayer.ComponentBody.Position);
            if (distance < nearestDistance)
            {
                nearest = brain;
                nearestDistance = distance;
            }
        }

        if (count > 1)
        {
            Log.Warning($"[Genius] {count} NPC brains alive; using the nearest.");
        }

        return nearest;
    }

    /// <summary>Tool handlers in <c>Mod/Tools/</c> speak to the player through this.</summary>
    internal void AppendChat(GeniusChatRole role, string text) => AppendLog(role, text);

    /// <summary>Shared with the tool handlers so chat and the HUD clip alike.</summary>
    internal static string Shorten(string text, int maxLength) => Truncate(text, maxLength);

    private void AppendLog(GeniusChatRole role, string text)
    {
        Log.Information($"[Genius] chat/{role}: {Truncate(text, 200)}");
        _chatLog.Add(new GeniusChatLine(role, text));
        if (_chatLog.Count > MaxLogLines)
        {
            _chatLog.RemoveRange(0, _chatLog.Count - MaxLogLines);
        }

        ChatLogVersion++;
    }

    private static Point3 ReadPoint(JObject arguments)
    {
        var x = (int?)arguments["x"] ?? throw new InvalidOperationException("missing x");
        var y = (int?)arguments["y"] ?? throw new InvalidOperationException("missing y");
        var z = (int?)arguments["z"] ?? throw new InvalidOperationException("missing z");
        return new Point3(x, y, z);
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
