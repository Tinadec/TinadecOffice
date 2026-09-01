using Microsoft.EntityFrameworkCore;

namespace TinadecCore.DmaEA;

public sealed class AgentControlDbContext : DbContext
{
    public AgentControlDbContext(DbContextOptions<AgentControlDbContext> options) : base(options) { }
    public DbSet<AgentInstanceRecord> Instances => Set<AgentInstanceRecord>();
    public DbSet<AgentCandidateRecord> Candidates => Set<AgentCandidateRecord>();
    public DbSet<RuntimeProfileOverrideRecord> ProfileOverrides => Set<RuntimeProfileOverrideRecord>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentInstanceRecord>(entity =>
        {
            entity.ToTable("agent_instances"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Layer).HasMaxLength(32).IsRequired(); entity.Property(x => x.Role).HasMaxLength(128).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.DefinitionReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.DefinitionHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AgentDefinitionId).IsRequired(); entity.Property(x => x.AgentVersionId).IsRequired(); entity.Property(x => x.AgentVersionHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.RunId, x.AgentVersionId });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.RunId, x.CreatedAt }); entity.HasIndex(x => new { x.ParentInstanceId, x.CreatedAt });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.RunId, x.LaneKey });
        });
        modelBuilder.Entity<AgentCandidateRecord>(entity =>
        {
            entity.ToTable("agent_candidates"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.Property(x => x.Name).HasMaxLength(256).IsRequired(); entity.Property(x => x.Layer).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ProposalReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ProposalHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.CreatedAt });
        });
        modelBuilder.Entity<RuntimeProfileOverrideRecord>(entity =>
        {
            entity.ToTable("runtime_profile_overrides"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfileId).HasMaxLength(256).IsRequired(); entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProfileId, x.Version }).IsUnique();
        });
        modelBuilder.UseTinadecSnakeCase();
    }
}

/// <summary>Agent instance row; LaneKey is the lane it executes in, null reads as the implicit "main" lane.</summary>
public sealed class AgentInstanceRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid SessionId { get; set; } public Guid RunId { get; set; } public Guid? TaskNodeId { get; set; } public Guid? ParentInstanceId { get; set; } public Guid? ProfileId { get; set; } public Guid? CreatedByProfileId { get; set; } public Guid AgentDefinitionId { get; set; } public Guid AgentVersionId { get; set; } public string AgentVersionHash { get; set; } = string.Empty; public string Layer { get; set; } = string.Empty; public string Role { get; set; } = string.Empty; public string? LaneKey { get; set; } public int GenerationDepth { get; set; } public bool Generated { get; set; } public string Status { get; set; } = "created"; public string DefinitionReference { get; set; } = string.Empty; public string DefinitionHash { get; set; } = string.Empty; public long DefinitionLength { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? ReleasedAt { get; set; } }
public sealed class AgentCandidateRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid SourceRunId { get; set; } public Guid SourceInstanceId { get; set; } public Guid GeneratedByInstanceId { get; set; } public string Name { get; set; } = string.Empty; public string Layer { get; set; } = "execution"; public string AgentType { get; set; } = "generated"; public string Status { get; set; } = "proposed"; public double ConfidenceScore { get; set; } public string ProposalReference { get; set; } = string.Empty; public string ProposalHash { get; set; } = string.Empty; public long ProposalLength { get; set; } public Guid? PromotedAgentId { get; set; } public string? DecisionReason { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid? DecidedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class RuntimeProfileOverrideRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public string ProfileId { get; set; } = string.Empty; public int Version { get; set; } public bool Enabled { get; set; } = true; public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
