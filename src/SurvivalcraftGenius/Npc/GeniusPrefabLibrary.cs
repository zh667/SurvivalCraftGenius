// The "manifest declares prefabs, a text file holds the cells" shape is ported
// from 铁器风云 1.0.0 (BuildingsManager.InitializeBuildings) by Abrams、Bradley,
// used with the authors' permission — see docs/ATTRIBUTION.md.
// Changes: the manifest is the file listing itself rather than a separate XML
// (one fewer thing for a player to keep in sync), prefabs live in a
// player-editable folder like the knowledge guides, and a shipped prefab the
// player has edited is never overwritten.

namespace SurvivalcraftGenius.Npc;

/// <summary>
/// The prefab folder: shipped designs a player can edit, and their own designs
/// alongside. Pure file work — no game types, so it is fully testable.
/// </summary>
public sealed class GeniusPrefabLibrary(string directory)
{
    public const string Extension = ".txt";

    public string Directory { get; } = directory;

    /// <summary>
    /// Writes the shipped prefabs on first run and upgrades an untouched older
    /// copy, but never touches a file the player has edited — same contract as
    /// the knowledge guides.
    /// </summary>
    public void EnsureShipped()
    {
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var (name, content) in ShippedPrefabs.All)
        {
            var path = Path.Combine(Directory, name + Extension);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, content);
                continue;
            }

            var existing = File.ReadAllText(path).Trim();
            if (existing != content.Trim()
                && ShippedPrefabs.PreviousVersions(name).Any(old => existing == old.Trim()))
            {
                File.WriteAllText(path, content);
            }
        }
    }

    /// <summary>Prefab names available to build, sorted for a stable listing.</summary>
    public IReadOnlyList<string> Names()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            return [.. System.IO.Directory
                .GetFiles(Directory, "*" + Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Loads a prefab by name, matching loosely so the model does not have to
    /// reproduce the filename exactly. Null when nothing matches.
    /// </summary>
    public GeniusPrefab? Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = Names().FirstOrDefault(candidate =>
                string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            ?? Names().FirstOrDefault(candidate =>
                candidate.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        try
        {
            return GeniusPrefab.Parse(
                match, File.ReadAllText(Path.Combine(Directory, match + Extension)));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
