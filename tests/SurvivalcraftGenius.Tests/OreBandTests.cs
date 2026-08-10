using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class OreBandTests
{
    [Theory]
    [InlineData("铁矿", 2, 40)]
    [InlineData("iron ore", 2, 40)]
    [InlineData("钻石", 2, 15)]
    [InlineData("diamond", 2, 15)]
    [InlineData("铜", 20, 65)]
    [InlineData("孔雀石", 20, 65)]
    [InlineData("煤矿", 5, 200)]
    [InlineData("硝石", 50, 90)]
    public void Match_ReturnsTheGeneratorsBand(string query, int minY, int maxY)
    {
        var band = GeniusOreBands.Match(query);

        Assert.NotNull(band);
        Assert.Equal(minY, band!.Value.MinY);
        Assert.Equal(maxY, band.Value.MaxY);
    }

    [Theory]
    [InlineData("花岗岩")]
    [InlineData("木头")]
    [InlineData("")]
    public void Match_IgnoresNonOres(string query) => Assert.Null(GeniusOreBands.Match(query));

    [Fact]
    public void Hint_AboveTheBand_PointsAtDescendTo()
    {
        var hint = GeniusOreBands.Hint("铁矿", myY: 110f);

        Assert.Contains("descend_to", hint);
        Assert.Contains("y2-40", hint);
    }

    [Fact]
    public void Hint_InsideTheBand_SaysToSearchSidewaysInstead()
    {
        var hint = GeniusOreBands.Hint("铁矿", myY: 20f);

        Assert.DoesNotContain("descend_to", hint);
        Assert.Contains("已经在带内", hint);
    }

    [Fact]
    public void Hint_ForNonOre_IsEmpty() => Assert.Equal("", GeniusOreBands.Hint("花岗岩", 60f));

    [Fact]
    public void SearchY_ForDiamond_StaysBelowTheLavaPockets()
    {
        // Lava pockets sit at y15-20, right on top of the diamond band.
        Assert.True(GeniusOreBands.Match("钻石")!.Value.SearchY < 15);
    }
}
