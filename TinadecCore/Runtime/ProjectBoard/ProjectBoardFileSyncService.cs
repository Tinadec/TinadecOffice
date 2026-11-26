using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
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
/// 2) SHA-256 normalized echo prevention across both <c>board/tasks/*.md</c> and <c>project/notes.md</c>;
/// 3) 250ms debounced <see cref="FileSystemWatcher"/> + startup reconciliation;
/// 4) Non-destructive 3-way field merge (keeping user <c>.md</c> untouched on conflict);
/// 5) HARD GUARDRAIL: passive file sync NEVER dispatches or starts any Agent or CLI run.
/// </para>
/// </summary>
public sealed class ProjectBoardFileSyncService : IHostedService, IProjectBoardSyncHook, IProjectNotesContextReader, IDisposable
{
    public const string DecisionsSectionHeading = "## 决策与进展记录";
    public const int DefaultNotesContextMaxChars = 2400;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly Regex DecisionsHeadingRegex = new(
        @"^##\s+决策与进展记录\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnyH2OrH1Regex = new(
        @"^#{1,2}\s+\S",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IDbContextFactory<ProjectBoardDbContext> _dbFactory;
    private readonly ProjectSessionStore _sessionStore;
    private readonly ProjectBoardCoordinator _coordinator;
    private readonly ILogger<ProjectBoardFileSyncService>? _logger;

    private readonly ConcurrentDictionary<Guid, ProjectWatcherRegistration> _watchers = new();
    private readonly object _startupGate = new();
    private CancellationTokenSource? _startupCts;
    private Task? _startupTask;
    private volatile bool _disposed;

    private sealed class ProjectWatcherRegistration : IDisposable
    {
        public required Guid ProjectId { get; init; }
        public required string WatchedDirectory { get; init; }
        public required string WatchedNotesDirectory { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
        public FileSystemWatcher? NotesWatcher { get; init; }
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
            if (NotesWatcher is not null)
            {
                NotesWatcher.EnableRaisingEvents = false;
                NotesWatcher.Dispose();
            }
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return Task.CompletedTask;
        lock (_startupGate)
        {
            if (_disposed || _startupTask is not null)
                return Task.CompletedTask;
            _startupCts = new CancellationTokenSource();
            var ct = _startupCts.Token;
            _startupTask = Task.Run(() => RunStartupReconciliationAsync(ct), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    private async Task RunStartupReconciliationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _sessionStore.ListProjectsAsync(LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
            if (projects.Count == 0)
                return;

            Dictionary<Guid, ProjectBoardSettingsRecord> settingsByProject;
            HashSet<Guid> presentInDb;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var settingsRows = await db.BoardSettings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
                settingsByProject = settingsRows.ToDictionary(x => x.ProjectId);
                var taskIds = await db.BoardTasks.AsNoTracking().Where(x => !x.Deleted).Select(x => x.ProjectId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
                var notesIds = await db.NotesMeta.AsNoTracking().Select(x => x.ProjectId).ToListAsync(cancellationToken).ConfigureAwait(false);
                presentInDb = new HashSet<Guid>(settingsRows.Count + taskIds.Count + notesIds.Count);
                foreach (var row in settingsRows) presentInDb.Add(row.ProjectId);
                foreach (var id in taskIds) presentInDb.Add(id);
                foreach (var id in notesIds) presentInDb.Add(id);
            }

            var toSync = new List<ProjectRecord>();
            foreach (var project in projects)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var rootExists = !string.IsNullOrWhiteSpace(project.RootPath) && Directory.Exists(project.RootPath);
                settingsByProject.TryGetValue(project.Id, out var settings);
                if (!rootExists)
                {
                    var hasLiveExternalSyncRoot = settings is not null
                        && (settings.StorageMode == ProjectBoardStorageModes.AppData
                            || (settings.StorageMode == ProjectBoardStorageModes.CustomDir
                                && !string.IsNullOrWhiteSpace(settings.CustomSyncRoot)
                                && Directory.Exists(settings.CustomSyncRoot)));
                    if (!hasLiveExternalSyncRoot)
                        continue;
                }
                if (presentInDb.Contains(project.Id)
                    || (rootExists
                        && (Directory.Exists(Path.Combine(project.RootPath, ".tinadec", "board", "tasks"))
                            || File.Exists(Path.Combine(project.RootPath, ".tinadec", "project", "notes.md")))))
                {
                    toSync.Add(project);
                }
            }

            if (toSync.Count == 0)
                return;

            await Parallel.ForEachAsync(
                toSync,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (project, ct) =>
                {
                    try
                    {
                        await SyncProjectAsync(project.Id, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex, "ProjectBoard startup reconciliation skipped for project {ProjectId}.", project.Id);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "ProjectBoardFileSyncService startup pass encountered an error.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? startupTask;
        lock (_startupGate)
        {
            _startupCts?.Cancel();
            startupTask = _startupTask;
        }
        if (startupTask is not null)
        {
            try
            {
                await startupTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        DisposeAllWatchers();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_startupGate)
        {
            _startupCts?.Cancel();
            _startupCts?.Dispose();
            _startupCts = null;
        }
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
        string notesPath;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var (syncRoot, tasksDir, notesFilePath) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;
            notesPath = notesFilePath;

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

            _ = await EnsureOrReconcileNotesUnderLockAsync(db, project, settings, syncRoot, notesFilePath, cancellationToken).ConfigureAwait(false);
            await ExportSingleTaskUnderLockAsync(db, project, syncRoot, tasksDir, taskRow, syncRow, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        EnsureWatcher(projectId, boardTasksDir, notesPath);
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
        string notesPath;
        long syncedAtUnixMs;
        var touchedTaskDtos = new List<ProjectBoardTaskDto>();
        ProjectNotesDto? externallyUpdatedNotes = null;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            storageMode = settings.StorageMode;
            var (syncRoot, tasksDir, notesFilePath) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;
            notesPath = notesFilePath;

            StorageScopePaths.RejectLinks(syncRoot);
            Directory.CreateDirectory(boardTasksDir);
            var (reconciledNotes, notesChangedExternally) = await EnsureOrReconcileNotesUnderLockAsync(
                db, project, settings, syncRoot, notesFilePath, cancellationToken).ConfigureAwait(false);
            if (notesChangedExternally)
                externallyUpdatedNotes = reconciledNotes;

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

        EnsureWatcher(projectId, boardTasksDir, notesPath);

        if (externallyUpdatedNotes is not null)
        {
            await _coordinator.PublishEventAsync(new ProjectBoardEventDto
            {
                EventType = "notes_updated",
                ProjectId = projectId,
                Notes = externallyUpdatedNotes,
                OccurredAtUnixMs = externallyUpdatedNotes.UpdatedAtUnixMs
            }, cancellationToken).ConfigureAwait(false);
        }

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
        string notesPath;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var (syncRoot, tasksDir, notesFilePath) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;
            notesPath = notesFilePath;

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

        EnsureWatcher(projectId, boardTasksDir, notesPath);

        await _coordinator.PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "task_upserted",
            ProjectId = projectId,
            TaskId = taskId,
            Task = resolvedDto,
            OccurredAtUnixMs = resolvedDto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        _coordinator.NotifyAutopilot(projectId);
        return resolvedDto;
    }

    // -------------------------------------------------------------------------
    // Project Living Document (notes.md) Bidirectional Sync & Context Reader
    // -------------------------------------------------------------------------

    public async Task<ProjectNotesDto> GetProjectNotesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = _coordinator.GetProjectGate(projectId);

        ProjectNotesDto dto;
        bool changedFromDisk;
        string boardTasksDir;
        string notesPath;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var (syncRoot, tasksDir, notesFilePath) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;
            notesPath = notesFilePath;

            StorageScopePaths.RejectLinks(syncRoot);
            Directory.CreateDirectory(boardTasksDir);

            (dto, changedFromDisk) = await EnsureOrReconcileNotesUnderLockAsync(
                db, project, settings, syncRoot, notesFilePath, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        EnsureWatcher(projectId, boardTasksDir, notesPath);

        if (changedFromDisk)
        {
            await _coordinator.PublishEventAsync(new ProjectBoardEventDto
            {
                EventType = "notes_updated",
                ProjectId = projectId,
                Notes = dto,
                OccurredAtUnixMs = dto.UpdatedAtUnixMs
            }, cancellationToken).ConfigureAwait(false);
        }

        return dto;
    }

    public async Task<ProjectNotesDto> UpdateProjectNotesAsync(
        Guid projectId,
        UpdateProjectNotesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentMd is null)
            throw new ArgumentException("content_md is required.");

        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = _coordinator.GetProjectGate(projectId);

        ProjectNotesDto dto;
        string boardTasksDir;
        string notesPath;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var (syncRoot, tasksDir, notesFilePath) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;
            notesPath = notesFilePath;

            StorageScopePaths.RejectLinks(syncRoot);
            Directory.CreateDirectory(boardTasksDir);

            _ = await EnsureOrReconcileNotesUnderLockAsync(
                db, project, settings, syncRoot, notesFilePath, cancellationToken).ConfigureAwait(false);

            var meta = await db.NotesMeta.FirstAsync(x => x.ProjectId == projectId, cancellationToken).ConfigureAwait(false);

            if (request.ExpectedRevision.HasValue && request.ExpectedRevision.Value != meta.Revision)
            {
                throw new ProjectBoardRevisionConflictException(
                    meta.Revision,
                    $"Expected notes revision {request.ExpectedRevision.Value}, but current notes revision is {meta.Revision}.");
            }

            var normalized = ProjectBoardMarkdownCodec.NormalizeLineEndings(request.ContentMd);
            var sha256 = ProjectBoardMarkdownCodec.ComputeNormalizedSha256(normalized);
            var nowMs = _coordinator.NowUnixMs();

            await AtomicWriteFileAsync(syncRoot, notesFilePath, normalized, cancellationToken).ConfigureAwait(false);

            meta.ContentMd = normalized;
            meta.LastSyncedHash = sha256;
            meta.Revision += 1;
            meta.UpdatedAtUnixMs = nowMs;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            dto = ToNotesDto(meta, notesFilePath, settings.StorageMode);
        }
        finally
        {
            gate.Release();
        }

        EnsureWatcher(projectId, boardTasksDir, notesPath);

        await _coordinator.PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "notes_updated",
            ProjectId = projectId,
            Notes = dto,
            OccurredAtUnixMs = dto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return dto;
    }

    public async Task<ProjectNotesDto> AppendProjectNoteEntryAsync(
        Guid projectId,
        string entryMarkdown,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entryMarkdown))
            return await GetProjectNotesAsync(projectId, cancellationToken).ConfigureAwait(false);

        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = _coordinator.GetProjectGate(projectId);

        ProjectNotesDto dto;
        bool appended = false;
        string boardTasksDir;
        string notesPath;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var (syncRoot, tasksDir, notesFilePath) = _coordinator.ResolveStoragePaths(project, settings);
            boardTasksDir = tasksDir;
            notesPath = notesFilePath;

            StorageScopePaths.RejectLinks(syncRoot);
            Directory.CreateDirectory(boardTasksDir);

            _ = await EnsureOrReconcileNotesUnderLockAsync(
                db, project, settings, syncRoot, notesFilePath, cancellationToken).ConfigureAwait(false);

            var meta = await db.NotesMeta.FirstAsync(x => x.ProjectId == projectId, cancellationToken).ConfigureAwait(false);
            var updatedContent = InsertUnderDecisionsSection(meta.ContentMd, entryMarkdown);

            if (!string.Equals(updatedContent, meta.ContentMd, StringComparison.Ordinal))
            {
                var sha256 = ProjectBoardMarkdownCodec.ComputeNormalizedSha256(updatedContent);
                var nowMs = _coordinator.NowUnixMs();

                await AtomicWriteFileAsync(syncRoot, notesFilePath, updatedContent, cancellationToken).ConfigureAwait(false);

                meta.ContentMd = updatedContent;
                meta.LastSyncedHash = sha256;
                meta.Revision += 1;
                meta.UpdatedAtUnixMs = nowMs;
                appended = true;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            dto = ToNotesDto(meta, notesFilePath, settings.StorageMode);
        }
        finally
        {
            gate.Release();
        }

        EnsureWatcher(projectId, boardTasksDir, notesPath);

        if (appended)
        {
            await _coordinator.PublishEventAsync(new ProjectBoardEventDto
            {
                EventType = "notes_updated",
                ProjectId = projectId,
                Notes = dto,
                OccurredAtUnixMs = dto.UpdatedAtUnixMs
            }, cancellationToken).ConfigureAwait(false);
        }

        return dto;
    }

    public async Task<ProjectLivingContextSnapshot?> ReadProjectContextAsync(
        Guid projectId,
        int maxChars = DefaultNotesContextMaxChars,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            return null;

        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.LifecycleStatus != LifecycleStatuses.Active)
            return null;

        var cap = Math.Clamp(maxChars, 256, 8000);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await db.BoardSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        var meta = await db.NotesMeta.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);

        var instructions = settings?.ProjectInstructions?.Trim() ?? string.Empty;
        if (instructions.Length > cap)
            instructions = instructions[..cap].TrimEnd() + "\n…";

        var rawNotes = meta?.ContentMd;
        long revision = meta?.Revision ?? 0;

        var effectiveSettings = settings ?? new ProjectBoardSettingsRecord
        {
            ProjectId = projectId,
            StorageMode = ProjectBoardStorageModes.RepoTinadec
        };
        var (syncRoot, _, notesFilePath) = _coordinator.ResolveStoragePaths(project, effectiveSettings);

        if (File.Exists(notesFilePath))
        {
            try
            {
                StorageScopePaths.RejectLinks(syncRoot, notesFilePath);
                var diskNotes = ProjectBoardMarkdownCodec.NormalizeLineEndings(
                    await File.ReadAllTextAsync(notesFilePath, Utf8NoBom, cancellationToken).ConfigureAwait(false));
                if (meta is null || ProjectBoardMarkdownCodec.ComputeNormalizedSha256(diskNotes) != meta.LastSyncedHash)
                {
                    rawNotes = diskNotes;
                    if (revision == 0) revision = 1;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall back to DB meta when disk file is transiently locked.
            }
        }

        var notesSummary = SummarizeProjectNotes(rawNotes, cap);
        if (string.IsNullOrWhiteSpace(instructions) && string.IsNullOrWhiteSpace(notesSummary))
            return null;

        return new ProjectLivingContextSnapshot(projectId, instructions, notesSummary, revision);
    }

    public static string SummarizeProjectNotes(string? rawNotes, int maxChars = DefaultNotesContextMaxChars)
    {
        if (string.IsNullOrWhiteSpace(rawNotes))
            return string.Empty;

        var normalized = ProjectBoardMarkdownCodec.NormalizeLineEndings(rawNotes).Trim();
        if (!HasSubstantiveNotesContent(normalized))
            return string.Empty;

        if (normalized.Length <= maxChars)
            return normalized;

        // Prioritize upper sections (project goals & conventions) + the latest entries under "## 决策与进展记录".
        var lines = normalized.Split('\n');
        var decisionsIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (DecisionsHeadingRegex.IsMatch(lines[i].TrimEnd()))
            {
                decisionsIdx = i;
                break;
            }
        }

        if (decisionsIdx < 0)
            return ClipMarkdownLines(normalized, maxChars);

        var nextHeadingIdx = lines.Length;
        for (var i = decisionsIdx + 1; i < lines.Length; i++)
        {
            if (AnyH2OrH1Regex.IsMatch(lines[i].TrimEnd()))
            {
                nextHeadingIdx = i;
                break;
            }
        }

        var headBudget = Math.Max(256, (maxChars * 3) / 5);
        var headText = string.Join('\n', lines.Take(decisionsIdx)).Trim();
        var clippedHead = ClipMarkdownLines(headText, headBudget);

        var tailBudget = Math.Max(160, maxChars - clippedHead.Length - DecisionsSectionHeading.Length - 16);
        var decisionBodyLines = lines[(decisionsIdx + 1)..nextHeadingIdx]
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var selectedRecent = new List<string>();
        var used = 0;
        for (var i = decisionBodyLines.Count - 1; i >= 0; i--)
        {
            var line = decisionBodyLines[i];
            if (used + line.Length + 1 > tailBudget && selectedRecent.Count > 0)
                break;
            if (line.Length > tailBudget)
                line = line[..Math.Max(32, tailBudget - 2)] + "…";
            selectedRecent.Add(line);
            used += line.Length + 1;
        }
        selectedRecent.Reverse();

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(clippedHead))
            sb.Append(clippedHead).Append("\n\n");
        sb.Append(DecisionsSectionHeading).Append('\n');
        if (selectedRecent.Count < decisionBodyLines.Count)
            sb.Append("…\n");
        foreach (var item in selectedRecent)
            sb.Append(item).Append('\n');

        return ClipMarkdownLines(sb.ToString().Trim(), maxChars);
    }

    private static bool HasSubstantiveNotesContent(string normalized)
    {
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            return true;
        }
        return false;
    }

    private static string ClipMarkdownLines(string markdown, int maxChars)
    {
        if (markdown.Length <= maxChars)
            return markdown;

        var cut = markdown.LastIndexOf('\n', Math.Min(maxChars - 2, markdown.Length - 1));
        if (cut > maxChars / 2)
            return markdown[..cut].TrimEnd() + "\n…";
        return markdown[..Math.Max(1, maxChars - 1)].TrimEnd() + "…";
    }

    internal static string InsertUnderDecisionsSection(string currentMarkdown, string entryMarkdown)
    {
        var current = ProjectBoardMarkdownCodec.NormalizeLineEndings(currentMarkdown ?? string.Empty);
        var entry = ProjectBoardMarkdownCodec.NormalizeLineEndings(entryMarkdown).Trim();
        if (entry.Length == 0)
            return current;

        if (!entry.StartsWith("- ", StringComparison.Ordinal)
            && !entry.StartsWith("* ", StringComparison.Ordinal)
            && !entry.StartsWith("### ", StringComparison.Ordinal))
        {
            entry = "- " + entry.Replace("\n", "\n  ", StringComparison.Ordinal);
        }

        var lines = current.Length == 0 ? [] : current.Split('\n').ToList();
        var headingIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (DecisionsHeadingRegex.IsMatch(lines[i].TrimEnd()))
            {
                headingIdx = i;
                break;
            }
        }

        if (headingIdx < 0)
        {
            var trimmedBase = current.TrimEnd();
            return string.IsNullOrEmpty(trimmedBase)
                ? $"{DecisionsSectionHeading}\n\n{entry}\n"
                : $"{trimmedBase}\n\n{DecisionsSectionHeading}\n\n{entry}\n";
        }

        var nextHeadingIdx = lines.Count;
        for (var i = headingIdx + 1; i < lines.Count; i++)
        {
            if (AnyH2OrH1Regex.IsMatch(lines[i].TrimEnd()))
            {
                nextHeadingIdx = i;
                break;
            }
        }

        var insertAt = nextHeadingIdx;
        while (insertAt > headingIdx + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
            insertAt--;

        if (insertAt > headingIdx + 1 && string.Equals(lines[insertAt - 1].Trim(), entry.Trim(), StringComparison.Ordinal))
            return current;

        var sectionWasEmpty = insertAt == headingIdx + 1;
        var insertion = new List<string>();
        if (sectionWasEmpty)
            insertion.Add(string.Empty);
        insertion.AddRange(entry.Split('\n'));
        if (nextHeadingIdx < lines.Count)
            insertion.Add(string.Empty);

        lines.RemoveRange(insertAt, nextHeadingIdx - insertAt);
        lines.InsertRange(insertAt, insertion);

        var joined = string.Join('\n', lines).TrimEnd() + "\n";
        return joined;
    }

    private async Task<(ProjectNotesDto Dto, bool ChangedFromExternalDisk)> EnsureOrReconcileNotesUnderLockAsync(
        ProjectBoardDbContext db,
        ProjectRecord project,
        ProjectBoardSettingsRecord settings,
        string syncRoot,
        string notesFilePath,
        CancellationToken ct)
    {
        StorageScopePaths.RejectLinks(syncRoot);
        var notesDir = Path.GetDirectoryName(notesFilePath)!;
        Directory.CreateDirectory(notesDir);

        var meta = await db.NotesMeta
            .FirstOrDefaultAsync(x => x.ProjectId == project.Id, ct)
            .ConfigureAwait(false);

        var nowMs = _coordinator.NowUnixMs();
        var fileExists = File.Exists(notesFilePath);

        if (fileExists)
        {
            StorageScopePaths.RejectLinks(syncRoot, notesFilePath);
            var rawDisk = await File.ReadAllTextAsync(notesFilePath, Utf8NoBom, ct).ConfigureAwait(false);
            var normalizedDisk = ProjectBoardMarkdownCodec.NormalizeLineEndings(rawDisk);
            var diskHash = ProjectBoardMarkdownCodec.ComputeNormalizedSha256(normalizedDisk);

            if (meta is null)
            {
                meta = new ProjectNotesMetaRecord
                {
                    ProjectId = project.Id,
                    ContentMd = normalizedDisk,
                    LastSyncedHash = diskHash,
                    Revision = 1,
                    UpdatedAtUnixMs = nowMs
                };
                db.NotesMeta.Add(meta);
                return (ToNotesDto(meta, notesFilePath, settings.StorageMode), ChangedFromExternalDisk: false);
            }

            // SHA-256 Anti-Echo: skip when disk file hash matches our last recorded hash.
            if (string.Equals(diskHash, meta.LastSyncedHash, StringComparison.Ordinal))
            {
                return (ToNotesDto(meta, notesFilePath, settings.StorageMode), ChangedFromExternalDisk: false);
            }

            meta.ContentMd = normalizedDisk;
            meta.LastSyncedHash = diskHash;
            meta.Revision += 1;
            meta.UpdatedAtUnixMs = nowMs;
            return (ToNotesDto(meta, notesFilePath, settings.StorageMode), ChangedFromExternalDisk: true);
        }

        if (meta is not null)
        {
            var content = ProjectBoardMarkdownCodec.NormalizeLineEndings(meta.ContentMd);
            var hash = ProjectBoardMarkdownCodec.ComputeNormalizedSha256(content);
            await AtomicWriteFileAsync(syncRoot, notesFilePath, content, ct).ConfigureAwait(false);
            meta.LastSyncedHash = hash;
            return (ToNotesDto(meta, notesFilePath, settings.StorageMode), ChangedFromExternalDisk: false);
        }

        var defaultStub = BuildDefaultNotesStub(project.Name);
        var stubHash = ProjectBoardMarkdownCodec.ComputeNormalizedSha256(defaultStub);
        await AtomicWriteFileAsync(syncRoot, notesFilePath, defaultStub, ct).ConfigureAwait(false);

        meta = new ProjectNotesMetaRecord
        {
            ProjectId = project.Id,
            ContentMd = defaultStub,
            LastSyncedHash = stubHash,
            Revision = 1,
            UpdatedAtUnixMs = nowMs
        };
        db.NotesMeta.Add(meta);
        return (ToNotesDto(meta, notesFilePath, settings.StorageMode), ChangedFromExternalDisk: false);
    }

    private static string BuildDefaultNotesStub(string projectName) =>
        $"# {projectName} — Project Notes\n\n## 项目目标\n\n## 架构约定\n\n{DecisionsSectionHeading}\n";

    private static ProjectNotesDto ToNotesDto(ProjectNotesMetaRecord meta, string resolvedFilePath, string storageMode) => new()
    {
        ProjectId = meta.ProjectId,
        ContentMd = meta.ContentMd,
        ResolvedFilePath = resolvedFilePath,
        StorageMode = storageMode,
        Revision = meta.Revision,
        LastSyncedHash = meta.LastSyncedHash,
        UpdatedAtUnixMs = meta.UpdatedAtUnixMs
    };

    private async Task ReconcileProjectOnStartupIfPresentAsync(ProjectRecord project, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var settings = await db.BoardSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == project.Id, ct)
            .ConfigureAwait(false);
        var hasTasks = await db.BoardTasks.AsNoTracking()
            .AnyAsync(x => x.ProjectId == project.Id && !x.Deleted, ct)
            .ConfigureAwait(false);
        var hasNotesMeta = await db.NotesMeta.AsNoTracking()
            .AnyAsync(x => x.ProjectId == project.Id, ct)
            .ConfigureAwait(false);
        var defaultTinadecTasksDir = Path.Combine(project.RootPath, ".tinadec", "board", "tasks");
        var defaultTinadecNotesFile = Path.Combine(project.RootPath, ".tinadec", "project", "notes.md");

        if (settings is null && !hasTasks && !hasNotesMeta
            && !Directory.Exists(defaultTinadecTasksDir) && !File.Exists(defaultTinadecNotesFile))
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

    private void EnsureWatcher(Guid projectId, string boardTasksDir, string notesFilePath)
    {
        if (_disposed || !Directory.Exists(boardTasksDir))
            return;

        var notesDir = Path.GetDirectoryName(notesFilePath) ?? boardTasksDir;
        Directory.CreateDirectory(notesDir);

        var fullTasksDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boardTasksDir));
        var fullNotesDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(notesDir));

        if (_watchers.TryGetValue(projectId, out var existing))
        {
            if (StorageScopeInitializer.SamePath(existing.WatchedDirectory, fullTasksDir)
                && StorageScopeInitializer.SamePath(existing.WatchedNotesDirectory, fullNotesDir))
                return;
            if (_watchers.TryRemove(projectId, out var stale))
                stale.Dispose();
        }

        try
        {
            var tasksWatcher = new FileSystemWatcher(fullTasksDir, "*.md")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };

            FileSystemWatcher? notesWatcher = null;
            if (!StorageScopeInitializer.SamePath(fullTasksDir, fullNotesDir) && Directory.Exists(fullNotesDir))
            {
                notesWatcher = new FileSystemWatcher(fullNotesDir, "*.md")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };
            }

            var reg = new ProjectWatcherRegistration
            {
                ProjectId = projectId,
                WatchedDirectory = fullTasksDir,
                WatchedNotesDirectory = fullNotesDir,
                Watcher = tasksWatcher,
                NotesWatcher = notesWatcher
            };

            void OnFsEvent(object _, FileSystemEventArgs e)
            {
                var name = Path.GetFileName(e.FullPath);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase))
                    return;
                ScheduleDebouncedSync(reg);
            }

            tasksWatcher.Created += OnFsEvent;
            tasksWatcher.Changed += OnFsEvent;
            tasksWatcher.Deleted += OnFsEvent;
            tasksWatcher.Renamed += (_, e) => OnFsEvent(tasksWatcher, e);
            tasksWatcher.EnableRaisingEvents = true;

            if (notesWatcher is not null)
            {
                notesWatcher.Created += OnFsEvent;
                notesWatcher.Changed += OnFsEvent;
                notesWatcher.Deleted += OnFsEvent;
                notesWatcher.Renamed += (_, e) => OnFsEvent(notesWatcher, e);
                notesWatcher.EnableRaisingEvents = true;
            }

            if (!_watchers.TryAdd(projectId, reg))
                reg.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger?.LogDebug(ex, "Could not attach FileSystemWatcher on {Dir} / {NotesDir}.", fullTasksDir, fullNotesDir);
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
