using Engine;
using SurvivalcraftGenius.Npc.Nav;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// Engine-free tests of the 3D A* planner over a fake world. These cover the
/// exact terrain shapes the old greedy tunneler could not handle: L-shaped
/// detours, gaps needing bridges, lava moats, fall-height limits, and
/// non-collidable "floors".
/// </summary>
public sealed class NavAStarTests
{
    private const int GroundTop = 63;   // highest solid cell; feet walk at 64
    private const int FeetY = 64;

    private sealed class FakeWorld : INavWorld
    {
        public readonly Dictionary<Point3, NavCell> Cells = [];

        public NavCell At(int x, int y, int z)
        {
            if (y < 1)
            {
                return NavCell.Bedrock;
            }

            if (Cells.TryGetValue(new Point3(x, y, z), out var cell))
            {
                return cell;
            }

            return y <= GroundTop ? Solid() : NavCell.Air;
        }

        public void Set(int x, int y, int z, NavCell cell) => Cells[new Point3(x, y, z)] = cell;

        public void Fill(int x0, int x1, int y0, int y1, int z0, int z1, NavCell cell)
        {
            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    for (var z = z0; z <= z1; z++)
                    {
                        Set(x, y, z, cell);
                    }
                }
            }
        }

        public static NavCell Solid(float digSeconds = 1.5f) =>
            new(NavKind.Solid, digSeconds, standable: true);

        public static NavCell Undiggable() =>
            new(NavKind.Solid, float.PositiveInfinity, standable: true);

        public static NavCell Water() =>
            new(NavKind.Water, float.PositiveInfinity, standable: false);

        public static NavCell Lava() =>
            new(NavKind.Lava, float.PositiveInfinity, standable: false);

        public static NavCell Door() =>
            new(NavKind.Door, 2f, standable: false);
    }

    private static NavCapabilities Caps(
        bool dig = false, int blocks = 0, int maxNodes = 50_000) => new()
    {
        AllowDig = dig,
        PlaceableBlocks = blocks,
        MaxNodes = maxNodes,
        TimeBudgetSeconds = 5.0,
    };

    private static Vector3 Goal(int x, int y, int z) => new(x + 0.5f, y + 0.5f, z + 0.5f);

    private static NavPlan? Plan(FakeWorld world, Point3 start, Vector3 goal, NavCapabilities caps) =>
        NavAStar.Plan(world, start, goal, arriveDistance: 1.2f, caps);

    [Fact]
    public void WalksStraightOverFlatGround()
    {
        var world = new FakeWorld();
        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(8, FeetY, 0), Caps());

        Assert.NotNull(plan);
        Assert.True(plan.ReachesGoal);
        Assert.All(plan.Steps, s => Assert.Equal(FeetY, s.Feet.Y));
        Assert.All(plan.Steps, s => Assert.Empty(s.Dig));
    }

    [Fact]
    public void DetoursAroundLShapedWall()
    {
        // The old greedy single-axis tunneler dies here: the direct line is
        // walled off, and the opening requires first moving AWAY from the goal.
        var world = new FakeWorld();
        world.Fill(4, 4, FeetY, FeetY + 3, -8, 5, FakeWorld.Undiggable());

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(8, FeetY, 0), Caps());

        Assert.NotNull(plan);
        Assert.True(plan.ReachesGoal);
        Assert.Contains(plan.Steps, s => s.Feet.Z >= 6);
    }

    [Fact]
    public void DigsThroughWallWhenAllowedAndNoWayAround()
    {
        var world = new FakeWorld();
        world.Fill(4, 4, FeetY, 90, -40, 40, FakeWorld.Solid());

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(8, FeetY, 0), Caps(dig: true));

        Assert.NotNull(plan);
        Assert.True(plan.ReachesGoal);
        Assert.Contains(plan.Steps, s => s.Dig.Length > 0);
    }

    [Fact]
    public void ReportsNoRouteWhenWalledInWithoutDigging()
    {
        // Sealed open-top room, no digging, no spare blocks to pillar out.
        var world = new FakeWorld();
        world.Fill(-4, 4, FeetY, 90, -4, -4, FakeWorld.Undiggable());
        world.Fill(-4, 4, FeetY, 90, 4, 4, FakeWorld.Undiggable());
        world.Fill(-4, -4, FeetY, 90, -4, 4, FakeWorld.Undiggable());
        world.Fill(4, 4, FeetY, 90, -4, 4, FakeWorld.Undiggable());

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(8, FeetY, 0),
            Caps(maxNodes: 20_000));

        Assert.True(plan is null || !plan.ReachesGoal);
    }

    [Fact]
    public void NeverStandsOverLava()
    {
        // Two-wide lava moat across the direct line; blocks available, but
        // bridging over lava is forbidden — so is swimming through it.
        var world = new FakeWorld();
        world.Fill(4, 5, 60, FeetY, -30, 30, FakeWorld.Lava());

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(9, FeetY, 0),
            Caps(blocks: 20, maxNodes: 30_000));

        // Bridging high above the lava is legitimate; the invariants are:
        // never a step inside lava, never a support sitting directly on lava.
        Assert.NotNull(plan);
        Assert.All(plan.Steps, s =>
        {
            Assert.NotEqual(NavKind.Lava, world.At(s.Feet.X, s.Feet.Y, s.Feet.Z).Kind);
            Assert.NotEqual(NavKind.Lava, world.At(s.Feet.X, s.Feet.Y - 1, s.Feet.Z).Kind);
        });
    }

    [Fact]
    public void BridgesBottomlessGapWithSpareBlocks()
    {
        var world = new FakeWorld();
        world.Fill(4, 5, 20, GroundTop, -30, 30, new NavCell(NavKind.Air, 0f, standable: false));

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(9, FeetY, 0), Caps(blocks: 10));

        Assert.NotNull(plan);
        Assert.True(plan.ReachesGoal);
        Assert.Contains(plan.Steps, s => s.Place.HasValue);
    }

    [Fact]
    public void RefusesBigDropOntoSolidButTakesWaterLanding()
    {
        // A 6-block cliff: too high to jump down (MaxFallHeight = 3)...
        var world = new FakeWorld();
        world.Fill(5, 30, FeetY - 6, GroundTop, -30, 30, new NavCell(NavKind.Air, 0f, standable: false));

        var dryPlan = Plan(world, new Point3(0, FeetY, 0), Goal(9, FeetY - 6, 0),
            Caps(maxNodes: 20_000));
        Assert.True(dryPlan is null || !dryPlan.ReachesGoal);

        // ...but fine once the landing is a pool.
        world.Fill(5, 30, FeetY - 7, FeetY - 6, -30, 30, FakeWorld.Water());
        var wetPlan = Plan(world, new Point3(0, FeetY, 0), Goal(9, FeetY - 6, 0), Caps());
        Assert.NotNull(wetPlan);
        Assert.True(wetPlan.ReachesGoal);
    }

    [Fact]
    public void NonCollidableDecorationIsNotAFloor()
    {
        // A hole hidden under "tall grass" (non-collidable): the support cell
        // is Air-kind, so the planner must route around, never stand on it.
        // (The old navigator's contents!=0 check treated grass as ground.)
        var world = new FakeWorld();
        world.Fill(4, 4, 30, GroundTop, -2, 2, new NavCell(NavKind.Air, 0f, standable: false));
        world.Fill(4, 4, FeetY, FeetY, -2, 2, new NavCell(NavKind.Air, 0f, standable: false));

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(8, FeetY, 0), Caps());

        Assert.NotNull(plan);
        Assert.True(plan.ReachesGoal);
        Assert.All(plan.Steps, s => Assert.False(
            s.Feet.X == 4 && s.Feet.Z is >= -2 and <= 2,
            $"step at {s.Feet} stands on a decoration over a hole"));
    }

    [Fact]
    public void PrefersOpeningDoorOverDiggingWall()
    {
        var world = new FakeWorld();
        world.Fill(4, 4, FeetY, FeetY + 2, -10, 10, FakeWorld.Solid());
        world.Set(4, FeetY, 0, FakeWorld.Door());
        world.Set(4, FeetY + 1, 0, new NavCell(NavKind.Air, 0f, standable: false));

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(8, FeetY, 0), Caps(dig: true));

        Assert.NotNull(plan);
        Assert.True(plan.ReachesGoal);
        var doorCell = new Point3(4, FeetY, 0);
        Assert.Contains(plan.Steps, s => s.Dig.Contains(doorCell));
        Assert.DoesNotContain(plan.Steps, s => s.Dig.Any(d => d != doorCell
            && world.At(d.X, d.Y, d.Z).Kind == NavKind.Solid));
    }

    [Fact]
    public void ReturnsPartialProgressWhenGoalIsTooFarForBudget()
    {
        var world = new FakeWorld();
        var start = new Point3(0, FeetY, 0);
        var plan = Plan(world, start, Goal(400, FeetY, 0),
            Caps(maxNodes: 300));

        Assert.NotNull(plan);
        Assert.False(plan.ReachesGoal);
        Assert.NotEmpty(plan.Steps);
        var end = plan.Steps[^1].Feet;
        Assert.True(end.X > start.X + 4, $"partial path ended at {end}, no progress toward goal");
    }

    [Fact]
    public void SwimsAcrossAPond()
    {
        var world = new FakeWorld();
        // Pond: water surface at feet level, 4 blocks deep, spanning the line.
        world.Fill(3, 6, FeetY - 4, FeetY, -20, 20, FakeWorld.Water());

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(9, FeetY, 0), Caps());

        Assert.NotNull(plan);
        Assert.True(plan.ReachesGoal);
    }

    [Fact]
    public void ClimbsAStaircaseOfSingleJumps()
    {
        var world = new FakeWorld();
        for (var i = 0; i < 4; i++)
        {
            world.Fill(4 + i, 8, FeetY + i, FeetY + i, -20, 20, FakeWorld.Solid());
        }

        var plan = Plan(world, new Point3(0, FeetY, 0), Goal(8, FeetY + 4, 0), Caps());

        Assert.NotNull(plan);
        Assert.True(plan.ReachesGoal);
        Assert.Contains(plan.Steps, s => s.Kind == StepKind.Jump);
    }
}
