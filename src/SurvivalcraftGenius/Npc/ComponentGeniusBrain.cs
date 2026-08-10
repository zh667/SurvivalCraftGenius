using SurvivalcraftGenius.Agent;
using Engine;
using Game;
using Game.NetWork;
using GameEntitySystem;
using TemplatesDatabase;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// The NPC-side executor. Owns at most one active order (goto/dig/place) plus an
/// optional continuous follow mode, and drives the vanilla pathfinding stack.
/// All calls happen on the game main thread; async callers get their result via
/// the TaskCompletionSource attached to the order.
/// </summary>
public sealed class ComponentGeniusBrain : ComponentBehavior, IUpdateable
{
    public SubsystemTerrain m_subsystemTerrain = null!;
    public SubsystemTime m_subsystemTime = null!;
    public SubsystemBodies m_subsystemBodies = null!;
    public SubsystemPickables m_subsystemPickables = null!;
    public SubsystemBlockEntities m_subsystemBlockEntities = null!;
    public ComponentCreature m_componentCreature = null!;
    public ComponentPathfinding m_componentPathfinding = null!;
    public ComponentMiner m_componentMiner = null!;

    private GeniusOrder? _order;
    private ComponentBody? _followTarget;
    private double _nextFollowUpdateTime;
    private float _suppressedTime;
    private double _nextVacuumTime;
    private readonly Dictionary<string, int> _recentPickups = [];
    private string? _lastAttackerName;
    private double _lastAttackedTime;

    /// <summary>How long after a hit we still blame the attacker for the death.</summary>
    private const double AttackerMemorySeconds = 10.0;

    public UpdateOrder UpdateOrder => UpdateOrder.Default;

    public override float ImportanceLevel
    {
        get
        {
            // 310 outranks even low-health fleeing (300): orders run to the
            // end — or to death, which the revive-and-resume loop handles.
            if (_order is not null)
            {
                return 310f;
            }

            return _followTarget is not null ? 190f : 0f;
        }
    }

    /// <summary>
    /// Where this NPC died (for gear recovery); set on fatal removal. Instance
    /// state — callers must capture the brain reference before the death, since
    /// the revived NPC is a fresh entity with a fresh brain.
    /// </summary>
    public Vector3? DeathPosition { get; private set; }

    public ComponentCreature Creature => m_componentCreature;

    public ComponentMiner Miner => m_componentMiner;

    public SubsystemTerrain SubsystemTerrain => m_subsystemTerrain;

    public SubsystemBodies SubsystemBodies => m_subsystemBodies;

    public SubsystemPickables SubsystemPickables => m_subsystemPickables;

    public SubsystemBlockEntities SubsystemBlockEntities => m_subsystemBlockEntities;

    /// <summary>
    /// World-scoped landmark memory (owned by the player component, attached
    /// on tool dispatch) — orders and perception record stations they find.
    /// </summary>
    public LandmarkMemory? Landmarks { get; set; }

    /// <summary>
    /// PlayerGUID ("N" format) of the player who summoned this NPC; empty =
    /// unowned (legacy single-player worlds). In multiplayer each player only
    /// commands their own companion. Persisted with the entity.
    /// </summary>
    public string OwnerPlayerId { get; set; } = "";

    /// <summary>
    /// Set when teleporting into unloaded terrain: the body hovers here
    /// (pinned, zero velocity) until the chunk loads, then snaps to the
    /// surface. Teleporting blind at a guessed Y killed the NPC twice in
    /// playtests — physics runs before terrain exists.
    /// </summary>
    public Vector3? PendingTeleportHover { get; set; }

    /// <summary>
    /// The cell the pending hover is actually aiming for, so the landing can
    /// honour the requested Y (including underground) instead of defaulting to
    /// the surface once the chunk arrives.
    /// </summary>
    public Point3? PendingTeleportTarget { get; set; }

    /// <summary>Self-preservation reflexes that outbid orders for the body.</summary>
    public GeniusInstincts Instincts { get; } = new();

