using Engine;
using Game;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>Picking a fight, and refusing the ones that would kill me.</summary>
public static class CombatTools
{
    private const float SearchRange = 24f;

    public static Task<string> Attack(GeniusToolContext context, JObject arguments)
    {
        var brain = context.Brain;
        var query = (string?)arguments["target_name"] ?? "";
        var target = FindTarget(context, query);
        if (target is null)
        {
            var nearby = NearbyNames(context, SearchRange);
            var suggestions = NameSuggest.Clause(query, nearby);
            var listing = nearby.Count == 0
                ? "no creatures are within 24m at all — note: wildlife spawns " +
                  "periodically (around players, and around me on expeditions); if I " +
                  "just arrived, wait ~1 minute or move on — this spot may also simply " +
                  "be barren"
                : $"nearby creatures: {string.Join(", ", nearby)}";
            return Task.FromResult(
                $"error[not_found]: no creature matching '{query}' within 24m{suggestions}; {listing}");
        }

        // Weapon preflight: the NPC charged a bison (resilience 75) bare-handed
        // in playtest 3 and was trampled to death. Big game demands a real melee
        // weapon; small game is fine.
        var resilience = target.ComponentHealth.AttackResilience;
        var bestMelee = 1f;
        if (brain.Miner.Inventory is { } attackInventory)
        {
            for (var slot = 0; slot < attackInventory.SlotsCount; slot++)
            {
                var slotValue = attackInventory.GetSlotValue(slot);
                if (slotValue != 0)
                {
                    bestMelee = Math.Max(bestMelee, BlocksManager.Blocks[
                        Terrain.ExtractContents(slotValue)].GetMeleePower(slotValue));
                }
            }
        }

        if (resilience >= 50f && bestMelee < 3f)
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.ToolTooWeak,
                $"{target.DisplayName} is big game (resilience {resilience:0}) and my best " +
                $"melee power is only {bestMelee:0.#} — charging it would take dozens of hits " +
                "while it tramples me (this killed me before). Craft/get a real weapon " +
                "(剑/砍刀, melee_power ≥3) first, or pick smaller prey"));
        }

        var sneak = arguments["sneak"]?.ToObject<bool>() ?? false;
        var order = new AttackOrder(target, sneak);
        brain.StartOrder(order);
        return order.Completion;
    }

    /// <summary>
    /// Grounded first, then nearest. We only have melee, so an airborne duck is
    /// not a target, it is a 45-second wait — and playtest 14 spent two of them
    /// in a row on flying ducks while a landed bird stood nearby.
    /// </summary>
    internal static ComponentCreature? FindTarget(GeniusToolContext context, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var brain = context.Brain;
        ComponentCreature? best = null;
        var bestScore = float.MaxValue;
        foreach (var body in context.SubsystemBodies.Bodies)
        {
            var creature = body.Entity.FindComponent<ComponentCreature>();
            if (creature is null
                || creature is ComponentPlayer
                || creature == brain.Creature
                || creature.ComponentHealth.Health <= 0f
                || !creature.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var distance = Vector3.Distance(body.Position, brain.Creature.ComponentBody.Position);
            if (distance > SearchRange)
            {
                continue;
            }

            var airborne = !body.StandingOnValue.HasValue && body.ImmersionFactor <= 0f;
            var score = distance + (airborne ? 1000f : 0f);
            if (score < bestScore)
            {
                best = creature;
                bestScore = score;
            }
        }

        return best;
    }

    internal static List<string> NearbyNames(GeniusToolContext context, float range)
    {
        var brain = context.Brain;
        var names = new HashSet<string>();
        foreach (var body in context.SubsystemBodies.Bodies)
        {
            var creature = body.Entity.FindComponent<ComponentCreature>();
            if (creature is null
                || creature is ComponentPlayer
                || creature == brain.Creature
                || creature.ComponentHealth.Health <= 0f
                || Vector3.Distance(body.Position, brain.Creature.ComponentBody.Position) > range)
            {
                continue;
            }

            names.Add(creature.DisplayName);
        }

        return [.. names];
    }
}
