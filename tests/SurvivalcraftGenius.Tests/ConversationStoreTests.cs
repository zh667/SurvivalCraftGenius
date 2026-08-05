using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "genius-tests", Guid.NewGuid().ToString("N"));

    private ConversationStore Store => new(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void RoundTrip_PreservesMessagesAndSkipsSystemPrompt()
    {
        var store = Store;
        store.Save("World1", 12345,
        [
            ChatMessage.System("the prompt, never persisted"),
            ChatMessage.User("挖点煤"),
            ChatMessage.Assistant("", [new ToolCall("c1", "mine_resource", """{"resource_name":"煤"}""")]),
            ChatMessage.ToolResult("c1", "mined and collected: 煤 x5"),
            ChatMessage.Assistant("挖到了 5 个煤。"),
        ]);

        var restored = store.Load("World1", 12345);

        Assert.NotNull(restored);
        Assert.Equal(4, restored.Count);
        Assert.DoesNotContain(restored, message => message.Role == "system");
        Assert.Equal("挖点煤", restored[0].Content);
        Assert.Equal("mine_resource", restored[1].ToolCalls[0].Name);
        Assert.Equal("c1", restored[2].ToolCallId);
        Assert.Equal("挖到了 5 个煤。", restored[3].Content);
    }

    [Fact]
    public void Load_SeedMismatch_DiscardsRecycledWorldFile()
    {
        var store = Store;
        store.Save("World1", 111, [ChatMessage.User("old world chat")]);

        Assert.Null(store.Load("World1", 222));
        // The stale file is gone: even the original seed finds nothing now.
        Assert.Null(store.Load("World1", 111));
    }

    [Fact]
    public void Load_MissingOrCorruptFile_ReturnsNull()
    {
        var store = Store;
        Assert.Null(store.Load("NeverSaved", 1));

        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Broken.json"), "not json at all");
        Assert.Null(store.Load("Broken", 1));
    }

    [Fact]
    public void Save_CapsFileAtMaxSavedMessages()
    {
        var store = Store;
        var messages = Enumerable.Range(0, ConversationStore.MaxSavedMessages + 20)
            .Select(i => ChatMessage.User($"msg{i}"))
            .ToList();

        store.Save("World1", 7, messages);
        var restored = store.Load("World1", 7);

        Assert.NotNull(restored);
        Assert.Equal(ConversationStore.MaxSavedMessages, restored.Count);
        // The oldest overflowed; the newest survived.
        Assert.Equal("msg20", restored[0].Content);
        Assert.Equal($"msg{ConversationStore.MaxSavedMessages + 19}", restored[^1].Content);
    }

    [Fact]
    public void Load_TrimsDanglingLeadingToolResults()
    {
        var store = Store;
        store.Save("World1", 9,
        [
            ChatMessage.ToolResult("orphan", "result whose assistant call was capped away"),
            ChatMessage.User("hello"),
        ]);

        var restored = store.Load("World1", 9);

        Assert.NotNull(restored);
        Assert.Single(restored);
        Assert.Equal("user", restored[0].Role);
    }

    [Fact]
    public void PathKeys_WithHostileCharacters_StillWork()
    {
        var store = Store;
        store.Save("app:/Worlds/World 1", 5, [ChatMessage.User("hi")]);

        var restored = store.Load("app:/Worlds/World 1", 5);

        Assert.NotNull(restored);
        Assert.Equal("hi", restored[0].Content);
    }
}
