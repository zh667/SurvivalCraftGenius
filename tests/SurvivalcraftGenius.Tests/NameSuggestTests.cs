using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public sealed class NameSuggestTests
{
    private static readonly string[] BlockNames =
        ["煤矿", "铁矿", "铜矿", "花岗岩", "玄武岩", "石块", "钻石块", "工作台", "锻造台"];

    [Fact]
    public void SuggestsContainingNameForShortQuery()
    {
        var ranked = NameSuggest.Rank("煤", BlockNames);
        Assert.Contains("煤矿", ranked);
    }

    [Fact]
    public void SuggestsCloseChineseNameForOneCharSlip()
    {
        var ranked = NameSuggest.Rank("煤块", BlockNames);
        Assert.NotEmpty(ranked);
        Assert.Contains(ranked, name => name.Contains('煤') || name.Contains('块'));
    }

    [Fact]
    public void SuggestsEnglishTypo()
    {
        var ranked = NameSuggest.Rank("cobelstone", ["cobblestone", "sandstone", "wood plank"]);
        Assert.Equal("cobblestone", ranked[0]);
    }

    [Fact]
    public void ExactMatchIsNotSuggestedBack()
    {
        Assert.DoesNotContain("铁矿", NameSuggest.Rank("铁矿", BlockNames));
    }

    [Fact]
    public void SilentWhenNothingIsClose()
    {
        Assert.Equal("", NameSuggest.Clause("zzzzz", BlockNames));
    }

    [Fact]
    public void ClauseFormatsSuggestions()
    {
        var clause = NameSuggest.Clause("铁", BlockNames, max: 1);
        Assert.Equal(" — did you mean '铁矿'?", clause);
    }

    [Fact]
    public void HandlesEmptyInputs()
    {
        Assert.Empty(NameSuggest.Rank("", BlockNames));
        Assert.Empty(NameSuggest.Rank("煤", []));
    }
}
