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
        if (Brain.CurrentOrder is { } running && running.DispatchTurn == turn && turn != 0)
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.InvalidArgument,
                $"I am already on task #{running.TaskId}, dispatched moments ago in this same " +
                "reply. Wait for its result instead of starting another — a second dispatch " +
                "would restart the work from zero. Use task_status to check, task_stop to abort"));
        }

        Brain.StartOrder(order, turn);
        return order.Completion;
    }
}

/// <summary>One tool's implementation. Runs on the main thread.</summary>
public delegate Task<string> GeniusToolFn(GeniusToolContext context, JObject arguments);
