using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// Regression cover for the v0.9.5 descent bug. The shaft carved only two of
/// the three cells a 1.77-tall body needs, so <c>descend_to</c> jammed on the
/// first step through solid rock — it "worked" in playtests exactly as far as
/// the terrain happened to already be air (0-3 levels), which is what made it
/// look intermittent rather than broken.
/// </summary>
public class DescentGeometryTests
{
    [Fact]
    public void StepGoesOneDownAndOneSideways()
    {
        var target = GeniusDescentGeometry.StepTarget((10, 100, 20), 0);

        Assert.Equal(99, target.Y);
        Assert.Equal(1, Math.Abs(target.X - 10) + Math.Abs(target.Z - 20));
    }

    [Fact]
    public void FourStepsReturnToTheStartingColumnTwoBlocksLower()
    {
        var feet = (X: 0, Y: 100, Z: 0);
        for (var i = 0; i < 4; i++)
        {
            feet = GeniusDescentGeometry.StepTarget(feet, i);
        }

        // A 2x2 spiral: back over the start, four levels down.
        Assert.Equal((0, 96, 0), feet);
    }

    [Fact]
    public void EveryStepStaysInsideATwoByTwoFootprint()
    {
        var feet = (X: 5, Y: 100, Z: 5);
        var columns = new HashSet<(int, int)> { (feet.X, feet.Z) };
        for (var i = 0; i < 16; i++)
        {
            feet = GeniusDescentGeometry.StepTarget(feet, i);
            columns.Add((feet.X, feet.Z));
        }

        Assert.Equal(4, columns.Count);
    }

    [Fact]
    public void CarveClearsTheWholeBodyAtTheCurrentHeightPlusTheLandingCell()
    {
        var feet = (X: 0, Y: 100, Z: 0);
        var carved = GeniusDescentGeometry.CellsToCarve(feet, 0).ToList();
        var target = GeniusDescentGeometry.StepTarget(feet, 0);

        // While walking across, the body still stands at feet.Y — so the
        // destination column has to be open at every body cell of THAT height,
        // not just at the cell being stepped onto.
        foreach (var height in GeniusDescentGeometry.BodyHeights)
        {
            Assert.Contains((target.X, feet.Y + height, target.Z), carved);
        }

        Assert.Contains(target, carved);
    }

    [Fact]
    public void CarveIsOrderedTopDownSoFallingGravelCannotRefillTheFloor()
    {
        var carved = GeniusDescentGeometry.CellsToCarve((0, 100, 0), 0).ToList();

        Assert.Equal(carved.OrderByDescending(cell => cell.Y).ToList(), carved);
    }

    /// <summary>
    /// The test that would have caught the shipped bug: drive the real geometry
    /// through a world that is solid rock everywhere, clearing exactly what it
    /// asks for, and assert the body never ends a step inside a solid cell.
    /// With the old two-cell carve this fails on the very first step.
    /// </summary>
    [Fact]
    public void DescendsThroughSolidRockWithoutTheBodyEverClipping()
    {
        var air = new HashSet<(int X, int Y, int Z)>();
        var feet = (X: 0, Y: 120, Z: 0);

        // Spawn standing in a pocket just big enough to hold the body.
        foreach (var height in GeniusDescentGeometry.BodyHeights)
        {
            air.Add((feet.X, feet.Y + height, feet.Z));
        }

        for (var step = 0; step < 60; step++)
        {
            foreach (var cell in GeniusDescentGeometry.CellsToCarve(feet, step))
            {
                air.Add(cell);
            }

            var target = GeniusDescentGeometry.StepTarget(feet, step);

            // Walking across at the old height, then standing at the new one.
            foreach (var height in GeniusDescentGeometry.BodyHeights)
            {
                Assert.Contains((target.X, feet.Y + height, target.Z), air);
                Assert.Contains((target.X, target.Y + height, target.Z), air);
            }

            feet = target;
        }

        Assert.Equal(60, 120 - feet.Y);
    }
}
