using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The difference between a shelter and a pile of blocks is a handful of
/// invariants. Playtest 10 shipped the pile ("盖的还没第一次好，而且还是浮空的"),
/// so they are asserted here rather than hoped for.
/// </summary>
public class ShelterPlanTests
{
    private const int OriginX = 10;
    private const int GroundY = 64;
    private const int OriginZ = 20;

    private static List<GeniusShelterPlan.Cell> Plan(int w = 5, int l = 5, int h = 3) =>
        GeniusShelterPlan.Cells(OriginX, GroundY, OriginZ, w, l, h).ToList();

    [Fact]
    public void FloorCoversEveryColumnSoTheBuildingCannotFloat()
    {
        var floor = Plan()
            .Where(cell => cell.Y == GroundY)
            .ToList();

        Assert.All(floor, cell => Assert.True(cell.Solid));
        Assert.Equal(25, floor.Count);
        Assert.Equal(25, floor.Select(cell => (cell.X, cell.Z)).Distinct().Count());
    }

    [Fact]
    public void RoofCoversEveryColumn()
    {
        var roof = Plan().Where(cell => cell.Y == GroundY + 4).ToList();

        Assert.Equal(25, roof.Count);
        Assert.All(roof, cell => Assert.True(cell.Solid));
    }

    [Fact]
    public void WallsCloseThePerimeterAtEveryLevelExceptTheDoorway()
    {
        var plan = Plan();
        var openings = 0;
        for (var y = GroundY + 1; y <= GroundY + 3; y++)
        {
            foreach (var cell in plan.Where(c => c.Y == y))
            {
                var onEdge = cell.X == OriginX || cell.X == OriginX + 4
                    || cell.Z == OriginZ || cell.Z == OriginZ + 4;
                if (onEdge && !cell.Solid)
                {
                    openings++;
                }
            }
        }

        // Exactly the doorway: one column, two blocks tall.
        Assert.Equal(2, openings);
    }

    [Fact]
    public void DoorwayIsTwoTallAndOnTheOutsideWall()
    {
        var door = Plan()
            .Where(cell => !cell.Solid
                && cell.Z == OriginZ
                && cell.Y > GroundY)
            .ToList();

        Assert.Equal(2, door.Count);
        Assert.Single(door.Select(cell => cell.X).Distinct());
        Assert.Equal([GroundY + 1, GroundY + 2], door.Select(cell => cell.Y).Order());
    }

    [Fact]
    public void InteriorIsHollowAtEveryWallLevel()
    {
        var plan = Plan();
        for (var y = GroundY + 1; y <= GroundY + 3; y++)
        {
            var interior = plan
                .Where(cell => cell.Y == y
                    && cell.X > OriginX && cell.X < OriginX + 4
                    && cell.Z > OriginZ && cell.Z < OriginZ + 4)
                .ToList();

            Assert.Equal(9, interior.Count);
            Assert.All(interior, cell => Assert.False(cell.Solid));
        }
    }

    [Fact]
    public void NoCellIsPlannedBothSolidAndEmpty()
    {
        var plan = Plan();
        var conflicting = plan
            .GroupBy(cell => (cell.X, cell.Y, cell.Z))
            .Where(group => group.Select(cell => cell.Solid).Distinct().Count() > 1);

        Assert.Empty(conflicting);
    }

    [Fact]
    public void FloorIsLaidBeforeAnythingRestsOnIt()
    {
        var plan = Plan();
        var lastFloor = plan.FindLastIndex(cell => cell.Y == GroundY);
        var firstAbove = plan.FindIndex(cell => cell.Y > GroundY);

        Assert.True(lastFloor < firstAbove);
    }

    [Theory]
    [InlineData(3, 3, 2)]
    [InlineData(9, 9, 4)]
    [InlineData(3, 9, 3)]
    public void HoldsForEverySupportedSize(int w, int l, int h)
    {
        var plan = Plan(w, l, h);

        Assert.Equal(w * l, plan.Count(cell => cell.Y == GroundY && cell.Solid));
        Assert.Equal(w * l, plan.Count(cell => cell.Y == GroundY + h + 1 && cell.Solid));
        Assert.Equal(2, plan.Count(cell => !cell.Solid && cell.Z == OriginZ && cell.Y > GroundY));
        Assert.Equal(
            (w - 2) * (l - 2) * h,
            plan.Count(cell => !cell.Solid
                && cell.X > OriginX && cell.X < OriginX + w - 1
                && cell.Z > OriginZ && cell.Z < OriginZ + l - 1));
    }
}
