using Engine;

namespace SurvivalcraftGenius.Npc.Nav;

/// <summary>How the navigator may treat a single terrain cell.</summary>
public enum NavKind : byte
{
    /// <summary>Empty, or walk-through decoration (grass, flowers, torches, thin snow).</summary>
    Air,

    /// <summary>Collidable; standable on top unless flagged otherwise (fences).</summary>
    Solid,

    /// <summary>Swimmable liquid.</summary>
    Water,

    /// <summary>Never enter, never stand above.</summary>
    Lava,

    /// <summary>Cactus, fire, spikes — never enter, never stand on.</summary>
    Hazard,

    /// <summary>Closed door/gate/trapdoor that can be opened in place.</summary>
    Door,
}

public readonly struct NavCell(NavKind kind, float digSeconds, bool standable)
{
    public NavKind Kind { get; } = kind;

    /// <summary>Seconds to dig with the best tool; +inf when digging is impossible or forbidden.</summary>
    public float DigSeconds { get; } = digSeconds;

    /// <summary>True when a body can stand on top of this cell.</summary>
    public bool Standable { get; } = standable;

    /// <summary>A body part can occupy this cell without digging.</summary>
    public bool Enterable => Kind is NavKind.Air or NavKind.Water or NavKind.Door;

    public static NavCell Air { get; } = new(NavKind.Air, 0f, standable: false);

    public static NavCell Bedrock { get; } = new(NavKind.Solid, float.PositiveInfinity, standable: true);
}

/// <summary>
/// Read view of the world for path planning. Implementations must be safe to
/// call from the planner thread (the vanilla pathfinder reads terrain from its
/// own thread the same way; torn reads self-heal on execution re-checks).
/// </summary>
public interface INavWorld
{
    NavCell At(int x, int y, int z);
}

/// <summary>Snapshot of what the NPC can do, taken on the main thread before planning.</summary>
public sealed class NavCapabilities
{
    public bool AllowDig;
    public bool AllowPlace = true;

    /// <summary>Throwaway blocks available for bridging/pillaring.</summary>
    public int PlaceableBlocks;

    /// <summary>Digs slower than this are treated as impossible (tool gate).</summary>
    public float MaxDigSeconds = 8f;

    /// <summary>Falls onto solid ground beyond this many blocks are rejected.</summary>
    public int MaxFallHeight = 3;

    /// <summary>Falls that end in water can be much deeper.</summary>
    public int MaxFallIntoWater = 20;

    public int MaxNodes = 60_000;
    public double TimeBudgetSeconds = 1.0;

    /// <summary>Cells to steer away from (e.g. water we almost drowned in).</summary>
    public HashSet<Point3>? PenalizedCells;
}

/// <summary>
/// Action costs in seconds of body time, mirroring Numen's tick-derived cost
/// table but calibrated to Survivalcraft creature speeds. Only the ratios
/// matter to the search.
/// </summary>
public static class NavCosts
{
    public const float Walk = 0.25f;
    public const float Diagonal = 0.354f;

    /// <summary>Feet in water, head in air.</summary>
    public const float Swim = 0.55f;

    /// <summary>Head underwater: legal but strongly discouraged (drowning risk).</summary>
    public const float SwimSubmerged = 1.4f;

    public const float Jump = 0.42f;

    /// <summary>Per dug block, on top of the real dig time (discourage gratuitous digging).</summary>
    public const float DigExtra = 0.4f;

    /// <summary>Placing a block spends inventory — price it well above walking around.</summary>
    public const float Place = 1.6f;

    public const float OpenDoor = 0.8f;

    /// <summary>Added per cell listed in <see cref="NavCapabilities.PenalizedCells"/>.</summary>
    public const float Penalized = 6f;

    /// <summary>Finite "impossible" so accidental sums never overflow (Numen's COST_INF trick).</summary>
    public const float Inf = 1e6f;

    /// <summary>Free-fall time for a drop of the given height (g ≈ 10 m/s²).</summary>
    public static float Fall(int blocks) => 0.1f + 0.447f * MathF.Sqrt(blocks);
}

public enum StepKind : byte
{
    Walk,
    Jump,
    Fall,
    Pillar,
    DigDown,
}

/// <summary>One cell-to-cell move of the plan, with its terrain edits.</summary>
public sealed class NavStep
{
    public Point3 Feet;
    public StepKind Kind;

    /// <summary>Cells to clear before moving; door-kind cells are opened, not dug.</summary>
    public Point3[] Dig = [];

    /// <summary>Support block to place before moving.</summary>
    public Point3? Place;
}

public sealed class NavPlan
{
    public required List<NavStep> Steps { get; init; }

    /// <summary>False for a best-effort partial path toward the goal.</summary>
    public required bool ReachesGoal { get; init; }

    /// <summary>A route existed but was cut off by the dig-time tool gate.</summary>
    public bool ToolLimited { get; init; }

    public int PlacesNeeded { get; init; }
}
