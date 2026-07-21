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
