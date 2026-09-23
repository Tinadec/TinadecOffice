using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Skills;

public sealed class IntegrationDbContext : DbContext
{
    public IntegrationDbContext(DbContextOptions<IntegrationDbContext> options) : base(options) { }
    public DbSet<ExtensionSourceRecord> Sources => Set<ExtensionSourceRecord>();
    public DbSet<ExtensionCatalogRecord> CatalogEntries => Set<ExtensionCatalogRecord>();
    public DbSet<WorkspaceExtensionRecord> Extensions => Set<WorkspaceExtensionRecord>();
    public DbSet<WorkspaceExtensionVersionRecord> ExtensionVersions => Set<WorkspaceExtensionVersionRecord>();
    public DbSet<IntegrationInstanceRecord> Integrations => Set<IntegrationInstanceRecord>();
    public DbSet<IntegrationInstanceVersionRecord> IntegrationVersions => Set<IntegrationInstanceVersionRecord>();
    public DbSet<MarketInstallProposalRecord> InstallProposals => Set<MarketInstallProposalRecord>();
    public DbSet<MarketInstallationRecord> Installations => Set<MarketInstallationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExtensionSourceRecord>(entity => { entity.ToTable("extension_sources"); entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(256).IsRequired(); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.Location).HasMaxLength(2048).IsRequired(); entity.Property(x => x.LastError).HasMaxLength(2048); entity.HasIndex(x => new { x.TenantId, x.Name, x.DeletedAt }).IsUnique(); });
        modelBuilder.Entity<ExtensionCatalogRecord>(entity => { entity.ToTable("extension_catalog_entries"); entity.HasKey(x => x.Id); entity.Property(x => x.ExtensionId).HasMaxLength(256).IsRequired(); entity.Property(x => x.Version).HasMaxLength(128).IsRequired(); entity.Property(x => x.ManifestReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.DisplayName).HasMaxLength(512).IsRequired(); entity.Property(x => x.Description).HasMaxLength(4096); entity.Property(x => x.DetailJson).HasMaxLength(16384); entity.HasIndex(x => new { x.SourceId, x.ExtensionId, x.Version }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.Kind, x.ExtensionId }); });
        modelBuilder.Entity<WorkspaceExtensionRecord>(entity => { entity.ToTable("workspace_extensions"); entity.HasKey(x => x.Id); entity.Property(x => x.ExtensionId).HasMaxLength(256).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.WorkspaceId, x.ExtensionId, x.DeletedAt }).IsUnique(); });
        modelBuilder.Entity<WorkspaceExtensionVersionRecord>(entity => { entity.ToTable("workspace_extension_versions"); entity.HasKey(x => x.Id); entity.Property(x => x.ManifestReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ConfigReference).HasMaxLength(1024); entity.HasIndex(x => new { x.WorkspaceExtensionId, x.Version }).IsUnique(); });
        modelBuilder.Entity<IntegrationInstanceRecord>(entity => { entity.ToTable("integration_instances"); entity.HasKey(x => x.Id); entity.Property(x => x.Kind).HasMaxLength(32).IsRequired(); entity.Property(x => x.Name).HasMaxLength(256).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.WorkspaceId, x.Kind, x.Name, x.DeletedAt }).IsUnique(); });
        modelBuilder.Entity<IntegrationInstanceVersionRecord>(entity => { entity.ToTable("integration_instance_versions"); entity.HasKey(x => x.Id); entity.Property(x => x.ConfigReference).HasMaxLength(1024).IsRequired(); entity.HasIndex(x => new { x.IntegrationId, x.Version }).IsUnique(); });

        // A market install is per project root, because that is where the Tool Provider reads its
        // server config and therefore which file the governed write may touch. The pre-existing
        // workspace_extensions pair is workspace-scoped and versions its rows with an int
        // revision, so it cannot say "this project's config, this published version string"
        // without lying about the scope or the identity.
        modelBuilder.Entity<MarketInstallProposalRecord>(entity => { entity.ToTable("market_install_proposals"); entity.HasKey(x => x.Id); entity.Property(x => x.Action).HasMaxLength(32).IsRequired(); entity.Property(x => x.ExtensionId).HasMaxLength(256).IsRequired(); entity.Property(x => x.Version).HasMaxLength(128).IsRequired(); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.ServerId).HasMaxLength(128).IsRequired(); entity.Property(x => x.Command).HasMaxLength(256); entity.Property(x => x.TargetPath).HasMaxLength(2048).IsRequired(); entity.Property(x => x.Digest).HasMaxLength(128).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.Property(x => x.ExpectedFileHash).HasMaxLength(128); entity.Property(x => x.ManifestHash).HasMaxLength(128); entity.Property(x => x.ArgsJson).HasMaxLength(4096).IsRequired(); entity.Property(x => x.EnvironmentJson).HasMaxLength(8192).IsRequired(); entity.Property(x => x.Content).HasMaxLength(65536).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.Status, x.ExpiresAt }); entity.HasIndex(x => new { x.TenantId, x.ProjectId, x.Digest }).IsUnique(); });
        modelBuilder.Entity<MarketInstallationRecord>(entity => { entity.ToTable("market_installations"); entity.HasKey(x => x.Id); entity.Property(x => x.ExtensionId).HasMaxLength(256).IsRequired(); entity.Property(x => x.Version).HasMaxLength(128).IsRequired(); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.ServerId).HasMaxLength(128).IsRequired(); entity.Property(x => x.ConfigPath).HasMaxLength(2048).IsRequired(); entity.Property(x => x.ManifestHash).HasMaxLength(128).IsRequired(); entity.Property(x => x.State).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.ProjectId, x.ServerId }).IsUnique(); });
        modelBuilder.UseTinadecSnakeCase();
    }
}

