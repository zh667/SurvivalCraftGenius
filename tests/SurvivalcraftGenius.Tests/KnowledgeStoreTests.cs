using SurvivalcraftGenius.Mod;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public sealed class KnowledgeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "genius-knowledge-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private GeniusKnowledgeStore NewStore()
    {
        var store = new GeniusKnowledgeStore(_dir);
        store.EnsureStarter();
        return store;
    }

    [Fact]
    public void CreatesTopicalGuides()
    {
        NewStore();
        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        Assert.Contains("战斗与狩猎.md", files);
        Assert.Contains("矿物与科技.md", files);
        Assert.True(files.Count >= 5);
    }

    [Fact]
    public void ReturnsMatchingSectionNotWholeFile()
    {
        var store = NewStore();
        var result = store.Read("打鸟");
        Assert.Contains("正面半球", result);
        Assert.Contains("sneak=true", result);
        // Section-level: content from unrelated sections of the same file
        // must not be dragged in.
        Assert.DoesNotContain("刷石机", result);
        Assert.True(result.Length < 3500, $"lookup too fat: {result.Length} chars");
    }

    [Fact]
    public void ListsSiblingSectionsOfMatchedFile()
    {
        var store = NewStore();
        var result = store.Read("月圆");
        Assert.Contains("其他章节", result);
    }

    [Fact]
    public void SuggestsWhenTopicMisses()
    {
        var store = NewStore();
        var result = store.Read("狩腊");
        Assert.Contains("did you mean", result);
    }

    [Fact]
    public void EmptyTopicListsFilesWithHints()
    {
        var store = NewStore();
        var result = store.Read(null);
        Assert.Contains("战斗与狩猎", result);
        Assert.Contains("何时读", result);
    }

    [Fact]
    public void ShipsSpawnAndLoadingRules()
    {
        var store = NewStore();
        Assert.Contains("远征", store.Read("生物在哪里刷新"));
        Assert.Contains("跟着我", store.Read("世界加载规则"));
    }

    [Fact]
    public void RemovesUnmodifiedLegacyFileButKeepsEditedOne()
    {
        Directory.CreateDirectory(_dir);
        // An edited "legacy" file (content differs from the shipped original)
        // must survive the upgrade.
        var edited = Path.Combine(_dir, "进阶攻略-贴吧教程整理.md");
        File.WriteAllText(edited, "# 玩家自己改过的内容");
        NewStore();
        Assert.True(File.Exists(edited));
        Assert.Equal("# 玩家自己改过的内容", File.ReadAllText(edited));
    }
}
