using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;

namespace TinadecCore.Memory;

public sealed class MemoryDbContext : DbContext
{
    public MemoryDbContext(DbContextOptions<MemoryDbContext> options) : base(options) { }

    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectRecord>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
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
            entity.HasIndex(x => x.NormalizedRootPath).IsUnique();
            entity.HasIndex(x => new { x.Archived, x.UpdatedAt });
        });

        modelBuilder.Entity<SessionRecord>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.Mode).HasColumnName("mode");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.HistoryRevision).HasColumnName("history_revision");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.Archived).HasColumnName("archived");
            entity.Property(x => x.Title).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Mode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(4096);
            entity.HasIndex(x => new { x.ProjectId, x.Archived, x.UpdatedAt });
        });
    }
}

public sealed class ProjectRecord
{
    public Guid Id { get; set; }
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
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public string Mode { get; set; } = "default";
    public string? Summary { get; set; }
    public long HistoryRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Archived { get; set; }
}
