namespace SurvivalcraftGenius.Agent;

/// <summary>
/// "Did you mean" suggestions for misspelled block/item/creature names
/// (Numen's IdSuggest pattern): a wrong name should never be a dead end —
/// the error message itself carries the correction.
/// Pure C#, no engine references; works for Chinese (per-character edit
/// distance) and English alike.
/// </summary>
public static class NameSuggest
{
    /// <summary>Ranks candidates by similarity to the query; empty when nothing is close.</summary>
    public static List<string> Rank(string query, IEnumerable<string> candidates, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var scored = new List<(string Name, float Score)>();
        foreach (var candidate in candidates.Distinct())
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = Score(query, candidate);
            if (score >= 0.4f)
            {
                scored.Add((candidate, score));
            }
        }

        return scored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Name.Length)
            .Take(max)
            .Select(entry => entry.Name)
            .ToList();
    }

    /// <summary>
    /// Ready-to-append clause: " — did you mean 'X' or 'Y'?", or "" when
    /// nothing is close enough to suggest.
    /// </summary>
    public static string Clause(string query, IEnumerable<string> candidates, int max = 3)
    {
        var ranked = Rank(query, candidates, max);
        return ranked.Count == 0
            ? ""
            : $" — did you mean {string.Join(" or ", ranked.Select(name => $"'{name}'"))}?";
    }

    private static float Score(string query, string candidate)
    {
        // Containment either way is a strong signal ("煤" vs "煤矿").
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase)
            || query.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            var shorter = Math.Min(query.Length, candidate.Length);
            var longer = Math.Max(query.Length, candidate.Length);
            return 0.8f + 0.2f * shorter / longer;
        }

        var distance = Levenshtein(query.ToLowerInvariant(), candidate.ToLowerInvariant());
        var editScore = 1f - (float)distance / Math.Max(query.Length, candidate.Length);

        // CJK-friendly: a short query against a long title ("狩腊" vs
        // "战斗与狩猎") scores poorly on edit distance alone — count shared
        // ideographs as token overlap. Non-ASCII only, so English words
        // don't match on stray letters.
        var queryChars = query.Where(c => c > 127).Distinct().ToList();
        if (queryChars.Count > 0)
        {
            var common = queryChars.Count(candidate.Contains);
            if (common > 0)
            {
                var overlapScore = 0.25f + 0.4f * common / queryChars.Count;
                return MathF.Max(editScore, overlapScore);
            }
        }

        return editScore;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