    /// <summary>Keeps chunks loaded and wildlife spawning around the NPC on far expeditions.</summary>
    public GeniusExpeditionKeeper Expedition { get; } = new();

    /// <summary>Starts an order; a running order is cancelled with a failure result.</summary>
    public void StartOrder(GeniusOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        // One body, one intent: a new order also ends follow mode, as the
        // follow_player tool contract promises — otherwise following silently
        // resumes when the order finishes and drags the NPC back to the player.
        _followTarget = null;
        _order?.Finish("error[superseded]: superseded by a newer order");
        _order = order;
        order.Start(this);
    }

    public void StartFollowing(ComponentBody target)
    {
        _followTarget = target;
        _nextFollowUpdateTime = 0.0;
    }

    public void StopMoving()
    {
        _followTarget = null;
        _order?.Finish("error[superseded]: stopped");
        _order = null;
        m_componentPathfinding.Stop();
    }

    public void Update(float dt)
    {
        if (CommonLib.WorkType == WorkType.Client)
        {
            // Replicated shell: entity templates arrive on clients with every
            // component constructed and ticked. The server owns simulation;
            // the body's position flows in via the engine's body sync.
            return;
        }

        Expedition.Tick(this);
        if (PendingTeleportHover is { } hover)
        {
            var body = m_componentCreature.ComponentBody;
            var hoverCell = Terrain.ToCell(hover);
            if (m_subsystemTerrain.Terrain.GetChunkAtCell(hoverCell.X, hoverCell.Z) is not null)
            {
                // Land where the caller actually asked to land. This used to
                // always snap to the surface, which silently discarded the Y —
                // so a teleport into an unloaded ore band came back 40 blocks
                // too high and the model had to burn another call (playtest 8).
                var landing = PendingTeleportTarget is { } wanted
                    ? GeniusTeleportLanding.Resolve(this, wanted)
                    : default;
                body.Position = landing.Error is null && PendingTeleportTarget is not null
                    ? landing.Position + new Vector3(0f, 0.5f, 0f)
                    : new Vector3(
                        hoverCell.X + 0.5f,
                        m_subsystemTerrain.Terrain.GetTopHeight(hoverCell.X, hoverCell.Z) + 1.5f,
                        hoverCell.Z + 0.5f);
                body.Velocity = Vector3.Zero;
                PendingTeleportHover = null;
                PendingTeleportTarget = null;
            }
            else
            {
                // Pin in the sky until the expedition keeper loads the area.
                body.Position = hover;
                body.Velocity = Vector3.Zero;
                return;
            }
        }

        UpdateCore(dt);
        // Instincts run last so their movement overrides whatever the order
        // asked for this frame — the LLM is the lowest bidder for the body.
        Instincts.Tick(this, dt);
    }

