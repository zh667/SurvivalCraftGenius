using System.Text;
using SurvivalcraftGenius.Mod;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class GeniusToolCodecTests
{
    private static GeniusToolMessage RoundTrip(GeniusToolMessage message)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            GeniusToolCodec.Write(writer, message);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var restored = GeniusToolCodec.Read(reader);
        // The batch is not length-delimited: Read must consume EXACTLY what
        // Write produced or it corrupts every following package.
        Assert.Equal(stream.Length, stream.Position);
        return restored;
    }

    [Fact]
    public void Request_RoundTripsExactly()
    {
        var message = new GeniusToolMessage(
            GeniusToolMessageKind.Request, 42, "mine_resource",
            """{"resource_name":"煤","count":5}""");

        Assert.Equal(message, RoundTrip(message));
    }

    [Fact]
    public void Result_WithCjkAndNewlines_RoundTripsExactly()
    {
        var message = new GeniusToolMessage(
            GeniusToolMessageKind.Result, uint.MaxValue, "look_around",
            "俯视地形图 半径8\n.....#..\n图例: @=我");

        Assert.Equal(message, RoundTrip(message));
    }

    [Fact]
    public void OversizedPayload_IsCappedOnWrite()
    {
        var message = new GeniusToolMessage(
            GeniusToolMessageKind.Result, 1, "scan_surroundings",
            new string('x', GeniusToolCodec.MaxChars + 500));

        var restored = RoundTrip(message);

        Assert.Equal(GeniusToolCodec.MaxChars, restored.Payload.Length);
    }

    [Fact]
    public void UnknownKindByte_Throws()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)9);
            writer.Write(1u);
            writer.Write("goto");
            writer.Write("{}");
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        Assert.Throws<InvalidDataException>(() => GeniusToolCodec.Read(reader));
    }

    [Fact]
    public void ClientLocalTools_AreRealCatalogTools()
    {
        var catalog = SurvivalcraftGenius.Agent.ToolCatalog.CreateDefaultRegistry()
            .Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        string[] clientLocal = ["say", "query_recipes", "query_help", "read_knowledge", "list_waypoints"];

        Assert.All(clientLocal, name =>
        {
            Assert.True(GeniusNetwork.IsClientLocalTool(name), $"{name} should be client-local");
            Assert.Contains(name, catalog);
        });
        Assert.False(GeniusNetwork.IsClientLocalTool("mine_resource"));
        Assert.False(GeniusNetwork.IsClientLocalTool("scan_surroundings"));
    }
}
