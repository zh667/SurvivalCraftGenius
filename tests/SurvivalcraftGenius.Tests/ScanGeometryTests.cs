using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class ScanGeometryTests
{
    [Fact]
    public void Ring0_IsTheCentreColumnAlone()
    {
        Assert.Equal([(10, -4)], GeniusScanGeometry.RingColumns(10, -4, 0).ToList());
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 16)]
    [InlineData(7, 56)]
    [InlineData(64, 512)]
    public void Ring_HasExactlyItsPerimeterColumns_NoDuplicates(int ring, int expected)
    {
        var columns = GeniusScanGeometry.RingColumns(0, 0, ring).ToList();

        Assert.Equal(expected, columns.Count);
        Assert.Equal(expected, columns.Distinct().Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(32)]
    public void Ring_ContainsOnlyColumnsAtThatChebyshevDistance(int ring)
    {
        foreach (var (x, z) in GeniusScanGeometry.RingColumns(100, -50, ring))
        {
            Assert.Equal(ring, Math.Max(Math.Abs(x - 100), Math.Abs(z + 50)));
        }
    }

    [Fact]
    public void Rings0ToR_TileTheWholeSquare_ExactlyOnce()
    {
        // The ring walk replaced a plain dx/dz box scan; it must still cover
        // every column in that box, or a search silently misses ore.
        const int radius = 12;
        var covered = Enumerable.Range(0, radius + 1)
            .SelectMany(ring => GeniusScanGeometry.RingColumns(3, 7, ring))
            .ToList();

        var side = (2 * radius) + 1;
        Assert.Equal(side * side, covered.Count);
        Assert.Equal(side * side, covered.Distinct().Count());
        Assert.Contains((3 - radius, 7 - radius), covered);
        Assert.Contains((3 + radius, 7 + radius), covered);
    }

    [Fact]
    public void Rings_ComeOutNearestFirst()
    {
        var previous = -1;
        for (var ring = 0; ring <= 6; ring++)
        {
            foreach (var (x, z) in GeniusScanGeometry.RingColumns(0, 0, ring))
            {
                Assert.True(Math.Max(Math.Abs(x), Math.Abs(z)) >= previous);
            }

            previous = ring;
        }
    }
}
