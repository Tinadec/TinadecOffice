using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Models;

public sealed class ModelControlDbContext : DbContext
{
    public ModelControlDbContext(DbContextOptions<ModelControlDbContext> options) : base(options) { }
    public DbSet<ModelProviderRecord> Providers => Set<ModelProviderRecord>();
    public DbSet<ModelProviderVersionRecord> ProviderVersions => Set<ModelProviderVersionRecord>();
    public DbSet<ModelRouteRecord> Routes => Set<ModelRouteRecord>();
    public DbSet<ModelRouteVersionRecord> RouteVersions => Set<ModelRouteVersionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelProviderRecord>(entity =>
        {
            entity.ToTable("model_provider_instances"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Driver).HasMaxLength(128).IsRequired(); entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Scope).HasMaxLength(32).IsRequired(); entity.Property(x => x.ConnectionKind).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SecretReference).HasMaxLength(512); entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.Driver, x.DeletedAt });
        });
        modelBuilder.Entity<ModelProviderVersionRecord>(entity =>
        {
            entity.ToTable("model_provider_versions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.ProviderId, x.Version }).IsUnique();
        });
        modelBuilder.Entity<ModelRouteRecord>(entity =>
        {
            entity.ToTable("model_routes"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Purpose).HasMaxLength(128).IsRequired(); entity.Property(x => x.Scope).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProjectId, x.Purpose, x.DeletedAt }).IsUnique();
        });
        modelBuilder.Entity<ModelRouteVersionRecord>(entity =>
        {
            entity.ToTable("model_route_versions"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.RouteId, x.Version }).IsUnique();
        });
        modelBuilder.UseTinadecSnakeCase();
    }
}

public sealed class ModelProviderRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid? WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public string Scope { get; set; } = "workspace"; public string Driver { get; set; } = string.Empty; public string DisplayName { get; set; } = string.Empty; public string ConnectionKind { get; set; } = string.Empty; public string? SecretReference { get; set; } public bool Enabled { get; set; } = true; public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class ModelProviderVersionRecord { public Guid Id { get; set; } public Guid ProviderId { get; set; } public int Version { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class ModelRouteRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid? WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public string Scope { get; set; } = "workspace"; public string Purpose { get; set; } = string.Empty; public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class ModelRouteVersionRecord { public Guid Id { get; set; } public Guid RouteId { get; set; } public int Version { get; set; } public Guid ProviderId { get; set; } public string? Model { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
