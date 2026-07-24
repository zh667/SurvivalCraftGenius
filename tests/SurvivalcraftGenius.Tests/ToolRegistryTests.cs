using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class ToolRegistryTests
{
    private sealed record FakeTool(string Name) : IGeniusTool
    {
        public string Description => "fake";
        public string ParametersJsonSchema => "{\"type\":\"object\",\"properties\":{}}";
    }

    [Fact]
    public void Register_PreservesOrder_AndLooksUpByName()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("say"));
        registry.Register(new FakeTool("goto"));

        Assert.Equal(["say", "goto"], registry.Tools.Select(t => t.Name));
        Assert.True(registry.TryGet("goto", out var tool));
        Assert.Equal("goto", tool.Name);
    }

    [Fact]
    public void Register_RejectsDuplicatesAndEmptyNames()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("say"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeTool("say")));
        Assert.Throws<ArgumentException>(() => registry.Register(new FakeTool(" ")));
        Assert.False(registry.TryGet("missing", out _));
    }
}
