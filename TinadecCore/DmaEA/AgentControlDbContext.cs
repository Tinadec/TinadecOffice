using Microsoft.EntityFrameworkCore;

namespace TinadecCore.DmaEA;

public sealed class AgentControlDbContext : DbContext
{
    public AgentControlDbContext(DbContextOptions<AgentControlDbContext> options) : base(options) { }
    public DbSet<AgentInstanceRecord> Instances => Set<AgentInstanceRecord>();
    public DbSet<AgentCandidateRecord> Candidates => Set<AgentCandidateRecord>();
    public DbSet<AgentEvalSuiteRecord> EvalSuites => Set<AgentEvalSuiteRecord>();
    public DbSet<AgentEvalRunRecord> EvalRuns => Set<AgentEvalRunRecord>();
    public DbSet<LiveDigestSettingsRecord> LiveDigestSettings => Set<LiveDigestSettingsRecord>();
    public DbSet<LiveDigestSnapshotRecord> LiveDigestSnapshots => Set<LiveDigestSnapshotRecord>();
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
        modelBuilder.Entity<AgentEvalSuiteRecord>(entity =>
        {
            entity.ToTable("agent_eval_suites"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.DefinitionReference).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.DefinitionHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Slug }).IsUnique().HasDatabaseName("ix_agent_eval_suites_scope_slug");
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.CreatedAt }).HasDatabaseName("ix_agent_eval_suites_scope_created");
        });
        modelBuilder.Entity<AgentEvalRunRecord>(entity =>
        {
            entity.ToTable("agent_eval_runs"); entity.HasKey(x => x.Id);
            entity.Property(x => x.SuiteHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ControlVersionHash).HasMaxLength(128);
            entity.Property(x => x.ControlSystemPromptHash).HasMaxLength(128);
            entity.Property(x => x.TreatmentProposalHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.TreatmentSystemPromptHash).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.GateStatus).HasMaxLength(32);
            entity.Property(x => x.ModelRoute).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Model).HasMaxLength(256);
            entity.Property(x => x.ReportReference).HasMaxLength(1024);
            entity.Property(x => x.ReportHash).HasMaxLength(128);
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.CandidateId, x.StartedAt }).HasDatabaseName("ix_agent_eval_runs_scope_candidate_started");
            entity.HasIndex(x => x.SuiteId).HasDatabaseName("ix_agent_eval_runs_suite");
        });
        modelBuilder.Entity<LiveDigestSettingsRecord>(entity =>
        {
            entity.ToTable("live_digest_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Mode).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId }).IsUnique().HasDatabaseName("ix_live_digest_settings_scope");
        });
        modelBuilder.Entity<LiveDigestSnapshotRecord>(entity =>
        {
            entity.ToTable("live_digest_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Mode).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Headline).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.CurrentPhase).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SnapshotJson).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.RunId }).IsUnique().HasDatabaseName("ix_live_digest_snapshots_scope_run");
            entity.HasIndex(x => new { x.SessionId, x.UpdatedAt }).HasDatabaseName("ix_live_digest_snapshots_session_updated");
        });
        modelBuilder.UseTinadecSnakeCase();
    }
}

/// <summary>Agent instance row; LaneKey is the lane it executes in, null reads as the implicit "main" lane.</summary>
public sealed class AgentInstanceRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid SessionId { get; set; } public Guid RunId { get; set; } public Guid? TaskNodeId { get; set; } public Guid? ParentInstanceId { get; set; } public Guid? ProfileId { get; set; } public Guid? CreatedByProfileId { get; set; } public Guid AgentDefinitionId { get; set; } public Guid AgentVersionId { get; set; } public string AgentVersionHash { get; set; } = string.Empty; public string Layer { get; set; } = string.Empty; public string Role { get; set; } = string.Empty; public string? LaneKey { get; set; } public int GenerationDepth { get; set; } public bool Generated { get; set; } public string Status { get; set; } = "created"; public string DefinitionReference { get; set; } = string.Empty; public string DefinitionHash { get; set; } = string.Empty; public long DefinitionLength { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? ReleasedAt { get; set; } }
public sealed class AgentCandidateRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public Guid SourceRunId { get; set; } public Guid SourceInstanceId { get; set; } public Guid GeneratedByInstanceId { get; set; } public string Name { get; set; } = string.Empty; public string Layer { get; set; } = "execution"; public string AgentType { get; set; } = "generated"; public string Status { get; set; } = "proposed"; public double ConfidenceScore { get; set; } public string ProposalReference { get; set; } = string.Empty; public string ProposalHash { get; set; } = string.Empty; public long ProposalLength { get; set; } public Guid? PromotedAgentId { get; set; } public Guid? EvalRunId { get; set; } public string? DecisionReason { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid? DecidedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }

/// <summary>Immutable, content-addressed evaluation suite.</summary>
public sealed class AgentEvalSuiteRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "active";
    public string DefinitionReference { get; set; } = string.Empty;
    public string DefinitionHash { get; set; } = string.Empty;
    public long DefinitionLength { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Paired control/treatment evaluation evidence for one candidate.</summary>
public sealed class AgentEvalRunRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid SuiteId { get; set; }
    public Guid SourceInstanceId { get; set; }
    public string SuiteHash { get; set; } = string.Empty;
    public Guid? ControlAgentDefinitionId { get; set; }
    public Guid? ControlAgentVersionId { get; set; }
    public string? ControlVersionHash { get; set; }
    public string? ControlSystemPromptHash { get; set; }
    public string TreatmentProposalHash { get; set; } = string.Empty;
    public string? TreatmentSystemPromptHash { get; set; }
    public string Status { get; set; } = "running";
    public string? GateStatus { get; set; }
    public int CasesTotal { get; set; }
    public int CasesPassed { get; set; }
    public int CasesFailed { get; set; }
    public int CasesBlocked { get; set; }
    public int EligiblePairs { get; set; }
    public int BlockedPairs { get; set; }
    public double? ControlPassRate { get; set; }
    public double? TreatmentPassRate { get; set; }
    public double? WeightedPassRate { get; set; }
    public double? Lift { get; set; }
    public string ModelRoute { get; set; } = "chat";
    public string? Model { get; set; }
    public string? ReportReference { get; set; }
    public string? ReportHash { get; set; }
    public long? ReportLength { get; set; }
    public string? Error { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Workspace-scoped Live Digest narrator mode configuration (off | auto | always).</summary>
public sealed class LiveDigestSettingsRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Mode { get; set; } = "auto";
    public long Revision { get; set; }
    public Guid UpdatedByPrincipalId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Durable snapshot of a run's Live Digest chapters, waterline, signals and sentinel alerts for live recovery and post-run replay.</summary>
public sealed class LiveDigestSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid RunId { get; set; }
    public string Status { get; set; } = "running";
    public string Mode { get; set; } = "auto";
    public long Seq { get; set; }
    public string Headline { get; set; } = string.Empty;
    public string CurrentPhase { get; set; } = "hypothesis";
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
