using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;

namespace SurvivalcraftGenius.ToolBench;

/// <summary>
/// One frozen scenario: optional prior context + the user message, plus the
/// judgement keys — which first tool calls are acceptable and which argument
/// values are pinned.
/// </summary>
public sealed class BenchCase
{
    public required string Name { get; init; }

    /// <summary>Prior context, roles "user"/"assistant" only.</summary>
    public IReadOnlyList<ChatMessage> History { get; init; } = [];

    /// <summary>
    /// The world_state body injected before the user message, mirroring the
    /// production per-turn context. Null uses the runner's default.
    /// </summary>
    public string? WorldState { get; init; }

    /// <summary>The message under test.</summary>
    public required string User { get; init; }

    /// <summary>Acceptable names for the FIRST tool call.</summary>
    public required IReadOnlyList<string> AcceptTools { get; init; }

    /// <summary>Pinned args: key → any-of acceptable values (loose match).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectArgs { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    public static List<BenchCase> LoadFile(string path)
    {
        var root = JArray.Parse(File.ReadAllText(path));
        var cases = new List<BenchCase>();
        foreach (var entry in root.OfType<JObject>())
        {
            var history = new List<ChatMessage>();
            foreach (var line in entry["history"] as JArray ?? [])
            {
                var role = (string?)line["role"] ?? "user";
                var content = (string?)line["content"] ?? "";
                history.Add(role == "assistant"
                    ? ChatMessage.Assistant(content)
                    : ChatMessage.User(content));
            }

            var expect = new Dictionary<string, IReadOnlyList<string>>();
            if (entry["expect_args"] is JObject expectObject)
            {
                foreach (var property in expectObject.Properties())
                {
                    expect[property.Name] = property.Value is JArray anyOf
                        ? anyOf.Select(v => (string?)v ?? "").ToList()
                        : [(string?)property.Value ?? ""];
                }
            }

            cases.Add(new BenchCase
            {
                Name = (string?)entry["name"] ?? throw new InvalidDataException("case missing name"),
                WorldState = (string?)entry["world_state"],
                History = history,
                User = (string?)entry["user"] ?? throw new InvalidDataException("case missing user"),
                AcceptTools = (entry["accept_tools"] as JArray
                        ?? throw new InvalidDataException($"case '{entry["name"]}' missing accept_tools"))
                    .Select(v => (string?)v ?? "").ToList(),
                ExpectArgs = expect,
            });
        }

        return cases;
    }

    /// <summary>Throws when a case references a tool the catalog doesn't have.</summary>
    public static void Validate(IReadOnlyList<BenchCase> cases, ToolRegistry registry)
    {
        var names = new HashSet<string>(cases.Select(c => c.Name), StringComparer.Ordinal);
        if (names.Count != cases.Count)
        {
            throw new InvalidDataException("duplicate case names");
        }

        foreach (var benchCase in cases)
        {
            foreach (var tool in benchCase.AcceptTools)
            {
                if (!registry.TryGet(tool, out _))
                {
                    throw new InvalidDataException(
                        $"case '{benchCase.Name}' accepts unknown tool '{tool}'");
                }
            }
        }
    }
}
