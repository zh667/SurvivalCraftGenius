using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.ToolBench;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class ToolBenchTests
{
    private static readonly ToolRegistry Registry = ToolCatalog.CreateDefaultRegistry();

    private static BenchCase GotoCase => new()
    {
        Name = "goto_case",
        User = "到 (120, 64, -40) 来",
        AcceptTools = ["goto"],
        ExpectArgs = new Dictionary<string, IReadOnlyList<string>>
        {
            ["x"] = ["120"],
            ["dig_through"] = ["true"],
        },
    };

    private static LlmResponse ToolCallResponse(string name, string argumentsJson) =>
        new("", [new ToolCall("c1", name, argumentsJson)]);

    [Fact]
    public void Judge_NoToolCall_FailsEverything()
    {
        var verdict = BenchScorer.Judge(GotoCase, new LlmResponse("我在路上了", []), Registry);

        Assert.False(verdict.Selection);
        Assert.False(verdict.ArgsValid);
        Assert.False(verdict.ArgsMatch);
        Assert.False(verdict.Pass);
    }

    [Fact]
    public void Judge_CorrectCall_PassesAllThree()
    {
        var verdict = BenchScorer.Judge(
            GotoCase,
            ToolCallResponse("goto", """{"x":120,"y":64,"z":-40,"dig_through":true}"""),
            Registry);

        Assert.True(verdict.Pass, verdict.Detail);
    }

    [Fact]
    public void Judge_MissingRequiredArg_IsInvalid()
    {
        var verdict = BenchScorer.Judge(
            GotoCase,
            ToolCallResponse("goto", """{"x":120,"y":64}"""),
            Registry);

        Assert.True(verdict.Selection);
        Assert.False(verdict.ArgsValid);
    }

    [Fact]
    public void Judge_WrongTool_FailsSelectionButStillValidatesArgs()
    {
        var verdict = BenchScorer.Judge(
            GotoCase,
            ToolCallResponse("teleport", """{"x":120,"y":64,"z":-40}"""),
            Registry);

        Assert.False(verdict.Selection);
        Assert.True(verdict.ArgsValid);
        Assert.False(verdict.ArgsMatch);
    }

    [Fact]
    public void Judge_LooseMatch_AcceptsContainment()
    {
        var benchCase = new BenchCase
        {
            Name = "mine",
            User = "挖煤",
            AcceptTools = ["mine_resource"],
            ExpectArgs = new Dictionary<string, IReadOnlyList<string>> { ["resource_name"] = ["煤"] },
        };

        var verdict = BenchScorer.Judge(
            benchCase,
            ToolCallResponse("mine_resource", """{"resource_name":"煤矿","count":5}"""),
            Registry);

        Assert.True(verdict.Pass, verdict.Detail);
    }

    [Fact]
    public void Judge_MalformedArguments_IsInvalid()
    {
        var verdict = BenchScorer.Judge(GotoCase, ToolCallResponse("goto", "not json"), Registry);

        Assert.True(verdict.Selection);
        Assert.False(verdict.ArgsValid);
        Assert.False(verdict.ArgsMatch);
    }

    [Fact]
    public void ShippedCasesFile_LoadsAndReferencesOnlyRealTools()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? casesPath = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "benchmark", "cases.json");
            if (File.Exists(candidate))
            {
                casesPath = candidate;
                break;
            }

            directory = directory.Parent;
        }

        Assert.NotNull(casesPath);
        var cases = BenchCase.LoadFile(casesPath);
        Assert.True(cases.Count >= 20, $"expected a real case set, found {cases.Count}");
        BenchCase.Validate(cases, Registry);
    }
}
