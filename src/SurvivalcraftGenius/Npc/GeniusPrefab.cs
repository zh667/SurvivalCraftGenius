// The cell-list format and the "declare prefabs in a manifest, load the cells
// from a text file" shape are ported from 铁器风云 1.0.0 (BuildingsManager /
// Building) by Abrams、Bradley, used with the authors' permission — see
// docs/ATTRIBUTION.md.
// Changes: parsing is engine-free and total (a bad line is skipped and
// counted rather than throwing mid-generation); coordinates are normalised to
// a 0-based origin so a prefab can be authored anywhere and placed anywhere;
// and prefabs carry a material cost so survival mode can refuse up front
// instead of stopping half-built.

namespace SurvivalcraftGenius.Npc;

/// <summary>One cell of a prefab, relative to its own origin.</summary>
public readonly record struct PrefabCell(int X, int Y, int Z, int Value);

/// <summary>
/// A building we ship as data rather than as code.
///
/// <para>build_shelter generates its own geometry, which means the model
/// decides how the house looks — and that is where both the token cost and the
/// variance live ("the first house looked good, the later ones didn't"). A
/// prefab moves the design out of the model entirely: a name and a spot, and
/// the result is identical every time and can be made to look good ONCE.</para>
/// </summary>
public sealed class GeniusPrefab
{
    private GeniusPrefab(string name, IReadOnlyList<PrefabCell> cells, int skippedLines)
    {
        Name = name;
        Cells = cells;
        SkippedLines = skippedLines;
        Width = cells.Count == 0 ? 0 : cells.Max(cell => cell.X) + 1;
        Height = cells.Count == 0 ? 0 : cells.Max(cell => cell.Y) + 1;
        Length = cells.Count == 0 ? 0 : cells.Max(cell => cell.Z) + 1;
    }

    public string Name { get; }

    public IReadOnlyList<PrefabCell> Cells { get; }

    /// <summary>Malformed lines ignored while parsing — surfaced, never silent.</summary>
    public int SkippedLines { get; }

    public int Width { get; }

    public int Height { get; }

    public int Length { get; }

    /// <summary>Footprint description for the model, e.g. "5x4x5, 132 格".</summary>
    public string Describe() => $"{Width}x{Height}x{Length}, {Cells.Count} 格";

    /// <summary>
    /// How many of each block value the build consumes. Survival mode checks
    /// this before laying a single cell, so a short build is refused rather
    /// than abandoned half-finished.
    /// </summary>
    public Dictionary<int, int> MaterialCost()
    {
        var cost = new Dictionary<int, int>();
        foreach (var cell in Cells)
        {
            if (cell.Value != 0)
            {
                cost[cell.Value] = cost.GetValueOrDefault(cell.Value) + 1;
            }
        }

        return cost;
    }

    /// <summary>
    /// Parses the <c>x,y,z,value</c> cell list. Blank lines and lines starting
    /// with '#' are comments. Coordinates are re-based so the lowest corner
    /// becomes (0,0,0) — a prefab can be authored at any position in a world
    /// and still place correctly.
    /// </summary>
    public static GeniusPrefab Parse(string name, string text)
    {
        var raw = new List<PrefabCell>();
        var skipped = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var parts = trimmed.Split(',');
            if (parts.Length != 4
                || !int.TryParse(parts[0].Trim(), out var x)
                || !int.TryParse(parts[1].Trim(), out var y)
                || !int.TryParse(parts[2].Trim(), out var z)
                || !int.TryParse(parts[3].Trim(), out var value))
            {
                skipped++;
                continue;
            }

            raw.Add(new PrefabCell(x, y, z, value));
        }

        if (raw.Count == 0)
        {
            return new GeniusPrefab(name, [], skipped);
        }

        var minX = raw.Min(cell => cell.X);
        var minY = raw.Min(cell => cell.Y);
        var minZ = raw.Min(cell => cell.Z);
        var normalised = raw
            .Select(cell => cell with { X = cell.X - minX, Y = cell.Y - minY, Z = cell.Z - minZ })
            .ToList();

        // Build order matters: bottom-up, so nothing is ever placed against
        // thin air. Within a layer the order is arbitrary but must be stable,
        // or two runs of the same prefab lay cells differently.
        normalised.Sort((a, b) =>
        {
            var byY = a.Y.CompareTo(b.Y);
            if (byY != 0) return byY;
            var byX = a.X.CompareTo(b.X);
            return byX != 0 ? byX : a.Z.CompareTo(b.Z);
        });

        return new GeniusPrefab(name, normalised, skipped);
    }
}
