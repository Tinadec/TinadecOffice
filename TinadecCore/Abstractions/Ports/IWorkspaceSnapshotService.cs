namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Core-owned workspace snapshot boundary. This is deliberately independent of
/// MAF checkpoints: it describes externally visible workspace state and can be
/// restored only after a conflict check.
/// </summary>
public interface IWorkspaceSnapshotService
{
    Task<WorkspaceSnapshot> CreateAsync(
        WorkspaceSnapshotCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceSnapshot>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<WorkspaceSnapshot?> GetAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);

    Task<WorkspaceRestoreResult> RestoreAsync(
        Guid snapshotId,
        WorkspaceRestoreRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-captures provider state before a write resumes. A stored snapshot hash
    /// alone is not an authorization fact if the workspace changed meanwhile.
    /// </summary>
    Task<WorkspaceSnapshotValidationResult> ValidateAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the durable session runtime projection used by Debug Studio. This
    /// is deliberately separate from filesystem snapshots, conversation context
    /// snapshots, and MAF run checkpoints.
    /// </summary>
    Task<WorkspaceSessionSnapshot?> GetSessionProjectionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}

public sealed record WorkspaceSnapshotCreateRequest(
    Guid ProjectId,
    string? IdempotencyKey = null,
    string? ExpectedWorkspaceHash = null,
    bool IncludeHidden = false,
    int MaxFiles = 10_000,
    long MaxBytes = 64 * 1024 * 1024);

public sealed record WorkspaceRestoreRequest(
    string? IdempotencyKey = null,
    string? ExpectedWorkspaceHash = null,
    bool AllowConflicts = false);

public sealed record WorkspaceSnapshot(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid ProjectId,
    string Kind,
    string Status,
    bool IsGit,
    string WorkspaceHash,
    string ContentReference,
    string ContentHash,
    long ContentLength,
    int FileCount,
    Guid? BaseSnapshotId,
    DateTimeOffset CreatedAt);

public sealed record WorkspaceRestoreResult(
    string Status,
    Guid SnapshotId,
    string WorkspaceHash,
    IReadOnlyList<string> Conflicts,
    int AppliedFileCount,
    DateTimeOffset? RestoredAt = null);

public sealed record WorkspaceSnapshotValidationResult(
    bool IsValid,
    Guid SnapshotId,
    string ExpectedWorkspaceHash,
    string? CurrentWorkspaceHash,
    IReadOnlyList<string> Conflicts);

public sealed class WorkspaceSnapshotConflictException : InvalidOperationException
{
    public WorkspaceSnapshotConflictException(string message, IReadOnlyList<string> conflicts)
        : base(message) => Conflicts = conflicts;

    public IReadOnlyList<string> Conflicts { get; }
}

/// <summary>
/// Durable, tenant-scoped projection of one session's Core-owned runtime state.
/// Kind/source are explicit so consumers cannot mistake this for a file or MAF
/// checkpoint snapshot.
/// </summary>
public sealed record WorkspaceSessionSnapshot(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid SessionId,
    Guid? ProjectId,
    string Kind,
    string Source,
    string SchemaVersion,
    long Revision,
    DateTimeOffset CapturedAt,
    IReadOnlyList<WorkspaceRunMetadata> Runs,
    IReadOnlyList<WorkspaceTaskMetadata> Tasks,
    IReadOnlyList<WorkspaceEventMetadata> Events);

public sealed record WorkspaceRunMetadata(
    Guid Id,
    Guid SessionId,
    Guid TriggerMessageId,
    Guid? InitiatedByPrincipalId,
    string Status,
    string? Summary,
    long TaskRevision,
    long LastEventSequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record WorkspaceTaskMetadata(
    Guid RunId,
    int Index,
    System.Text.Json.JsonElement Value);

public sealed record WorkspaceEventMetadata(
    Guid EventId,
    Guid RunId,
    Guid SessionId,
    long Sequence,
    string EventType,
    string Severity,
    Guid? TaskId,
    Guid? ApprovalId,
    string? ToolId,
    string Summary,
    string SchemaVersion,
    string PayloadHash,
    DateTimeOffset Timestamp);
