using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;

namespace TinadecCore.Runtime.ProjectBoard;

/// <summary>
/// Event-driven Project Autopilot concurrent scheduler:
/// - Wakes on settings/autopilot updates, task creation/updates/approvals, and <c>apply-plan</c> (no busy spin);
/// - Enforces <c>active_task_count &lt; max_concurrency</c> (<c>1..8</c>, default <c>2</c>) strictly;
/// - Claims only <c>status == "ready"</c>, <c>blocked == false</c>, non-<c>human</c> tasks whose <c>depends_on</c>
///   prerequisites all exist and have <c>status == "done"</c>;
/// - Respects <see cref="IResourceLeaseService"/> mutual exclusion (leaving contenders in <c>ready</c> until leases release);
/// - Marks failed/conflicted/human-decision tasks <c>blocked = true</c> with <c>blocked_reason</c>, freeing active slots
///   while holding downstream dependents safely;
/// - Supports instant pause (<c>autopilot_enabled = false</c>) with optional <c>cancel_active</c>.
/// </summary>
public sealed class ProjectAutopilotService : IHostedService, IProjectAutopilotWakeHook, IDisposable
{
    public const int DefaultMaxConcurrency = 2;
    public const int MinConcurrency = 1;
    public const int MaxAllowedConcurrency = 8;

    private readonly ProjectBoardCoordinator _coordinator;
    private readonly ProjectTaskExecutionService _execution;
    private readonly ProjectSessionStore _sessionStore;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProjectAutopilotService>? _logger;

    private readonly Channel<Guid> _wakeChannel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly ConcurrentDictionary<Guid, byte> _queuedProjects = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _evalGates = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, InFlightEntry>> _inFlightByProject = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _projectEpochs = new();
    private readonly ConcurrentDictionary<Guid, long> _lastTickByProject = new();
    private readonly ConcurrentDictionary<Guid, string?> _lastErrorByProject = new();
    private readonly ConcurrentDictionary<Guid, byte> _leaseDeferredProjects = new();

    private readonly object _loopGate = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Task? _leaseWatchTask;
    private volatile bool _disposed;

    private sealed class InFlightEntry
    {
        public required Guid TaskId { get; init; }
        public TaskCompletionSource StartedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? ExecutionTask { get; set; }
    }

