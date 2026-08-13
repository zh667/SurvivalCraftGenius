using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The knowledge table of contents pinned into the system prompt. Without it
/// the model had to burn a whole round trip on read_knowledge with no topic
/// just to learn which guides exist.
/// </summary>
public class KnowledgeIndexTests
{
    [Fact]
    public void IndexIsWrappedAndTellsTheModelItIsOnlyAListing()
    {
        var wrapped = GeniusAgent.WrapKnowledgeIndex("种田: 耕地与作物; 挖矿与工具: 矿层与工具");

        Assert.StartsWith("<knowledge_files>", wrapped);
        Assert.EndsWith("</knowledge_files>", wrapped);
        Assert.Contains("种田", wrapped);
        Assert.Contains("read_knowledge", wrapped);
        // The point of the index is fewer lookups, not more.
        Assert.Contains("Do not look one up before every action", wrapped);
    }

    [Fact]
    public void SurroundingWhitespaceDoesNotLeakIn()
    {
        var wrapped = GeniusAgent.WrapKnowledgeIndex("\n\n  种田: 耕地  \n\n");

        Assert.DoesNotContain("\n\n\n", wrapped);
        Assert.Contains("种田: 耕地", wrapped);
    }

    /// <summary>
    /// A world with no knowledge folder must not paste an index section — least
    /// of all an error string — into the prompt that rides on every request.
    /// </summary>
    [Fact]
    public void AgentWithoutAnIndexKeepsThePromptUntouched()
    {
        var withNothing = BuildPrompt(knowledgeIndex: null);
        var withBlank = BuildPrompt(knowledgeIndex: "   ");

        Assert.DoesNotContain("<knowledge_files>", withNothing);
        Assert.DoesNotContain("<knowledge_files>", withBlank);
        Assert.Equal(GeniusAgent.DefaultSystemPrompt, withNothing);
    }

    [Fact]
    public void AgentWithAnIndexCarriesItAfterTheRules()
    {
        var prompt = BuildPrompt("种田: 耕地与作物");

        Assert.Contains("<knowledge_files>", prompt);
        Assert.Contains("种田: 耕地与作物", prompt);
        Assert.True(
            prompt.IndexOf("<knowledge_files>", StringComparison.Ordinal)
                > prompt.IndexOf("<failures>", StringComparison.Ordinal),
            "the index belongs after the rules, not in front of them");
    }

    private static string BuildPrompt(string? knowledgeIndex)
    {
        var settings = new GeniusSettings
        {
            ApiBaseUrl = "http://localhost/v1",
            ApiKey = "k",
            Model = "m",
        };
        using var client = new LlmClient(settings, new SilentHandler());
        var agent = new GeniusAgent(
            client,
            ToolCatalog.CreateDefaultRegistry(),
            (_, _) => Task.FromResult("ok"),
            _ => { },
            settings,
            knowledgeIndex: knowledgeIndex);
        return agent.SystemPrompt;
    }

    /// <summary>Never called — the prompt is composed in the constructor.</summary>
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("this test never sends a request");
    }
}
