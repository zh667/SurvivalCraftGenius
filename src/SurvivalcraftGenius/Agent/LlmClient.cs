using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SurvivalcraftGenius.Agent;

public sealed class LlmException(string message) : Exception(message);

public sealed record LlmResponse(string Content, IReadOnlyList<ToolCall> ToolCalls)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>
/// Minimal OpenAI-compatible chat/completions client (works with DeepSeek, Qwen,
/// Kimi, GPT, Claude-compatible gateways, ...). Pure .NET — no game types.
/// </summary>
public sealed class LlmClient : IDisposable
{
    private const int MaxAttempts = 3;

    private readonly GeniusSettings _settings;
    private readonly HttpClient _http;

    /// <summary>Backoff unit between retries (attempt × this); tests shrink it to zero.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public LlmClient(GeniusSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(10, settings.RequestTimeoutSeconds));
    }

    /// <summary>
    /// Sends the completion request, retrying transient failures (timeouts,
    /// connection errors, 408/429/5xx) so a single hiccup doesn't kill the
    /// whole agent turn. Non-retryable statuses (bad key, bad request) and
    /// user cancellation surface immediately.
    /// </summary>
    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<IGeniusTool> tools,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(messages, tools, _settings.Model).ToString();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _settings.ChatCompletionsUrl);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.ApiKey}");
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return ParseResponse(body);
                }

                var status = (int)response.StatusCode;
                var retryable = status is 408 or 429 or >= 500;
                if (!retryable || attempt >= MaxAttempts)
                {
                    var snippet = body.Length > 400 ? body[..400] : body;
                    throw new LlmException($"LLM API {status}: {snippet}");
                }
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
            }
            catch (HttpRequestException exception)
            {
                throw new LlmException($"LLM API unreachable: {exception.Message}");
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException) when (attempt >= MaxAttempts)
            {
                // HttpClient timeout (not user cancellation), out of retries.
                throw new LlmException($"LLM API timed out after {_settings.RequestTimeoutSeconds}s");
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    public static JObject BuildPayload(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<IGeniusTool> tools,
        string model)
    {
        var messageArray = new JArray();
        foreach (var message in messages)
        {
            var entry = new JObject { ["role"] = message.Role };
            if (message.Role == "assistant" && message.ToolCalls.Count > 0)
            {
                if (!string.IsNullOrEmpty(message.Content))
                {
                    entry["content"] = message.Content;
                }

                entry["tool_calls"] = new JArray(message.ToolCalls.Select(call => new JObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    },
                }));
            }
            else
            {
                entry["content"] = message.Content;
            }

            if (message.Role == "tool")
            {
                entry["tool_call_id"] = message.ToolCallId;
            }

            messageArray.Add(entry);
        }

        var payload = new JObject
        {
            ["model"] = model,
            ["messages"] = messageArray,
        };
        if (tools.Count > 0)
        {
            payload["tools"] = new JArray(tools.Select(tool => new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JObject.Parse(tool.ParametersJsonSchema),
                },
            }));
            payload["tool_choice"] = "auto";
        }

        return payload;
    }

    public static LlmResponse ParseResponse(string responseBody)
    {
        JObject root;
        try
        {
            root = JObject.Parse(responseBody);
        }
        catch (Exception exception)
        {
            throw new LlmException($"LLM returned unparseable JSON: {exception.Message}");
        }

        if (root["choices"] is not JArray { Count: > 0 } choices
            || choices[0]?["message"] is not JObject message)
        {
            throw new LlmException("LLM response has no choices[0].message.");
        }

        var content = message["content"]?.Type == JTokenType.String
            ? (string)message["content"]!
            : "";
        var toolCalls = new List<ToolCall>();
        if (message["tool_calls"] is JArray calls)
        {
            foreach (var call in calls.OfType<JObject>())
            {
                var id = (string?)call["id"] ?? $"call_{toolCalls.Count}";
                var name = (string?)call["function"]?["name"] ?? "";
                var arguments = (string?)call["function"]?["arguments"] ?? "{}";
                if (!string.IsNullOrEmpty(name))
                {
                    toolCalls.Add(new ToolCall(id, name, arguments));
                }
            }
        }

        return new LlmResponse(content, toolCalls);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
