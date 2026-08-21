using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;

namespace TinadecCore.Memory;

public sealed class MemoryDbContext : DbContext
{
    public MemoryDbContext(DbContextOptions<MemoryDbContext> options) : base(options) { }

    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();
    public DbSet<TurnRecord> Turns => Set<TurnRecord>();
    public DbSet<ContextSnapshotRecord> ContextSnapshots => Set<ContextSnapshotRecord>();
    public DbSet<ContextPatchRecord> ContextPatches => Set<ContextPatchRecord>();
    public DbSet<MemoryCandidateRecord> MemoryCandidates => Set<MemoryCandidateRecord>();
    public DbSet<MemoryItemRecord> MemoryItems => Set<MemoryItemRecord>();
    public DbSet<MemoryVersionRecord> MemoryVersions => Set<MemoryVersionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectRecord>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.RootPath).HasColumnName("root_path");
            entity.Property(x => x.NormalizedRootPath).HasColumnName("normalized_root_path");
            entity.Property(x => x.Kind).HasColumnName("kind");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.Archived).HasColumnName("archived");
            entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RootPath).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.NormalizedRootPath).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.NormalizedRootPath }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Archived, x.UpdatedAt });
            entity.HasIndex(x => new { x.Archived, x.UpdatedAt });
        });

        modelBuilder.Entity<SessionRecord>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.Mode).HasColumnName("mode");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.HistoryRevision).HasColumnName("history_revision");
            entity.Property(x => x.ModeVersionId).HasColumnName("mode_version_id");
            entity.Property(x => x.MeetingModel).HasColumnName("meeting_model");
            entity.Property(x => x.MeetingProviderId).HasColumnName("meeting_provider_id");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.Archived).HasColumnName("archived");
            entity.Property(x => x.Title).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Mode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(4096);
            entity.Property(x => x.MeetingModel).HasMaxLength(256);
            entity.Property(x => x.MeetingProviderId).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProjectId, x.Archived, x.UpdatedAt });
        });

        modelBuilder.Entity<MessageRecord>(entity =>
        {
            entity.ToTable("messages"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired(); entity.Property(x => x.ClientMessageId).HasMaxLength(256);
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.ClientMessageId }).IsUnique();
            entity.HasIndex(x => new { x.RunId, x.CreatedAt });
        });
        modelBuilder.Entity<TurnRecord>(entity =>
        {
            entity.ToTable("turns"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(32).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.CreatedAt });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.UserMessageId }).IsUnique();
        });
        modelBuilder.Entity<ContextSnapshotRecord>(entity =>
        {
            entity.ToTable("context_snapshots"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.Revision }).IsUnique();
        });
        modelBuilder.Entity<ContextPatchRecord>(entity =>
        {
            entity.ToTable("context_patches"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId, x.CreatedAt });
        });
        modelBuilder.Entity<MemoryCandidateRecord>(entity =>
        {
            entity.ToTable("memory_candidates"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasMaxLength(32).IsRequired(); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.CreatedAt });
        });
        modelBuilder.Entity<MemoryItemRecord>(entity =>
        {
            entity.ToTable("memory_items"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasMaxLength(32).IsRequired(); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProjectId, x.Scope, x.Status });
        });
        modelBuilder.Entity<MemoryVersionRecord>(entity =>
        {
            entity.ToTable("memory_versions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.MemoryItemId, x.Version }).IsUnique();
        });
        modelBuilder.UseTinadecSnakeCase();
    }
}

public sealed class ProjectRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string NormalizedRootPath { get; set; } = string.Empty;
    public string Kind { get; set; } = "workspace";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Archived { get; set; }
}

public sealed class SessionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public string Mode { get; set; } = "default";
    public string? Summary { get; set; }
    public long HistoryRevision { get; set; }
    public Guid? ModeVersionId { get; set; }
    public string? MeetingModel { get; set; }
    public string? MeetingProviderId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Archived { get; set; }
}

public sealed class MessageRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid SessionId { get; set; } public Guid? RunId { get; set; } public Guid? TurnId { get; set; } public string? ClientMessageId { get; set; } public long Sequence { get; set; } public string Role { get; set; } = "user"; public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class TurnRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid SessionId { get; set; } public Guid UserMessageId { get; set; } public Guid? AssistantMessageId { get; set; } public Guid? RunId { get; set; } public string Kind { get; set; } = "new_task"; public string Status { get; set; } = "accepted"; public long BaseContextRevision { get; set; } public long ResultContextRevision { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } }
public sealed class ContextSnapshotRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid SessionId { get; set; } public Guid? RunId { get; set; } public long Revision { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class ContextPatchRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid SessionId { get; set; } public Guid? RunId { get; set; } public Guid? AgentInstanceId { get; set; } public long BaseRevision { get; set; } public long? AppliedRevision { get; set; } public string Status { get; set; } = "pending"; public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? AppliedAt { get; set; } }
public sealed class MemoryCandidateRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid? AgentProfileId { get; set; } public Guid SourceRunId { get; set; } public Guid GeneratedByInstanceId { get; set; } public string Scope { get; set; } = "workspace"; public string Kind { get; set; } = "fact"; public string Status { get; set; } = "proposed"; public double Confidence { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public string? DecisionReason { get; set; } public Guid? PromotedMemoryItemId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid? DecidedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class MemoryItemRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid? PrincipalId { get; set; } public Guid? AgentProfileId { get; set; } public string Scope { get; set; } = "workspace"; public string Kind { get; set; } = "fact"; public string Status { get; set; } = "active"; public int CurrentVersion { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? RevokedAt { get; set; } public Guid? SupersededById { get; set; } }
public sealed class MemoryVersionRecord { public Guid Id { get; set; } public Guid MemoryItemId { get; set; } public int Version { get; set; } public Guid? SourceCandidateId { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
