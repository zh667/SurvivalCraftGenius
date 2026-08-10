namespace SurvivalcraftGenius.Npc;

/// <summary>
/// Scan-order geometry, kept engine-free so it can be unit tested: a gap here
/// silently loses ore, a duplicate silently doubles the cost.
/// </summary>
public static class GeniusScanGeometry
{
    /// <summary>
    /// Columns at Chebyshev distance exactly <paramref name="ring"/> from the
    /// centre — the perimeter of a square, each column emitted once. Walking
    /// outward ring by ring makes a wide search nearest-first, so an
    /// interrupted scan still holds the closest match and can report honestly
    /// how far it actually reached.
    /// </summary>
    public static IEnumerable<(int X, int Z)> RingColumns(int centerX, int centerZ, int ring)
    {
        if (ring <= 0)
        {
            yield return (centerX, centerZ);
            yield break;
        }

        for (var dx = -ring; dx <= ring; dx++)
        {
            yield return (centerX + dx, centerZ - ring);
            yield return (centerX + dx, centerZ + ring);
        }

        // The two rows above already covered all four corners.
        for (var dz = -ring + 1; dz <= ring - 1; dz++)
        {
            yield return (centerX - ring, centerZ + dz);
            yield return (centerX + ring, centerZ + dz);
        }
    }
}
