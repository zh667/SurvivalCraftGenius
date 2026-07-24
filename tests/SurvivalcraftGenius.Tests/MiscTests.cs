using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.UI;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class SettingsTests
{
    [Fact]
    public void Json_RoundTrips()
    {
        var settings = new GeniusSettings
        {
            ApiBaseUrl = "https://example.com/v1/",
            ApiKey = "secret",
            Model = "qwen-max",
            MaxToolSteps = 5,
        };

        var restored = GeniusSettings.FromJson(settings.ToJson());

        Assert.Equal("https://example.com/v1/", restored.ApiBaseUrl);
        Assert.Equal("secret", restored.ApiKey);
        Assert.Equal("qwen-max", restored.Model);
        Assert.Equal(5, restored.MaxToolSteps);
        Assert.Equal("https://example.com/v1/chat/completions", restored.ChatCompletionsUrl);
    }

    [Fact]
    public void EmptyKey_MeansNotConfigured()
    {
        Assert.False(new GeniusSettings().IsConfigured);
        Assert.True(new GeniusSettings { ApiKey = "x" }.IsConfigured);
    }
}

public class TextWrapperTests
{
    [Fact]
    public void ShortAsciiText_SingleLine()
    {
        Assert.Equal(["hello"], TextWrapper.Wrap("hello", 44).ToList());
    }

    [Fact]
    public void CjkText_CountsDoubleWidth()
    {
        var lines = TextWrapper.Wrap(new string('中', 50), 20).ToList();
        Assert.All(lines, line => Assert.True(line.Length <= 20));
        Assert.Equal(50, lines.Sum(l => l.Length));
    }

    [Fact]
    public void Newlines_AreFlattened()
    {
        Assert.Equal(["a b"], TextWrapper.Wrap("a\nb", 44).ToList());
    }
}
