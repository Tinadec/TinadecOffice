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

public sealed class ModelReadiness
{
    public bool IsReady { get; init; }
    public string? StatusMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
