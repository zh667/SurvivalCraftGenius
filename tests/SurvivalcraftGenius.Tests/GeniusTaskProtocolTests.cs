using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class GeniusTaskProtocolTests
{
    [Fact]
    public void Accept_MarksAsyncAndForbidsPolling()
    {
        var text = GeniusTaskProtocol.Accept(12, "mine_resource");
        Assert.Contains("task #12", text, StringComparison.Ordinal);
        Assert.Contains("mine_resource", text, StringComparison.Ordinal);
        Assert.Contains("async=true", text, StringComparison.Ordinal);
        Assert.DoesNotContain("standing=true", text, StringComparison.Ordinal);
        Assert.Contains("do not poll", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task_finished", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StandingAccept_WarnsThereWillBeNoEvent()
    {
        var text = GeniusTaskProtocol.Accept(0, "tend_farm", standing: true);
        Assert.Contains("standing=true", text, StringComparison.Ordinal);
        Assert.Contains("will not send task_finished", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mined 8 铁矿 and walked back", "done")]
    [InlineData("error[timeout]: ran out of time — 3 of 8", "timeout")]
    [InlineData("error[superseded]: stopped on request", "stopped")]
    [InlineData("error[superseded]: superseded by a newer order", "interrupted")]
    [InlineData("error[not_found]: no 铁矿 nearby", "failed")]
    [InlineData(null, "done")]
    public void StatusOf_MapsGeniusFailures(string? result, string expected)
    {
        Assert.Equal(expected, GeniusTaskProtocol.StatusOf(result));
    }

    [Fact]
    public void FinishedEvent_IsSingleLineMarkupThePromptCanName()
    {
        var ev = GeniusTaskProtocol.FinishedEvent(3, "goto", "arrived at (10, 64, 10)\nand stood");
        Assert.StartsWith("<event kind=\"task_finished\"", ev, StringComparison.Ordinal);
        Assert.Contains("id=\"3\"", ev, StringComparison.Ordinal);
        Assert.Contains("tool=\"goto\"", ev, StringComparison.Ordinal);
        Assert.Contains("status=\"done\"", ev, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", ev, StringComparison.Ordinal);
        Assert.EndsWith("</event>", ev, StringComparison.Ordinal);
    }
}
