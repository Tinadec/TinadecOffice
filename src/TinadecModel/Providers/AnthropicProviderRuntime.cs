using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Storage;
using TinadecModel.Services;
using TinadecModel.Json;

namespace TinadecModel.Providers;

public sealed class AnthropicClient(HttpClient httpClient)
{
    public async Task<ModelInvocationResponseDto> CreateAssistantResponseAsync(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        string? providerId,
        CancellationToken cancellationToken)
    {
        using var request = BuildMessagesRequest(settings, apiKey, messages);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Anthropic request failed with {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        return new ModelInvocationResponseDto(
            ReadTextContent(root),
            ReadUsage(root),
            ReadFinishReason(root),
            new ProviderMetadataDto(
                providerId ?? "anthropic",
                ReadString(root, "model") ?? settings.Model,
                "anthropic",
                BuildMetadata(root)),
            null,
            null,
            null);
    }

    public static HttpRequestMessage BuildMessagesRequest(
        StoredModelSettings settings,
        string? apiKey,
        IReadOnlyList<MessageDto> messages)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildMessagesEndpoint(settings.BaseUrl));
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("x-api-key", apiKey.Trim());
        }

        var systemPrompt = string.Join("\n\n", messages
            .Where(message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content.Trim())
            .Where(content => !string.IsNullOrWhiteSpace(content)));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["stream"] = false,
            ["max_tokens"] = 1024,
            ["messages"] = messages
                .Where(message => !string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(ToAnthropicMessage)
                .ToArray()
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["system"] = systemPrompt;
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, TinadecJson.Options),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private static Uri BuildMessagesEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/messages";
        }

        return new Uri(trimmed, UriKind.Absolute);
    }

    private static object ToAnthropicMessage(MessageDto message)
    {
        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Anthropic tool_result messages require a tool_use_id and structured tool_result content.");
        }

        var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? "assistant"
            : "user";

        return new
        {
            role,
            content = new[]
            {
                new
                {
                    type = "text",
                    text = message.Content
                }
            }
        };
    }

    private static string ReadTextContent(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return "The model returned an empty response.";
        }

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (ReadString(item, "type") == "text")
            {
                builder.Append(ReadString(item, "text"));
            }
        }

        var text = builder.ToString();
        return string.IsNullOrWhiteSpace(text)
            ? "The model returned an empty response."
            : text;
    }

    private static ModelUsageDto ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return new ModelUsageDto(0, 0, 0);
        }

        var promptTokens = ReadInt32(usage, "input_tokens") ?? 0;
        var completionTokens = ReadInt32(usage, "output_tokens") ?? 0;
        return new ModelUsageDto(promptTokens, completionTokens, promptTokens + completionTokens);
    }

    private static ModelFinishReason ReadFinishReason(JsonElement root)
    {
        return ReadString(root, "stop_reason") switch
        {
            "end_turn" or "stop_sequence" => ModelFinishReason.Stop,
            "max_tokens" => ModelFinishReason.Length,
            "tool_use" => ModelFinishReason.ToolCalls,
            null or "" => ModelFinishReason.Unknown,
            _ => ModelFinishReason.Unknown
        };
    }

    private static Dictionary<string, object?> BuildMetadata(JsonElement root)
    {
        var metadata = new Dictionary<string, object?>();
        AddIfPresent(metadata, "message_id", ReadString(root, "id"));
        AddIfPresent(metadata, "message_type", ReadString(root, "type"));
        AddIfPresent(metadata, "role", ReadString(root, "role"));
        AddIfPresent(metadata, "stop_reason", ReadString(root, "stop_reason"));
        return metadata;
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

    private static void AddIfPresent(Dictionary<string, object?> custom, string key, object? value)
    {
        if (value is not null)
        {
            custom[key] = value;
        }
    }
}

public sealed class AnthropicProviderRuntime(AnthropicClient client) : IModelProviderRuntime
{
    public string Id => "anthropic";

    public bool CanHandle(ResolvedModelInvocationContextDto context)
    {
        return string.Equals(context.Driver, "anthropic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context.Provider?.Driver, "anthropic", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ModelInvocationResultDto> GenerateAsync(
        ResolvedModelInvocationContextDto context,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        try
        {
            var settings = new StoredModelSettings(
                context.EffectiveBaseUrl,
                context.EffectiveModel,
                context.EncryptedApiKey,
                DateTimeOffset.UtcNow);

            var response = await client.CreateAssistantResponseAsync(
                settings,
                apiKey,
                messages,
                context.ProviderInstanceId,
                cancellationToken);

            return new ModelInvocationResultDto(
                "executed",
                response.TextContent,
                context,
                false,
                Id);
        }
        catch (Exception exception)
        {
            var failure = exception is ArgumentException or InvalidOperationException
                ? ProviderErrorMapper.FromHttpStatus(context.ProviderInstanceId, 400)
                : ProviderErrorMapper.FromException(context.ProviderInstanceId, exception);

            return new ModelInvocationResultDto(
                "failed",
                failure.SafeMessage,
                context,
                false,
                Id,
                failure.Category,
                failure.Retryable,
                failure.StatusCode,
                failure.ExitCode,
                failure.SafeMessage,
                failure.ProviderId);
        }
    }
}

public sealed class AnthropicModule : IModelProviderModule
{
    public string ProviderFamily => "anthropic";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddHttpClient<AnthropicClient>();
        services.AddSingleton<IModelProviderRuntime, AnthropicProviderRuntime>();
    }

    public ProviderCapabilityDto GetCapabilities()
    {
        return new ProviderCapabilityDto(
            SupportsStreaming: false,
            SupportsTools: true,
            SupportsJsonMode: true,
            SupportsSystemPrompt: true,
            MaxContextTokens: null,
            RequiresWorkspace: false,
            CredentialKind: "api_key",
            HealthStatus: ProviderHealthStatus.Unknown);
    }
}
