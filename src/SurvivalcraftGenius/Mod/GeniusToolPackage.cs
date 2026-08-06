using Game.NetWork;

namespace SurvivalcraftGenius.Mod;

/// <summary>
/// The single network package of the mod: a generic tool relay. The client
/// runs the LLM brain with its own API key and sends (tool, args) requests;
/// the server executes them against that player's companion and replies with
/// the result string. One package pair covers all 22 tools plus summon and
/// dismiss — the protocol boundary is exactly the agent layer's existing
/// (name, argsJson) → result contract.
/// </summary>
public sealed class GeniusToolPackage : IPackage
{
    // 219 sits far from the vanilla ID ranges (0-40, 56-59, 250-253, 255) and
    // known ecosystem picks: 41/217 are TravelMap's, 61 is claimed by popular
    // anticheat mods (per TravelMap's ID-conflict postmortem).
    public const byte PackageId = 219;

    public byte ID => PackageId;

    public Client To { get; set; } = null!;

    public Client Except { get; set; } = null!;

    public Client From { get; set; } = null!;

    public ClientState MinNeedState => ClientState.NotConnected;

    public GeniusToolMessage Message { get; private set; } =
        new(GeniusToolMessageKind.Request, 0, "", "");

    public GeniusToolPackage()
    {
    }

    public GeniusToolPackage(GeniusToolMessage message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public void WriteData(PackageStreamWriter writer) => GeniusToolCodec.Write(writer, Message);

    public void ReadData(PackageStreamReader reader)
    {
        Message = GeniusToolCodec.Read(reader);
    }

    public void Handle(ProjectNet projectNet, NetNode netNode, bool isServer) =>
        GeniusNetwork.Handle(this, projectNet, netNode, isServer);
}
