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
                var settings = GeniusSettings.FromJson(File.ReadAllText(SettingsPath));
                // Migrate configs written when the default budget was 8: too
                // small in practice (tasks froze mid-way waiting for 继续).
                if (settings.MaxToolSteps < 24)
                {
                    settings.MaxToolSteps = 32;
                    Save(settings);
                }

                return settings;
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
