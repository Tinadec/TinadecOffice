using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime.SpatialBoard;

/// <summary>
/// Isolated spatial board persistence context owned by the Runtime SpatialBoard feature.
/// All timestamps are stored as <see cref="long"/> Unix milliseconds for provider-neutral SQLite/PostgreSQL queries.
/// </summary>
public sealed class SpatialBoardDbContext(DbContextOptions<SpatialBoardDbContext> options) : DbContext(options)
{
    public DbSet<SpatialBoardMetaRecord> BoardMeta => Set<SpatialBoardMetaRecord>();
    public DbSet<SpatialBoardObjectRecord> BoardObjects => Set<SpatialBoardObjectRecord>();
    public DbSet<SpatialBoardCommentRecord> BoardComments => Set<SpatialBoardCommentRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SpatialBoardMetaRecord>(e =>
        {
            e.ToTable("spatial_board_meta");
            e.HasKey(x => x.SessionId);
        });

        model.Entity<SpatialBoardObjectRecord>(e =>
        {
            e.ToTable("spatial_board_objects");
            e.HasKey(x => new { x.SessionId, x.ObjectId });
            e.Property(x => x.ObjectId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.ParentId).HasMaxLength(128);
            e.Property(x => x.SortKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.PropsJson).IsRequired();
            e.Property(x => x.PropTimestampsJson).IsRequired();
            e.Property(x => x.DeletedWriterId).HasMaxLength(128);
            e.HasIndex(x => new { x.SessionId, x.Deleted });
            e.HasIndex(x => new { x.SessionId, x.UpdatedAtUnixMs });
        });

        model.Entity<SpatialBoardCommentRecord>(e =>
        {
            e.ToTable("spatial_board_comments");
            e.HasKey(x => x.PinId);
            e.Property(x => x.PinId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ObjectId).HasMaxLength(128);
            e.Property(x => x.AuthorId).HasMaxLength(128).IsRequired();
            e.Property(x => x.AuthorName).HasMaxLength(256).IsRequired();
            e.Property(x => x.AuthorKind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Body).IsRequired();
            e.Property(x => x.MentionsJson).IsRequired();
            e.Property(x => x.RepliesJson).IsRequired();
            e.HasIndex(x => new { x.SessionId, x.CreatedAtUnixMs });
        });

        model.UseTinadecSnakeCase();
    }
}

/// <summary>
/// Idempotent schema bootstrapper for <see cref="SpatialBoardDbContext"/> participating in <see cref="IStorageMigrationRunner"/>.
/// </summary>
public sealed class SpatialBoardMigrationParticipant(IDbContextFactory<SpatialBoardDbContext> factory) : IStorageMigrationParticipant
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SpatialBoardMetaRecord
{
    public Guid SessionId { get; set; }
    public long Seq { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

public sealed class SpatialBoardObjectRecord
{
    public Guid SessionId { get; set; }
    public string ObjectId { get; set; } = string.Empty;
    public string Kind { get; set; } = "sticky";
    public string? ParentId { get; set; }
    public string SortKey { get; set; } = "V";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Rotation { get; set; }
    public string PropsJson { get; set; } = "{}";
    public string PropTimestampsJson { get; set; } = "{}";
    public bool Deleted { get; set; }
    public long? DeletedLamport { get; set; }
    public string? DeletedWriterId { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

public sealed class SpatialBoardCommentRecord
{
    public Guid SessionId { get; set; }
    public string PinId { get; set; } = string.Empty;
    public string? ObjectId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorKind { get; set; } = "human";
    public string Body { get; set; } = string.Empty;
    public string MentionsJson { get; set; } = "[]";
    public string RepliesJson { get; set; } = "[]";
    public bool Resolved { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}
