using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class LandmarkMemoryTests
{
    [Fact]
    public void Record_DedupesByCell_UpdatingTheName()
    {
        var memory = new LandmarkMemory();
        memory.Record("箱子", 1, 2, 3);
        memory.Record("熔炉", 1, 2, 3);

        var snapshot = memory.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("熔炉", snapshot[0].Name);
    }

    [Fact]
    public void Record_CapsAtMaxDroppingOldest()
    {
        var memory = new LandmarkMemory();
        for (var i = 0; i < LandmarkMemory.MaxLandmarks + 5; i++)
        {
            memory.Record("箱子", i, 0, 0);
        }

        var snapshot = memory.Snapshot();
        Assert.Equal(LandmarkMemory.MaxLandmarks, snapshot.Count);
        Assert.Equal(5, snapshot[0].X);
    }

    [Fact]
    public void Remove_ForgetsTheCell()
    {
        var memory = new LandmarkMemory();
        memory.Record("箱子", 1, 2, 3);
        memory.Remove(1, 2, 3);

        Assert.Empty(memory.Snapshot());
        Assert.Equal("", memory.Describe());
    }

    [Fact]
    public void Describe_SortsByDistanceAndShowsIt()
    {
        var memory = new LandmarkMemory();
        memory.Record("远箱子", 100, 0, 0);
        memory.Record("近工作台", 3, 0, 4);

        var described = memory.Describe((0, 0, 0));

        Assert.StartsWith("近工作台(3,0,4) 5m", described, StringComparison.Ordinal);
        Assert.Contains("远箱子(100,0,0) 100m", described);
    }

    [Fact]
    public void Describe_LimitsToNearestFew()
    {
        var memory = new LandmarkMemory();
        for (var i = 1; i <= LandmarkMemory.MaxDescribed + 10; i++)
        {
            memory.Record("箱子", i * 10, 0, 0);
        }

        var described = memory.Describe((0, 0, 0));

        Assert.Equal(LandmarkMemory.MaxDescribed, described.Split(';').Length);
        Assert.DoesNotContain($"({(LandmarkMemory.MaxDescribed + 1) * 10},0,0)", described);
    }

    [Fact]
    public void JsonRoundTrip_PreservesEverything()
    {
        var landmarks = new List<Landmark> { new("工作台", 10, 64, -20), new("箱子", 0, 1, 2) };

        var restored = LandmarkMemory.FromJson(LandmarkMemory.ToJson(landmarks));

        Assert.Equal(landmarks, restored);
    }

    [Fact]
    public void Restore_SkipsDuplicateCells()
    {
        var memory = new LandmarkMemory();
        memory.Record("熔炉", 1, 2, 3);
        memory.Restore([new Landmark("箱子", 1, 2, 3), new Landmark("箱子", 4, 5, 6)]);

        var snapshot = memory.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("熔炉", snapshot[0].Name);
    }
}
