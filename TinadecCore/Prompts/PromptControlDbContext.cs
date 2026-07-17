using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Prompts;

public sealed class PromptControlDbContext : DbContext
{
    public PromptControlDbContext(DbContextOptions<PromptControlDbContext> options) : base(options) { }
    public DbSet<PromptFragmentRecord> Fragments => Set<PromptFragmentRecord>();
    public DbSet<PromptFragmentVersionRecord> Versions => Set<PromptFragmentVersionRecord>();
    public DbSet<PromptFragmentSignalRecord> Signals => Set<PromptFragmentSignalRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PromptFragmentRecord>(entity =>
        {
            entity.ToTable("prompt_fragments"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(256).IsRequired(); entity.Property(x => x.Title).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Scope).HasMaxLength(32).IsRequired(); entity.Property(x => x.Category).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.ProjectId, x.Key, x.TargetAgentId, x.DeletedAt }).IsUnique();
        });
        modelBuilder.Entity<PromptFragmentVersionRecord>(entity =>
        {
            entity.ToTable("prompt_fragment_versions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ContentReference).HasMaxLength(1024).IsRequired(); entity.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.FragmentId, x.Version }).IsUnique();
        });
        modelBuilder.Entity<PromptFragmentSignalRecord>(entity =>
        {
            entity.ToTable("prompt_fragment_signals"); entity.HasKey(x => x.Id); entity.Property(x => x.Signal).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.FragmentId, x.Version, x.CreatedAt });
        });
        modelBuilder.UseTinadecSnakeCase();
    }
}

public sealed class PromptFragmentRecord { public Guid Id { get; set; } public Guid TenantId { get; set; } public Guid? WorkspaceId { get; set; } public Guid? ProjectId { get; set; } public string Scope { get; set; } = "workspace"; public string Key { get; set; } = string.Empty; public string Title { get; set; } = string.Empty; public Guid? TargetAgentId { get; set; } public string Category { get; set; } = string.Empty; public int Priority { get; set; } public bool Enabled { get; set; } = true; public bool IsBuiltIn { get; set; } public long Revision { get; set; } public Guid CurrentVersionId { get; set; } public Guid CreatedByPrincipalId { get; set; } public Guid UpdatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
public sealed class PromptFragmentVersionRecord { public Guid Id { get; set; } public Guid FragmentId { get; set; } public int Version { get; set; } public string ContentReference { get; set; } = string.Empty; public string ContentHash { get; set; } = string.Empty; public long ContentLength { get; set; } public string ChangeSummary { get; set; } = string.Empty; public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class PromptFragmentSignalRecord { public Guid Id { get; set; } public Guid FragmentId { get; set; } public int Version { get; set; } public Guid? RunId { get; set; } public Guid? SessionId { get; set; } public string Signal { get; set; } = string.Empty; public Guid CreatedByPrincipalId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