    private void UpdateCore(float dt)
    {
        // Always vacuum drops at my feet — thrown gifts, dig spoils, loot.
        if (m_subsystemTime.GameTime >= _nextVacuumTime)
        {
            _nextVacuumTime = m_subsystemTime.GameTime + 0.5;
            VacuumNearbyPickables();
        }

        if (_order is not null)
        {
            // Dead bodies cannot act: ComponentBehaviorSelector picks no
            // behavior at all while Health == 0, so IsActive is false for every
            // behavior. Without this check the order reports "endangered",
            // which reads as "busy fleeing" and had the model waiting for a
            // recovery that can never come.
            if (m_componentCreature.ComponentHealth.Health <= 0f)
            {
                var cell = Terrain.ToCell(m_componentCreature.ComponentBody.Position);
                _order.Finish($"error[died]: I was killed at ({cell.X},{cell.Y},{cell.Z}) — "
                    + $"{DeathCauseOrUnknown()}. Everything I carried is kept for the next "
                    + "summon — tell the player how I died and ask them to summon me again; "
                    + "I cannot act until then");
                _order = null;
                return;
            }

            if (!IsActive)
            {
                // A higher-priority behavior (fleeing at low health, importance
                // 300) is overriding us. Fail fast instead of hanging the tool
                // call until its timeout.
                _suppressedTime += dt;
                if (_suppressedTime > 8f)
                {
                    _suppressedTime = 0f;
                    _order.Finish("error[endangered]: I'm in danger (fleeing) and cannot work right now");
                    _order = null;
                }

                return;
            }

            _suppressedTime = 0f;
            var finished = _order.Tick(this, dt);
            if (finished)
            {
                _order = null;
                m_componentPathfinding.Stop();
                // Blanket cleanup — orders that sneak (stalking attacks) must
                // not leave the flag on after any exit path incl. timeout.
                m_componentCreature.ComponentBody.IsSneaking = false;
            }

            return;
        }

        if (_followTarget is not null && IsActive)
        {
            if (_followTarget.Entity.Project is null)
            {
                _followTarget = null;
                m_componentPathfinding.Stop();
                return;
            }

            if (m_subsystemTime.GameTime >= _nextFollowUpdateTime)
            {
                _nextFollowUpdateTime = m_subsystemTime.GameTime + 0.5;
                var distance = Vector3.Distance(
                    m_componentCreature.ComponentBody.Position,
                    _followTarget.Position);
                if (distance > 3.5f)
                {
                    m_componentPathfinding.SetDestination(
                        _followTarget.Position,
                        1f,
                        3f,
                        2000,
                        useRandomMovements: true,
                        ignoreHeightDifference: false,
                        raycastDestination: false,
                        _followTarget);
                }
                else
                {
                    m_componentPathfinding.Stop();
                }
            }
        }
    }

