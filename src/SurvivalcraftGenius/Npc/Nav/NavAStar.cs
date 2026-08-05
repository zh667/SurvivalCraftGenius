using System.Diagnostics;
using Engine;

namespace SurvivalcraftGenius.Npc.Nav;

/// <summary>
/// 3D A* over terrain cells with dig/place/swim/fall move primitives — the
/// Numen/Baritone search shape re-implemented for Survivalcraft: cost-based
/// tunneling replaces greedy single-axis digging, and a multi-coefficient
/// best-so-far table returns a useful partial path when the full search
/// times out (so long or blocked goals still make progress).
/// </summary>
public static class NavAStar
{
    /// <summary>Progressively greedier weightings tracked for partial-path fallback.</summary>
    private static readonly float[] Coefficients = [1.5f, 2f, 2.5f, 3f, 4f, 5f, 10f];

    /// <summary>A partial path must get at least this far from the start to be worth walking.</summary>
    private const float MinPartialDistanceSq = 25f;

    private sealed class Node
    {
        public int X, Y, Z;
        public float G = NavCosts.Inf;
        public float H;
        public float F;
        public int HeapIndex = -1;
        public Node? Parent;
        public StepKind Kind;
        public Point3[] Digs = [];
        public Point3? Place;
    }

    public static NavPlan? Plan(
        INavWorld world,
        Point3 start,
        Vector3 goal,
        float arriveDistance,
        NavCapabilities caps,
        CancellationToken cancellation = default)
    {
        var nodes = new Dictionary<long, Node>(4096);
        var open = new List<Node>(1024);
        var toolLimited = false;
        var arriveSq = MathF.Max(arriveDistance, 1.1f) * MathF.Max(arriveDistance, 1.1f);

        var startNode = GetNode(nodes, start.X, start.Y, start.Z, goal);
        startNode.G = 0f;
        startNode.F = startNode.H;
        HeapPush(open, startNode);

        var bestSoFar = new Node[Coefficients.Length];
        var bestHeuristic = new float[Coefficients.Length];
        for (var i = 0; i < Coefficients.Length; i++)
        {
            bestSoFar[i] = startNode;
            bestHeuristic[i] = startNode.H;
        }

        var stopwatch = Stopwatch.StartNew();
        var popped = 0;
        var scratchDigs = new List<Point3>(6);
        while (open.Count > 0 && popped < caps.MaxNodes)
        {
            if ((++popped & 127) == 0
                && (cancellation.IsCancellationRequested
                    || stopwatch.Elapsed.TotalSeconds > caps.TimeBudgetSeconds))
            {
                break;
            }

            var current = HeapPop(open);
            if (DistanceSqToGoal(current, goal) <= arriveSq)
            {
                return Assemble(current, startNode, reachesGoal: true, toolLimited);
            }

            Expand(world, caps, nodes, open, current, goal, scratchDigs, ref toolLimited);

            for (var i = 0; i < Coefficients.Length; i++)
            {
                var relaxed = current.H + current.G / Coefficients[i];
                if (bestHeuristic[i] - relaxed > 0.01f)
                {
                    bestHeuristic[i] = relaxed;
                    bestSoFar[i] = current;
                }
            }
        }

        foreach (var candidate in bestSoFar)
        {
            var dx = candidate.X - start.X;
            var dy = candidate.Y - start.Y;
            var dz = candidate.Z - start.Z;
            if (dx * dx + dy * dy + dz * dz > MinPartialDistanceSq)
            {
                return Assemble(candidate, startNode, reachesGoal: false, toolLimited);
            }
        }

        return null;
    }

