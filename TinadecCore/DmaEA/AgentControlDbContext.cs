using Microsoft.EntityFrameworkCore;

namespace TinadecCore.DmaEA;

public sealed class AgentControlDbContext : DbContext
{
    public AgentControlDbContext(DbContextOptions<AgentControlDbContext> options) : base(options) { }
    public DbSet<AgentProfileRecord> Agents => Set<AgentProfileRecord>();
    public DbSet<AgentProfileVersionRecord> Versions => Set<AgentProfileVersionRecord>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentProfileRecord>(entity =>
        {
            entity.ToTable("agent_profiles"); entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Layer).HasMaxLength(32).IsRequired(); entity.Property(x => x.AgentType).HasMaxLength(128).IsRequired(); entity.Property(x => x.Scope).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProjectId, x.Name, x.DeletedAt });
        });
        modelBuilder.Entity<AgentProfileVersionRecord>(entity =>
        {
            entity.ToTable("agent_profile_versions"); entity.HasKey(x => x.Id); entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired(); entity.HasIndex(x => new { x.AgentId, x.Version }).IsUnique();
        });
        modelBuilder.UseTinadecSnakeCase();
    }
}

public sealed class AgentProfileRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid? WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public string Scope { get; set; } = "workspace"; public string Name { get; set; } = string.Empty; public string Layer { get; set; } = string.Empty; public string AgentType { get; set; } = string.Empty; public bool Enabled { get; set; } = true; public bool IsBuiltIn { get; set; } public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class AgentProfileVersionRecord { public Guid Id { get; set; } public Guid AgentId { get; set; } public int Version { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public Guid? ModelRouteId { get; set; } public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
