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

/// <summary>
/// The builder must never be inside the box it is closing. Playtest 14, three
/// times: "我看到的就是你一直在你盖的房子里转圈圈".
/// </summary>
public class ShelterBuildOrderTests
{
    private const int GroundY = 100;

    [Fact]
    public void FloorComesBeforeAnyWall()
    {
        // The floor is walked on, so it has to be laid while there is still a
        // way out; every wall afterwards is placed from outside.
        var cells = GeniusShelterPlan.Cells(0, GroundY, 0, 5, 5, 3).ToList();
        var lastFloor = cells.FindLastIndex(c => c.Solid && c.Y == GroundY);
        var firstWall = cells.FindIndex(c => c.Solid && c.Y > GroundY);

        Assert.True(lastFloor >= 0 && firstWall > lastFloor);
    }

    [Fact]
    public void RoofIsTheLastThingPlaced()
    {
        var cells = GeniusShelterPlan.Cells(0, GroundY, 0, 5, 5, 3).ToList();
        var roofY = cells.Where(c => c.Solid).Max(c => c.Y);
        var firstRoof = cells.FindIndex(c => c.Solid && c.Y == roofY);
        var lastNonRoof = cells.FindLastIndex(c => c.Solid && c.Y < roofY);

        Assert.True(firstRoof > lastNonRoof, "the roof must go on after the walls");
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(7, 6)]
    [InlineData(9, 9)]
    public void EveryWallCellHasAStandingSpotOutsideTheFootprint(int width, int length)
    {
        // Mirrors BuildShelterOrder.StandSpotFor: push out along the dominant
        // axis. If any wall cell's spot fell inside, the builder would seal
        // itself in again.
        var centreX = (width - 1) / 2f;
        var centreZ = (length - 1) / 2f;
        foreach (var cell in GeniusShelterPlan.Cells(0, GroundY, 0, width, length, 3)
                     .Where(c => c.Solid && c.Y > GroundY))
        {
            var offsetX = cell.X - centreX;
            var offsetZ = cell.Z - centreZ;
            var (spotX, spotZ) = Math.Abs(offsetX) >= Math.Abs(offsetZ)
                ? (offsetX >= 0 ? width : -1, cell.Z)
                : (cell.X, offsetZ >= 0 ? length : -1);

            var inside = spotX >= 0 && spotX < width && spotZ >= 0 && spotZ < length;
            Assert.False(inside, $"cell ({cell.X},{cell.Y},{cell.Z}) would be built from inside");
        }
    }
}