    public override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        base.Load(valuesDictionary, idToEntityMap);
        OwnerPlayerId = valuesDictionary.GetValue("OwnerPlayerId", "");
        m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(throwOnError: true);
        m_subsystemTime = Project.FindSubsystem<SubsystemTime>(throwOnError: true);
        m_subsystemBodies = Project.FindSubsystem<SubsystemBodies>(throwOnError: true);
        m_subsystemPickables = Project.FindSubsystem<SubsystemPickables>(throwOnError: true);
        m_subsystemBlockEntities = Project.FindSubsystem<SubsystemBlockEntities>(throwOnError: true);
        m_componentCreature = Entity.FindComponent<ComponentCreature>(throwOnError: true)!;
        m_componentPathfinding = Entity.FindComponent<ComponentPathfinding>(throwOnError: true)!;
        m_componentMiner = Entity.FindComponent<ComponentMiner>(throwOnError: true)!;
        // Same hook vanilla creatures use for retaliation (ComponentChaseBehavior).
        m_componentCreature.ComponentHealth.Attacked += Instincts.NotifyAttacked;
        m_componentCreature.ComponentHealth.Attacked += RecordAttacker;
    }

    /// <summary>
    /// Remembers who hit us last. ComponentHealth.CauseOfDeath records the
    /// KIND of damage ("被咬伤"), but only names a killer when the killer is
    /// another player — for a wolf or a hyena it stays anonymous, which is why
    /// the companion could only answer "系统只返回阵亡,没说明原因" when asked how
    /// it died. The engine raises Attacked solely for attacker != null, so a
    /// recent entry here is exactly "something killed me" vs "I fell/drowned".
    /// </summary>
    private void RecordAttacker(ComponentCreature? attacker)
    {
        if (attacker is null)
        {
            return;
        }

        _lastAttackerName = attacker is ComponentPlayer player
            ? player.PlayerData?.Name ?? attacker.DisplayName
            : attacker.DisplayName;
        _lastAttackedTime = m_subsystemTime.GameTime;
    }

    /// <summary>
    /// A human-readable cause of death: the engine's own damage description
    /// plus the attacker, when one landed a hit in the last few seconds.
    /// Returns null while alive or when nothing is known.
    /// </summary>
    public string? DeathCause()
    {
        if (m_componentCreature.ComponentHealth.Health > 0f)
        {
            return null;
        }

        var cause = m_componentCreature.ComponentHealth.CauseOfDeath;
        var killer = _lastAttackerName is { Length: > 0 } name
            && m_subsystemTime.GameTime - _lastAttackedTime <= AttackerMemorySeconds
                ? name
                : null;

        return (cause, killer) switch
        {
            ({ Length: > 0 }, not null) => $"{cause}(凶手:{killer})",
            ({ Length: > 0 }, null) => cause,
            (_, not null) => $"被{killer}杀死",
            _ => null,
        };
    }

    /// <summary>Where the death happened, for the player-facing announcement.</summary>
    public string DeathCauseOrUnknown() => DeathCause() ?? "死因不明(没有留下伤害记录)";

    public override void OnEntityAdded()
    {
        Engine.Log.Information(
            $"[Genius] NPC entity added to project at {m_componentCreature.ComponentBody.Position}.");
        base.OnEntityAdded();
    }

    public override void Save(ValuesDictionary valuesDictionary, EntityToIdMap entityToIdMap)
    {
        base.Save(valuesDictionary, entityToIdMap);
        if (OwnerPlayerId.Length > 0)
        {
            valuesDictionary.SetValue("OwnerPlayerId", OwnerPlayerId);
        }
    }

    public override void OnEntityRemoved()
    {
        if (CommonLib.WorkType == WorkType.Client)
        {
            base.OnEntityRemoved();
            return;
        }

        var died = m_componentCreature.ComponentHealth.Health <= 0f;
        Engine.Log.Information(
            $"[Genius] NPC entity removed from project (pos={m_componentCreature.ComponentBody.Position}, " +
            $"health={m_componentCreature.ComponentHealth.Health}, died={died}).");
        SpillInventoryIfDead();
        Expedition.Shutdown(this);
        var cause = died ? DeathCauseOrUnknown() : null;
        _order?.Finish(died
            ? $"error[died]: I died on the job — {cause} (my inventory is preserved and returns "
                + "with me on re-summon)"
            : "error[not_summoned]: the companion was removed from the world");
        _order = null;
        if (died)
        {
            // The player has no other way to learn about it: the body simply
            // stops existing, and a chat reply from a dead companion is worse
            // than silence (playtest: "AI没血了还能和我对话").
            CompanionDied?.Invoke(
                OwnerPlayerId, m_componentCreature.ComponentBody.Position, cause!);
        }

        base.OnEntityRemoved();
    }

    public void VacuumNearbyPickables(float range = 2.6f)
    {
        if (m_componentMiner.Inventory is not { } inventory
            || m_componentCreature.ComponentHealth.Health <= 0f)
        {
            return;
        }

        var myPosition = m_componentCreature.ComponentBody.Position;
        foreach (var pickable in m_subsystemPickables.Pickables)
        {
            if (pickable.ToRemove
                || Vector3.Distance(pickable.Position, myPosition) > range)
            {
                continue;
            }

            var leftover = ComponentInventoryBase.AcquireItems(inventory, pickable.Value, pickable.Count);
            var taken = pickable.Count - leftover;
            if (taken > 0)
            {
                var name = BlocksManager.Blocks[Terrain.ExtractContents(pickable.Value)]
                    .GetDisplayName(m_subsystemTerrain, pickable.Value);
                _recentPickups[name] = _recentPickups.TryGetValue(name, out var count) ? count + taken : taken;
            }

            if (leftover == 0)
            {
                pickable.ToRemove = true;
            }
            else
            {
                pickable.Count = leftover;
            }
        }
    }

    /// <summary>
    /// Items auto-picked since the last drain — tools report these so the model
    /// knows loot went straight into the inventory (it can't see the vacuum).
    /// </summary>
    public string DrainRecentPickups()
    {
        if (_recentPickups.Count == 0)
        {
            return "";
        }

        var summary = string.Join(", ", _recentPickups.Select(pair => $"{pair.Key} x{pair.Value}"));
        _recentPickups.Clear();
        return summary;
    }

    public bool IsFollowing => _followTarget is not null;

    /// <summary>
    /// True from the killing blow until the corpse is removed. The HUD needs
    /// this window: for those seconds the brain still exists and still reports
    /// its last order, which reads as "still working".
    /// </summary>
    public bool IsDead => m_componentCreature.ComponentHealth.Health <= 0f;

    public string? CurrentOrderLabel => _order?.GetType().Name;

    /// <summary>
    /// Kept gear, per owner: filled on death and on dismissal, handed back by
    /// the next summon. Persisted alongside the world's conversation memory —
    /// dismissing, quitting and coming back tomorrow must not eat the backpack.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, List<(int Value, int Count)>> DeathStashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised whenever a stash changes so the owner can persist it. An event,
    /// not a settable delegate: on a server every player has their own
    /// GeniusPlayerComponent and a plain assignment would leave only the last
    /// one wired up.
    /// </summary>
    public static event Action? StashesChanged;

    /// <summary>Raised when a companion dies, with its owner id, death spot and cause.</summary>
    public static event Action<string, Vector3, string>? CompanionDied;

    /// <summary>Removes and returns the pending stash for an owner, if any.</summary>
    public static List<(int Value, int Count)>? TakeDeathStash(string ownerId)
    {
        if (!DeathStashes.TryRemove(ownerId ?? "", out var stash))
        {
            return null;
        }

        StashesChanged?.Invoke();
        return stash;
    }

    /// <summary>Whole-table access for the persistence layer (server side only).</summary>
    public static IReadOnlyDictionary<string, List<(int Value, int Count)>> SnapshotStashes() =>
        DeathStashes.ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>Replaces the in-memory table with what a world's save file held.</summary>
    public static void LoadStashes(IReadOnlyDictionary<string, List<(int Value, int Count)>> stashes)
    {
        DeathStashes.Clear();
        foreach (var (ownerId, items) in stashes)
        {
            if (items.Count > 0)
            {
                DeathStashes[ownerId] = [.. items];
            }
        }
    }

    /// <summary>
    /// Moves everything carried into the owner's stash and returns how many
    /// items were kept. Used by both exits from the world — dying (called from
    /// the keep-inventory hook, before the engine's drop pass) and being
    /// dismissed (playtest: 收回 left the whole backpack lying on the ground).
    /// Merges instead of overwriting, so dying twice never erases the first
    /// stash.
    /// </summary>
    public int StashCarriedItems()
    {
        if (m_componentMiner.Inventory is not { } inventory)
        {
            return 0;
        }

        var stash = DeathStashes.TryGetValue(OwnerPlayerId, out var existing) ? existing : [];
        var kept = 0;
        for (var slot = 0; slot < inventory.SlotsCount; slot++)
        {
            var value = inventory.GetSlotValue(slot);
            var count = inventory.GetSlotCount(slot);
            if (value != 0 && count > 0)
            {
                stash.Add((value, count));
                inventory.RemoveSlotItems(slot, count);
                kept += count;
            }
        }

        if (stash.Count > 0)
        {
            DeathStashes[OwnerPlayerId] = stash;
            StashesChanged?.Invoke();
        }

        return kept;
    }

    /// <summary>
    /// Safety net at removal time. With keep-inventory on, the death hook has
    /// already emptied the inventory and this finds nothing; with the rule off
    /// the engine's drop pass got there first. Either way it records where the
    /// body fell, for gear recovery.
    /// </summary>
    private void SpillInventoryIfDead()
    {
        if (m_componentCreature.ComponentHealth.Health > 0f)
        {
            return;
        }

        DeathPosition = m_componentCreature.ComponentBody.Position;
        StashCarriedItems();
    }
}

