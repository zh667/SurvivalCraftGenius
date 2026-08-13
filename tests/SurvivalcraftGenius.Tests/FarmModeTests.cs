using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The standing farm mode's priority rule, ported from 工具人. This is the
/// half that decides; keeping it engine-free is what lets it be tested at all,
/// since the loop it replaces could only ever be checked by playing.
/// </summary>
public class FarmModeTests
{
    private static FarmSnapshot World(
        bool pickable = false,
        bool ripe = false,
        bool bare = false,
        bool seeds = false,
        bool full = false,
        bool attacked = false) =>
        new(pickable, ripe, bare, seeds, full, attacked);

    [Fact]
    public void NothingToDoIsIdle()
    {
        Assert.Equal(FarmAction.Idle, GeniusFarmMode.Decide(World()));
    }

    /// <summary>Drops despawn; ripe crops do not. Pick up first.</summary>
    [Fact]
    public void DropsOutrankEverything()
    {
        Assert.Equal(
            FarmAction.PickUp,
            GeniusFarmMode.Decide(World(pickable: true, ripe: true, bare: true, seeds: true)));
    }

    [Fact]
    public void RipeCropsOutrankReplanting()
    {
        Assert.Equal(
            FarmAction.Harvest,
            GeniusFarmMode.Decide(World(ripe: true, bare: true, seeds: true)));
    }

    [Fact]
    public void BareFarmlandIsPlantedLast()
    {
        Assert.Equal(FarmAction.Plant, GeniusFarmMode.Decide(World(bare: true, seeds: true)));
    }

    [Fact]
    public void BareFarmlandWithoutSeedsIsNotPlantable()
    {
        Assert.Equal(FarmAction.Idle, GeniusFarmMode.Decide(World(bare: true)));
    }

    /// <summary>A full bag makes picking up pointless — move on to the crops.</summary>
    [Fact]
    public void AFullBagSkipsPickupButStillHarvests()
    {
        Assert.Equal(
            FarmAction.Harvest,
            GeniusFarmMode.Decide(World(pickable: true, ripe: true, full: true)));
    }

    /// <summary>
    /// Combat outbids the whole mode. 工具人 does this by yielding whenever its
    /// chase behaviour holds a target, and the reason is simple: a body being
    /// bitten has no business planting.
    /// </summary>
    [Fact]
    public void BeingAttackedStopsFarmingEntirely()
    {
        Assert.Equal(
            FarmAction.Idle,
            GeniusFarmMode.Decide(
                World(pickable: true, ripe: true, bare: true, seeds: true, attacked: true)));
    }

    [Theory]
    [InlineData(true, false, false, "袭击")]
    [InlineData(false, true, true, "背包满")]
    [InlineData(false, false, false, "守着")]
    public void IdlingExplainsItself(bool attacked, bool full, bool pickable, string expected)
    {
        var reason = GeniusFarmMode.ExplainIdle(
            World(pickable: pickable, full: full, attacked: attacked));

        Assert.Contains(expected, reason);
    }

    /// <summary>The fix for "有空地却不种" is one sentence to the player.</summary>
    [Fact]
    public void MissingSeedsIsCalledOutByName()
    {
        Assert.Contains("种子", GeniusFarmMode.ExplainIdle(World(bare: true)));
    }
}
