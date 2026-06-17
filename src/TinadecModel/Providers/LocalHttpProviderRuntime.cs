using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json;
using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Json;
using TinadecModel.Services;
using TinadecModel.Storage;

namespace TinadecModel.Providers;

public sealed record LocalHttpHealthProbeResult(
    ProviderHealthStatus Status,
    ProviderErrorCategory? ErrorCategory,
    bool Retryable,
    int? StatusCode,
    string? SafeMessage);

public sealed class LocalHttpProviderRuntime(HttpClient httpClient, OpenAiCompatibleClient openAiCompatibleClient) : IModelProviderRuntime
{
    public const string RuntimeId = "local-http";

    public string Id => RuntimeId;

    public bool CanHandle(ResolvedModelInvocationContextDto context)
    {
        return (IsLocalHttpDriver(context.Driver) || IsLocalServerOpenAiCompatibleDriver(context.Driver))
            && (string.Equals(context.ConnectionKind, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(context.ConnectionKind, "local-server", StringComparison.OrdinalIgnoreCase));
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
            var response = ResolveAdapterStrategy(context.Driver) == LocalHttpAdapterStrategy.OpenAiCompatible
                ? await GenerateOpenAiCompatibleAsync(context, apiKey, messages, cancellationToken)
                : await GenerateBasicHttpAsync(context, apiKey, messages, cancellationToken);

            return new ModelInvocationResultDto(
                "executed",
                response.TextContent,
                context,
                false,
                Id);
        }
        catch (Exception exception)
        {
            var failure = MapFailure(context.ProviderInstanceId, exception, cancellationToken);
            return CreateFailureResult(context, failure);
        }
    }

