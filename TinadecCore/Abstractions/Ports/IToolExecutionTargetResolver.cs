namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The concrete execution root selected by a durable worktree or environment
/// assignment. The project root remains the authorization boundary; this target
/// is the provider's actual working root.
/// </summary>
public sealed record ToolExecutionTarget(
    string Kind,
    string Key,
    string RootPath,
    Guid LeaseId);

public sealed record ToolExecutionTargetResolution(
    ToolExecutionTarget? Target,
    string? Error = null)
{
    public bool IsRejected => !string.IsNullOrWhiteSpace(Error);
}

/// <summary>
/// Resolves the current run/task execution target from active durable leases.
/// Returning an error is fail-closed: a lease without a usable provider target
/// must never silently fall back to the project root.
/// </summary>
public interface IToolExecutionTargetResolver
{
    Task<ToolExecutionTargetResolution> ResolveAsync(
        Guid sessionId,
        Guid runId,
        Guid taskId,
        string toolId,
        string projectRoot,
        CancellationToken cancellationToken = default);
}
