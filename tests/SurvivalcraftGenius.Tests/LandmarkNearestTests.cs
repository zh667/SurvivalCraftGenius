using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The lookup half of the landmark memory. It was write-only until now:
/// crafting recorded where the bench was and the prompt carried it every turn,
/// but the code still swept ~139,000 cells to find the same bench again.
/// </summary>
public class LandmarkNearestTests
{
    [Fact]
    public void ReturnsNullWhenNothingMatches()
    {
        var memory = new LandmarkMemory();
        memory.Record("熔炉", 10, 64, 10);

        Assert.Null(memory.Nearest("工作台", 0, 64, 0));
    }

    [Fact]
    public void PicksTheClosestOfSeveralWithTheSameName()
    {
        var memory = new LandmarkMemory();
        memory.Record("工作台", 100, 64, 0);
        memory.Record("工作台", 5, 64, 0);
        memory.Record("工作台", 40, 64, 0);

        var found = memory.Nearest("工作台", 0, 64, 0);

        Assert.NotNull(found);
        Assert.Equal(5, found!.X);
    }

    /// <summary>Height counts: a bench 30 below is not "right here".</summary>
    [Fact]
    public void DistanceIncludesTheVerticalAxis()
    {
        var memory = new LandmarkMemory();
        memory.Record("工作台", 0, 34, 0);
        memory.Record("工作台", 8, 64, 0);

        var found = memory.Nearest("工作台", 0, 64, 0);

        Assert.Equal(8, found!.X);
    }

    [Fact]
    public void NamesMatchExactlyRatherThanLoosely()
    {
        var memory = new LandmarkMemory();
        memory.Record("工作台旁的箱子", 1, 64, 1);

        Assert.Null(memory.Nearest("工作台", 0, 64, 0));
    }

    [Fact]
    public void ForgettingACellRemovesItFromLookup()
    {
        var memory = new LandmarkMemory();
        memory.Record("工作台", 5, 64, 5);
        memory.Remove(5, 64, 5);

        Assert.Null(memory.Nearest("工作台", 0, 64, 0));
    }
}
