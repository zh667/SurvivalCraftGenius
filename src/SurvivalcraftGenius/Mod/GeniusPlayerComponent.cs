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
    private LlmClient? _llmClient;
    private GeniusAgent? _agent;
    private GeniusChatDialog? _dialog;
    private CancellationTokenSource? _turnCts;
    private string? _pendingMessage;

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

        if (!m_componentPlayer.PlayerData.IsMainPlayer)
        {
            return;
        }

        var input = m_componentPlayer.GameWidget.Input;
        if (input.IsKeyDownOnce(Engine.Input.Key.G) && _dialog is null)
        {
            OpenChatDialog();
        }
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
            AppendLog(GeniusChatRole.Info,
                "当前是联机客户端模式:实体必须由服务端生成,暂不支持召唤(联机支持在路线图 M4)。请在本地单机世界使用。");
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
            var body = entity.FindComponent<ComponentBody>(throwOnError: true)!;
            var playerBody = m_componentPlayer.ComponentBody;
            var spawnPosition = FindSpawnPositionNear(playerBody);
            body.Position = spawnPosition;
            body.Rotation = playerBody.Rotation;
            entity.FindComponent<ComponentSpawn>(throwOnError: true)!.SpawnDuration = 0.5f;
            Project.AddEntity(entity);
            Log.Information(
                $"[Genius] NPC spawned at {spawnPosition} (player at {playerBody.Position}, workType={_workType}).");
            AppendLog(GeniusChatRole.Info, "Genius 已召唤。按 G 打开对话,直接下指令吧。");
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
        var brain = FindBrain();
        if (brain is null)
        {
            AppendLog(GeniusChatRole.Info, "Genius 不在附近。");
            return;
        }

        brain.StopMoving();
        brain.Creature.ComponentSpawn.Despawn();
        AppendLog(GeniusChatRole.Info, "Genius 已收回。");
    }

    public void SaveSettings(GeniusSettings settings)
    {
        _settings = settings;
        _settingsStore.Save(settings);
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
        _knowledgeStore = new GeniusKnowledgeStore(
            Storage.GetSystemPath("data:/SurvivalcraftGenius/knowledge"));
        _knowledgeStore.EnsureStarter();
        Log.Information($"[Genius] Player component loaded (workType={_workType}).");
    }

    public override void OnEntityRemoved()
    {
        _lifetime.Cancel();
        _dialog = null;
        base.OnEntityRemoved();
    }

    public override void Dispose()
    {
        _lifetime.Cancel();
        _llmClient?.Dispose();
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
        _agent = new GeniusAgent(
            _llmClient,
            ToolCatalog.CreateDefaultRegistry(),
            ExecuteToolAsync,
            OnAgentEvent,
            _settings);
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
                    if (agentEvent.ToolName != "say" && agentEvent.Text.StartsWith("error:", StringComparison.Ordinal))
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
    /// Runs on the agent's background thread. Parses arguments there, then hops
    /// onto the game thread to touch game state; long-running orders complete
    /// later via their TaskCompletionSource.
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
            return Task.FromResult($"error: bad tool arguments ({exception.Message})");
        }

        Log.Information($"[Genius] tool {name} {Truncate(argumentsJson, 160)}");
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
                completion.TrySetResult(Task.FromResult($"error: {exception.Message}"));
            }
        });
        return completion.Task.Unwrap().ContinueWith(task =>
        {
            var result = task.IsFaulted
                ? $"error: {task.Exception?.GetBaseException().Message}"
                : task.Result;
            Log.Information($"[Genius] tool {name} -> {Truncate(result, 240)}");
            // If the owning turn was cancelled, the model never sees this
            // result — surface it in chat so the player still gets the outcome.
            if (LongRunningTools.Contains(name) && _agent?.IsBusy != true)
            {
                _mainThreadQueue.Enqueue(
                    () => AppendLog(GeniusChatRole.Info, $"(后台完成) {name}: {Truncate(result, 140)}"));
            }

            return result;
        });
    }

    private static readonly HashSet<string> LongRunningTools = new(StringComparer.Ordinal)
    {
        "mine_resource", "goto", "craft", "smelt", "collect_items", "dig_block", "take_from_chest",
    };

    private Task<string> ExecuteToolOnMainThread(string name, JObject arguments)
    {
        if (name == "say")
        {
            var text = (string?)arguments["text"] ?? "";
            if (text.Length > 0)
            {
                AppendLog(GeniusChatRole.Genius, text);
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    $"Genius: {Truncate(text, 80)}",
                    Color.LightGreen,
                    blinking: false,
                    playNotificationSound: false);
            }

            return Task.FromResult("said");
        }

        // Knowledge tools work without the NPC being summoned.
        switch (name)
        {
            case "query_recipes":
                return Task.FromResult(GeniusKnowledge.QueryRecipes(
                    m_subsystemTerrain, (string?)arguments["item_name"] ?? ""));
            case "query_help":
                return Task.FromResult(GeniusKnowledge.QueryHelp((string?)arguments["keyword"] ?? ""));
            case "read_knowledge":
                return Task.FromResult(_knowledgeStore.Read((string?)arguments["topic"]));
        }

        var brain = FindBrain();
        if (brain is null)
        {
            return Task.FromResult("error: the companion is not summoned — ask the player to summon it first");
        }

        switch (name)
        {
            case "scan_surroundings":
                return Task.FromResult(
                    GeniusPerception.ScanSurroundings(brain, m_componentPlayer.ComponentBody));
            case "get_inventory":
                return Task.FromResult(GeniusPerception.DescribeInventory(brain));
            case "follow_player":
                brain.StartFollowing(m_componentPlayer.ComponentBody);
                return Task.FromResult("now following the player");
            case "goto":
            {
                var order = new GotoOrder(
                    ReadPoint(arguments),
                    (bool?)arguments["dig_through"] ?? false);
                brain.StartOrder(order);
                return order.Completion;
            }

            case "mine_resource":
                return RunResilientMiningAsync(
                    (string?)arguments["resource_name"] ?? "",
                    (int?)arguments["count"] ?? 1);

            case "list_waypoints":
            {
                var waypoints = TravelMapBridge.TryReadWaypoints(m_componentPlayer);
                if (waypoints is null)
                {
                    return Task.FromResult("error: TravelMap mod is not installed (or has no data yet)");
                }

                if (waypoints.Count == 0)
                {
                    return Task.FromResult("no waypoints saved on the travel map yet");
                }

                var listed = waypoints.Select(waypoint =>
                    $"{waypoint.Name} ({(int)waypoint.Position.X}, {(int)waypoint.Position.Y}, {(int)waypoint.Position.Z})");
                return Task.FromResult("waypoints: " + string.Join("; ", listed));
            }

            case "teleport":
            {
                Vector3 destination;
                var waypointName = (string?)arguments["waypoint_name"];
                if (!string.IsNullOrWhiteSpace(waypointName))
                {
                    var waypoints = TravelMapBridge.TryReadWaypoints(m_componentPlayer);
                    var match = waypoints?.FirstOrDefault(waypoint =>
                        waypoint.Name.Contains(waypointName, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        return Task.FromResult($"error: no waypoint matching '{waypointName}'");
                    }

                    destination = match.Position;
                }
                else if (arguments["x"] is not null && arguments["y"] is not null && arguments["z"] is not null)
                {
                    var point = ReadPoint(arguments);
                    destination = new Vector3(point.X + 0.5f, point.Y, point.Z + 0.5f);
                }
                else
                {
                    return Task.FromResult("error: give either waypoint_name or x/y/z");
                }

                brain.StopMoving();
                brain.Creature.ComponentBody.Position = destination + new Vector3(0f, 0.5f, 0f);
                return Task.FromResult(
                    $"teleported to ({(int)destination.X}, {(int)destination.Y}, {(int)destination.Z})" +
                    "; note: if this is far from the player the area may be unloaded — prefer teleporting near the player or a visited spot");
            }

            case "dig_block":
            {
                var order = new DigOrder(ReadPoint(arguments));
                brain.StartOrder(order);
                return order.Completion;
            }

            case "place_block":
            {
                var slotIndex = (int?)arguments["slot_index"] ?? -1;
                var order = new PlaceOrder(ReadPoint(arguments), slotIndex);
                brain.StartOrder(order);
                return order.Completion;
            }

            case "collect_items":
            {
                var order = new CollectItemsOrder();
                brain.StartOrder(order);
                return order.Completion;
            }

            case "take_from_chest":
            {
                var order = new TakeFromChestOrder(
                    ReadPoint(arguments),
                    (string?)arguments["item_name"],
                    (int?)arguments["max_count"] ?? int.MaxValue);
                brain.StartOrder(order);
                return order.Completion;
            }

            case "put_into_chest":
            {
                var order = new PutIntoChestOrder(ReadPoint(arguments), (string?)arguments["item_name"]);
                brain.StartOrder(order);
                return order.Completion;
            }

            case "craft":
            {
                var order = new CraftOrder(
                    (string?)arguments["item_name"] ?? "",
                    (int?)arguments["count"] ?? 1);
                brain.StartOrder(order);
                return order.Completion;
            }

            case "smelt":
            {
                var order = new SmeltOrder(
                    (string?)arguments["item_name"] ?? "",
                    (int?)arguments["count"] ?? 1);
                brain.StartOrder(order);
                return order.Completion;
            }

            case "give_to_player":
            {
                var order = new GiveToPlayerOrder(
                    m_componentPlayer.ComponentBody,
                    (string?)arguments["item_name"],
                    (int?)arguments["max_count"] ?? int.MaxValue);
                brain.StartOrder(order);
                return order.Completion;
            }

            case "equip_tool":
            {
                var inventory = brain.Miner.Inventory;
                var slotIndex = (int?)arguments["slot_index"] ?? -1;
                if (inventory is null || slotIndex < 0 || slotIndex >= inventory.SlotsCount)
                {
                    return Task.FromResult("error: invalid slot index");
                }

                inventory.ActiveSlotIndex = slotIndex;
                var value = inventory.GetSlotValue(slotIndex);
                var equippedName = value == 0
                    ? "empty hand"
                    : BlocksManager.Blocks[Terrain.ExtractContents(value)]
                        .GetDisplayName(brain.SubsystemTerrain, value);
                return Task.FromResult($"equipped: {equippedName}");
            }

            case "attack":
            {
                var query = (string?)arguments["target_name"] ?? "";
                var target = FindAttackTarget(brain, query);
                if (target is null)
                {
                    return Task.FromResult($"error: no creature matching '{query}' within 24m");
                }

                var order = new AttackOrder(target);
                brain.StartOrder(order);
                return order.Completion;
            }

            default:
                return Task.FromResult($"error: unknown tool '{name}'");
        }
    }

    private const string DeathMarker = "I died on the job";

    /// <summary>
    /// Result-oriented mining: if the NPC dies mid-expedition, resummon it,
    /// tunnel back to the death spot, recover the dropped gear, and restart the
    /// job. Gives up after three deaths.
    /// </summary>
    private async Task<string> RunResilientMiningAsync(string resource, int count)
    {
        var notes = "";
        for (var life = 0; life < 3; life++)
        {
            var result = await StartOrderAsync(() => new MineResourceOrder(resource, count))
                .ConfigureAwait(false);
            if (result is null)
            {
                return notes + "error: the companion is not summoned";
            }

            if (!result.Contains(DeathMarker, StringComparison.Ordinal))
            {
                return notes.Length == 0 ? result : $"{notes}最终:{result}";
            }

            var deathPosition = ComponentGeniusBrain.LastDeathPosition;
            var revived = await ReviveAsync().ConfigureAwait(false);
            if (!revived)
            {
                return notes + "error: I died and could not be revived";
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

        return notes + "error: 反复阵亡,任务中止 —— 那片区域太危险了";
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

    private ComponentCreature? FindAttackTarget(ComponentGeniusBrain brain, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        ComponentCreature? best = null;
        var bestDistance = 24f;
        foreach (var body in m_subsystemBodies.Bodies)
        {
            var creature = body.Entity.FindComponent<ComponentCreature>();
            if (creature is null
                || creature is ComponentPlayer
                || creature == brain.Creature
                || creature.ComponentHealth.Health <= 0f
                || !creature.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var distance = Vector3.Distance(body.Position, brain.Creature.ComponentBody.Position);
            if (distance < bestDistance)
            {
                best = creature;
                bestDistance = distance;
            }
        }

        return best;
    }

    private ComponentGeniusBrain? FindBrain()
    {
        ComponentGeniusBrain? nearest = null;
        var nearestDistance = float.MaxValue;
        var count = 0;
        foreach (var body in m_subsystemBodies.Bodies)
        {
            var brain = body.Entity.FindComponent<ComponentGeniusBrain>();
            if (brain is null
                || body.Entity.FindComponent<ComponentSpawn>() is { IsDespawning: true })
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
