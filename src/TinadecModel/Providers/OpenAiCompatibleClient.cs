using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TinadecModel.Storage;
using Tinadec.Contracts.Models;
using TinadecModel.Tracing;
using TinadecModel.Json;

namespace TinadecModel.Providers;

public sealed class OpenAiCompatibleClient(HttpClient httpClient)
{
    public async Task<string> CreateAssistantReplyAsync(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken)
    {
        var response = await CreateAssistantResponseAsync(settings, apiKey, messages, null, cancellationToken);
        return response.TextContent;
    }

    public async Task<ModelInvocationResponseDto> CreateAssistantResponseAsync(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        string? providerId,
        CancellationToken cancellationToken,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        using var activity = ModelActivitySource.Instance.StartActivity(ModelSpanNames.AgentInference);
        activity?
            .SetTag(ModelSpanAttrs.Model, settings.Model)
            .SetTag(ModelSpanAttrs.BaseUrl, settings.BaseUrl)
            .SetTag(ModelSpanAttrs.HasApiKey, !string.IsNullOrWhiteSpace(apiKey));

        if (string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.Model))
        {
            return CreateResponse(
                "TinadecCode Core is running. Add an OpenAI-compatible base URL and model to enable live model responses.",
                new ModelUsageDto(0, 0, 0),
                ModelFinishReason.Unknown,
                settings,
                providerId,
                null,
                null,
                null,
                null);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var request = BuildChatCompletionRequest(settings, apiKey, messages);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            activity?.SetTag(ModelSpanAttrs.StatusCode, (int)response.StatusCode);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, $"Model request failed with {(int)response.StatusCode}");
            throw new HttpRequestException(
                $"Model request failed with {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        sw.Stop();
        activity?.SetTag(ModelSpanAttrs.LatencyMs, sw.ElapsedMilliseconds);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var messageElement = choice.GetProperty("message");
        var content = messageElement.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String
            ? contentProp.GetString()
            : null;

        var textContent = string.IsNullOrWhiteSpace(content)
            ? "The model returned an empty response."
            : content;

        // Parse tool_calls from the response
        IReadOnlyList<ToolCallDto>? parsedToolCalls = null;
        if (messageElement.TryGetProperty("tool_calls", out var toolCallsElement)
            && toolCallsElement.ValueKind == JsonValueKind.Array
            && toolCallsElement.GetArrayLength() > 0)
        {
            var calls = new List<ToolCallDto>();
            foreach (var tc in toolCallsElement.EnumerateArray())
            {
                var callId = tc.GetProperty("id").GetString() ?? $"call_{Guid.NewGuid():N}";
                var function = tc.GetProperty("function");
                var functionName = function.GetProperty("name").GetString() ?? "unknown";
                var argsJson = function.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() ?? "{}" : "{}";
                var arguments = ParseToolArguments(argsJson);
                calls.Add(new ToolCallDto(callId, functionName, arguments));
            }
            parsedToolCalls = calls;
        }

        return CreateResponse(
            textContent,
            ReadUsage(root),
            ReadFinishReason(choice),
            settings,
            providerId,
            ReadString(root, "id"),
            ReadString(root, "object"),
            ReadInt64(root, "created"),
            root.GetProperty("choices").GetArrayLength(),
            parsedToolCalls);
    }

    public static HttpRequestMessage BuildChatCompletionRequest(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var endpoint = BuildChatCompletionsEndpoint(settings.BaseUrl);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var messageList = messages.Select(message =>
        {
            var msg = new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            };
            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                msg["tool_call_id"] = message.ToolCallId;
            }
            return msg;
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["stream"] = false,
            ["messages"] = messageList
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(tool => new
            {
                type = tool.Type,
                function = new
                {
                    name = tool.Function.Name,
                    description = tool.Function.Description,
                    parameters = tool.Function.Parameters
                }
            }).ToArray();
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, TinadecJson.Options),
            Encoding.UTF8,
            "application/json");

        return request;
    }

    public static Uri BuildChatCompletionsEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/chat/completions";
        }

        return new Uri(trimmed, UriKind.Absolute);
    }

    private static ModelInvocationResponseDto CreateResponse(
        string textContent,
        ModelUsageDto usage,
        ModelFinishReason finishReason,
        StoredModelSettings settings,
        string? providerId,
        string? responseId,
        string? responseObject,
        long? created,
        int? choiceCount,
        IReadOnlyList<ToolCallDto>? toolCalls = null)
    {
        var custom = new Dictionary<string, object?>();
        AddIfPresent(custom, "response_id", responseId);
        AddIfPresent(custom, "response_object", responseObject);
        AddIfPresent(custom, "created", created);
        AddIfPresent(custom, "choice_count", choiceCount);
        if (toolCalls is { Count: > 0 })
        {
            custom["tool_calls"] = toolCalls.Select(tc => new Dictionary<string, object?>
            {
                ["call_id"] = tc.CallId,
                ["tool_id"] = tc.ToolId,
                ["argument_keys"] = tc.Arguments.Keys.OrderBy(k => k).ToArray()
            }).ToArray();
        }

        return new ModelInvocationResponseDto(
            textContent,
            usage,
            finishReason,
            new ProviderMetadataDto(
                providerId ?? "openai-compatible",
                settings.Model,
                "openai-compatible",
                custom),
            null,
            null,
            null,
            toolCalls);
    }

    private static ModelUsageDto ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return new ModelUsageDto(0, 0, 0);
        }

        var promptTokens = ReadInt32(usage, "prompt_tokens") ?? 0;
        var completionTokens = ReadInt32(usage, "completion_tokens") ?? 0;
        var totalTokens = ReadInt32(usage, "total_tokens") ?? promptTokens + completionTokens;
        return new ModelUsageDto(promptTokens, completionTokens, totalTokens);
    }

    private static ModelFinishReason ReadFinishReason(JsonElement choice)
    {
        var finishReason = ReadString(choice, "finish_reason");
        return finishReason switch
        {
            "stop" => ModelFinishReason.Stop,
            "length" => ModelFinishReason.Length,
            "content_filter" => ModelFinishReason.ContentFilter,
            "tool_calls" or "function_call" => ModelFinishReason.ToolCalls,
            "cancelled" => ModelFinishReason.Cancelled,
            null or "" => ModelFinishReason.Unknown,
            _ => ModelFinishReason.Unknown
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static long? ReadInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static void AddIfPresent(Dictionary<string, object?> custom, string key, object? value)
    {
        if (value is not null)
        {
            custom[key] = value;
        }
    }

    private static IReadOnlyDictionary<string, object?> ParseToolArguments(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson == "{}")
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var document = JsonDocument.Parse(argsJson);
            var result = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText()
                };
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, object?> { ["raw"] = argsJson };
        }
    }

}
