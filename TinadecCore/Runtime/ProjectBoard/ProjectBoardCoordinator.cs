using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime.SpatialBoard;

namespace TinadecCore.Runtime.ProjectBoard;

/// <summary>
/// Authoritative coordinator for single-project and cross-project task boards:
/// enforces strict DAG cycle rejection (<c>cycle_detected</c>) across <c>depends_on</c> and <c>parent_task_id</c>,
/// Base62 Fractional Indexing <c>sort_key</c> normalization, optimistic concurrency (<c>revision_conflict</c>),
/// atomic <c>KEY-N</c> sequence assignment, saved views CRUD, and real-time project/global SSE channels.
/// </summary>
public sealed class ProjectBoardCoordinator
{
    private readonly IDbContextFactory<ProjectBoardDbContext> _dbFactory;
    private readonly ProjectSessionStore _sessionStore;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProjectBoardCoordinator>? _logger;

    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaEnsured;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _projectGates = new();
    private readonly SemaphoreSlim _viewsGate = new(1, 1);
    private long _eventSeq;

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<ProjectBoardEventDto>>> _projectSubscribers = new();
    private readonly ConcurrentDictionary<Guid, Channel<ProjectBoardEventDto>> _globalSubscribers = new();

    private IProjectBoardSyncHook? _syncHook;

