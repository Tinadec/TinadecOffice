using Microsoft.EntityFrameworkCore;

namespace TinadecCore.AgentConfiguration;

/// <summary>
/// Isolated agent/mode/prompt configuration store.
/// Covers formal agents, modes, prompts, candidates-like versions; does NOT reuse legacy agent_instances.
/// All tables carry tenant_id/workspace_id, revision (ETag), status, created_at/updated_at/archived_at.
/// Single draft per logical entity is enforced by a filtered unique index (tenant, workspace, entity_key) where status='draft'.
/// Publishing freezes an immutable version; draft revision is the ETag for optimistic concurrency.
/// Mode publish validates dual-lane topology (operation ≥1 meeting, execution ≥1, cross-layer reuse warning) at service layer.
/// SQLite default file shares data/tinadec.db; PostgreSQL via UseTinadecDatabase.
/// Migration modules provide idempotent SQLite/PostgreSQL CreateTable scripts;
/// the startup EnsureTables fallback remains for databases created by older
/// Core builds (see DbContextSchemaBootstrapper).
/// </summary>
public sealed class AgentConfigurationDbContext : DbContext
{
    public AgentConfigurationDbContext(DbContextOptions<AgentConfigurationDbContext> options) : base(options) { }

    public DbSet<AgentDefinitionRecord> AgentDefinitions => Set<AgentDefinitionRecord>();
    public DbSet<AgentVersionRecord> AgentVersions => Set<AgentVersionRecord>();
    public DbSet<AgentModeRecord> AgentModes => Set<AgentModeRecord>();
    public DbSet<ModeVersionRecord> ModeVersions => Set<ModeVersionRecord>();
    public DbSet<ModeNodeRecord> ModeNodes => Set<ModeNodeRecord>();
    public DbSet<ModeEdgeRecord> ModeEdges => Set<ModeEdgeRecord>();
    public DbSet<CanvasLayoutRecord> CanvasLayouts => Set<CanvasLayoutRecord>();
    public DbSet<PromptPipelineRecord> PromptPipelines => Set<PromptPipelineRecord>();
    public DbSet<PromptVersionRecord> PromptVersions => Set<PromptVersionRecord>();
    public DbSet<PromptNodeRecord> PromptNodes => Set<PromptNodeRecord>();
    public DbSet<WorkspaceDefaultsRecord> WorkspaceDefaults => Set<WorkspaceDefaultsRecord>();
    public DbSet<AgentPackInstallationRecord> AgentPackInstallations => Set<AgentPackInstallationRecord>();
    public DbSet<AgentPackVersionRecord> AgentPackVersions => Set<AgentPackVersionRecord>();
    public DbSet<AgentPackManagedResourceRecord> AgentPackManagedResources => Set<AgentPackManagedResourceRecord>();
    public DbSet<AgentPackResourceBindingRecord> AgentPackResourceBindings => Set<AgentPackResourceBindingRecord>();
    public DbSet<AgentPackPreviewRecord> AgentPackPreviews => Set<AgentPackPreviewRecord>();
    public DbSet<AgentPackDefaultAdoptionRecord> AgentPackDefaultAdoptions => Set<AgentPackDefaultAdoptionRecord>();
    public DbSet<AgentPackOperationRecord> AgentPackOperations => Set<AgentPackOperationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentDefinitionRecord>(entity =>
        {
            entity.ToTable("agent_definitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Layer).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Role).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CapabilitiesJson).HasMaxLength(4096);
            entity.Property(x => x.ModelStrategyJson).HasMaxLength(4096);
            entity.Property(x => x.ToolScopeJson).HasMaxLength(4096);
            entity.Property(x => x.SystemPrompt).HasMaxLength(16384);
            entity.Property(x => x.Description).HasMaxLength(2048);
            entity.Property(x => x.SourceKind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SourceKey).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            // single draft per workspace logical agent
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Slug }).IsUnique().HasFilter("status = 'draft'");
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.UpdatedAt });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Layer, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SourceKind, x.SourceKey }).IsUnique();
        });

        modelBuilder.Entity<AgentVersionRecord>(entity =>
        {
            entity.ToTable("agent_versions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Layer).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Role).HasMaxLength(128).IsRequired();
            entity.Property(x => x.SnapshotJson).HasMaxLength(16384).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.AgentDefinitionId, x.Version }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.AgentDefinitionId, x.CreatedAt });
        });

        modelBuilder.Entity<AgentModeRecord>(entity =>
        {
            entity.ToTable("agent_modes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4096);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Slug }).IsUnique().HasFilter("status = 'draft'");
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<ModeVersionRecord>(entity =>
        {
            entity.ToTable("mode_versions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SnapshotJson).HasMaxLength(32768);
            entity.Property(x => x.TopologyHash).HasMaxLength(128);
            entity.Property(x => x.WarningJson).HasMaxLength(4096);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.AgentModeId, x.Version }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.AgentModeId, x.CreatedAt });
        });

        modelBuilder.Entity<ModeNodeRecord>(entity =>
        {
            entity.ToTable("mode_nodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NodeKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AgentDefinitionId).IsRequired();
            entity.Property(x => x.Layer).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Label).HasMaxLength(256);
            entity.Property(x => x.PositionJson).HasMaxLength(4096);
            entity.Property(x => x.ConfigJson).HasMaxLength(8192);
            entity.Property(x => x.ModelStrategyOverrideJson).HasMaxLength(4096);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ModeId, x.NodeKey }).IsUnique().HasFilter("status = 'draft'");
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ModeId, x.Status });
        });

        modelBuilder.Entity<ModeEdgeRecord>(entity =>
        {
            entity.ToTable("mode_edges");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EdgeKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.SourceNodeKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.TargetNodeKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ConditionJson).HasMaxLength(4096);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ModeId, x.EdgeKey }).IsUnique().HasFilter("status = 'draft'");
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ModeId, x.Status });
        });

        modelBuilder.Entity<CanvasLayoutRecord>(entity =>
        {
            entity.ToTable("canvas_layouts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LayoutJson).HasMaxLength(32768).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ModeId }).IsUnique().HasFilter("status = 'draft'");
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<PromptPipelineRecord>(entity =>
        {
            entity.ToTable("prompt_pipelines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4096);
            entity.Property(x => x.GraphJson).HasMaxLength(32768).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Slug }).IsUnique().HasFilter("status = 'draft'");
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<PromptVersionRecord>(entity =>
        {
            entity.ToTable("prompt_versions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.GraphJson).HasMaxLength(32768).IsRequired();
            entity.Property(x => x.ContentHash).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.PromptPipelineId, x.Version }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.PromptPipelineId, x.CreatedAt });
        });

        modelBuilder.Entity<PromptNodeRecord>(entity =>
        {
            entity.ToTable("prompt_nodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NodeKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ConfigJson).HasMaxLength(16384).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.PromptPipelineId, x.NodeKey }).IsUnique().HasFilter("status = 'draft'");
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.PromptPipelineId, x.Status });
        });

        modelBuilder.Entity<WorkspaceDefaultsRecord>(entity =>
        {
            entity.ToTable("workspace_defaults");
            entity.HasKey(x => new { x.TenantId, x.WorkspaceId });
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status });
        });

        modelBuilder.Entity<AgentPackInstallationRecord>(entity =>
        {
            entity.ToTable("agent_pack_installations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PackId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Owner).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ProductId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.PackId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<AgentPackVersionRecord>(entity =>
        {
            entity.ToTable("agent_pack_versions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PackVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ManifestContentReference).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.ManifestHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.InstallationId, x.PackVersion }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.InstallationId, x.CreatedAt });
        });

        modelBuilder.Entity<AgentPackManagedResourceRecord>(entity =>
        {
            entity.ToTable("agent_pack_managed_resources");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceKind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ResourceKey).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => new { x.InstallationId, x.ResourceKind, x.ResourceKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ResourceKind, x.LogicalEntityId }).IsUnique();
        });

        modelBuilder.Entity<AgentPackResourceBindingRecord>(entity =>
        {
            entity.ToTable("agent_pack_resource_bindings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceKind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ResourceKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Disposition).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.PackVersionId, x.ResourceKind, x.ResourceKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.VersionId });
        });

        modelBuilder.Entity<AgentPackPreviewRecord>(entity =>
        {
            entity.ToTable("agent_pack_previews");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PackId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Owner).HasMaxLength(256).IsRequired();
            entity.Property(x => x.PackVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ManifestContentReference).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.ManifestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.PrincipalId, x.ExpiresAt });
        });

        modelBuilder.Entity<AgentPackDefaultAdoptionRecord>(entity =>
        {
            entity.ToTable("agent_pack_default_adoptions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.InstallationId, x.PackVersionId }).IsUnique();
        });

        modelBuilder.Entity<AgentPackOperationRecord>(entity =>
        {
            entity.ToTable("agent_pack_operations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Operation).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ResponseJson).HasMaxLength(32768).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.PrincipalId, x.Operation, x.IdempotencyKey }).IsUnique();
        });

        modelBuilder.UseTinadecSnakeCase();
    }
}

