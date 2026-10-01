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
    /// Compares one snapshot's manifest against what the workspace holds now, per file. This is
    /// the read side of reviewing a write: the snapshot was taken *before* the tool ran, so a row
    /// says what the agent is about to change and whether it still stands. Restoring the whole
    /// workspace is the wrong granularity for that question — a user who approves three of five
    /// files must not lose the other two.
    /// </summary>
    Task<IReadOnlyList<WorkspaceFileChange>> ListFileChangesAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The two bounded texts behind one <see cref="WorkspaceFileChange"/>. Served apart from the
    /// listing because a manifest can hold thousands of files and a review card reads one diff at
    /// a time.
    /// </summary>
    Task<WorkspaceFileDiff?> GetFileDiffAsync(
        Guid snapshotId,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one file back to its pre-capture content. <paramref name="request"/>.ExpectedSha256
    /// must be the hash the caller was shown; without it this is an unguarded overwrite.
    /// </summary>
    Task<WorkspaceFileChange> RestoreFileAsync(
        Guid snapshotId,
        WorkspaceFileRestoreRequest request,
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

/// <summary>
/// One file of a snapshot compared against the workspace as it stands now.
/// <c>added</c> is in the workspace only, <c>deleted</c> in the snapshot only,
/// <c>modified</c> in both with different hashes, <c>unchanged</c> in both alike.
/// <see cref="Restorable"/> is the answer to "can this row be undone?": the manifest
/// holds the pre-capture bytes only for files under the provider's content ceiling, so a
/// large file is reportable but not reversible, and a review card must not offer a button
/// that cannot do anything.
/// </summary>
public sealed record WorkspaceFileChange(
    string Path,
    string Status,
    string? BeforeSha256,
    string? AfterSha256,
    long? BeforeLength,
    long? AfterLength,
    bool Restorable);

/// <summary>
/// The two bodies behind one change, bounded by the diff ceiling. A side that is absent from
/// disk or workspace has <see cref="WorkspaceFileSide.Present"/> false rather than an empty
/// text, so "the file was deleted" never renders as "the file became empty".
/// </summary>
public sealed record WorkspaceFileDiff(
    string Path,
    string Status,
    WorkspaceFileSide Before,
    WorkspaceFileSide After,
    bool Restorable);

public sealed record WorkspaceFileSide(
    bool Present,
    long? Length,
    string? Sha256,
    bool Binary,
    bool Truncated,
    string? Text);

/// <summary>
/// <paramref name="ExpectedSha256"/> is the hash the reviewer was just shown for that path, and it
/// must be present: null means nobody reviewed anything, so it is refused. An empty string is a
/// different fact and not a missing one — it says "the file was not there when I looked", which is
/// what an undelete has to assert.
/// </summary>
public sealed record WorkspaceFileRestoreRequest(
    string Path,
    string? ExpectedSha256);

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
/// The snapshot knows the file but not its bytes, so this one cannot be undone. Distinct from a
/// conflict on purpose: nothing raced, the row is simply irreversible, and a caller that offered a
/// restore button needs that said back rather than "workspace changed".
/// </summary>
public sealed class WorkspaceSnapshotFileNotCapturedException : InvalidOperationException
{
    public WorkspaceSnapshotFileNotCapturedException(string path)
        : base($"The workspace snapshot did not capture the content of '{path}', so it cannot be restored.")
        => Path = path;

    public string Path { get; }
}

/// <summary>
/// A caller-supplied path that will not stay inside the workspace root. Its own type, because
/// "you asked for something outside the box" and "you left a required field out" both arrive as
/// bad requests but must not share a machine code: a client that repaired the second would still
/// be refused by the first.
/// </summary>
public sealed class WorkspaceSnapshotPathException : ArgumentException
{
    public WorkspaceSnapshotPathException(string path)
        : base($"'{path}' does not resolve to a file inside the workspace root.")
        => Path = path;

    public string Path { get; }
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
