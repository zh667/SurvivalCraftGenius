using Engine;
using Game;

namespace SurvivalcraftGenius.Npc;

/// <summary>Getting the body within working range of a cell.</summary>
public static class GeniusApproach
{
    /// <summary>Outcome of one <see cref="Step"/> tick.</summary>
    public enum Result
    {
        /// <summary>Close enough to work on it.</summary>
        InReach,

        /// <summary>Still walking; return null and try again next tick.</summary>
        Walking,

        /// <summary>Genuinely cannot get there after retrying.</summary>
        Unreachable,
    }

    /// <summary>
    /// Walks toward a cell and says whether it is workable yet.
    ///
    /// The reason this exists: <c>ComponentPathfinding.IsStuck</c> is a LATCHED
    /// flag. The engine clears it only in <c>Stop()</c> or on entering the
    /// MovingDirect state — so once an order sees it, every later check sees it
    /// too unless a fresh destination is issued. Orders that wrote a cell off on
    /// <c>IsStuck</c> and moved to the next one therefore wrote off the ENTIRE
    /// remaining plan the moment they got stuck once.
    ///
    /// Playtest 12: build_shelter reported "125 spots gave trouble: 走不到" for a
    /// 5x5x5 plan in eighteen seconds — it never walked anywhere. It had been
    /// stuck on the first cell.
    ///
    /// So a stuck report costs a retry, not the plan: clear the latch, re-issue
    /// the destination, and only give up on that cell after <paramref
    /// name="retries"/> genuine failures.
    /// </summary>
    public static Result Step(
        ComponentGeniusBrain brain,
        Vector3 target,
        float reach,
        ref int stuckCount,
        int retries = 2)
    {
        if (Vector3.Distance(brain.Creature.ComponentBody.Position, target) <= reach)
        {
            stuckCount = 0;
            return Result.InReach;
        }

        var pathfinding = brain.m_componentPathfinding;
        if (pathfinding.IsStuck)
        {
            if (++stuckCount > retries)
            {
                stuckCount = 0;
                pathfinding.Stop();
                return Result.Unreachable;
            }

            // Stop() is what clears the latch; without it the next SetDestination
            // still reads as stuck.
            pathfinding.Stop();
            WalkTowards(brain, target, 2.5f);
            return Result.Walking;
        }

        if (!pathfinding.Destination.HasValue)
        {
            WalkTowards(brain, target, 2.5f);
        }

        return Result.Walking;
    }


    private static void WalkTowards(ComponentGeniusBrain brain, Vector3 destination, float range) =>
        brain.m_componentPathfinding.SetDestination(
            destination, 1f, range, 2000,
            useRandomMovements: true, ignoreHeightDifference: false,
            raycastDestination: false, null!);
}
