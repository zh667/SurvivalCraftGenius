using Engine;
using Game;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>Getting the body from here to there.</summary>
public static class MovementTools
{
    public static Task<string> Goto(GeniusToolContext context, JObject arguments)
    {
        var order = new GotoOrder(
            GeniusToolContext.ReadPoint(arguments),
            (bool?)arguments["dig_through"] ?? false);
        context.Brain.StartOrder(order);
        return order.Completion;
    }

    public static Task<string> FollowPlayer(GeniusToolContext context, JObject arguments)
    {
        context.Brain.StartFollowing(context.ComponentPlayer.ComponentBody);
        return Task.FromResult(
            "now following the player (ends when I start any new task; call follow_player again to resume)");
    }

    public static Task<string> DescendTo(GeniusToolContext context, JObject arguments)
    {
        if ((int?)arguments["y"] is not { } targetY)
        {
            return Task.FromResult("error[invalid_argument]: give the target depth as y");
        }

        var order = new DescendOrder(targetY, (string?)arguments["looking_for"]);
        context.Brain.StartOrder(order);
        return order.Completion;
    }

    /// <summary>
    /// Teleport, now with a price. It was free and unlimited, and its own
    /// description sold it as the fastest way down to an ore band — so the model
    /// used it as transport and every pathfinding failure stayed invisible
    /// behind it. Two limits, both cheap to explain to the model: a cooldown,
    /// and a refusal for short hops that legs can cover.
    /// </summary>
    public static Task<string> Teleport(GeniusToolContext context, JObject arguments)
    {
        var brain = context.Brain;
        Vector3 destination;
        var waypointName = (string?)arguments["waypoint_name"];
        if (!string.IsNullOrWhiteSpace(waypointName))
        {
            var waypoints = TravelMapBridge.TryReadWaypoints(context.ComponentPlayer);
            var match = waypoints?.FirstOrDefault(waypoint =>
                waypoint.Name.Contains(waypointName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return Task.FromResult($"error[not_found]: no waypoint matching '{waypointName}'");
            }

            destination = match.Position;
        }
        else if (GeniusToolContext.HasPoint(arguments))
        {
            var point = GeniusToolContext.ReadPoint(arguments);
            destination = new Vector3(point.X + 0.5f, point.Y, point.Z + 0.5f);
        }
        else
        {
            return Task.FromResult("error[invalid_argument]: give either waypoint_name or x/y/z");
        }

        var here = brain.Creature.ComponentBody.Position;
        var distance = Vector3.Distance(here, destination);
        if (distance < ComponentGeniusBrain.TeleportMinimumDistance)
        {
            return Task.FromResult(GeniusFailure.Format(FailureType.InvalidArgument,
                $"that is only {distance:0} blocks away — I walk that. Teleport is for getting " +
                "out of a hole or crossing a map, not for commuting; use goto"));
        }

        var elapsed = brain.GameTime - brain.LastTeleportTime;
        if (elapsed < ComponentGeniusBrain.TeleportCooldownSeconds)
        {
            var wait = ComponentGeniusBrain.TeleportCooldownSeconds - elapsed;
            return Task.FromResult(GeniusFailure.Format(FailureType.NotReady,
                $"I just teleported — it needs about {wait:0} more seconds. Walk (goto), " +
                "or do something else meanwhile"));
        }

        brain.StopMoving();
        var destCell = Terrain.ToCell(destination);
        var terrain = brain.SubsystemTerrain.Terrain;
        var loaded = GeniusTerrainReady.HasCells(terrain, destCell.X, destCell.Z);
        if (!loaded)
        {
            // Blind teleport killed the NPC twice (physics runs before terrain
            // exists). Hover in the sky; the brain snaps to the surface once the
            // expedition keeper loads the chunk.
            brain.LastTeleportTime = brain.GameTime;
            brain.PendingTeleportHover = new Vector3(destCell.X + 0.5f, 150f, destCell.Z + 0.5f);
            brain.PendingTeleportTarget = destCell;
            // Somewhere real to go back to if the area never generates — otherwise
            // the body stays pinned in the sky forever.
            brain.PendingTeleportReturn = brain.Creature.ComponentBody.Position;
            brain.Creature.ComponentBody.Position = brain.PendingTeleportHover.Value;
            brain.Creature.ComponentBody.Velocity = Vector3.Zero;
            return Task.FromResult(
                $"teleporting to ({destCell.X}, {destCell.Y}, {destCell.Z}) — the area is still " +
                "loading; I hover safely and drop to that spot within a few seconds " +
                "(underground targets included). Wait a moment, then scan before acting.");
        }

        var landing = GeniusTeleportLanding.Resolve(brain, destCell);
        if (landing.Error is { } landingError)
        {
            return Task.FromResult(landingError);
        }

        destination = landing.Position;
        brain.LastTeleportTime = brain.GameTime;
        brain.Creature.ComponentBody.Position = destination + new Vector3(0f, 0.5f, 0f);
        return Task.FromResult(
            $"teleported to ({(int)destination.X}, {(int)destination.Y}, {(int)destination.Z})" +
            landing.Note);
    }
}
