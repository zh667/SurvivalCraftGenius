using System.Xml.Linq;
using SurvivalcraftGenius.Mod;
using Xunit;

namespace SurvivalcraftGenius.Tests;

public class DatabaseInjectorTests
{
    private static XElement BaseDatabase() => XElement.Parse("""
        <Database>
          <Folder Name="Animals" Guid="66a49cbb-cb12-4479-9b40-fbadd9db3425">
            <EntityTemplate Name="Wolf" Guid="11111111-1111-1111-1111-111111111111" />
          </Folder>
          <EntityTemplate Name="Player" Guid="4be6c1c5-d65d-4537-8a8b-a391969e6dc2" />
        </Database>
        """);

    private static XElement Source() => XElement.Parse("""
        <SurvivalCraftMap>
          <Folder Name="Animals" Guid="66a49cbb-cb12-4479-9b40-fbadd9db3425">
            <EntityTemplate Name="GeniusNpc" Guid="75e4ab99-72b3-4656-8bb0-db411edb2d6a">
              <MemberComponentTemplate Name="Brain" Guid="eeea54c2-9d33-4748-b629-7bc40236b1ae" />
            </EntityTemplate>
          </Folder>
          <EntityTemplate Name="Player" Guid="4be6c1c5-d65d-4537-8a8b-a391969e6dc2">
            <MemberComponentTemplate Name="GeniusPlayer" Guid="5079b4b9-c3a9-4e44-95eb-f7b5dfd674f1" />
          </EntityTemplate>
        </SurvivalCraftMap>
        """);

    [Fact]
    public void Inject_AddsTemplatesUnderAnchors()
    {
        var database = BaseDatabase();
        GeniusDatabaseInjector.Inject(Source(), database);

        var npc = database.Descendants("EntityTemplate")
            .Single(e => e.Attribute("Name")?.Value == "GeniusNpc");
        Assert.Equal("Animals", npc.Parent?.Attribute("Name")?.Value);
        Assert.Single(npc.Elements("MemberComponentTemplate"));
        var player = database.Elements("EntityTemplate")
            .Single(e => e.Attribute("Name")?.Value == "Player");
        Assert.Single(player.Elements("MemberComponentTemplate"));
    }

    [Fact]
    public void Inject_IsIdempotent()
    {
        var database = BaseDatabase();
        GeniusDatabaseInjector.Inject(Source(), database);
        GeniusDatabaseInjector.Inject(Source(), database);

        Assert.Single(database.Descendants("EntityTemplate")
            .Where(e => e.Attribute("Name")?.Value == "GeniusNpc"));
    }

    [Fact]
    public void Inject_MissingAnchor_Throws()
    {
        var database = XElement.Parse("""<Database />""");
        Assert.Throws<InvalidDataException>(() => GeniusDatabaseInjector.Inject(Source(), database));
    }

    [Fact]
    public void Inject_GuidCollisionWithDifferentContent_Throws()
    {
        var database = BaseDatabase();
        // Same GUID as GeniusNpc but different shape, planted elsewhere.
        database.Add(XElement.Parse(
            """<EntityTemplate Name="Impostor" Guid="75e4ab99-72b3-4656-8bb0-db411edb2d6a" />"""));

        Assert.Throws<InvalidDataException>(() => GeniusDatabaseInjector.Inject(Source(), database));
    }

    [Fact]
    public void Inject_WrongRoot_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => GeniusDatabaseInjector.Inject(XElement.Parse("<Wrong/>"), BaseDatabase()));
    }
}
