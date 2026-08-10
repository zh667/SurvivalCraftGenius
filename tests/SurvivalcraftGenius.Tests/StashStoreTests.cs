using SurvivalcraftGenius.Agent;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class StashStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "genius-stash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEveryStack()
    {
        var store = new StashStore(_directory);
        var stashes = new Dictionary<string, List<(int Value, int Count)>>
        {
            ["ownerA"] = [(3, 12), (7, 1)],
            ["ownerB"] = [(9, 64)],
        };

        store.Save("World", 1234, stashes);
        var loaded = store.Load("World", 1234);

        Assert.Equal(2, loaded.Count);
        Assert.Equal([(3, 12), (7, 1)], loaded["ownerA"]);
        Assert.Equal([(9, 64)], loaded["ownerB"]);
    }

    [Fact]
    public void Load_DiscardsFileFromADifferentWorld()
    {
        // The game recycles world folder names, so a name match with a
        // different seed is a different world — its stash must not leak in.
        var store = new StashStore(_directory);
        store.Save("World", 1234, new Dictionary<string, List<(int, int)>>
        {
            ["owner"] = [(3, 1)],
        });

        Assert.Empty(store.Load("World", 4321));
        Assert.Empty(store.Load("World", 1234));
    }

    [Fact]
    public void Load_ReturnsEmptyForMissingOrCorruptFile()
    {
        var store = new StashStore(_directory);
        Assert.Empty(store.Load("NeverSaved", 1));

        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Broken.stash.json"), "{not json");
        Assert.Empty(store.Load("Broken", 1));
    }

    [Fact]
    public void Save_WithNothingKept_RemovesTheFile()
    {
        var store = new StashStore(_directory);
        store.Save("World", 7, new Dictionary<string, List<(int, int)>> { ["owner"] = [(3, 1)] });
        store.Save("World", 7, new Dictionary<string, List<(int, int)>>());

        Assert.Empty(store.Load("World", 7));
        Assert.False(File.Exists(Path.Combine(_directory, "World.stash.json")));
    }
}
