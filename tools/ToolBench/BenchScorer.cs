using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;

namespace SurvivalcraftGenius.ToolBench;

/// <summary>
/// Selection: was the first tool call in the acceptable set. ArgsValid: did
/// the arguments parse and carry every schema-required key (for whatever tool
/// was actually called). ArgsMatch: did the pinned key args hold.
/// </summary>
public sealed record BenchVerdict(bool Selection, bool ArgsValid, bool ArgsMatch, string Detail)
{
    public bool Pass => Selection && ArgsValid && ArgsMatch;
}

public static class BenchScorer
{
    public static BenchVerdict Judge(BenchCase benchCase, LlmResponse response, ToolRegistry registry)
    {
        if (!response.HasToolCalls)
        {
            var said = response.Content.Length > 60 ? response.Content[..60] + "…" : response.Content;
            return new BenchVerdict(false, false, false, $"no tool call, said: {said}");
        }

        var call = response.ToolCalls[0];
        var selection = benchCase.AcceptTools.Contains(call.Name, StringComparer.Ordinal);

        JObject? args = null;
        try
        {
            args = JObject.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson)
                ? "{}"
                : call.ArgumentsJson);
        }
        catch (Exception)
        {
        }

        var argsValid = false;
        if (args is not null && registry.TryGet(call.Name, out var tool))
        {
            var schema = JObject.Parse(tool.ParametersJsonSchema);
            var required = schema["required"] as JArray ?? [];
            argsValid = required.All(key => args.ContainsKey((string?)key ?? ""));
        }

        var argsMatch = selection && args is not null && benchCase.ExpectArgs.All(pair =>
            args.TryGetValue(pair.Key, out var actual) && MatchesAny(Normalize(actual), pair.Value));

        var detail = $"{call.Name} {call.ArgumentsJson}";
        return new BenchVerdict(selection, argsValid, argsMatch, detail);
    }

    /// <summary>JSON value → comparable lowercase string ("true", "5", "煤矿").</summary>
    private static string Normalize(JToken token) => token.Type switch
    {
        JTokenType.Boolean => (bool)token ? "true" : "false",
        JTokenType.Null => "",
        _ => token.ToString().Trim().ToLowerInvariant(),
    };

    /// <summary>
    /// Loose equality: exact (case-insensitive) or containment either way, so
    /// expected "煤" accepts "煤矿" and expected "煤矿" accepts "煤".
    /// </summary>
    private static bool MatchesAny(string actual, IReadOnlyList<string> anyOf)
    {
        foreach (var candidate in anyOf)
        {
            var expected = candidate.Trim().ToLowerInvariant();
            if (actual == expected)
            {
                return true;
            }

            if (actual.Length > 0 && expected.Length > 0
                && (actual.Contains(expected, StringComparison.Ordinal)
                    || expected.Contains(actual, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
