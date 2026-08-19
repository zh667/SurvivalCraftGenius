using Engine;
using Game;
using Game.NetWork;

namespace SurvivalcraftGenius.Mod;

/// <summary>
/// Client-brain / server-body plumbing (Numen's multiplayer shape adapted to
/// Survivalcraft's package netcode). The server never sees an API key; each
/// client pays for its own companion's LLM. Identity is taken solely from the
/// package's From client (anti-spoof, vanilla precedent), and results are
/// re-bound to the client by (PlayerGuid, TokenId) at completion time so a
/// reconnect on a recycled byte ID cannot misdeliver a late mining report.
/// </summary>
public static class GeniusNetwork
{
    /// <summary>Pseudo-tools carried over the same relay as real tools.</summary>
    public const string SummonOp = "_summon";
    public const string DismissOp = "_dismiss";

    /// <summary>
    /// Tools that run on the client: pure speech, knowledge lookups (data is
    /// device-local), and waypoint listing (TravelMap data lives client-side).
    /// Everything world-touching executes on the server for truth — client
    /// terrain can even be deliberately falsified (anti-xray obfuscator).
    /// </summary>
    private static readonly HashSet<string> ClientLocalTools = new(StringComparer.Ordinal)
    {
        "say", "query_recipes", "query_help", "read_knowledge", "list_waypoints",
    };

    public static bool PackageRegistered { get; private set; }

    public static bool IsClientLocalTool(string name) => ClientLocalTools.Contains(name);

    /// <summary>
    /// Manual registration from __ModInitialize — the game's auto-registration
    /// of mod packages is dead code (IsSubclassOf never matches an interface).
    /// A conflicting ID degrades multiplayer support instead of killing the mod.
    /// </summary>
    public static void TryRegisterPackage()
    {
        if (PackageRegistered)
        {
            return;
        }

        try
        {
            PackageManager.RegisterPackage(new GeniusToolPackage());
            PackageRegistered = true;
        }
        catch (Exception exception)
        {
            Log.Warning($"[Genius] Network package ID {GeniusToolPackage.PackageId} conflict — " +
                $"multiplayer client support disabled: {exception.Message}");
        }
    }

    public static void Handle(GeniusToolPackage package, ProjectNet projectNet, NetNode netNode, bool isServer)
    {
        if (projectNet is null)
        {
            // Handshake/broadcast paths deliver a null project; nothing to do.
            return;
        }

        if (isServer && package.Message.Kind == GeniusToolMessageKind.Request)
        {
            HandleRequestOnServer(package, netNode);
        }
        else if (!isServer && package.Message.Kind is GeniusToolMessageKind.Result or GeniusToolMessageKind.Event)
        {
            HandleResultOnClient(package, projectNet);
        }
    }

    /// <summary>Runs on the server main thread (package dispatch is frame-loop).</summary>
    private static void HandleRequestOnServer(GeniusToolPackage package, NetNode netNode)
    {
        var source = package.From;
        var component = source?.PlayerData?.ComponentPlayer?.Entity
            .FindComponent<GeniusPlayerComponent>(throwOnError: false);
        if (source is null || component is null)
        {
            return;
        }

        var message = package.Message;
        var sourceGuid = source.PlayerGuid;
        var sourceToken = source.TokenId;
        component.RememberNetPeer(sourceGuid, sourceToken);
        _ = RunAsync();

        async Task RunAsync()
        {
            string result;
            try
            {
                result = await component.ExecuteNetToolAsync(message.Name, message.Payload)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                result = Agent.GeniusFailure.Format(Agent.FailureType.Internal, exception.Message);
            }

            // Long orders finish minutes later: marshal to the main thread and
            // re-resolve the client — byte IDs are recycled across reconnects.
            component.RunOnMainThread(() =>
            {
                var client = FindClientByIdentity(netNode, sourceGuid, sourceToken);
                if (client is { IsConnected: true })
                {
                    netNode.QueuePackage(new GeniusToolPackage(
                        new GeniusToolMessage(GeniusToolMessageKind.Result, message.RequestId, message.Name, result))
                    {
                        To = client,
                    });
                }
            });
        }
    }

    public static Client? FindClient(Guid playerGuid, Guid tokenId) =>
        CommonLib.Net is { } netNode ? FindClientByIdentity(netNode, playerGuid, tokenId) : null;

    private static Client? FindClientByIdentity(NetNode netNode, Guid playerGuid, Guid tokenId)
    {
        foreach (var client in netNode.Clients.Values)
        {
            if (client.PlayerGuid == playerGuid && client.TokenId == tokenId)
            {
                return client;
            }
        }

        return null;
    }

    private static void HandleResultOnClient(GeniusToolPackage package, ProjectNet projectNet)
    {
        var component = projectNet.FindSubsystem<SubsystemPlayers>(throwOnError: false)
            ?.MainPlayer?.Entity.FindComponent<GeniusPlayerComponent>(throwOnError: false);
        if (component is null)
        {
            return;
        }

        if (package.Message.Kind == GeniusToolMessageKind.Event)
        {
            component.HandleNetEvent(package.Message.Payload);
            return;
        }

        component.CompleteNetTool(package.Message.RequestId, package.Message.Payload);
    }
}
