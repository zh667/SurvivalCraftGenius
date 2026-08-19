namespace SurvivalcraftGenius.Mod;

public enum GeniusToolMessageKind : byte
{
    /// <summary>client → server: run this tool against my companion.</summary>
    Request = 0,

    /// <summary>server → client: the tool's result string.</summary>
    Result = 1,

    /// <summary>server → client: a background job finished (task_finished).</summary>
    Event = 2,
}

public sealed record GeniusToolMessage(
    GeniusToolMessageKind Kind,
    uint RequestId,
    string Name,
    string Payload);

/// <summary>
/// Wire format for the tool-relay protocol. The netcode's package batches are
/// not length-delimited (a Read that doesn't consume exactly what Write
/// produced kills the whole batch), so every field is self-delimiting:
/// byte + uint32 + two 7-bit-length-prefixed strings. Pure .NET over
/// BinaryWriter/Reader so it unit-tests without the game.
/// </summary>
public static class GeniusToolCodec
{
    /// <summary>Scan JSON runs ~4KB; anything past this is a bug, not data.</summary>
    public const int MaxChars = 32_000;

    public static void Write(BinaryWriter writer, GeniusToolMessage message)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);
        writer.Write((byte)message.Kind);
        writer.Write(message.RequestId);
        writer.Write(Cap(message.Name, 256));
        writer.Write(Cap(message.Payload, MaxChars));
    }

    public static GeniusToolMessage Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var kind = reader.ReadByte();
        if (kind > (byte)GeniusToolMessageKind.Event)
        {
            throw new InvalidDataException($"unknown GeniusToolMessage kind {kind}");
        }

        var requestId = reader.ReadUInt32();
        var name = reader.ReadString();
        var payload = reader.ReadString();
        if (name.Length > 256 || payload.Length > MaxChars)
        {
            throw new InvalidDataException("GeniusToolMessage field over size cap");
        }

        return new GeniusToolMessage((GeniusToolMessageKind)kind, requestId, name, payload);
    }

    private static string Cap(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];
}
