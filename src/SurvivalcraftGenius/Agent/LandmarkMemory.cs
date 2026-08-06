using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Agent;

public sealed record Landmark(string Name, int X, int Y, int Z);

/// <summary>
/// Structured memory of workstation blocks the NPC has seen or used (crafting
/// tables, furnaces, chests) — Numen's known_blocks lesson. Injected into each
/// turn's world-state context so the model reuses known coordinates instead of
/// re-scanning for a table every time. Recorded on the game thread, read on
/// the agent thread; all access is locked. Pure .NET — no game types.
/// </summary>
public sealed class LandmarkMemory
{
    /// <summary>Oldest entries are dropped past this (48 ≈ several bases' worth).</summary>
    public const int MaxLandmarks = 48;

    /// <summary>Context lists only the nearest few — far bases add noise, not signal.</summary>
    public const int MaxDescribed = 15;

    private readonly object _gate = new();
    private readonly List<Landmark> _landmarks = [];

    /// <summary>Adds or refreshes a landmark; same cell updates the name in place.</summary>
    public void Record(string name, int x, int y, int z)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (_gate)
        {
            var index = _landmarks.FindIndex(l => l.X == x && l.Y == y && l.Z == z);
            if (index >= 0)
            {
                _landmarks[index] = new Landmark(name, x, y, z);
                return;
            }

            _landmarks.Add(new Landmark(name, x, y, z));
            if (_landmarks.Count > MaxLandmarks)
            {
                _landmarks.RemoveAt(0);
            }
        }
    }

    /// <summary>Forget a landmark that turned out to be gone (block destroyed/moved).</summary>
    public void Remove(int x, int y, int z)
    {
        lock (_gate)
        {
            _landmarks.RemoveAll(l => l.X == x && l.Y == y && l.Z == z);
        }
    }

    public IReadOnlyList<Landmark> Snapshot()
    {
        lock (_gate)
        {
            return [.. _landmarks];
        }
    }

    /// <summary>
    /// One compact line per landmark, nearest-first from <paramref name="origin"/>
    /// (unsorted without one). Empty string when nothing is known.
    /// </summary>
    public string Describe((int X, int Y, int Z)? origin = null)
    {
        var snapshot = Snapshot();
        if (snapshot.Count == 0)
        {
            return "";
        }

        var ordered = origin is { } from
            ? snapshot.OrderBy(l => DistanceSquared(l, from)).ToList()
            : [.. snapshot];
        var parts = ordered.Take(MaxDescribed).Select(l =>
        {
            var entry = $"{l.Name}({l.X},{l.Y},{l.Z})";
            if (origin is { } o)
            {
                entry += $" {Math.Round(Math.Sqrt(DistanceSquared(l, o)))}m";
            }

            return entry;
        });
        return string.Join(";", parts);
    }

    public void Restore(IEnumerable<Landmark> landmarks)
    {
        lock (_gate)
        {
            foreach (var landmark in landmarks)
            {
                if (_landmarks.Count >= MaxLandmarks)
                {
                    break;
                }

                if (!_landmarks.Any(l => l.X == landmark.X && l.Y == landmark.Y && l.Z == landmark.Z))
                {
                    _landmarks.Add(landmark);
                }
            }
        }
    }

    public static JArray ToJson(IReadOnlyList<Landmark> landmarks) =>
        new(landmarks.Select(l => new JObject
        {
            ["name"] = l.Name,
            ["x"] = l.X,
            ["y"] = l.Y,
            ["z"] = l.Z,
        }));

    public static List<Landmark> FromJson(JArray? array)
    {
        var landmarks = new List<Landmark>();
        foreach (var entry in array?.OfType<JObject>() ?? [])
        {
            var name = (string?)entry["name"] ?? "";
            if (name.Length > 0)
            {
                landmarks.Add(new Landmark(
                    name,
                    (int?)entry["x"] ?? 0,
                    (int?)entry["y"] ?? 0,
                    (int?)entry["z"] ?? 0));
            }
        }

        return landmarks;
    }

    private static long DistanceSquared(Landmark landmark, (int X, int Y, int Z) from)
    {
        long dx = landmark.X - from.X;
        long dy = landmark.Y - from.Y;
        long dz = landmark.Z - from.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
