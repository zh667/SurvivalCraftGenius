using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class LlmClientTests
{
    private const string OkBody = """{"choices":[{"message":{"content":"ok"}}]}""";

    /// <summary>Serves the scripted responses in order, repeating the last one.</summary>
    private sealed class SequenceHandler(params (int Status, string Body)[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = responses[Math.Min(Requests, responses.Length - 1)];
            Requests++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static LlmClient CreateClient(SequenceHandler handler) =>
        new(new GeniusSettings { ApiKey = "key" }, handler) { RetryDelay = TimeSpan.Zero };

    [Fact]
    public async Task CompleteAsync_RetriesTransientServerErrors()
    {
        var handler = new SequenceHandler((500, "boom"), (429, "slow down"), (200, OkBody));
        using var client = CreateClient(handler);

        var response = await client.CompleteAsync([ChatMessage.User("hi")], [], CancellationToken.None);

        Assert.Equal("ok", response.Content);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotRetryClientErrors()
    {
        var handler = new SequenceHandler((401, "bad key"));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<LlmException>(
            () => client.CompleteAsync([ChatMessage.User("hi")], [], CancellationToken.None));

        Assert.Contains("401", exception.Message);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_GivesUpAfterMaxAttempts()
    {
        var handler = new SequenceHandler((503, "still down"));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<LlmException>(
            () => client.CompleteAsync([ChatMessage.User("hi")], [], CancellationToken.None));

        Assert.Contains("503", exception.Message);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public void BuildPayload_SerializesMessagesAndTools()
    {
        var registry = ToolCatalog.CreateDefaultRegistry();
        var messages = new[]
        {
            ChatMessage.System("sys"),
            ChatMessage.User("hello"),
            ChatMessage.Assistant("", [new ToolCall("call_1", "scan_surroundings", "{}")]),
            ChatMessage.ToolResult("call_1", "{\"my_pos\":[1,2,3]}"),
        };

        var payload = LlmClient.BuildPayload(messages, registry.Tools, "test-model");

        Assert.Equal("test-model", (string?)payload["model"]);
        var serialized = (JArray)payload["messages"]!;
        Assert.Equal(4, serialized.Count);
        Assert.Equal("system", (string?)serialized[0]["role"]);
        Assert.Equal("scan_surroundings", (string?)serialized[2]["tool_calls"]?[0]?["function"]?["name"]);
        Assert.Equal("call_1", (string?)serialized[3]["tool_call_id"]);
        Assert.Equal(registry.Tools.Count, ((JArray)payload["tools"]!).Count);
        Assert.All(
            ((JArray)payload["tools"]!).Select(t => t["function"]?["parameters"]),
            parameters => Assert.Equal("object", (string?)parameters?["type"]));
    }

    [Fact]
    public async Task CompleteAsync_SendsCompactJson()
    {
        // JObject.ToString() defaults to Formatting.Indented — sending that costs
        // ~1.9k tokens per step in whitespace alone, on every step of every task.
        string? sent = null;
        var handler = new CapturingHandler(body => sent = body);
        using var client = new LlmClient(new GeniusSettings { ApiKey = "key" }, handler);

        await client.CompleteAsync(
            [ChatMessage.User("hi")],
            ToolCatalog.CreateDefaultRegistry().Tools,
            CancellationToken.None);

        Assert.NotNull(sent);
        Assert.DoesNotContain('\n', sent);
    }

    private sealed class CapturingHandler(Action<string> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OkBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void BuildPayload_WithoutCaching_LeavesContentAsPlainStrings()
    {
        var payload = LlmClient.BuildPayload(
            [ChatMessage.System("sys"), ChatMessage.User("hello")], [], "test-model");

        var messages = (JArray)payload["messages"]!;
        Assert.All(messages, message => Assert.Equal(JTokenType.String, message["content"]!.Type));
    }

    [Fact]
    public void BuildPayload_WithCaching_MarksSystemPrefixAndNewestMessage()
    {
        var messages = new[]
        {
            ChatMessage.System("sys"),
            ChatMessage.User("hello"),
            ChatMessage.Assistant("", [new ToolCall("call_1", "scan_surroundings", "{}")]),
            ChatMessage.ToolResult("call_1", "result"),
        };

        var serialized = (JArray)LlmClient.BuildPayload(messages, [], "claude", true)["messages"]!;

        // The system prompt (which the tool schemas share a prefix with) and the
        // newest message: everything between them rides along in the same block.
        Assert.Equal("ephemeral", (string?)serialized[0]["content"]?[0]?["cache_control"]?["type"]);
        Assert.Equal("ephemeral", (string?)serialized[3]["content"]?[0]?["cache_control"]?["type"]);
        Assert.Equal("sys", (string?)serialized[0]["content"]?[0]?["text"]);
        Assert.Equal(JTokenType.String, serialized[1]["content"]!.Type);
    }

    [Fact]
    public void BuildPayload_WithCaching_SkipsAssistantToolCallMessages()
    {
        // An assistant tool_calls message is serialized through the tool_calls
        // branch, which has nowhere to hang a marker — so it must not be chosen
        // as the newest markable message either, or the breakpoint vanishes and
        // the history silently stops being cached. Its content is non-empty here
        // on purpose: that is the case where the two rules can disagree.
        var messages = new[]
        {
            ChatMessage.System("sys"),
            ChatMessage.User("hello"),
            ChatMessage.Assistant("thinking out loud", [new ToolCall("call_1", "goto", "{}")]),
        };

        var serialized = (JArray)LlmClient.BuildPayload(messages, [], "claude", true)["messages"]!;

        Assert.Equal("ephemeral", (string?)serialized[1]["content"]?[0]?["cache_control"]?["type"]);
        Assert.Equal(JTokenType.String, serialized[2]["content"]!.Type);
    }

    [Fact]
    public async Task CompleteAsync_DropsCacheMarkersWhenTheGatewayRejectsThem()
    {
        var handler = new SequenceHandler(
            (400, """{"error":{"message":"unsupported field: cache_control"}}"""),
            (200, OkBody));
        using var client = new LlmClient(
            new GeniusSettings { ApiKey = "key", Model = "claude-opus-5" }, handler)
        { RetryDelay = TimeSpan.Zero };

        var response = await client.CompleteAsync(
            [ChatMessage.System("sys"), ChatMessage.User("hi")], [], CancellationToken.None);

        Assert.Equal("ok", response.Content);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData("auto", "claude-opus-5", true)]
    [InlineData("auto", "gpt-5.6-sol", false)]
    [InlineData("on", "gpt-5.6-sol", true)]
    [InlineData("off", "claude-opus-5", false)]
    public void UsePromptCache_FollowsSettingThenModelFamily(string mode, string model, bool expected)
    {
        var settings = new GeniusSettings { PromptCache = mode, Model = model };

        Assert.Equal(expected, settings.UsePromptCache);
    }

    [Fact]
    public void ParseResponse_ReadsContentAndToolCalls()
    {
        const string body = """
            {"choices":[{"message":{
              "content":null,
              "tool_calls":[{"id":"abc","type":"function",
                "function":{"name":"goto","arguments":"{\"x\":1,\"y\":64,\"z\":-3}"}}]}}]}
            """;

        var response = LlmClient.ParseResponse(body);

        Assert.True(response.HasToolCalls);
        Assert.Equal("goto", response.ToolCalls[0].Name);
        Assert.Equal("abc", response.ToolCalls[0].Id);
        Assert.Contains("\"x\":1", response.ToolCalls[0].ArgumentsJson);
        Assert.Equal("", response.Content);
    }

    [Fact]
    public void ParseResponse_PlainText()
    {
        const string body = """{"choices":[{"message":{"content":"你好呀"}}]}""";
        var response = LlmClient.ParseResponse(body);
        Assert.False(response.HasToolCalls);
        Assert.Equal("你好呀", response.Content);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    public void ParseResponse_BadBodies_Throw(string body)
    {
        Assert.Throws<LlmException>(() => LlmClient.ParseResponse(body));
    }
}