public sealed class ExtensionSourceRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public string Name { get; set; } = string.Empty; public string Kind { get; set; } = string.Empty; public string Location { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public long Revision { get; set; } public DateTimeOffset? LastRefreshedAt { get; set; } public string? LastError { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class ExtensionCatalogRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid SourceId { get; set; } public string ExtensionId { get; set; } = string.Empty; public string Version { get; set; } = string.Empty; public string Kind { get; set; } = string.Empty; public string DisplayName { get; set; } = string.Empty; public string? Description { get; set; } public string? DetailJson { get; set; } public string ManifestReference { get; set; } = string.Empty; public string ManifestHash { get; set; } = string.Empty; public DateTimeOffset RefreshedAt { get; set; } public DateTimeOffset ExpiresAt { get; set; } }
public sealed class WorkspaceExtensionRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public string ExtensionId { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public string Status { get; set; } = "installed"; public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class WorkspaceExtensionVersionRecord { public Guid Id { get; set; } public Guid WorkspaceExtensionId { get; set; } public int Version { get; set; } public Guid? CatalogEntryId { get; set; } public string ManifestReference { get; set; } = string.Empty; public string ManifestHash { get; set; } = string.Empty; public string? ConfigReference { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class IntegrationInstanceRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid WorkspaceExtensionId { get; set; } public string Kind { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public string Status { get; set; } = "unknown"; public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class IntegrationInstanceVersionRecord { public Guid Id { get; set; } public Guid IntegrationId { get; set; } public int Version { get; set; } public string ConfigReference { get; set; } = string.Empty; public string ConfigHash { get; set; } = string.Empty; public Guid? SecretReferenceOwnerId { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }

/// <summary>
/// One frozen install proposal. The reviewed bytes live here rather than being recomputed at
/// apply time, because the thing a human approved has to be the thing that gets written — and
/// the digest is what makes an apply provably the same request as the preview.
/// </summary>
public sealed class MarketInstallProposalRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PrincipalId { get; set; }
    /// <summary><c>install</c> or <c>uninstall</c>.</summary>
    public string Action { get; set; } = string.Empty;
    public Guid? CatalogId { get; set; }
    public Guid? SourceId { get; set; }
    public Guid? InstallationId { get; set; }
    public string ExtensionId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public string? Command { get; set; }
    public string ArgsJson { get; set; } = "[]";
    public string EnvironmentJson { get; set; } = "[]";
    public string? ReplacesCommand { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    /// <summary>Null while the file does not exist yet — a create needs no precondition.</summary>
    public string? ExpectedFileHash { get; set; }
    /// <summary>The catalog row's content hash when this was computed; a refresh moves it.</summary>
    public string? ManifestHash { get; set; }
    public string Digest { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public Guid? UserToolActionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>What a human approved for which project. Whether the write landed stays with the action.</summary>
public sealed class MarketInstallationRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid CatalogId { get; set; }
    public Guid SourceId { get; set; }
    public string ExtensionId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public Guid InstallActionId { get; set; }
    public Guid? UninstallActionId { get; set; }
    public string State { get; set; } = "installing";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
