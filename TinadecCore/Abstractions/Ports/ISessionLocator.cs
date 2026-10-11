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

public sealed record SessionModelOverride(Guid ProviderInstanceId, string? Model);

public sealed record SessionReference(
    Guid SessionId,
    Guid? ProjectId,
    Guid TenantId,
    Guid WorkspaceId,
    Guid? ModeVersionId = null,
    SessionModelOverride? MeetingModelOverride = null,
    // ConversationIdentity (DmaEA graph orchestration), frozen at session creation.
    // Null on sessions created before the columns existed — the freeze gate keeps
    // the legacy literal-meeting semantics for those rows.
    string? ConversationNodeKey = null,
    string? ConversationTemplateSlug = null,
    SpaceRunOptions? SpaceOptions = null,
    string PermissionMode = "default",
    string ViewMode = "flat",
    long SettingsRevision = 0,
    // "concise" | "standard" | "detailed"; null on sessions predating the column.
    string? TranscriptTemplate = null);

public sealed record ProjectReference(Guid ProjectId, Guid TenantId, Guid WorkspaceId, string RootPath);