/// <summary>A multi-frame task executed by the brain on the game thread.</summary>
public abstract class GeniusOrder
{
    private readonly TaskCompletionSource<string> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private double _deadline;

    public Task<string> Completion => _completion.Task;

    protected abstract float TimeoutSeconds { get; }

    public void Start(ComponentGeniusBrain brain)
    {
        _deadline = brain.m_subsystemTime.GameTime + TimeoutSeconds;
        try
        {
            OnStart(brain);
        }
        catch (Exception exception)
        {
            Finish($"error[internal]: {exception.Message}");
        }
    }

    /// <summary>Returns true when the order is finished (result already set).</summary>
    public bool Tick(ComponentGeniusBrain brain, float dt)
    {
        if (_completion.Task.IsCompleted)
        {
            return true;
        }

        if (DeadlineFrozen(brain))
        {
            // The async route planner is thinking and the body is idle; the
            // deadline budgets body work, so wall-clock spent planning must
            // not burn it (Numen: freeze task deadline while planningInFlight).
            _deadline += dt;
        }
        else if (brain.m_subsystemTime.GameTime >= _deadline)
        {
            Finish(TimeoutResult());
            return true;
        }

        try
        {
            var result = OnTick(brain, dt);
            if (result is not null)
            {
                Finish(result);
                return true;
            }
        }
        catch (Exception exception)
        {
            Finish($"error[internal]: {exception.Message}");
            return true;
        }

        return false;
    }

