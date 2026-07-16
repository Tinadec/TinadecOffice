using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Lifecycle;

public sealed class LifecycleDbContext : DbContext
{
    public LifecycleDbContext(DbContextOptions<LifecycleDbContext> options) : base(options) { }

    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<EventIndexRecord> EventIndex => Set<EventIndexRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunRecord>(entity =>
        {
            entity.ToTable("runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
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
            entity.HasIndex(x => new { x.SessionId, x.CreatedAt });
            entity.HasIndex(x => new { x.SessionId, x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<EventIndexRecord>(entity =>
        {
            entity.ToTable("event_index");
            entity.HasKey(x => x.EventId);
            entity.Property(x => x.EventId).HasColumnName("event_id");
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
            entity.HasIndex(x => new { x.SessionId, x.Timestamp });
            entity.HasIndex(x => new { x.ProjectId, x.Timestamp });
            entity.HasIndex(x => new { x.RunId, x.Sequence });
            entity.HasIndex(x => new { x.ApprovalId, x.EventType, x.Timestamp });
        });
    }
}

public sealed class RunRecord
{
    public Guid Id { get; set; }
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
