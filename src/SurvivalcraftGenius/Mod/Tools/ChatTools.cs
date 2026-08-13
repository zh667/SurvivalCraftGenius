using Engine;
using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Mod.Tools;

/// <summary>Talking to the player. Works before the companion is summoned.</summary>
public static class ChatTools
{
    public static Task<string> Say(GeniusToolContext context, JObject arguments)
    {
        var text = (string?)arguments["text"] ?? "";
        if (text.Length > 0)
        {
            context.Player.AppendChat(GeniusChatRole.Genius, text);
            context.ComponentPlayer.ComponentGui.DisplaySmallMessage(
                $"Genius: {GeniusPlayerComponent.Shorten(text, 80)}",
                Color.LightGreen,
                blinking: false,
                playNotificationSound: false);
        }

        return Task.FromResult("said");
    }
}
