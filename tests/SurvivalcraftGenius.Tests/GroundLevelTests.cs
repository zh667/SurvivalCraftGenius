using SurvivalcraftGenius.Npc;
using Xunit;
using Column = SurvivalcraftGenius.Npc.GeniusGroundLevel.Column;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// Flattening a plot. The tools used to reject uneven ground; a player would
/// just dig and fill, so now so do we.
/// </summary>
public class GroundLevelTests
{
    [Fact]
    public void ChooseTargetY_PicksAnExistingHeight_NotAnAverage()
    {
        // Mean would be 101.5 — a height no column actually has, so every
        // single one would need work.
        Assert.Equal(101, GeniusGroundLevel.ChooseTargetY([100, 101, 102, 103]));
    }

    [Fact]
    public void ChooseTargetY_IsUnmovedByOneTallSpike()
    {
        // A single boulder must not drag the whole floor up with it.
        Assert.Equal(100, GeniusGroundLevel.ChooseTargetY([100, 100, 100, 100, 140]));
    }

    [Fact]
    public void ChooseTargetY_MinimisesTotalWork()
    {
        int[] heights = [100, 100, 101, 103];
        var target = GeniusGroundLevel.ChooseTargetY(heights);
        var columns = heights.Select((h, i) => new Column(i, 0, h)).ToList();
        var best = GeniusGroundLevel.Cost(columns, target);

        for (var candidate = 99; candidate <= 104; candidate++)
        {
            var cost = GeniusGroundLevel.Cost(columns, candidate);
            Assert.True(
                best.Cut + best.Fill <= cost.Cut + cost.Fill,
                $"target {target} costs {best.Cut + best.Fill}, {candidate} costs {cost.Cut + cost.Fill}");
        }
    }

    [Fact]
    public void Plan_LeavesEveryColumnAtTheTargetHeight()
    {
        var columns = new List<Column>
        {
            new(0, 0, 100), new(1, 0, 102), new(2, 0, 99), new(3, 0, 101),
        };
        var target = GeniusGroundLevel.ChooseTargetY(columns.Select(c => c.GroundY).ToList());

        // Replay the ops onto a model of the world and check the result.
        var ground = columns.ToDictionary(c => c.X, c => c.GroundY);
        foreach (var op in GeniusGroundLevel.Plan(columns, target))
        {
            ground[op.X] = op.Fill ? op.Y : op.Y - 1;
        }

        Assert.All(ground.Values, y => Assert.Equal(target, y));
    }

    [Fact]
    public void Plan_CutsFromTheTopDown()
    {
        // Sand and gravel fall: digging bottom-first refills the hole from above.
        var ops = GeniusGroundLevel.Plan([new Column(0, 0, 105)], 100)
            .Where(op => !op.Fill)
            .Select(op => op.Y)
            .ToList();

        Assert.Equal([105, 104, 103, 102, 101], ops);
    }

    [Fact]
    public void Plan_FillsFromTheBottomUp()
    {
        // A block placed in mid-air has nothing to rest on.
        var ops = GeniusGroundLevel.Plan([new Column(0, 0, 97)], 100)
            .Where(op => op.Fill)
            .Select(op => op.Y)
            .ToList();

        Assert.Equal([98, 99, 100], ops);
    }

    [Fact]
    public void Plan_DoesAllCuttingBeforeAnyFilling()
    {
        var ops = GeniusGroundLevel.Plan(
            [new Column(0, 0, 103), new Column(1, 0, 98)], 100).ToList();
        var lastCut = ops.FindLastIndex(op => !op.Fill);
        var firstFill = ops.FindIndex(op => op.Fill);

        Assert.True(lastCut < firstFill, "cuts must finish before fills start");
    }

    [Fact]
    public void Plan_IsEmptyForGroundThatIsAlreadyFlat()
    {
        var columns = new List<Column> { new(0, 0, 100), new(1, 0, 100) };

        Assert.Empty(GeniusGroundLevel.Plan(columns, 100));
        Assert.Null(GeniusGroundLevel.Describe(columns, 100));
    }

    [Fact]
    public void Describe_NamesBothHalvesOfTheWork()
    {
        var text = GeniusGroundLevel.Describe(
            [new Column(0, 0, 102), new Column(1, 0, 98)], 100);

        Assert.Contains("削平", text);
        Assert.Contains("垫高", text);
    }
}
