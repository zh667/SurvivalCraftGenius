using System.Reflection;
using Engine;
using Game;

namespace SurvivalcraftGenius.Mod;

/// <summary>
/// Loose-coupled bridge to the SurvivalcraftTravelMap mod: reads the waypoint
/// list off the player's TravelMapComponent via reflection. No hard assembly
/// reference, so Genius keeps working when the map mod is absent.
/// </summary>
public static class TravelMapBridge
{
    private const string ComponentTypeName = "SurvivalcraftTravelMap.Mod.TravelMapComponent";

    public sealed record MapWaypoint(string Name, Vector3 Position);

    public static IReadOnlyList<MapWaypoint>? TryReadWaypoints(ComponentPlayer componentPlayer)
    {
        var travelMap = componentPlayer.Entity
            .FindComponents<GameEntitySystem.Component>()
            .FirstOrDefault(component => component.GetType().FullName == ComponentTypeName);
        if (travelMap is null)
        {
            return null;
        }

        var field = travelMap.GetType().GetField(
            "_waypoints", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(travelMap) is not System.Collections.IEnumerable waypoints)
        {
            return null;
        }

        var result = new List<MapWaypoint>();
        foreach (var waypoint in waypoints)
        {
            var type = waypoint.GetType();
            var name = type.GetProperty("Name")?.GetValue(waypoint) as string;
            if (string.IsNullOrEmpty(name)
                || type.GetProperty("Position")?.GetValue(waypoint)
                    is not System.Numerics.Vector3 position)
            {
                continue;
            }

            result.Add(new MapWaypoint(name, new Vector3(position.X, position.Y, position.Z)));
        }

        return result;
    }
}
