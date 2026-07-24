using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class LlmClientTests
{
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
