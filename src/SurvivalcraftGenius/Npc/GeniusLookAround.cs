using Engine;
using SurvivalcraftGenius.Npc.Nav;

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Egocentric top-down ASCII terrain map (Numen's look_around lesson, backed
/// by the VLN/VoT literature it cites: text grids beat coordinate lists for
/// spatial reasoning). Every glyph is derived from the SAME NavCell predicates
/// the A* planner uses (<see cref="INavWorld"/>), so perception and navigation
/// can never disagree about what is walkable. Compass axes follow the sun as
/// measured in TravelMap: east=-X, west=+X, north=+Z, south=-Z.
/// </summary>
public static class GeniusLookAround
{
    public const int DefaultRadius = 8;
    public const int MinRadius = 4;
    public const int MaxRadius = 16;

    /// <summary>How far a column may step up/down and still count as walkable.</summary>
    private static readonly int[] FootingOffsets = [0, 1, -1, -2, -3];

    public static string Render(
        INavWorld world,
        Point3 feet,
        Point3? playerCell,
        int radius,
        string facing,
        Func<int, int, bool>? isLoaded = null)
    {
        radius = Math.Clamp(radius, MinRadius, MaxRadius);
        var size = radius * 2 + 1;
        var grid = new char[size, size];

        // Row 0 is north (+Z), column 0 is west (+X) — sun-verified axes.
        for (var row = 0; row < size; row++)
        {
            var z = feet.Z + (radius - row);
            for (var column = 0; column < size; column++)
            {
                var x = feet.X + (radius - column);
                grid[row, column] = isLoaded is not null && !isLoaded(x, z)
                    ? '?'
                    : Classify(world, x, feet.Y, z);
            }
        }

        InflateHazards(grid, size);

        grid[radius, radius] = '@';
        if (playerCell is { } player
            && Math.Abs(player.X - feet.X) <= radius
            && Math.Abs(player.Z - feet.Z) <= radius
            && (player.X != feet.X || player.Z != feet.Z))
        {
            grid[radius - (player.Z - feet.Z), radius - (player.X - feet.X)] = 'P';
        }

        var lines = new List<string>(size + 4)
        {
            $"俯视地形图 半径{radius} 我=({feet.X},{feet.Y},{feet.Z}) 朝向={facing}",
            "轴向(以太阳实测): 上=北(z增) 下=南(z减) 左=西(x增) 右=东(x减)",
        };
        for (var row = 0; row < size; row++)
        {
            var builder = new System.Text.StringBuilder(size);
            for (var column = 0; column < size; column++)
            {
                builder.Append(grid[row, column]);
            }

            lines.Add(builder.ToString());
        }

        lines.Add("图例: @=我 P=玩家 .=平地可走 ^=上1格台阶 v=可跳下1-3格 V=深坑/悬崖 " +
            "#=墙/障碍 ~=水 D=关着的门 !=岩浆/危险 x=紧邻危险慎踩 ?=未加载");
        lines.Add("提示: 每字符=1格;目标坐标按轴向换算(如向上3格 → z+3)。规划复杂路线交给 goto 即可。");
        return string.Join("\n", lines);
    }

    /// <summary>One column → one glyph, using the planner's own cell semantics.</summary>
    private static char Classify(INavWorld world, int x, int feetY, int z)
    {
        // Dangers at body level dominate everything.
        for (var dy = -1; dy <= 1; dy++)
        {
            var kind = world.At(x, feetY + dy, z).Kind;
            if (kind is NavKind.Lava or NavKind.Hazard)
            {
                return '!';
            }
        }

        if (world.At(x, feetY, z).Kind == NavKind.Door
            || world.At(x, feetY + 1, z).Kind == NavKind.Door)
        {
            return 'D';
        }

        // Walkable footing within a step/jump/short drop.
        foreach (var dy in FootingOffsets)
        {
            var floor = world.At(x, feetY + dy - 1, z);
            var lower = world.At(x, feetY + dy, z);
            var upper = world.At(x, feetY + dy + 1, z);
            if (lower.Kind == NavKind.Water || floor.Kind == NavKind.Water)
            {
                return '~';
            }

            if (floor.Standable && lower.Enterable && upper.Enterable)
            {
                return dy switch
                {
                    0 => '.',
                    1 => '^',
                    _ => 'v',
                };
            }
        }

        // No footing: a wall at body height, or a deep drop.
        if (world.At(x, feetY, z).Kind == NavKind.Solid
            || world.At(x, feetY + 1, z).Kind == NavKind.Solid)
        {
            return '#';
        }

        for (var dy = -4; dy >= -9; dy--)
        {
            var kind = world.At(x, feetY + dy, z).Kind;
            if (kind == NavKind.Lava)
            {
                return '!';
            }

            if (kind == NavKind.Water)
            {
                return '~';
            }

            if (kind != NavKind.Air)
            {
                break;
            }
        }

        return 'V';
    }

    /// <summary>
    /// Costmap-style inflation (Numen/ROS Nav2): walkable cells touching a
    /// hazard become 'x' so the model keeps a one-cell safety margin.
    /// </summary>
    private static void InflateHazards(char[,] grid, int size)
    {
        const string inflatable = ".^v~D";
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                if (!inflatable.Contains(grid[row, column]))
                {
                    continue;
                }

                for (var dr = -1; dr <= 1 && grid[row, column] != 'x'; dr++)
                {
                    for (var dc = -1; dc <= 1; dc++)
                    {
                        var r = row + dr;
                        var c = column + dc;
                        if (r >= 0 && r < size && c >= 0 && c < size && grid[r, c] == '!')
                        {
                            grid[row, column] = 'x';
                            break;
                        }
                    }
                }
            }
        }
    }
}
