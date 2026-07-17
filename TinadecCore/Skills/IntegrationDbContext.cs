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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExtensionSourceRecord>(entity => { entity.ToTable("extension_sources"); entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(256).IsRequired(); entity.Property(x => x.Kind).HasMaxLength(64).IsRequired(); entity.Property(x => x.Location).HasMaxLength(2048).IsRequired(); entity.HasIndex(x => new { x.TenantId, x.Name, x.DeletedAt }).IsUnique(); });
        modelBuilder.Entity<ExtensionCatalogRecord>(entity => { entity.ToTable("extension_catalog_entries"); entity.HasKey(x => x.Id); entity.Property(x => x.ExtensionId).HasMaxLength(256).IsRequired(); entity.Property(x => x.Version).HasMaxLength(128).IsRequired(); entity.Property(x => x.ManifestReference).HasMaxLength(1024).IsRequired(); entity.HasIndex(x => new { x.SourceId, x.ExtensionId, x.Version }).IsUnique(); });
        modelBuilder.Entity<WorkspaceExtensionRecord>(entity => { entity.ToTable("workspace_extensions"); entity.HasKey(x => x.Id); entity.Property(x => x.ExtensionId).HasMaxLength(256).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.WorkspaceId, x.ExtensionId, x.DeletedAt }).IsUnique(); });
        modelBuilder.Entity<WorkspaceExtensionVersionRecord>(entity => { entity.ToTable("workspace_extension_versions"); entity.HasKey(x => x.Id); entity.Property(x => x.ManifestReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ConfigReference).HasMaxLength(1024); entity.HasIndex(x => new { x.WorkspaceExtensionId, x.Version }).IsUnique(); });
        modelBuilder.Entity<IntegrationInstanceRecord>(entity => { entity.ToTable("integration_instances"); entity.HasKey(x => x.Id); entity.Property(x => x.Kind).HasMaxLength(32).IsRequired(); entity.Property(x => x.Name).HasMaxLength(256).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.WorkspaceId, x.Kind, x.Name, x.DeletedAt }).IsUnique(); });
        modelBuilder.Entity<IntegrationInstanceVersionRecord>(entity => { entity.ToTable("integration_instance_versions"); entity.HasKey(x => x.Id); entity.Property(x => x.ConfigReference).HasMaxLength(1024).IsRequired(); entity.HasIndex(x => new { x.IntegrationId, x.Version }).IsUnique(); });
        modelBuilder.UseTinadecSnakeCase();
    }
}

public sealed class ExtensionSourceRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public string Name { get; set; } = string.Empty; public string Kind { get; set; } = string.Empty; public string Location { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public long Revision { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class ExtensionCatalogRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid SourceId { get; set; } public string ExtensionId { get; set; } = string.Empty; public string Version { get; set; } = string.Empty; public string ManifestReference { get; set; } = string.Empty; public string ManifestHash { get; set; } = string.Empty; public DateTimeOffset RefreshedAt { get; set; } public DateTimeOffset ExpiresAt { get; set; } }
public sealed class WorkspaceExtensionRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public string ExtensionId { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public string Status { get; set; } = "installed"; public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class WorkspaceExtensionVersionRecord { public Guid Id { get; set; } public Guid WorkspaceExtensionId { get; set; } public int Version { get; set; } public Guid? CatalogEntryId { get; set; } public string ManifestReference { get; set; } = string.Empty; public string ManifestHash { get; set; } = string.Empty; public string? ConfigReference { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class IntegrationInstanceRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid WorkspaceId { get; set; } public Guid WorkspaceExtensionId { get; set; } public string Kind { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public string Status { get; set; } = "unknown"; public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class IntegrationInstanceVersionRecord { public Guid Id { get; set; } public Guid IntegrationId { get; set; } public int Version { get; set; } public string ConfigReference { get; set; } = string.Empty; public string ConfigHash { get; set; } = string.Empty; public Guid? SecretReferenceOwnerId { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
