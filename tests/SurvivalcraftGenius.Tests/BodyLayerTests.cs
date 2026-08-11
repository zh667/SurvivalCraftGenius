using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The engine-free halves of the reflex layer: armour arithmetic and crop
/// ripeness. Both are transcriptions of engine behaviour, so the tests pin the
/// numbers the engine actually uses.
/// </summary>
public class ArmorMathTests
{
    [Theory]
    [InlineData(0.0f, GeniusArmorSlot.Feet)]
    [InlineData(0.09f, GeniusArmorSlot.Feet)]
    [InlineData(0.1f, GeniusArmorSlot.Legs)]
    [InlineData(0.29f, GeniusArmorSlot.Legs)]
    [InlineData(0.3f, GeniusArmorSlot.Torso)]
    [InlineData(0.89f, GeniusArmorSlot.Torso)]
    [InlineData(0.9f, GeniusArmorSlot.Head)]
    [InlineData(1.0f, GeniusArmorSlot.Head)]
    public void SlotForRoll_MatchesTheEnginesDistribution(float roll, GeniusArmorSlot expected)
    {
        Assert.Equal(expected, GeniusArmorMath.SlotForRoll(roll));
    }

    [Fact]
    public void SlotForRoll_TorsoIsTheLikeliestHit()
    {
        var torso = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (GeniusArmorMath.SlotForRoll(i / 1000f) == GeniusArmorSlot.Torso)
            {
                torso++;
            }
        }

        Assert.InRange(torso, 550, 650);
    }

    [Fact]
    public void AbsorbCapacity_FallsAsThePieceWearsOut()
    {
        var fresh = GeniusArmorMath.AbsorbCapacity(sturdiness: 10f, damage: 0, maxDurability: 20f);
        var halfWorn = GeniusArmorMath.AbsorbCapacity(sturdiness: 10f, damage: 10, maxDurability: 20f);
        var spent = GeniusArmorMath.AbsorbCapacity(sturdiness: 10f, damage: 20, maxDurability: 20f);

        Assert.Equal(10f, fresh, 3);
        Assert.Equal(5f, halfWorn, 3);
        Assert.Equal(0f, spent, 3);
    }

    [Fact]
    public void Absorbed_IsCappedByBothRatingAndRemainingDurability()
    {
        // Rating would stop 3 of a 10-power blow, but the battered piece can
        // only take 1 more point of punishment.
        Assert.Equal(1f, GeniusArmorMath.Absorbed(10f, 0.3f, capacity: 1f), 3);
        // Fresh piece: the rating is what binds.
        Assert.Equal(3f, GeniusArmorMath.Absorbed(10f, 0.3f, capacity: 99f), 3);
    }

    [Fact]
    public void Absorbed_NeverExceedsTheBlowAndNeverGoesNegative()
    {
        Assert.Equal(0f, GeniusArmorMath.Absorbed(0f, 1f, 99f), 3);
        Assert.Equal(5f, GeniusArmorMath.Absorbed(5f, 2f, 99f), 3);
        Assert.Equal(0f, GeniusArmorMath.Absorbed(5f, -1f, 99f), 3);
    }

    [Fact]
    public void DurabilityCost_ScalesWithWhatWasAbsorbed()
    {
        var light = GeniusArmorMath.DurabilityCost(1f, sturdiness: 10f, maxDurability: 20f);
        var heavy = GeniusArmorMath.DurabilityCost(5f, sturdiness: 10f, maxDurability: 20f);

        Assert.Equal(2.001f, light, 3);
        Assert.Equal(10.001f, heavy, 3);
    }

    [Fact]
    public void DurabilityCost_IsZeroForSomethingThatCannotProtect()
    {
        Assert.Equal(0f, GeniusArmorMath.DurabilityCost(5f, sturdiness: 0f, maxDurability: 20f), 3);
    }
}

public class HarvestRulesTests
{
    [Theory]
    // Planted rye: 5 and 6 drop seed, only 7 drops grain — cutting early is a
    // strictly worse trade, since the plant is consumed either way.
    [InlineData(GeniusHarvestRules.RyeContents, 6, false, false)]
    [InlineData(GeniusHarvestRules.RyeContents, 7, false, true)]
    // Wild rye has no grain stage at all; > 2 is where it starts dropping.
    [InlineData(GeniusHarvestRules.RyeContents, 2, true, false)]
    [InlineData(GeniusHarvestRules.RyeContents, 3, true, true)]
    // Cotton's own check is size == 2, and 2 is its maximum.
    [InlineData(GeniusHarvestRules.CottonContents, 1, false, false)]
    [InlineData(GeniusHarvestRules.CottonContents, 2, false, true)]
    // Pumpkins drop at any size but only feed anyone at 7.
    [InlineData(GeniusHarvestRules.PumpkinContents, 6, false, false)]
    [InlineData(GeniusHarvestRules.PumpkinContents, 7, false, true)]
    public void IsRipe_MatchesEachBlocksOwnDropRule(
        int contents, int size, bool isWild, bool expected)
    {
        Assert.Equal(expected, GeniusHarvestRules.IsRipe(contents, size, isWild));
    }

    [Fact]
    public void IsRipe_IsFalseForAnythingThatIsNotACrop()
    {
        Assert.False(GeniusHarvestRules.IsRipe(contents: 2, size: 7, isWild: false));
        Assert.False(GeniusHarvestRules.IsCrop(2));
    }

    [Fact]
    public void WildAndPlantedRyeDoNotShareAThreshold()
    {
        // A wild rye at 3 is worth cutting; a planted one at 3 is not. Treating
        // them alike would have the companion mow down half-grown fields.
        Assert.True(GeniusHarvestRules.IsRipe(GeniusHarvestRules.RyeContents, 3, isWild: true));
        Assert.False(GeniusHarvestRules.IsRipe(GeniusHarvestRules.RyeContents, 3, isWild: false));
    }

    [Fact]
    public void NotRipeReason_NamesTheCropAndHowFarOffItIs()
    {
        var reason = GeniusHarvestRules.NotRipeReason(GeniusHarvestRules.CottonContents, 1, false);

        Assert.Contains("棉花", reason);
        Assert.Contains("1", reason);
    }
}
