using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Tenancy;

public sealed class TenancyDbContext : DbContext
{
    public TenancyDbContext(DbContextOptions<TenancyDbContext> options) : base(options) { }

    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();
    public DbSet<PrincipalRecord> Principals => Set<PrincipalRecord>();
    public DbSet<TenantMembershipRecord> TenantMemberships => Set<TenantMembershipRecord>();
    public DbSet<WorkspaceRecord> Workspaces => Set<WorkspaceRecord>();
    public DbSet<WorkspaceMembershipRecord> WorkspaceMemberships => Set<WorkspaceMembershipRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantRecord>(entity =>
        {
            entity.ToTable("tenants"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id"); entity.Property(x => x.Slug).HasColumnName("slug"); entity.Property(x => x.Name).HasColumnName("name"); entity.Property(x => x.Status).HasColumnName("status"); entity.Property(x => x.CreatedAt).HasColumnName("created_at"); entity.Property(x => x.UpdatedAt).HasColumnName("updated_at"); entity.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            entity.Property(x => x.Slug).HasMaxLength(128).IsRequired(); entity.HasIndex(x => x.Slug).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(256).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
        });
        modelBuilder.Entity<PrincipalRecord>(entity =>
        {
            entity.ToTable("principals"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id"); entity.Property(x => x.Issuer).HasColumnName("issuer"); entity.Property(x => x.Subject).HasColumnName("subject"); entity.Property(x => x.DisplayName).HasColumnName("display_name"); entity.Property(x => x.Status).HasColumnName("status"); entity.Property(x => x.CreatedAt).HasColumnName("created_at"); entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.Issuer).HasMaxLength(512).IsRequired(); entity.Property(x => x.Subject).HasMaxLength(512).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();
        });
        modelBuilder.Entity<TenantMembershipRecord>(entity =>
        {
            entity.ToTable("tenant_memberships"); entity.HasKey(x => new { x.TenantId, x.PrincipalId });
            entity.Property(x => x.TenantId).HasColumnName("tenant_id"); entity.Property(x => x.PrincipalId).HasColumnName("principal_id"); entity.Property(x => x.Role).HasColumnName("role"); entity.Property(x => x.Status).HasColumnName("status"); entity.Property(x => x.CreatedAt).HasColumnName("created_at"); entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
        });
        modelBuilder.Entity<WorkspaceRecord>(entity =>
        {
            entity.ToTable("workspaces"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id"); entity.Property(x => x.TenantId).HasColumnName("tenant_id"); entity.Property(x => x.Slug).HasColumnName("slug"); entity.Property(x => x.Name).HasColumnName("name"); entity.Property(x => x.Status).HasColumnName("status"); entity.Property(x => x.CreatedAt).HasColumnName("created_at"); entity.Property(x => x.UpdatedAt).HasColumnName("updated_at"); entity.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            entity.Property(x => x.Slug).HasMaxLength(128).IsRequired(); entity.Property(x => x.Name).HasMaxLength(256).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
        });
        modelBuilder.Entity<WorkspaceMembershipRecord>(entity =>
        {
            entity.ToTable("workspace_memberships"); entity.HasKey(x => new { x.WorkspaceId, x.PrincipalId });
            entity.Property(x => x.WorkspaceId).HasColumnName("workspace_id"); entity.Property(x => x.PrincipalId).HasColumnName("principal_id"); entity.Property(x => x.Role).HasColumnName("role"); entity.Property(x => x.Status).HasColumnName("status"); entity.Property(x => x.CreatedAt).HasColumnName("created_at"); entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
        });
    }
}

public sealed class TenantRecord { public Guid Id { get; set; } public string Slug { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public string Status { get; set; } = "active"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class PrincipalRecord { public Guid Id { get; set; } public string Issuer { get; set; } = string.Empty; public string Subject { get; set; } = string.Empty; public string DisplayName { get; set; } = string.Empty; public string Status { get; set; } = "active"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class TenantMembershipRecord { public Guid TenantId { get; set; } public Guid PrincipalId { get; set; } public string Role { get; set; } = "viewer"; public string Status { get; set; } = "active"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class WorkspaceRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public string Slug { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public string Status { get; set; } = "active"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class WorkspaceMembershipRecord { public Guid WorkspaceId { get; set; } public Guid PrincipalId { get; set; } public string Role { get; set; } = "viewer"; public string Status { get; set; } = "active"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