    public void Finish(string result)
    {
        _completion.TrySetResult(result);
    }

    protected abstract void OnStart(ComponentGeniusBrain brain);

    /// <summary>Override to report partial progress when the order times out.</summary>
    protected virtual string TimeoutResult() => "error[timeout]: timed out";

    /// <summary>True while an async planner is thinking (freezes the deadline).</summary>
    protected virtual bool DeadlineFrozen(ComponentGeniusBrain brain) => false;

    /// <summary>Returns null while running, or the final result string.</summary>
    protected abstract string? OnTick(ComponentGeniusBrain brain, float dt);

    protected static void WalkTowards(
        ComponentGeniusBrain brain,
        Vector3 destination,
        float range)
    {
        brain.m_componentPathfinding.SetDestination(
            destination,
            1f,
            range,
            2000,
            useRandomMovements: true,
            ignoreHeightDifference: false,
            raycastDestination: false,
            null!);
    }
}

public sealed class GotoOrder(Point3 target, bool digThrough = false) : GeniusOrder
{
    private TunnelNavigator? _navigator;

    private Vector3 Destination => new(target.X + 0.5f, target.Y, target.Z + 0.5f);

    protected override float TimeoutSeconds => digThrough ? 240f : 90f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        _navigator = new TunnelNavigator(Destination, digThrough, arriveDistance: 2.0f);
    }

    protected override bool DeadlineFrozen(ComponentGeniusBrain brain) =>
        _navigator?.PlanningInFlight == true;

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        switch (_navigator!.Tick(brain, dt))
        {
            case NavStatus.Arrived:
                return $"arrived at ({target.X}, {target.Y}, {target.Z})";
            case NavStatus.Failed:
                var position = brain.Creature.ComponentBody.Position;
                return $"error[{GeniusFailure.Slug(_navigator.FailureType)}]: {_navigator.FailureReason} — I'm at " +
                    $"({(int)position.X}, {(int)position.Y}, {(int)position.Z})" +
                    (digThrough ? "" : "; retry with dig_through=true to tunnel there");
            default:
                return null;
        }
    }
}

public sealed class DigOrder(Point3 target) : GeniusOrder
{
    private const float ReachDistance = 4.5f;
    private bool _digging;
    private float _digTimeNeeded;
    private float _digTimeSpent;

    private Vector3 BlockCenter => new(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);

    protected override float TimeoutSeconds => 120f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        var cellValue = brain.SubsystemTerrain.Terrain.GetCellValue(target.X, target.Y, target.Z);
        if (Terrain.ExtractContents(cellValue) == 0)
        {
            Finish("error[invalid_target]: that position is air, nothing to dig");
            return;
        }

        WalkTowards(brain, BlockCenter, 2.5f);
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var cellValue = brain.SubsystemTerrain.Terrain.GetCellValue(target.X, target.Y, target.Z);
        var contents = Terrain.ExtractContents(cellValue);
        if (contents == 0)
        {
            return "the block is already gone";
        }

