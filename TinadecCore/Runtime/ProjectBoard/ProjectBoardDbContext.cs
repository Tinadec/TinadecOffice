using Microsoft.EntityFrameworkCore;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime.ProjectBoard;

/// <summary>
/// Isolated project board persistence context owned by the Runtime ProjectBoard feature.
/// All timestamps are stored as <see cref="long"/> Unix milliseconds for provider-neutral SQLite/PostgreSQL queries.
/// </summary>
public sealed class ProjectBoardDbContext(DbContextOptions<ProjectBoardDbContext> options) : DbContext(options)
{
    public DbSet<ProjectBoardSettingsRecord> BoardSettings => Set<ProjectBoardSettingsRecord>();
    public DbSet<ProjectBoardTaskRecord> BoardTasks => Set<ProjectBoardTaskRecord>();
    public DbSet<ProjectBoardViewRecord> BoardViews => Set<ProjectBoardViewRecord>();
    public DbSet<ProjectBoardSyncStateRecord> BoardSyncStates => Set<ProjectBoardSyncStateRecord>();
    public DbSet<ProjectNotesMetaRecord> NotesMeta => Set<ProjectNotesMetaRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ProjectBoardSettingsRecord>(e =>
        {
            e.ToTable("project_board_settings");
            e.HasKey(x => x.ProjectId);
            e.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.StorageMode).HasMaxLength(32).IsRequired();
            e.Property(x => x.CustomSyncRoot).HasMaxLength(4096);
            e.Property(x => x.ProjectInstructions).IsRequired();
            e.Property(x => x.DefaultAssigneeKind).HasMaxLength(32).IsRequired();
        });

        model.Entity<ProjectBoardTaskRecord>(e =>
        {
            e.ToTable("project_board_tasks");
            e.HasKey(x => x.TaskId);
            e.Property(x => x.TaskKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Title).HasMaxLength(512).IsRequired();
            e.Property(x => x.DescriptionMd).IsRequired();
            e.Property(x => x.ChecklistJson).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.Priority).HasMaxLength(32).IsRequired();
            e.Property(x => x.BlockedReason).HasMaxLength(2048);
            e.Property(x => x.DependsOnJson).IsRequired();
            e.Property(x => x.LabelsJson).IsRequired();
            e.Property(x => x.SortKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.RunStatus).HasMaxLength(64);
            e.Property(x => x.WorktreePath).HasMaxLength(4096);
            e.Property(x => x.LiveDigestSummary).HasMaxLength(2048);
            e.HasIndex(x => new { x.ProjectId, x.TaskKey }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.Deleted, x.Status });
            e.HasIndex(x => new { x.ProjectId, x.UpdatedAtUnixMs });
        });

        model.Entity<ProjectBoardViewRecord>(e =>
        {
            e.ToTable("project_board_views");
            e.HasKey(x => x.ViewId);
            e.Property(x => x.Scope).HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.ConfigJson).IsRequired();
            e.HasIndex(x => new { x.Scope, x.ProjectId, x.SortOrder });
        });

        model.Entity<ProjectBoardSyncStateRecord>(e =>
        {
            e.ToTable("project_board_sync_state");
            e.HasKey(x => new { x.ProjectId, x.TaskId });
            e.Property(x => x.FileRelPath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.LastSyncedHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.LastSyncedPayloadJson).IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.HasConflict });
        });

        model.Entity<ProjectNotesMetaRecord>(e =>
        {
            e.ToTable("project_notes_meta");
            e.HasKey(x => x.ProjectId);
            e.Property(x => x.ContentMd).IsRequired();
            e.Property(x => x.LastSyncedHash).HasMaxLength(128).IsRequired();
        });

        model.UseTinadecSnakeCase();
    }
}

/// <summary>
/// Idempotent schema bootstrapper for <see cref="ProjectBoardDbContext"/> participating in <see cref="IStorageMigrationRunner"/>.
/// </summary>
public sealed class ProjectBoardMigrationParticipant(IDbContextFactory<ProjectBoardDbContext> factory) : IStorageMigrationParticipant
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ProjectBoardSettingsRecord
{
    public Guid ProjectId { get; set; }
    public string KeyPrefix { get; set; } = "TIN";
    public long NextSequence { get; set; } = 1;
    public string StorageMode { get; set; } = ProjectBoardStorageModes.RepoTinadec;
    public string? CustomSyncRoot { get; set; }
    public string ProjectInstructions { get; set; } = string.Empty;
    public string DefaultAssigneeKind { get; set; } = ProjectBoardAssigneeKinds.Native;
    public int MaxConcurrency { get; set; } = 2;
    public bool AutopilotEnabled { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

public sealed class ProjectBoardTaskRecord
{
    public Guid TaskId { get; set; }
    public Guid ProjectId { get; set; }
    public string TaskKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DescriptionMd { get; set; } = string.Empty;
    public string ChecklistJson { get; set; } = "[]";
    public string Status { get; set; } = ProjectBoardTaskStatuses.Backlog;
    public string Priority { get; set; } = ProjectBoardPriorities.None;
    public bool Blocked { get; set; }
    public string? BlockedReason { get; set; }
    public string? AssigneeJson { get; set; }
    public Guid? ParentTaskId { get; set; }
    public string DependsOnJson { get; set; } = "[]";
    public string LabelsJson { get; set; } = "[]";
    public string? SpatialRefJson { get; set; }
    public string SortKey { get; set; } = "V";
    public Guid? ActiveSessionId { get; set; }
    public Guid? ActiveRunId { get; set; }
    public string? RunStatus { get; set; }
    public string? WorktreePath { get; set; }
    public Guid? LeaseId { get; set; }
    public string? LiveDigestSummary { get; set; }
    public string? DiffStatsJson { get; set; }
    public long Revision { get; set; } = 1;
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
    public bool Deleted { get; set; }
}

public sealed class ProjectBoardViewRecord
{
    public Guid ViewId { get; set; }
    public string Scope { get; set; } = "global";
    public Guid? ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = "{}";
    public int SortOrder { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

public sealed class ProjectBoardSyncStateRecord
{
    public Guid ProjectId { get; set; }
    public Guid TaskId { get; set; }
    public string FileRelPath { get; set; } = string.Empty;
    public string LastSyncedHash { get; set; } = string.Empty;
    public long LastSyncedRevision { get; set; }
    public string LastSyncedPayloadJson { get; set; } = "{}";
    public bool HasConflict { get; set; }
    public string? ConflictFileSnapshotJson { get; set; }
    public string? ConflictBoardSnapshotJson { get; set; }
    public string? ConflictingFieldsJson { get; set; }
    public long? ConflictDetectedAtUnixMs { get; set; }
}

public sealed class ProjectNotesMetaRecord
{
    public Guid ProjectId { get; set; }
    public string ContentMd { get; set; } = string.Empty;
    public string LastSyncedHash { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public long UpdatedAtUnixMs { get; set; }
}
