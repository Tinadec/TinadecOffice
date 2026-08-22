using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Lifecycle;

public sealed class LifecycleDbContext : DbContext
{
    public LifecycleDbContext(DbContextOptions<LifecycleDbContext> options) : base(options) { }

    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<EventIndexRecord> EventIndex => Set<EventIndexRecord>();
    public DbSet<ApprovalRequestRecord> ApprovalRequests => Set<ApprovalRequestRecord>();
    public DbSet<ApprovalDecisionRecord> ApprovalDecisions => Set<ApprovalDecisionRecord>();
    public DbSet<RunConfigurationBindingRecord> RunConfigurationBindings => Set<RunConfigurationBindingRecord>();
    public DbSet<ArtifactIndexRecord> ArtifactIndex => Set<ArtifactIndexRecord>();
    public DbSet<ControlEventIndexRecord> ControlEventIndex => Set<ControlEventIndexRecord>();
    public DbSet<ToolExecutionRecord> ToolExecutions => Set<ToolExecutionRecord>();
    public DbSet<RunCheckpointRecord> RunCheckpoints => Set<RunCheckpointRecord>();
    public DbSet<RunStreamRecord> RunStream => Set<RunStreamRecord>();
    public DbSet<RunStreamCursorRecord> RunStreamCursors => Set<RunStreamCursorRecord>();
    public DbSet<WorkspaceSnapshotRecord> WorkspaceSnapshots => Set<WorkspaceSnapshotRecord>();
    public DbSet<SessionMetadataSnapshotRecord> SessionMetadataSnapshots => Set<SessionMetadataSnapshotRecord>();
    public DbSet<UserToolActionRecord> UserToolActions => Set<UserToolActionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunRecord>(entity =>
        {
            entity.ToTable("runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(x => x.SessionId).HasColumnName("session_id");
            entity.Property(x => x.InitiatedByPrincipalId).HasColumnName("initiated_by_principal_id");
            entity.Property(x => x.TriggerMessageId).HasColumnName("trigger_message_id");
            entity.Property(x => x.TurnId).HasColumnName("turn_id");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.TaskRevision).HasColumnName("task_revision");
            entity.Property(x => x.LastEventSequence).HasColumnName("last_event_sequence");
            entity.Property(x => x.LastEventAt).HasColumnName("last_event_at");
            entity.Property(x => x.ApplicationMode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AgentMode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PermissionMode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RuntimeProfileId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ConfigurationHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.FrozenConfigurationReference).HasMaxLength(1024);
            entity.Property(x => x.FrozenConfigurationHash).HasMaxLength(128);
            entity.Property(x => x.FrozenConfigurationSchemaVersion).HasMaxLength(64);
            entity.Property(x => x.LeaseOwner).HasMaxLength(256);
            entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(4096);
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.CreatedAt });
            entity.HasIndex(x => new { x.SessionId, x.Status, x.UpdatedAt });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.TriggerMessageId }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
        });

        modelBuilder.Entity<EventIndexRecord>(entity =>
        {
            entity.ToTable("event_index");
            entity.HasKey(x => x.EventId);
            entity.Property(x => x.EventId).HasColumnName("event_id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(x => x.RunId).HasColumnName("run_id");
            entity.Property(x => x.SessionId).HasColumnName("session_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.EventType).HasColumnName("event_type");
            entity.Property(x => x.Severity).HasColumnName("severity");
            entity.Property(x => x.Sequence).HasColumnName("sequence");
            entity.Property(x => x.TaskId).HasColumnName("task_id");
            entity.Property(x => x.ApprovalId).HasColumnName("approval_id");
            entity.Property(x => x.ToolId).HasColumnName("tool_id");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.SchemaVersion).HasColumnName("schema_version");
            entity.Property(x => x.PayloadHash).HasColumnName("payload_hash");
            entity.Property(x => x.RelativeFilePath).HasColumnName("relative_file_path");
            entity.Property(x => x.ByteOffset).HasColumnName("byte_offset");
            entity.Property(x => x.ByteLength).HasColumnName("byte_length");
            entity.Property(x => x.Timestamp).HasColumnName("timestamp");
            entity.Property(x => x.EventType).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Severity).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.SchemaVersion).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RelativeFilePath).HasMaxLength(512).IsRequired();
            entity.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.Timestamp });
            entity.HasIndex(x => new { x.ProjectId, x.Timestamp });
            entity.HasIndex(x => new { x.RunId, x.Sequence });
            entity.HasIndex(x => new { x.ApprovalId, x.EventType, x.Timestamp });
        });
        modelBuilder.Entity<ApprovalRequestRecord>(entity => { entity.ToTable("approval_requests"); entity.HasKey(x => x.Id); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.ToolId).HasMaxLength(256).IsRequired(); entity.Property(x => x.Risk).HasMaxLength(32).IsRequired(); entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired(); entity.Property(x => x.NonceHash).HasMaxLength(128); entity.Property(x => x.NonceSecretReference).HasMaxLength(512); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.Property(x => x.Decision).HasMaxLength(32); entity.Property(x => x.ParametersReference).HasMaxLength(1024).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.ExpiresAt }); entity.HasIndex(x => new { x.RunId, x.ToolId, x.RequestHash }); entity.HasIndex(x => x.ExecutionId); entity.HasIndex(x => x.UserToolActionId); });
        modelBuilder.Entity<ApprovalDecisionRecord>(entity => { entity.ToTable("approval_decisions"); entity.HasKey(x => x.Id); entity.Property(x => x.Decision).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.ApprovalRequestId, x.CreatedAt }); });
        modelBuilder.Entity<RunConfigurationBindingRecord>(entity => { entity.ToTable("run_configuration_bindings"); entity.HasKey(x => new { x.RunId, x.ConfigurationKind, x.ConfigurationVersionId }); entity.Property(x => x.ConfigurationKind).HasMaxLength(64).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.ConfigurationVersionId }); });
        modelBuilder.Entity<ArtifactIndexRecord>(entity => { entity.ToTable("artifact_index"); entity.HasKey(x => x.Id); entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired(); entity.Property(x => x.MediaType).HasMaxLength(256).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.RunId, x.CreatedAt }); });
        modelBuilder.Entity<ControlEventIndexRecord>(entity => { entity.ToTable("control_event_index"); entity.HasKey(x => x.Id); entity.Property(x => x.AggregateType).HasMaxLength(64).IsRequired(); entity.Property(x => x.EventType).HasMaxLength(256).IsRequired(); entity.Property(x => x.RelativeFilePath).HasMaxLength(1024).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Timestamp }); });
        modelBuilder.Entity<ToolExecutionRecord>(entity =>
        {
            entity.ToTable("tool_executions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ToolId).HasMaxLength(256).IsRequired(); entity.Property(x => x.ToolCallKey).HasMaxLength(512).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.Property(x => x.Risk).HasMaxLength(32).IsRequired(); entity.Property(x => x.ErrorCategory).HasMaxLength(128);
        entity.Property(x => x.ParametersHash).HasMaxLength(128).IsRequired(); entity.Property(x => x.ParametersReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ResultReference).HasMaxLength(1024);
            entity.Property(x => x.PermissionRequestId);
            entity.Property(x => x.AuthorizationDecisionId);
            entity.Property(x => x.CapabilityLeaseId);
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.RunId, x.CreatedAt }); entity.HasIndex(x => new { x.RunId, x.ToolCallKey }).IsUnique(); entity.HasIndex(x => new { x.ApprovalId, x.Status });
        });
        modelBuilder.Entity<ApprovalRequestRecord>(entity => entity.HasIndex(x => x.ExecutionId).IsUnique());
        modelBuilder.Entity<RunCheckpointRecord>(entity =>
        {
            entity.ToTable("run_checkpoints"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Phase).HasMaxLength(64).IsRequired(); entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.RunId, x.Revision }).IsUnique(); entity.HasIndex(x => new { x.RunId, x.IdempotencyKey }).IsUnique();
        });
        modelBuilder.Entity<RunStreamRecord>(entity =>
        {
            entity.ToTable("run_stream"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(32).IsRequired(); entity.Property(x => x.IdempotencyKey).HasMaxLength(256);
            entity.Property(x => x.DeltaReference).HasMaxLength(1024); entity.Property(x => x.DeltaHash).HasMaxLength(128);
            entity.Property(x => x.UsageReference).HasMaxLength(1024); entity.Property(x => x.UsageHash).HasMaxLength(128);
            entity.Property(x => x.FinishReason).HasMaxLength(128); entity.Property(x => x.ErrorCategory).HasMaxLength(128); entity.Property(x => x.SafeErrorMessage).HasMaxLength(4096);
            entity.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique(); entity.HasIndex(x => new { x.RunId, x.TurnId, x.Sequence });
            entity.HasIndex(x => new { x.RunId, x.IdempotencyKey }).IsUnique();
        });
        modelBuilder.Entity<RunStreamCursorRecord>(entity =>
        {
            entity.ToTable("run_stream_cursors"); entity.HasKey(x => x.RunId);
            entity.Property(x => x.NextSequence).IsConcurrencyToken();
        });
        modelBuilder.Entity<WorkspaceSnapshotRecord>(entity =>
        {
            entity.ToTable("workspace_snapshots"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.WorkspaceHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256);
            entity.Property(x => x.LastRestoreIdempotencyKey).HasMaxLength(256);
            entity.Property(x => x.ConflictJson).HasMaxLength(16384);
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProjectId, x.CreatedAt });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProjectId, x.IdempotencyKey }).IsUnique();
        });
        modelBuilder.Entity<SessionMetadataSnapshotRecord>(entity =>
        {
            entity.ToTable("session_metadata_snapshots"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(128).IsRequired();
            entity.Property(x => x.SchemaVersion).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.Kind, x.Source, x.Revision }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.Kind, x.Source, x.ContentHash }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.CapturedAt });
        });
        modelBuilder.Entity<UserToolActionRecord>(entity =>
        {
            entity.ToTable("user_tool_actions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ToolId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ParametersReference).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.ParametersHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Risk).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ErrorCategory).HasMaxLength(128);
            entity.Property(x => x.SafeErrorMessage).HasMaxLength(4096);
            entity.Property(x => x.ResultReference).HasMaxLength(1024);
            entity.Property(x => x.SnapshotHash).HasMaxLength(128);
            entity.Property(x => x.SnapshotOverrideReason).HasMaxLength(4096);
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.CreatedAt });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => x.ActionApprovalId);
        });
        modelBuilder.UseTinadecSnakeCase();
    }
}

