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
        ComponentGeniusBrain? brain,
        string toolName = "")
    {
        Player = player;
        ComponentPlayer = componentPlayer;
        SubsystemTerrain = subsystemTerrain;
        SubsystemBodies = subsystemBodies;
        Knowledge = knowledge;
        ToolName = toolName;
        _brain = brain;
    }

    public GeniusPlayerComponent Player { get; }

    public ComponentPlayer ComponentPlayer { get; }

    public SubsystemTerrain SubsystemTerrain { get; }

    public SubsystemBodies SubsystemBodies { get; }

    public GeniusKnowledgeStore Knowledge { get; }

    /// <summary>The catalog name of the tool currently running this handler.</summary>
    public string ToolName { get; }

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
    /// Dispatches a body job: accept immediately, body runs in the background,
    /// completion arrives as <c>task_finished</c> (Numen <c>setTask</c>).
    ///
    /// <para>A later turn's new job replaces the running one — the player
    /// changing their mind always wins. The one refusal is a SECOND dispatch
    /// inside the SAME agent turn: the model fired twice without waiting for
    /// the first receipt. Instant-complete orders (finished in OnStart) still
    /// return their result as the tool result, so a missing material does not
    /// bounce through an event.</para>
    /// </summary>
    public Task<string> Dispatch(GeniusOrder order)
    {
        var turn = Player.TurnId;
        var running = Brain.CurrentOrder;
        if (IsSameTurnDispatch(running?.DispatchTurn ?? 0, turn))
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.InvalidArgument,
                $"I just accepted task #{running!.TaskId} in this same reply — one body, one job. " +
                "Wait for its task_finished (or task_stop). Next turn, a different job replaces it"));
        }

        Brain.StartOrder(order, turn);
        if (order.Completion.IsCompleted)
        {
            return order.Completion;
        }

        var toolName = string.IsNullOrEmpty(ToolName) ? order.GetType().Name : ToolName;
        Player.WatchBackgroundOrder(toolName, order);
        return Task.FromResult(GeniusTaskProtocol.Accept(order.TaskId, toolName));
    }

    /// <summary>
    /// True when the slot already holds a job accepted in this same turn.
    /// Numen: refuse the second dispatch in the batch; a later turn replaces.
    /// </summary>
    public static bool IsSameTurnDispatch(int runningTurn, int currentTurn) =>
        currentTurn != 0 && runningTurn == currentTurn;
}

/// <summary>One tool's implementation. Runs on the main thread.</summary>
public delegate Task<string> GeniusToolFn(GeniusToolContext context, JObject arguments);