    private static void Expand(
        INavWorld world,
        NavCapabilities caps,
        Dictionary<long, Node> nodes,
        List<Node> open,
        Node n,
        Vector3 goal,
        List<Point3> digs,
        ref bool toolLimited)
    {
        Span<(int Dx, int Dz)> cardinals = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        var feetHere = world.At(n.X, n.Y, n.Z);
        var inWater = feetHere.Kind == NavKind.Water;

        foreach (var (dx, dz) in cardinals)
        {
            var x = n.X + dx;
            var z = n.Z + dz;

            // --- Traverse: same level ---
            digs.Clear();
            var cost = EnterColumnCost(world, caps, x, n.Y, z, digs, ref toolLimited);
            if (cost < NavCosts.Inf)
            {
                var feet = world.At(x, n.Y, z);
                var head = world.At(x, n.Y + 1, z);
                var support = world.At(x, n.Y - 1, z);
                var moveBase = MoveBase(feet, head, NavCosts.Walk);
                var (supportCost, place) = SupportCost(caps, support, feet, x, n.Y - 1, z);
                var total = moveBase + cost + supportCost + PenaltyAt(caps, x, n.Y, z);
                if (total < NavCosts.Inf)
                {
                    Relax(nodes, open, n, x, n.Y, z, total, goal, StepKind.Walk, digs, place);
                }
            }

            // --- Ascend: up one, forward one ---
            digs.Clear();
            var headroom = world.At(n.X, n.Y + 2, n.Z);
            var headroomCost = CellClearCost(caps, headroom, new Point3(n.X, n.Y + 2, n.Z), digs, ref toolLimited);
            if (headroomCost < NavCosts.Inf)
            {
                var enterCost = EnterColumnCost(world, caps, x, n.Y + 1, z, digs, ref toolLimited);
                if (enterCost < NavCosts.Inf)
                {
                    var feet = world.At(x, n.Y + 1, z);
                    var head = world.At(x, n.Y + 2, z);
                    var support = world.At(x, n.Y, z);
                    var moveBase = MoveBase(feet, head, NavCosts.Walk) + NavCosts.Jump;
                    var (supportCost, place) = SupportCost(caps, support, feet, x, n.Y, z);
                    var total = moveBase + headroomCost + enterCost + supportCost + PenaltyAt(caps, x, n.Y + 1, z);
                    if (total < NavCosts.Inf)
                    {
                        Relax(nodes, open, n, x, n.Y + 1, z, total, goal, StepKind.Jump, digs, place);
                    }
                }
            }

            // --- Descend / fall: forward, then drop to the first landing ---
            digs.Clear();
            var entryCost = EnterColumnCost(world, caps, x, n.Y, z, digs, ref toolLimited);
            if (entryCost < NavCosts.Inf && !world.At(x, n.Y - 1, z).Standable)
            {
                var landing = ScanFall(world, caps, x, n.Y, z);
                if (landing is { } land)
                {
                    var drop = n.Y - land.FeetY;
                    var feet = world.At(x, land.FeetY, z);
                    var head = world.At(x, land.FeetY + 1, z);
                    var moveBase = feet.Kind == NavKind.Water
                        ? MoveBase(feet, head, NavCosts.Walk)
                        : NavCosts.Fall(drop);
                    var total = NavCosts.Walk * 0.6f + moveBase + entryCost
                        + PenaltyAt(caps, x, land.FeetY, z);
                    Relax(nodes, open, n, x, land.FeetY, z, total, goal, StepKind.Fall, digs, place: null);
                }
            }
        }

        // --- Diagonals: flat, no dig, no place, no corner clipping ---
        Span<(int Dx, int Dz)> diagonals = [(1, 1), (1, -1), (-1, 1), (-1, -1)];
        foreach (var (dx, dz) in diagonals)
        {
            var x = n.X + dx;
            var z = n.Z + dz;
            var feet = world.At(x, n.Y, z);
            var head = world.At(x, n.Y + 1, z);
            if (!feet.Enterable || !head.Enterable)
            {
                continue;
            }

            if (!world.At(n.X + dx, n.Y, n.Z).Enterable || !world.At(n.X + dx, n.Y + 1, n.Z).Enterable
                || !world.At(n.X, n.Y, n.Z + dz).Enterable || !world.At(n.X, n.Y + 1, n.Z + dz).Enterable)
            {
                continue;
            }

            var support = world.At(x, n.Y - 1, z);
            if (!support.Standable && feet.Kind != NavKind.Water)
            {
                continue;
            }

            if (support.Kind is NavKind.Lava or NavKind.Hazard)
            {
                continue;
            }

            var total = MoveBase(feet, head, NavCosts.Diagonal) + PenaltyAt(caps, x, n.Y, z);
            Relax(nodes, open, n, x, n.Y, z, total, goal, StepKind.Walk, digs: null, place: null);
        }

        // --- Pillar up (place under own feet) or swim up ---
        digs.Clear();
        var above = world.At(n.X, n.Y + 1, n.Z);
        var aboveHead = world.At(n.X, n.Y + 2, n.Z);
        if (inWater && above.Kind == NavKind.Water)
        {
            Relax(nodes, open, n, n.X, n.Y + 1, n.Z, NavCosts.Swim, goal, StepKind.Walk, digs: null, place: null);
        }
        else if (caps.AllowPlace && caps.PlaceableBlocks > 0 && above.Enterable)
        {
            var clearCost = CellClearCost(caps, aboveHead, new Point3(n.X, n.Y + 2, n.Z), digs, ref toolLimited);
            if (clearCost < NavCosts.Inf && feetHere.Kind != NavKind.Lava)
            {
                var total = NavCosts.Jump + NavCosts.Place + clearCost;
                Relax(nodes, open, n, n.X, n.Y + 1, n.Z, total, goal, StepKind.Pillar,
                    digs, new Point3(n.X, n.Y, n.Z));
            }
        }

        // --- Swim down, or dig straight down ---
        var below = world.At(n.X, n.Y - 1, n.Z);
        if (below.Kind == NavKind.Water)
        {
            Relax(nodes, open, n, n.X, n.Y - 1, n.Z, NavCosts.SwimSubmerged, goal, StepKind.Walk,
                digs: null, place: null);
        }
        else if (caps.AllowDig && below.Kind == NavKind.Solid)
        {
            var under = world.At(n.X, n.Y - 2, n.Z);
            if (under.Standable || under.Kind == NavKind.Water)
            {
                digs.Clear();
                var digCost = CellClearCost(caps, below, new Point3(n.X, n.Y - 1, n.Z), digs, ref toolLimited);
                if (digCost < NavCosts.Inf)
                {
                    var total = digCost + NavCosts.Fall(1);
                    Relax(nodes, open, n, n.X, n.Y - 1, n.Z, total, goal, StepKind.DigDown, digs, place: null);
                }
            }
        }
    }

