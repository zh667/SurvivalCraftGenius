using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Agent;

/// <summary>
/// Persists the conversation history per world so the companion remembers
/// across game sessions. One JSON file per world directory name, stamped with
/// the world seed: Survivalcraft recycles world folder names (World, World1,
/// …), so a matching name with a different seed means a different world and
/// the stale file is discarded (same guard as TravelMap's seed stamp).
/// The system prompt is never persisted — it evolves with the mod version and
/// is re-prepended fresh on load. Pure .NET — no game types.
/// </summary>
public sealed class ConversationStore(string directory)
{
    /// <summary>Newest messages kept on disk; older ones were summarized anyway.</summary>
    public const int MaxSavedMessages = 80;

    /// <summary>Returns the restored history, or null when absent/stale/corrupt.</summary>
    public List<ChatMessage>? Load(string worldKey, int worldSeed)
    {
        var path = PathFor(worldKey);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var root = JObject.Parse(File.ReadAllText(path));
            if ((int?)root["seed"] != worldSeed)
            {
                // Same folder name, different world: the game recycled the name.
                File.Delete(path);
                return null;
            }

            var messages = new List<ChatMessage>();
            foreach (var entry in root["messages"] as JArray ?? [])
            {
                var role = (string?)entry["role"] ?? "";
                if (role is not ("user" or "assistant" or "tool"))
                {
                    continue;
                }

                var toolCalls = new List<ToolCall>();
                if (entry["tool_calls"] is JArray calls)
                {
                    foreach (var call in calls.OfType<JObject>())
                    {
                        toolCalls.Add(new ToolCall(
                            (string?)call["id"] ?? "",
                            (string?)call["name"] ?? "",
                            (string?)call["arguments"] ?? "{}"));
                    }
                }

                messages.Add(new ChatMessage
                {
                    Role = role,
                    Content = (string?)entry["content"] ?? "",
                    ToolCalls = toolCalls,
                    ToolCallId = (string?)entry["tool_call_id"] ?? "",
                });
            }

            // Never start with tool results whose originating assistant
            // message was trimmed away — APIs reject dangling tool messages.
            while (messages.Count > 0 && messages[0].Role == "tool")
            {
                messages.RemoveAt(0);
            }

            return messages.Count == 0 ? null : messages;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(string worldKey, int worldSeed, IReadOnlyList<ChatMessage> messages)
    {
        Directory.CreateDirectory(directory);
        var array = new JArray();
        foreach (var message in messages)
        {
            if (message.Role == "system")
            {
                continue;
            }

            var entry = new JObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content,
            };
            if (message.ToolCalls.Count > 0)
            {
                entry["tool_calls"] = new JArray(message.ToolCalls.Select(call => new JObject
                {
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                }));
            }

            if (message.ToolCallId.Length > 0)
            {
                entry["tool_call_id"] = message.ToolCallId;
            }

            array.Add(entry);
        }

        while (array.Count > MaxSavedMessages)
        {
            array.RemoveAt(0);
        }

        var root = new JObject
        {
            ["seed"] = worldSeed,
            ["messages"] = array,
        };

        // Write-then-move so a crash mid-write can't corrupt the only copy.
        var path = PathFor(worldKey);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToString());
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string worldKey)
    {
        var safe = new string(worldKey.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length == 0)
        {
            safe = "world";
        }

        return Path.Combine(directory, safe + ".json");
    }
}
