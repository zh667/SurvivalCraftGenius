using System.Globalization;
using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.ToolBench;

// Tool-selection benchmark (Numen's toolBench lesson, minimal version):
// frozen scenarios × the real ToolCatalog × the real system prompt × a live
// LLM. Measures whether prompt/tool-description changes help or hurt, with
// results appended to git-tracked benchmark/history.csv.
//
//   dotnet run --project tools/ToolBench -p:SurvivalcraftDir=$HOME/sc-libs/ [-- --samples 1 --filter mine]
//
// Config (shell env overrides benchmark/.env):
//   GENIUS_BENCH_API_KEY   required — skips gracefully when absent
//   GENIUS_BENCH_BASE_URL  default https://api.deepseek.com/v1
//   GENIUS_BENCH_MODEL     default deepseek-chat
//   GENIUS_BENCH_SAMPLES   default 3

var samplesOverride = (int?)null;
var filter = "";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--samples" when i + 1 < args.Length:
            samplesOverride = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--filter" when i + 1 < args.Length:
            filter = args[++i];
            break;
    }
}

var benchDir = FindBenchmarkDir();
var env = LoadEnv(Path.Combine(benchDir, ".env"));
string Get(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } fromShell
        ? fromShell
        : env.GetValueOrDefault(key, fallback);

var apiKey = Get("GENIUS_BENCH_API_KEY", "");
var baseUrl = Get("GENIUS_BENCH_BASE_URL", "https://api.deepseek.com/v1");
var model = Get("GENIUS_BENCH_MODEL", "deepseek-chat");
var samples = samplesOverride
    ?? int.Parse(Get("GENIUS_BENCH_SAMPLES", "3"), CultureInfo.InvariantCulture);

var registry = ToolCatalog.CreateDefaultRegistry();
var cases = BenchCase.LoadFile(Path.Combine(benchDir, "cases.json"));
BenchCase.Validate(cases, registry);
if (filter.Length > 0)
{
    cases = cases.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
}

Console.WriteLine($"ToolBench: {cases.Count} cases × {samples} samples, model={model} @ {new Uri(baseUrl).Host}");
if (apiKey.Length == 0)
{
    Console.WriteLine("GENIUS_BENCH_API_KEY not set (env or benchmark/.env) — cases validated, live run skipped.");
    return 0;
}

var settings = new GeniusSettings { ApiBaseUrl = baseUrl, ApiKey = apiKey, Model = model };
using var client = new LlmClient(settings);
var throttle = new SemaphoreSlim(4);

var runs = cases.Select(async benchCase =>
{
    var verdicts = new BenchVerdict[samples];
    for (var sample = 0; sample < samples; sample++)
    {
        await throttle.WaitAsync();
        try
        {
            List<ChatMessage> messages =
                [ChatMessage.System(GeniusAgent.DefaultSystemPrompt), .. benchCase.History, ChatMessage.User(benchCase.User)];
            var response = await client.CompleteAsync(messages, registry.Tools, CancellationToken.None);
            verdicts[sample] = BenchScorer.Judge(benchCase, response, registry);
        }
        catch (Exception exception)
        {
            verdicts[sample] = new BenchVerdict(false, false, false, $"request failed: {exception.Message}");
        }
        finally
        {
            throttle.Release();
        }
    }

    return (Case: benchCase, Verdicts: verdicts);
}).ToList();

var results = await Task.WhenAll(runs);

var totalSamples = 0;
int selection = 0, argsValid = 0, argsMatch = 0, passAtK = 0;
foreach (var (benchCase, verdicts) in results.OrderBy(r => r.Case.Name, StringComparer.Ordinal))
{
    totalSamples += verdicts.Length;
    selection += verdicts.Count(v => v.Selection);
    argsValid += verdicts.Count(v => v.ArgsValid);
    argsMatch += verdicts.Count(v => v.ArgsMatch);
    var pass = verdicts.Any(v => v.Pass);
    if (pass)
    {
        passAtK++;
    }

    Console.WriteLine($"{(pass ? "✓" : "✗")} {benchCase.Name,-28} " +
        $"sel {verdicts.Count(v => v.Selection)}/{verdicts.Length} " +
        $"valid {verdicts.Count(v => v.ArgsValid)}/{verdicts.Length} " +
        $"match {verdicts.Count(v => v.ArgsMatch)}/{verdicts.Length}");
    foreach (var verdict in verdicts.Where(v => !v.Pass))
    {
        Console.WriteLine($"    ✗ {verdict.Detail}");
    }
}

string Pct(int hits) => (100.0 * hits / totalSamples).ToString("F1", CultureInfo.InvariantCulture);
var passPct = (100.0 * passAtK / results.Length).ToString("F1", CultureInfo.InvariantCulture);
Console.WriteLine($"TOTAL selection {Pct(selection)}%  pass@{samples} {passPct}%  " +
    $"args_valid {Pct(argsValid)}%  args_match {Pct(argsMatch)}%");

var historyPath = Path.Combine(benchDir, "history.csv");
if (!File.Exists(historyPath))
{
    File.WriteAllText(historyPath,
        "timestamp,provider,model,samples,cases,selection,pass_at_k,args_valid,args_match\n");
}

File.AppendAllText(historyPath, string.Join(",",
    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
    new Uri(baseUrl).Host,
    model,
    samples.ToString(CultureInfo.InvariantCulture),
    results.Length.ToString(CultureInfo.InvariantCulture),
    Pct(selection) + "%",
    passPct + "%",
    Pct(argsValid) + "%",
    Pct(argsMatch) + "%") + "\n");
Console.WriteLine($"appended to {historyPath}");
return 0;

static string FindBenchmarkDir()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "benchmark", "cases.json");
        if (File.Exists(candidate))
        {
            return Path.GetDirectoryName(candidate)!;
        }

        directory = directory.Parent;
    }

    throw new InvalidDataException("benchmark/cases.json not found above " + AppContext.BaseDirectory);
}

static Dictionary<string, string> LoadEnv(string path)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!File.Exists(path))
    {
        return values;
    }

    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('='))
        {
            continue;
        }

        var separator = trimmed.IndexOf('=');
        values[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
    }

    return values;
}