// ─────────────────────────────────────────────
// Records — workspace-scoped, revision/ETag optimistic concurrency, soft archive
// ─────────────────────────────────────────────

public sealed class AgentDefinitionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    // logical key — unique draft per workspace
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    // only operation / execution — planning is rejected at service layer
    public string Layer { get; set; } = "operation";
    public string Role { get; set; } = string.Empty;
    public string? CapabilitiesJson { get; set; }
    public Guid? BasePromptPipelineId { get; set; }
    // json: { kind: inherit } | { kind: route, route_purpose } | { kind: fixed, provider_instance_id, model }
    public string? ModelStrategyJson { get; set; }
    // json: { allowed_tools: string[], deny, etc }
    public string? ToolScopeJson { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Description { get; set; }
    public string SourceKind { get; set; } = "custom";
    public string SourceKey { get; set; } = string.Empty;
    public bool Managed { get; set; }
    public bool Enabled { get; set; } = true;
    public string Status { get; set; } = "draft";
    public long Revision { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public Guid UpdatedByPrincipalId { get; set; }
}

public sealed class AgentVersionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid AgentDefinitionId { get; set; }
    public int Version { get; set; }
    public string Layer { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public string? ContentHash { get; set; }
    public long ContentLength { get; set; }
    public string Status { get; set; } = "published";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
}

public sealed class AgentModeRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "draft";
    public long Revision { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public Guid UpdatedByPrincipalId { get; set; }
}

public sealed class ModeVersionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid AgentModeId { get; set; }
    public int Version { get; set; }
    // frozen snapshot of nodes+edges+layout for this version
    public string? SnapshotJson { get; set; }
    public string? TopologyHash { get; set; }
    // json array of cross-layer reuse warnings
    public string? WarningJson { get; set; }
    public string Status { get; set; } = "published";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
}

