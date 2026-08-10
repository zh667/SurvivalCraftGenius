namespace SurvivalcraftGenius.Npc;

/// <summary>
/// The shape of a shelter, kept engine-free so the invariants that make it a
/// building rather than a pile — a floor with no holes, walls that close, a
/// doorway you can walk through, a roof — are unit tested.
///
/// The descent bug taught this lesson once already: geometry that only exists
/// inside an order is geometry nothing can check.
/// </summary>
public static class GeniusShelterPlan
{
    public readonly record struct Cell(int X, int Y, int Z, bool Solid);

    /// <summary>
    /// Every cell of the structure, floor first so the building is supported
    /// before anything rests on it, then interior, walls and roof.
    /// <paramref name="groundY"/> is the floor level itself.
    /// </summary>
    public static IEnumerable<Cell> Cells(
        int originX, int groundY, int originZ, int width, int length, int wallHeight)
    {
        var maxX = originX + width - 1;
        var maxZ = originZ + length - 1;
        var doorX = originX + (width / 2);

        // Floor: every column, including any the terrain left hollow. This is
        // the anti-floating guarantee.
        for (var x = originX; x <= maxX; x++)
        {
            for (var z = originZ; z <= maxZ; z++)
            {
                yield return new Cell(x, groundY, z, true);
            }
        }

        for (var y = groundY + 1; y <= groundY + wallHeight; y++)
        {
            for (var x = originX; x <= maxX; x++)
            {
                for (var z = originZ; z <= maxZ; z++)
                {
                    var onEdge = x == originX || x == maxX || z == originZ || z == maxZ;
                    if (!onEdge)
                    {
                        // Interior has to be actively cleared, or the "house"
                        // is a solid block with a roof on it.
                        yield return new Cell(x, y, z, false);
                        continue;
                    }

                    // Doorway: two cells tall on the -Z wall.
                    var isDoor = x == doorX && z == originZ && y <= groundY + 2;
                    yield return new Cell(x, y, z, !isDoor);
                }
            }
        }

        for (var x = originX; x <= maxX; x++)
        {
            for (var z = originZ; z <= maxZ; z++)
            {
                yield return new Cell(x, groundY + wallHeight + 1, z, true);
            }
        }
    }
}