    private readonly record struct FallLanding(int FeetY);

    /// <summary>Finds where a fall from (x, fromY, z) ends, honoring fall-height caps and hazards.</summary>
    private static FallLanding? ScanFall(INavWorld world, NavCapabilities caps, int x, int fromY, int z)
    {
        for (var feetY = fromY - 1; feetY >= fromY - caps.MaxFallIntoWater && feetY >= 1; feetY--)
        {
            var cell = world.At(x, feetY, z);
            if (cell.Kind == NavKind.Water)
            {
                return new FallLanding(feetY);
            }

            if (cell.Kind is NavKind.Lava or NavKind.Hazard || !cell.Enterable && cell.Kind != NavKind.Air)
            {
                return null;
            }

            var support = world.At(x, feetY - 1, z);
            if (support.Kind is NavKind.Lava or NavKind.Hazard)
            {
                return null;
            }

            if (support.Standable)
            {
                return fromY - feetY <= caps.MaxFallHeight ? new FallLanding(feetY) : null;
            }
        }

        return null;
    }

    /// <summary>Cost to make feet+head at a column enterable, queueing digs/doors.</summary>
    private static float EnterColumnCost(
        INavWorld world,
        NavCapabilities caps,
        int x,
        int y,
        int z,
        List<Point3> digs,
        ref bool toolLimited)
    {
        var feetCost = CellClearCost(caps, world.At(x, y, z), new Point3(x, y, z), digs, ref toolLimited);
        if (feetCost >= NavCosts.Inf)
        {
            return NavCosts.Inf;
        }

        var headCost = CellClearCost(caps, world.At(x, y + 1, z), new Point3(x, y + 1, z), digs, ref toolLimited);
        return headCost >= NavCosts.Inf ? NavCosts.Inf : feetCost + headCost;
    }

    private static float CellClearCost(
        NavCapabilities caps,
        in NavCell cell,
        Point3 position,
        List<Point3> digs,
        ref bool toolLimited)
    {
        switch (cell.Kind)
        {
            case NavKind.Air:
            case NavKind.Water:
                return 0f;
            case NavKind.Door:
                digs.Add(position);
                return NavCosts.OpenDoor;
            case NavKind.Solid when caps.AllowDig:
                if (float.IsPositiveInfinity(cell.DigSeconds))
                {
                    return NavCosts.Inf;
                }

                if (cell.DigSeconds > caps.MaxDigSeconds)
                {
                    toolLimited = true;
                    return NavCosts.Inf;
                }

                digs.Add(position);
                return cell.DigSeconds + NavCosts.DigExtra;
            default:
                return NavCosts.Inf;
        }
    }

    private static float MoveBase(in NavCell feet, in NavCell head, float dryCost)
    {
        if (feet.Kind == NavKind.Water)
        {
            var swim = head.Kind == NavKind.Water ? NavCosts.SwimSubmerged : NavCosts.Swim;
            return swim * (dryCost / NavCosts.Walk);
        }

        return dryCost;
    }

    private static (float Cost, Point3? Place) SupportCost(
        NavCapabilities caps,
        in NavCell support,
        in NavCell feet,
        int x,
        int y,
        int z)
    {
        if (feet.Kind == NavKind.Water)
        {
            return (0f, null);
        }

        if (support.Kind is NavKind.Lava or NavKind.Hazard)
        {
            return (NavCosts.Inf, null);
        }

        if (support.Standable)
        {
            return (0f, null);
        }

        if (caps.AllowPlace && caps.PlaceableBlocks > 0
            && support.Kind is NavKind.Air or NavKind.Water)
        {
            return (NavCosts.Place, new Point3(x, y, z));
        }

        return (NavCosts.Inf, null);
    }

