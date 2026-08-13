using Engine;
using Game;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>
/// Everything a tool handler is allowed to touch. Handlers are static methods
/// grouped by domain (perception / movement / work / farm / build / item /
/// combat / knowledge); this record is the only channel between them and the
/// player component, so adding a tool never means editing a shared switch.
/// </summary>
public sealed class GeniusToolContext
{
    private readonly ComponentGeniusBrain? _brain;

    public GeniusToolContext(
        GeniusPlayerComponent player,
        ComponentPlayer componentPlayer,
        SubsystemTerrain subsystemTerrain,
        SubsystemBodies subsystemBodies,
        GeniusKnowledgeStore knowledge,
        ComponentGeniusBrain? brain)
    {
        Player = player;
        ComponentPlayer = componentPlayer;
        SubsystemTerrain = subsystemTerrain;
        SubsystemBodies = subsystemBodies;
        Knowledge = knowledge;
        _brain = brain;
    }

    public GeniusPlayerComponent Player { get; }

    public ComponentPlayer ComponentPlayer { get; }

    public SubsystemTerrain SubsystemTerrain { get; }

    public SubsystemBodies SubsystemBodies { get; }

    public GeniusKnowledgeStore Knowledge { get; }

    /// <summary>
    /// The summoned companion. The router refuses every tool outside
    /// <see cref="GeniusToolTable.WorksWithoutBrain"/> before a handler runs, so
    /// handlers may take this for granted.
    /// </summary>
    public ComponentGeniusBrain Brain => _brain
        ?? throw new InvalidOperationException(
            "a tool handler asked for the companion but ran without one — " +
            "the router should have refused the call");

    /// <summary>Null-tolerant view, for the handful of tools that work unsummoned.</summary>
    public ComponentGeniusBrain? BrainOrNull => _brain;

    public static Point3 ReadPoint(JObject arguments)
    {
        var x = (int?)arguments["x"] ?? throw new InvalidOperationException("missing x");
        var y = (int?)arguments["y"] ?? throw new InvalidOperationException("missing y");
        var z = (int?)arguments["z"] ?? throw new InvalidOperationException("missing z");
        return new Point3(x, y, z);
    }

    /// <summary>True when all three coordinates are present (they are optional on some tools).</summary>
    public static bool HasPoint(JObject arguments) =>
        arguments["x"] is not null && arguments["y"] is not null && arguments["z"] is not null;

    /// <summary>
    /// Dispatches a long job, replacing whatever was running.
    ///
    /// <para>The one case that is refused is a SECOND dispatch inside the SAME
    /// agent turn: that means the model fired again without waiting for the
    /// first result, which is how three houses in a row got restarted from
    /// zero. A dispatch from a later turn is the player changing their mind,
    /// and that always wins — v0.11.7 refused those too, and a companion that
    /// ignores you for five minutes because it is mining reads as broken
    /// rather than busy.</para>
    /// </summary>
    public Task<string> Dispatch(GeniusOrder order)
    {
        var turn = Player.TurnId;

        // Refuse ONLY re-sending the SAME job in the same reply. That is the
        // case that restarts work from zero.
        //
        // The first version refused any second dispatch in a turn, which
        // deadlocked a whole session: an agent-side timeout left the order
        // alive in the body, so every later tool — a different tool, a
        // different job — was refused for the rest of the turn, and the
        // companion could only tell the player to re-summon it. A different
        // job replacing the running one is the normal, safe path; that is what
        // "one body, one job" has always meant.
        var running = Brain.CurrentOrder;
        if (IsDuplicateDispatch(order.Signature, running?.Signature, running?.DispatchTurn ?? 0, turn))
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.InvalidArgument,
                $"I am already doing exactly this (task #{running!.TaskId}), dispatched moments " +
                "ago in this same reply. Sending it again would restart it from zero — wait for " +
                "its result. task_status shows progress, task_stop aborts it"));
        }

        Brain.StartOrder(order, turn);
        return order.Completion;
    }

    /// <summary>
    /// Should this dispatch be refused? Extracted so the rule that deadlocked
    /// playtest 16 has a test: the guard must catch the model re-sending the
    /// SAME job in one reply, and must never block a DIFFERENT job, because
    /// blocking those left the companion unable to do anything at all for the
    /// rest of the turn.
    /// </summary>
    public static bool IsDuplicateDispatch(
        string? newSignature, string? runningSignature, int runningTurn, int currentTurn) =>
        newSignature is not null
        && runningSignature is not null
        && currentTurn != 0
        && runningTurn == currentTurn
        && string.Equals(newSignature, runningSignature, StringComparison.Ordinal);
}

/// <summary>One tool's implementation. Runs on the main thread.</summary>
public delegate Task<string> GeniusToolFn(GeniusToolContext context, JObject arguments);
