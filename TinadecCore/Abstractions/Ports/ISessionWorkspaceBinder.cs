namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Binds a projectless (free-conversation) session to a real project workspace:
/// the project record is found-or-created for the directory and the session is
/// migrated onto it atomically. Used by the Core-owned create_workspace virtual
/// tool after its approval gate has passed.
/// </summary>
public interface ISessionWorkspaceBinder
{
    Task<SessionWorkspaceBinding> BindSessionToWorkspaceAsync(Guid sessionId, string name, string path, CancellationToken cancellationToken = default);
}

public sealed record SessionWorkspaceBinding(
    Guid SessionId,
    Guid ProjectId,
    string ProjectName,
    string RootPath,
    bool ProjectCreated);
