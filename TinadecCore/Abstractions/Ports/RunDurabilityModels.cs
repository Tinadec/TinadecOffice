namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Result of an idempotent run admission. A repeated trigger message returns the
/// original run instead of creating a second durable execution.
/// </summary>
public sealed record RunStartResult(string RunId, bool Existing);

/// <summary>
/// Immutable, resolved configuration material retained for one run. The content is
/// intentionally opaque to Lifecycle; DmaEA owns its schema and interpretation.
/// </summary>
public sealed record FrozenRunConfigurationWrite(
    string SchemaVersion,
    string Content,
    IReadOnlyList<RunConfigurationBinding> Bindings);

public sealed record FrozenRunConfiguration(
    string RunId,
    string SchemaVersion,
    string Content,
    string ContentReference,
    string ContentHash,
    long ContentLength,
    DateTimeOffset FrozenAt,
    IReadOnlyList<RunConfigurationBinding> Bindings);

/// <summary>Immutable version reference resolved into a frozen run configuration.</summary>
public sealed record RunConfigurationBinding(
    string ConfigurationKind,
    Guid ConfigurationId,
    Guid ConfigurationVersionId,
    string ManifestHash);

/// <summary>
/// Opaque, immutable checkpoint body written with optimistic revision control. The
/// execution engine owns the body schema; Lifecycle owns ordering and retention.
/// </summary>
public sealed record RunCheckpointWrite(
    long ExpectedRevision,
    string Phase,
    string Content,
    long AppliedThroughEventSequence = 0,
    string? IdempotencyKey = null);

public sealed record RunCheckpoint(
    Guid Id,
    string RunId,
    long Revision,
    string Phase,
    string Content,
    string ContentReference,
    string ContentHash,
    long ContentLength,
    long AppliedThroughEventSequence,
    string IdempotencyKey,
    DateTimeOffset CreatedAt);

/// <summary>Thrown when a checkpoint writer has not observed the latest revision.</summary>
public sealed class RunCheckpointConflictException : InvalidOperationException
{
    public RunCheckpointConflictException(string runId, long expectedRevision, long actualRevision)
        : base($"Run '{runId}' checkpoint revision conflict: expected {expectedRevision}, actual {actualRevision}.")
    {
        RunId = runId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string RunId { get; }
    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}

/// <summary>Database-backed ownership lease used to ensure only one host resumes a run.</summary>
public sealed record RunLease(
    string RunId,
    string OwnerId,
    bool Acquired,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? HeartbeatAt,
    int RecoveryCount);

/// <summary>
/// Durable stream item. Text and usage values are persisted outside the event payload
/// and are loaded only for authorized stream readers.
/// </summary>
public sealed record DurableRunStreamAppend(
    Guid TurnId,
    string Kind,
    Guid? MessageId = null,
    string? Delta = null,
    string? UsageJson = null,
    string? FinishReason = null,
    string? ErrorCategory = null,
    string? SafeErrorMessage = null,
    string? IdempotencyKey = null);

public sealed record DurableRunStreamChunk(
    string RunId,
    Guid TurnId,
    Guid? MessageId,
    long Sequence,
    string Kind,
    string? Delta,
    string? UsageJson,
    string? FinishReason,
    string? ErrorCategory,
    string? SafeErrorMessage,
    DateTimeOffset CreatedAt);
