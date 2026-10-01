using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>Bounds of one topology read. The defaults fit a model's context; the UI may ask for more, never unbounded.</summary>
public sealed record SessionTopologyQuery(
    int MaxRuns = 8,
    int MaxTasksPerRun = 60,
    int MaxInstancesPerRun = 60,
    int MaxLeases = 60,
    int MaxMembers = 80,
    bool IncludeFinishedRuns = true,
    Guid? RunId = null)
{
    public const int Ceiling = 500;

    public SessionTopologyQuery Clamped() => this with
    {
        MaxRuns = Math.Clamp(MaxRuns, 1, 50),
        MaxTasksPerRun = Math.Clamp(MaxTasksPerRun, 1, Ceiling),
        MaxInstancesPerRun = Math.Clamp(MaxInstancesPerRun, 1, Ceiling),
        MaxLeases = Math.Clamp(MaxLeases, 1, Ceiling),
        MaxMembers = Math.Clamp(MaxMembers, 1, Ceiling)
    };
}

/// <summary>
/// The session as a graph: runs → instances → tasks → resources, plus the organization's members.
/// Read-only; composed at the host from the lifecycle, the checkpoints, the ledger and TinaChat.
/// </summary>
public interface ISessionTopology
{
    /// <summary>Null when the session does not exist in the caller's scope.</summary>
    Task<SessionTopologyDto?> GetAsync(Guid sessionId, SessionTopologyQuery query, CancellationToken cancellationToken = default);
}
