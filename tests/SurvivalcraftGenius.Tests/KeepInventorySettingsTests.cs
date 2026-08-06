using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class KeepInventorySettingsTests
{
    [Fact]
    public void Default_IsCompanionOnly()
    {
        Assert.Equal(GeniusSettings.KeepInventoryCompanion, new GeniusSettings().KeepInventoryMode);
    }

    [Theory]
    [InlineData("all", GeniusSettings.KeepInventoryAll)]
    [InlineData("ALL", GeniusSettings.KeepInventoryAll)]
    [InlineData(" off ", GeniusSettings.KeepInventoryOff)]
    [InlineData("companion", GeniusSettings.KeepInventoryCompanion)]
    [InlineData("", GeniusSettings.KeepInventoryCompanion)]
    [InlineData("nonsense", GeniusSettings.KeepInventoryCompanion)]
    public void Mode_NormalizesAndFallsBackSafely(string stored, string expected)
    {
        var settings = new GeniusSettings { KeepInventoryOnDeath = stored };

        Assert.Equal(expected, settings.KeepInventoryMode);
    }

    [Fact]
    public void Mode_SurvivesJsonRoundTrip()
    {
        var settings = new GeniusSettings { KeepInventoryOnDeath = GeniusSettings.KeepInventoryAll };

        var restored = GeniusSettings.FromJson(settings.ToJson());

        Assert.Equal(GeniusSettings.KeepInventoryAll, restored.KeepInventoryMode);
    }

    [Fact]
    public void LegacySettingsFile_WithoutTheField_DefaultsToCompanion()
    {
        var restored = GeniusSettings.FromJson("""{"ApiKey":"k","Model":"m"}""");

        Assert.Equal(GeniusSettings.KeepInventoryCompanion, restored.KeepInventoryMode);
    }
}
