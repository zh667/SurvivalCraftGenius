using Engine;
using SurvivalcraftGenius.Npc;
using SurvivalcraftGenius.Npc.Nav;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class LookAroundTests
{
    /// <summary>Flat solid ground up to y=63 (feet stand at 64), sparse overrides.</summary>
    private sealed class GridWorld : INavWorld
    {
        private readonly Dictionary<Point3, NavCell> _cells = [];

        public NavCell At(int x, int y, int z)
        {
            if (y < 1)
            {
                return NavCell.Bedrock;
            }

            if (_cells.TryGetValue(new Point3(x, y, z), out var cell))
            {
                return cell;
            }

            return y <= 63 ? Solid : NavCell.Air;
        }

        public void Set(int x, int y, int z, NavCell cell) => _cells[new Point3(x, y, z)] = cell;

        public void SetColumn(int x, int yFrom, int yTo, int z, NavCell cell)
        {
            for (var y = yFrom; y <= yTo; y++)
            {
                Set(x, y, z, cell);
            }
        }

        public static NavCell Solid { get; } = new(NavKind.Solid, 1f, standable: true);

        public static NavCell Water { get; } = new(NavKind.Water, float.PositiveInfinity, standable: false);

        public static NavCell Lava { get; } = new(NavKind.Lava, float.PositiveInfinity, standable: false);

        public static NavCell Door { get; } = new(NavKind.Door, 1f, standable: false);
    }

    private static readonly Point3 Feet = new(0, 64, 0);

    private static char GlyphAt(string rendered, int radius, int x, int z)
    {
        // Two header lines, then rows north(+Z) to south; column 0 is west(+X).
        var lines = rendered.Split('\n');
        return lines[2 + (radius - z)][radius - x];
    }

    [Fact]
    public void FlatGround_IsAllWalkableWithMeInTheCenter()
    {
        var rendered = GeniusLookAround.Render(new GridWorld(), Feet, null, 4, "北(z增)");

        Assert.Equal('@', GlyphAt(rendered, 4, 0, 0));
        Assert.Equal('.', GlyphAt(rendered, 4, 3, -2));
        Assert.Contains("轴向(以太阳实测)", rendered);
        Assert.Contains("图例:", rendered);
        Assert.Contains("朝向=北(z增)", rendered);
    }

    [Fact]
    public void StepUp_WallAndDeepPit_GetDistinctGlyphs()
    {
        var world = new GridWorld();
        world.Set(2, 64, 0, GridWorld.Solid);              // one-block step
        world.SetColumn(-2, 64, 65, 0, GridWorld.Solid);    // two-high wall
        world.SetColumn(3, 55, 63, 3, NavCell.Air);         // deep pit
        world.SetColumn(1, 62, 63, -1, NavCell.Air);        // 2-deep hop-down

        var rendered = GeniusLookAround.Render(world, Feet, null, 4, "?");

        Assert.Equal('^', GlyphAt(rendered, 4, 2, 0));
        Assert.Equal('#', GlyphAt(rendered, 4, -2, 0));
        Assert.Equal('V', GlyphAt(rendered, 4, 3, 3));
        Assert.Equal('v', GlyphAt(rendered, 4, 1, -1));
    }

    [Fact]
    public void WaterAndClosedDoor_AreMarked()
    {
        var world = new GridWorld();
        world.Set(2, 63, 2, GridWorld.Water);   // pond surface
        world.Set(-1, 64, 0, GridWorld.Door);   // closed door at body level

        var rendered = GeniusLookAround.Render(world, Feet, null, 4, "?");

        Assert.Equal('~', GlyphAt(rendered, 4, 2, 2));
        Assert.Equal('D', GlyphAt(rendered, 4, -1, 0));
    }

    [Fact]
    public void Lava_MarksHazardAndInflatesNeighbours()
    {
        var world = new GridWorld();
        world.Set(3, 64, 0, GridWorld.Lava);

        var rendered = GeniusLookAround.Render(world, Feet, null, 4, "?");

        Assert.Equal('!', GlyphAt(rendered, 4, 3, 0));
        Assert.Equal('x', GlyphAt(rendered, 4, 2, 0));
        Assert.Equal('x', GlyphAt(rendered, 4, 2, 1));
        Assert.Equal('.', GlyphAt(rendered, 4, 1, 0));
    }

    [Fact]
    public void Player_AndUnloadedColumns_AreMarked()
    {
        var rendered = GeniusLookAround.Render(
            new GridWorld(), Feet, new Point3(1, 64, 1), 4, "?",
            isLoaded: (x, _) => x > -3);

        Assert.Equal('P', GlyphAt(rendered, 4, 1, 1));
        Assert.Equal('?', GlyphAt(rendered, 4, -3, 0));
        Assert.Equal('?', GlyphAt(rendered, 4, -4, 2));
        Assert.Equal('.', GlyphAt(rendered, 4, -2, 0));
    }

    [Fact]
    public void Radius_IsClampedToBounds()
    {
        var rendered = GeniusLookAround.Render(new GridWorld(), Feet, null, 99, "?");

        // 2 header lines + (16*2+1) rows + 2 footer lines.
        Assert.Equal(2 + 33 + 2, rendered.Split('\n').Length);
        Assert.Contains("半径16", rendered);
    }
}