    public ProjectAutopilotService(
        ProjectBoardCoordinator coordinator,
        ProjectTaskExecutionService execution,
        ProjectSessionStore sessionStore,
        IServiceProvider services,
        TimeProvider? timeProvider = null,
        ILogger<ProjectAutopilotService>? logger = null)
    {
        _coordinator = coordinator;
        _execution = execution;
        _sessionStore = sessionStore;
        _services = services;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _coordinator.AttachAutopilotHook(this);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureBackgroundLoopRunning();

        try
        {
            await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _sessionStore.ListProjectsAsync(LifecycleStatuses.Active, cancellationToken).ConfigureAwait(false);
            foreach (var project in projects)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    var settings = await _coordinator.GetSettingsAsync(project.Id, cancellationToken).ConfigureAwait(false);
                    if (settings.AutopilotEnabled)
                    {
                        NotifyProjectChanged(project.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Skipped startup Autopilot check for project {ProjectId}.", project.Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "ProjectAutopilotService startup scan encountered an error.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        lock (_loopGate)
        {
            _loopCts?.Cancel();
            loop = _loopTask;
        }

        foreach (var cts in _projectEpochs.Values)
        {
            try { cts.Cancel(); } catch { }
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_loopGate)
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
        }

        _wakeChannel.Writer.TryComplete();

        foreach (var key in _projectEpochs.Keys.ToArray())
        {
            if (_projectEpochs.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
        }
    }

    public void NotifyProjectChanged(Guid projectId)
    {
        if (_disposed || projectId == Guid.Empty)
            return;

        EnsureBackgroundLoopRunning();
        if (_queuedProjects.TryAdd(projectId, 1))
        {
            _wakeChannel.Writer.TryWrite(projectId);
        }
    }

    public async Task<ProjectAutopilotStateDto> GetAutopilotStateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        _ = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        return await BuildAutopilotStateAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectAutopilotStateDto> UpdateAutopilotAsync(
        Guid projectId,
        UpdateProjectAutopilotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        _ = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        EnsureBackgroundLoopRunning();

        var currentSettings = await _coordinator.GetSettingsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var nextEnabled = request.AutopilotEnabled ?? currentSettings.AutopilotEnabled;
        var nextConcurrency = request.MaxConcurrency.HasValue
            ? Math.Clamp(request.MaxConcurrency.Value, MinConcurrency, MaxAllowedConcurrency)
            : Math.Clamp(currentSettings.MaxConcurrency, MinConcurrency, MaxAllowedConcurrency);

        // If pausing, immediately rotate/cancel the project epoch before updating DB so in-progress scheduler ticks stop claiming!
        if (!nextEnabled)
        {
            _leaseDeferredProjects.TryRemove(projectId, out _);
            if (request.CancelActive == true)
            {
                RotateProjectEpoch(projectId, cancelPrevious: true);
            }
        }

        _ = await _coordinator.UpdateSettingsAsync(projectId, new UpdateProjectBoardSettingsRequest
        {
            AutopilotEnabled = nextEnabled,
            MaxConcurrency = nextConcurrency
        }, cancellationToken).ConfigureAwait(false);

        if (!nextEnabled && request.CancelActive == true)
        {
            await CancelActiveTasksForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        if (nextEnabled)
        {
            _lastErrorByProject.TryRemove(projectId, out _);
            await EvaluateProjectPassAsync(projectId, waitBrieflyForDispatchesToStart: true, cancellationToken).ConfigureAwait(false);
        }

        var state = await BuildAutopilotStateAsync(projectId, cancellationToken).ConfigureAwait(false);
        await PublishAutopilotUpdatedAsync(state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    // -------------------------------------------------------------------------
    // Event-Driven Scheduler Loop & Single-Project Pass
    // -------------------------------------------------------------------------

    private void EnsureBackgroundLoopRunning()
    {
        if (_disposed) return;

        lock (_loopGate)
        {
            if (_disposed || (_loopTask is not null && !_loopTask.IsCompleted))
                return;

            _loopCts = new CancellationTokenSource();
            var ct = _loopCts.Token;
            _loopTask = Task.Run(() => RunWakeupLoopAsync(ct), CancellationToken.None);
        }
    }

    private async Task RunWakeupLoopAsync(CancellationToken ct)
    {
        var reader = _wakeChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var projectId))
                {
                    _queuedProjects.TryRemove(projectId, out _);
                    if (_disposed || ct.IsCancellationRequested)
                        return;

                    try
                    {
                        await EvaluateProjectPassAsync(projectId, waitBrieflyForDispatchesToStart: false, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Autopilot evaluation pass encountered an error for project {ProjectId}.", projectId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task EvaluateProjectPassAsync(
        Guid projectId,
        bool waitBrieflyForDispatchesToStart,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        var evalGate = _evalGates.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        var newlyLaunched = new List<InFlightEntry>();
        var stateMutated = false;

        await evalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null || project.LifecycleStatus != LifecycleStatuses.Active)
                return;

            var settings = await _coordinator.GetSettingsAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (!settings.AutopilotEnabled)
            {
                _leaseDeferredProjects.TryRemove(projectId, out _);
                return;
            }

            _lastTickByProject[projectId] = NowUnixMs();
            var maxConcurrency = Math.Clamp(settings.MaxConcurrency, MinConcurrency, MaxAllowedConcurrency);

            var rawTasks = await _coordinator.ListProjectTasksAsync(projectId, filter: null, cancellationToken).ConfigureAwait(false);
            var tasks = (await _execution.RefreshTasksRunStateAsync(projectId, rawTasks, cancellationToken).ConfigureAwait(false)).ToList();

            // 1. Flag any ready/in-progress task that encountered a Markdown sync conflict as Blocked.
            for (var i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (t.HasSyncConflict && !t.Blocked
                    && t.Status is ProjectBoardTaskStatuses.Ready or ProjectBoardTaskStatuses.InProgress)
                {
                    const string conflictReason = "Markdown sync conflict detected; resolve conflict before Autopilot continues.";
                    var blockedDto = await _coordinator.MutateTaskRuntimeStateAsync(projectId, t.TaskId, row =>
                    {
                        row.Blocked = true;
                        row.BlockedReason = conflictReason;
                    }, exportDeclarativeMarkdown: false, cancellationToken).ConfigureAwait(false);
                    if (blockedDto is not null)
                    {
                        tasks[i] = blockedDto;
                        _lastErrorByProject[projectId] = conflictReason;
                        stateMutated = true;
                    }
                }
            }

            var inFlightMap = GetInFlightMap(projectId);
            PruneCompletedInFlight(inFlightMap);

            var activeIds = CollectActiveTaskIds(tasks, inFlightMap);
            var availableSlots = maxConcurrency - activeIds.Count;
            if (availableSlots <= 0)
            {
                if (stateMutated)
                {
                    var currentState = await BuildAutopilotStateAsync(projectId, cancellationToken).ConfigureAwait(false);
                    await PublishAutopilotUpdatedAsync(currentState, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            var tasksById = tasks.ToDictionary(t => t.TaskId);
            var candidates = tasks
                .Where(t => IsEligibleReadyCandidate(t, activeIds, tasksById))
                .OrderBy(t => PriorityRank(t.Priority))
                .ThenBy(t => t.SortKey, StringComparer.Ordinal)
                .ThenBy(t => t.CreatedAtUnixMs)
                .ThenBy(t => t.TaskKey, StringComparer.Ordinal)
                .ToList();

            var sawLeaseContention = false;
            var leaseService = _services.GetService<IResourceLeaseService>();
            var epochToken = GetOrCreateProjectEpochToken(projectId);

            foreach (var candidate in candidates)
            {
                if (newlyLaunched.Count >= availableSlots)
                    break;

                // Re-verify instant pause right before claiming each candidate.
                var freshSettings = await _coordinator.GetSettingsAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (!freshSettings.AutopilotEnabled || epochToken.IsCancellationRequested)
                    break;

                // Pre-probe ResourceLease contention so contended candidates stay cleanly in `ready` without failing.
                if (leaseService is not null && await HasResourceLeaseContentionAsync(leaseService, project, candidate, cancellationToken).ConfigureAwait(false))
                {
                    sawLeaseContention = true;
                    continue;
                }

                var effectiveAssignee = ResolveAutopilotAssignee(candidate.Assignee, freshSettings.DefaultAssigneeKind);
                var entry = new InFlightEntry { TaskId = candidate.TaskId };
                if (!inFlightMap.TryAdd(candidate.TaskId, entry))
                    continue;

                activeIds.Add(candidate.TaskId);
                entry.ExecutionTask = Task.Run(
                    () => ExecuteClaimedTaskAsync(projectId, candidate, effectiveAssignee, entry, epochToken),
                    CancellationToken.None);
                newlyLaunched.Add(entry);
                stateMutated = true;
            }

            if (sawLeaseContention)
            {
                _leaseDeferredProjects[projectId] = 1;
                EnsureLeaseDeferredWatcherRunning();
            }
            else
            {
                _leaseDeferredProjects.TryRemove(projectId, out _);
            }
        }
        finally
        {
            evalGate.Release();
        }

        if (waitBrieflyForDispatchesToStart && newlyLaunched.Count > 0)
        {
            var signals = newlyLaunched.Select(e => e.StartedSignal.Task).ToArray();
            try
            {
                await Task.WhenAll(signals).WaitAsync(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        if (stateMutated)
        {
            var state = await BuildAutopilotStateAsync(projectId, cancellationToken).ConfigureAwait(false);
            await PublishAutopilotUpdatedAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteClaimedTaskAsync(
        Guid projectId,
        ProjectBoardTaskDto candidate,
        ProjectBoardAssigneeDto effectiveAssignee,
        InFlightEntry entry,
        CancellationToken epochToken)
    {
        var inFlightMap = GetInFlightMap(projectId);
        try
        {
            // Re-check Autopilot enabled immediately before invoking DispatchTaskAsync in case pause raced.
            var latestSettings = await _coordinator.GetSettingsAsync(projectId, CancellationToken.None).ConfigureAwait(false);
            if (!latestSettings.AutopilotEnabled || epochToken.IsCancellationRequested)
            {
                entry.StartedSignal.TrySetResult();
                return;
            }

            var dispatchTask = _execution.DispatchTaskAsync(
                projectId,
                candidate.TaskId,
                new DispatchProjectBoardTaskRequest
                {
                    AssigneeOverride = effectiveAssignee,
                    FailOnLeaseContention = true
                },
                epochToken);

            // Signal StartedSignal once dispatch either completes quickly or yields after marking in_progress.
            _ = SignalStartedSoonAsync(entry.StartedSignal, dispatchTask);

            var result = await dispatchTask.ConfigureAwait(false);
            entry.StartedSignal.TrySetResult();

            if (result is not null && result.Blocked)
            {
                _lastErrorByProject[projectId] = result.BlockedReason ?? "Task blocked during execution.";
            }
        }
        catch (ProjectBoardLeaseContentionException)
        {
            // Leave candidate in `ready` without failing; re-check once lease releases.
            entry.StartedSignal.TrySetResult();
            _leaseDeferredProjects[projectId] = 1;
            EnsureLeaseDeferredWatcherRunning();
        }
        catch (OperationCanceledException) when (epochToken.IsCancellationRequested || _disposed)
        {
            entry.StartedSignal.TrySetResult();
        }
        catch (Exception ex)
        {
            entry.StartedSignal.TrySetResult();
            var reason = string.IsNullOrWhiteSpace(ex.Message) ? "Autopilot task execution failed." : ex.Message;
            _lastErrorByProject[projectId] = reason;

            try
            {
                _ = await _coordinator.MutateTaskRuntimeStateAsync(projectId, candidate.TaskId, row =>
                {
                    row.Blocked = true;
                    row.BlockedReason = reason;
                    row.ActiveRunId = null;
                    row.RunStatus = "failed";
                    row.LeaseId = null;
                    row.LiveDigestSummary = reason;
                }, exportDeclarativeMarkdown: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception innerEx)
            {
                _logger?.LogDebug(innerEx, "Could not persist blocked state for failed Autopilot task {TaskId}.", candidate.TaskId);
            }
        }
        finally
        {
            inFlightMap.TryRemove(candidate.TaskId, out _);

            if (!_disposed)
            {
                try
                {
                    var state = await BuildAutopilotStateAsync(projectId, CancellationToken.None).ConfigureAwait(false);
                    await PublishAutopilotUpdatedAsync(state, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }

                // Wake scheduler so freed concurrency slot or newly unblocked tasks can be evaluated immediately.
                NotifyProjectChanged(projectId);
            }
        }
    }

    private static async Task SignalStartedSoonAsync(TaskCompletionSource signal, Task dispatchTask)
    {
        var completed = await Task.WhenAny(dispatchTask, Task.Delay(25)).ConfigureAwait(false);
        _ = completed;
        signal.TrySetResult();
    }

    private static async Task<bool> HasResourceLeaseContentionAsync(
        IResourceLeaseService leaseService,
        ProjectRecord project,
        ProjectBoardTaskDto candidate,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(candidate.WorktreePath) && Directory.Exists(candidate.WorktreePath))
        {
            var wtConflicts = await leaseService.ProbeAsync(
                new ResourceClaim(ResourceLeaseKinds.Worktree, candidate.WorktreePath, Exclusive: true),
                runId: null,
                ct).ConfigureAwait(false);
            if (wtConflicts.Any(c => c.TaskId != candidate.TaskId))
                return true;
        }

        // Also check whether an exclusive path/worktree lease currently locks the project root itself.
        var rootConflicts = await leaseService.ProbeAsync(
            new ResourceClaim(ResourceLeaseKinds.Path, project.RootPath, Exclusive: false),
            runId: null,
            ct).ConfigureAwait(false);
        return rootConflicts.Any(c => c.TaskId != candidate.TaskId);
    }

    private void EnsureLeaseDeferredWatcherRunning()
    {
        if (_disposed) return;

        lock (_loopGate)
        {
            if (_disposed || (_leaseWatchTask is not null && !_leaseWatchTask.IsCompleted))
                return;

            var ct = _loopCts?.Token ?? CancellationToken.None;
            _leaseWatchTask = Task.Run(async () =>
            {
                try
                {
                    while (!_disposed && !ct.IsCancellationRequested && !_leaseDeferredProjects.IsEmpty)
                    {
                        await Task.Delay(150, ct).ConfigureAwait(false);
                        foreach (var projectId in _leaseDeferredProjects.Keys.ToArray())
                        {
                            NotifyProjectChanged(projectId);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
        }
    }

    private async Task CancelActiveTasksForProjectAsync(Guid projectId, CancellationToken ct)
    {
        var inFlightMap = GetInFlightMap(projectId);
        var tasks = await _coordinator.ListProjectTasksAsync(projectId, filter: null, ct).ConfigureAwait(false);
        var activeIds = CollectActiveTaskIds(tasks, inFlightMap);

        foreach (var taskId in activeIds)
        {
            try
            {
                _ = await _execution.CancelTaskExecutionAsync(projectId, taskId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Failed to cancel active board task {TaskId} during Autopilot pause.", taskId);
            }
        }
    }

    private async Task<ProjectAutopilotStateDto> BuildAutopilotStateAsync(
        Guid projectId,
        CancellationToken ct)
    {
        var settings = await _coordinator.GetSettingsAsync(projectId, ct).ConfigureAwait(false);
        var rawTasks = await _coordinator.ListProjectTasksAsync(projectId, filter: null, ct).ConfigureAwait(false);
        var tasks = await _execution.RefreshTasksRunStateAsync(projectId, rawTasks, ct).ConfigureAwait(false);

        var inFlightMap = GetInFlightMap(projectId);
        PruneCompletedInFlight(inFlightMap);

        var activeIds = CollectActiveTaskIds(tasks, inFlightMap).ToList();
        var blockedTasks = tasks
            .Where(t => t.Blocked)
            .OrderByDescending(t => t.UpdatedAtUnixMs)
            .ThenBy(t => t.TaskKey, StringComparer.Ordinal)
            .ToList();

        var queuedReadyCount = tasks.Count(t =>
            t.Status == ProjectBoardTaskStatuses.Ready
            && !t.Blocked
            && !t.HasSyncConflict
            && !activeIds.Contains(t.TaskId)
            && t.Assignee?.Kind != ProjectBoardAssigneeKinds.Human);

        _lastTickByProject.TryGetValue(projectId, out var lastTick);
        _lastErrorByProject.TryGetValue(projectId, out var lastError);
        if (blockedTasks.Count == 0 && lastError is not null && settings.AutopilotEnabled)
        {
            // Clear stale error once user unblocks all tasks.
            _lastErrorByProject.TryRemove(projectId, out _);
            lastError = null;
        }

        return new ProjectAutopilotStateDto
        {
            ProjectId = projectId,
            AutopilotEnabled = settings.AutopilotEnabled,
            MaxConcurrency = Math.Clamp(settings.MaxConcurrency, MinConcurrency, MaxAllowedConcurrency),
            ActiveTaskCount = activeIds.Count,
            ActiveTaskIds = activeIds,
            QueuedReadyCount = queuedReadyCount,
            BlockedTaskCount = blockedTasks.Count,
            BlockedTasks = blockedTasks,
            LastTickAtUnixMs = lastTick > 0 ? lastTick : null,
            LastError = lastError,
            UpdatedAtUnixMs = Math.Max(settings.UpdatedAtUnixMs, lastTick)
        };
    }

    private Task PublishAutopilotUpdatedAsync(ProjectAutopilotStateDto state, CancellationToken ct) =>
        _coordinator.PublishEventAsync(new ProjectBoardEventDto
        {
            EventType = "autopilot_updated",
            ProjectId = state.ProjectId,
            Autopilot = state,
            OccurredAtUnixMs = NowUnixMs()
        }, ct);

    private static bool IsEligibleReadyCandidate(
        ProjectBoardTaskDto task,
        HashSet<Guid> activeIds,
        IReadOnlyDictionary<Guid, ProjectBoardTaskDto> tasksById)
    {
        if (task.Status != ProjectBoardTaskStatuses.Ready)
            return false;
        if (task.Blocked || task.HasSyncConflict)
            return false;
        if (activeIds.Contains(task.TaskId))
            return false;
        if (task.Assignee?.Kind == ProjectBoardAssigneeKinds.Human)
            return false;

        foreach (var depId in task.DependsOn)
        {
            if (!tasksById.TryGetValue(depId, out var depTask))
                return false;
            if (depTask.Status != ProjectBoardTaskStatuses.Done || depTask.Blocked)
                return false;
        }

        return true;
    }

    internal static ProjectBoardAssigneeDto ResolveAutopilotAssignee(
        ProjectBoardAssigneeDto? taskAssignee,
        string defaultAssigneeKind)
    {
        if (taskAssignee?.Kind == ProjectBoardAssigneeKinds.Cli)
        {
            var harnessId = string.IsNullOrWhiteSpace(taskAssignee.HarnessId)
                ? (string.IsNullOrWhiteSpace(taskAssignee.Id) ? "claude-code" : taskAssignee.Id)
                : taskAssignee.HarnessId;
            return new ProjectBoardAssigneeDto
            {
                Kind = ProjectBoardAssigneeKinds.Cli,
                Id = taskAssignee.Id ?? harnessId,
                DisplayName = taskAssignee.DisplayName,
                HarnessId = harnessId,
                ModeVersionId = taskAssignee.ModeVersionId
            };
        }

        if (taskAssignee?.Kind == ProjectBoardAssigneeKinds.Native)
        {
            return taskAssignee;
        }

        // Unassigned or `coordinator` resolves to project's `default_assignee_kind` (falling back to `native` if default is human/coordinator).
        if (string.Equals(defaultAssigneeKind, ProjectBoardAssigneeKinds.Cli, StringComparison.OrdinalIgnoreCase))
        {
            var harnessId = !string.IsNullOrWhiteSpace(taskAssignee?.HarnessId)
                ? taskAssignee!.HarnessId!
                : "claude-code";
            return new ProjectBoardAssigneeDto
            {
                Kind = ProjectBoardAssigneeKinds.Cli,
                Id = harnessId,
                HarnessId = harnessId,
                ModeVersionId = taskAssignee?.ModeVersionId
            };
        }

        return new ProjectBoardAssigneeDto
        {
            Kind = ProjectBoardAssigneeKinds.Native,
            ModeVersionId = taskAssignee?.ModeVersionId
        };
    }

    private static HashSet<Guid> CollectActiveTaskIds(
        IEnumerable<ProjectBoardTaskDto> tasks,
        ConcurrentDictionary<Guid, InFlightEntry> inFlightMap)
    {
        var set = new HashSet<Guid>(inFlightMap.Keys);
        foreach (var t in tasks)
        {
            if (t.Blocked)
                continue;
            if (t.Status == ProjectBoardTaskStatuses.InProgress
                && t.Assignee?.Kind != ProjectBoardAssigneeKinds.Human
                && t.RunStatus is not ("completed" or "failed" or "cancelled"))
            {
                set.Add(t.TaskId);
            }
        }
        return set;
    }

    private static void PruneCompletedInFlight(ConcurrentDictionary<Guid, InFlightEntry> inFlightMap)
    {
        foreach (var (taskId, entry) in inFlightMap.ToArray())
        {
            if (entry.ExecutionTask is { IsCompleted: true })
            {
                inFlightMap.TryRemove(taskId, out _);
            }
        }
    }

    private ConcurrentDictionary<Guid, InFlightEntry> GetInFlightMap(Guid projectId) =>
        _inFlightByProject.GetOrAdd(projectId, static _ => new ConcurrentDictionary<Guid, InFlightEntry>());

    private CancellationToken GetOrCreateProjectEpochToken(Guid projectId) =>
        _projectEpochs.GetOrAdd(projectId, static _ => new CancellationTokenSource()).Token;

    private void RotateProjectEpoch(Guid projectId, bool cancelPrevious)
    {
        var next = new CancellationTokenSource();
        var prev = _projectEpochs.AddOrUpdate(projectId, next, (_, old) =>
        {
            if (cancelPrevious)
            {
                try { old.Cancel(); } catch { }
            }
            return next;
        });

        if (!ReferenceEquals(prev, next) && cancelPrevious)
        {
            try { prev.Cancel(); } catch { }
        }
    }

    private static int PriorityRank(string? priority) => priority switch
    {
        ProjectBoardPriorities.Urgent => 0,
        ProjectBoardPriorities.High => 1,
        ProjectBoardPriorities.Medium => 2,
        ProjectBoardPriorities.Low => 3,
        _ => 4
    };

    private long NowUnixMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
}
