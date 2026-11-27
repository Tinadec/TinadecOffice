using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.DmaEA;

internal sealed partial class FullDuplexRunEngine
{
    private const int MaxTeammateTranscriptSummaryChars = 2400;
    private const int MaxTeammateOutcomeSummaryChars = 1200;

    private readonly ConcurrentDictionary<(Guid RunId, string Handle), ScopedTeamMcpLease> _runTeammateMcpLeases = new();

    private enum ActorOutcomeKind
    {
        Completed,
        ParkedOnWait,
        AwaitingExternalDecision,
        PausedByHuman
    }

    private sealed record ActorCompletion(
        Guid TaskId,
        ActorOutcomeKind Outcome,
        TaskExecutionResult? Result = null,
        long ParkedAtWakeVersion = 0);

    internal sealed record LiveMailboxMessage(
        long Seq,
        string Sender,
        string Kind,
        string Content,
        bool Urgent,
        DateTimeOffset CreatedAt);

    internal sealed class TeammateActorMailbox
    {
        public ConcurrentQueue<LiveMailboxMessage> UrgentInbox { get; } = new();
        public ConcurrentQueue<LiveMailboxMessage> NormalInbox { get; } = new();
        private volatile bool _pauseRequested;

        public bool IsPauseRequested => _pauseRequested;
        public void RequestPause() => _pauseRequested = true;
        public void ClearPause() => _pauseRequested = false;

        public void Enqueue(LiveMailboxMessage message)
        {
            if (message.Urgent)
                UrgentInbox.Enqueue(message);
            else
                NormalInbox.Enqueue(message);
        }

        public List<LiveMailboxMessage> DrainAll()
        {
            var drained = new List<LiveMailboxMessage>();
            while (UrgentInbox.TryDequeue(out var urgent))
                drained.Add(urgent);
            while (NormalInbox.TryDequeue(out var normal))
                drained.Add(normal);
            return drained;
        }
    }

    internal sealed class RunTeammateActorHub(Guid runId)
    {
        private volatile TaskCompletionSource<bool> _wakePulse = CreatePulse();
        private long _wakeVersion;

        public Guid RunId { get; } = runId;
        public SemaphoreSlim CheckpointGate { get; } = new(1, 1);
        public ConcurrentDictionary<string, TeammateActorMailbox> Mailboxes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FullDuplexCheckpointV1? LiveCheckpoint { get; set; }
        public FrozenRunConfigurationV1? LiveConfiguration { get; set; }
        public RunState? LiveRun { get; set; }
        public RunLeaseEpoch? LiveLeaseEpoch { get; set; }

        public long WakeVersion => Interlocked.Read(ref _wakeVersion);

        public TeammateActorMailbox GetMailbox(string handle) =>
            Mailboxes.GetOrAdd(handle.Trim().TrimStart('@'), _ => new TeammateActorMailbox());

        public void SignalWake()
        {
            Interlocked.Increment(ref _wakeVersion);
            var previous = Interlocked.Exchange(ref _wakePulse, CreatePulse());
            previous.TrySetResult(true);
        }

        public async Task WaitForWakeAsync(long observedVersion, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            if (WakeVersion != observedVersion)
                return;
            var pulseTask = _wakePulse.Task;
            if (WakeVersion != observedVersion)
                return;
            var delayTask = Task.Delay(maxWait, cancellationToken);
            await Task.WhenAny(pulseTask, delayTask).ConfigureAwait(false);
        }

        private static TaskCompletionSource<bool> CreatePulse() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class CheckpointGateHolder
    {
        public Guid? HeldRunId;
        public int Depth;
    }

    private readonly struct CheckpointGateScope(CheckpointGateHolder? holder, SemaphoreSlim? gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (holder is null)
                return ValueTask.CompletedTask;

            holder.Depth--;
            if (holder.Depth <= 0)
            {
                holder.Depth = 0;
                holder.HeldRunId = null;
                gate?.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    private RunTeammateActorHub GetOrCreateActorHub(Guid runId) =>
        _runActorHubs.GetOrAdd(runId, static id => new RunTeammateActorHub(id));

    private ValueTask<CheckpointGateScope> EnterCheckpointGateAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Synchronous outer method ensures the holder instance is installed in the caller's ExecutionContext!
        var holder = _checkpointGateHolder.Value;
        if (holder is null)
        {
            holder = new CheckpointGateHolder();
            _checkpointGateHolder.Value = holder;
        }
        if (holder.HeldRunId == runId && holder.Depth > 0)
        {
            holder.Depth++;
            return ValueTask.FromResult(new CheckpointGateScope(holder, null));
        }
        return AcquireCheckpointGateSlowAsync(GetOrCreateActorHub(runId).CheckpointGate, runId, holder, cancellationToken);
    }

    private static async ValueTask<CheckpointGateScope> AcquireCheckpointGateSlowAsync(
        SemaphoreSlim gate,
        Guid runId,
        CheckpointGateHolder holder,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        holder.HeldRunId = runId;
        holder.Depth = 1;
        return new CheckpointGateScope(holder, gate);
    }

    internal static bool IsTaskPausedByTeammate(FullDuplexCheckpointV1 checkpoint, DurableTaskNode task)
    {
        if (string.Equals(task.Status, "paused", StringComparison.OrdinalIgnoreCase))
            return true;
        var handle = task.TeammateHandle ?? task.WorkerHandle;
        if (string.IsNullOrWhiteSpace(handle))
            return false;
        var tm = checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, handle, StringComparison.OrdinalIgnoreCase));
        return tm?.Paused == true;
    }

