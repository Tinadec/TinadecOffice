using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Manages model provider instances, credentials, routing, capabilities,
/// error normalization, and readiness. Uses IChatClient / ChatClientAgent as entry point.
/// Does not rewrite model HTTP clients.
/// </summary>
public interface IModelProvider
{
    Task<IChatClient?> GetChatClientAsync(
        string? routeId = null,
        CancellationToken cancellationToken = default);

    Task<ModelReadiness> CheckReadinessAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the configured embedding route without exposing provider credentials to callers.</summary>
public interface IEmbeddingProvider
{
    Task<EmbeddingResult> GenerateAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class EmbeddingRequest
{
    public Guid TenantId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public Guid ProjectId { get; init; }
    public IReadOnlyList<string> Inputs { get; init; } = [];
}

public sealed class EmbeddingResult
{
    public bool IsAvailable { get; init; }
    public string? ModelId { get; init; }
    public int Dimension { get; init; }
    public IReadOnlyList<float[]> Vectors { get; init; } = [];
    public string? Detail { get; init; }
}

public sealed class ModelReadiness
{
    public bool IsReady { get; init; }
    public string? StatusMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Provider-neutral token accounting persisted by TinadecCore. Framework-specific
/// usage objects are normalized into this type inside the MAF adapter.
/// </summary>
public sealed record ModelUsage(
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens,
    [property: JsonPropertyName("cached_input_tokens")] long? CachedInputTokens = null,
    [property: JsonPropertyName("reasoning_tokens")] long? ReasoningTokens = null,
    [property: JsonPropertyName("additional_counts")] IReadOnlyDictionary<string, long>? AdditionalCounts = null);

public sealed class ChatResolution
{
    public bool IsAvailable { get; init; }
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelId { get; init; }
    /// <summary>Wire protocol the resolved provider speaks; one of <see cref="ChatProtocols"/>.</summary>
    public string? Protocol { get; init; }
    /// <summary>Local HTTP endpoint of a CLI runtime (opencode serve); null for HTTP API providers.</summary>
    public string? ServerUrl { get; init; }
    /// <summary>Bearer token issued by an ACP CLI on startup, when the agent prints one.</summary>
    public string? Token { get; init; }
    /// <summary>Absolute path to the CLI executable for <see cref="Acp"/> / <see cref="OpencodeServe"/> protocols.</summary>
    public string? BinaryPath { get; init; }
    /// <summary>CLI launch arguments (e.g. <c>serve --port 4096</c>, <c>--acp-port 0</c>).</summary>
    public string? LaunchArgs { get; init; }
    public string? HomePath { get; init; }
    public string? Error { get; init; }
    public Guid? ProviderInstanceId { get; init; }
    public Guid? ProviderVersionId { get; init; }
    public Guid? RouteId { get; init; }
    public Guid? RouteVersionId { get; init; }
    public int CandidatePosition { get; init; }
    public string? StrategySource { get; init; }
}

/// <summary>
/// Canonical chat wire-protocol identifiers carried by <see cref="ChatResolution.Protocol"/>
/// and stored in provider configuration JSON (<c>protocol</c> key).
/// </summary>
public static class ChatProtocols
{
    /// <summary>OpenAI-compatible <c>/chat/completions</c> protocol (default).</summary>
    public const string OpenAiChat = "openai-chat";

    /// <summary>OpenAI Responses API protocol.</summary>
    public const string OpenAiResponses = "openai-responses";

    /// <summary>Anthropic Messages API protocol (<c>/v1/messages</c>).</summary>
    public const string AnthropicMessages = "anthropic-messages";

    /// <summary>
    /// Agent Client Protocol (ACP): the client hosts a CLI subprocess (claude/codex/cursor-agent)
    /// that exposes a JSON-RPC 2.0 + SSE server on a local port; chat runs through that process.
    /// </summary>
    public const string Acp = "acp";

    /// <summary>
    /// opencode <c>serve</c> protocol: an HTTP/SSE session surface on <c>http://127.0.0.1:&lt;port&gt;</c>
    /// (POST /session, POST /session/{id}/message, GET /session/{id}/event).
    /// </summary>
    public const string OpencodeServe = "opencode-serve";

    /// <summary>
    /// Normalizes a stored protocol value; blank or unknown values fall back to
    /// <see cref="OpenAiChat"/> so legacy configurations keep working.
    /// </summary>
    public static string Normalize(string? protocol) => protocol?.Trim().ToLowerInvariant() switch
    {
        OpenAiResponses => OpenAiResponses,
        AnthropicMessages => AnthropicMessages,
        Acp => Acp,
        OpencodeServe => OpencodeServe,
        _ => OpenAiChat
    };

    /// <summary>Infers the protocol from a provider driver name when no explicit protocol is configured.</summary>
    public static string InferFromDriver(string? driver) => driver?.Trim().ToLowerInvariant() switch
    {
        "anthropic" or "claude" => AnthropicMessages,
        "openai-responses" => OpenAiResponses,
        "claude-cli" or "codex-cli" or "cursor-acp" => Acp,
        "opencode" => OpencodeServe,
        _ => OpenAiChat
    };
}
