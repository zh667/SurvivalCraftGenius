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

    public override void __ModInitialize()
    {
        Engine.Log.Information("[Genius] Mod initialized (v0.3.0).");
    }
}
