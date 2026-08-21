namespace TinadecCore.Abstractions.Ports;

/// <summary>Minimal cross-module lookup used to validate lifecycle ownership boundaries.</summary>
public interface ISessionLocator
{
    Task<SessionReference?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a project-owned workspace root after the caller has already
    /// established the tenant/workspace boundary through a session or run.
    /// </summary>
    Task<ProjectReference?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed record SessionReference(Guid SessionId, Guid ProjectId, Guid TenantId, Guid WorkspaceId, Guid? ModeVersionId = null, string? MeetingModel = null, string? MeetingProviderId = null);

public sealed record ProjectReference(Guid ProjectId, Guid TenantId, Guid WorkspaceId, string RootPath);
