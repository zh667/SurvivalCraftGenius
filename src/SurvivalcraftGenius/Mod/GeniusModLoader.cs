using Game;

namespace SurvivalcraftGenius.Mod;

public sealed class GeniusModLoader : ModLoader
{
    public override void __ModInitialize()
    {
        Engine.Log.Information("[Genius] Mod loaded (v0.1.0 skeleton).");
    }
}
