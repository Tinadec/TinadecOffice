namespace TinadecCore.Abstractions.Ports;

/// <summary>Sanitized snapshot of a recorded 5xx failure for agent diagnostics.</summary>
public sealed record ServerFailureSnapshotDto(
    string Method,
    string Path,
    int Status,
    string Code,
    string ExceptionType,
    string ExceptionMessage,
    string? RootType,
    string? RootMessage,
    string TraceId,
    DateTimeOffset Timestamp);

/// <summary>Read-only port over the host's in-memory recent failure journal.</summary>
public interface IServerFailureReader
{
    IReadOnlyList<ServerFailureSnapshotDto> Recent();
}
