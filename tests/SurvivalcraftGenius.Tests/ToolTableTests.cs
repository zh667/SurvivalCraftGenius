using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.Mod.Tools;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// Guards the split of the old 421-line dispatch switch into per-domain
/// handlers. The failure this is really here to catch is a tool silently
/// losing its implementation during the move: the catalog still advertises it
/// to the model, the table no longer answers, and the only symptom is an
/// "unknown tool" mid-playtest.
/// </summary>
public class ToolTableTests
{
    [Fact]
    public void EveryAdvertisedToolHasAHandler()
    {
        var advertised = ToolCatalog.CreateDefaultRegistry().Tools.Select(tool => tool.Name);
        var missing = advertised.Where(name => GeniusToolTable.Resolve(name) is null).ToList();
        Assert.True(missing.Count == 0,
            "advertised to the model but not implemented: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryHandlerIsAdvertised()
    {
        var advertised = ToolCatalog.CreateDefaultRegistry().Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var orphans = GeniusToolTable.Names.Where(name => !advertised.Contains(name)).ToList();
        Assert.True(orphans.Count == 0,
            "implemented but never offered to the model: " + string.Join(", ", orphans));
    }

    [Fact]
    public void UnknownToolResolvesToNothing()
    {
        Assert.Null(GeniusToolTable.Resolve("teleport_to_the_moon"));
        Assert.Null(GeniusToolTable.Resolve(""));
    }

    /// <summary>
    /// Case drift (<c>Goto</c> for <c>goto</c>) is the LLM typo that actually
    /// happens; Numen repairs it rather than failing the turn, and so do we.
    /// </summary>
    [Theory]
    [InlineData("Goto", "goto")]
    [InlineData("ATTACK", "attack")]
    [InlineData("Take_From_Chest", "take_from_chest")]
    public void CaseDriftStillFindsTheTool(string typo, string canonical)
    {
        Assert.NotNull(GeniusToolTable.Resolve(typo));
        Assert.Same(GeniusToolTable.Resolve(canonical), GeniusToolTable.Resolve(typo));
    }

    /// <summary>
    /// Bookkeeping and knowledge answer without a body; everything that touches
    /// the world must not. todowrite is here on purpose — a plan is worth
    /// keeping before the companion is even summoned.
    /// </summary>
    [Fact]
    public void OnlyKnowledgeChatAndPlanningRunWithoutTheCompanion()
    {
        Assert.Equal(
            new[] { "query_help", "query_recipes", "read_knowledge", "say", "task_status", "task_stop", "todowrite" },
            GeniusToolTable.WorksWithoutBrain.OrderBy(name => name, StringComparer.Ordinal));

        Assert.False(GeniusToolTable.NeedsBrain("say"));
        Assert.False(GeniusToolTable.NeedsBrain("todowrite"));
        Assert.False(GeniusToolTable.NeedsBrain("Read_Knowledge"));
        Assert.True(GeniusToolTable.NeedsBrain("goto"));
        Assert.True(GeniusToolTable.NeedsBrain("attack"));
    }
}
