using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Agent;

/// <summary>
/// Persists the companion's kept gear per world, next to the conversation
/// memory and guarded by the same world seed stamp (Survivalcraft recycles
/// world folder names). Without this the stash lived only in memory, so
/// dismissing the companion and quitting the game destroyed the backpack —
/// strictly worse than the old behaviour of dropping it on the ground.
/// Pure .NET: block values are plain ints, no game types.
/// </summary>
public sealed class StashStore(string directory)
{
    /// <summary>Per-owner stacks: owner PlayerGUID ("N") -> (block value, count).</summary>
    public Dictionary<string, List<(int Value, int Count)>> Load(string worldKey, int worldSeed)
    {
        var result = new Dictionary<string, List<(int Value, int Count)>>(StringComparer.OrdinalIgnoreCase);
        var path = PathFor(worldKey);
        try
        {
            if (!File.Exists(path))
            {
                return result;
            }

            var root = JObject.Parse(File.ReadAllText(path));
            if ((int?)root["seed"] != worldSeed)
            {
                File.Delete(path);
                return result;
            }

            foreach (var owner in (root["stashes"] as JObject)?.Properties() ?? [])
            {
                var items = new List<(int Value, int Count)>();
                foreach (var entry in owner.Value as JArray ?? [])
                {
                    var value = (int?)entry["v"] ?? 0;
                    var count = (int?)entry["n"] ?? 0;
                    if (value != 0 && count > 0)
                    {
                        items.Add((value, count));
                    }
                }

                if (items.Count > 0)
                {
                    result[owner.Name] = items;
                }
            }
        }
        catch (Exception)
        {
            // A corrupt stash file must never block loading a world.
        }

        return result;
    }

    public void Save(
        string worldKey,
        int worldSeed,
        IReadOnlyDictionary<string, List<(int Value, int Count)>> stashes)
    {
        var path = PathFor(worldKey);
        if (stashes.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(directory);
        var owners = new JObject();
        foreach (var (ownerId, items) in stashes)
        {
            owners[ownerId] = new JArray(items.Select(item => new JObject
            {
                ["v"] = item.Value,
                ["n"] = item.Count,
            }));
        }

        var root = new JObject
        {
            ["seed"] = worldSeed,
            ["stashes"] = owners,
        };

        // Write-then-move so a crash mid-write can't corrupt the only copy.
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToString());
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string worldKey)
    {
        var safe = new string(worldKey.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length == 0)
        {
            safe = "world";
        }

        return Path.Combine(directory, safe + ".stash.json");
    }
}
