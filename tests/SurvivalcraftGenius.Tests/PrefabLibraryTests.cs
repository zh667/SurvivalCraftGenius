using SurvivalcraftGenius.Npc;
using Xunit;

namespace SurvivalcraftGenius.Tests;

/// <summary>
/// The prefab folder. Same contract as the knowledge guides: shipped designs
/// land on first run, an untouched old default may be upgraded, and a file the
/// player has edited is never overwritten.
/// </summary>
public class PrefabLibraryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "genius-prefab-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private GeniusPrefabLibrary Library => new(_directory);

    [Fact]
    public void ShippedPrefabsLandOnFirstRun()
    {
        Library.EnsureShipped();

        var names = Library.Names();
        Assert.Contains("小屋", names);
        Assert.Contains("木屋", names);
    }

    [Fact]
    public void APlayerEditIsNeverOverwritten()
    {
        var library = Library;
        library.EnsureShipped();
        var path = Path.Combine(_directory, "小屋.txt");
        File.WriteAllText(path, "0,0,0,21\n");

        library.EnsureShipped();

        Assert.Equal("0,0,0,21\n", File.ReadAllText(path));
        Assert.Single(library.Load("小屋")!.Cells);
    }

    [Fact]
    public void EveryShippedPrefabParsesCleanlyAndIsAWholeBuilding()
    {
        var library = Library;
        library.EnsureShipped();

        foreach (var name in library.Names())
        {
            var prefab = library.Load(name);
            Assert.NotNull(prefab);
            Assert.Equal(0, prefab!.SkippedLines);
            // A building, not a stray block: floor, walls and a roof.
            Assert.True(prefab.Cells.Count > 50, $"{name} has only {prefab.Cells.Count} cells");
            Assert.True(prefab.Height >= 4, $"{name} is only {prefab.Height} tall");
        }
    }

    /// <summary>The doorway is the point: a sealed box is a tomb, not a shelter.</summary>
    [Fact]
    public void ShippedHousesHaveAWayIn()
    {
        var library = Library;
        library.EnsureShipped();
        var hut = library.Load("小屋")!;

        var solid = hut.Cells.Select(cell => (cell.X, cell.Y, cell.Z)).ToHashSet();
        var doorX = hut.Width / 2;

        Assert.DoesNotContain((doorX, 1, 0), solid);
        Assert.DoesNotContain((doorX, 2, 0), solid);
        // …and the wall around it really is a wall.
        Assert.Contains((0, 1, 0), solid);
    }

    [Fact]
    public void LookupToleratesPartialNames()
    {
        var library = Library;
        library.EnsureShipped();

        Assert.NotNull(library.Load("小屋"));
        Assert.NotNull(library.Load("小"));
        Assert.Null(library.Load("宫殿"));
        Assert.Null(library.Load(""));
    }

    [Fact]
    public void AMissingFolderListsNothingRatherThanThrowing()
    {
        Assert.Empty(Library.Names());
        Assert.Null(Library.Load("小屋"));
    }
}