    public ProjectBoardCoordinator(
        IDbContextFactory<ProjectBoardDbContext> dbFactory,
        ProjectSessionStore sessionStore,
        IServiceProvider services,
        TimeProvider? timeProvider = null,
        ILogger<ProjectBoardCoordinator>? logger = null)
    {
        _dbFactory = dbFactory;
        _sessionStore = sessionStore;
        _services = services;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    internal void AttachSyncHook(IProjectBoardSyncHook hook) => _syncHook = hook;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaEnsured) return;
        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaEnsured) return;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public (string SyncRoot, string BoardTasksDir, string NotesFilePath) ResolveStoragePaths(
        ProjectRecord project,
        ProjectBoardSettingsRecord settings)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);

        string syncRoot;
        switch (settings.StorageMode)
        {
            case ProjectBoardStorageModes.AppData:
            {
                var scopeLocations = _services.GetService<IScopeStorageLocations>();
                var scopeRegistry = _services.GetService<IStorageScopeRegistry>();
                if (scopeLocations is StorageScopeDescriptor { ScopeKind: "project", External: true } externalDesc)
                {
                    syncRoot = externalDesc.Root;
                }
                else if (scopeRegistry is not null)
                {
                    syncRoot = Path.Combine(scopeRegistry.User.Root, "projects", project.Id.ToString("N"));
                }
                else if (scopeLocations is not null)
                {
                    syncRoot = Path.Combine(scopeLocations.Data, "project-boards", project.Id.ToString("N"));
                }
                else
                {
                    syncRoot = Path.Combine(AppContext.BaseDirectory, "data", "project-boards", project.Id.ToString("N"));
                }
                break;
            }

            case ProjectBoardStorageModes.CustomDir:
            {
                var custom = settings.CustomSyncRoot?.Trim();
                if (string.IsNullOrWhiteSpace(custom) || !Path.IsPathFullyQualified(custom))
                    throw new ArgumentException("custom_sync_root must be a fully-qualified local directory path when storage_mode is custom_dir.");

                var fullCustom = Path.TrimEndingDirectorySeparator(Path.GetFullPath(custom));
                var rootDrive = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullCustom) ?? string.Empty);
                if (string.Equals(fullCustom, rootDrive, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException("custom_sync_root cannot be a filesystem root directory.");

                EnsureNoForbiddenDotAgentSegment(fullCustom);
                syncRoot = fullCustom;
                break;
            }

            case ProjectBoardStorageModes.RepoTinadec:
            default:
            {
                syncRoot = Path.Combine(project.RootPath, ".tinadec");
                break;
            }
        }

        var boardTasksDir = Path.Combine(syncRoot, "board", "tasks");
        var notesFilePath = Path.Combine(syncRoot, "project", "notes.md");
        return (syncRoot, boardTasksDir, notesFilePath);
    }

    public async Task<ProjectBoardSettingsDto> GetSettingsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = GetProjectGate(projectId);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            return ToSettingsDto(project, settings);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProjectBoardSettingsDto> UpdateSettingsAsync(
        Guid projectId,
        UpdateProjectBoardSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = GetProjectGate(projectId);

        ProjectBoardSettingsDto dto;
        bool storageChanged;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var prevMode = settings.StorageMode;
            var prevCustom = settings.CustomSyncRoot;

            if (request.KeyPrefix is not null)
            {
                var normalizedPrefix = NormalizeKeyPrefix(request.KeyPrefix);
                settings.KeyPrefix = normalizedPrefix;
            }

            if (request.StorageMode is not null)
            {
                var mode = request.StorageMode.Trim().ToLowerInvariant();
                if (!ProjectBoardStorageModes.IsValid(mode))
                    throw new ArgumentException("storage_mode must be one of: repo_tinadec, app_data, custom_dir.");
                settings.StorageMode = mode;
            }

            if (request.HasCustomSyncRoot)
            {
                settings.CustomSyncRoot = string.IsNullOrWhiteSpace(request.CustomSyncRoot)
                    ? null
                    : Path.TrimEndingDirectorySeparator(Path.GetFullPath(ValidateCustomRootInput(request.CustomSyncRoot)));
            }

            if (settings.StorageMode == ProjectBoardStorageModes.CustomDir && string.IsNullOrWhiteSpace(settings.CustomSyncRoot))
            {
                throw new ArgumentException("custom_sync_root is required when storage_mode is custom_dir.");
            }

            // Validate resolved storage paths before committing.
            _ = ResolveStoragePaths(project, settings);

            if (request.ProjectInstructions is not null)
            {
                settings.ProjectInstructions = request.ProjectInstructions;
            }

            if (request.DefaultAssigneeKind is not null)
            {
                var kind = request.DefaultAssigneeKind.Trim().ToLowerInvariant();
                if (!ProjectBoardAssigneeKinds.IsValid(kind))
                    throw new ArgumentException("default_assignee_kind must be one of: human, native, cli, coordinator.");
                settings.DefaultAssigneeKind = kind;
            }

            if (request.MaxConcurrency.HasValue)
            {
                if (request.MaxConcurrency.Value is < 1 or > 32)
                    throw new ArgumentException("max_concurrency must be between 1 and 32.");
                settings.MaxConcurrency = request.MaxConcurrency.Value;
            }

            if (request.AutopilotEnabled.HasValue)
            {
                settings.AutopilotEnabled = request.AutopilotEnabled.Value;
            }

            settings.UpdatedAtUnixMs = NowUnixMs();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            storageChanged = !string.Equals(prevMode, settings.StorageMode, StringComparison.Ordinal)
                || !string.Equals(prevCustom, settings.CustomSyncRoot, StringComparison.Ordinal);
            dto = ToSettingsDto(project, settings);
        }
        finally
        {
            gate.Release();
        }

        if (storageChanged && GetSyncHook() is { } syncHook)
        {
            await syncHook.OnStorageSettingsChangedAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        await PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "settings_updated",
            ProjectId = projectId,
            Settings = dto,
            OccurredAtUnixMs = dto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return dto;
    }

    public sealed class TaskQueryFilter
    {
        public IReadOnlyCollection<Guid>? ProjectIds { get; init; }
        public IReadOnlyCollection<string>? Statuses { get; init; }
        public IReadOnlyCollection<string>? Priorities { get; init; }
        public IReadOnlyCollection<string>? AssigneeKinds { get; init; }
        public IReadOnlyCollection<string>? Labels { get; init; }
        public bool? NeedsMyAction { get; init; }
        public bool? HasConflict { get; init; }
        public string? Query { get; init; }
    }

    public async Task<IReadOnlyList<ProjectBoardTaskDto>> ListProjectTasksAsync(
        Guid projectId,
        TaskQueryFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var taskRows = await db.BoardTasks.AsNoTracking()
            .Where(x => x.ProjectId == projectId && !x.Deleted)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var syncMap = await db.BoardSyncStates.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .ToDictionaryAsync(x => x.TaskId, cancellationToken)
            .ConfigureAwait(false);

        var dtos = new List<ProjectBoardTaskDto>(taskRows.Count);
        foreach (var row in taskRows
                     .OrderBy(r => r.SortKey, StringComparer.Ordinal)
                     .ThenBy(r => r.CreatedAtUnixMs)
                     .ThenBy(r => r.TaskKey, StringComparer.Ordinal))
        {
            syncMap.TryGetValue(row.TaskId, out var syncRow);
            var dto = ToTaskDto(row, project.Name, syncRow);
            if (MatchesFilter(dto, filter))
                dtos.Add(dto);
        }

        return dtos;
    }

    public async Task<ProjectBoardTaskDto?> GetTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.LifecycleStatus != LifecycleStatuses.Active)
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.BoardTasks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId && !x.Deleted, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
            return null;

        var syncRow = await db.BoardSyncStates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId, cancellationToken)
            .ConfigureAwait(false);

        return ToTaskDto(row, project.Name, syncRow);
    }

    public async Task<ProjectBoardTaskDto> CreateTaskAsync(
        Guid projectId,
        CreateProjectBoardTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = request.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title) || title.Length > 512)
            throw new ArgumentException("Task title is required and must be at most 512 characters.");

        var status = NormalizeStatus(request.Status ?? ProjectBoardTaskStatuses.Backlog);
        var priority = NormalizePriority(request.Priority ?? ProjectBoardPriorities.None);
        var assignee = NormalizeAssignee(request.Assignee);
        var checklist = NormalizeChecklist(request.Checklist);
        var labels = NormalizeLabels(request.Labels);
        var dependsOn = NormalizeGuidList(request.DependsOn);
        var spatialRef = NormalizeSpatialRef(request.SpatialRef);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = GetProjectGate(projectId);

        ProjectBoardTaskDto createdDto;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var existingRows = await db.BoardTasks
                .Where(x => x.ProjectId == projectId && !x.Deleted)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var taskId = Guid.NewGuid();
            var graphMap = BuildGraphMap(existingRows);
            ValidateNoCyclesOrThrow(taskId, request.ParentTaskId, dependsOn, graphMap, requireExistingReferences: true);

            var taskKey = AllocateNextTaskKey(settings, existingRows);
            var sortKey = ProjectBoardFractionalIndex.Normalize(
                request.SortKey,
                existingRows.Where(r => r.Status == status).Select(r => r.SortKey));

            var nowMs = NowUnixMs();
            var blocked = request.Blocked ?? false;
            var blockedReason = string.IsNullOrWhiteSpace(request.BlockedReason) ? null : request.BlockedReason.Trim();

            var record = new ProjectBoardTaskRecord
            {
                TaskId = taskId,
                ProjectId = projectId,
                TaskKey = taskKey,
                Title = title,
                DescriptionMd = ProjectBoardMarkdownCodec.NormalizeLineEndings(request.DescriptionMd ?? string.Empty).Trim(),
                ChecklistJson = JsonSerializer.Serialize(checklist, ProjectBoardMarkdownCodec.CanonicalJson),
                Status = status,
                Priority = priority,
                Blocked = blocked,
                BlockedReason = blockedReason,
                AssigneeJson = assignee is null ? null : JsonSerializer.Serialize(assignee, ProjectBoardMarkdownCodec.CanonicalJson),
                ParentTaskId = request.ParentTaskId,
                DependsOnJson = JsonSerializer.Serialize(dependsOn, ProjectBoardMarkdownCodec.CanonicalJson),
                LabelsJson = JsonSerializer.Serialize(labels, ProjectBoardMarkdownCodec.CanonicalJson),
                SpatialRefJson = spatialRef is null ? null : JsonSerializer.Serialize(spatialRef, ProjectBoardMarkdownCodec.CanonicalJson),
                SortKey = sortKey,
                Revision = 1,
                CreatedAtUnixMs = nowMs,
                UpdatedAtUnixMs = nowMs,
                Deleted = false
            };

            db.BoardTasks.Add(record);
            settings.UpdatedAtUnixMs = nowMs;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            createdDto = ToTaskDto(record, project.Name, syncRow: null);
        }
        finally
        {
            gate.Release();
        }

        if (GetSyncHook() is { } syncHook)
        {
            await syncHook.ExportTaskToMarkdownAsync(projectId, createdDto.TaskId, cancellationToken).ConfigureAwait(false);
            createdDto = (await GetTaskAsync(projectId, createdDto.TaskId, cancellationToken).ConfigureAwait(false)) ?? createdDto;
        }

        await PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "task_upserted",
            ProjectId = projectId,
            TaskId = createdDto.TaskId,
            Task = createdDto,
            OccurredAtUnixMs = createdDto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return createdDto;
    }

    public async Task<ProjectBoardTaskDto?> UpdateTaskAsync(
        Guid projectId,
        Guid taskId,
        UpdateProjectBoardTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = GetProjectGate(projectId);

        ProjectBoardTaskDto? updatedDto;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existingRows = await db.BoardTasks
                .Where(x => x.ProjectId == projectId && !x.Deleted)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var record = existingRows.FirstOrDefault(x => x.TaskId == taskId);
            if (record is null)
                return null;

            if (request.ExpectedRevision.HasValue && request.ExpectedRevision.Value != record.Revision)
            {
                throw new ProjectBoardRevisionConflictException(
                    record.Revision,
                    $"Expected revision {request.ExpectedRevision.Value}, but current task revision is {record.Revision}.");
            }

            var nextTitle = request.Title is not null ? request.Title.Trim() : record.Title;
            if (string.IsNullOrEmpty(nextTitle) || nextTitle.Length > 512)
                throw new ArgumentException("Task title cannot be empty and must be at most 512 characters.");

            var nextStatus = request.Status is not null ? NormalizeStatus(request.Status) : record.Status;
            var nextPriority = request.Priority is not null ? NormalizePriority(request.Priority) : record.Priority;
            var nextParent = request.HasParentTaskId ? request.ParentTaskId : record.ParentTaskId;
            var nextDependsOn = request.DependsOn is not null
                ? NormalizeGuidList(request.DependsOn)
                : ProjectBoardMarkdownCodec.DeserializeList<Guid>(record.DependsOnJson);

            var graphMap = BuildGraphMap(existingRows);
            ValidateNoCyclesOrThrow(taskId, nextParent, nextDependsOn, graphMap, requireExistingReferences: true);

            record.Title = nextTitle;
            if (request.DescriptionMd is not null)
                record.DescriptionMd = ProjectBoardMarkdownCodec.NormalizeLineEndings(request.DescriptionMd).Trim();
            if (request.Checklist is not null)
                record.ChecklistJson = JsonSerializer.Serialize(NormalizeChecklist(request.Checklist), ProjectBoardMarkdownCodec.CanonicalJson);

            record.Status = nextStatus;
            record.Priority = nextPriority;

            if (request.Blocked.HasValue)
                record.Blocked = request.Blocked.Value;
            if (request.HasBlockedReason)
                record.BlockedReason = string.IsNullOrWhiteSpace(request.BlockedReason) ? null : request.BlockedReason.Trim();

            if (request.HasAssignee)
            {
                var normalizedAssignee = NormalizeAssignee(request.Assignee);
                record.AssigneeJson = normalizedAssignee is null
                    ? null
                    : JsonSerializer.Serialize(normalizedAssignee, ProjectBoardMarkdownCodec.CanonicalJson);
            }

            record.ParentTaskId = nextParent;
            record.DependsOnJson = JsonSerializer.Serialize(nextDependsOn, ProjectBoardMarkdownCodec.CanonicalJson);

            if (request.Labels is not null)
                record.LabelsJson = JsonSerializer.Serialize(NormalizeLabels(request.Labels), ProjectBoardMarkdownCodec.CanonicalJson);

            if (request.HasSpatialRef)
            {
                var normalizedSpatial = NormalizeSpatialRef(request.SpatialRef);
                record.SpatialRefJson = normalizedSpatial is null
                    ? null
                    : JsonSerializer.Serialize(normalizedSpatial, ProjectBoardMarkdownCodec.CanonicalJson);
            }

            if (request.SortKey is not null)
            {
                record.SortKey = ProjectBoardFractionalIndex.Normalize(
                    request.SortKey,
                    existingRows.Where(r => r.TaskId != taskId && r.Status == nextStatus).Select(r => r.SortKey));
            }

            var nowMs = NowUnixMs();
            record.Revision += 1;
            record.UpdatedAtUnixMs = nowMs;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var syncRow = await db.BoardSyncStates.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId, cancellationToken)
                .ConfigureAwait(false);
            updatedDto = ToTaskDto(record, project.Name, syncRow);
        }
        finally
        {
            gate.Release();
        }

        if (GetSyncHook() is { } syncHook)
        {
            await syncHook.ExportTaskToMarkdownAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
            updatedDto = (await GetTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false)) ?? updatedDto;
        }

        await PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "task_upserted",
            ProjectId = projectId,
            TaskId = taskId,
            Task = updatedDto,
            OccurredAtUnixMs = updatedDto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return updatedDto;
    }

    public async Task<bool> DeleteTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        _ = await RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var gate = GetProjectGate(projectId);

        string? taskKey = null;
        long nowMs;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.BoardTasks
                .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId && !x.Deleted, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
                return false;

            nowMs = NowUnixMs();
            taskKey = record.TaskKey;
            record.Deleted = true;
            record.Revision += 1;
            record.UpdatedAtUnixMs = nowMs;

            // Clean dangling references from sibling tasks in the same project.
            var siblings = await db.BoardTasks
                .Where(x => x.ProjectId == projectId && !x.Deleted && x.TaskId != taskId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var sib in siblings)
            {
                var touched = false;
                if (sib.ParentTaskId == taskId)
                {
                    sib.ParentTaskId = null;
                    touched = true;
                }

                var deps = ProjectBoardMarkdownCodec.DeserializeList<Guid>(sib.DependsOnJson);
                if (deps.Remove(taskId))
                {
                    sib.DependsOnJson = JsonSerializer.Serialize(deps, ProjectBoardMarkdownCodec.CanonicalJson);
                    touched = true;
                }

                if (touched)
                {
                    sib.Revision += 1;
                    sib.UpdatedAtUnixMs = nowMs;
                }
            }

            var syncState = await db.BoardSyncStates
                .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId, cancellationToken)
                .ConfigureAwait(false);
            if (syncState is not null)
                db.BoardSyncStates.Remove(syncState);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        if (taskKey is not null && GetSyncHook() is { } syncHook)
        {
            await syncHook.DeleteTaskMarkdownAsync(projectId, taskKey, cancellationToken).ConfigureAwait(false);
        }

        await PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "task_deleted",
            ProjectId = projectId,
            TaskId = taskId,
            OccurredAtUnixMs = nowMs
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<PromoteFromSpatialResultDto> PromoteFromSpatialAsync(
        Guid projectId,
        PromoteFromSpatialRequest request,
        SpatialBoardCoordinator? spatialCoordinatorOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty)
            throw new ArgumentException("session_id is required.");

        var requestedIds = (request.ObjectIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requestedIds.Count == 0)
            throw new ArgumentException("object_ids must contain at least one spatial object id.");

        var mode = request.Mode is null
            ? SpatialPromotionModes.SplitMany
            : request.Mode.Trim().ToLowerInvariant();
        if (!SpatialPromotionModes.IsValid(mode))
            throw new ArgumentException("mode must be one of: merge_single, split_many, parent_with_children.");

        var targetStatus = request.TargetStatus is null
            ? ProjectBoardTaskStatuses.Ready
            : request.TargetStatus.Trim().ToLowerInvariant();
        if (!ProjectBoardTaskStatuses.IsValid(targetStatus) || targetStatus == ProjectBoardTaskStatuses.Done)
            throw new ArgumentException("target_status must be a valid non-terminal board status (backlog, ready, in_progress, or in_review).");

        var priority = request.Priority is null
            ? ProjectBoardPriorities.None
            : NormalizePriority(request.Priority);
        var assignee = NormalizeAssignee(request.Assignee);
        var labels = NormalizeLabels(request.Labels);
        JsonElement? viewportHint = request.ViewportHint is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } vh
            ? vh.Clone()
            : null;
        var writerId = !string.IsNullOrWhiteSpace(request.WriterId)
            ? request.WriterId.Trim()
            : "board_promoter";

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        var spatialCoordinator = spatialCoordinatorOverride ?? _services.GetRequiredService<SpatialBoardCoordinator>();
        var snapshot = await spatialCoordinator.GetSnapshotAsync(request.SessionId, includeDeleted: false, cancellationToken).ConfigureAwait(false);

        var selectedObjects = new List<SpatialBoardObjectDto>(requestedIds.Count);
        foreach (var id in requestedIds)
        {
            if (!snapshot.Objects.TryGetValue(id, out var obj) || obj.Deleted)
                throw new KeyNotFoundException($"Spatial object '{id}' was not found on session '{request.SessionId}'.");
            selectedObjects.Add(obj);
        }

        var gate = GetProjectGate(projectId);
        ProjectBoardSpatialPromoter.PromotionPlan plan;
        var createdDtos = new List<ProjectBoardTaskDto>();
        var updatedLinks = new List<SpatialObjectTaskLinkDto>();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var existingRows = await db.BoardTasks
                .Where(x => x.ProjectId == projectId && !x.Deleted)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var existingGraph = BuildGraphMap(existingRows);
            plan = ProjectBoardSpatialPromoter.BuildPlan(mode, selectedObjects, snapshot, existingGraph);

            var combinedGraph = new Dictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)>(existingGraph);
            foreach (var planned in plan.Tasks)
                combinedGraph[planned.TaskId] = (planned.ParentTaskId, [.. planned.DependsOn]);

            foreach (var planned in plan.Tasks)
            {
                ValidateNoCyclesOrThrow(
                    planned.TaskId,
                    planned.ParentTaskId,
                    planned.DependsOn,
                    combinedGraph,
                    requireExistingReferences: true);
            }

            var allRows = new List<ProjectBoardTaskRecord>(existingRows);
            var statusSortKeys = existingRows
                .Where(r => r.Status == targetStatus)
                .Select(r => r.SortKey)
                .ToList();
            var seenLinkedObjects = new HashSet<string>(StringComparer.Ordinal);
            var nowMs = NowUnixMs();

            foreach (var planned in plan.Tasks)
            {
                var taskKey = AllocateNextTaskKey(settings, allRows);
                var sortKey = ProjectBoardFractionalIndex.Normalize(null, statusSortKeys);
                statusSortKeys.Add(sortKey);

                var normalizedChecklist = NormalizeChecklist(planned.Checklist);
                var spatialRef = NormalizeSpatialRef(new ProjectBoardSpatialRefDto
                {
                    SessionId = request.SessionId,
                    ObjectId = planned.PrimaryObjectId,
                    ViewportHint = viewportHint
                });

                var record = new ProjectBoardTaskRecord
                {
                    TaskId = planned.TaskId,
                    ProjectId = projectId,
                    TaskKey = taskKey,
                    Title = planned.Title,
                    DescriptionMd = ProjectBoardMarkdownCodec.NormalizeLineEndings(planned.DescriptionMd).Trim(),
                    ChecklistJson = JsonSerializer.Serialize(normalizedChecklist, ProjectBoardMarkdownCodec.CanonicalJson),
                    Status = targetStatus,
                    Priority = priority,
                    Blocked = false,
                    BlockedReason = null,
                    AssigneeJson = assignee is null ? null : JsonSerializer.Serialize(assignee, ProjectBoardMarkdownCodec.CanonicalJson),
                    ParentTaskId = planned.ParentTaskId,
                    DependsOnJson = JsonSerializer.Serialize(planned.DependsOn, ProjectBoardMarkdownCodec.CanonicalJson),
                    LabelsJson = JsonSerializer.Serialize(labels, ProjectBoardMarkdownCodec.CanonicalJson),
                    SpatialRefJson = spatialRef is null ? null : JsonSerializer.Serialize(spatialRef, ProjectBoardMarkdownCodec.CanonicalJson),
                    SortKey = sortKey,
                    Revision = 1,
                    CreatedAtUnixMs = nowMs,
                    UpdatedAtUnixMs = nowMs,
                    Deleted = false
                };

                db.BoardTasks.Add(record);
                allRows.Add(record);
                createdDtos.Add(ToTaskDto(record, project.Name, syncRow: null));

                foreach (var linkedObjId in planned.LinkedObjectIds)
                {
                    if (seenLinkedObjects.Add(linkedObjId))
                    {
                        updatedLinks.Add(new SpatialObjectTaskLinkDto
                        {
                            ObjectId = linkedObjId,
                            TaskId = record.TaskId,
                            TaskKey = record.TaskKey
                        });
                    }
                }
            }

            settings.UpdatedAtUnixMs = nowMs;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        var syncHook = GetSyncHook();
        var finalDtos = new List<ProjectBoardTaskDto>(createdDtos.Count);
        foreach (var dto in createdDtos)
        {
            var current = dto;
            if (syncHook is not null)
            {
                await syncHook.ExportTaskToMarkdownAsync(projectId, dto.TaskId, cancellationToken).ConfigureAwait(false);
                current = (await GetTaskAsync(projectId, dto.TaskId, cancellationToken).ConfigureAwait(false)) ?? dto;
            }

            finalDtos.Add(current);

            await PublishEventAsync(new ProjectBoardEventDto
            {
                EventType = "task_upserted",
                ProjectId = projectId,
                TaskId = current.TaskId,
                Task = current,
                OccurredAtUnixMs = current.UpdatedAtUnixMs
            }, cancellationToken).ConfigureAwait(false);
        }

        var latestSnapshot = await spatialCoordinator
            .GetSnapshotAsync(request.SessionId, includeDeleted: true, cancellationToken)
            .ConfigureAwait(false);

        var spatialOps = new List<SpatialPropertyOpDto>(updatedLinks.Count);
        foreach (var link in updatedLinks)
        {
            long maxLamport = 0;
            if (latestSnapshot.Objects.TryGetValue(link.ObjectId, out var spatialObj))
            {
                if (spatialObj.DeletedTs is { } delTs && delTs.Lamport > maxLamport)
                    maxLamport = delTs.Lamport;
                foreach (var ts in spatialObj.PropTimestamps.Values)
                {
                    if (ts.Lamport > maxLamport)
                        maxLamport = ts.Lamport;
                }
            }

            var nextTs = new SpatialLwwTimestampDto(maxLamport + 1, writerId);
            spatialOps.Add(new SpatialPropertyOpDto
            {
                ClientOpId = $"promote-{link.ObjectId}-{link.TaskKey}",
                ObjectId = link.ObjectId,
                Props = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["board_task_id"] = JsonSerializer.SerializeToElement(link.TaskId.ToString("D")),
                    ["board_task_key"] = JsonSerializer.SerializeToElement(link.TaskKey)
                },
                Timestamps = new Dictionary<string, SpatialLwwTimestampDto>(StringComparer.Ordinal)
                {
                    ["props.board_task_id"] = nextTs,
                    ["props.board_task_key"] = nextTs
                }
            });
        }

        var opsResult = await spatialCoordinator.ApplyOpsAsync(
            request.SessionId,
            new SpatialBoardOpsRequestDto
            {
                ClientId = writerId,
                BaseSeq = latestSnapshot.Seq,
                Ops = spatialOps
            },
            senderConnectionId: null,
            cancellationToken).ConfigureAwait(false);

        return new PromoteFromSpatialResultDto
        {
            Mode = mode,
            ParentTaskId = plan.ParentTaskId,
            Tasks = finalDtos,
            UpdatedObjectLinks = updatedLinks,
            SpatialSeq = opsResult.Seq
        };
    }

    internal async Task<ProjectBoardTaskDto?> MutateTaskRuntimeStateAsync(
        Guid projectId,
        Guid taskId,
        Action<ProjectBoardTaskRecord> mutate,
        bool exportDeclarativeMarkdown,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null) return null;

        var gate = GetProjectGate(projectId);
        ProjectBoardTaskDto? updatedDto;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.BoardTasks
                .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId && !x.Deleted, cancellationToken)
                .ConfigureAwait(false);
            if (record is null) return null;

            var beforeSnapshot = ProjectBoardMarkdownCodec.ToSnapshot(record);
            mutate(record);
            var afterSnapshot = ProjectBoardMarkdownCodec.ToSnapshot(record);

            var nowMs = NowUnixMs();
            if (!ProjectBoardMarkdownCodec.DeclarativeEquals(beforeSnapshot, afterSnapshot))
            {
                record.Revision += 1;
            }
            record.UpdatedAtUnixMs = nowMs;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var syncRow = await db.BoardSyncStates.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TaskId == taskId, cancellationToken)
                .ConfigureAwait(false);
            updatedDto = ToTaskDto(record, project.Name, syncRow);
        }
        finally
        {
            gate.Release();
        }

        if (exportDeclarativeMarkdown && GetSyncHook() is { } syncHook)
        {
            await syncHook.ExportTaskToMarkdownAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
            updatedDto = (await GetTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false)) ?? updatedDto;
        }

        await PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "task_upserted",
            ProjectId = projectId,
            TaskId = taskId,
            Task = updatedDto,
            OccurredAtUnixMs = updatedDto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return updatedDto;
    }

    internal SemaphoreSlim GetProjectGate(Guid projectId) =>
        _projectGates.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));

    internal async Task<ProjectRecord> RequireActiveProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.LifecycleStatus != LifecycleStatuses.Active)
            throw new KeyNotFoundException($"Project '{projectId}' was not found.");
        return project;
    }

    internal async Task<ProjectBoardSettingsRecord> GetOrCreateSettingsUnderLockAsync(
        ProjectBoardDbContext db,
        ProjectRecord project,
        CancellationToken cancellationToken)
    {
        var existing = await db.BoardSettings
            .FirstOrDefaultAsync(x => x.ProjectId == project.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var created = new ProjectBoardSettingsRecord
        {
            ProjectId = project.Id,
            KeyPrefix = DeriveDefaultPrefix(project.Name),
            NextSequence = 1,
            StorageMode = ProjectBoardStorageModes.RepoTinadec,
            CustomSyncRoot = null,
            ProjectInstructions = string.Empty,
            DefaultAssigneeKind = ProjectBoardAssigneeKinds.Native,
            MaxConcurrency = 2,
            AutopilotEnabled = false,
            UpdatedAtUnixMs = NowUnixMs()
        };
        db.BoardSettings.Add(created);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    // -------------------------------------------------------------------------
    // Saved Views CRUD (global & project scoped)
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<ProjectBoardViewDto>> ListViewsAsync(
        string? scope = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = db.BoardViews.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var normScope = scope.Trim().ToLowerInvariant();
            query = query.Where(x => x.Scope == normScope);
        }

        if (projectId.HasValue)
        {
            query = query.Where(x => x.Scope == "global" || x.ProjectId == projectId.Value);
        }

        var rows = await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAtUnixMs)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToViewDto).ToList();
    }

    public async Task<ProjectBoardViewDto> CreateViewAsync(
        CreateProjectBoardViewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name) || name.Length > 256)
            throw new ArgumentException("View name is required and must be at most 256 characters.");

        var scope = string.IsNullOrWhiteSpace(request.Scope)
            ? (request.ProjectId.HasValue ? "project" : "global")
            : request.Scope.Trim().ToLowerInvariant();
        if (scope is not ("global" or "project"))
            throw new ArgumentException("View scope must be 'global' or 'project'.");

        if (scope == "project" && (!request.ProjectId.HasValue || request.ProjectId.Value == Guid.Empty))
            throw new ArgumentException("project_id is required when scope is 'project'.");

        var config = NormalizeViewConfig(request.Config);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        ProjectBoardViewDto dto;

        await _viewsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var nowMs = NowUnixMs();
            var nextOrder = request.SortOrder ?? ((await db.BoardViews.MaxAsync(x => (int?)x.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1);

            var row = new ProjectBoardViewRecord
            {
                ViewId = Guid.NewGuid(),
                Scope = scope,
                ProjectId = scope == "project" ? request.ProjectId : null,
                Name = name,
                ConfigJson = JsonSerializer.Serialize(config, ProjectBoardMarkdownCodec.CanonicalJson),
                SortOrder = nextOrder,
                CreatedAtUnixMs = nowMs,
                UpdatedAtUnixMs = nowMs
            };

            db.BoardViews.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            dto = ToViewDto(row);
        }
        finally
        {
            _viewsGate.Release();
        }

        await PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "view_upserted",
            ProjectId = dto.ProjectId,
            ViewId = dto.ViewId,
            View = dto,
            OccurredAtUnixMs = dto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return dto;
    }

    public async Task<ProjectBoardViewDto?> UpdateViewAsync(
        Guid viewId,
        UpdateProjectBoardViewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        ProjectBoardViewDto? dto;
        await _viewsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.BoardViews
                .FirstOrDefaultAsync(x => x.ViewId == viewId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
                return null;

            if (request.Name is not null)
            {
                var trimmed = request.Name.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 256)
                    throw new ArgumentException("View name cannot be empty and must be at most 256 characters.");
                row.Name = trimmed;
            }

            if (request.Config is not null)
            {
                var normalized = NormalizeViewConfig(request.Config);
                row.ConfigJson = JsonSerializer.Serialize(normalized, ProjectBoardMarkdownCodec.CanonicalJson);
            }

            if (request.SortOrder.HasValue)
                row.SortOrder = request.SortOrder.Value;

            row.UpdatedAtUnixMs = NowUnixMs();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            dto = ToViewDto(row);
        }
        finally
        {
            _viewsGate.Release();
        }

        await PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "view_upserted",
            ProjectId = dto.ProjectId,
            ViewId = dto.ViewId,
            View = dto,
            OccurredAtUnixMs = dto.UpdatedAtUnixMs
        }, cancellationToken).ConfigureAwait(false);

        return dto;
    }

    public async Task<bool> DeleteViewAsync(Guid viewId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        Guid? projectId = null;

        await _viewsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.BoardViews
                .FirstOrDefaultAsync(x => x.ViewId == viewId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
                return false;

            projectId = row.ProjectId;
            db.BoardViews.Remove(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _viewsGate.Release();
        }

        await PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "view_deleted",
            ProjectId = projectId,
            ViewId = viewId,
            OccurredAtUnixMs = NowUnixMs()
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    // -------------------------------------------------------------------------
    // Summary Builder
    // -------------------------------------------------------------------------

    public ProjectBoardSummaryDto BuildSummary(
        IEnumerable<(Guid ProjectId, string ProjectName)> activeProjects,
        IReadOnlyCollection<ProjectBoardTaskDto> allTasks)
    {
        var tasksByProject = allTasks.GroupBy(t => t.ProjectId).ToDictionary(g => g.Key, g => g.ToList());
        var projectSummaries = new List<ProjectBoardProjectSummaryItemDto>();

        foreach (var (projId, projName) in activeProjects)
        {
            var pTasks = tasksByProject.TryGetValue(projId, out var list) ? list : [];
            projectSummaries.Add(new ProjectBoardProjectSummaryItemDto
            {
                ProjectId = projId,
                ProjectName = projName,
                TotalTasks = pTasks.Count,
                InProgressCount = pTasks.Count(t => t.Status == ProjectBoardTaskStatuses.InProgress),
                InReviewCount = pTasks.Count(t => t.Status == ProjectBoardTaskStatuses.InReview),
                BlockedCount = pTasks.Count(t => t.Blocked),
                ConflictCount = pTasks.Count(t => t.HasSyncConflict),
                NeedsActionCount = pTasks.Count(NeedsUserAction)
            });
        }

        return new ProjectBoardSummaryDto
        {
            TotalTasks = allTasks.Count,
            BacklogCount = allTasks.Count(t => t.Status == ProjectBoardTaskStatuses.Backlog),
            ReadyCount = allTasks.Count(t => t.Status == ProjectBoardTaskStatuses.Ready),
            InProgressCount = allTasks.Count(t => t.Status == ProjectBoardTaskStatuses.InProgress),
            InReviewCount = allTasks.Count(t => t.Status == ProjectBoardTaskStatuses.InReview),
            DoneCount = allTasks.Count(t => t.Status == ProjectBoardTaskStatuses.Done),
            BlockedCount = allTasks.Count(t => t.Blocked),
            ConflictCount = allTasks.Count(t => t.HasSyncConflict),
            ActiveRunCount = allTasks.Count(t => t.ActiveRunId.HasValue && IsActiveRunStatus(t.RunStatus)),
            NeedsActionCount = allTasks.Count(NeedsUserAction),
            Projects = projectSummaries,
            UpdatedAtUnixMs = NowUnixMs()
        };
    }

    public static bool NeedsUserAction(ProjectBoardTaskDto task) =>
        task.Status == ProjectBoardTaskStatuses.InReview
        || task.Blocked
        || task.HasSyncConflict
        || task.RunStatus is "awaiting_approval" or "awaiting_user" or "awaiting_delegate";

    private static bool IsActiveRunStatus(string? runStatus) =>
        runStatus is not (null or "completed" or "failed" or "cancelled");

    // -------------------------------------------------------------------------
    // SSE Subscription & Broadcast
    // -------------------------------------------------------------------------

    public IDisposable SubscribeProjectEvents(Guid projectId, out ChannelReader<ProjectBoardEventDto> reader)
    {
        var subId = Guid.NewGuid();
        var channel = Channel.CreateBounded<ProjectBoardEventDto>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var map = _projectSubscribers.GetOrAdd(projectId, static _ => new ConcurrentDictionary<Guid, Channel<ProjectBoardEventDto>>());
        map[subId] = channel;
        reader = channel.Reader;
        return new SubscriptionHandle(() =>
        {
            if (map.TryRemove(subId, out var removed))
                removed.Writer.TryComplete();
        });
    }

    public IDisposable SubscribeGlobalEvents(out ChannelReader<ProjectBoardEventDto> reader)
    {
        var subId = Guid.NewGuid();
        var channel = Channel.CreateBounded<ProjectBoardEventDto>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _globalSubscribers[subId] = channel;
        reader = channel.Reader;
        return new SubscriptionHandle(() =>
        {
            if (_globalSubscribers.TryRemove(subId, out var removed))
                removed.Writer.TryComplete();
        });
    }

    public async Task PublishEventAsync(ProjectBoardEventDto evt, CancellationToken cancellationToken = default)
    {
        var stamped = new ProjectBoardEventDto
        {
            Seq = Interlocked.Increment(ref _eventSeq),
            EventType = evt.EventType,
            ProjectId = evt.ProjectId,
            TaskId = evt.TaskId,
            Task = evt.Task,
            Settings = evt.Settings,
            ViewId = evt.ViewId,
            View = evt.View,
            OccurredAtUnixMs = evt.OccurredAtUnixMs > 0 ? evt.OccurredAtUnixMs : NowUnixMs()
        };

        PublishLocal(stamped);

        // When running inside a mounted project storage scope, also forward to the host ("user")
        // coordinator so cross-project GET /api/v1/board/events subscribers receive live updates.
        var scopeLocations = _services.GetService<IScopeStorageLocations>();
        var scopeRegistry = _services.GetService<IStorageScopeRegistry>();
        if (scopeRegistry is not null && scopeLocations is not null && scopeLocations.StorageId != "user")
        {
            try
            {
                await using var userLease = await scopeRegistry.AcquireAsync("user", cancellationToken).ConfigureAwait(false);
                var hostCoordinator = userLease.Services.GetService<ProjectBoardCoordinator>();
                if (hostCoordinator is not null && !ReferenceEquals(hostCoordinator, this))
                {
                    hostCoordinator.PublishFromChildScope(stamped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Skipped forwarding project board event to host scope.");
            }
        }
    }

    internal void PublishFromChildScope(ProjectBoardEventDto evt)
    {
        var stamped = new ProjectBoardEventDto
        {
            Seq = Interlocked.Increment(ref _eventSeq),
            EventType = evt.EventType,
            ProjectId = evt.ProjectId,
            TaskId = evt.TaskId,
            Task = evt.Task,
            Settings = evt.Settings,
            ViewId = evt.ViewId,
            View = evt.View,
            OccurredAtUnixMs = evt.OccurredAtUnixMs
        };
        PublishLocal(stamped);
    }

    private void PublishLocal(ProjectBoardEventDto stamped)
    {
        if (stamped.ProjectId is { } projectId && _projectSubscribers.TryGetValue(projectId, out var map))
        {
            foreach (var ch in map.Values)
            {
                ch.Writer.TryWrite(stamped);
            }
        }

        foreach (var ch in _globalSubscribers.Values)
        {
            ch.Writer.TryWrite(stamped);
        }
    }

    private sealed class SubscriptionHandle(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    // -------------------------------------------------------------------------
    // Strict DAG Validation & Normalization Helpers
    // -------------------------------------------------------------------------

    internal static Dictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)> BuildGraphMap(IEnumerable<ProjectBoardTaskRecord> rows)
    {
        var map = new Dictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)>();
        foreach (var row in rows)
        {
            if (row.Deleted) continue;
            map[row.TaskId] = (row.ParentTaskId, ProjectBoardMarkdownCodec.DeserializeList<Guid>(row.DependsOnJson));
        }
        return map;
    }

    /// <summary>
    /// Enforces strict DAG acyclicity across <c>parent_task_id</c> and <c>depends_on</c>.
    /// Throws <see cref="ProjectBoardCycleException"/> (<c>cycle_detected</c>) on any direct, transitive, or cross-hierarchy cycle.
    /// </summary>
    public static void ValidateNoCyclesOrThrow(
        Guid taskId,
        Guid? candidateParentId,
        IReadOnlyList<Guid> candidateDependsOn,
        IReadOnlyDictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)> existingTasks,
        bool requireExistingReferences)
    {
        if (candidateParentId.HasValue && candidateParentId.Value == taskId)
            throw new ProjectBoardCycleException($"Task '{taskId}' cannot set parent_task_id to itself.");

        if (candidateDependsOn.Contains(taskId))
            throw new ProjectBoardCycleException($"Task '{taskId}' cannot depend on itself.");

        if (requireExistingReferences)
        {
            if (candidateParentId.HasValue && !existingTasks.ContainsKey(candidateParentId.Value))
                throw new ArgumentException($"parent_task_id '{candidateParentId.Value}' does not reference an active task in this project.");

            foreach (var depId in candidateDependsOn)
            {
                if (!existingTasks.ContainsKey(depId))
                    throw new ArgumentException($"depends_on task '{depId}' does not reference an active task in this project.");
            }
        }

        // Check parent_task_id ancestor chain and forbid depending on one's own ancestor.
        var ancestors = new HashSet<Guid> { taskId };
        var parentCursor = candidateParentId;
        while (parentCursor.HasValue)
        {
            if (!ancestors.Add(parentCursor.Value))
                throw new ProjectBoardCycleException($"Setting parent_task_id '{candidateParentId}' on task '{taskId}' creates a parent cycle.");

            parentCursor = existingTasks.TryGetValue(parentCursor.Value, out var parentNode)
                ? parentNode.ParentId
                : null;
        }

        foreach (var depId in candidateDependsOn)
        {
            if (ancestors.Contains(depId))
                throw new ProjectBoardCycleException($"Task '{taskId}' cannot depend on its own parent/ancestor task '{depId}'.");
        }

        // Full directed reachability check across unified (parent_task_id U depends_on) edges:
        // if taskId is reachable from any outgoing edge of taskId, a cycle exists.
        var visited = new HashSet<Guid>();
        var stack = new Stack<Guid>();

        if (candidateParentId.HasValue)
            stack.Push(candidateParentId.Value);
        foreach (var depId in candidateDependsOn)
            stack.Push(depId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == taskId)
                throw new ProjectBoardCycleException($"Updating task '{taskId}' introduces a dependency/hierarchy cycle.");

            if (!visited.Add(current))
                continue;

            if (!existingTasks.TryGetValue(current, out var node))
                continue;

            if (node.ParentId.HasValue)
                stack.Push(node.ParentId.Value);

            foreach (var nextDep in node.DependsOn)
                stack.Push(nextDep);
        }
    }

    internal static string AllocateNextTaskKey(ProjectBoardSettingsRecord settings, IReadOnlyCollection<ProjectBoardTaskRecord> existingRows)
    {
        var existingKeys = existingRows
            .Select(r => r.TaskKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seq = Math.Max(1L, settings.NextSequence);
        while (true)
        {
            var candidate = $"{settings.KeyPrefix}-{seq}";
            seq++;
            if (!existingKeys.Contains(candidate))
            {
                settings.NextSequence = seq;
                return candidate;
            }
        }
    }

    internal static void BumpSequenceFromImportedKey(ProjectBoardSettingsRecord settings, string taskKey)
    {
        var prefixWithDash = settings.KeyPrefix + "-";
        if (taskKey.StartsWith(prefixWithDash, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(taskKey[prefixWithDash.Length..], out var num)
            && num >= settings.NextSequence)
        {
            settings.NextSequence = num + 1;
        }
    }

    internal ProjectBoardSettingsDto ToSettingsDto(ProjectRecord project, ProjectBoardSettingsRecord settings)
    {
        var (_, boardTasksDir, _) = ResolveStoragePaths(project, settings);
        return new ProjectBoardSettingsDto
        {
            ProjectId = project.Id,
            KeyPrefix = settings.KeyPrefix,
            NextSequence = settings.NextSequence,
            StorageMode = settings.StorageMode,
            CustomSyncRoot = settings.CustomSyncRoot,
            ResolvedBoardDir = boardTasksDir,
            ProjectInstructions = settings.ProjectInstructions,
            DefaultAssigneeKind = settings.DefaultAssigneeKind,
            MaxConcurrency = settings.MaxConcurrency,
            AutopilotEnabled = settings.AutopilotEnabled,
            UpdatedAtUnixMs = settings.UpdatedAtUnixMs
        };
    }

    internal static ProjectBoardTaskDto ToTaskDto(
        ProjectBoardTaskRecord row,
        string projectName,
        ProjectBoardSyncStateRecord? syncRow)
    {
        ProjectBoardSyncConflictDto? syncConflict = null;
        var hasConflict = syncRow?.HasConflict == true;
        if (hasConflict && syncRow is not null)
        {
            var fileSnap = ProjectBoardMarkdownCodec.DeserializeNullable<ProjectBoardTaskSnapshotDto>(syncRow.ConflictFileSnapshotJson)
                ?? ProjectBoardMarkdownCodec.ToSnapshot(row);
            var boardSnap = ProjectBoardMarkdownCodec.DeserializeNullable<ProjectBoardTaskSnapshotDto>(syncRow.ConflictBoardSnapshotJson)
                ?? ProjectBoardMarkdownCodec.ToSnapshot(row);
            var fields = ProjectBoardMarkdownCodec.DeserializeList<string>(syncRow.ConflictingFieldsJson);

            syncConflict = new ProjectBoardSyncConflictDto
            {
                FileRelPath = syncRow.FileRelPath,
                DetectedAtUnixMs = syncRow.ConflictDetectedAtUnixMs ?? row.UpdatedAtUnixMs,
                FileSnapshot = fileSnap,
                BoardSnapshot = boardSnap,
                ConflictingFields = fields
            };
        }

        return new ProjectBoardTaskDto
        {
            TaskId = row.TaskId,
            ProjectId = row.ProjectId,
            ProjectName = projectName,
            TaskKey = row.TaskKey,
            Title = row.Title,
            DescriptionMd = row.DescriptionMd,
            Checklist = ProjectBoardMarkdownCodec.DeserializeList<ProjectBoardChecklistItemDto>(row.ChecklistJson),
            Status = row.Status,
            Priority = row.Priority,
            Blocked = row.Blocked,
            BlockedReason = row.BlockedReason,
            Assignee = ProjectBoardMarkdownCodec.DeserializeNullable<ProjectBoardAssigneeDto>(row.AssigneeJson),
            ParentTaskId = row.ParentTaskId,
            DependsOn = ProjectBoardMarkdownCodec.DeserializeList<Guid>(row.DependsOnJson),
            Labels = ProjectBoardMarkdownCodec.DeserializeList<string>(row.LabelsJson),
            SpatialRef = ProjectBoardMarkdownCodec.DeserializeNullable<ProjectBoardSpatialRefDto>(row.SpatialRefJson),
            SortKey = row.SortKey,
            ActiveSessionId = row.ActiveSessionId,
            ActiveRunId = row.ActiveRunId,
            RunStatus = row.RunStatus,
            WorktreePath = row.WorktreePath,
            LeaseId = row.LeaseId,
            LiveDigestSummary = row.LiveDigestSummary,
            DiffStats = ProjectBoardMarkdownCodec.DeserializeNullable<ProjectBoardDiffStatsDto>(row.DiffStatsJson),
            HasSyncConflict = hasConflict,
            SyncConflict = syncConflict,
            Revision = row.Revision,
            CreatedAtUnixMs = row.CreatedAtUnixMs,
            UpdatedAtUnixMs = row.UpdatedAtUnixMs
        };
    }

    internal static ProjectBoardViewDto ToViewDto(ProjectBoardViewRecord row) => new()
    {
        ViewId = row.ViewId,
        Scope = row.Scope,
        ProjectId = row.ProjectId,
        Name = row.Name,
        Config = ProjectBoardMarkdownCodec.DeserializeNullable<ProjectBoardViewConfigDto>(row.ConfigJson) ?? new ProjectBoardViewConfigDto(),
        SortOrder = row.SortOrder,
        CreatedAtUnixMs = row.CreatedAtUnixMs,
        UpdatedAtUnixMs = row.UpdatedAtUnixMs
    };

    public static bool MatchesFilter(ProjectBoardTaskDto task, TaskQueryFilter? filter)
    {
        if (filter is null) return true;

        if (filter.ProjectIds is { Count: > 0 } && !filter.ProjectIds.Contains(task.ProjectId))
            return false;

        if (filter.Statuses is { Count: > 0 } && !filter.Statuses.Contains(task.Status, StringComparer.OrdinalIgnoreCase))
            return false;

        if (filter.Priorities is { Count: > 0 } && !filter.Priorities.Contains(task.Priority, StringComparer.OrdinalIgnoreCase))
            return false;

        if (filter.AssigneeKinds is { Count: > 0 })
        {
            var kind = task.Assignee?.Kind ?? "unassigned";
            if (!filter.AssigneeKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        if (filter.Labels is { Count: > 0 } && !task.Labels.Any(l => filter.Labels.Contains(l, StringComparer.OrdinalIgnoreCase)))
            return false;

        if (filter.NeedsMyAction == true && !NeedsUserAction(task))
            return false;

        if (filter.HasConflict.HasValue && task.HasSyncConflict != filter.HasConflict.Value)
            return false;

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            var hit = task.TaskKey.Contains(q, StringComparison.OrdinalIgnoreCase)
                || task.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || task.DescriptionMd.Contains(q, StringComparison.OrdinalIgnoreCase)
                || task.Labels.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (!hit) return false;
        }

        return true;
    }

    internal long NowUnixMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private IProjectBoardSyncHook? GetSyncHook() =>
        _syncHook ?? _services.GetService<IProjectBoardSyncHook>();

    private static string NormalizeStatus(string raw)
    {
        var norm = raw.Trim().ToLowerInvariant();
        if (!ProjectBoardTaskStatuses.IsValid(norm))
            throw new ArgumentException($"Invalid task status '{raw}'. Must be backlog, ready, in_progress, in_review, or done.");
        return norm;
    }

    private static string NormalizePriority(string raw)
    {
        var norm = raw.Trim().ToLowerInvariant();
        if (!ProjectBoardPriorities.IsValid(norm))
            throw new ArgumentException($"Invalid task priority '{raw}'. Must be urgent, high, medium, low, or none.");
        return norm;
    }

    internal static ProjectBoardAssigneeDto? NormalizeAssignee(ProjectBoardAssigneeDto? assignee)
    {
        if (assignee is null) return null;
        var kind = assignee.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!ProjectBoardAssigneeKinds.IsValid(kind))
            throw new ArgumentException($"Invalid assignee kind '{assignee.Kind}'. Must be human, native, cli, or coordinator.");

        if (kind == ProjectBoardAssigneeKinds.Cli && !string.IsNullOrWhiteSpace(assignee.HarnessId))
        {
            if (HarnessCatalog.Find(assignee.HarnessId) is null)
                throw new ArgumentException($"Unknown CLI harness '{assignee.HarnessId}'.");
        }

        return new ProjectBoardAssigneeDto
        {
            Kind = kind,
            Id = string.IsNullOrWhiteSpace(assignee.Id) ? null : assignee.Id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(assignee.DisplayName) ? null : assignee.DisplayName.Trim(),
            ModeVersionId = assignee.ModeVersionId == Guid.Empty ? null : assignee.ModeVersionId,
            HarnessId = string.IsNullOrWhiteSpace(assignee.HarnessId) ? null : assignee.HarnessId.Trim()
        };
    }

    private static List<ProjectBoardChecklistItemDto> NormalizeChecklist(List<ProjectBoardChecklistItemDto>? items)
    {
        if (items is null || items.Count == 0) return [];
        var list = new List<ProjectBoardChecklistItemDto>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var idx = 0;
        foreach (var item in items)
        {
            var text = item.Text?.Trim() ?? string.Empty;
            if (text.Length == 0) continue;
            idx++;
            var id = string.IsNullOrWhiteSpace(item.Id) ? $"chk-{idx}" : item.Id.Trim();
            while (!seen.Add(id))
            {
                idx++;
                id = $"chk-{idx}";
            }
            list.Add(new ProjectBoardChecklistItemDto { Id = id, Text = text, Done = item.Done });
        }
        return list;
    }

    private static List<string> NormalizeLabels(List<string>? labels) =>
        labels is null
            ? []
            : labels
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(32)
                .ToList();

    private static List<Guid> NormalizeGuidList(List<Guid>? ids) =>
        ids is null
            ? []
            : ids.Where(id => id != Guid.Empty).Distinct().ToList();

    private static ProjectBoardSpatialRefDto? NormalizeSpatialRef(ProjectBoardSpatialRefDto? spatialRef)
    {
        if (spatialRef is null) return null;
        if (spatialRef.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(spatialRef.ObjectId))
            throw new ArgumentException("spatial_ref requires a valid session_id and non-empty object_id.");

        JsonElement? viewportHint = spatialRef.ViewportHint is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } vh
            ? vh.Clone()
            : null;

        return new ProjectBoardSpatialRefDto
        {
            SessionId = spatialRef.SessionId,
            ObjectId = spatialRef.ObjectId.Trim(),
            ViewportHint = viewportHint
        };
    }

    private static ProjectBoardViewConfigDto NormalizeViewConfig(ProjectBoardViewConfigDto? config)
    {
        config ??= new ProjectBoardViewConfigDto();
        var layout = config.Layout?.Trim().ToLowerInvariant() ?? "kanban";
        if (layout is not ("kanban" or "list" or "swimlane"))
            throw new ArgumentException("view config.layout must be kanban, list, or swimlane.");

        var groupBy = config.GroupBy?.Trim().ToLowerInvariant() ?? "status";
        if (groupBy is not ("status" or "project" or "assignee" or "priority" or "none"))
            throw new ArgumentException("view config.group_by must be status, project, assignee, priority, or none.");

        string? swimlaneBy = string.IsNullOrWhiteSpace(config.SwimlaneBy) ? null : config.SwimlaneBy.Trim().ToLowerInvariant();
        if (swimlaneBy is not (null or "project" or "assignee"))
            throw new ArgumentException("view config.swimlane_by must be project, assignee, or null.");

        var sortBy = config.SortBy?.Trim().ToLowerInvariant() ?? "manual";
        if (sortBy is not ("manual" or "priority" or "updated_at" or "dependency"))
            throw new ArgumentException("view config.sort_by must be manual, priority, updated_at, or dependency.");

        return new ProjectBoardViewConfigDto
        {
            Layout = layout,
            GroupBy = groupBy,
            SwimlaneBy = swimlaneBy,
            SortBy = sortBy,
            Filters = config.Filters ?? new ProjectBoardViewFiltersDto(),
            VisibleFields = config.VisibleFields?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? []
        };
    }

    private static string NormalizeKeyPrefix(string raw)
    {
        var cleaned = new string(raw.Trim().ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        if (cleaned.Length is < 2 or > 10 || !char.IsAsciiLetter(cleaned[0]))
            throw new ArgumentException("key_prefix must be 2–10 ASCII letters/digits starting with a letter.");
        return cleaned;
    }

    private static string DeriveDefaultPrefix(string projectName)
    {
        var letters = new StringBuilder();
        foreach (var ch in projectName ?? string.Empty)
        {
            if (char.IsAsciiLetter(ch))
            {
                letters.Append(char.ToUpperInvariant(ch));
                if (letters.Length == 3) break;
            }
        }

        if (letters.Length >= 2)
            return letters.ToString();

        return "TIN";
    }

    private static string ValidateCustomRootInput(string raw)
    {
        var trimmed = raw.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            throw new ArgumentException("custom_sync_root must be an absolute path.");
        EnsureNoForbiddenDotAgentSegment(trimmed);
        return trimmed;
    }

    private static void EnsureNoForbiddenDotAgentSegment(string path)
    {
        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(seg => string.Equals(seg, ".agent", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("'.agent' directories are forbidden; use '.tinadec' or a standard sync folder.");
    }
}

internal interface IProjectBoardSyncHook
{
    Task ExportTaskToMarkdownAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default);
    Task DeleteTaskMarkdownAsync(Guid projectId, string taskKey, CancellationToken cancellationToken = default);
    Task OnStorageSettingsChangedAsync(Guid projectId, CancellationToken cancellationToken = default);
}