    private async Task<FullDuplexCheckpointV1> ExecuteReadyTasksAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);

        var plannerId = checkpoint.PlannerAgentId ?? throw new InvalidDataException("Planner instance is missing from checkpoint.");

        // Space preparation stays isolated before planning begins.
        var spacePrepTask = checkpoint.Tasks.FirstOrDefault(t => t.IsSpacePreparation && !IsTaskTerminal(t));
        if (spacePrepTask is not null)
        {
            if (spacePrepTask.Status != "waiting" && !HasLivePendingExecution(spacePrepTask))
            {
                spacePrepTask.Attempt++;
                spacePrepTask.Status = "running";
                spacePrepTask.InputContextRevision = checkpoint.ContextRevision;
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "space-prep-dispatched", cancellationToken).ConfigureAwait(false);
            }

            var prepWorker = await AssignWorkerAsync(run, configuration, checkpoint, plannerId, spacePrepTask, cancellationToken).ConfigureAwait(false);
            if (prepWorker is not null)
            {
                var prepTools = await GetWorkerToolsAsync(run, configuration, spacePrepTask, prepWorker, cancellationToken).ConfigureAwait(false);
                var prepResult = await ExecuteToolTaskAsync(run, configuration, checkpoint, plannerId, spacePrepTask, cancellationToken, prepTools).ConfigureAwait(false);
                checkpoint = prepResult.Checkpoint;
                if (prepResult.Waiting)
                {
                    var pendingTurn = spacePrepTask.ToolTurns.LastOrDefault(t => t.ExecutionId == spacePrepTask.PendingToolExecutionId);
                    var waitStatus = pendingTurn?.DispatchStatus switch
                    {
                        ToolDispatchStatus.AwaitingDelegate => "awaiting_delegate",
                        ToolDispatchStatus.AwaitingUser => "awaiting_user",
                        _ => "awaiting_approval"
                    };
                    await TrySetRunStatusAsync(run.RunId, waitStatus, null, cancellationToken).ConfigureAwait(false);
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken).ConfigureAwait(false);
                    throw new RunAwaitingExternalDecisionException();
                }
                if (prepResult.Result is not null)
                {
                    await ApplyTaskResultAsync(run, configuration, runId, checkpoint, prepResult.Result, cancellationToken).ConfigureAwait(false);
                }
            }

            if (checkpoint.Tasks.All(IsTaskTerminal)
                && configuration.SpaceOptions?.WorkflowModeVersionId is not null
                && checkpoint.SpacePreparationComplete
                && checkpoint.Tasks.Any(item => item.IsSpacePreparation))
            {
                checkpoint.Tasks.Clear();
                checkpoint.PlanRevision = 0;
                checkpoint.Phase = "planning";
                return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "space-prepared", cancellationToken).ConfigureAwait(false);
            }
        }

        // True asynchronous Actor scheduling loop (#50):
        // - Zero Task.WhenAll batch barrier; any finished actor settles immediately and wakes waiters/dependents.
        // - Resumed tasks (ToolTurns.Count > 0, PendingToolExecutionId != null) and senior teammates with team_spawn
        //   all execute concurrently in the shared actor pool governed by MaxParallelWorkers & MaxTeammateRelayDepth.
        // - A single teammate hitting awaiting_approval suspends ONLY its own actor; sibling teammates continue at full speed.
        var hub = GetOrCreateActorHub(runId);
        hub.LiveRun = run;
        hub.LiveConfiguration = configuration;
        hub.LiveCheckpoint = checkpoint;

        var maxParallel = Math.Max(1, configuration.Spawn.MaxParallelWorkers);
        using var concurrencySemaphore = new SemaphoreSlim(maxParallel, maxParallel);
        using var actorScopeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeActors = new Dictionary<Guid, Task<ActorCompletion>>();
        var parkedOnExternalDecision = new Dictionary<Guid, long>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await HeartbeatAsync(runId, cancellationToken).ConfigureAwait(false);
                var liveRunState = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
                if (liveRunState.Status is RunStatus.Cancelled or RunStatus.Paused || IsTerminal(liveRunState.Status))
                {
                    return checkpoint;
                }

                var observedWakeVersion = hub.WakeVersion;
                var tasksToLaunch = new List<DurableTaskNode>();

                await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                {
                    if (await DrainLiveMcpBoardPostsAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false))
                    {
                        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "mcp-board-drained", cancellationToken).ConfigureAwait(false);
                    }

                    if (await ApplyPendingTaskDispatchesAsync(run, checkpoint, cancellationToken).ConfigureAwait(false))
                    {
                        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-dispatched", cancellationToken).ConfigureAwait(false);
                    }

                    MarkBlockedDescendants(checkpoint.Tasks);

                    var freeSlots = concurrencySemaphore.CurrentCount;
                    if (freeSlots > 0)
                    {
                        foreach (var candidate in checkpoint.Tasks)
                        {
                            if (tasksToLaunch.Count >= freeSlots) break;
                            if (activeActors.ContainsKey(candidate.TaskId)) continue;
                            if (IsTaskTerminal(candidate)) continue;
                            if (IsTaskPausedByTeammate(checkpoint, candidate)) continue;

                            if (HasLivePendingExecution(candidate))
                            {
                                if (parkedOnExternalDecision.TryGetValue(candidate.TaskId, out var parkedWake)
                                    && observedWakeVersion <= parkedWake)
                                {
                                    continue;
                                }
                                parkedOnExternalDecision.Remove(candidate.TaskId);
                                tasksToLaunch.Add(candidate);
                                continue;
                            }

                            if (IsDispatchable(candidate, checkpoint)
                                && candidate.Dependencies.All(dependency =>
                                    checkpoint.Tasks.Any(other => other.TaskKey == dependency && other.Status == "completed")))
                            {
                                tasksToLaunch.Add(candidate);
                            }
                        }

                        if (tasksToLaunch.Count > 0)
                        {
                            foreach (var task in tasksToLaunch)
                            {
                                if (!HasLivePendingExecution(task) && task.Status != "waiting")
                                {
                                    task.Attempt++;
                                    task.InputContextRevision = checkpoint.ContextRevision;
                                }
                                else if (!HasLivePendingExecution(task))
                                {
                                    task.InputContextRevision = checkpoint.ContextRevision;
                                }
                                task.Status = "running";

                                var handle = task.TeammateHandle ?? task.WorkerHandle;
                                if (!string.IsNullOrWhiteSpace(handle))
                                {
                                    hub.GetMailbox(handle).ClearPause();
                                    var tm = checkpoint.Teammates.FirstOrDefault(t =>
                                        string.Equals(t.Handle, handle, StringComparison.OrdinalIgnoreCase));
                                    if (tm is not null && !tm.Paused)
                                    {
                                        tm.Status = "working";
                                        tm.CurrentTaskId = task.TaskId;
                                        tm.UpdatedAt = DateTimeOffset.UtcNow;
                                    }
                                }
                            }
                            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-dispatched", cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                foreach (var task in tasksToLaunch)
                {
                    await concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var captured = task;
                    activeActors[captured.TaskId] = LaunchSingleTeammateActorTask(
                        run, configuration, checkpoint, plannerId, captured, hub, concurrencySemaphore, actorScopeCts.Token);
                }

                if (activeActors.Count == 0)
                {
                    bool anyNewlyReady;
                    await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                    {
                        anyNewlyReady = checkpoint.Tasks.Any(candidate =>
                            !IsTaskTerminal(candidate)
                            && !IsTaskPausedByTeammate(checkpoint, candidate)
                            && ((HasLivePendingExecution(candidate)
                                    && (!parkedOnExternalDecision.TryGetValue(candidate.TaskId, out var pv) || hub.WakeVersion > pv))
                                || (!HasLivePendingExecution(candidate)
                                    && IsDispatchable(candidate, checkpoint)
                                    && candidate.Dependencies.All(dep =>
                                        checkpoint.Tasks.Any(other => other.TaskKey == dep && other.Status == "completed")))));
                    }
                    if (anyNewlyReady) continue;

                    if (string.Equals(checkpoint.Phase, "awaiting_user", StringComparison.Ordinal) || checkpoint.AwaitingApprovalExpiryReview)
                    {
                        throw new RunAwaitingExternalDecisionException();
                    }

                    if (checkpoint.Tasks.All(IsTaskTerminal))
                    {
                        if (configuration.SpaceOptions?.WorkflowModeVersionId is not null
                            && checkpoint.SpacePreparationComplete
                            && checkpoint.Tasks.Any(item => item.IsSpacePreparation))
                        {
                            checkpoint.Tasks.Clear();
                            checkpoint.PlanRevision = 0;
                            checkpoint.Phase = "planning";
                            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "space-prepared", cancellationToken).ConfigureAwait(false);
                        }
                        checkpoint.Phase = "reviewing";
                        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "execution-complete", cancellationToken).ConfigureAwait(false);
                    }

                    var awaitingToolTask = checkpoint.Tasks.FirstOrDefault(item => HasLivePendingExecution(item) && !IsTaskPausedByTeammate(checkpoint, item));
                    if (awaitingToolTask is not null)
                    {
                        var pendingTurn = awaitingToolTask.ToolTurns.LastOrDefault(t => t.ExecutionId == awaitingToolTask.PendingToolExecutionId);
                        var waitRunStatus = pendingTurn?.DispatchStatus switch
                        {
                            ToolDispatchStatus.AwaitingDelegate => "awaiting_delegate",
                            ToolDispatchStatus.AwaitingUser => "awaiting_user",
                            _ => "awaiting_approval"
                        };
                        await TrySetRunStatusAsync(run.RunId, waitRunStatus, $"Awaiting decision for '{awaitingToolTask.TaskKey}'.", cancellationToken).ConfigureAwait(false);
                        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken).ConfigureAwait(false);
                        throw new RunAwaitingExternalDecisionException();
                    }

                    if (checkpoint.Tasks.Any(item => !IsTaskTerminal(item) && IsTaskPausedByTeammate(checkpoint, item)))
                    {
                        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "teammate-paused-idle", cancellationToken).ConfigureAwait(false);
                        throw new RunAwaitingExternalDecisionException();
                    }

                    var waitingTasks = checkpoint.Tasks.Where(item => item.Status == "waiting").ToList();
                    if (waitingTasks.Count > 0)
                    {
                        var deadlines = waitingTasks
                            .Where(item => item.TeamWaitDeadline.HasValue)
                            .Select(item => item.TeamWaitDeadline!.Value)
                            .ToArray();
                        if (deadlines.Length > 0)
                        {
                            var earliest = deadlines.Min();
                            ScheduleModelRetryInMemory(runId, earliest);
                            throw new RunAwaitingExternalDecisionException();
                        }
                        if (_runTeammateMcpLeases.Keys.Any(k => k.RunId == runId))
                        {
                            throw new RunAwaitingExternalDecisionException();
                        }
                    }

                    await FailRunAsync(runId, checkpoint, "invalid_task_graph", "No dependency-ready task remains in the persisted graph.", cancellationToken).ConfigureAwait(false);
                    return checkpoint;
                }

                var wakeSignalTask = hub.WaitForWakeAsync(observedWakeVersion, TimeSpan.FromMilliseconds(200), cancellationToken);
                var waitSet = new List<Task>(activeActors.Count + 1) { wakeSignalTask };
                waitSet.AddRange(activeActors.Values);
                await Task.WhenAny(waitSet).ConfigureAwait(false);

                var completedIds = activeActors.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToArray();
                foreach (var finishedId in completedIds)
                {
                    var finishedActorTask = activeActors[finishedId];
                    activeActors.Remove(finishedId);
                    var completion = await finishedActorTask.ConfigureAwait(false);

                    await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                    {
                        if (completion.Outcome == ActorOutcomeKind.AwaitingExternalDecision)
                        {
                            parkedOnExternalDecision[finishedId] = completion.ParkedAtWakeVersion;
                            var taskNode = checkpoint.Tasks.FirstOrDefault(t => t.TaskId == finishedId);
                            if (taskNode is not null)
                            {
                                var tm = FindBoundTeammate(checkpoint, taskNode);
                                if (tm is not null && !tm.Paused)
                                {
                                    tm.Status = "awaiting_approval";
                                    tm.UpdatedAt = DateTimeOffset.UtcNow;
                                }
                            }
                            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "actor-awaiting-decision", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (completion.Outcome == ActorOutcomeKind.PausedByHuman)
                        {
                            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "actor-paused", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (completion.Outcome == ActorOutcomeKind.ParkedOnWait)
                        {
                            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "actor-waiting", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (completion.Result is not null)
                        {
                            var contextChanged = await ApplyPendingContextPatchesAsync(run, checkpoint, cancellationToken).ConfigureAwait(false);
                            if (!contextChanged)
                            {
                                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, completion.Result, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                var staleTask = checkpoint.Tasks.FirstOrDefault(item => item.TaskId == completion.Result.TaskId);
                                if (staleTask is not null)
                                {
                                    staleTask.StaleEvidence.Add(new StaleTaskEvidence(
                                        staleTask.InputContextRevision,
                                        completion.Result.Status,
                                        completion.Result.Result.Summary,
                                        completion.Result.Result.Evidence,
                                        DateTimeOffset.UtcNow));
                                    staleTask.Status = "pending";
                                    staleTask.ResultStatus = null;
                                    staleTask.ResultSummary = null;
                                    staleTask.Evidence = [];
                                    staleTask.CompletedAt = null;
                                }
                            }
                            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "actor-completed", cancellationToken).ConfigureAwait(false);
                            hub.SignalWake();
                        }
                    }
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            actorScopeCts.Cancel();
            if (activeActors.Count > 0)
            {
                try { await Task.WhenAll(activeActors.Values).ConfigureAwait(false); }
                catch { /* Primary failure/cancellation propagates above. */ }
            }
            hub.LiveRun = null;
            hub.LiveConfiguration = null;
            hub.LiveCheckpoint = null;
        }
    }

    private Task<ActorCompletion> LaunchSingleTeammateActorTask(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        RunTeammateActorHub hub,
        SemaphoreSlim concurrencySemaphore,
        CancellationToken cancellationToken)
    {
        var parentHolder = _checkpointGateHolder.Value;
        _checkpointGateHolder.Value = new CheckpointGateHolder();
        try
        {
            return RunSingleTeammateActorAsync(
                run, configuration, checkpoint, plannerId, task, hub, concurrencySemaphore, cancellationToken);
        }
        finally
        {
            _checkpointGateHolder.Value = parentHolder;
        }
    }

    private async Task<ActorCompletion> RunSingleTeammateActorAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        RunTeammateActorHub hub,
        SemaphoreSlim concurrencySemaphore,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        try
        {
            RuntimeAgentInstance? worker;
            await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
            {
                if (IsTaskPausedByTeammate(checkpoint, task))
                {
                    task.Status = "paused";
                    return new ActorCompletion(task.TaskId, ActorOutcomeKind.PausedByHuman);
                }

                worker = await AssignWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
                if (worker is null)
                {
                    // AssignWorkerAsync already applied the failure result onto checkpoint.
                    return new ActorCompletion(task.TaskId, ActorOutcomeKind.Completed);
                }

                var (wtPath, wtBranch) = ResolveTaskWorktree(checkpoint, task);
                await TrySyncTaskTransitionToSessionPlanAsync(
                    checkpoint.SessionId, runId, task, summary: null, evidence: null,
                    worktreePath: wtPath, worktreeBranch: wtBranch, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<WorkerToolDescriptor> tools;
            try
            {
                tools = await GetWorkerToolsAsync(run, configuration, task, worker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ActorCompletion(
                    task.TaskId,
                    ActorOutcomeKind.Completed,
                    FailedTaskResult(task, worker.Id, ToolManifestUnavailableCategory, SafeError(ex)));
            }

            if (!task.IsSpacePreparation && tools.Count == 0 && !HasLivePendingExecution(task) && task.ToolTurns.Count == 0)
            {
                var textResult = await ExecuteTextTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
                await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                {
                    checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, textResult.Usage);
                }
                return new ActorCompletion(task.TaskId, ActorOutcomeKind.Completed, textResult);
            }

            var wakeBeforeToolLoop = hub.WakeVersion;
            var toolOutcome = await ExecuteToolTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken, tools).ConfigureAwait(false);
            if (toolOutcome.PausedByHuman)
            {
                return new ActorCompletion(task.TaskId, ActorOutcomeKind.PausedByHuman);
            }
            if (toolOutcome.Waiting)
            {
                return new ActorCompletion(task.TaskId, ActorOutcomeKind.AwaitingExternalDecision, ParkedAtWakeVersion: wakeBeforeToolLoop);
            }
            if (toolOutcome.ParkedOnWait || task.Status == "waiting")
            {
                return new ActorCompletion(task.TaskId, ActorOutcomeKind.ParkedOnWait);
            }
            return new ActorCompletion(task.TaskId, ActorOutcomeKind.Completed, toolOutcome.Result);
        }
        finally
        {
            concurrencySemaphore.Release();
        }
    }

    private static DurableTeammateState? FindBoundTeammate(FullDuplexCheckpointV1 checkpoint, DurableTaskNode task)
    {
        var handle = task.TeammateHandle ?? task.WorkerHandle;
        if (string.IsNullOrWhiteSpace(handle)) return null;
        return checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, handle, StringComparison.OrdinalIgnoreCase));
    }

    internal static void PropagateExpiryPark(FullDuplexCheckpointV1 target, FullDuplexCheckpointV1 source)
    {
        if (!source.AwaitingApprovalExpiryReview) return;
        target.AwaitingApprovalExpiryReview = true;
        target.Phase = "awaiting_user";
    }

    private async Task<RuntimeAgentInstance?> AssignWorkerAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        RuntimeAgentInstance worker;
        try
        {
            worker = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerAssignmentException ex)
        {
            await ApplyTaskResultAsync(run, configuration, runId, checkpoint,
                FailedTaskResult(task, task.WorkerAgentId, WorkerAssignmentInvalidCategory, SafeError(ex)), cancellationToken).ConfigureAwait(false);
            return null;
        }
        var refusal = await AcquireWriteScopeAsync(run, configuration, runId, task, worker.Id, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            await ApplyTaskResultAsync(run, configuration, runId, checkpoint,
                FailedTaskResult(task, task.WorkerAgentId, RunErrorTaxonomy.ResourceConflict, refusal), cancellationToken).ConfigureAwait(false);
            return null;
        }
        await EnrolWorkerAsync(run, configuration, checkpoint, task, worker, cancellationToken).ConfigureAwait(false);
        return worker;
    }

    private async Task<RuntimeAgentInstance> GetOrCreateWorkerAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var graph = configuration.Graph;
        var instances = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);

        var spawnable = ResolvePersistedSpawnable(configuration, task);
        var soloMaster = ResolveSoloMaster(configuration, graph, task);
        WorkerSelection selected;
        if (soloMaster is not null)
        {
            selected = soloMaster;
        }
        else if (spawnable is not null)
        {
            selected = new WorkerSelection(SpawnableDefinition(spawnable, configuration), task.WorkerAssignmentReason ?? "spawnable_whitelist");
        }
        else if (!string.IsNullOrWhiteSpace(task.RequestedAgent))
        {
            (selected, spawnable) = ResolveRequestedWorker(configuration, task, checkpoint.Tasks);
        }
        else
        {
            try
            {
                selected = ResolveOrSelectWorker(configuration, task);
            }
            catch (WorkerUnavailableException)
            {
                await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.CapabilityMissing, run, configuration, checkpoint, cancellationToken,
                    $"No executor covers task '{task.TaskKey}' (tools: {string.Join(", ", task.RequiredTools)}; capabilities: {string.Join(", ", task.RequiredCapabilities)}).",
                    "task", task.TaskId.ToString("N")).ConfigureAwait(false);
                var coverage = GraphSpawnAuthority.FindCoverage(graph, task.RequiredTools, task.RequiredCapabilities);
                if (coverage is null) throw;
                var tierCarriesSpawnAuthority = graph is not null
                    && GraphSpawnAuthority.CarriesSpawnAuthority(graph.Tier);
                if (!tierCarriesSpawnAuthority)
                    throw new WorkerAssignmentException(
                        $"Task '{task.TaskKey}' matches spawnable template '{coverage.Slug}', but the {graph?.Tier ?? "declared-graph"} tier denies spawn (graph_tier_spawn_denied).");
                spawnable = coverage;
                selected = new WorkerSelection(SpawnableDefinition(spawnable, configuration), "spawnable_whitelist");
            }
            catch (InvalidDataException ex)
            {
                throw new WorkerAssignmentException(ex.Message, ex);
            }
        }

        var dispatcherSlug = ResolveDispatcherAgentSlug(checkpoint.Tasks, task);
        if (spawnable is null && soloMaster is null && graph is not null
            && !GraphEdgeAuthority.IsDispatchAllowed(graph, selected.Agent.Id, dispatcherSlug))
        {
            throw new WorkerAssignmentException(
                $"Task '{task.TaskKey}' worker '{selected.Agent.Id}' is not a declared dispatch target of the mode graph (tier {graph.Tier}).");
        }

        var boundTeammate = !string.IsNullOrWhiteSpace(task.TeammateHandle)
            ? checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase))
            : null;
        var isTeammateWakeContinuation = boundTeammate is { RelayDepth: > 0, AgentInstanceId: not null };
        if (!isTeammateWakeContinuation)
        {
            EnsureGraphWorkerBudget(instances, configuration.Spawn.MaxAgentsPerRun, task.TaskKey);
        }

        var lineage = ResolveDispatcherLineage(instances, checkpoint.Tasks, task);
        EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, configuration.Spawn.MaxDepth, task.TaskKey);

        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug))
        {
            task.WorkerAgentSlug = selected.Agent.Id;
            task.WorkerAgentDefinitionId = selected.Agent.AgentDefinitionId;
            task.WorkerAgentVersionId = selected.Agent.AgentVersionId;
            task.WorkerAgentVersionHash = selected.Agent.VersionContentHash;
            task.WorkerAssignmentReason = selected.Reason;
            task.WorkerHandle = !string.IsNullOrWhiteSpace(task.TeammateHandle)
                ? task.TeammateHandle
                : NextWorkerHandle(checkpoint.Tasks, selected.Agent.Id);
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-selected", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "worker.assigned", $"Task assigned to {task.WorkerHandle}.", new
            {
                task_id = task.TaskId,
                task_key = task.TaskKey,
                title = task.Title,
                handle = task.WorkerHandle,
                teammate_handle = task.TeammateHandle,
                teammate_mode = task.TeammateMode,
                requested_agent = task.RequestedAgent,
                agent_slug = selected.Agent.Id,
                agent_definition_id = selected.Agent.AgentDefinitionId,
                agent_version_id = selected.Agent.AgentVersionId,
                agent_version_hash = selected.Agent.VersionContentHash,
                reason = selected.Reason,
                required_capabilities = task.RequiredCapabilities,
                required_tools = task.RequiredTools
            }, cancellationToken, task.TaskId).ConfigureAwait(false);
        }

        if (task.WorkerAgentId is { } assigned)
        {
            var existing = instances.FirstOrDefault(item => item.Id == assigned);
            if (existing is not null)
            {
                VerifyWorkerInstance(existing, selected.Agent, task);
                if (boundTeammate is not null)
                {
                    boundTeammate.AgentInstanceId = existing.Id;
                    boundTeammate.CurrentTaskId = task.TaskId;
                }
                return existing;
            }
        }

        var worker = instances.FirstOrDefault(item => item.Generated
            && item.TaskId == task.TaskId
            && item.AgentVersionId == selected.Agent.AgentVersionId
            && item.Status is "created" or "running");
        if (worker is null)
        {
            if (selected.Agent.AgentDefinitionId is not { } definitionId
                || selected.Agent.AgentVersionId is not { } versionId
                || string.IsNullOrWhiteSpace(selected.Agent.VersionContentHash))
                throw new WorkerAssignmentException($"Frozen specialist '{selected.Agent.Id}' has no immutable version binding.");

            IReadOnlyList<string> seedTools;
            if (spawnable is not null)
            {
                var required = task.RequiredTools
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var outside = spawnable.ToolCeiling.Contains("*", StringComparer.Ordinal)
                    ? []
                    : required.Where(tool => !spawnable.ToolCeiling.Contains(tool, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (outside.Length > 0)
                    throw new WorkerAssignmentException(
                        $"Task '{task.TaskKey}' requires tools outside the spawnable template '{spawnable.Slug}' ceiling: {string.Join(", ", outside)} (spawn_tool_ceiling_exceeded).");
                seedTools = required.Length > 0 ? required : spawnable.ToolCeiling;
            }
            else
            {
                seedTools = selected.Agent.AllowedTools;
            }

            if (task.Worktree
                && string.IsNullOrWhiteSpace(task.TaskWorktreePath)
                && !seedTools.Contains("*", StringComparer.Ordinal)
                && !seedTools.Contains("git_worktree_create", StringComparer.OrdinalIgnoreCase)
                && configuration.ToolManifest.Any(m => string.Equals(m.Id, "git_worktree_create", StringComparison.OrdinalIgnoreCase)))
            {
                seedTools = [.. seedTools, "git_worktree_create"];
            }

            var seedGrants = ResourceSeed(spawnable?.ResourceGrants ?? selected.Agent.ResourceGrants);
            if (task.Worktree && !seedGrants.Any(g => g.StartsWith("write:", StringComparison.OrdinalIgnoreCase)))
            {
                seedGrants = [.. seedGrants, "write:.tinadec/worktrees"];
            }

            worker = await _instances.CreateRootAsync(new RuntimeAgentSeed(
                Guid.Parse(run.SessionId),
                runId,
                selected.Agent.Id,
                selected.Agent.Layer,
                selected.Agent.Role,
                "chat",
                selected.Agent.Capabilities,
                seedTools,
                seedGrants,
                configuration.Context.DefaultTokenBudget,
                TaskId: task.TaskId,
                AgentDefinitionId: definitionId,
                AgentVersionId: versionId,
                VersionContentHash: selected.Agent.VersionContentHash,
                AllowedDispatchTargets: spawnable?.AllowedDispatchTargets ?? selected.Agent.AllowedDispatchTargets,
                ParentInstanceId: lineage.InstanceId,
                GenerationDepth: lineage.Depth ?? 0), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.created",
                spawnable is null ? "Graph-tier execution worker created." : $"Spawnable worker '{spawnable.Slug}' created from the frozen whitelist.",
                new
                {
                    agent_instance_id = worker.Id,
                    agent_slug = selected.Agent.Id,
                    agent_definition_id = worker.AgentDefinitionId,
                    agent_version_id = worker.AgentVersionId,
                    layer = worker.Layer,
                    role = worker.Role,
                    author_instance_id = plannerId
                }, cancellationToken).ConfigureAwait(false);
        }
        VerifyWorkerInstance(worker, selected.Agent, task);

        if (boundTeammate is not null)
        {
            boundTeammate.AgentInstanceId = worker.Id;
            boundTeammate.CurrentTaskId = task.TaskId;
        }
        if (task.WorkerAgentId != worker.Id)
        {
            task.WorkerAgentId = worker.Id;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-assigned", cancellationToken).ConfigureAwait(false);
        }
        return worker;
    }

    private static string? ResolveDispatcherAgentSlug(IReadOnlyList<DurableTaskNode> tasks, DurableTaskNode task)
    {
        if (task.DispatchedByTaskId is not { } parentId) return null;
        return tasks.FirstOrDefault(item => item.TaskId == parentId)?.WorkerAgentSlug;
    }

    internal static void EnsureGraphWorkerBudget(IReadOnlyList<RuntimeAgentInstance> instances, int maxAgentsPerRun, string taskKey)
    {
        if (instances.Count >= maxAgentsPerRun)
            throw new WorkerAssignmentException(
                $"Graph-tier worker budget exhausted for task '{taskKey}': the run already carries {instances.Count} instances (ceiling {maxAgentsPerRun}).");
    }

    internal static (bool Dispatched, Guid? InstanceId, int? Depth) ResolveDispatcherLineage(
        IReadOnlyList<RuntimeAgentInstance> instances,
        IReadOnlyList<DurableTaskNode> tasks,
        DurableTaskNode task)
    {
        if (task.DispatchedByTaskId is not { } dispatchedBy) return (false, null, 0);
        var dispatcher = tasks.FirstOrDefault(item => item.TaskId == dispatchedBy);
        if (dispatcher?.WorkerAgentId is not { } dispatcherInstanceId) return (true, null, null);
        var dispatcherInstance = instances.FirstOrDefault(item => item.Id == dispatcherInstanceId);
        if (dispatcher is { IsCoordinatorTask: true } || string.Equals(dispatcher.TaskKey, "solo", StringComparison.Ordinal))
        {
            // Direct dispatch from the root conversation/coordinator task sits at depth 1
            // when MaxDepth >= 1, or depth 0 when MaxDepth == 0 (single-hop coordinator -> worker).
            return (true, dispatcherInstanceId, dispatcherInstance?.GenerationDepth ?? 0);
        }
        return (true, dispatcherInstanceId, dispatcherInstance?.GenerationDepth + 1);
    }

    internal static void EnsureGraphWorkerDepth(bool dispatched, int? workerDepth, int maxDepth, string taskKey)
    {
        if (!dispatched) return;
        if (workerDepth is null)
            throw new WorkerAssignmentException(
                $"Task '{taskKey}' is a dispatched sub-task whose dispatcher worker could not be resolved, so its spawn depth is unverifiable (max_depth = {maxDepth}). Refusing to create it at depth 0.");
        if (workerDepth.Value > maxDepth)
            throw new WorkerAssignmentException(
                $"Task '{taskKey}' would exceed the spawn depth ceiling (max_depth = {maxDepth}): it would be created at depth {workerDepth.Value}.");
    }

    private static IReadOnlyList<string> ResourceSeed(IReadOnlyList<FrozenResourceGrant> grants) =>
        grants.Count == 0
            ? []
            : grants.Select(grant => $"{grant.Level}:{grant.PathPrefix}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static FrozenSpawnableTemplate? ResolvePersistedSpawnable(FrozenRunConfigurationV1 configuration, DurableTaskNode task)
    {
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug)) return null;
        var template = configuration.Graph?.SpawnableTemplates.FirstOrDefault(item =>
            string.Equals(item.Slug, task.WorkerAgentSlug, StringComparison.OrdinalIgnoreCase));
        if (template is null) return null;
        if ((task.WorkerAgentDefinitionId is { } persistedDefinition && persistedDefinition != template.AgentDefinitionId)
            || (task.WorkerAgentVersionId is { } persistedVersion && persistedVersion != template.AgentVersionId)
            || (task.WorkerAgentVersionHash is { } persistedHash && !string.Equals(persistedHash, template.VersionHash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Persisted worker assignment for task '{task.TaskKey}' does not match the frozen spawnable template.");
        return template;
    }

    private static RuntimeAgentDefinition SpawnableDefinition(FrozenSpawnableTemplate template, FrozenRunConfigurationV1 configuration)
    {
        var conversation = RequiredConversationAgent(configuration);
        return new RuntimeAgentDefinition(
            template.Slug,
            "execution",
            template.Role,
            "on_demand",
            template.Capabilities,
            DirectUserOutput: false,
            ContextAccess: "read")
        {
            AgentDefinitionId = template.AgentDefinitionId,
            AgentVersionId = template.AgentVersionId,
            VersionContentHash = template.VersionHash,
            AllowedTools = template.ToolCeiling,
            ResourceGrants = template.ResourceGrants,
            Description = template.Description,
            SystemPrompt = template.SystemPrompt ?? string.Empty,
            AllowedDispatchTargets = template.AllowedDispatchTargets,
            ModelPlan = conversation.ModelPlan,
            Enabled = true
        };
    }

    internal static (WorkerSelection Selection, FrozenSpawnableTemplate? Spawnable) ResolveRequestedWorker(
        FrozenRunConfigurationV1 configuration,
        DurableTaskNode task,
        IReadOnlyList<DurableTaskNode>? allTasks = null,
        string? dispatcherSlug = null,
        int dispatcherDepth = 0)
    {
        var requested = task.RequestedAgent!.Trim();
        var effectiveDispatcherSlug = dispatcherSlug ?? (allTasks is not null ? ResolveDispatcherAgentSlug(allTasks, task) : null);
        var isRootDispatcher = string.IsNullOrWhiteSpace(effectiveDispatcherSlug)
            || string.Equals(effectiveDispatcherSlug, configuration.Graph?.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase);

        if (isRootDispatcher
            && ConversationDispatchTargets(configuration) is { } allowed
            && !allowed.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            throw new WorkerAssignmentException(
                $"Task '{task.TaskKey}' names executor '{requested}', which this mode does not allow its coordinator to dispatch to. {DescribeDispatchChoices(DispatchRosterOf(configuration))}");
        }
        if (!isRootDispatcher)
        {
            var subAllowed = DispatchRosterForWorker(configuration, effectiveDispatcherSlug!, currentDepth: dispatcherDepth);
            if (!subAllowed.Any(t => string.Equals(t.Id, requested, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WorkerAssignmentException(
                    $"Task '{task.TaskKey}' names executor '{requested}', which dispatcher '{effectiveDispatcherSlug}' is not permitted to dispatch to. {DescribeDispatchChoices(subAllowed)}");
            }
        }

        var agent = configuration.ExecutionAgents.FirstOrDefault(candidate =>
            candidate.Enabled && string.Equals(candidate.Id, requested, StringComparison.OrdinalIgnoreCase));
        if (agent is not null)
        {
            ValidateFrozenAgent(agent, "worker");
            if (!string.IsNullOrWhiteSpace(task.WorkerAgentSlug)
                && (!string.Equals(task.WorkerAgentSlug, agent.Id, StringComparison.Ordinal)
                    || task.WorkerAgentDefinitionId != agent.AgentDefinitionId
                    || task.WorkerAgentVersionId != agent.AgentVersionId
                    || !string.Equals(task.WorkerAgentVersionHash, agent.VersionContentHash, StringComparison.OrdinalIgnoreCase)))
                throw new WorkerAssignmentException($"Persisted worker assignment for task '{task.TaskKey}' does not match its requested executor '{agent.Id}'.");
            return (new WorkerSelection(agent, task.WorkerAssignmentReason ?? "coordinator_assigned"), null);
        }

        var graph = configuration.Graph;
        var template = graph?.SpawnableTemplates.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, requested, StringComparison.OrdinalIgnoreCase));
        if (template is not null)
        {
            if (!GraphSpawnAuthority.CarriesSpawnAuthority(graph!.Tier))
                throw new WorkerAssignmentException(
                    $"Task '{task.TaskKey}' names spawnable template '{template.Slug}', but the {graph.Tier} tier denies spawn (graph_tier_spawn_denied).");
            return (new WorkerSelection(SpawnableDefinition(template, configuration), task.WorkerAssignmentReason ?? "coordinator_assigned"), template);
        }
        throw new WorkerAssignmentException(
            $"Task '{task.TaskKey}' names executor '{requested}', which this run cannot dispatch to. {DescribeDispatchChoices(DispatchRosterOf(configuration))}");
    }

    internal static string NextWorkerHandle(IEnumerable<DurableTaskNode> tasks, string agentId)
    {
        var used = tasks.Count(item => !string.IsNullOrWhiteSpace(item.WorkerHandle)
            && string.Equals(item.WorkerAgentSlug, agentId, StringComparison.OrdinalIgnoreCase));
        return $"{agentId}#{used + 1}";
    }

    internal static WorkerSelection ResolveOrSelectWorker(FrozenRunConfigurationV1 configuration, DurableTaskNode task)
    {
        var selected = SelectWorker(configuration, task);
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug)) return selected;
        if (!string.Equals(task.WorkerAgentSlug, selected.Agent.Id, StringComparison.Ordinal)
            || task.WorkerAgentDefinitionId != selected.Agent.AgentDefinitionId
            || task.WorkerAgentVersionId != selected.Agent.AgentVersionId
            || !string.Equals(task.WorkerAgentVersionHash, selected.Agent.VersionContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Persisted worker assignment for task '{task.TaskKey}' does not match the frozen deterministic selection.");
        }
        return selected with { Reason = task.WorkerAssignmentReason ?? selected.Reason };
    }

    internal static WorkerSelection SelectWorker(FrozenRunConfigurationV1 configuration, DurableTaskNode task)
    {
        var requiredTools = NormalizeValues(task.RequiredTools);
        var requiredCapabilities = NormalizeValues(task.RequiredCapabilities);
        var manifestTools = configuration.ToolManifest.Select(tool => tool.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingManifestTool = requiredTools.FirstOrDefault(tool => !manifestTools.Contains(tool));
        if (missingManifestTool is not null)
            throw new InvalidDataException($"Task '{task.TaskKey}' requires tool '{missingManifestTool}', which is not in the frozen run manifest.");

        var hasGeneralWorker = configuration.ExecutionAgents.Any(agent =>
            agent.Enabled && string.Equals(agent.Id, "worker.general", StringComparison.Ordinal));
        var unclassifiedTask = requiredTools.Count == 0 && requiredCapabilities.Count == 0;
        var candidates = configuration.ExecutionAgents
            .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
            .Select(agent =>
            {
                ValidateFrozenAgent(agent, "worker");
                var tools = ExpandFrozenTools(agent.AllowedTools, manifestTools);
                var capabilities = agent.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isGeneral = string.Equals(agent.Id, "worker.general", StringComparison.Ordinal);
                var coversTools = requiredTools.All(tools.Contains);
                var coversCapabilities = requiredCapabilities.All(capabilities.Contains);
                var eligible = coversTools && coversCapabilities;
                if (unclassifiedTask) eligible = eligible && (isGeneral || !hasGeneralWorker);
                return new WorkerCandidate(agent, tools, capabilities, isGeneral, eligible);
            })
            .Where(candidate => candidate.Eligible)
            .OrderBy(candidate => candidate.IsGeneral)
            .ThenBy(candidate => unclassifiedTask
                ? -candidate.Tools.Count
                : Math.Max(0, candidate.Tools.Count - requiredTools.Count))
            .ThenBy(candidate => unclassifiedTask
                ? -candidate.Capabilities.Count
                : Math.Max(0, candidate.Capabilities.Count - requiredCapabilities.Count))
            .ThenBy(candidate => candidate.Agent.RosterOrder)
            .ThenBy(candidate => candidate.Agent.Id, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            var capabilityText = requiredCapabilities.Count == 0 ? "none" : string.Join(", ", requiredCapabilities);
            var toolText = requiredTools.Count == 0 ? "none" : string.Join(", ", requiredTools);
            throw new WorkerUnavailableException($"No frozen execution specialist can satisfy task '{task.TaskKey}' (capabilities: {capabilityText}; tools: {toolText}).");
        }

        var winner = candidates[0];
        var reason = winner.IsGeneral
            ? unclassifiedTask ? "general_default" : "general_fallback"
            : unclassifiedTask ? "general_unavailable_fallback"
            : requiredCapabilities.Count == 0 ? "specialist_tool_match" : "specialist_capability_and_tool_match";
        return new WorkerSelection(winner.Agent, reason);
    }

    private static HashSet<string> ExpandFrozenTools(
        IReadOnlyList<string> allowedTools,
        IReadOnlySet<string> manifestTools)
    {
        if (allowedTools.Any(tool => string.Equals(tool, "*", StringComparison.OrdinalIgnoreCase)))
            return manifestTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowedTools.Where(manifestTools.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> NormalizeValues(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static RuntimeAgentDefinition GetAssignedWorkerDefinition(
        FrozenRunConfigurationV1 configuration,
        DurableTaskNode task)
    {
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug))
            throw new InvalidDataException($"Task '{task.TaskKey}' has no persisted worker assignment.");
        var definition = configuration.ExecutionAgents.SingleOrDefault(agent =>
            string.Equals(agent.Id, task.WorkerAgentSlug, StringComparison.Ordinal)
            && agent.AgentDefinitionId == task.WorkerAgentDefinitionId
            && agent.AgentVersionId == task.WorkerAgentVersionId
            && string.Equals(agent.VersionContentHash, task.WorkerAgentVersionHash, StringComparison.OrdinalIgnoreCase));
        if (definition is not null) return definition;
        var master = configuration.OperationAgents.SingleOrDefault(agent =>
            string.Equals(agent.Id, task.WorkerAgentSlug, StringComparison.Ordinal)
            && agent.AgentDefinitionId == task.WorkerAgentDefinitionId
            && agent.AgentVersionId == task.WorkerAgentVersionId
            && string.Equals(agent.VersionContentHash, task.WorkerAgentVersionHash, StringComparison.OrdinalIgnoreCase));
        if (master is not null) return master;
        var template = configuration.Graph?.SpawnableTemplates.SingleOrDefault(item =>
            string.Equals(item.Slug, task.WorkerAgentSlug, StringComparison.OrdinalIgnoreCase)
            && item.AgentDefinitionId == task.WorkerAgentDefinitionId
            && item.AgentVersionId == task.WorkerAgentVersionId
            && string.Equals(item.VersionHash, task.WorkerAgentVersionHash, StringComparison.OrdinalIgnoreCase));
        return template is not null
            ? SpawnableDefinition(template, configuration)
            : throw new InvalidDataException($"Task '{task.TaskKey}' worker assignment is not present in the frozen roster.");
    }

    private static void VerifyWorkerInstance(
        RuntimeAgentInstance instance,
        RuntimeAgentDefinition definition,
        DurableTaskNode task)
    {
        if (instance.AgentDefinitionId != definition.AgentDefinitionId
            || instance.AgentVersionId != definition.AgentVersionId
            || !string.Equals(instance.AgentVersionContentHash, definition.VersionContentHash, StringComparison.OrdinalIgnoreCase)
            || instance.TaskId != task.TaskId)
            throw new WorkerAssignmentException($"Worker instance '{instance.Id}' does not match task '{task.TaskKey}' frozen assignment.");
    }

    private async Task<WorkerModelTurn> GetWorkerModelTurnAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        IReadOnlyList<WorkerToolDescriptor> tools,
        CancellationToken cancellationToken,
        string? closeoutPrompt = null)
    {
        var runGuid = Guid.Parse(run.RunId);
        var workerDefinition = GetAssignedWorkerDefinition(configuration, task);
        (string? taskWorktreePath, string? taskWorktreeBranch) worktreeInfo;
        await using (await EnterCheckpointGateAsync(runGuid, cancellationToken).ConfigureAwait(false))
        {
            worktreeInfo = ResolveTaskWorktree(checkpoint, task);
        }
        var (taskWorktreePath, taskWorktreeBranch) = worktreeInfo;
        var promptConfiguration = SpacePromptConfiguration(configuration, checkpoint, taskWorktreePath);
        var context = await BuildContextAsync(
            run,
            promptConfiguration,
            workerDefinition,
            WithSpaceContext(
                checkpoint,
                $"Task: {task.Title}\nDescription: {task.Description}\nSuccess criteria: {string.Join("; ", task.SuccessCriteria)}"
                + (task.WriteScope is { Count: > 0 } scope
                    ? $"\nWrite scope (reserved for you; change files only inside it — changes elsewhere are reported to the reviewer): {string.Join("; ", scope)}"
                    : string.Empty),
                taskWorktreePath,
                taskWorktreeBranch),
            checkpoint.UserGoal,
            cancellationToken).ConfigureAwait(false);

        var assembly = await AssemblePromptAsync(promptConfiguration, workerDefinition, context, cancellationToken).ConfigureAwait(false);
        var agent = new AgentDefinition
        {
            Id = worker.Id,
            Name = workerDefinition.Id,
            Layer = worker.Layer,
            AgentType = workerDefinition.Role,
            ModelRoutePurpose = "chat",
            AllowedTools = worker.AllowedTools,
            Enabled = true
        };

        var hasTeamSpawn = worker.AllowedTools.Any(CoreVirtualToolPolicy.IsTeamSpawn);
        var hasTaskDispatch = worker.AllowedTools.Any(CoreVirtualToolPolicy.IsTaskDispatch);
        var dispatchSection = hasTeamSpawn || hasTaskDispatch
            ? "\n\n" + BuildDispatchRosterSection(
                DispatchRosterForWorker(configuration, workerDefinition.Id, worker.GenerationDepth),
                usesTeamSpawn: hasTeamSpawn)
            : string.Empty;

        var modelFactory = CreateModelFactory(
            configuration,
            checkpoint,
            workerDefinition,
            worker.Id,
            worker.ParentInstanceId,
            AnswerStreaming.WorkLayer,
            task: task,
            run: run);

        var probeResolution = await modelFactory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        var isNonMcpHarness = ChatProtocols.Normalize(probeResolution.Protocol) is ChatProtocols.HeadlessCli or ChatProtocols.Tui;

        string boardSection;
        List<WorkerToolTurn> turnsSnapshot;
        List<WorkerInterruption>? interruptionsSnapshot;
        PlannedTask plannedTaskSnapshot;

        await using (await EnterCheckpointGateAsync(runGuid, cancellationToken).ConfigureAwait(false))
        {
            ConsumePendingMailboxIntoTask(checkpoint, task, GetOrCreateActorHub(runGuid));
            boardSection = FormatBoardContextForAgent(checkpoint, task, isNonMcpHarness);
            if (!string.IsNullOrWhiteSpace(task.TeammateHandle))
            {
                var tm = checkpoint.Teammates.FirstOrDefault(t =>
                    string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase));
                if (tm is not null)
                {
                    tm.LastDeliveredBoardSeq = checkpoint.NextTeamBoardSeq;
                }
            }
            turnsSnapshot = task.ToolTurns.ToList();
            interruptionsSnapshot = task.Interruptions?.ToList();
            plannedTaskSnapshot = ToPlannedTask(task);
        }

        var fullResults = await LoadToolResultsKeptOnExecutionAsync(task, cancellationToken).ConfigureAwait(false);
        var effectiveInstructions = assembly.Instructions + dispatchSection + boardSection + WorkerPatchProtocol;
        return await new ExecutionAgent(modelFactory, _logger).GetNextTurnAsync(
            CreateRunContext(run, checkpoint),
            agent,
            plannedTaskSnapshot,
            turnsSnapshot,
            tools,
            closeoutPrompt is null
                ? effectiveInstructions
                : closeoutPrompt + "\n\n" + effectiveInstructions,
            configuration.Context.RecentMessageLimit,
            cancellationToken,
            interruptionsSnapshot,
            _services.GetService(typeof(IMessageAttachmentStore)) as IMessageAttachmentStore,
            fullResults).ConfigureAwait(false);
    }

    private static bool ConsumePendingMailboxIntoTask(
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RunTeammateActorHub hub,
        bool skipUnstartedOnUrgent = false)
    {
        var selfHandle = task.IsCoordinatorTask
            ? "coordinator"
            : task.TeammateHandle ?? task.WorkerHandle;
        if (string.IsNullOrWhiteSpace(selfHandle))
            return false;

        var changed = false;
        var mailboxMessages = hub.GetMailbox(selfHandle).DrainAll();
        var boundTeammate = !task.IsCoordinatorTask
            ? checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, selfHandle, StringComparison.OrdinalIgnoreCase))
            : null;

        var pendingSteers = boundTeammate?.PendingSteers is { Count: > 0 } steers
            ? steers.OrderByDescending(s => s.Urgent).ThenBy(s => s.CreatedAt).ToList()
            : [];
        if (pendingSteers.Count > 0 && boundTeammate is not null)
        {
            boundTeammate.PendingSteers = null;
            changed = true;
        }

        var hasUrgentSteer = pendingSteers.Any(s => s.Urgent) || mailboxMessages.Any(m => m.Urgent && m.Kind == "steer");
        var skippedCount = 0;
        if (skipUnstartedOnUrgent && hasUrgentSteer)
        {
            var unstarted = task.ToolTurns
                .Where(turn => string.IsNullOrWhiteSpace(turn.ResultJson) && string.IsNullOrWhiteSpace(turn.ExecutionId))
                .ToList();
            foreach (var turn in unstarted)
            {
                turn.DispatchStatus = NotRunAfterInterrupt;
                turn.ResultJson = JsonSerializer.Serialize(new
                {
                    tool_id = turn.ToolId,
                    status = NotRunAfterInterrupt,
                    reason = "interrupted_by_teammate_steer",
                    message = "Not run: direct human steer arrived for this teammate before this tool call started."
                });
                skippedCount++;
                changed = true;
            }
        }

        // Inject human direct steers as high-priority WorkerInterruptions so the model sees them immediately at the tail of its turn history.
        var deliveredSteerSeqs = new HashSet<long>();
        foreach (var steer in pendingSteers)
        {
            deliveredSteerSeqs.Add(steer.BoardSeq);
            var interruptions = task.Interruptions ??= [];
            interruptions.Add(new WorkerInterruption(
                task.ToolTurns.Count,
                $"[DIRECT HUMAN STEER -> @{selfHandle}{(steer.Urgent ? " (URGENT)" : "")}]: {Clip(steer.Message, 1800)}",
                null,
                skippedCount,
                steer.CreatedAt));
            if (interruptions.Count > MaxTaskInterruptions)
                interruptions.RemoveRange(0, interruptions.Count - MaxTaskInterruptions);
            skippedCount = 0;
            changed = true;
        }

        // Also surface live mid-turn peer @messages that arrived in the mailbox while this teammate already completed >= 1 tool turn!
        foreach (var liveMsg in mailboxMessages)
        {
            if (liveMsg.Kind == "steer" && deliveredSteerSeqs.Contains(liveMsg.Seq))
                continue;
            if (liveMsg.Kind == "steer")
            {
                var interruptions = task.Interruptions ??= [];
                interruptions.Add(new WorkerInterruption(
                    task.ToolTurns.Count,
                    $"[DIRECT HUMAN STEER -> @{selfHandle}{(liveMsg.Urgent ? " (URGENT)" : "")}]: {Clip(liveMsg.Content, 1800)}",
                    null,
                    skippedCount,
                    liveMsg.CreatedAt));
                if (interruptions.Count > MaxTaskInterruptions)
                    interruptions.RemoveRange(0, interruptions.Count - MaxTaskInterruptions);
                skippedCount = 0;
                changed = true;
            }
            else if (task.ToolTurns.Count > 0)
            {
                var interruptions = task.Interruptions ??= [];
                interruptions.Add(new WorkerInterruption(
                    task.ToolTurns.Count,
                    $"[LIVE TEAM BOARD MESSAGE #{liveMsg.Seq} from @{liveMsg.Sender} ({liveMsg.Kind})]: {Clip(liveMsg.Content, 1200)}",
                    null,
                    0,
                    liveMsg.CreatedAt));
                if (interruptions.Count > MaxTaskInterruptions)
                    interruptions.RemoveRange(0, interruptions.Count - MaxTaskInterruptions);
                changed = true;
            }
        }

        return changed;
    }

    private IAgentChatClientFactory CreateModelFactory(
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RuntimeAgentDefinition definition,
        Guid? instanceId,
        Guid? parentInstanceId,
        AnswerStreaming streaming = AnswerStreaming.None,
        bool observeOutput = true,
        DurableTaskNode? task = null,
        bool interruptible = true,
        RunState? run = null)
    {
        var resolver = _modelResolver ?? throw new InvalidOperationException("The agent model resolver is not registered.");
        var basePlan = definition.ModelPlan ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no model plan.");
        var effectivePlan = BuildEffectiveModelPlan(configuration, basePlan, task);
        var definitionId = definition.AgentDefinitionId ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no definition id.");
        var versionId = definition.AgentVersionId ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no version id.");
        var modeVersionId = configuration.Bindings.Single(binding => binding.ConfigurationKind == "agent_mode_version").ConfigurationVersionId;

        IAgentModelResolver effectiveResolver = resolver;
        if (task is not null)
        {
            effectiveResolver = new TeammateScopedModelResolver(
                resolver,
                this,
                run,
                configuration,
                checkpoint,
                task);
        }

        return new ModelInvocationChatFactory(effectiveResolver, _chatClients, effectivePlan, new ModelInvocationContext(
            checkpoint.SessionId, checkpoint.RunId, checkpoint.TurnId, instanceId, parentInstanceId,
            definitionId, versionId, modeVersionId, effectivePlan.StrategySource), observeOutput ? async (frame, ct) =>
            {
                var state = await _lifecycle.GetRunStateAsync(checkpoint.RunId.ToString(), ct).ConfigureAwait(false);
                var epoch = LeaseEpochFor(checkpoint.RunId);
                if (IsTerminal(state.Status) || state.Status == RunStatus.Paused
                    || epoch is null || state.LeaseOwner != epoch.OwnerId || state.RecoveryCount != epoch.RecoveryCount)
                    return;
                if (frame.Kind != "delta" || frame.Channel == "reasoning" || task is not null)
                {
                    string? badgeSnapshot = null;
                    if (task?.TeammateHandle is { } tmHandle)
                    {
                        try
                        {
                            badgeSnapshot = checkpoint.Teammates.ToArray()
                                .FirstOrDefault(t => string.Equals(t.Handle, tmHandle, StringComparison.OrdinalIgnoreCase))?.RuntimeBadge;
                        }
                        catch { /* Concurrent list resize during streaming observation is non-fatal. */ }
                    }
                    await AppendEventAsync(checkpoint.RunId, $"model.output.{frame.Kind}",
                        "Model output update.", new
                        {
                            run_id = checkpoint.RunId,
                            turn_id = checkpoint.TurnId,
                            response_id = frame.ResponseId,
                            agent_name = definition.Id,
                            instance_id = instanceId,
                            task_id = task?.TaskId,
                            handle = task?.TeammateHandle ?? task?.WorkerHandle,
                            runtime_badge = badgeSnapshot,
                            channel = frame.Channel,
                            delta = frame.Delta
                        }, ct).ConfigureAwait(false);
                }
                var draft = streaming == AnswerStreaming.AnswerLayer
                    ? _answerDrafts.GetOrAdd(checkpoint.RunId, _ => new AnswerDraft())
                    : null;
                var delta = frame.Kind == "delta" && frame.Channel == "text"
                    ? draft?.Append(frame.Delta ?? string.Empty)
                    : null;
                if (streaming == AnswerStreaming.AnswerLayer && (frame.Kind is "started" or "failed" or "interrupted" || frame.Channel == "text"))
                {
                    await PublishRunStreamAsync(checkpoint.RunId.ToString(), new DurableRunStreamAppend(
                        checkpoint.TurnId, frame.Kind == "interrupted" ? "answer.failed" : $"answer.{frame.Kind}", Delta: delta ?? frame.Delta), ct).ConfigureAwait(false);
                    if (frame.Kind == "delta" && frame.Channel == "text") draft?.MarkPublished();
                }
            } : null,
            interruptible ? Interrupts : null);
    }

    private static FrozenModelPlan BuildEffectiveModelPlan(
        FrozenRunConfigurationV1 configuration,
        FrozenModelPlan basePlan,
        DurableTaskNode? task)
    {
        if (task is null
            || (task.ProviderInstanceOverride is null
                && string.IsNullOrWhiteSpace(task.ChannelOverride)
                && string.IsNullOrWhiteSpace(task.ModelIdOverride)))
        {
            return basePlan;
        }

        var mappedProtocol = MapChannelOverrideToProtocol(task.ChannelOverride);
        var candidates = basePlan.Candidates.ToList();

        if (task.ProviderInstanceOverride is { } targetProviderId)
        {
            var matching = candidates.Where(c => c.ProviderInstanceId == targetProviderId).ToList();
            if (matching.Count == 0)
            {
                var poolMatch = configuration.OperationAgents.Concat(configuration.ExecutionAgents)
                    .Where(a => a.ModelPlan is not null)
                    .SelectMany(a => a.ModelPlan!.Candidates)
                    .FirstOrDefault(c => c.ProviderInstanceId == targetProviderId);
                if (poolMatch is not null)
                {
                    matching.Add(poolMatch);
                }
            }
            if (matching.Count > 0)
            {
                var rest = candidates.Where(c => c.ProviderInstanceId != targetProviderId).ToList();
                candidates = [.. matching, .. rest];
            }
        }

        var adjusted = candidates
            .Select((c, idx) => c with
            {
                Position = idx,
                Model = !string.IsNullOrWhiteSpace(task.ModelIdOverride) && (idx == 0 || c.ProviderInstanceId == task.ProviderInstanceOverride)
                    ? task.ModelIdOverride
                    : c.Model,
                Protocol = !string.IsNullOrWhiteSpace(mappedProtocol) && (idx == 0 || c.ProviderInstanceId == task.ProviderInstanceOverride)
                    ? mappedProtocol!
                    : c.Protocol
            })
            .ToArray();

        return basePlan with
        {
            StrategySource = "team_spawn_override",
            Candidates = adjusted,
            InheritFromParent = task.ProviderInstanceOverride is null && basePlan.InheritFromParent
        };
    }

    private static string? MapChannelOverrideToProtocol(string? channel) =>
        channel?.Trim().ToLowerInvariant() switch
        {
            "acp" => ChatProtocols.Acp,
            "headless" or "headless-cli" or "cli" => ChatProtocols.HeadlessCli,
            "tui" => ChatProtocols.Tui,
            "http" or "opencode-serve" => ChatProtocols.OpencodeServe,
            _ => null
        };

    private ChatResolution EnrichTeammateResolution(
        RunState? run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        ChatResolution resolution)
    {
        var effectiveHandle = task.TeammateHandle ?? task.WorkerHandle ?? task.TaskKey;
        var (taskWorktreePath, _) = ResolveTaskWorktree(checkpoint, task);
        var effectiveWorktree = taskWorktreePath ?? checkpoint.SpaceWorktreePath;
        var teammate = !string.IsNullOrWhiteSpace(task.TeammateHandle)
            ? checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase))
            : null;
        var effectiveWriteScope = task.WriteScope ?? teammate?.WriteScope;

        string? mcpEndpoint = null;
        string? mcpToken = null;
        if (_teamBoardMcpBridge is not null && !task.IsCoordinatorTask)
        {
            mcpEndpoint = ResolveMcpLoopbackEndpoint();
            var leaseKey = (checkpoint.RunId, effectiveHandle.ToLowerInvariant());
            if (!_runTeammateMcpLeases.TryGetValue(leaseKey, out var lease)
                || _teamBoardMcpBridge.ValidateToken(lease.Token) is null)
            {
                var tenantId = run is not null && Guid.TryParse(run.TenantId, out var tid) ? tid : Guid.Empty;
                Guid? workspaceId = run is not null && Guid.TryParse(run.WorkspaceId, out var wid) ? wid : null;
                var principalId = run is not null && Guid.TryParse(run.InitiatedByPrincipalId, out var pid) ? pid : Guid.Empty;
                lease = _teamBoardMcpBridge.IssueToken(
                    tenantId,
                    workspaceId,
                    checkpoint.SessionId,
                    checkpoint.RunId,
                    effectiveHandle,
                    projectId: configuration.Workspace?.ProjectId,
                    principalId: principalId);
                _runTeammateMcpLeases[leaseKey] = lease;
            }
            mcpToken = lease.Token;
        }

        var enriched = resolution with
        {
            RunId = checkpoint.RunId.ToString(),
            TeammateHandle = effectiveHandle,
            WorkingDirectoryOverride = effectiveWorktree,
            WriteScope = effectiveWriteScope,
            McpLoopbackEndpoint = mcpEndpoint,
            McpScopedBearerToken = mcpToken,
            Model = !string.IsNullOrWhiteSpace(task.ModelIdOverride) ? task.ModelIdOverride : resolution.Model,
            ModelId = !string.IsNullOrWhiteSpace(task.ModelIdOverride) ? task.ModelIdOverride : resolution.ModelId,
        };

        if (teammate is not null && enriched.IsAvailable)
        {
            teammate.RuntimeBadge = enriched.RuntimeBadge ?? FallbackRuntimeBadge(enriched);
            teammate.ProviderInstanceId ??= enriched.ProviderInstanceId;
            teammate.ModelId ??= enriched.Model ?? enriched.ModelId;
            teammate.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return enriched;
    }

    private static string FallbackRuntimeBadge(ChatResolution resolution)
    {
        var protocol = ChatProtocols.Normalize(resolution.Protocol);
        var label = resolution.Model ?? resolution.ModelId ?? protocol;
        return protocol switch
        {
            ChatProtocols.Acp => $"ACP · {label}",
            ChatProtocols.HeadlessCli => $"CLI · {label}",
            ChatProtocols.Tui => $"TUI · {label}",
            ChatProtocols.OpencodeServe => $"HTTP · {label}",
            _ => $"API · {label}"
        };
    }

    private string ResolveMcpLoopbackEndpoint()
    {
        var explicitMcp = Environment.GetEnvironmentVariable("TINADEC_MCP_URL");
        if (!string.IsNullOrWhiteSpace(explicitMcp))
            return explicitMcp.Trim();

        var config = _services.GetService(typeof(IConfiguration)) as IConfiguration;
        var rawUrls = Environment.GetEnvironmentVariable("TINADEC_CORE_URL")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? config?["Tinadec:LoopbackBaseUrl"]
            ?? config?["urls"]
            ?? config?["ASPNETCORE_URLS"];

        if (!string.IsNullOrWhiteSpace(rawUrls))
        {
            var first = rawUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                var normalized = first
                    .Replace("://0.0.0.0:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal)
                    .TrimEnd('/');
                if (normalized.EndsWith("/api/v1/mcp/tinadec", StringComparison.OrdinalIgnoreCase))
                    return normalized;
                return normalized + "/api/v1/mcp/tinadec";
            }
        }

        return "http://127.0.0.1:48720/api/v1/mcp/tinadec";
    }

    private async Task SettleTeammateTurnAsync(
        RunState run,
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        TaskExecutionResult execution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.TeammateHandle)) return;

        var teammate = checkpoint.Teammates.FirstOrDefault(t =>
            string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase));
        if (teammate is null) return;

        var (worktreePath, worktreeBranch) = ResolveTaskWorktree(checkpoint, task);
        if (!string.IsNullOrWhiteSpace(worktreePath)) teammate.WorktreePath = worktreePath;
        if (!string.IsNullOrWhiteSpace(worktreeBranch)) teammate.BranchName = worktreeBranch;

        var rawSummary = task.ResultSummary ?? execution.Result.Summary ?? string.Empty;
        var cleanedSummary = ExtractNonMcpBoardMessages(checkpoint, teammate, task.DispatchedByTaskId, rawSummary);
        teammate.LastOutcomeSummary = BoundText(cleanedSummary, MaxTeammateOutcomeSummaryChars);
        teammate.TranscriptSummary = AppendBoundedTranscriptSummary(
            teammate.TranscriptSummary,
            task.Attempt,
            teammate.RelayDepth,
            execution.Status,
            teammate.LastOutcomeSummary);

        teammate.Status = teammate.Paused
            ? "paused"
            : string.Equals(teammate.Mode, "teammate", StringComparison.OrdinalIgnoreCase)
                ? "waiting"
                : "terminated";
        teammate.CurrentTaskId = null;
        teammate.UpdatedAt = DateTimeOffset.UtcNow;

        // If this teammate claimed a step on the shared task board and completed its turn cleanly, settle the claimed step and unlock dependents.
        if (!string.IsNullOrWhiteSpace(teammate.ClaimedStepId) && _sessionPlan is not null)
        {
            var settledStepId = teammate.ClaimedStepId!;
            teammate.ClaimedStepId = null;
            if (execution.Status == "completed")
            {
                try
                {
                    var completeRes = await _sessionPlan.CompleteTaskAsync(
                        checkpoint.SessionId,
                        settledStepId,
                        teammate.Handle,
                        teammate.LastOutcomeSummary,
                        task.Evidence,
                        runId,
                        cancellationToken).ConfigureAwait(false);
                    if (completeRes.Completed)
                    {
                        await NotifyUnlockedTasksAndWakeIdleTeammatesAsync(
                            runId, checkpoint, teammate.Handle, completeRes, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.TryLogDebug(ex, "Could not auto-complete claimed step {StepId} for teammate {Handle}.", settledStepId, teammate.Handle);
                }
            }
        }

        var alreadyPostedThisTurn = task.ToolTurns.Any(turn =>
            CoreVirtualToolPolicy.IsTeamMessage(turn.ToolId) && turn.ToolSuccess == true);
        if (!alreadyPostedThisTurn && !string.IsNullOrWhiteSpace(teammate.LastOutcomeSummary))
        {
            AppendBoardMessage(
                checkpoint,
                teammate.Handle,
                "coordinator",
                execution.Status == "completed" ? "handoff" : "update",
                $"[{execution.Status}] {teammate.LastOutcomeSummary}",
                wake: false);
        }

        await AppendEventAsync(runId, "team.teammate_settled",
            $"Teammate @{teammate.Handle} ({teammate.AgentSlug}) settled into '{teammate.Status}'.",
            new
            {
                run_id = runId,
                task_id = task.TaskId,
                handle = teammate.Handle,
                agent_slug = teammate.AgentSlug,
                mode = teammate.Mode,
                status = teammate.Status,
                paused = teammate.Paused,
                relay_depth = teammate.RelayDepth,
                worktree_path = teammate.WorktreePath,
                branch_name = teammate.BranchName,
                runtime_badge = teammate.RuntimeBadge,
                last_outcome = teammate.LastOutcomeSummary
            }, cancellationToken, task.TaskId).ConfigureAwait(false);

        // Persistent ('teammate') autonomous continuation (#50):
        // 1) If direct human steers arrived just as the turn settled, wake immediately to address them.
        // 2) Otherwise, check the claimable task board for open unclaimed dependency-ready tasks (never stealing 'coordinator'-owned steps;
        //    'ephemeral' workers stay terminated and never auto-claim).
        if (string.Equals(teammate.Mode, "teammate", StringComparison.OrdinalIgnoreCase)
            && !teammate.Paused
            && string.Equals(teammate.Status, "waiting", StringComparison.OrdinalIgnoreCase))
        {
            if (teammate.PendingSteers is { Count: > 0 } steers)
            {
                var nextSteer = steers[0];
                WakeTeammateFromBoard(
                    checkpoint,
                    task.DispatchedByTaskId,
                    teammate.Handle,
                    nextSteer.BoardSeq,
                    $"[DIRECT HUMAN STEER]: {nextSteer.Message}",
                    senderRelayDepth: 0,
                    bypassRelayCap: true);
                return;
            }

            if (execution.Status == "completed"
                && teammate.RelayDepth < MaxTeammateRelayDepth
                && _sessionPlan is not null)
            {
                await TryAutoClaimNextOpenTaskForTeammateAsync(
                    runId, checkpoint, teammate, task.DispatchedByTaskId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TryAutoClaimNextOpenTaskForTeammateAsync(
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        DurableTeammateState teammate,
        Guid? dispatcherTaskId,
        CancellationToken cancellationToken)
    {
        if (_sessionPlan is null || teammate.Paused || !string.Equals(teammate.Status, "waiting", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var boardSnapshot = await _sessionPlan.ListTasksAsync(checkpoint.SessionId, onlyReady: true, cancellationToken).ConfigureAwait(false);
            // Only auto-claim tasks placed in the open pool (AssignedAgentSlug is null / 'unassigned' / 'any')
            // so coordinator-owned steps or explicitly targeted steps are never hijacked unexpectedly.
            var openCandidate = boardSnapshot.Tasks.FirstOrDefault(item =>
                item.Ready
                && string.IsNullOrWhiteSpace(item.ClaimedBy)
                && (string.IsNullOrWhiteSpace(item.AssignedAgentSlug)
                    || string.Equals(item.AssignedAgentSlug, "unassigned", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.AssignedAgentSlug, "any", StringComparison.OrdinalIgnoreCase)));
            if (openCandidate is null)
                return;

            var claim = await _sessionPlan.ClaimTaskAsync(
                checkpoint.SessionId,
                openCandidate.Id,
                teammate.Handle,
                expectedRevision: boardSnapshot.Revision,
                runId: runId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!claim.Claimed || claim.Task is null)
                return;

            var claimPrompt = $"Autonomously claimed ready task '{claim.Task.Id}' ({claim.Task.Title}) from the shared task board."
                + (string.IsNullOrWhiteSpace(claim.Task.Description) ? string.Empty : $"\nDetails: {claim.Task.Description}")
                + "\nExecute this step within your authorized scope and call `team_tasks` (`action: 'complete'`) or report completion when done.";
            var claimMsg = AppendBoardMessage(
                checkpoint,
                teammate.Handle,
                "coordinator",
                "update",
                $"[task-claimed] @{teammate.Handle} claimed ready step '{claim.Task.Id}' ({claim.Task.Title}).",
                wake: false);
            if (WakeTeammateFromBoard(checkpoint, dispatcherTaskId, teammate.Handle, claimMsg.Seq, claimPrompt))
            {
                teammate.ClaimedStepId = claim.Task.Id;
                await AppendEventAsync(runId, "team.task_claimed",
                    $"Teammate @{teammate.Handle} autonomously claimed task '{claim.Task.Id}'.",
                    new
                    {
                        run_id = runId,
                        handle = teammate.Handle,
                        step_id = claim.Task.Id,
                        title = claim.Task.Title,
                        revision = claim.Revision,
                        autonomous = true
                    }, cancellationToken, teammate.CurrentTaskId).ConfigureAwait(false);
            }
            else
            {
                await _sessionPlan.ReleaseTaskAsync(checkpoint.SessionId, claim.Task.Id, teammate.Handle, "Relay depth reached.", false, runId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogDebug(ex, "Auto-claim scan skipped for teammate {Handle} in run {RunId}.", teammate.Handle, runId);
        }
    }

    internal async Task NotifyUnlockedTasksAndWakeIdleTeammatesAsync(
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        string completerHandle,
        TeamTaskCompleteResult completed,
        CancellationToken cancellationToken)
    {
        await AppendEventAsync(runId, "team.task_completed",
            $"@{completerHandle} completed task '{completed.StepId}' (unlocked {completed.UnlockedReadyTasks.Count} downstream task(s)).",
            new
            {
                run_id = runId,
                handle = completerHandle,
                step_id = completed.StepId,
                unlocked_step_ids = completed.UnlockedReadyTasks.Select(t => t.Id).ToArray(),
                revision = completed.Revision
            }, cancellationToken).ConfigureAwait(false);

        if (completed.UnlockedReadyTasks.Count == 0)
            return;

        var coordinatorTaskId = checkpoint.Tasks.FirstOrDefault(t => t.IsCoordinatorTask)?.TaskId
            ?? checkpoint.Tasks.FirstOrDefault(t => t.DispatchedByTaskId is null)?.TaskId;
        var idleTeammates = checkpoint.Teammates
            .Where(t => string.Equals(t.Mode, "teammate", StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Status, "waiting", StringComparison.OrdinalIgnoreCase)
                && !t.Paused
                && !string.Equals(t.Handle, completerHandle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var unlocked in completed.UnlockedReadyTasks)
        {
            if (string.Equals(unlocked.AssignedAgentSlug, "coordinator", StringComparison.OrdinalIgnoreCase))
                continue;

            DurableTeammateState? wakeCandidate = null;
            if (!string.IsNullOrWhiteSpace(unlocked.AssignedAgentSlug)
                && !string.Equals(unlocked.AssignedAgentSlug, "unassigned", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(unlocked.AssignedAgentSlug, "any", StringComparison.OrdinalIgnoreCase))
            {
                wakeCandidate = idleTeammates.FirstOrDefault(t =>
                    string.Equals(t.Handle, unlocked.AssignedAgentSlug, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.AgentSlug, unlocked.AssignedAgentSlug, StringComparison.OrdinalIgnoreCase));
            }
            wakeCandidate ??= idleTeammates.FirstOrDefault();
            if (wakeCandidate is null)
                break;

            idleTeammates.Remove(wakeCandidate);
            var unlockMsg = AppendBoardMessage(
                checkpoint,
                completerHandle,
                wakeCandidate.Handle,
                "handoff",
                $"Step '{completed.StepId}' is complete, unlocking ready task '{unlocked.Id}' ({unlocked.Title}). Claim it via `team_tasks` (`action: 'claim'`, `step_id: '{unlocked.Id}'`) and proceed.",
                wake: true);
            WakeTeammateFromBoard(checkpoint, coordinatorTaskId, wakeCandidate.Handle, unlockMsg.Seq, unlockMsg.Content);
        }

        GetOrCreateActorHub(runId).SignalWake();
    }

    private static string AppendBoundedTranscriptSummary(
        string? existing,
        int attempt,
        int relayDepth,
        string status,
        string outcomeSummary)
    {
        var entry = $"[turn #{Math.Max(1, attempt)} / wake #{relayDepth} -> {status}] {outcomeSummary}".Trim();
        var combined = string.IsNullOrWhiteSpace(existing)
            ? entry
            : existing.Trim() + "\n" + entry;
        if (combined.Length <= MaxTeammateTranscriptSummaryChars)
            return combined;
        return "…" + combined[(combined.Length - MaxTeammateTranscriptSummaryChars + 1)..];
    }

    /// <summary>
    /// Bridges <c>BOARD_MESSAGE: @&lt;target&gt; | &lt;content&gt;</c> lines emitted by HeadlessCli / Tui
    /// teammates (which cannot issue live mid-turn HTTP MCP calls) back onto the shared run board.
    /// </summary>
    private static string ExtractNonMcpBoardMessages(
        FullDuplexCheckpointV1 checkpoint,
        DurableTeammateState sender,
        Guid? dispatcherTaskId,
        string text)
    {
        const string marker = "BOARD_MESSAGE:";
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return text;

        var kept = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var body = trimmed[marker.Length..].Trim();
                var sep = body.IndexOf('|', StringComparison.Ordinal);
                var target = "coordinator";
                var msgContent = body;
                if (sep > 0)
                {
                    target = body[..sep].Trim().TrimStart('@');
                    if (string.IsNullOrWhiteSpace(target)) target = "coordinator";
                    msgContent = body[(sep + 1)..].Trim();
                }
                if (!string.IsNullOrWhiteSpace(msgContent))
                {
                    var entry = AppendBoardMessage(checkpoint, sender.Handle, target, "update", msgContent, wake: true);
                    if (!string.Equals(target, "coordinator", StringComparison.OrdinalIgnoreCase))
                    {
                        var wakeTargets = string.Equals(target, "all", StringComparison.OrdinalIgnoreCase)
                            ? checkpoint.Teammates.Where(t => !string.Equals(t.Handle, sender.Handle, StringComparison.OrdinalIgnoreCase)).Select(t => t.Handle).ToArray()
                            : [target];
                        foreach (var h in wakeTargets)
                        {
                            WakeTeammateFromBoard(checkpoint, dispatcherTaskId, h, entry.Seq, msgContent, sender.RelayDepth);
                        }
                    }
                }
                continue;
            }
            kept.Add(line);
        }
        var cleaned = string.Join("\n", kept).Trim();
        return cleaned.Length == 0 ? text.Trim() : cleaned;
    }

    internal static bool WakeTeammateFromBoard(
        FullDuplexCheckpointV1 checkpoint,
        Guid? dispatcherTaskId,
        string handle,
        long wakeSeq,
        string reasonPrompt,
        int? senderRelayDepth = null,
        bool bypassRelayCap = false)
    {
        if (string.IsNullOrWhiteSpace(handle)) return false;
        var cleanHandle = handle.Trim().TrimStart('@');
        var teammate = checkpoint.Teammates.FirstOrDefault(t =>
            string.Equals(t.Handle, cleanHandle, StringComparison.OrdinalIgnoreCase));
        if (teammate is null) return false;
        if (teammate.Paused) return false;
        if (!string.Equals(teammate.Mode, "teammate", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(teammate.Status, "waiting", StringComparison.OrdinalIgnoreCase)) return false;

        var nextDepth = bypassRelayCap
            ? Math.Min(teammate.RelayDepth + 1, MaxTeammateRelayDepth)
            : (senderRelayDepth ?? teammate.RelayDepth) + 1;
        if (!bypassRelayCap && nextDepth > MaxTeammateRelayDepth)
        {
            AppendBoardMessage(
                checkpoint,
                "system",
                "coordinator",
                "update",
                $"[relay-cap] Refused waking @{teammate.Handle}: relay depth {nextDepth} exceeds MaxTeammateRelayDepth ({MaxTeammateRelayDepth}).",
                wake: false);
            return false;
        }

        var baseKey = NormalizeTaskKey(null, $"{teammate.Handle}-wake-{nextDepth}");
        var taskKey = baseKey;
        if (checkpoint.Tasks.Any(n => string.Equals(n.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)))
        {
            taskKey = $"{baseKey}-{checkpoint.Tasks.Count + 1}";
        }

        var parentTaskId = dispatcherTaskId
            ?? checkpoint.Tasks.FirstOrDefault(t => t.IsCoordinatorTask)?.TaskId
            ?? checkpoint.Tasks.FirstOrDefault(t => t.DispatchedByTaskId is null)?.TaskId;

        var wakeDescription = BuildTeammateWakePrompt(checkpoint, teammate, reasonPrompt);
        var wakeTask = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = taskKey,
            Title = $"@{teammate.Handle} continuation (wake #{nextDepth})",
            Description = wakeDescription,
            RequestedAgent = teammate.AgentSlug,
            TeammateHandle = teammate.Handle,
            TeammateMode = "teammate",
            WorkerHandle = teammate.Handle,
            DispatchedByTaskId = parentTaskId,
            WriteScope = teammate.WriteScope?.ToList(),
            Worktree = !string.IsNullOrWhiteSpace(teammate.WorktreePath),
            TaskWorktreePath = teammate.WorktreePath,
            TaskWorktreeBranch = teammate.BranchName,
            ProviderInstanceOverride = teammate.ProviderInstanceId,
            ChannelOverride = teammate.ChannelOverride,
            ModelIdOverride = teammate.ModelId,
            SuccessCriteria = [],
            Priority = 2,
            Risk = "medium",
            Status = "pending"
        };

        teammate.Status = "working";
        teammate.RelayDepth = nextDepth;
        teammate.LastWakeSeq = wakeSeq;
        teammate.CurrentTaskId = wakeTask.TaskId;
        teammate.UpdatedAt = DateTimeOffset.UtcNow;

        checkpoint.Tasks.Add(wakeTask);
        return true;
    }

    private static string BuildTeammateWakePrompt(
        FullDuplexCheckpointV1 checkpoint,
        DurableTeammateState teammate,
        string reasonPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are persistent teammate @{teammate.Handle} ({teammate.AgentSlug}), woken on the shared board (wake #{teammate.RelayDepth + 1}/{MaxTeammateRelayDepth}).");
        if (!string.IsNullOrWhiteSpace(teammate.WorktreePath))
        {
            sb.AppendLine($"Your isolated Git worktree from earlier turns is preserved at: {teammate.WorktreePath}"
                + (string.IsNullOrWhiteSpace(teammate.BranchName) ? string.Empty : $" (branch: {teammate.BranchName})"));
        }
        if (teammate.WriteScope is { Count: > 0 })
        {
            sb.AppendLine($"Authorized write_scope: {string.Join(", ", teammate.WriteScope)}");
        }
        if (!string.IsNullOrWhiteSpace(teammate.TranscriptSummary))
        {
            sb.AppendLine("\nYour bounded prior turn summary:");
            sb.AppendLine(teammate.TranscriptSummary);
        }
        var unread = checkpoint.TeamBoard
            .Where(m => m.Seq > teammate.LastDeliveredBoardSeq)
            .TakeLast(16)
            .ToArray();
        if (unread.Length > 0)
        {
            sb.AppendLine("\nRecent board messages since your last turn:");
            foreach (var msg in unread)
            {
                sb.AppendLine($"- [#{msg.Seq} {msg.Sender} -> @{msg.Target} ({msg.Kind})]: {BoundText(msg.Content, 600)}");
            }
        }
        sb.AppendLine("\nNew wake instruction / feedback to address now:");
        sb.AppendLine(reasonPrompt.Trim());
        return sb.ToString();
    }

    private sealed record WorkerCandidate(
        RuntimeAgentDefinition Agent,
        HashSet<string> Tools,
        HashSet<string> Capabilities,
        bool IsGeneral,
        bool Eligible);

    internal sealed record WorkerSelection(RuntimeAgentDefinition Agent, string Reason);

    private sealed class TeammateScopedModelResolver(
        IAgentModelResolver inner,
        FullDuplexRunEngine engine,
        RunState? run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task) : IAgentModelResolver
    {
        public Task<ModelResolutionPreviewDto> PreviewAsync(ModelResolutionPreviewRequestDto request, CancellationToken cancellationToken = default) =>
            inner.PreviewAsync(request, cancellationToken);

        public Task<FrozenModelPlan> FreezeAsync(AgentModelFreezeRequest request, CancellationToken cancellationToken = default) =>
            inner.FreezeAsync(request, cancellationToken);

        public async Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(
            FrozenModelPlan plan,
            Guid? parentInstanceId,
            CancellationToken cancellationToken = default)
        {
            var resolved = await inner.ResolveInvocationCandidatesAsync(plan, parentInstanceId, cancellationToken).ConfigureAwait(false);
            if (resolved.Count == 0) return resolved;
            return resolved
                .Select(candidate => engine.EnrichTeammateResolution(run, configuration, checkpoint, task, candidate))
                .ToArray();
        }

        public Task<Guid> StartInvocationAsync(ModelInvocationStart request, CancellationToken cancellationToken = default) =>
            inner.StartInvocationAsync(request, cancellationToken);

        public Task CompleteInvocationAsync(
            Guid invocationId,
            string status,
            ModelUsage? usage = null,
            string? errorCategory = null,
            string? safeErrorMessage = null,
            CancellationToken cancellationToken = default) =>
            inner.CompleteInvocationAsync(invocationId, status, usage, errorCategory, safeErrorMessage, cancellationToken);
    }

    // =========================================================================
    // ITeammateControlPort — Direct human-to-teammate steer / pause / resume (#50)
    // =========================================================================

    public async Task<TeammateListResponseDto> ListTeammatesAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        RunState run;
        try
        {
            run = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            throw new TeammateControlException(404, "NOT_FOUND", $"Run '{runId}' was not found.");
        }

        var sessionId = Guid.TryParse(run.SessionId, out var sid) ? sid : Guid.Empty;
        IReadOnlyList<TeammateSnapshotDto> snapshots;

        await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            var checkpoint = _runActorHubs.TryGetValue(runId, out var hub) && hub.LiveCheckpoint is not null
                ? hub.LiveCheckpoint
                : await TryReadCheckpointAsync(runId).ConfigureAwait(false);
            snapshots = ProjectTeammateSnapshots(checkpoint);
        }

        if (_peerDelegation is not null)
        {
            var peerLinks = await _peerDelegation.ListRunDelegationsAsync(runId, refreshActive: true, cancellationToken).ConfigureAwait(false);
            if (peerLinks.Count > 0)
            {
                snapshots = MergePeerDelegationsIntoSnapshots(snapshots, peerLinks);
            }
        }

        return new TeammateListResponseDto
        {
            RunId = runId,
            SessionId = sessionId,
            RunStatus = ToStatusVocabulary(run.Status),
            Teammates = snapshots
        };
    }

    internal static IReadOnlyList<TeammateSnapshotDto> MergePeerDelegationsIntoSnapshots(
        IReadOnlyList<TeammateSnapshotDto> localSnapshots,
        IReadOnlyList<PeerDelegationLinkDto> peerLinks)
    {
        if (peerLinks.Count == 0) return localSnapshots;

        var merged = localSnapshots.ToList();
        var indexByHandle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < merged.Count; i++)
        {
            indexByHandle[merged[i].Handle] = i;
        }

        foreach (var link in peerLinks)
        {
            var status = MapPeerLinkStatusToTeammateStatus(link.Status, link.AwaitingApproval);
            var awaiting = link.AwaitingApproval || string.Equals(link.Status, "awaiting_approval", StringComparison.OrdinalIgnoreCase);
            var updatedAt = link.UpdatedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(link.UpdatedAt)
                : DateTimeOffset.UtcNow;
            var peerSnapshot = new TeammateSnapshotDto
            {
                Handle = link.TeammateHandle,
                AgentSlug = $"peer:{link.RemoteDeviceName}",
                Mode = "peer",
                Status = status,
                Paused = false,
                AwaitingApproval = awaiting,
                CurrentTaskId = null,
                CurrentTaskTitle = status == "terminated" ? null : link.Goal,
                ClaimedStepId = null,
                LatestSummary = !string.IsNullOrWhiteSpace(link.SummaryText) ? link.SummaryText : link.Goal,
                RuntimeBadge = link.RuntimeBadge,
                WorktreePath = null,
                BranchName = null,
                WriteScope = link.WriteScope,
                RelayDepth = link.HopDepth,
                PendingSteerCount = 0,
                UpdatedAt = updatedAt
            };

            if (indexByHandle.TryGetValue(link.TeammateHandle, out var existingIndex))
            {
                merged[existingIndex] = peerSnapshot;
            }
            else
            {
                indexByHandle[link.TeammateHandle] = merged.Count;
                merged.Add(peerSnapshot);
            }
        }

        return merged;
    }

    internal static string MapPeerLinkStatusToTeammateStatus(string? linkStatus, bool awaitingApproval = false)
    {
        if (awaitingApproval || string.Equals(linkStatus, "awaiting_approval", StringComparison.OrdinalIgnoreCase))
            return "awaiting_approval";
        return linkStatus?.Trim().ToLowerInvariant() switch
        {
            "completed" or "failed" or "cancelled" => "terminated",
            _ => "working"
        };
    }

    public async Task<TeammateSteerReceiptDto> SteerTeammateAsync(
        Guid runId,
        string handle,
        TeammateSteerRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var rawMessage = request?.Message?.Trim();
        if (string.IsNullOrWhiteSpace(rawMessage))
            throw new TeammateControlException(400, "INVALID_STEER_MESSAGE", "Steer message must be non-empty.");

        var cleanHandle = SanitizeLookupHandle(handle);
        if (string.IsNullOrWhiteSpace(cleanHandle))
            throw new TeammateControlException(400, "INVALID_HANDLE", "Teammate handle is required.");

        var run = await RequireActiveRunForControlAsync(runId, cancellationToken).ConfigureAwait(false);
        var urgent = request?.Urgent ?? true;
        var boundedMessage = BoundText(rawMessage, 4000);
        var now = DateTimeOffset.UtcNow;

        TeammateSteerReceiptDto? receipt = null;
        var shouldEnqueueRun = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                {
                    var hub = GetOrCreateActorHub(runId);
                    var cp = hub.LiveCheckpoint ?? await TryReadCheckpointAsync(runId).ConfigureAwait(false)
                        ?? throw new TeammateControlException(404, "NOT_FOUND", $"Checkpoint for run '{runId}' is not initialized yet.");

                    var teammate = ResolveOrMaterializeTeammate(cp, cleanHandle)
                        ?? throw new TeammateControlException(404, "NOT_FOUND", $"Teammate '@{cleanHandle}' was not found in run '{runId}'.");

                    var activeTask = FindActiveTeammateTask(cp, teammate);
                    if (string.Equals(teammate.Status, "terminated", StringComparison.OrdinalIgnoreCase)
                        && activeTask is null
                        && string.Equals(teammate.Mode, "ephemeral", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new TeammateControlException(409, "TEAMMATE_TERMINATED", $"Ephemeral teammate '@{teammate.Handle}' has already finished and cannot be steered.");
                    }

                    var boardEntry = AppendBoardMessage(cp, "human", teammate.Handle, "steer", boundedMessage, wake: true);
                    var steerId = string.IsNullOrWhiteSpace(request?.ClientControlId)
                        ? $"steer-{boardEntry.Seq}"
                        : request!.ClientControlId!.Trim();
                    var steerRecord = new PendingTeammateSteer(steerId, boundedMessage, urgent, boardEntry.Seq, now);

                    string deliveryMode;
                    var woken = false;

                    if (teammate.Paused)
                    {
                        EnqueuePendingSteer(teammate, steerRecord);
                        deliveryMode = "queued_while_paused";
                    }
                    else if (string.Equals(teammate.Status, "waiting", StringComparison.OrdinalIgnoreCase) && activeTask is null)
                    {
                        woken = WakeTeammateFromBoard(
                            cp,
                            dispatcherTaskId: null,
                            teammate.Handle,
                            boardEntry.Seq,
                            $"[DIRECT HUMAN STEER{(urgent ? " · URGENT" : "")}]: {boundedMessage}",
                            senderRelayDepth: 0,
                            bypassRelayCap: true);
                        if (!woken)
                        {
                            EnqueuePendingSteer(teammate, steerRecord);
                            deliveryMode = "injected_next_turn";
                        }
                        else
                        {
                            deliveryMode = "woken";
                            shouldEnqueueRun = true;
                        }
                    }
                    else
                    {
                        EnqueuePendingSteer(teammate, steerRecord);
                        hub.GetMailbox(teammate.Handle).Enqueue(new LiveMailboxMessage(
                            boardEntry.Seq, "human", "steer", boundedMessage, urgent, now));
                        deliveryMode = "injected_next_turn";
                        if (hub.LiveCheckpoint is null)
                            shouldEnqueueRun = true;
                    }

                    teammate.UpdatedAt = now;
                    cp = await SaveCheckpointAsync(cp, cp.CheckpointRevision, $"teammate-steer-{teammate.Handle}", cancellationToken).ConfigureAwait(false);
                    hub.SignalWake();

                    var effectiveStatus = ComputeTeammateEffectiveStatus(cp, teammate);
                    receipt = new TeammateSteerReceiptDto
                    {
                        Accepted = true,
                        RunId = runId,
                        Handle = teammate.Handle,
                        Status = effectiveStatus,
                        Paused = teammate.Paused,
                        Urgent = urgent,
                        Woken = woken,
                        DeliveryMode = deliveryMode,
                        BoardSeq = boardEntry.Seq,
                        PendingSteerCount = teammate.PendingSteers?.Count ?? 0,
                        TaskId = teammate.CurrentTaskId ?? activeTask?.TaskId,
                        DeliveredAt = now
                    };
                }
                break;
            }
            catch (RunCheckpointConflictException) when (attempt < 2)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        if (receipt is null)
            throw new TeammateControlException(409, "CHECKPOINT_CONFLICT", "Concurrent run state update conflicted; please retry.");

        await AppendEventAsync(runId, "team.teammate_steered",
            $"Human steered @{receipt.Handle} ({receipt.DeliveryMode}, urgent={receipt.Urgent.ToString().ToLowerInvariant()}).",
            new
            {
                run_id = runId,
                handle = receipt.Handle,
                urgent = receipt.Urgent,
                woken = receipt.Woken,
                delivery_mode = receipt.DeliveryMode,
                board_seq = receipt.BoardSeq,
                task_id = receipt.TaskId,
                message = BoundText(boundedMessage, 600)
            }, cancellationToken, receipt.TaskId).ConfigureAwait(false);

        if (shouldEnqueueRun)
        {
            if (run.Status == RunStatus.AwaitingUser)
            {
                await TrySetRunStatusAsync(run.RunId, "executing", $"Resuming run to deliver direct steer to @{receipt.Handle}.", cancellationToken).ConfigureAwait(false);
            }
            await EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        return receipt;
    }

    public async Task<TeammateControlReceiptDto> PauseTeammateAsync(
        Guid runId,
        string handle,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var cleanHandle = SanitizeLookupHandle(handle);
        if (string.IsNullOrWhiteSpace(cleanHandle))
            throw new TeammateControlException(400, "INVALID_HANDLE", "Teammate handle is required.");

        await RequireActiveRunForControlAsync(runId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        TeammateControlReceiptDto? receipt = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                {
                    var hub = GetOrCreateActorHub(runId);
                    var cp = hub.LiveCheckpoint ?? await TryReadCheckpointAsync(runId).ConfigureAwait(false)
                        ?? throw new TeammateControlException(404, "NOT_FOUND", $"Checkpoint for run '{runId}' is not initialized yet.");

                    var teammate = ResolveOrMaterializeTeammate(cp, cleanHandle)
                        ?? throw new TeammateControlException(404, "NOT_FOUND", $"Teammate '@{cleanHandle}' was not found in run '{runId}'.");

                    var activeTask = FindActiveTeammateTask(cp, teammate);
                    if (string.Equals(teammate.Status, "terminated", StringComparison.OrdinalIgnoreCase)
                        && activeTask is null
                        && string.Equals(teammate.Mode, "ephemeral", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new TeammateControlException(409, "TEAMMATE_TERMINATED", $"Ephemeral teammate '@{teammate.Handle}' has already terminated.");
                    }

                    teammate.Paused = true;
                    teammate.PausedReason = string.IsNullOrWhiteSpace(reason) ? null : BoundText(reason, 400);
                    teammate.Status = "paused";
                    teammate.UpdatedAt = now;

                    hub.GetMailbox(teammate.Handle).RequestPause();
                    if (activeTask is not null && activeTask.Status is "pending" or "ready" or "waiting")
                    {
                        activeTask.Status = "paused";
                    }

                    cp = await SaveCheckpointAsync(cp, cp.CheckpointRevision, $"teammate-pause-{teammate.Handle}", cancellationToken).ConfigureAwait(false);
                    hub.SignalWake();

                    receipt = new TeammateControlReceiptDto
                    {
                        Accepted = true,
                        RunId = runId,
                        Handle = teammate.Handle,
                        Action = "pause",
                        Status = "paused",
                        Paused = true,
                        CurrentTaskId = teammate.CurrentTaskId ?? activeTask?.TaskId,
                        UpdatedAt = now
                    };
                }
                break;
            }
            catch (RunCheckpointConflictException) when (attempt < 2)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        if (receipt is null)
            throw new TeammateControlException(409, "CHECKPOINT_CONFLICT", "Concurrent run state update conflicted; please retry.");

        await AppendEventAsync(runId, "team.teammate_paused",
            $"Teammate @{receipt.Handle} paused independently.",
            new
            {
                run_id = runId,
                handle = receipt.Handle,
                reason = string.IsNullOrWhiteSpace(reason) ? null : BoundText(reason, 400),
                current_task_id = receipt.CurrentTaskId
            }, cancellationToken, receipt.CurrentTaskId).ConfigureAwait(false);

        return receipt;
    }

    public async Task<TeammateControlReceiptDto> ResumeTeammateAsync(
        Guid runId,
        string handle,
        CancellationToken cancellationToken = default)
    {
        var cleanHandle = SanitizeLookupHandle(handle);
        if (string.IsNullOrWhiteSpace(cleanHandle))
            throw new TeammateControlException(400, "INVALID_HANDLE", "Teammate handle is required.");

        var run = await RequireActiveRunForControlAsync(runId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        TeammateControlReceiptDto? receipt = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using (await EnterCheckpointGateAsync(runId, cancellationToken).ConfigureAwait(false))
                {
                    var hub = GetOrCreateActorHub(runId);
                    var cp = hub.LiveCheckpoint ?? await TryReadCheckpointAsync(runId).ConfigureAwait(false)
                        ?? throw new TeammateControlException(404, "NOT_FOUND", $"Checkpoint for run '{runId}' is not initialized yet.");

                    var teammate = ResolveOrMaterializeTeammate(cp, cleanHandle)
                        ?? throw new TeammateControlException(404, "NOT_FOUND", $"Teammate '@{cleanHandle}' was not found in run '{runId}'.");

                    teammate.Paused = false;
                    teammate.PausedReason = null;
                    hub.GetMailbox(teammate.Handle).ClearPause();

                    var activeTask = FindActiveTeammateTask(cp, teammate);
                    if (activeTask is not null)
                    {
                        if (string.Equals(activeTask.Status, "paused", StringComparison.OrdinalIgnoreCase))
                        {
                            activeTask.Status = HasLivePendingExecution(activeTask) ? "running" : "pending";
                        }
                        teammate.CurrentTaskId = activeTask.TaskId;
                        teammate.Status = IsTaskAwaitingExternalDecision(activeTask) ? "awaiting_approval" : "working";
                    }
                    else if (string.Equals(teammate.Mode, "teammate", StringComparison.OrdinalIgnoreCase))
                    {
                        teammate.Status = "waiting";
                        if (teammate.PendingSteers is { Count: > 0 } queuedSteers)
                        {
                            var firstSteer = queuedSteers[0];
                            teammate.PendingSteers.RemoveAt(0);
                            if (teammate.PendingSteers.Count == 0) teammate.PendingSteers = null;
                            WakeTeammateFromBoard(
                                cp,
                                dispatcherTaskId: null,
                                teammate.Handle,
                                firstSteer.BoardSeq,
                                $"[DIRECT HUMAN STEER{(firstSteer.Urgent ? " · URGENT" : "")}]: {firstSteer.Message}",
                                senderRelayDepth: 0,
                                bypassRelayCap: true);
                        }
                    }
                    else
                    {
                        teammate.Status = "terminated";
                    }

                    teammate.UpdatedAt = now;
                    cp = await SaveCheckpointAsync(cp, cp.CheckpointRevision, $"teammate-resume-{teammate.Handle}", cancellationToken).ConfigureAwait(false);
                    hub.SignalWake();

                    var effectiveStatus = ComputeTeammateEffectiveStatus(cp, teammate);
                    receipt = new TeammateControlReceiptDto
                    {
                        Accepted = true,
                        RunId = runId,
                        Handle = teammate.Handle,
                        Action = "resume",
                        Status = effectiveStatus,
                        Paused = false,
                        CurrentTaskId = teammate.CurrentTaskId ?? activeTask?.TaskId,
                        UpdatedAt = now
                    };
                }
                break;
            }
            catch (RunCheckpointConflictException) when (attempt < 2)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        if (receipt is null)
            throw new TeammateControlException(409, "CHECKPOINT_CONFLICT", "Concurrent run state update conflicted; please retry.");

        await AppendEventAsync(runId, "team.teammate_resumed",
            $"Teammate @{receipt.Handle} resumed ({receipt.Status}).",
            new
            {
                run_id = runId,
                handle = receipt.Handle,
                status = receipt.Status,
                current_task_id = receipt.CurrentTaskId
            }, cancellationToken, receipt.CurrentTaskId).ConfigureAwait(false);

        if (run.Status != RunStatus.Paused && !IsTerminal(run.Status))
        {
            await EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        return receipt;
    }

    internal static IReadOnlyList<TeammateSnapshotDto> ProjectTeammateSnapshots(FullDuplexCheckpointV1? checkpoint)
    {
        if (checkpoint is null) return [];

        var result = new List<TeammateSnapshotDto>();
        var seenHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tm in checkpoint.Teammates)
        {
            seenHandles.Add(tm.Handle);
            var activeTask = FindActiveTeammateTask(checkpoint, tm);
            var lastTask = activeTask ?? checkpoint.Tasks.LastOrDefault(t =>
                string.Equals(t.TeammateHandle ?? t.WorkerHandle, tm.Handle, StringComparison.OrdinalIgnoreCase));
            var effectiveStatus = ComputeTeammateEffectiveStatus(checkpoint, tm);
            var awaitingApproval = (activeTask is not null && IsTaskAwaitingExternalDecision(activeTask))
                || string.Equals(effectiveStatus, "awaiting_approval", StringComparison.OrdinalIgnoreCase);

            result.Add(new TeammateSnapshotDto
            {
                Handle = tm.Handle,
                AgentSlug = tm.AgentSlug,
                Mode = tm.Mode,
                Status = effectiveStatus,
                Paused = tm.Paused,
                AwaitingApproval = awaitingApproval,
                CurrentTaskId = activeTask?.TaskId ?? tm.CurrentTaskId,
                CurrentTaskTitle = activeTask?.Title,
                ClaimedStepId = tm.ClaimedStepId,
                LatestSummary = tm.LastOutcomeSummary ?? lastTask?.ResultSummary ?? activeTask?.Description,
                RuntimeBadge = tm.RuntimeBadge,
                WorktreePath = tm.WorktreePath ?? lastTask?.TaskWorktreePath,
                BranchName = tm.BranchName ?? lastTask?.TaskWorktreeBranch,
                WriteScope = tm.WriteScope ?? lastTask?.WriteScope ?? [],
                RelayDepth = tm.RelayDepth,
                PendingSteerCount = tm.PendingSteers?.Count ?? 0,
                UpdatedAt = tm.UpdatedAt
            });
        }

        // Also project non-coordinator graph workers that were dispatched directly without team_spawn.
        foreach (var task in checkpoint.Tasks)
        {
            if (task.IsCoordinatorTask || task.IsSpacePreparation) continue;
            var handle = task.TeammateHandle ?? task.WorkerHandle;
            if (string.IsNullOrWhiteSpace(handle) || !seenHandles.Add(handle)) continue;

            var awaitingApproval = IsTaskAwaitingExternalDecision(task);
            var paused = string.Equals(task.Status, "paused", StringComparison.OrdinalIgnoreCase);
            var status = paused
                ? "paused"
                : awaitingApproval
                    ? "awaiting_approval"
                    : IsTaskTerminal(task)
                        ? "terminated"
                        : "working";

            result.Add(new TeammateSnapshotDto
            {
                Handle = handle,
                AgentSlug = task.WorkerAgentSlug ?? task.RequestedAgent ?? "worker",
                Mode = task.TeammateMode ?? "ephemeral",
                Status = status,
                Paused = paused,
                AwaitingApproval = awaitingApproval,
                CurrentTaskId = IsTaskTerminal(task) ? null : task.TaskId,
                CurrentTaskTitle = IsTaskTerminal(task) ? null : task.Title,
                ClaimedStepId = null,
                LatestSummary = task.ResultSummary ?? task.Title,
                RuntimeBadge = null,
                WorktreePath = task.TaskWorktreePath,
                BranchName = task.TaskWorktreeBranch,
                WriteScope = task.WriteScope ?? [],
                RelayDepth = 0,
                PendingSteerCount = 0,
                UpdatedAt = task.CompletedAt ?? DateTimeOffset.UtcNow
            });
        }

        return result;
    }

    private async Task<RunState> RequireActiveRunForControlAsync(Guid runId, CancellationToken cancellationToken)
    {
        RunState run;
        try
        {
            run = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            throw new TeammateControlException(404, "NOT_FOUND", $"Run '{runId}' was not found.");
        }

        if (IsTerminal(run.Status) || IsCompletionClaimed(run))
        {
            throw new TeammateControlException(409, "RUN_NOT_ACTIVE", $"Run '{runId}' is already terminal ('{ToStatusVocabulary(run.Status)}').");
        }

        return run;
    }

    private static string SanitizeLookupHandle(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? string.Empty : Uri.UnescapeDataString(raw).Trim().TrimStart('@');

    private static DurableTeammateState? ResolveOrMaterializeTeammate(FullDuplexCheckpointV1 checkpoint, string handle)
    {
        var existing = checkpoint.Teammates.FirstOrDefault(t =>
            string.Equals(t.Handle, handle, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var matchingTask = checkpoint.Tasks.LastOrDefault(t =>
            !t.IsCoordinatorTask
            && !t.IsSpacePreparation
            && (string.Equals(t.TeammateHandle, handle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.WorkerHandle, handle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.TaskKey, handle, StringComparison.OrdinalIgnoreCase)));
        if (matchingTask is null)
            return null;

        var canonicalHandle = matchingTask.TeammateHandle ?? matchingTask.WorkerHandle ?? matchingTask.TaskKey;
        matchingTask.TeammateHandle ??= canonicalHandle;
        var materialized = new DurableTeammateState
        {
            Handle = canonicalHandle,
            AgentSlug = matchingTask.WorkerAgentSlug ?? matchingTask.RequestedAgent ?? "worker",
            Mode = matchingTask.TeammateMode ?? "ephemeral",
            Status = IsTaskTerminal(matchingTask) ? "terminated" : "working",
            WriteScope = matchingTask.WriteScope,
            WorktreePath = matchingTask.TaskWorktreePath,
            BranchName = matchingTask.TaskWorktreeBranch,
            AgentInstanceId = matchingTask.WorkerAgentId,
            CurrentTaskId = IsTaskTerminal(matchingTask) ? null : matchingTask.TaskId,
            LastOutcomeSummary = matchingTask.ResultSummary,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        checkpoint.Teammates.Add(materialized);
        return materialized;
    }

    private static DurableTaskNode? FindActiveTeammateTask(FullDuplexCheckpointV1 checkpoint, DurableTeammateState teammate)
    {
        if (teammate.CurrentTaskId is { } currentId)
        {
            var byId = checkpoint.Tasks.FirstOrDefault(t => t.TaskId == currentId && !IsTaskTerminal(t));
            if (byId is not null) return byId;
        }

        return checkpoint.Tasks.LastOrDefault(t =>
            !IsTaskTerminal(t)
            && string.Equals(t.TeammateHandle ?? t.WorkerHandle, teammate.Handle, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsTaskAwaitingExternalDecision(DurableTaskNode task)
    {
        if (!HasLivePendingExecution(task))
            return false;
        var pendingTurn = task.ToolTurns.LastOrDefault(t => t.ExecutionId == task.PendingToolExecutionId);
        if (pendingTurn?.DispatchStatus is ToolDispatchStatus.AwaitingApproval
            or ToolDispatchStatus.AwaitingDelegate
            or ToolDispatchStatus.AwaitingUser)
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(task.PendingToolApprovalId)
            && pendingTurn?.DispatchStatus is null;
    }

    private static string ComputeTeammateEffectiveStatus(FullDuplexCheckpointV1 checkpoint, DurableTeammateState teammate)
    {
        if (teammate.Paused) return "paused";
        var activeTask = FindActiveTeammateTask(checkpoint, teammate);
        if (activeTask is not null && IsTaskAwaitingExternalDecision(activeTask))
            return "awaiting_approval";
        if (activeTask is not null)
            return "working";
        return string.IsNullOrWhiteSpace(teammate.Status) ? "waiting" : teammate.Status;
    }

    private static void EnqueuePendingSteer(DurableTeammateState teammate, PendingTeammateSteer steer)
    {
        var list = teammate.PendingSteers ??= [];
        if (list.Any(s => string.Equals(s.Id, steer.Id, StringComparison.Ordinal)))
            return;
        if (steer.Urgent)
        {
            var insertIdx = list.FindIndex(s => !s.Urgent);
            if (insertIdx >= 0) list.Insert(insertIdx, steer);
            else list.Add(steer);
        }
        else
        {
            list.Add(steer);
        }
    }
}
