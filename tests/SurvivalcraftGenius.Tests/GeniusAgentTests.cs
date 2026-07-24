using System.Net;
using System.Net.Http;
using System.Text;
using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class GeniusAgentTests
{
    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = responses[Math.Min(_index++, responses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static GeniusSettings TestSettings => new()
    {
        ApiBaseUrl = "http://localhost/v1",
        ApiKey = "k",
        Model = "m",
        MaxToolSteps = 4,
        ToolTimeoutSeconds = 5,
    };

    [Fact]
    public async Task RunTurn_ExecutesToolThenSpeaks()
    {
        const string toolCallResponse = """
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"c1","type":"function","function":{"name":"scan_surroundings","arguments":"{}"}}]}}]}
            """;
        const string finalResponse = """
            {"choices":[{"message":{"content":"周围有一棵树。"}}]}
            """;
        var handler = new ScriptedHandler(toolCallResponse, finalResponse);
        var settings = TestSettings;
        using var client = new LlmClient(settings, handler);
        var executed = new List<string>();
        var events = new List<AgentEvent>();
        var agent = new GeniusAgent(
            client,
            ToolCatalog.CreateDefaultRegistry(),
            (name, _) =>
            {
                executed.Add(name);
                return Task.FromResult("""{"trees":1}""");
            },
            events.Add,
            settings);

        Assert.True(agent.TryBeginTurn());
        await agent.RunTurnAsync("看看周围", CancellationToken.None);

        Assert.Equal(["scan_surroundings"], executed);
        Assert.Contains(events, e => e.Kind == AgentEventKind.AssistantSaid && e.Text == "周围有一棵树。");
        Assert.Equal(AgentEventKind.TurnFinished, events[^1].Kind);
        Assert.False(agent.IsBusy);
        // Second request must carry the tool result back to the model
        // (the JSON payload string-escapes the embedded tool result).
        Assert.Contains("\\\"trees\\\":1", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task RunTurn_UnknownTool_ReportsErrorResult()
    {
        const string toolCallResponse = """
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"c1","type":"function","function":{"name":"fly_to_moon","arguments":"{}"}}]}}]}
            """;
        const string finalResponse = """{"choices":[{"message":{"content":"做不到。"}}]}""";
        var handler = new ScriptedHandler(toolCallResponse, finalResponse);
        var settings = TestSettings;
        using var client = new LlmClient(settings, handler);
        var agent = new GeniusAgent(
            client,
            ToolCatalog.CreateDefaultRegistry(),
            (_, _) => Task.FromResult("should not run"),
            _ => { },
            settings);

        Assert.True(agent.TryBeginTurn());
        await agent.RunTurnAsync("上月球", CancellationToken.None);

        Assert.Contains("unknown tool", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task RunTurn_HttpError_RaisesErrorEventAndFreesAgent()
    {
        var handler = new ScriptedHandler("""{"error":"boom"}""");
        var settings = TestSettings;
        using var client = new LlmClient(settings, handler);
        var events = new List<AgentEvent>();
        var agent = new GeniusAgent(
            client,
            ToolCatalog.CreateDefaultRegistry(),
            (_, _) => Task.FromResult(""),
            events.Add,
            settings);

        Assert.True(agent.TryBeginTurn());
        await agent.RunTurnAsync("hi", CancellationToken.None);

        Assert.Contains(events, e => e.Kind == AgentEventKind.Error);
        Assert.True(agent.TryBeginTurn());
    }
}