public sealed class RunRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid InitiatedByPrincipalId { get; set; }
    public Guid TriggerMessageId { get; set; }
    public Guid? TurnId { get; set; }
    public string Status { get; set; } = "planning";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Summary { get; set; }
    public long TaskRevision { get; set; }
    public long LastEventSequence { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public long ContextRevision { get; set; }
    public long ConfigurationVersion { get; set; }
    public string ConfigurationHash { get; set; } = string.Empty;
    public string ApplicationMode { get; set; } = "conversation";
    public string AgentMode { get; set; } = "auto";
    public string PermissionMode { get; set; } = "default";
    public string RuntimeProfileId { get; set; } = "conversation.auto";
    public string? FrozenConfigurationReference { get; set; }
    public string? FrozenConfigurationHash { get; set; }
    public long? FrozenConfigurationLength { get; set; }
    public string? FrozenConfigurationSchemaVersion { get; set; }
    public DateTimeOffset? FrozenConfigurationAt { get; set; }
    public Guid? CurrentCheckpointId { get; set; }
    public long CheckpointRevision { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? LeaseHeartbeatAt { get; set; }
    public long? LeaseExpiresUnixMilliseconds { get; set; }
    public long? LeaseHeartbeatUnixMilliseconds { get; set; }
    public int RecoveryCount { get; set; }
}

public sealed class EventIndexRecord
{
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RunId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ProjectId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public long Sequence { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? ApprovalId { get; set; }
    public string? ToolId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "1.0";
    public string PayloadHash { get; set; } = string.Empty;
    public string RelativeFilePath { get; set; } = string.Empty;
    public long ByteOffset { get; set; }
    public int ByteLength { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public sealed class ApprovalRequestRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid? SessionId { get; set; } public Guid? RunId { get; set; } public Guid? TaskId { get; set; } public Guid? AgentInstanceId { get; set; } public Guid? ExecutionId { get; set; } public Guid? UserToolActionId { get; set; } public Guid? PolicyVersionId { get; set; } public string Kind { get; set; } = string.Empty; public string ToolId { get; set; } = string.Empty; public string Risk { get; set; } = "low"; public string RequestHash { get; set; } = string.Empty; public string? NonceHash { get; set; } public string? NonceSecretReference { get; set; } public string ParametersReference { get; set; } = string.Empty; public string Summary { get; set; } = string.Empty; public string Status { get; set; } = "pending"; public string? Decision { get; set; } public string? DecisionReason { get; set; } public DateTimeOffset? DecidedAt { get; set; } public DateTimeOffset ExpiresAt { get; set; } public Guid RequestedByPrincipalId { get; set; } public Guid? ConsumedByExecutionId { get; set; } public DateTimeOffset? ConsumedAt { get; set; } [System.ComponentModel.DataAnnotations.Schema.NotMapped] public string Nonce { get; set; } = string.Empty; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class ApprovalDecisionRecord { public Guid Id { get; set; } public Guid ApprovalRequestId { get; set; } public string Decision { get; set; } = string.Empty; public string? Reason { get; set; } public Guid DecidedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class RunConfigurationBindingRecord { public Guid RunId { get; set; } public Guid TenantId { get; set; } public string ConfigurationKind { get; set; } = string.Empty; public Guid ConfigurationId { get; set; } public Guid ConfigurationVersionId { get; set; } public string ManifestHash { get; set; } = string.Empty; public DateTimeOffset BoundAt { get; set; } }
public sealed class ArtifactIndexRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid RunId { get; set; } public Guid? TaskId { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public string MediaType { get; set; } = string.Empty; public string Classification { get; set; } = "internal"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class ControlEventIndexRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid? WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid? ActorPrincipalId { get; set; } public string AggregateType { get; set; } = string.Empty; public Guid AggregateId { get; set; } public string EventType { get; set; } = string.Empty; public string RelativeFilePath { get; set; } = string.Empty; public long ByteOffset { get; set; } public int ByteLength { get; set; } public string PayloadHash { get; set; } = string.Empty; public DateTimeOffset Timestamp { get; set; } }
public sealed class ToolExecutionRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid SessionId { get; set; } public Guid RunId { get; set; } public Guid TaskId { get; set; } public Guid AgentInstanceId { get; set; } public Guid? ApprovalId { get; set; } public Guid? PermissionRequestId { get; set; } public Guid? AuthorizationDecisionId { get; set; } public Guid? CapabilityLeaseId { get; set; } public string ToolId { get; set; } = string.Empty; public string ToolCallKey { get; set; } = string.Empty; public string Risk { get; set; } = "low"; public bool MutatesWorkspace { get; set; } public bool RequiresApproval { get; set; } public int LeaseUses { get; set; } = 1; public string Status { get; set; } = "requested"; public string ParametersHash { get; set; } = string.Empty; public string ParametersReference { get; set; } = string.Empty; public long ParametersLength { get; set; } public string? ResultReference { get; set; } public string? ResultHash { get; set; } public long? ResultLength { get; set; } public string? ErrorCategory { get; set; } public string? SafeErrorMessage { get; set; } public int Attempt { get; set; } = 1; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } }
public sealed class RunCheckpointRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid RunId { get; set; } public long Revision { get; set; } public string Phase { get; set; } = string.Empty; public string IdempotencyKey { get; set; } = string.Empty; public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public long AppliedThroughEventSequence { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class RunStreamRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid RunId { get; set; } public Guid TurnId { get; set; } public Guid? MessageId { get; set; } public long Sequence { get; set; } public string Kind { get; set; } = string.Empty; public string? IdempotencyKey { get; set; } public string? DeltaReference { get; set; } public string? DeltaHash { get; set; } public long? DeltaLength { get; set; } public string? UsageReference { get; set; } public string? UsageHash { get; set; } public long? UsageLength { get; set; } public string? FinishReason { get; set; } public string? ErrorCategory { get; set; } public string? SafeErrorMessage { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class RunStreamCursorRecord { public Guid RunId { get; set; } public long NextSequence { get; set; } public DateTimeOffset UpdatedAt { get; set; } }

public sealed class WorkspaceSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public string Kind { get; set; } = "workspace";
    public string Status { get; set; } = "created";
    public bool IsGit { get; set; }
    public string WorkspaceHash { get; set; } = string.Empty;
    public string ContentReference { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long ContentLength { get; set; }
    public int FileCount { get; set; }
    public Guid? BaseSnapshotId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? LastRestoreIdempotencyKey { get; set; }
    public string? ConflictJson { get; set; }
    public int AppliedFileCount { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Immutable content pointer for the session runtime projection. It intentionally
/// has no filesystem or MAF fields; those concerns have separate durable models.
/// </summary>
public sealed class SessionMetadataSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ProjectId { get; set; }
    public string Kind { get; set; } = "session_runtime";
    public string Source { get; set; } = "lifecycle_projection";
    public string SchemaVersion { get; set; } = "1.0";
    public long Revision { get; set; }
    public string ContentReference { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long ContentLength { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

/// <summary>Core-owned state for a user initiated tool action. It deliberately has no RunId.</summary>
public sealed class UserToolActionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PrincipalId { get; set; }
    public string ToolId { get; set; } = string.Empty;
    public string ParametersReference { get; set; } = string.Empty;
    public long ParametersLength { get; set; }
    public string ParametersHash { get; set; } = string.Empty;
    public string Risk { get; set; } = "low";
    public bool MutatesWorkspace { get; set; }
    public bool RequiresApproval { get; set; }
    public string Status { get; set; } = "requested";
    public string? IdempotencyKey { get; set; }
    public Guid? PermissionRequestId { get; set; }
    public Guid? AuthorizationDecisionId { get; set; }
    public Guid? CapabilityLeaseId { get; set; }
    public Guid? ActionApprovalId { get; set; }
    public Guid? SnapshotId { get; set; }
    public string? SnapshotHash { get; set; }
    public bool SnapshotOverride { get; set; }
    public string? SnapshotOverrideReason { get; set; }
    public string? ResultReference { get; set; }
    public string? ResultHash { get; set; }
    public long? ResultLength { get; set; }
    public string? ErrorCategory { get; set; }
    public string? SafeErrorMessage { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
