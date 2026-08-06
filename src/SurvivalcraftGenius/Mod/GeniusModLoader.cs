using System.Xml.Linq;
using Game;

namespace SurvivalcraftGenius.Mod;

public sealed class GeniusModLoader : ModLoader
{
    public override void OnXdbLoad(XElement database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var injected = false;
        Entity.GetFiles(".netxdb", (_, stream) =>
        {
            try
            {
                var source = XElement.Load(stream, LoadOptions.None);
                GeniusDatabaseInjector.Inject(source, database);
                injected = true;
                Engine.Log.Information("[Genius] Entity templates injected.");
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or FormatException)
            {
                Engine.Log.Warning($"[Genius] Database injection failed: {exception.Message}");
            }
        });

        if (!injected)
        {
            Engine.Log.Warning("[Genius] mod.netxdb missing or rejected; the companion is unavailable.");
        }
    }

    /// <summary>
    /// Engine hook at the death moment (ComponentHealth, server side). Skip=true
    /// cancels the vanilla drop pass — this is what makes keep-inventory apply
    /// to every player, including ones who join later.
    /// </summary>
    public override void DeadBeforeDrops(Game.ComponentHealth componentHealth, out bool Skip)
    {
        Skip = false;
        try
        {
            Skip = GeniusKeepInventory.ShouldKeepInventory(componentHealth);
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] keep-inventory check failed: {exception.Message}");
        }
    }

    public override void __ModInitialize()
    {
        // Manual registration is mandatory: the game's auto-registration of
        // mod packages never fires (IsSubclassOf on an interface).
        GeniusNetwork.TryRegisterPackage();
        var version = typeof(GeniusModLoader).Assembly.GetName().Version?.ToString(3) ?? "?";
        Engine.Log.Information($"[Genius] Mod initialized (v{version}).");
    }
}
