using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The prefab format ported from 铁器风云. Engine-free, so all of it runs on
/// Linux — unlike the generator it replaces.
/// </summary>
public class PrefabTests
{
    [Fact]
    public void ParsesTheCellListAndMeasuresTheFootprint()
    {
        var prefab = GeniusPrefab.Parse("小屋", """
            0,0,0,26
            1,0,0,26
            0,1,0,15454
            """);

        Assert.Equal(3, prefab.Cells.Count);
        Assert.Equal(2, prefab.Width);
        Assert.Equal(2, prefab.Height);
        Assert.Equal(1, prefab.Length);
        Assert.Equal(0, prefab.SkippedLines);
    }

    [Fact]
    public void CommentsAndBlankLinesAreNotCells()
    {
        var prefab = GeniusPrefab.Parse("小屋", """
            # 地基
            0,0,0,26

            0,1,0,26
            """);

        Assert.Equal(2, prefab.Cells.Count);
        Assert.Equal(0, prefab.SkippedLines);
    }

    /// <summary>
    /// A malformed line must not take the whole building down, but it must not
    /// vanish either — a prefab quietly missing its roof is worse than one that
    /// says so.
    /// </summary>
    [Fact]
    public void MalformedLinesAreSkippedAndCounted()
    {
        var prefab = GeniusPrefab.Parse("小屋", """
            0,0,0,26
            这一行是坏的
            1,2,3
            9,9,9,not_a_number
            1,0,0,26
            """);

        Assert.Equal(2, prefab.Cells.Count);
        Assert.Equal(3, prefab.SkippedLines);
    }

    /// <summary>
    /// Authored anywhere in a world, placed anywhere else: the lowest corner
    /// becomes the origin so the caller's coordinate means the same thing for
    /// every prefab.
    /// </summary>
    [Fact]
    public void CoordinatesAreRebasedToTheLowestCorner()
    {
        var prefab = GeniusPrefab.Parse("远处的屋子", """
            1000,64,-500,26
            1001,64,-500,26
            1000,65,-500,26
            """);

        Assert.Contains(prefab.Cells, cell => cell is { X: 0, Y: 0, Z: 0 });
        Assert.Equal(2, prefab.Width);
        Assert.Equal(2, prefab.Height);
        Assert.Equal(1, prefab.Length);
    }

    /// <summary>Nothing may be placed against thin air, so lower layers go first.</summary>
    [Fact]
    public void CellsComeOutBottomUp()
    {
        var prefab = GeniusPrefab.Parse("塔", """
            0,3,0,26
            0,0,0,26
            0,2,0,26
            0,1,0,26
            """);

        Assert.Equal([0, 1, 2, 3], prefab.Cells.Select(cell => cell.Y));
    }

    [Fact]
    public void OrderIsStableAcrossParses()
    {
        const string text = """
            2,0,1,26
            0,0,2,26
            1,0,0,26
            """;

        Assert.Equal(
            GeniusPrefab.Parse("a", text).Cells,
            GeniusPrefab.Parse("a", text).Cells);
    }

    [Fact]
    public void MaterialCostGroupsByBlockAndIgnoresAir()
    {
        var prefab = GeniusPrefab.Parse("小屋", """
            0,0,0,26
            1,0,0,26
            2,0,0,15454
            3,0,0,0
            """);

        var cost = prefab.MaterialCost();

        Assert.Equal(2, cost[26]);
        Assert.Equal(1, cost[15454]);
        Assert.DoesNotContain(0, cost.Keys);
    }

    [Fact]
    public void AnEmptyFileIsAnEmptyPrefabRatherThanACrash()
    {
        var prefab = GeniusPrefab.Parse("空", "   \n\n# 只有注释\n");

        Assert.Empty(prefab.Cells);
        Assert.Equal(0, prefab.Width);
        Assert.Equal("0x0x0, 0 格", prefab.Describe());
    }
}