        var position = brain.Creature.ComponentBody.Position;
        var distance = Vector3.Distance(position, BlockCenter);
        if (!_digging)
        {
            if (distance <= ReachDistance)
            {
                TimedDigger.EquipBestToolFor(brain, cellValue);
                var activeValue = brain.Miner.ActiveBlockValue;
                _digTimeNeeded = brain.Miner.CalculateDigTime(cellValue, Terrain.ExtractContents(activeValue));
                if (float.IsPositiveInfinity(_digTimeNeeded))
                {
                    return "error[tool_too_weak]: this block cannot be dug with my current tool";
                }

                brain.m_componentPathfinding.Stop();
                _digging = true;
            }
            else if (brain.m_componentPathfinding.IsStuck)
            {
                return "error[no_path]: cannot get close enough to the block — path is blocked";
            }
            else if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, BlockCenter, 2.5f);
            }

            return null;
        }

        if (distance > ReachDistance + 2f)
        {
            _digging = false;
            WalkTowards(brain, BlockCenter, 2.5f);
            return null;
        }

        _digTimeSpent += dt;
        if (_digTimeSpent < _digTimeNeeded)
        {
            return null;
        }

        var block = BlocksManager.Blocks[contents];
        var blockName = block.GetDisplayName(brain.SubsystemTerrain, cellValue);
        var toolValue = brain.Miner.ActiveBlockValue;
        var toolLevel = BlocksManager.Blocks[Terrain.ExtractContents(toolValue)].ToolLevel;
        brain.SubsystemTerrain.DestroyCell(
            toolLevel,
            target.X,
            target.Y,
            target.Z,
            0,
            noDrop: false,
            noParticleSystem: false);
        brain.Miner.DamageActiveTool(1);
        return $"dug '{blockName}' at ({target.X}, {target.Y}, {target.Z}); drops fell on the ground";
    }
}

public sealed class PlaceOrder(Point3 target, int slotIndex) : GeniusOrder
{
    private const float ReachDistance = 4.5f;

    private Vector3 BlockCenter => new(target.X + 0.5f, target.Y + 0.5f, target.Z + 0.5f);

    protected override float TimeoutSeconds => 90f;

    protected override void OnStart(ComponentGeniusBrain brain)
    {
        WalkTowards(brain, BlockCenter, 2.5f);
    }

    protected override string? OnTick(ComponentGeniusBrain brain, float dt)
    {
        var inventory = brain.Miner.Inventory;
        if (inventory is null || slotIndex < 0 || slotIndex >= inventory.SlotsCount)
        {
            return "error[invalid_argument]: invalid inventory slot";
        }

        var position = brain.Creature.ComponentBody.Position;
        var distance = Vector3.Distance(position, BlockCenter);
        if (distance > ReachDistance)
        {
            if (brain.m_componentPathfinding.IsStuck)
            {
                return "error[no_path]: cannot get close enough to the target spot";
            }

            if (!brain.m_componentPathfinding.Destination.HasValue)
            {
                WalkTowards(brain, BlockCenter, 2.5f);
            }

            return null;
        }

        var existing = brain.SubsystemTerrain.Terrain.GetCellValue(target.X, target.Y, target.Z);
        if (Terrain.ExtractContents(existing) != 0)
        {
            return "error[invalid_target]: target position is not empty";
        }

        var slotValue = inventory.GetSlotValue(slotIndex);
        var slotCount = inventory.GetSlotCount(slotIndex);
        if (slotValue == 0 || slotCount <= 0)
        {
            return "error[invalid_argument]: that inventory slot is empty";
        }

        var block = BlocksManager.Blocks[Terrain.ExtractContents(slotValue)];
        if (!block.IsPlaceable)
        {
            return "error[invalid_target]: that item is not a placeable block";
        }

        if (target.Y is <= 0 or >= 255)
        {
            return "error[invalid_target]: cannot place at that height";
        }

        var blockName = block.GetDisplayName(brain.SubsystemTerrain, slotValue);
        brain.SubsystemTerrain.DestroyCell(
            0,
            target.X,
            target.Y,
            target.Z,
            slotValue,
            noDrop: false,
            noParticleSystem: true);
        inventory.RemoveSlotItems(slotIndex, 1);
        return $"placed '{blockName}' at ({target.X}, {target.Y}, {target.Z})";
    }
}