public sealed class ModeNodeRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ModeId { get; set; }
    public string NodeKey { get; set; } = string.Empty;
    public Guid AgentDefinitionId { get; set; }
    public string Layer { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? PositionJson { get; set; }
    public string? ConfigJson { get; set; }
    public string? ModelStrategyOverrideJson { get; set; }
    public string Status { get; set; } = "draft";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class ModeEdgeRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ModeId { get; set; }
    public string EdgeKey { get; set; } = string.Empty;
    public string SourceNodeKey { get; set; } = string.Empty;
    public string TargetNodeKey { get; set; } = string.Empty;
    public string? ConditionJson { get; set; }
    public string Status { get; set; } = "draft";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class CanvasLayoutRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ModeId { get; set; }
    public string LayoutJson { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class PromptPipelineRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    // json graph (nodes+edges)
    public string GraphJson { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public long Revision { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public Guid UpdatedByPrincipalId { get; set; }
}

public sealed class PromptVersionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PromptPipelineId { get; set; }
    public int Version { get; set; }
    public string GraphJson { get; set; } = string.Empty;
    public string? ContentHash { get; set; }
    public long ContentLength { get; set; }
    public string Status { get; set; } = "published";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
}

public sealed class PromptNodeRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PromptPipelineId { get; set; }
    public string NodeKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class WorkspaceDefaultsRecord
{
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? DefaultAgentDefinitionId { get; set; }
    public Guid? DefaultAgentModeId { get; set; }
    public Guid? DefaultPromptPipelineId { get; set; }
    public Guid? DefaultAgentVersionId { get; set; }
    public Guid? DefaultModeVersionId { get; set; }
    public Guid? DefaultPromptVersionId { get; set; }
    public string Status { get; set; } = "active";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class AgentPackInstallationRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string PackId { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? ActiveVersionId { get; set; }
    public string Status { get; set; } = "active";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
    public Guid UpdatedByPrincipalId { get; set; }
}

public sealed class AgentPackVersionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstallationId { get; set; }
    public string PackVersion { get; set; } = string.Empty;
    public string ManifestContentReference { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public long ManifestLength { get; set; }
    public Guid? PreviousVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByPrincipalId { get; set; }
}

public sealed class AgentPackManagedResourceRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstallationId { get; set; }
    public string ResourceKind { get; set; } = string.Empty;
    public string ResourceKey { get; set; } = string.Empty;
    public Guid LogicalEntityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AgentPackResourceBindingRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PackVersionId { get; set; }
    public string ResourceKind { get; set; } = string.Empty;
    public string ResourceKey { get; set; } = string.Empty;
    public Guid LogicalEntityId { get; set; }
    public Guid VersionId { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string Disposition { get; set; } = "created";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AgentPackPreviewRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PrincipalId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string PackId { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string PackVersion { get; set; } = string.Empty;
    public string ManifestContentReference { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public long ManifestLength { get; set; }
    public long BaseInstallationRevision { get; set; }
    public long? BaseDefaultsRevision { get; set; }
    public Guid? TargetPackVersionId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class AgentPackDefaultAdoptionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid PackVersionId { get; set; }
    public Guid? PreviousAgentDefinitionId { get; set; }
    public Guid? PreviousAgentVersionId { get; set; }
    public Guid? PreviousAgentModeId { get; set; }
    public Guid? PreviousModeVersionId { get; set; }
    public Guid? PreviousPromptPipelineId { get; set; }
    public Guid? PreviousPromptVersionId { get; set; }
    public Guid? AppliedAgentDefinitionId { get; set; }
    public Guid? AppliedAgentVersionId { get; set; }
    public Guid? AppliedAgentModeId { get; set; }
    public Guid? AppliedModeVersionId { get; set; }
    public Guid? AppliedPromptPipelineId { get; set; }
    public Guid? AppliedPromptVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AgentPackOperationRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PrincipalId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ResponseJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
