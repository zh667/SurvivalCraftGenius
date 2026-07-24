using SurvivalcraftGenius.Agent;

namespace SurvivalcraftGenius.Mod;

/// <summary>
/// Loads/saves device-level settings at data:/SurvivalcraftGenius/settings.json.
/// A missing file is created with defaults so the user can also edit it by hand.
/// </summary>
public sealed class GeniusSettingsStore(string directory)
{
    public string SettingsPath { get; } = Path.Combine(directory, "settings.json");

    public GeniusSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return GeniusSettings.FromJson(File.ReadAllText(SettingsPath));
            }

            var defaults = new GeniusSettings();
            Save(defaults);
            return defaults;
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] Failed to load settings ({exception.Message}); using defaults.");
            return new GeniusSettings();
        }
    }

    public void Save(GeniusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, settings.ToJson());
        }
        catch (Exception exception)
        {
            Engine.Log.Warning($"[Genius] Failed to save settings: {exception.Message}");
        }
    }
}