    private static float PenaltyAt(NavCapabilities caps, int x, int y, int z) =>
        caps.PenalizedCells?.Contains(new Point3(x, y, z)) == true ? NavCosts.Penalized : 0f;

    private static void Relax(
        Dictionary<long, Node> nodes,
        List<Node> open,
        Node from,
        int x,
        int y,
        int z,
        float cost,
        Vector3 goal,
        StepKind kind,
        List<Point3>? digs,
        Point3? place)
    {
        if (y < 1 || y > 254 || cost >= NavCosts.Inf)
        {
            return;
        }

        var node = GetNode(nodes, x, y, z, goal);
        var tentative = from.G + cost;
        if (node.G - tentative <= 0.01f)
        {
            return;
        }

        node.G = tentative;
        node.F = tentative + node.H;
        node.Parent = from;
        node.Kind = kind;
        node.Digs = digs is { Count: > 0 } ? [.. digs] : [];
        node.Place = place;
        if (node.HeapIndex >= 0)
        {
            HeapUpdate(open, node);
        }
        else
        {
            HeapPush(open, node);
        }
    }

    private static NavPlan Assemble(Node end, Node start, bool reachesGoal, bool toolLimited)
    {
        var steps = new List<NavStep>();
        var places = 0;
        for (var node = end; node != start && node.Parent is not null; node = node.Parent)
        {
            if (node.Place.HasValue)
            {
                places++;
            }

            steps.Add(new NavStep
            {
                Feet = new Point3(node.X, node.Y, node.Z),
                Kind = node.Kind,
                Dig = node.Digs,
                Place = node.Place,
            });
        }

        steps.Reverse();
        return new NavPlan
        {
            Steps = steps,
            ReachesGoal = reachesGoal,
            ToolLimited = toolLimited,
            PlacesNeeded = places,
        };
    }

    private static float DistanceSqToGoal(Node n, Vector3 goal)
    {
        var dx = n.X + 0.5f - goal.X;
        var dy = n.Y + 0.5f - goal.Y;
        var dz = n.Z + 0.5f - goal.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static Node GetNode(Dictionary<long, Node> nodes, int x, int y, int z, Vector3 goal)
    {
        var key = ((long)(x & 0x3FFFFFF) << 34) | ((long)(z & 0x3FFFFFF) << 8) | (uint)(y & 0xFF);
        if (!nodes.TryGetValue(key, out var node))
        {
            var adx = MathF.Abs(x + 0.5f - goal.X);
            var adz = MathF.Abs(z + 0.5f - goal.Z);
            var ady = MathF.Abs(y + 0.5f - goal.Y);
            var dmax = MathF.Max(adx, adz);
            var dmin = MathF.Min(adx, adz);
            node = new Node
            {
                X = x,
                Y = y,
                Z = z,
                H = NavCosts.Walk * (dmax + 0.414f * dmin) + 0.2f * ady,
            };
            nodes.Add(key, node);
        }

        return node;
    }

    private static void HeapPush(List<Node> heap, Node node)
    {
        node.HeapIndex = heap.Count;
        heap.Add(node);
        SiftUp(heap, node.HeapIndex);
    }

    private static Node HeapPop(List<Node> heap)
    {
        var top = heap[0];
        var last = heap[^1];
        heap.RemoveAt(heap.Count - 1);
        top.HeapIndex = -1;
        if (heap.Count > 0)
        {
            heap[0] = last;
            last.HeapIndex = 0;
            SiftDown(heap, 0);
        }

        return top;
    }

    private static void HeapUpdate(List<Node> heap, Node node) => SiftUp(heap, node.HeapIndex);

    private static void SiftUp(List<Node> heap, int i)
    {
        var node = heap[i];
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (heap[parent].F <= node.F)
            {
                break;
            }

            heap[i] = heap[parent];
            heap[i].HeapIndex = i;
            i = parent;
        }

        heap[i] = node;
        node.HeapIndex = i;
    }

    private static void SiftDown(List<Node> heap, int i)
    {
        var node = heap[i];
        while (true)
        {
            var child = 2 * i + 1;
            if (child >= heap.Count)
            {
                break;
            }

            if (child + 1 < heap.Count && heap[child + 1].F < heap[child].F)
            {
                child++;
            }

            if (node.F <= heap[child].F)
            {
                break;
            }

            heap[i] = heap[child];
            heap[i].HeapIndex = i;
            i = child;
        }

        heap[i] = node;
        node.HeapIndex = i;
    }
}
