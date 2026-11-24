using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime.ProjectBoard;

/// <summary>
/// Bidirectional synchronization service between <see cref="ProjectBoardDbContext"/> and the selected
/// project storage directory (<c>repo_tinadec</c>, <c>app_data</c>, or <c>custom_dir</c>).
/// <para>
/// Enforces:
/// 1) Atomic writes (<see cref="FileOptions.WriteThrough"/> temp + <c>File.Move(overwrite: true)</c>);
/// 2) SHA-256 normalized echo prevention;
/// 3) 250ms debounced <see cref="FileSystemWatcher"/> + startup reconciliation;
/// 4) Non-destructive 3-way field merge (keeping user <c>.md</c> untouched on conflict);
/// 5) HARD GUARDRAIL: passive file sync NEVER dispatches or starts any Agent or CLI run.
/// </para>
/// </summary>
public sealed class ProjectBoardFileSyncService : IHostedService, IProjectBoardSyncHook, IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IDbContextFactory<ProjectBoardDbContext> _dbFactory;
    private readonly ProjectSessionStore _sessionStore;
    private readonly ProjectBoardCoordinator _coordinator;
    private readonly ILogger<ProjectBoardFileSyncService>? _logger;

    private readonly ConcurrentDictionary<Guid, ProjectWatcherRegistration> _watchers = new();
    private volatile bool _disposed;

    private sealed class ProjectWatcherRegistration : IDisposable
    {
        public required Guid ProjectId { get; init; }
        public required string WatchedDirectory { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
        public readonly object DebounceLock = new();
        public CancellationTokenSource? DebounceCts;

        public void Dispose()
        {
            lock (DebounceLock)
            {
                DebounceCts?.Cancel();
                DebounceCts?.Dispose();
                DebounceCts = null;
            }
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
        }
    }

    public ProjectBoardFileSyncService(
        IDbContextFactory<ProjectBoardDbContext> dbFactory,
        ProjectSessionStore sessionStore,
        ProjectBoardCoordinator coordinator,
        ILogger<ProjectBoardFileSyncService>? logger = null)
    {
        _dbFactory = dbFactory;
        _sessionStore = sessionStore;
        _coordinator = coordinator;
        _logger = logger;
        _coordinator.AttachSyncHook(this);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _sessionStore.ListProjectsAsync(LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
            foreach (var project in projects)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    // Reconcile only projects that already initialized board settings or have existing .tinadec/board/tasks/*.md
                    await ReconcileProjectOnStartupIfPresentAsync(project, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "ProjectBoard startup reconciliation skipped for project {ProjectId}.", project.Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "ProjectBoardFileSyncService startup pass encountered an error.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeAllWatchers();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeAllWatchers();
    }

    public async Task OnStorageSettingsChangedAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        StopWatcher(projectId);
        await SyncProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportTaskToMarkdownAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.LifecycleStatus != LifecycleStatuses.Active)
            return;

        var gate = _coordinator.GetProjectGate(projectId);
        string boardTasksDir;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var (syncRoot, tasksDir, notesFilePath) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;

            var taskRow = await db.BoardTasks
                .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId && !x.Deleted, cancellationToken)
                .ConfigureAwait(false);
            if (taskRow is null)
                return;

            var syncRow = await db.BoardSyncStates
                .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId, cancellationToken)
                .ConfigureAwait(false);

            // Never silently overwrite an unresolved conflict file on disk!
            if (syncRow?.HasConflict == true)
                return;

            EnsureNotesStubIfNeeded(notesFilePath, project.Name);
            await ExportSingleTaskUnderLockAsync(db, project, syncRoot, tasksDir, taskRow, syncRow, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        EnsureWatcher(projectId, boardTasksDir);
    }

    public async Task DeleteTaskMarkdownAsync(Guid projectId, string taskKey, CancellationToken cancellationToken = default)
    {
        var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await db.BoardSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (settings is null) return;

        var (syncRoot, tasksDir, _) = _coordinator.ResolveStoragePaths(project, settings);
        var filePath = Path.Combine(tasksDir, $"{SanitizeFileStem(taskKey)}.md");
        try
        {
            StorageScopePaths.RejectLinks(syncRoot, filePath);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not delete task markdown file {FilePath}.", filePath);
        }
    }

    /// <summary>
    /// Runs an explicit bidirectional reconciliation pass between the project's board directory and SQLite/PostgreSQL.
    /// Passive imports/merges NEVER dispatch or start any Agent or CLI run.
    /// </summary>
    public async Task<ProjectBoardSyncResultDto> SyncProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = _coordinator.GetProjectGate(projectId);

        int imported = 0, exported = 0, merged = 0, unchanged = 0, conflicts = 0;
        string storageMode;
        string boardTasksDir;
        long syncedAtUnixMs;
        var touchedTaskDtos = new List<ProjectBoardTaskDto>();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            storageMode = settings.StorageMode;
            var (syncRoot, tasksDir, notesFilePath) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;

            StorageScopePaths.RejectLinks(syncRoot);
            Directory.CreateDirectory(boardTasksDir);
            EnsureNotesStubIfNeeded(notesFilePath, project.Name);

            var taskRows = await db.BoardTasks
                .Where(x => x.ProjectId == projectId && !x.Deleted)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var syncRows = await db.BoardSyncStates
                .Where(x => x.ProjectId == projectId)
                .ToDictionaryAsync(x => x.TaskId, cancellationToken)
                .ConfigureAwait(false);

            var byId = taskRows.ToDictionary(x => x.TaskId);
            var byKey = taskRows.ToDictionary(x => x.TaskKey, StringComparer.OrdinalIgnoreCase);
            var handledTaskIds = new HashSet<Guid>();
            var nowMs = _coordinator.NowUnixMs();
            syncedAtUnixMs = nowMs;

            var mdFiles = Directory.Exists(boardTasksDir)
                ? Directory.EnumerateFiles(boardTasksDir, "*.md", SearchOption.TopDirectoryOnly)
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];

            foreach (var filePath in mdFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StorageScopePaths.RejectLinks(syncRoot, filePath);

                var rawContent = await File.ReadAllTextAsync(filePath, Utf8NoBom, cancellationToken).ConfigureAwait(false);
                var fileHash = ProjectBoardMarkdownCodec.ComputeNormalizedSha256(rawContent);
                var stemKey = Path.GetFileNameWithoutExtension(filePath);
                var parsed = ProjectBoardMarkdownCodec.ParseTaskMarkdown(rawContent, stemKey, nowMs);

                ProjectBoardTaskRecord? matchedTask = null;
                if (parsed.TaskId.HasValue && byId.TryGetValue(parsed.TaskId.Value, out var foundById))
                {
                    matchedTask = foundById;
                }
                else if (!string.IsNullOrWhiteSpace(parsed.TaskKey) && byKey.TryGetValue(parsed.TaskKey!, out var foundByKey))
                {
                    matchedTask = foundByKey;
                }

                if (matchedTask is null)
                {
                    // Brand-new .md file created externally -> passive import into board DB (NEVER starts a run!)
                    var newTaskId = parsed.TaskId.HasValue && parsed.TaskId.Value != Guid.Empty
                        && !await db.BoardTasks.AnyAsync(x => x.TaskId == parsed.TaskId.Value, cancellationToken).ConfigureAwait(false)
                        ? parsed.TaskId.Value
                        : Guid.NewGuid();

                    var candidateKey = !string.IsNullOrWhiteSpace(parsed.TaskKey) && !byKey.ContainsKey(parsed.TaskKey!)
                        ? parsed.TaskKey!.Trim()
                        : ProjectBoardCoordinator.AllocateNextTaskKey(settings, taskRows);

                    ProjectBoardCoordinator.BumpSequenceFromImportedKey(settings, candidateKey);

                    var graphMap = ProjectBoardCoordinator.BuildGraphMap(taskRows);
                    var validParent = parsed.Snapshot.ParentTaskId;
                    var validDeps = parsed.Snapshot.DependsOn;
                    try
                    {
                        ProjectBoardCoordinator.ValidateNoCyclesOrThrow(newTaskId, validParent, validDeps, graphMap, requireExistingReferences: false);
                    }
                    catch (ProjectBoardCycleException)
                    {
                        validParent = null;
                        validDeps = [];
                    }

                    var sortKey = ProjectBoardFractionalIndex.Normalize(
                        parsed.Snapshot.SortKey,
                        taskRows.Where(r => r.Status == parsed.Snapshot.Status).Select(r => r.SortKey));

                    var importedRow = new ProjectBoardTaskRecord
                    {
                        TaskId = newTaskId,
                        ProjectId = projectId,
                        TaskKey = candidateKey,
                        Title = parsed.Snapshot.Title,
                        DescriptionMd = parsed.Snapshot.DescriptionMd,
                        ChecklistJson = JsonSerializer.Serialize(parsed.Snapshot.Checklist, ProjectBoardMarkdownCodec.CanonicalJson),
                        Status = parsed.Snapshot.Status,
                        Priority = parsed.Snapshot.Priority,
                        Blocked = parsed.Snapshot.Blocked,
                        BlockedReason = parsed.Snapshot.BlockedReason,
                        AssigneeJson = parsed.Snapshot.Assignee is null ? null : JsonSerializer.Serialize(parsed.Snapshot.Assignee, ProjectBoardMarkdownCodec.CanonicalJson),
                        ParentTaskId = validParent,
                        DependsOnJson = JsonSerializer.Serialize(validDeps, ProjectBoardMarkdownCodec.CanonicalJson),
                        LabelsJson = JsonSerializer.Serialize(parsed.Snapshot.Labels, ProjectBoardMarkdownCodec.CanonicalJson),
                        SpatialRefJson = parsed.Snapshot.SpatialRef is null ? null : JsonSerializer.Serialize(parsed.Snapshot.SpatialRef, ProjectBoardMarkdownCodec.CanonicalJson),
                        SortKey = sortKey,
                        Revision = Math.Max(1L, parsed.Snapshot.Revision),
                        CreatedAtUnixMs = parsed.Snapshot.UpdatedAtUnixMs,
                        UpdatedAtUnixMs = nowMs,
                        Deleted = false
                    };

                    db.BoardTasks.Add(importedRow);
                    taskRows.Add(importedRow);
                    byId[importedRow.TaskId] = importedRow;
                    byKey[importedRow.TaskKey] = importedRow;
                    handledTaskIds.Add(importedRow.TaskId);

                    var snap = ProjectBoardMarkdownCodec.ToSnapshot(importedRow);
                    var relPath = Path.GetRelativePath(syncRoot, filePath).Replace('\\', '/');
                    var newSync = new ProjectBoardSyncStateRecord
                    {
                        ProjectId = projectId,
                        TaskId = importedRow.TaskId,
                        FileRelPath = relPath,
                        LastSyncedHash = fileHash,
                        LastSyncedRevision = importedRow.Revision,
                        LastSyncedPayloadJson = JsonSerializer.Serialize(snap, ProjectBoardMarkdownCodec.CanonicalJson),
                        HasConflict = false
                    };
                    db.BoardSyncStates.Add(newSync);
                    syncRows[importedRow.TaskId] = newSync;

                    imported++;
                    touchedTaskDtos.Add(ProjectBoardCoordinator.ToTaskDto(importedRow, project.Name, newSync));
                    continue;
                }

                handledTaskIds.Add(matchedTask.TaskId);
                syncRows.TryGetValue(matchedTask.TaskId, out var syncRow);

                // Anti-echo fast path: file hash matches last synced hash
                if (syncRow is not null && string.Equals(fileHash, syncRow.LastSyncedHash, StringComparison.Ordinal))
                {
                    if (syncRow.HasConflict)
                    {
                        conflicts++;
                        continue;
                    }

                    if (matchedTask.Revision > syncRow.LastSyncedRevision)
                    {
                        await ExportSingleTaskUnderLockAsync(db, project, syncRoot, boardTasksDir, matchedTask, syncRow, cancellationToken).ConfigureAwait(false);
                        exported++;
                    }
                    else
                    {
                        unchanged++;
                    }
                    continue;
                }

                var boardSnap = ProjectBoardMarkdownCodec.ToSnapshot(matchedTask);
                var fileSnap = parsed.Snapshot;

                // If both sides already represent the exact same declarative content, record new hash without rewriting user's file.
                if (ProjectBoardMarkdownCodec.DeclarativeEquals(fileSnap, boardSnap))
                {
                    syncRow = UpsertSyncStateClean(db, syncRows, projectId, matchedTask, syncRoot, filePath, fileHash, boardSnap);
                    unchanged++;
                    continue;
                }

                var baseSnap = syncRow is not null
                    ? ProjectBoardMarkdownCodec.DeserializeNullable<ProjectBoardTaskSnapshotDto>(syncRow.LastSyncedPayloadJson) ?? boardSnap
                    : boardSnap;

                // If there was no prior sync baseline and both exist with divergent content, treat board as base if revision==1 else 3-way merge.
                var (mergedSnap, conflictingFields, changedFromBoard) = ProjectBoardMarkdownCodec.MergeThreeWay(
                    baseSnap, fileSnap, boardSnap, nowMs);

                if (conflictingFields.Count > 0)
                {
                    // Same-field divergence: DO NOT overwrite user's .md file! Persist conflict snapshot for 1-click UI resolution.
                    var relPath = Path.GetRelativePath(syncRoot, filePath).Replace('\\', '/');
                    if (syncRow is null)
                    {
                        syncRow = new ProjectBoardSyncStateRecord
                        {
                            ProjectId = projectId,
                            TaskId = matchedTask.TaskId,
                            FileRelPath = relPath,
                            LastSyncedHash = string.Empty,
                            LastSyncedRevision = matchedTask.Revision,
                            LastSyncedPayloadJson = JsonSerializer.Serialize(baseSnap, ProjectBoardMarkdownCodec.CanonicalJson)
                        };
                        db.BoardSyncStates.Add(syncRow);
                        syncRows[matchedTask.TaskId] = syncRow;
                    }

                    syncRow.FileRelPath = relPath;
                    syncRow.HasConflict = true;
                    syncRow.ConflictFileSnapshotJson = JsonSerializer.Serialize(fileSnap, ProjectBoardMarkdownCodec.CanonicalJson);
                    syncRow.ConflictBoardSnapshotJson = JsonSerializer.Serialize(boardSnap, ProjectBoardMarkdownCodec.CanonicalJson);
                    syncRow.ConflictingFieldsJson = JsonSerializer.Serialize(conflictingFields, ProjectBoardMarkdownCodec.CanonicalJson);
                    syncRow.ConflictDetectedAtUnixMs = nowMs;

                    conflicts++;
                    touchedTaskDtos.Add(ProjectBoardCoordinator.ToTaskDto(matchedTask, project.Name, syncRow));
                    continue;
                }

                // Validate DAG before applying merged fields to the board task.
                var existingGraph = ProjectBoardCoordinator.BuildGraphMap(taskRows);
                try
                {
                    ProjectBoardCoordinator.ValidateNoCyclesOrThrow(
                        matchedTask.TaskId,
                        mergedSnap.ParentTaskId,
                        mergedSnap.DependsOn,
                        existingGraph,
                        requireExistingReferences: false);
                }
                catch (ProjectBoardCycleException)
                {
                    var relPath = Path.GetRelativePath(syncRoot, filePath).Replace('\\', '/');
                    syncRow ??= new ProjectBoardSyncStateRecord
                    {
                        ProjectId = projectId,
                        TaskId = matchedTask.TaskId,
                        FileRelPath = relPath,
                        LastSyncedHash = string.Empty,
                        LastSyncedRevision = matchedTask.Revision,
                        LastSyncedPayloadJson = JsonSerializer.Serialize(baseSnap, ProjectBoardMarkdownCodec.CanonicalJson)
                    };
                    if (!syncRows.ContainsKey(matchedTask.TaskId))
                    {
                        db.BoardSyncStates.Add(syncRow);
                        syncRows[matchedTask.TaskId] = syncRow;
                    }
                    syncRow.HasConflict = true;
                    syncRow.ConflictFileSnapshotJson = JsonSerializer.Serialize(fileSnap, ProjectBoardMarkdownCodec.CanonicalJson);
                    syncRow.ConflictBoardSnapshotJson = JsonSerializer.Serialize(boardSnap, ProjectBoardMarkdownCodec.CanonicalJson);
                    syncRow.ConflictingFieldsJson = JsonSerializer.Serialize(new[] { "depends_on", "parent_task_id" }, ProjectBoardMarkdownCodec.CanonicalJson);
                    syncRow.ConflictDetectedAtUnixMs = nowMs;
                    conflicts++;
                    touchedTaskDtos.Add(ProjectBoardCoordinator.ToTaskDto(matchedTask, project.Name, syncRow));
                    continue;
                }

                ApplySnapshotToRecord(matchedTask, mergedSnap, nowMs);

                // If board also contributed changes (true 3-way merge), write merged markdown back to disk;
                // if only the file changed (fast-forward from file), preserve the user's exact .md bytes and record its hash!
                var boardAlsoChanged = !ProjectBoardMarkdownCodec.DeclarativeEquals(boardSnap, baseSnap);
                if (boardAlsoChanged)
                {
                    await ExportSingleTaskUnderLockAsync(db, project, syncRoot, boardTasksDir, matchedTask, syncRow, cancellationToken).ConfigureAwait(false);
                    syncRows.TryGetValue(matchedTask.TaskId, out syncRow);
                }
                else
                {
                    var currentSnap = ProjectBoardMarkdownCodec.ToSnapshot(matchedTask);
                    syncRow = UpsertSyncStateClean(db, syncRows, projectId, matchedTask, syncRoot, filePath, fileHash, currentSnap);
                }

                merged++;
                if (changedFromBoard || syncRow?.HasConflict == false)
                {
                    touchedTaskDtos.Add(ProjectBoardCoordinator.ToTaskDto(matchedTask, project.Name, syncRow));
                }
            }

            // Export any DB tasks that had no corresponding .md file on disk yet.
            foreach (var row in taskRows)
            {
                if (handledTaskIds.Contains(row.TaskId))
                    continue;

                syncRows.TryGetValue(row.TaskId, out var syncRow);
                if (syncRow?.HasConflict == true)
                {
                    conflicts++;
                    continue;
                }

                await ExportSingleTaskUnderLockAsync(db, project, syncRoot, boardTasksDir, row, syncRow, cancellationToken).ConfigureAwait(false);
                exported++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        EnsureWatcher(projectId, boardTasksDir);

        foreach (var dto in touchedTaskDtos)
        {
            await _coordinator.PublishEventAsync(new ProjectBoardEventDto
            {
                EventType = "task_upserted",
                ProjectId = projectId,
                TaskId = dto.TaskId,
                Task = dto,
                OccurredAtUnixMs = syncedAtUnixMs
            }, cancellationToken).ConfigureAwait(false);
        }

        await _coordinator.PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "sync_completed",
            ProjectId = projectId,
            OccurredAtUnixMs = syncedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return new ProjectBoardSyncResultDto
        {
            ProjectId = projectId,
            StorageMode = storageMode,
            ResolvedBoardDir = boardTasksDir,
            ImportedCount = imported,
            ExportedCount = exported,
            MergedCount = merged,
            UnchangedCount = unchanged,
            ConflictCount = conflicts,
            SyncedAtUnixMs = syncedAtUnixMs
        };
    }

    public async Task<ProjectBoardTaskDto?> ResolveConflictAsync(
        Guid projectId,
        Guid taskId,
        ResolveProjectBoardConflictRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolution = request.Resolution?.Trim().ToLowerInvariant() ?? string.Empty;
        if (resolution is not ("keep_file" or "keep_board"))
            throw new ArgumentException("resolution must be 'keep_file' or 'keep_board'.");

        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = _coordinator.GetProjectGate(projectId);

        ProjectBoardTaskDto resolvedDto;
        string boardTasksDir;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var (syncRoot, tasksDir, _) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;

            var taskRows = await db.BoardTasks
                .Where(x => x.ProjectId == projectId && !x.Deleted)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var taskRow = taskRows.FirstOrDefault(x => x.TaskId == taskId);
            if (taskRow is null)
                return null;

            var syncRow = await db.BoardSyncStates
                .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId, cancellationToken)
                .ConfigureAwait(false);
            if (syncRow is null || !syncRow.HasConflict)
                return ProjectBoardCoordinator.ToTaskDto(taskRow, project.Name, syncRow);

            var nowMs = _coordinator.NowUnixMs();
            if (resolution == "keep_file")
            {
                ProjectBoardTaskSnapshotDto? fileSnap = null;
                var diskPath = Path.Combine(tasksDir, $"{SanitizeFileStem(taskRow.TaskKey)}.md");
                if (File.Exists(diskPath))
                {
                    StorageScopePaths.RejectLinks(syncRoot, diskPath);
                    var raw = await File.ReadAllTextAsync(diskPath, Utf8NoBom, cancellationToken).ConfigureAwait(false);
                    fileSnap = ProjectBoardMarkdownCodec.ParseTaskMarkdown(raw, taskRow.TaskKey, nowMs).Snapshot;
                }
                fileSnap ??= ProjectBoardMarkdownCodec.DeserializeNullable<ProjectBoardTaskSnapshotDto>(syncRow.ConflictFileSnapshotJson);
                if (fileSnap is null)
                    throw new InvalidOperationException("No file snapshot available to resolve conflict.");

                var graphMap = ProjectBoardCoordinator.BuildGraphMap(taskRows);
                ProjectBoardCoordinator.ValidateNoCyclesOrThrow(
                    taskId,
                    fileSnap.ParentTaskId,
                    fileSnap.DependsOn,
                    graphMap,
                    requireExistingReferences: false);

                ApplySnapshotToRecord(taskRow, fileSnap, nowMs);
            }
            else
            {
                taskRow.Revision += 1;
                taskRow.UpdatedAtUnixMs = nowMs;
            }

            syncRow.HasConflict = false;
            syncRow.ConflictFileSnapshotJson = null;
            syncRow.ConflictBoardSnapshotJson = null;
            syncRow.ConflictingFieldsJson = null;
            syncRow.ConflictDetectedAtUnixMs = null;

            await ExportSingleTaskUnderLockAsync(db, project, syncRoot, tasksDir, taskRow, syncRow, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            resolvedDto = ProjectBoardCoordinator.ToTaskDto(taskRow, project.Name, syncRow);
        }
        finally
        {
            gate.Release();
        }

        EnsureWatcher(projectId, boardTasksDir);

        await _coordinator.PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "task_upserted",
            ProjectId = projectId,
            TaskId = taskId,
            Task = resolvedDto,
            OccurredAtUnixMs = resolvedDto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return resolvedDto;
    }

    private async Task ReconcileProjectOnStartupIfPresentAsync(ProjectRecord project, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var settings = await db.BoardSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == project.Id, ct)
            .ConfigureAwait(false);
        var hasTasks = await db.BoardTasks.AsNoTracking()
            .AnyAsync(x => x.ProjectId == project.Id && !x.Deleted, ct)
            .ConfigureAwait(false);
        var defaultTinadecTasksDir = Path.Combine(project.RootPath, ".tinadec", "board", "tasks");

        if (settings is null && !hasTasks && !Directory.Exists(defaultTinadecTasksDir))
            return;

        await SyncProjectAsync(project.Id, ct).ConfigureAwait(false);
    }

    private static async Task ExportSingleTaskUnderLockAsync(
        ProjectBoardDbContext db,
        ProjectRecord project,
        string syncRoot,
        string boardTasksDir,
        ProjectBoardTaskRecord taskRow,
        ProjectBoardSyncStateRecord? syncRow,
        CancellationToken ct)
    {
        StorageScopePaths.RejectLinks(syncRoot);
        Directory.CreateDirectory(boardTasksDir);

        var filePath = Path.Combine(boardTasksDir, $"{SanitizeFileStem(taskRow.TaskKey)}.md");
        StorageScopePaths.RejectLinks(syncRoot, filePath);

        var snapshot = ProjectBoardMarkdownCodec.ToSnapshot(taskRow);
        var markdown = ProjectBoardMarkdownCodec.FormatTaskMarkdown(taskRow.TaskId, taskRow.TaskKey, snapshot);
        var sha256 = ProjectBoardMarkdownCodec.ComputeNormalizedSha256(markdown);

        await AtomicWriteFileAsync(syncRoot, filePath, markdown, ct).ConfigureAwait(false);

        var relPath = Path.GetRelativePath(syncRoot, filePath).Replace('\\', '/');
        if (syncRow is null)
        {
            syncRow = new ProjectBoardSyncStateRecord
            {
                ProjectId = project.Id,
                TaskId = taskRow.TaskId
            };
            db.BoardSyncStates.Add(syncRow);
        }

        syncRow.FileRelPath = relPath;
        syncRow.LastSyncedHash = sha256;
        syncRow.LastSyncedRevision = taskRow.Revision;
        syncRow.LastSyncedPayloadJson = JsonSerializer.Serialize(snapshot, ProjectBoardMarkdownCodec.CanonicalJson);
        syncRow.HasConflict = false;
        syncRow.ConflictFileSnapshotJson = null;
        syncRow.ConflictBoardSnapshotJson = null;
        syncRow.ConflictingFieldsJson = null;
        syncRow.ConflictDetectedAtUnixMs = null;
    }

    private static ProjectBoardSyncStateRecord UpsertSyncStateClean(
        ProjectBoardDbContext db,
        Dictionary<Guid, ProjectBoardSyncStateRecord> syncRows,
        Guid projectId,
        ProjectBoardTaskRecord taskRow,
        string syncRoot,
        string filePath,
        string fileHash,
        ProjectBoardTaskSnapshotDto snapshot)
    {
        var relPath = Path.GetRelativePath(syncRoot, filePath).Replace('\\', '/');
        if (!syncRows.TryGetValue(taskRow.TaskId, out var syncRow))
        {
            syncRow = new ProjectBoardSyncStateRecord
            {
                ProjectId = projectId,
                TaskId = taskRow.TaskId
            };
            db.BoardSyncStates.Add(syncRow);
            syncRows[taskRow.TaskId] = syncRow;
        }

        syncRow.FileRelPath = relPath;
        syncRow.LastSyncedHash = fileHash;
        syncRow.LastSyncedRevision = taskRow.Revision;
        syncRow.LastSyncedPayloadJson = JsonSerializer.Serialize(snapshot, ProjectBoardMarkdownCodec.CanonicalJson);
        syncRow.HasConflict = false;
        syncRow.ConflictFileSnapshotJson = null;
        syncRow.ConflictBoardSnapshotJson = null;
        syncRow.ConflictingFieldsJson = null;
        syncRow.ConflictDetectedAtUnixMs = null;
        return syncRow;
    }

    private static void ApplySnapshotToRecord(ProjectBoardTaskRecord row, ProjectBoardTaskSnapshotDto snap, long nowUnixMs)
    {
        row.Title = snap.Title;
        row.DescriptionMd = snap.DescriptionMd;
        row.ChecklistJson = JsonSerializer.Serialize(snap.Checklist, ProjectBoardMarkdownCodec.CanonicalJson);
        row.Status = snap.Status;
        row.Priority = snap.Priority;
        row.Blocked = snap.Blocked;
        row.BlockedReason = snap.BlockedReason;
        row.AssigneeJson = snap.Assignee is null ? null : JsonSerializer.Serialize(snap.Assignee, ProjectBoardMarkdownCodec.CanonicalJson);
        row.ParentTaskId = snap.ParentTaskId;
        row.DependsOnJson = JsonSerializer.Serialize(snap.DependsOn, ProjectBoardMarkdownCodec.CanonicalJson);
        row.LabelsJson = JsonSerializer.Serialize(snap.Labels, ProjectBoardMarkdownCodec.CanonicalJson);
        row.SpatialRefJson = snap.SpatialRef is null ? null : JsonSerializer.Serialize(snap.SpatialRef, ProjectBoardMarkdownCodec.CanonicalJson);
        row.SortKey = ProjectBoardFractionalIndex.Normalize(snap.SortKey);
        row.Revision += 1;
        row.UpdatedAtUnixMs = nowUnixMs;
    }

    internal static async Task AtomicWriteFileAsync(string syncRoot, string targetPath, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);
        StorageScopePaths.RejectLinks(syncRoot, targetPath);

        var tempPath = $"{targetPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void EnsureNotesStubIfNeeded(string notesFilePath, string projectName)
    {
        try
        {
            if (File.Exists(notesFilePath))
                return;

            var dir = Path.GetDirectoryName(notesFilePath)!;
            Directory.CreateDirectory(dir);
            var stub = $"# {projectName} — Project Notes\n\n";
            using var fs = new FileStream(notesFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            fs.Write(Utf8NoBom.GetBytes(stub));
        }
        catch (IOException)
        {
            // Already created concurrently or read-only directory.
        }
    }

    private void EnsureWatcher(Guid projectId, string boardTasksDir)
    {
        if (_disposed || !Directory.Exists(boardTasksDir))
            return;

        var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boardTasksDir));
        if (_watchers.TryGetValue(projectId, out var existing))
        {
            if (StorageScopeInitializer.SamePath(existing.WatchedDirectory, fullDir))
                return;
            if (_watchers.TryRemove(projectId, out var stale))
                stale.Dispose();
        }

        try
        {
            var watcher = new FileSystemWatcher(fullDir, "*.md")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };

            var reg = new ProjectWatcherRegistration
            {
                ProjectId = projectId,
                WatchedDirectory = fullDir,
                Watcher = watcher
            };

            void OnFsEvent(object _, FileSystemEventArgs e)
            {
                var name = Path.GetFileName(e.FullPath);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase))
                    return;
                ScheduleDebouncedSync(reg);
            }

            watcher.Created += OnFsEvent;
            watcher.Changed += OnFsEvent;
            watcher.Deleted += OnFsEvent;
            watcher.Renamed += (_, e) => OnFsEvent(watcher, e);
            watcher.EnableRaisingEvents = true;

            if (!_watchers.TryAdd(projectId, reg))
                reg.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger?.LogDebug(ex, "Could not attach FileSystemWatcher on {Dir}.", fullDir);
        }
    }

    private void ScheduleDebouncedSync(ProjectWatcherRegistration reg)
    {
        if (_disposed) return;

        CancellationTokenSource cts;
        lock (reg.DebounceLock)
        {
            if (_disposed) return;
            reg.DebounceCts?.Cancel();
            reg.DebounceCts?.Dispose();
            cts = new CancellationTokenSource();
            reg.DebounceCts = cts;
        }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WatcherDebounce, token).ConfigureAwait(false);
                if (_disposed || token.IsCancellationRequested) return;
                await SyncProjectAsync(reg.ProjectId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Debounced FileSystemWatcher sync failed for project {ProjectId}.", reg.ProjectId);
            }
        }, CancellationToken.None);
    }

    private void StopWatcher(Guid projectId)
    {
        if (_watchers.TryRemove(projectId, out var reg))
            reg.Dispose();
    }

    private void DisposeAllWatchers()
    {
        foreach (var key in _watchers.Keys.ToArray())
        {
            if (_watchers.TryRemove(key, out var reg))
                reg.Dispose();
        }
    }

    private static string SanitizeFileStem(string taskKey)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(taskKey.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "TASK" : cleaned;
    }
}
