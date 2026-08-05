using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class GeniusFailureTests
{
    [Fact]
    public void Format_ProducesTaggedError()
    {
        Assert.Equal(
            "error[not_found]: no chest at (1, 2, 3)",
            GeniusFailure.Format(FailureType.NotFound, "no chest at (1, 2, 3)"));
    }

    [Fact]
    public void Slugs_AreUniqueAndStable()
    {
        var slugs = Enum.GetValues<FailureType>().Select(GeniusFailure.Slug).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(slugs, slug => Assert.Matches("^[a-z_]+$", slug));
    }

    [Fact]
    public void TryParse_RoundTripsEveryCategory()
    {
        foreach (var type in Enum.GetValues<FailureType>())
        {
            var formatted = GeniusFailure.Format(type, "message");
            Assert.Equal(type, GeniusFailure.TryParse(formatted));
        }
    }

    [Fact]
    public void TryParse_FindsTagEmbeddedInPartialSuccess()
    {
        Assert.Equal(
            FailureType.Timeout,
            GeniusFailure.TryParse(
                "mined and collected: 煤 x3; error[timeout]: ran out of time"));
    }

    [Theory]
    [InlineData("arrived at (1, 2, 3)")]
    [InlineData("error: legacy untagged prose")]
    [InlineData("error[made_up_slug]: unknown category")]
    [InlineData("error[broken tag with no close")]
    public void TryParse_ReturnsNullForUntaggedOrUnknown(string result)
    {
        Assert.Null(GeniusFailure.TryParse(result));
    }

    [Theory]
    [InlineData("error[no_path]: blocked", true)]
    [InlineData("error: legacy", true)]
    [InlineData("now following the player", false)]
    public void IsError_CoversTaggedAndLegacy(string result, bool expected)
    {
        Assert.Equal(expected, GeniusFailure.IsError(result));
    }
}
