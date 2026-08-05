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
