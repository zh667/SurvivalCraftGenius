using Engine;
using Game;
using Newtonsoft.Json.Linq;
using SurvivalcraftGenius.Npc;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>Looking at the world without changing it.</summary>
public static class PerceptionTools
{
    public static Task<string> ScanSurroundings(GeniusToolContext context, JObject arguments) =>
        Task.FromResult(GeniusPerception.ScanSurroundings(
            context.Brain, context.ComponentPlayer.ComponentBody));

    public static Task<string> LookAround(GeniusToolContext context, JObject arguments)
    {
        var brain = context.Brain;
        var radius = (int?)arguments["radius"] ?? GeniusLookAround.DefaultRadius;
        var (navWorld, _) = Npc.Nav.ScNavWorld.Capture(brain, allowDigging: false);
        var terrain = brain.SubsystemTerrain.Terrain;
        var forward = brain.Creature.ComponentBody.Matrix.Forward;
        // Sun-verified compass (TravelMap lesson): east=-X, north=+Z.
        var facing = Math.Abs(forward.X) >= Math.Abs(forward.Z)
            ? forward.X < 0 ? "东(x减)" : "西(x增)"
            : forward.Z > 0 ? "北(z增)" : "南(z减)";
        return Task.FromResult(GeniusLookAround.Render(
            navWorld,
            Terrain.ToCell(brain.Creature.ComponentBody.Position),
            Terrain.ToCell(context.ComponentPlayer.ComponentBody.Position),
            radius,
            facing,
            (x, z) => GeniusTerrainReady.HasCells(terrain, x, z)));
    }

    public static Task<string> GetInventory(GeniusToolContext context, JObject arguments) =>
        Task.FromResult(GeniusPerception.DescribeInventory(context.Brain));

    public static Task<string> FindBlocks(GeniusToolContext context, JObject arguments) =>
        Task.FromResult(GeniusPerception.FindBlocks(
            context.Brain,
            (string?)arguments["name"] ?? "",
            (int?)arguments["radius"] ?? 32));

    public static Task<string> FindBuildSite(GeniusToolContext context, JObject arguments)
    {
        var siteWidth = (int?)arguments["width"] ?? 5;
        var siteLength = (int?)arguments["length"] ?? 5;
        var forFarm = string.Equals(
            (string?)arguments["purpose"], "farm", StringComparison.OrdinalIgnoreCase);
        var searchRadius = Math.Clamp((int?)arguments["radius"] ?? 16, 2, 48);
        var found = GeniusSiteSurvey.FindBest(
            context.Brain, Math.Clamp(siteWidth, 1, 16), Math.Clamp(siteLength, 1, 16),
            searchRadius, forFarm);
        return Task.FromResult(found is { } site
            ? $"best {siteWidth}x{siteLength} {(forFarm ? "farm" : "build")} site: " +
              $"({site.Origin.X},{site.GroundY},{site.Origin.Z}) — {site.Note}, " +
              $"光照{site.Light}. Use exactly this x/y/z"
            : $"error[not_found]: no {siteWidth}x{siteLength} spot within {searchRadius}m is " +
              (forFarm
                  ? "flat soil in daylight — farmland needs grass/dirt ground and light>=9; " +
                    "try a smaller plot, or move to open grassland"
                  : "flat and solid enough to build on — try a smaller footprint or move me"));
    }

    public static Task<string> ListWaypoints(GeniusToolContext context, JObject arguments)
    {
        var waypoints = TravelMapBridge.TryReadWaypoints(context.ComponentPlayer);
        if (waypoints is null)
        {
            return Task.FromResult(
                "error[unavailable]: TravelMap mod is not installed (or has no data yet)");
        }

        if (waypoints.Count == 0)
        {
            return Task.FromResult("no waypoints saved on the travel map yet");
        }

        var listed = waypoints.Select(waypoint =>
            $"{waypoint.Name} ({(int)waypoint.Position.X}, {(int)waypoint.Position.Y}, {(int)waypoint.Position.Z})");
        return Task.FromResult("waypoints: " + string.Join("; ", listed));
    }
}
