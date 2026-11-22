using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Discovers external CLI tool configurations (<c>claude_code</c>, <c>codex</c>,
/// <c>z_code</c>, <c>minimax_code</c>, <c>pi</c>) with strict secret redaction on
/// discovery and SecretStore migration + safe stdio defaults on opted-in import.
/// </summary>
public interface IExternalCliDiscoveryService
{
    Task<ExternalCliDiscoveryDto> DiscoverAsync(
        Guid? projectId = null,
        IReadOnlyList<string>? ecosystems = null,
        CancellationToken cancellationToken = default);

    Task<ExternalCliImportResultDto> ImportAsync(
        ExternalCliImportRequestDto request,
        CancellationToken cancellationToken = default);
}