    public async Task<LocalHttpHealthProbeResult> ProbeHealthAsync(
        ResolvedModelInvocationContextDto context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildEndpoint(context.EffectiveBaseUrl));
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new LocalHttpHealthProbeResult(ProviderHealthStatus.Healthy, null, false, (int)response.StatusCode, null);
            }

            var failure = ProviderErrorMapper.FromHttpStatus(context.ProviderInstanceId, (int)response.StatusCode);
            return new LocalHttpHealthProbeResult(ProviderHealthStatus.Unhealthy, failure.Category, failure.Retryable, failure.StatusCode, failure.SafeMessage);
        }
        catch (Exception exception)
        {
            var failure = MapFailure(context.ProviderInstanceId, exception, cancellationToken);
            return new LocalHttpHealthProbeResult(ProviderHealthStatus.Unhealthy, failure.Category, failure.Retryable, failure.StatusCode, failure.SafeMessage);
        }
    }

    private async Task<ModelInvocationResponseDto> GenerateOpenAiCompatibleAsync(
        ResolvedModelInvocationContextDto context,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken)
    {
        var settings = new StoredModelSettings(
            context.EffectiveBaseUrl,
            context.EffectiveModel,
            context.EncryptedApiKey,
            DateTimeOffset.UtcNow);

        return await openAiCompatibleClient.CreateAssistantResponseAsync(
            settings,
            apiKey,
            messages,
            context.ProviderInstanceId,
            cancellationToken);
    }

    private async Task<ModelInvocationResponseDto> GenerateBasicHttpAsync(
        ResolvedModelInvocationContextDto context,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken)
    {
        using var request = BuildBasicHttpRequest(context, apiKey, messages);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Local HTTP provider request failed with {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var content = ExtractBasicResponseText(body);

        return new ModelInvocationResponseDto(
            content,
            new ModelUsageDto(0, 0, 0),
            ModelFinishReason.Unknown,
            new ProviderMetadataDto(
                context.ProviderInstanceId,
                context.EffectiveModel,
                ResolveAdapterStrategy(context.Driver).ToDriverName(),
                new Dictionary<string, object?>()),
            null,
            null,
            null);
    }

    private static HttpRequestMessage BuildBasicHttpRequest(
        ResolvedModelInvocationContextDto context,
        string? apiKey,
        IReadOnlyList<MessageDto> messages)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(context.EffectiveBaseUrl));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        var payload = new
        {
            model = context.EffectiveModel,
            stream = false,
            messages = messages.Select(message => new
            {
                role = message.Role,
                content = message.Content
            })
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, TinadecJson.Options),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new HttpRequestException("Local HTTP provider base URL is not configured.", null, HttpStatusCode.BadRequest);
        }

        return new Uri(baseUrl.Trim(), UriKind.Absolute);
    }

    private static string ExtractBasicResponseText(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "The model returned an empty response.";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                return document.RootElement.GetString() ?? "The model returned an empty response.";
            }

            foreach (var propertyName in new[] { "text", "content", "response", "message", "result" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
                {
                    return string.IsNullOrWhiteSpace(property.GetString())
                        ? "The model returned an empty response."
                        : property.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            return body;
        }

        return body;
    }

    private static ProviderFailureDetails MapFailure(string providerId, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ProviderFailureDetails(
                ProviderErrorCategory.Timeout,
                ProviderErrorMapper.IsRetryable(ProviderErrorCategory.Timeout),
                null,
                null,
                providerId,
                "Provider request timed out.");
        }

        if (exception is HttpRequestException { StatusCode: null })
        {
            return new ProviderFailureDetails(
                ProviderErrorCategory.ProviderUnavailable,
                ProviderErrorMapper.IsRetryable(ProviderErrorCategory.ProviderUnavailable),
                null,
                null,
                providerId,
                "Provider is temporarily unavailable.");
        }

        return ProviderErrorMapper.FromException(providerId, exception);
    }

    private static ModelInvocationResultDto CreateFailureResult(
        ResolvedModelInvocationContextDto context,
        ProviderFailureDetails failure)
    {
        return new ModelInvocationResultDto(
            "failed",
            failure.SafeMessage,
            context,
            false,
            RuntimeId,
            failure.Category,
            failure.Retryable,
            failure.StatusCode,
            failure.ExitCode,
            failure.SafeMessage,
            failure.ProviderId);
    }

    private static bool IsLocalHttpDriver(string? driver)
    {
        return string.Equals(driver, "local-http", StringComparison.OrdinalIgnoreCase)
            || string.Equals(driver, "local-http-generic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(driver, "local-http-openai-compatible", StringComparison.OrdinalIgnoreCase)
            || string.Equals(driver, "local-http-ollama", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalServerOpenAiCompatibleDriver(string? driver)
    {
        return ProviderTemplateRules.IsLocalOpenAiCompatibleDriver(driver);
    }

    private static LocalHttpAdapterStrategy ResolveAdapterStrategy(string? driver)
    {
        return driver?.Trim().ToLowerInvariant() switch
        {
            "local-http-openai-compatible" => LocalHttpAdapterStrategy.OpenAiCompatible,
            "ollama" or "vllm" or "sglang" or "lmstudio" or "llamacpp" => LocalHttpAdapterStrategy.OpenAiCompatible,
            "local-http-ollama" => LocalHttpAdapterStrategy.Ollama,
            _ => LocalHttpAdapterStrategy.Generic
        };
    }
}

public sealed class LocalHttpModule : IModelProviderModule
{
    public string ProviderFamily => "local-http";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddHttpClient<OpenAiCompatibleClient>();
        services.AddHttpClient<LocalHttpProviderRuntime>();
        services.AddSingleton<IModelProviderRuntime>(provider => provider.GetRequiredService<LocalHttpProviderRuntime>());
    }

    public ProviderCapabilityDto GetCapabilities()
    {
        return new ProviderCapabilityDto(
            SupportsStreaming: true,
            SupportsTools: false,
            SupportsJsonMode: false,
            SupportsSystemPrompt: true,
            MaxContextTokens: null,
            RequiresWorkspace: false,
            CredentialKind: "none",
            HealthStatus: ProviderHealthStatus.Unknown);
    }
}

internal enum LocalHttpAdapterStrategy
{
    Generic,
    OpenAiCompatible,
    Ollama
}

internal static class LocalHttpAdapterStrategyExtensions
{
    public static string ToDriverName(this LocalHttpAdapterStrategy strategy)
    {
        return strategy switch
        {
            LocalHttpAdapterStrategy.OpenAiCompatible => "local-http-openai-compatible",
            LocalHttpAdapterStrategy.Ollama => "local-http-ollama",
            _ => "local-http"
        };
    }
}
