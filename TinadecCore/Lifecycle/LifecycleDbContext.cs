using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Lifecycle;

public sealed class LifecycleDbContext : DbContext
{
    public LifecycleDbContext(DbContextOptions<LifecycleDbContext> options) : base(options) { }

    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<EventIndexRecord> EventIndex => Set<EventIndexRecord>();
    public DbSet<PolicySetRecord> PolicySets => Set<PolicySetRecord>();
    public DbSet<PolicyVersionRecord> PolicyVersions => Set<PolicyVersionRecord>();
    public DbSet<ApprovalRequestRecord> ApprovalRequests => Set<ApprovalRequestRecord>();
    public DbSet<ApprovalDecisionRecord> ApprovalDecisions => Set<ApprovalDecisionRecord>();
    public DbSet<RunConfigurationBindingRecord> RunConfigurationBindings => Set<RunConfigurationBindingRecord>();
    public DbSet<ArtifactIndexRecord> ArtifactIndex => Set<ArtifactIndexRecord>();
    public DbSet<ControlEventIndexRecord> ControlEventIndex => Set<ControlEventIndexRecord>();

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
            entity.Property(x => x.TriggerMessageId).HasColumnName("trigger_message_id");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.TaskRevision).HasColumnName("task_revision");
            entity.Property(x => x.LastEventSequence).HasColumnName("last_event_sequence");
            entity.Property(x => x.LastEventAt).HasColumnName("last_event_at");
            entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(4096);
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.CreatedAt });
            entity.HasIndex(x => new { x.SessionId, x.Status, x.UpdatedAt });
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
        modelBuilder.Entity<PolicySetRecord>(entity => { entity.ToTable("policy_sets"); entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(256).IsRequired(); entity.Property(x => x.Scope).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProjectId, x.Name, x.DeletedAt }).IsUnique(); });
        modelBuilder.Entity<PolicyVersionRecord>(entity => { entity.ToTable("policy_versions"); entity.HasKey(x => x.Id); entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.HasIndex(x => new { x.PolicySetId, x.Version }).IsUnique(); });
        modelBuilder.Entity<ApprovalRequestRecord>(entity => { entity.ToTable("approval_requests"); entity.HasKey(x => x.Id); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.ToolId).HasMaxLength(256).IsRequired(); entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.Property(x => x.ParametersReference).HasMaxLength(1024).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.ExpiresAt }); entity.HasIndex(x => new { x.RunId, x.ToolId, x.RequestHash }); });
        modelBuilder.Entity<ApprovalDecisionRecord>(entity => { entity.ToTable("approval_decisions"); entity.HasKey(x => x.Id); entity.Property(x => x.Decision).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.ApprovalRequestId, x.CreatedAt }); });
        modelBuilder.Entity<RunConfigurationBindingRecord>(entity => { entity.ToTable("run_configuration_bindings"); entity.HasKey(x => new { x.RunId, x.ConfigurationKind, x.ConfigurationVersionId }); entity.Property(x => x.ConfigurationKind).HasMaxLength(64).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.ConfigurationVersionId }); });
        modelBuilder.Entity<ArtifactIndexRecord>(entity => { entity.ToTable("artifact_index"); entity.HasKey(x => x.Id); entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired(); entity.Property(x => x.MediaType).HasMaxLength(256).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.RunId, x.CreatedAt }); });
        modelBuilder.Entity<ControlEventIndexRecord>(entity => { entity.ToTable("control_event_index"); entity.HasKey(x => x.Id); entity.Property(x => x.AggregateType).HasMaxLength(64).IsRequired(); entity.Property(x => x.EventType).HasMaxLength(256).IsRequired(); entity.Property(x => x.RelativeFilePath).HasMaxLength(1024).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Timestamp }); });
    }
}

public sealed class RunRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid TriggerMessageId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Summary { get; set; }
    public long TaskRevision { get; set; }
    public long LastEventSequence { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
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

public sealed class PolicySetRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid? WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public string Scope { get; set; } = "workspace"; public string Name { get; set; } = string.Empty; public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class PolicyVersionRecord { public Guid Id { get; set; } public Guid PolicySetId { get; set; } public int Version { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class ApprovalRequestRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid? SessionId { get; set; } public Guid? RunId { get; set; } public Guid? TaskId { get; set; } public Guid? PolicyVersionId { get; set; } public string Kind { get; set; } = string.Empty; public string ToolId { get; set; } = string.Empty; public string RequestHash { get; set; } = string.Empty; public string ParametersReference { get; set; } = string.Empty; public string Summary { get; set; } = string.Empty; public string Status { get; set; } = "pending"; public DateTimeOffset ExpiresAt { get; set; } public Guid RequestedByPrincipalId { get; set; } public Guid? ConsumedByExecutionId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class ApprovalDecisionRecord { public Guid Id { get; set; } public Guid ApprovalRequestId { get; set; } public string Decision { get; set; } = string.Empty; public string? Reason { get; set; } public Guid DecidedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class RunConfigurationBindingRecord { public Guid RunId { get; set; } public Guid TenantId { get; set; } public string ConfigurationKind { get; set; } = string.Empty; public Guid ConfigurationId { get; set; } public Guid ConfigurationVersionId { get; set; } public string ManifestHash { get; set; } = string.Empty; public DateTimeOffset BoundAt { get; set; } }
public sealed class ArtifactIndexRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid RunId { get; set; } public Guid? TaskId { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public string MediaType { get; set; } = string.Empty; public string Classification { get; set; } = "internal"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class ControlEventIndexRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid? WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid? ActorPrincipalId { get; set; } public string AggregateType { get; set; } = string.Empty; public Guid AggregateId { get; set; } public string EventType { get; set; } = string.Empty; public string RelativeFilePath { get; set; } = string.Empty; public long ByteOffset { get; set; } public int ByteLength { get; set; } public string PayloadHash { get; set; } = string.Empty; public DateTimeOffset Timestamp { get; set; } }
