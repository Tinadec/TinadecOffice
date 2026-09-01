using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.DmaEA;

/// <summary>
/// Durable execution owner for full-duplex runs. Admission and HTTP follow operations
/// never execute model work; this hosted service leases and resumes persisted runs.
/// </summary>
public interface IFullDuplexRunEngine
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default);
}

internal sealed class FullDuplexRunEngine : BackgroundService, IFullDuplexRunEngine
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Lets a worker propose a durable context fact without touching shared state
    /// directly. The engine extracts the marker line and applies it as a
    /// CAS-checked patch against the task's input context revision.
    /// </summary>
    private const string WorkerPatchProtocol =
        "\n\nIf this task produced a durable fact, decision, or constraint that later tasks must respect, append exactly one final line in this format: CONTEXT_PATCH: <one-line summary> || <full detail>. Otherwise output no CONTEXT_PATCH line.";
    private const string ContextPatchMarker = "CONTEXT_PATCH:";

    private readonly ILifecycleManager _lifecycle;
    private readonly IConversationStore _conversations;
    private readonly IAgentInstanceService _instances;
    private readonly IContextProvider _contextProvider;
    private readonly IPromptAssembler _promptAssembler;
    private readonly IAgentChatClientFactory _chatClients;
    private readonly IServiceProvider _services;
    private readonly IAgentModelResolver? _modelResolver;
    private readonly IOperationalTriggerEvaluator? _triggerEvaluator;
    private readonly ILoopGuard? _loopGuard;
    private readonly ILongTermMemoryService? _longTermMemory;
    private readonly ILogger<FullDuplexRunEngine> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    // A decision can enqueue a run during the small window in which the prior
    // owner is unwinding. Keep that wake-up durable until the owner has left the
    // running set; otherwise the queue item is consumed and silently discarded.
    private readonly ConcurrentDictionary<Guid, byte> _wakeAfterRun = new();
    private readonly string _ownerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public FullDuplexRunEngine(
        ILifecycleManager lifecycle,
        IConversationStore conversations,
        IAgentInstanceService instances,
        IContextProvider contextProvider,
        IPromptAssembler promptAssembler,
        IAgentChatClientFactory chatClients,
        IServiceProvider services,
        ILogger<FullDuplexRunEngine> logger,
        IAgentModelResolver? modelResolver = null,
        IOperationalTriggerEvaluator? triggerEvaluator = null,
        ILoopGuard? loopGuard = null)
    {
        _lifecycle = lifecycle;
        _conversations = conversations;
        _instances = instances;
        _contextProvider = contextProvider;
        _promptAssembler = promptAssembler;
        _chatClients = chatClients;
        _services = services;
        _modelResolver = modelResolver ?? services.GetService(typeof(IAgentModelResolver)) as IAgentModelResolver;
        _triggerEvaluator = triggerEvaluator ?? services.GetService(typeof(IOperationalTriggerEvaluator)) as IOperationalTriggerEvaluator;
        _loopGuard = loopGuard ?? services.GetService(typeof(ILoopGuard)) as ILoopGuard;
        _longTermMemory = services.GetService(typeof(ILongTermMemoryService)) as ILongTermMemoryService;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (_queued.TryAdd(runId, 0))
        {
            return _queue.Writer.WriteAsync(runId, cancellationToken);
        }
        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new Dictionary<Guid, Task>();
        var nextScan = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextScan)
            {
                try
                {
                    var candidates = await _lifecycle.ListLeaseEligibleRunsAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                    foreach (var candidate in candidates)
                    {
                        if (Guid.TryParse(candidate.RunId, out var runId)) await EnqueueAsync(runId, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    TryLogWarning(ex, "Durable full-duplex recovery scan failed.");
                }
                nextScan = DateTimeOffset.UtcNow.Add(ScanInterval);
            }

            while (_queue.Reader.TryRead(out var runId))
            {
                _queued.TryRemove(runId, out _);
                if (running.ContainsKey(runId))
                {
                    _wakeAfterRun.TryAdd(runId, 0);
                    continue;
                }
                // Calling an async method establishes BackgroundService ownership without
                // using request-bound Task.Run. It starts at its first awaited I/O.
                running[runId] = ExecuteRunAsync(runId, stoppingToken);
            }

            foreach (var completed in running.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToArray())
            {
                try { await running[completed].ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) { TryLogError(ex, "Durable engine task {RunId} ended unexpectedly.", completed); }
                running.Remove(completed);
                if (_wakeAfterRun.TryRemove(completed, out _)
                    && !stoppingToken.IsCancellationRequested)
                {
                    await EnqueueAsync(completed, stoppingToken).ConfigureAwait(false);
                }
            }

            var delay = Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
            try
            {
                await Task.WhenAny(delay, _queue.Reader.WaitToReadAsync(stoppingToken).AsTask()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        try { await Task.WhenAll(running.Values).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task ExecuteRunAsync(Guid runId, CancellationToken stoppingToken)
    {
        // A decision endpoint can wake a run while its previous owner is still
        // completing the final checkpoint/release transaction. Retry briefly so
        // that hand-off does not rely on the delayed recovery scan (which skips
        // young admissions by design).
        RunLease? lease = null;
        for (var attempt = 0; attempt < 20 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            lease = await _lifecycle.TryAcquireRunLeaseAsync(runId.ToString(), _ownerId, LeaseDuration, stoppingToken).ConfigureAwait(false);
            if (lease.Acquired) break;
            await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
        }
        if (lease is null or { Acquired: false }) return;

        try
        {
            var run = await _lifecycle.GetRunStateAsync(runId.ToString(), stoppingToken).ConfigureAwait(false);
            if (IsTerminal(run.Status)) return;
            if (run.Status == RunStatus.Paused) return;

            if (!Guid.TryParse(run.SessionId, out var sessionId)
                || !Guid.TryParse(run.TurnId, out var turnId)
                || !Guid.TryParse(run.TriggerMessageId, out var triggerMessageId))
            {
                await FailLegacyRunAsync(runId, run, "legacy_run_not_resumable", "The run does not have the durable full-duplex identity required for recovery.", stoppingToken).ConfigureAwait(false);
                return;
            }

            var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(runId.ToString(), stoppingToken).ConfigureAwait(false);
            if (frozen is null)
            {
                await FailLegacyRunAsync(runId, run, "legacy_run_not_resumable", "The run has no frozen full-duplex configuration.", stoppingToken).ConfigureAwait(false);
                return;
            }

            FrozenRunConfigurationV1 configuration;
            try
            {
                configuration = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(frozen.Content, JsonOptions)
                    ?? throw new InvalidDataException("Frozen configuration is empty.");
                if (!string.Equals(configuration.ContentHash, frozen.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Frozen configuration hash does not match its stored body.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                await FailLegacyRunAsync(runId, run, "recovery_configuration_invalid", "The frozen run configuration could not be verified.", stoppingToken).ConfigureAwait(false);
                return;
            }

            var messages = await _conversations.ListMessagesAsync(sessionId, cancellationToken: stoppingToken).ConfigureAwait(false);
            var trigger = messages.FirstOrDefault(item => item.Id == triggerMessageId);
            if (trigger is null)
            {
                await FailLegacyRunAsync(runId, run, "recovery_checkpoint_invalid", "The run trigger message is unavailable.", stoppingToken).ConfigureAwait(false);
                return;
            }

            var checkpoint = await LoadOrCreateCheckpointAsync(runId, run, sessionId, turnId, trigger, stoppingToken).ConfigureAwait(false);
            if (checkpoint is null) return;

            // A supervision checkpoint is written before its run status. If a host
            // stops in that small window, repair the status before considering the
            // phase. Never let a recovered escalation fall through to finalization.
            if (!IsTerminal(run.Status)
                && checkpoint.Phase == "awaiting_user"
                && run.Status is not RunStatus.AwaitingUser and not RunStatus.Executing)
            {
                await _lifecycle.SetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review.", stoppingToken).ConfigureAwait(false);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await HeartbeatAsync(runId, stoppingToken).ConfigureAwait(false);
                run = await _lifecycle.GetRunStateAsync(runId.ToString(), stoppingToken).ConfigureAwait(false);
                if (run.Status == RunStatus.Cancelled)
                {
                    await FinalizeCancellationAsync(runId, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
                    return;
                }
                if (run.Status == RunStatus.Paused
                    || run.Status == RunStatus.AwaitingUser && checkpoint.Phase == "awaiting_user")
                {
                    return;
                }
                if (IsTerminal(run.Status)) return;

                if (await ApplyPendingContextPatchesAsync(run, checkpoint, stoppingToken).ConfigureAwait(false))
                {
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "context-patch", stoppingToken).ConfigureAwait(false);
                }

                switch (checkpoint.Phase)
                {
                    case "planning":
                        checkpoint = await PlanAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
                        break;
                    case "executing":
                        checkpoint = await ExecuteReadyTasksAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
                        break;
                    case "reviewing":
                        checkpoint = await ReviewAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
                        break;
                    case "responding":
                        checkpoint = await RespondToInteractionAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
                        break;
                    case "awaiting_user":
                        // A resumed escalation is an explicit user choice to
                        // continue. Corrections are applied above as context
                        // patches and move the checkpoint back to planning.
                        checkpoint.SupervisionDecision = null;
                        checkpoint.SupervisionReasons = [];
                        checkpoint.Phase = "finalizing";
                        await AppendEventAsync(Guid.Parse(run.RunId), "supervision.user_decision", "User chose to continue after supervision escalation.", new
                        {
                            run_id = run.RunId,
                            decision = "continue"
                        }, stoppingToken).ConfigureAwait(false);
                        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "supervision-continued", stoppingToken).ConfigureAwait(false);
                        break;
                    case "finalizing":
                        await FinalizeAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
                        return;
                    default:
                        await FailRunAsync(runId, checkpoint, "recovery_checkpoint_invalid", "The persisted run phase is not recognized.", stoppingToken).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A stopped host leaves its last checkpoint intact for another host.
        }
        catch (RunAwaitingExternalDecisionException)
        {
            // Approval, pause/resume, and unknown-outcome decisions are durable
            // wake-up boundaries. Keep the checkpoint and release the lease; the
            // decision endpoint or recovery scan will enqueue the run again.
        }
        catch (RunCheckpointConflictException)
        {
            // Another owner made progress after a lease hand-off. The scan will load it.
        }
        catch (Exception ex)
        {
            // A logging provider failure must not prevent the durable failure
            // transition below from publishing the terminal stream event.
            TryLogError(ex, "Full-duplex run {RunId} failed in durable engine.", runId);
            var run = await _lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
            var checkpoint = await TryReadCheckpointAsync(runId).ConfigureAwait(false);
            var failureCode = ex is WorkerUnavailableException ? "worker_unavailable" : "runtime";
            if (checkpoint is not null) await FailRunAsync(runId, checkpoint, failureCode, SafeError(ex), CancellationToken.None).ConfigureAwait(false);
            else await FailLegacyRunAsync(runId, run, failureCode, SafeError(ex), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { await _lifecycle.ReleaseRunLeaseAsync(runId.ToString(), _ownerId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { TryLogDebug(ex, "Could not release run lease {RunId}.", runId); }
        }
    }

    private void TryLogWarning(Exception exception, string message, params object[] args)
    {
        try { _logger.LogWarning(exception, message, args); }
        catch { /* logging is advisory; durable state remains authoritative */ }
    }

    private void TryLogError(Exception exception, string message, params object[] args)
    {
        try { _logger.LogError(exception, message, args); }
        catch { /* logging is advisory; durable state remains authoritative */ }
    }

    private void TryLogDebug(Exception exception, string message, params object[] args)
    {
        try { _logger.LogDebug(exception, message, args); }
        catch { /* logging is advisory; durable state remains authoritative */ }
    }

    private async Task<FullDuplexCheckpointV1?> LoadOrCreateCheckpointAsync(
        Guid runId,
        RunState run,
        Guid sessionId,
        Guid turnId,
        ConversationMessage trigger,
        CancellationToken cancellationToken)
    {
        var stored = await _lifecycle.GetCurrentRunCheckpointAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            try
            {
                var checkpoint = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(stored.Content, JsonOptions)
                    ?? throw new InvalidDataException("Checkpoint is empty.");
                checkpoint.CheckpointRevision = stored.Revision;
                return checkpoint;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                await FailLegacyRunAsync(runId, run, "recovery_checkpoint_invalid", "The persisted checkpoint could not be verified.", cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var sessionRevision = await _conversations.GetContextRevisionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var initial = new FullDuplexCheckpointV1
        {
            RunId = runId,
            SessionId = sessionId,
            TurnId = turnId,
            TriggerMessageId = trigger.Id,
            UserGoal = trigger.Content,
            Phase = "planning",
            InteractionKind = "new_task",
            // The trigger message is already appended; take the live session
            // revision so task input revisions observe real context state.
            ContextRevision = Math.Max(sessionRevision, run.ContextRevision),
            PlanRevision = 0
        };
        return await SaveCheckpointAsync(initial, run.CheckpointRevision, "admitted", cancellationToken).ConfigureAwait(false);
    }

    private async Task<FullDuplexCheckpointV1> PlanAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        await _lifecycle.SetRunStatusAsync(run.RunId,
            checkpoint.PlanRevision == 0 ? "understanding" : "replanning",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var agents = await EnsureRootAgentsAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        checkpoint.MeetingAgentId = agents.Meeting.Id;
        checkpoint.PlannerAgentId = agents.Planner.Id;

        var plannerDefinition = RequiredAgent(configuration.ExecutionAgents, "task_planner");
        var context = await BuildContextAsync(run, configuration, plannerDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(plannerDefinition, context, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "context.packed", "Planner context assembled.", new
        {
            evidence_count = context.Evidence.Count,
            estimated_tokens = context.EstimatedTokens,
            token_budget = context.TokenBudget,
            context_revision = checkpoint.ContextRevision
        }, cancellationToken).ConfigureAwait(false);

        PlannedTask[] planned = [];
        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var contextForPlanner = CreateRunContext(run, checkpoint);
                var planner = new PlanningAgent(CreateModelFactory(configuration, checkpoint, plannerDefinition,
                    checkpoint.PlannerAgentId, null), _logger);
                planned = await planner.PlanAsync(
                    contextForPlanner,
                    BuildFrozenPlannerRoster(configuration),
                    PlannerInstructions(checkpoint, assembly.Instructions),
                    cancellationToken).ConfigureAwait(false);
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, planner.LastUsage);
                var materialized = ValidateAndMaterializeGraph(planned, configuration.Spawn.MaxAgentsPerRun);
                checkpoint.Tasks = checkpoint.PlanRevision == 0
                    ? materialized
                    : MergeReplannedGraph(checkpoint.Tasks, materialized);
                SyncLanes(checkpoint);
                lastError = null;
                break;
            }
            catch (InvalidTaskGraphException ex)
            {
                lastError = ex;
            }
        }
        if (lastError is not null)
        {
            await FailRunAsync(Guid.Parse(run.RunId), checkpoint, "invalid_task_graph", lastError.Message, cancellationToken).ConfigureAwait(false);
            return checkpoint;
        }

        checkpoint.PlanRevision++;
        checkpoint.Phase = "executing";
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "planned", cancellationToken).ConfigureAwait(false);
        await _lifecycle.SetRunStatusAsync(run.RunId, "executing", cancellationToken: cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "task_graph.created", $"{checkpoint.Tasks.Count} task(s) planned.", new
        {
            run_id = run.RunId,
            plan_revision = checkpoint.PlanRevision,
            task_count = checkpoint.Tasks.Count,
            task_keys = checkpoint.Tasks.Select(item => item.TaskKey).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.TaskGraphCreated, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private async Task<FullDuplexCheckpointV1> ExecuteReadyTasksAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        // The frozen lanes_enabled switch keeps the single-lane fast path below
        // byte-for-byte: with lanes off, directive and lane machinery stay idle.
        if (configuration.Orchestration.LanesEnabled)
        {
            return await ExecuteLaneTickAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var runId = Guid.Parse(run.RunId);
        await _lifecycle.SetRunStatusAsync(run.RunId, "executing", cancellationToken: cancellationToken).ConfigureAwait(false);

        // A previous worker may have stopped after PrepareAsync persisted an
        // execution. Resume it before asking a model to produce another call.
        var pending = checkpoint.Tasks.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PendingToolExecutionId));
        if (pending is not null)
        {
            var resumed = await ResumePendingToolAsync(run, configuration, checkpoint, pending, cancellationToken).ConfigureAwait(false);
            checkpoint = resumed.Checkpoint;
            if (resumed.Waiting)
            {
                throw new RunAwaitingExternalDecisionException();
            }
            if (resumed.Result is not null)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, resumed.Result, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-resumed", cancellationToken).ConfigureAwait(false);
            }
        }

        MarkBlockedDescendants(checkpoint.Tasks);
        var ready = checkpoint.Tasks
            .Where(item => item.Status is "pending" or "ready")
            .Where(item => item.Dependencies.All(dependency => checkpoint.Tasks.Any(other => other.TaskKey == dependency && other.Status == "completed")))
            .Take(Math.Max(1, configuration.Spawn.MaxParallelWorkers))
            .ToList();

        if (ready.Count == 0)
        {
            if (checkpoint.Tasks.All(item => item.Status is "completed" or "failed" or "blocked"))
            {
                checkpoint.Phase = "reviewing";
                return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "execution-complete", cancellationToken).ConfigureAwait(false);
            }
            if (checkpoint.Tasks.Any(item => !string.IsNullOrWhiteSpace(item.PendingToolExecutionId)))
            {
                throw new RunAwaitingExternalDecisionException();
            }
            await FailRunAsync(runId, checkpoint, "invalid_task_graph", "No dependency-ready task remains in the persisted graph.", cancellationToken).ConfigureAwait(false);
            return checkpoint;
        }

        foreach (var task in ready)
        {
            task.Status = "running";
            task.Attempt++;
            task.InputContextRevision = checkpoint.ContextRevision;
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-dispatched", cancellationToken).ConfigureAwait(false);

        var plannerId = checkpoint.PlannerAgentId ?? throw new InvalidDataException("Planner instance is missing from checkpoint.");
        // Assign workers before any parallel text-only turns. This makes the
        // lineage durable before a model call and avoids concurrent checkpoint CAS
        // writes from independent workers.
        foreach (var task in ready)
        {
            _ = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "workers-assigned", cancellationToken).ConfigureAwait(false);
        // Tool-capable workers are advanced by the single run owner below. Plain
        // model-only workers can still make their first model turn in parallel.
        var textOnly = ready.Where(task => task.RequiredTools.Count == 0).ToList();
        var toolCapable = ready.Where(task => task.RequiredTools.Count != 0).ToList();
        var results = await Task.WhenAll(textOnly.Select(task => ExecuteTextTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken))).ConfigureAwait(false);
        var contextChanged = await ApplyPendingContextPatchesAsync(run, checkpoint, cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, result.Usage);
            if (!contextChanged)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var staleTask = checkpoint.Tasks.FirstOrDefault(item => item.TaskId == result.TaskId);
                if (staleTask is not null)
                {
                    staleTask.StaleEvidence.Add(new StaleTaskEvidence(
                        staleTask.InputContextRevision,
                        result.Status,
                        result.Result.Summary,
                        result.Result.Evidence,
                        DateTimeOffset.UtcNow));
                    staleTask.Status = "pending";
                    staleTask.ResultStatus = null;
                    staleTask.ResultSummary = null;
                    staleTask.Evidence = [];
                    staleTask.CompletedAt = null;
                }
            }
        }

        // Do not resume a worker against context that changed while the parallel
        // text-only turns were in flight. Its task stays pending for replanning.
        if (contextChanged)
        {
            foreach (var task in toolCapable)
            {
                task.Status = "pending";
                task.ResultStatus = null;
                task.ResultSummary = null;
                task.Evidence = [];
                task.CompletedAt = null;
            }
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-stale-after-context", cancellationToken).ConfigureAwait(false);
        }

        foreach (var task in toolCapable)
        {
            var result = await ExecuteToolTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
            checkpoint = result.Checkpoint;
            if (result.Waiting)
            {
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            if (result.Result is not null)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result.Result, cancellationToken).ConfigureAwait(false);
            }
        }

        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision,
            "tasks-completed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One multi-lane tick: every non-terminal lane advances at most one step.
    /// Worker concurrency is budgeted globally across lanes, and a lane blocked
    /// on another lane's unfinished work parks as "waiting" instead of failing
    /// the run as an invalid graph. All lanes stay inside the single run lease.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> ExecuteLaneTickAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        await _lifecycle.SetRunStatusAsync(run.RunId, "executing", cancellationToken: cancellationToken).ConfigureAwait(false);
        SyncLanes(checkpoint);

        // A previous worker may have stopped after PrepareAsync persisted an
        // execution. Resume it before asking a model to produce another call.
        var pending = checkpoint.Tasks.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PendingToolExecutionId));
        if (pending is not null)
        {
            var resumed = await ResumePendingToolAsync(run, configuration, checkpoint, pending, cancellationToken).ConfigureAwait(false);
            checkpoint = resumed.Checkpoint;
            if (resumed.Waiting)
            {
                checkpoint = await MarkWaitingLanesAsync(runId, checkpoint, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken, LaneKeyOf(pending)).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            if (resumed.Result is not null)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, resumed.Result, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-resumed", cancellationToken, LaneKeyOf(pending)).ConfigureAwait(false);
            }
        }

        MarkBlockedDescendants(checkpoint.Tasks);
        SyncLanes(checkpoint);

        // Per-lane ready sets dispatched under one global worker budget so the
        // active worker peak across lanes never exceeds MaxParallelWorkers.
        var budget = Math.Max(1, configuration.Spawn.MaxParallelWorkers);
        var laneOrder = checkpoint.Lanes
            .Where(lane => checkpoint.Tasks.Any(task => LaneKeyOf(task) == lane.LaneKey && task.Status is "pending" or "ready" or "running"))
            .Select(lane => lane.LaneKey)
            .OrderBy(key => string.Equals(key, "main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dispatched = new List<(string LaneKey, DurableTaskNode Task)>();
        var progress = true;
        while (dispatched.Count < budget && progress)
        {
            progress = false;
            foreach (var laneKey in laneOrder)
            {
                if (dispatched.Count >= budget) break;
                var ready = checkpoint.Tasks
                    .Where(item => LaneKeyOf(item) == laneKey
                        && item.Status is "pending" or "ready"
                        && item.Dependencies.All(dependency => checkpoint.Tasks.Any(other => other.TaskKey == dependency && other.Status == "completed")))
                    .OrderBy(item => item.Priority).ThenBy(item => item.TaskKey, StringComparer.Ordinal)
                    .FirstOrDefault(item => dispatched.All(pair => pair.Task != item));
                if (ready is null) continue;
                dispatched.Add((laneKey, ready));
                progress = true;
            }
        }

        if (dispatched.Count == 0)
        {
            if (checkpoint.Tasks.All(item => item.Status is "completed" or "failed" or "blocked"))
            {
                checkpoint.Phase = "reviewing";
                return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "execution-complete", cancellationToken).ConfigureAwait(false);
            }
            if (checkpoint.Tasks.Any(item => !string.IsNullOrWhiteSpace(item.PendingToolExecutionId)))
            {
                checkpoint = await MarkWaitingLanesAsync(runId, checkpoint, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting", cancellationToken).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            checkpoint = await MarkWaitingLanesAsync(runId, checkpoint, cancellationToken).ConfigureAwait(false);
            // Anything still dispatchable-but-not-waiting is genuinely stuck: a
            // crashed "running" dispatch or an in-lane chain planning validation
            // failed to exclude. That stays an invalid graph.
            var stuck = checkpoint.Tasks.Any(item => item.Status is "pending" or "ready" or "running"
                && checkpoint.Lanes.First(lane => string.Equals(lane.LaneKey, LaneKeyOf(item), StringComparison.OrdinalIgnoreCase)).Status != "waiting");
            if (stuck)
            {
                await FailRunAsync(runId, checkpoint, "invalid_task_graph", "No dependency-ready task remains in the persisted graph.", cancellationToken).ConfigureAwait(false);
                return checkpoint;
            }
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lanes-waiting", cancellationToken).ConfigureAwait(false);
            throw new RunAwaitingExternalDecisionException();
        }

        foreach (var (_, task) in dispatched)
        {
            task.Status = "running";
            task.Attempt++;
            task.InputContextRevision = checkpoint.ContextRevision;
            task.Waits = [];
        }
        foreach (var lane in checkpoint.Lanes)
        {
            if (dispatched.Any(pair => pair.LaneKey == lane.LaneKey)) lane.Status = "executing";
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-dispatched", cancellationToken).ConfigureAwait(false);

        var plannerId = checkpoint.PlannerAgentId ?? throw new InvalidDataException("Planner instance is missing from checkpoint.");
        foreach (var (_, task) in dispatched)
        {
            _ = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "workers-assigned", cancellationToken).ConfigureAwait(false);

        var textOnly = dispatched.Where(pair => pair.Task.RequiredTools.Count == 0).Select(pair => pair.Task).ToList();
        var toolCapable = dispatched.Where(pair => pair.Task.RequiredTools.Count != 0).Select(pair => pair.Task).ToList();
        var results = await Task.WhenAll(textOnly.Select(task => ExecuteTextTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken))).ConfigureAwait(false);
        var contextChanged = await ApplyPendingContextPatchesAsync(run, checkpoint, cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, result.Usage);
            if (!contextChanged)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var staleTask = checkpoint.Tasks.FirstOrDefault(item => item.TaskId == result.TaskId);
                if (staleTask is not null)
                {
                    staleTask.StaleEvidence.Add(new StaleTaskEvidence(
                        staleTask.InputContextRevision,
                        result.Status,
                        result.Result.Summary,
                        result.Result.Evidence,
                        DateTimeOffset.UtcNow));
                    staleTask.Status = "pending";
                    staleTask.ResultStatus = null;
                    staleTask.ResultSummary = null;
                    staleTask.Evidence = [];
                    staleTask.CompletedAt = null;
                }
            }
        }

        if (contextChanged)
        {
            foreach (var task in toolCapable)
            {
                task.Status = "pending";
                task.ResultStatus = null;
                task.ResultSummary = null;
                task.Evidence = [];
                task.CompletedAt = null;
            }
            SyncLanes(checkpoint);
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-stale-after-context", cancellationToken).ConfigureAwait(false);
        }

        foreach (var task in toolCapable)
        {
            var result = await ExecuteToolTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
            checkpoint = result.Checkpoint;
            if (result.Waiting)
            {
                checkpoint = await MarkWaitingLanesAsync(runId, checkpoint, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken, LaneKeyOf(task)).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            if (result.Result is not null)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result.Result, cancellationToken).ConfigureAwait(false);
            }
        }

        SyncLanes(checkpoint);
        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lane-tick", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parks lanes that cannot advance because their unmet dependencies live in
    /// other lanes' unfinished work. Emits one transition event per newly
    /// waiting lane; already-waiting lanes only refresh their Waits roster.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> MarkWaitingLanesAsync(
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        foreach (var lane in checkpoint.Lanes)
        {
            var waits = CrossLaneWaitsFor(checkpoint.Tasks, lane.LaneKey);
            if (waits is null) continue;

            if (!string.Equals(lane.Status, "waiting", StringComparison.Ordinal))
            {
                lane.Status = "waiting";
                await AppendEventAsync(runId, "orchestration.lane_waiting",
                    $"Lane '{lane.LaneKey}' is waiting on other lanes.",
                    new { lane_key = lane.LaneKey, waits = waits }, cancellationToken).ConfigureAwait(false);
            }
            foreach (var task in checkpoint.Tasks.Where(item => LaneKeyOf(item) == lane.LaneKey && item.Status is "pending" or "ready"))
            {
                task.Waits = [.. waits];
            }
        }
        return checkpoint;
    }

    /// <summary>
    /// The lanes a lane must wait on, or null when the lane is not waiting
    /// material: terminal, still dispatching (running or parked on a tool
    /// decision), without dispatchable tasks, blocked only inside itself, or
    /// blocked on a dependency that no planned task satisfies (a planning error
    /// that stays an invalid graph).
    /// </summary>
    internal static List<string>? CrossLaneWaitsFor(IReadOnlyList<DurableTaskNode> tasks, string laneKey)
    {
        var laneTasks = tasks.Where(item => LaneKeyOf(item) == laneKey).ToList();
        if (laneTasks.Count == 0) return null;
        if (laneTasks.All(item => item.Status is "completed" or "failed" or "blocked")) return null;
        if (laneTasks.Any(item => item.Status == "running" || !string.IsNullOrWhiteSpace(item.PendingToolExecutionId))) return null;
        var waitingTasks = laneTasks.Where(item => item.Status is "pending" or "ready").ToList();
        if (waitingTasks.Count == 0) return null;

        var waits = waitingTasks
            .SelectMany(item => item.Dependencies)
            .Where(dependency => !tasks.Any(other => other.TaskKey == dependency && other.Status == "completed"))
            .Select(dependency => tasks.FirstOrDefault(other => other.TaskKey == dependency))
            .OfType<DurableTaskNode>()
            .Select(other => LaneKeyOf(other))
            .Where(key => !string.Equals(key, laneKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return waits.Count == 0 ? null : waits;
    }

    private static string LaneKeyOf(DurableTaskNode task) =>
        string.IsNullOrWhiteSpace(task.LaneKey) ? "main" : task.LaneKey.Trim();

    private static void SyncLanes(FullDuplexCheckpointV1 checkpoint)
    {
        foreach (var group in checkpoint.Tasks.GroupBy(item => LaneKeyOf(item), StringComparer.OrdinalIgnoreCase))
        {
            var lane = checkpoint.Lanes.FirstOrDefault(item => string.Equals(item.LaneKey, group.Key, StringComparison.OrdinalIgnoreCase));
            if (lane is null)
            {
                lane = new DurableLane { LaneKey = group.Key, Status = "pending" };
                checkpoint.Lanes.Add(lane);
            }
            if (group.All(item => item.Status == "completed")) lane.Status = "completed";
        }
    }

    private async Task<TaskExecutionResult> ExecuteTextTaskAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        try
        {
            var worker = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
            var model = await GetWorkerModelTurnAsync(run, configuration, checkpoint, task, worker, [], cancellationToken).ConfigureAwait(false);
            if (!model.IsAvailable || model.Error is not null)
            {
                var failed = new StepResult { TaskNodeId = task.TaskId, AgentId = worker.Id.ToString("N"), Status = "failed", Summary = model.Error ?? "Worker model is unavailable.", Evidence = [] };
                return new TaskExecutionResult(task.TaskId, worker.Id, "failed", failed, model.Usage);
            }
            if (model.Calls.Count != 0)
            {
                var failed = new StepResult { TaskNodeId = task.TaskId, AgentId = worker.Id.ToString("N"), Status = "failed", Summary = "The model requested a tool that was not advertised to this worker.", Evidence = [] };
                return new TaskExecutionResult(task.TaskId, worker.Id, "failed", failed, model.Usage);
            }
            var result = string.IsNullOrWhiteSpace(model.Text)
                ? new StepResult { TaskNodeId = task.TaskId, AgentId = worker.Id.ToString("N"), Status = "failed", Summary = "Execution returned no output.", Evidence = [] }
                : BuildCompletedStepResult(task.TaskId, worker.Id, model.Text);
            return new TaskExecutionResult(task.TaskId, worker.Id, result.Status == "completed" ? "completed" : "failed", result, model.Usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new StepResult { TaskNodeId = task.TaskId, Status = "failed", Summary = SafeError(ex), Evidence = [] };
            return new TaskExecutionResult(task.TaskId, null, "failed", failed);
        }
    }

    /// <summary>
    /// Resumes the durable tool loop for a task whose execution id was already
    /// checkpointed. The method deliberately delegates to the same serial loop
    /// used for a fresh worker so a restart cannot regenerate a write call.
    /// </summary>
    private Task<ToolTaskExecutionResult> ResumePendingToolAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        CancellationToken cancellationToken) =>
        ExecuteToolTaskAsync(run, configuration, checkpoint, checkpoint.PlannerAgentId ?? Guid.Empty, task, cancellationToken);

    private async Task<ToolTaskExecutionResult> ExecuteToolTaskAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken)
    {
        var worker = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
        var dispatcher = _services.GetRequiredService<IToolDispatcher>();
        var executionCoordinator = _services.GetRequiredService<IToolExecutionCoordinator>();
        var roundLimit = configuration.Tools.ResolveTaskRoundLimit(task.Category, task.Risk);
        IReadOnlyList<WorkerToolDescriptor> descriptors;
        try
        {
            descriptors = await GetWorkerToolsAsync(run, configuration, task, worker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailedToolTask(checkpoint, task, worker, "tool_manifest_unavailable", SafeError(ex));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            // At most one unresolved call is resumed at a time. A model response
            // may contain multiple calls, but each call gets its own durable
            // prepare/resume boundary and approval decision.
            var pendingTurn = task.ToolTurns.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.ResultJson));
            if (pendingTurn is not null)
            {
                ToolDispatchResultDto dispatch;
                if (string.IsNullOrWhiteSpace(pendingTurn.ExecutionId))
                {
                    if (!TryParseJsonObject(pendingTurn.ArgumentsJson, out var parameters))
                    {
                        return FailedToolTask(checkpoint, task, worker, "invalid_tool_arguments", "The worker returned invalid tool arguments.");
                    }

                    var toolCallKey = $"run:{run.RunId}:task:{task.TaskKey}:attempt:{task.Attempt}:round:{task.ToolRounds}:call:{pendingTurn.CallId}";
                    dispatch = await dispatcher.PrepareAsync(new ToolDispatchRequestDto
                    {
                        RunId = run.RunId,
                        TaskId = task.TaskId.ToString(),
                        AgentInstanceId = worker.Id.ToString(),
                        ToolId = pendingTurn.ToolId,
                        ToolCallKey = toolCallKey,
                        LeaseUses = Math.Max(1, roundLimit - task.ToolRounds + 1),
                        Params = parameters
                    }, cancellationToken).ConfigureAwait(false);

                    pendingTurn.ExecutionId = dispatch.ExecutionId;
                    pendingTurn.ApprovalId = dispatch.ApprovalId;
                    pendingTurn.DispatchStatus = dispatch.Status;
                    task.PendingToolExecutionId = dispatch.ExecutionId;
                    task.PendingToolApprovalId = dispatch.ApprovalId;
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-prepared", cancellationToken).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(dispatch.ExecutionId))
                    {
                        return FailedToolTask(checkpoint, task, worker, dispatch.ErrorCategory ?? "tool_prepare_failed", dispatch.Message ?? "The tool call could not be prepared.");
                    }
                }

                dispatch = await dispatcher.ResumeAsync(pendingTurn.ExecutionId!, cancellationToken).ConfigureAwait(false);
                pendingTurn.DispatchStatus = dispatch.Status;
                if (dispatch.Status is ToolDispatchStatus.AwaitingApproval or ToolDispatchStatus.AwaitingResume
                    or ToolDispatchStatus.AwaitingDelegate or ToolDispatchStatus.AwaitingUser)
                {
                    if (dispatch.Status is ToolDispatchStatus.AwaitingApproval or ToolDispatchStatus.AwaitingDelegate or ToolDispatchStatus.AwaitingUser)
                    {
                        var runStatus = dispatch.Status switch
                        {
                            ToolDispatchStatus.AwaitingDelegate => "awaiting_delegate",
                            ToolDispatchStatus.AwaitingUser => "awaiting_user",
                            _ => "awaiting_approval"
                        };
                        await _lifecycle.SetRunStatusAsync(run.RunId, runStatus, dispatch.Message, cancellationToken).ConfigureAwait(false);
                    }
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting", cancellationToken).ConfigureAwait(false);
                    return new ToolTaskExecutionResult(checkpoint, Waiting: true, Result: null);
                }
                if (dispatch.Status == ToolDispatchStatus.OutcomeUnknown)
                {
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-outcome-unknown", cancellationToken).ConfigureAwait(false);
                    return new ToolTaskExecutionResult(checkpoint, Waiting: true, Result: null);
                }
                if (dispatch.Status != ToolDispatchStatus.Completed)
                {
                    var summary = dispatch.Message ?? $"Tool '{pendingTurn.ToolId}' returned {dispatch.Status}.";
                    return FailedToolTask(checkpoint, task, worker, dispatch.ErrorCategory ?? dispatch.Status, summary);
                }

                var resultJson = dispatch.Result?.GetRawText();
                if (string.IsNullOrWhiteSpace(resultJson))
                {
                    var snapshot = await executionCoordinator.FindAsync(Guid.Parse(pendingTurn.ExecutionId!), cancellationToken).ConfigureAwait(false);
                    resultJson = snapshot?.ResultJson ?? "null";
                }
                pendingTurn.ResultJson = resultJson;
                pendingTurn.DispatchStatus = ToolDispatchStatus.Completed;
                task.PendingToolExecutionId = null;
                task.PendingToolApprovalId = null;
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-result", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var model = await GetWorkerModelTurnAsync(run, configuration, checkpoint, task, worker, descriptors, cancellationToken).ConfigureAwait(false);
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, model.Usage);
            if (!model.IsAvailable || model.Error is not null)
            {
                return FailedToolTask(checkpoint, task, worker, "model_unavailable", model.Error ?? "Worker model is unavailable.");
            }
            if (model.Calls.Count == 0)
            {
                var status = string.IsNullOrWhiteSpace(model.Text) ? "failed" : "completed";
                var stepResult = string.IsNullOrWhiteSpace(model.Text)
                    ? new StepResult
                    {
                        TaskNodeId = task.TaskId,
                        AgentId = worker.Id.ToString("N"),
                        Status = status,
                        Summary = "Execution returned no output.",
                        Evidence = []
                    }
                    : BuildCompletedStepResult(task.TaskId, worker.Id, model.Text);
                return new ToolTaskExecutionResult(checkpoint, Waiting: false,
                    new TaskExecutionResult(task.TaskId, worker.Id, stepResult.Status, stepResult));
            }

            task.ToolRounds++;
            if (task.ToolRounds > roundLimit)
            {
                return FailedToolTask(checkpoint, task, worker, "tool_round_limit", $"The worker exceeded the effective max_tool_rounds limit ({roundLimit}).");
            }
            if (_loopGuard is { } guard && task.ToolRounds > configuration.Tools.MaxToolRounds)
            {
                // The task is running past the frozen global default only because a
                // category/risk override raised its limit; every extra round must
                // clear the loop guard before another call is dispatched.
                var fingerprints = task.ToolTurns
                    .Where(turn => !string.IsNullOrWhiteSpace(turn.ResultJson))
                    .Select(turn => $"{turn.ToolId}:{turn.ArgumentsJson}")
                    .ToArray();
                var decision = await guard.EvaluateAsync(
                    run.SessionId.ToString(),
                    run.RunId.ToString(),
                    new LoopGuardContext
                    {
                        // ToolRounds counts the round about to dispatch and the
                        // engine allows ToolRounds == roundLimit, but the F#
                        // iteration check rejects on >=; report completed rounds
                        // so the guard's boundary matches the engine's `>`.
                        Iteration = task.ToolRounds - 1,
                        MaxIterations = roundLimit,
                        ToolCallCount = task.ToolTurns.Count,
                        RecentToolCallFingerprints = fingerprints
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!decision.ShouldContinue)
                {
                    return FailedToolTask(checkpoint, task, worker, "tool_loop_detected",
                        decision.Reason ?? "The loop guard rejected further tool rounds for this task.");
                }
            }

            var existingCallIds = task.ToolTurns.Select(item => item.CallId).ToHashSet(StringComparer.Ordinal);
            foreach (var call in model.Calls)
            {
                if (!existingCallIds.Add(call.CallId))
                {
                    return FailedToolTask(checkpoint, task, worker, "duplicate_tool_call", $"The worker reused tool call id '{call.CallId}'.");
                }
                task.ToolTurns.Add(new WorkerToolTurn
                {
                    CallId = call.CallId,
                    ToolId = call.ToolId,
                    ArgumentsJson = call.ArgumentsJson,
                    AssistantText = task.ToolTurns.Count == 0 ? model.Text : null
                });
            }

            // The assistant call transcript is durable before the first external
            // side effect. A host crash here simply re-enters PrepareAsync with the
            // same call id and idempotency key.
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-calls", cancellationToken).ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<IReadOnlyList<WorkerToolDescriptor>> GetWorkerToolsAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        CancellationToken cancellationToken)
    {
        if (task.RequiredTools.Count == 0) return [];
        // The declaration surface is the intersection of this instance's grant and the
        // run-frozen manifest.  The live provider manifest is deliberately not consulted:
        // a tool that appeared in a child process after admission is not authority for
        // what a worker may be told it can call.
        var catalog = _services.GetRequiredService<IFrozenToolManifestCatalog>();
        var authorized = await catalog.ListAuthorizedAsync(
            Guid.Parse(run.RunId), task.TaskId, worker.Id, cancellationToken).ConfigureAwait(false);
        var byId = authorized.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);

        var entries = new List<WorkerToolDescriptor>(task.RequiredTools.Count);
        foreach (var required in task.RequiredTools)
        {
            if (!byId.TryGetValue(required, out var entry))
                throw new InvalidDataException($"Task '{task.TaskKey}' requires tool '{required}', which is not authorized for the assigned worker instance.");
            entries.Add(new WorkerToolDescriptor(entry.Id, entry.Description, entry.InputSchema));
        }
        return entries;
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
        WorkerSelection selected;
        try
        {
            selected = ResolveOrSelectWorker(configuration, task);
        }
        catch (WorkerUnavailableException)
        {
            await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.CapabilityMissing, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug))
        {
            task.WorkerAgentSlug = selected.Agent.Id;
            task.WorkerAgentDefinitionId = selected.Agent.AgentDefinitionId;
            task.WorkerAgentVersionId = selected.Agent.AgentVersionId;
            task.WorkerAgentVersionHash = selected.Agent.VersionContentHash;
            task.WorkerAssignmentReason = selected.Reason;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-selected", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "worker.assigned", $"Task assigned to {selected.Agent.Id}.", new
            {
                task_id = task.TaskId,
                task_key = task.TaskKey,
                agent_slug = selected.Agent.Id,
                agent_definition_id = selected.Agent.AgentDefinitionId,
                agent_version_id = selected.Agent.AgentVersionId,
                agent_version_hash = selected.Agent.VersionContentHash,
                reason = selected.Reason,
                required_capabilities = task.RequiredCapabilities,
                required_tools = task.RequiredTools
            }, cancellationToken, task.TaskId).ConfigureAwait(false);
        }
        var instances = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (task.WorkerAgentId is { } assigned)
        {
            var existing = instances.FirstOrDefault(item => item.Id == assigned);
            if (existing is not null)
            {
                VerifyWorkerInstance(existing, selected.Agent, task);
                return existing;
            }
        }

        var worker = instances.FirstOrDefault(item => item.Generated
            && item.TaskId == task.TaskId
            && item.AgentVersionId == selected.Agent.AgentVersionId
            && item.Status is "created" or "running");
        if (worker is null)
        {
            var parent = instances.FirstOrDefault(item => item.Id == plannerId)
                ?? throw new InvalidDataException("Planner instance is missing from the run lineage.");
            if (selected.Agent.AgentDefinitionId is not { } definitionId
                || selected.Agent.AgentVersionId is not { } versionId
                || string.IsNullOrWhiteSpace(selected.Agent.VersionContentHash))
                throw new InvalidDataException($"Frozen specialist '{selected.Agent.Id}' has no immutable version binding.");
            worker = await _instances.SpawnAsync(new AgentSpawnRequest(
                parent.Id,
                string.IsNullOrWhiteSpace(task.Description) ? task.Title : task.Description,
                task.SuccessCriteria,
                ["session_history", "task_context", "reviewed_memory"],
                "chat",
                task.RequiredTools,
                ["workspace"],
                configuration.Context.DefaultTokenBudget,
                task.TaskId,
                selected.Agent.Role,
                new AgentSpawnLimits(configuration.Spawn.MaxDepth, configuration.Spawn.MaxAgentsPerRun, configuration.Spawn.MaxParallelWorkers),
                Template: new FrozenAgentTemplate(
                    selected.Agent.Id,
                    selected.Agent.Layer,
                    selected.Agent.Role,
                    selected.Agent.Capabilities,
                    selected.Agent.AllowedTools,
                    definitionId,
                    versionId,
                    selected.Agent.VersionContentHash)), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.created", "Execution worker created.", new
            {
                agent_instance_id = worker.Id,
                parent_instance_id = worker.ParentInstanceId,
                task_id = task.TaskId,
                agent_slug = selected.Agent.Id,
                agent_definition_id = worker.AgentDefinitionId,
                agent_version_id = worker.AgentVersionId,
                layer = worker.Layer,
                role = worker.Role
            }, cancellationToken, task.TaskId).ConfigureAwait(false);
            try { await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(checkpoint.TurnId, "ephemeral_agent", null, IdempotencyKey: $"run:{runId}:ephemeral:{worker.Id}"), cancellationToken).ConfigureAwait(false); } catch { }
        }
        VerifyWorkerInstance(worker, selected.Agent, task);

        if (task.WorkerAgentId != worker.Id)
        {
            task.WorkerAgentId = worker.Id;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-assigned", cancellationToken).ConfigureAwait(false);
        }
        return worker;
    }

    private async Task<WorkerModelTurn> GetWorkerModelTurnAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        IReadOnlyList<WorkerToolDescriptor> tools,
        CancellationToken cancellationToken)
    {
        var workerDefinition = GetAssignedWorkerDefinition(configuration, task);
        var context = await BuildContextAsync(run, configuration, workerDefinition.Id,
            $"Task: {task.Title}\nDescription: {task.Description}\nSuccess criteria: {string.Join("; ", task.SuccessCriteria)}",
            cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(workerDefinition, context, cancellationToken).ConfigureAwait(false);
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
        return await new ExecutionAgent(CreateModelFactory(configuration, checkpoint, workerDefinition,
            worker.Id, worker.ParentInstanceId), _logger).GetNextTurnAsync(
            CreateRunContext(run, checkpoint),
            agent,
            ToPlannedTask(task),
            task.ToolTurns,
            tools,
            assembly.Instructions + WorkerPatchProtocol,
            configuration.Context.RecentMessageLimit,
            cancellationToken).ConfigureAwait(false);
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

        var candidates = configuration.ExecutionAgents
            .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
            .Where(agent => agent.Id.StartsWith("worker.", StringComparison.Ordinal)
                || agent.Role is "task_executor" or "git_specialist")
            .Select(agent =>
            {
                ValidateFrozenAgent(agent, "worker");
                var tools = ExpandFrozenTools(agent.AllowedTools, manifestTools);
                var capabilities = agent.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isGeneral = string.Equals(agent.Id, "worker.general", StringComparison.Ordinal);
                var coversTools = requiredTools.All(tools.Contains);
                var coversCapabilities = requiredCapabilities.All(capabilities.Contains);
                var eligible = coversTools && coversCapabilities;
                if (requiredTools.Count == 0 && requiredCapabilities.Count == 0) eligible = eligible && isGeneral;
                return new WorkerCandidate(agent, tools, capabilities, isGeneral, eligible);
            })
            .Where(candidate => candidate.Eligible)
            .OrderBy(candidate => candidate.IsGeneral)
            .ThenBy(candidate => Math.Max(0, candidate.Tools.Count - requiredTools.Count))
            .ThenBy(candidate => Math.Max(0, candidate.Capabilities.Count - requiredCapabilities.Count))
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
            ? requiredTools.Count == 0 && requiredCapabilities.Count == 0 ? "general_default" : "general_fallback"
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

    private static IReadOnlyList<AgentDefinition> BuildFrozenPlannerRoster(FrozenRunConfigurationV1 configuration) =>
        configuration.ExecutionAgents
            .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
            .Where(agent => agent.Id.StartsWith("worker.", StringComparison.Ordinal)
                || agent.Role is "task_executor" or "git_specialist")
            .OrderBy(agent => agent.RosterOrder)
            .ThenBy(agent => agent.Id, StringComparer.Ordinal)
            .Select(agent => new AgentDefinition
            {
                Id = agent.AgentDefinitionId ?? Guid.Empty,
                Name = agent.Id,
                Layer = agent.Layer,
                AgentType = agent.Role,
                Capabilities = agent.Capabilities,
                AllowedTools = agent.AllowedTools,
                Enabled = agent.Enabled
            })
            .ToArray();

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
        return definition ?? throw new InvalidDataException($"Task '{task.TaskKey}' worker assignment is not present in the frozen roster.");
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
            throw new InvalidDataException($"Worker instance '{instance.Id}' does not match task '{task.TaskKey}' frozen assignment.");
    }

    private static RuntimeAgentDefinition RequiredAgent(
        IReadOnlyList<RuntimeAgentDefinition> agents,
        string slug)
    {
        var agent = agents.SingleOrDefault(item => string.Equals(item.Id, slug, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Frozen profile has no '{slug}' agent.");
        if (!agent.Enabled) throw new InvalidDataException($"Frozen agent '{slug}' is disabled.");
        ValidateFrozenAgent(agent, slug);
        return agent;
    }

    private static void ValidateFrozenAgent(RuntimeAgentDefinition agent, string purpose)
    {
        if (agent.AgentDefinitionId is null || agent.AgentVersionId is null || string.IsNullOrWhiteSpace(agent.VersionContentHash))
            throw new InvalidDataException($"Frozen {purpose} agent '{agent.Id}' has no immutable version binding.");
    }

    private async Task<PromptAssemblyResult> AssemblePromptAsync(
        RuntimeAgentDefinition definition,
        ContextPack context,
        CancellationToken cancellationToken) =>
        await _promptAssembler.AssembleAsync(new FrozenPromptAssemblyRequest(
            definition.Id,
            context,
            definition.SystemPrompt,
            definition.AgentVersionId,
            definition.VersionContentHash,
            definition.PromptPipelineId,
            definition.PromptVersionId,
            definition.PromptVersionContentHash,
            definition.PromptGraphJson), cancellationToken).ConfigureAwait(false);

    private IAgentChatClientFactory CreateModelFactory(
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RuntimeAgentDefinition definition,
        Guid? instanceId,
        Guid? parentInstanceId)
    {
        var resolver = _modelResolver ?? throw new InvalidOperationException("The agent model resolver is not registered.");
        var plan = definition.ModelPlan ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no model plan.");
        var definitionId = definition.AgentDefinitionId ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no definition id.");
        var versionId = definition.AgentVersionId ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no version id.");
        var modeVersionId = configuration.Bindings.Single(binding => binding.ConfigurationKind == "agent_mode_version").ConfigurationVersionId;
        return new ModelInvocationChatFactory(resolver, _chatClients, plan, new ModelInvocationContext(
            checkpoint.SessionId, checkpoint.RunId, checkpoint.TurnId, instanceId, parentInstanceId,
            definitionId, versionId, modeVersionId, plan.StrategySource));
    }

    private async Task ApplyTaskResultAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        TaskExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var task = checkpoint.Tasks.FirstOrDefault(item => item.TaskId == execution.TaskId)
            ?? throw new InvalidDataException($"Task '{execution.TaskId}' is missing from the checkpoint.");
        task.Status = execution.Status;
        task.ResultStatus = execution.Result.Status;
        task.ResultSummary = execution.Result.Summary;
        task.Evidence = execution.Result.Evidence.ToList();
        task.CompletedAt = execution.Status is "completed" or "failed" or "blocked" ? DateTimeOffset.UtcNow : null;
        if (execution.WorkerAgentId is { } workerId) task.WorkerAgentId = workerId;

        await _lifecycle.UpdateTaskSnapshotAsync(runId, new
        {
            id = task.TaskId,
            task_key = task.TaskKey,
            run_id = runId,
            title = task.Title,
            description = task.Description,
            status = task.Status,
            priority = task.Priority,
            risk = task.Risk,
            success_criteria = task.SuccessCriteria,
            dependencies = task.Dependencies,
            required_capabilities = task.RequiredCapabilities,
            required_tools = task.RequiredTools,
            worker_agent_id = task.WorkerAgentId,
            result_summary = task.ResultSummary,
            evidence = task.Evidence,
            updated_at = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
        var eventType = execution.Status == "completed" ? "worker.completed" : "worker.failed";
        await AppendEventAsync(runId, eventType, execution.Result.Summary, new
        {
            task_id = task.TaskId,
            task_key = task.TaskKey,
            agent_instance_id = execution.WorkerAgentId,
            status = execution.Result.Status,
            summary = execution.Result.Summary,
            evidence = execution.Result.Evidence
        }, cancellationToken, task.TaskId).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(execution.Result.ProposedPatchContent))
        {
            // Arbitration per the full-duplex contract: the patch is accepted only
            // while the session context is still at the revision the task observed.
            // A stale result loses the race and the patch stays audit-only.
            var patchOutcome = await _conversations.ApplyContextPatchAsync(new ContextPatchRequest(
                Guid.Parse(run.SessionId),
                task.InputContextRevision,
                execution.Result.ProposedPatchContent,
                string.IsNullOrWhiteSpace(execution.Result.ProposedPatchSummary)
                    ? $"Worker patch for task '{task.TaskKey}'."
                    : execution.Result.ProposedPatchSummary,
                checkpoint.RunId,
                execution.WorkerAgentId,
                Kind: "supplement"), cancellationToken).ConfigureAwait(false);
            var patchApplied = string.Equals(patchOutcome.Status, "applied", StringComparison.OrdinalIgnoreCase);
            await AppendEventAsync(runId, patchApplied ? "context.patch.accepted" : "context.patch.stale",
                patchApplied
                    ? $"Worker patch for task '{task.TaskKey}' applied."
                    : $"Worker patch for task '{task.TaskKey}' was based on stale context and kept for audit.", new
                {
                    task_id = task.TaskId,
                    task_key = task.TaskKey,
                    source = "worker",
                    agent_instance_id = execution.WorkerAgentId,
                    patch_id = patchOutcome.PatchId,
                    base_context_revision = task.InputContextRevision,
                    context_revision = patchApplied ? patchOutcome.AppliedRevision : null
                }, cancellationToken, task.TaskId).ConfigureAwait(false);
        }

        if (execution.Status == "completed")
        {
            await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.TaskCompleted, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ToolTaskExecutionResult FailedToolTask(
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        string category,
        string message) =>
        new(checkpoint, Waiting: false, new TaskExecutionResult(task.TaskId, worker.Id, "failed", new StepResult
        {
            TaskNodeId = task.TaskId,
            AgentId = worker.Id.ToString("N"),
            Status = "failed",
            Summary = message,
            Evidence = [$"error_category:{category}"]
        }));

    private static bool TryParseJsonObject(string json, out JsonElement parameters)
    {
        parameters = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) return false;
            parameters = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<FullDuplexCheckpointV1> ReviewAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        await _lifecycle.SetRunStatusAsync(run.RunId, "reviewing", cancellationToken: cancellationToken).ConfigureAwait(false);
        var plans = checkpoint.Tasks.Select(ToPlannedTask).ToArray();
        var results = checkpoint.Tasks.Select(item => new StepResult
        {
            TaskNodeId = item.TaskId,
            AgentId = item.WorkerAgentId?.ToString() ?? string.Empty,
            Status = item.ResultStatus ?? item.Status,
            Summary = item.ResultSummary ?? string.Empty,
            Evidence = item.Evidence
        }).ToArray();
        await AppendEventAsync(runId, "supervision.requested", "Supervision reviewing execution evidence.", new
        {
            run_id = run.RunId,
            revision_round = checkpoint.SupervisionRound,
            result_count = results.Length
        }, cancellationToken).ConfigureAwait(false);
        SupervisionVerdict verdict;
        if (configuration.Supervision.RequiredBeforeFinal)
        {
            var supervisorDefinition = RequiredAgent(configuration.OperationAgents, "supervisor");
            var supervisorInstance = await EnsureSupervisorAgentAsync(run, configuration, checkpoint, supervisorDefinition, cancellationToken).ConfigureAwait(false);
            checkpoint.SupervisorAgentId = supervisorInstance.Id;
            var context = await BuildContextAsync(run, configuration, supervisorDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
            var assembly = await AssemblePromptAsync(supervisorDefinition, context, cancellationToken).ConfigureAwait(false);
            var supervisor = new SupervisionAgent(CreateModelFactory(configuration, checkpoint, supervisorDefinition,
                supervisorInstance.Id, supervisorInstance.ParentInstanceId), _logger);
            verdict = await supervisor.ReviewAsync(checkpoint.UserGoal, plans, results, checkpoint.SupervisionRound, assembly.Instructions, cancellationToken).ConfigureAwait(false);
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, supervisor.LastUsage);
        }
        else
        {
            verdict = new SupervisionVerdict(SupervisionDecision.Pass, [], []);
        }
        checkpoint.SupervisionDecision = verdict.DecictionString();
        checkpoint.SupervisionReasons = verdict.Reasons.ToList();
        await AppendEventAsync(runId, "supervision.completed", $"Supervision decision: {checkpoint.SupervisionDecision}.", new
        {
            decision = checkpoint.SupervisionDecision,
            revision_round = checkpoint.SupervisionRound,
            reasons = checkpoint.SupervisionReasons,
            revise_task_indexes = verdict.ReviseTaskIndexes
        }, cancellationToken).ConfigureAwait(false);

        if (verdict.Decision == SupervisionDecision.Escalate)
        {
            // Escalation is a durable user-review gate, not a successful terminal
            // result. Persist the phase before changing the aggregate status so a
            // restart cannot accidentally continue the run without a decision.
            checkpoint.Phase = "awaiting_user";
            checkpoint.MeetingResponse = null;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "supervision-escalated", cancellationToken).ConfigureAwait(false);
            await _lifecycle.SetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review before this run can finish.", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "supervision.user_review.requested", "Supervision escalated the run and is waiting for a user decision.", new
            {
                run_id = run.RunId,
                decision = "escalate",
                reasons = checkpoint.SupervisionReasons,
                options = new[] { "continue", "correct", "cancel" }
            }, cancellationToken).ConfigureAwait(false);
            return checkpoint;
        }

        if (verdict.Decision == SupervisionDecision.Revise && checkpoint.SupervisionRound < configuration.Supervision.MaxRevisionRounds)
        {
            foreach (var index in verdict.ReviseTaskIndexes.Where(index => index >= 0 && index < checkpoint.Tasks.Count))
            {
                var node = checkpoint.Tasks[index];
                node.Status = "pending";
                node.ResultStatus = null;
                node.ResultSummary = null;
                node.Evidence = [];
            }
            checkpoint.SupervisionRound++;
            checkpoint.Phase = "executing";
            await _lifecycle.SetRunStatusAsync(run.RunId, "replanning", cancellationToken: cancellationToken).ConfigureAwait(false);
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "replanned", cancellationToken).ConfigureAwait(false);
        }

        checkpoint.Phase = "finalizing";
        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "reviewed", cancellationToken).ConfigureAwait(false);
    }

    private async Task<FullDuplexCheckpointV1> RespondToInteractionAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        await _lifecycle.SetRunStatusAsync(run.RunId, "executing", cancellationToken: cancellationToken).ConfigureAwait(false);
        checkpoint.MeetingResponse = checkpoint.InteractionKind switch
        {
            "status_query" => await BuildStatusResponseAsync(checkpoint, cancellationToken).ConfigureAwait(false),
            _ => await GenerateInteractionResponseAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false)
        };
        checkpoint.SupervisionDecision = "pass";
        checkpoint.Phase = "finalizing";
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "interaction-response", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "meeting.interaction.responded", "A non-execution meeting interaction was completed.", new
        {
            interaction_kind = checkpoint.InteractionKind,
            target_run_id = checkpoint.TargetRunId,
            context_revision = checkpoint.ContextRevision
        }, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private async Task<string> GenerateInteractionResponseAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var meetingDefinition = RequiredAgent(configuration.OperationAgents, "meeting");
        var context = await BuildContextAsync(run, configuration, meetingDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var factory = CreateModelFactory(configuration, checkpoint, meetingDefinition, checkpoint.MeetingAgentId, null);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(meetingDefinition, context, cancellationToken).ConfigureAwait(false);

        var targetSummary = "No active target run.";
        if (checkpoint.TargetRunId is { } targetRunId)
        {
            var target = await _lifecycle.GetRunStateAsync(targetRunId.ToString(), cancellationToken).ConfigureAwait(false);
            targetSummary = $"Target run {target.RunId} is {target.Status.ToString().ToLowerInvariant()} at context revision {target.ContextRevision}.";
        }
        var instructions = assembly.Instructions
            + "\n\nYou are the meeting agent, the only user-facing agent. The user sent a follow-up message (interaction kind: "
            + checkpoint.InteractionKind
            + ") while another run may be active. Respond directly and honestly in the user's language: acknowledge the message, state its impact on the active task, and keep it short. Do not claim tools ran.";
        var prompt = $"Interaction kind: {checkpoint.InteractionKind}\nUser message:\n{checkpoint.UserGoal}\n\n{targetSummary}";
        using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            "operation.meeting",
            "meeting",
            "Produces the only formal user-facing response from governed evidence.",
            new ChatOptions { Instructions = instructions });
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(
            checkpoint.ModelUsage,
            Maf18RuntimeAdapter.NormalizeUsage(response.Usage));
        if (string.IsNullOrWhiteSpace(response.Text)) throw new InvalidOperationException("Meeting agent returned no output.");
        return response.Text;
    }

    private async Task<string> BuildStatusResponseAsync(FullDuplexCheckpointV1 checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.TargetRunId is not { } targetRunId)
        {
            return "I need the active run id to provide its status.";
        }

        var target = await _lifecycle.GetRunStateAsync(targetRunId.ToString(), cancellationToken).ConfigureAwait(false);
        return $"Run {target.RunId} is {target.Status.ToString().ToLowerInvariant()} at context revision {target.ContextRevision}.";
    }

    private async Task<bool> ApplyPendingContextPatchesAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var patches = await _conversations.ListAppliedContextPatchesAsync(
            checkpoint.SessionId,
            checkpoint.RunId,
            checkpoint.ContextRevision,
            cancellationToken).ConfigureAwait(false);
        if (patches.Count == 0) return false;

        foreach (var patch in patches)
        {
            checkpoint.ContextRevision = patch.AppliedRevision;
            await _lifecycle.AdvanceRunContextRevisionAsync(run.RunId, patch.AppliedRevision, cancellationToken).ConfigureAwait(false);
            // Any user context supplied while a supervision escalation is
            // pending is a correction decision. Replan from the new evidence;
            // do not leave the checkpoint in the continuation-only phase.
            var supervisionCorrection = checkpoint.Phase == "awaiting_user"
                && string.Equals(checkpoint.SupervisionDecision, "escalate", StringComparison.OrdinalIgnoreCase);
            if (patch.Kind == "goal_adjustment" || supervisionCorrection)
            {
                if (patch.Kind == "goal_adjustment") checkpoint.UserGoal = patch.Content;
                checkpoint.Phase = "planning";
                checkpoint.SupervisionDecision = null;
                checkpoint.SupervisionReasons = [];
                checkpoint.MeetingResponse = null;
                await AppendEventAsync(Guid.Parse(run.RunId), "context.goal_adjusted", "The active goal changed and will be replanned.", new
                {
                    patch_id = patch.Id,
                    base_context_revision = patch.BaseRevision,
                    context_revision = patch.AppliedRevision,
                    plan_revision = checkpoint.PlanRevision
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AppendEventAsync(Guid.Parse(run.RunId), "context.supplement_applied", "Supplemental context was applied to the active run.", new
                {
                    patch_id = patch.Id,
                    base_context_revision = patch.BaseRevision,
                    context_revision = patch.AppliedRevision
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        return true;
    }

    private async Task FinalizeAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);

        // This is a defensive invariant for legacy or partially committed
        // checkpoints. An unresolved supervision escalation must never produce a
        // meeting response or terminal success, even if its phase was corrupted
        // to finalizing during recovery.
        if (checkpoint.SupervisionDecision == "escalate")
        {
            checkpoint.Phase = "awaiting_user";
            checkpoint.MeetingResponse = null;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "supervision-escalated", cancellationToken).ConfigureAwait(false);
            if (run.Status != RunStatus.AwaitingUser)
            {
                await _lifecycle.SetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review before this run can finish.", cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(checkpoint.MeetingResponse))
        {
            // A cancellation can land while the run is executing or parked before
            // finalization. Re-check before producing the user-facing response and
            // again after it (the meeting call itself can be slow) so a cancelled
            // run never streams a completed result.
            var stateBeforeMeeting = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            if (stateBeforeMeeting.Status == RunStatus.Cancelled)
            {
                await FinalizeCancellationAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            var meetingDefinition = RequiredAgent(configuration.OperationAgents, "meeting");
            var context = await BuildContextAsync(run, configuration, meetingDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
            checkpoint.MeetingResponse = await GenerateMeetingResponseAsync(configuration, checkpoint, meetingDefinition, context, cancellationToken).ConfigureAwait(false);
            var stateAfterMeeting = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            if (stateAfterMeeting.Status == RunStatus.Cancelled)
            {
                checkpoint.MeetingResponse = null;
                await FinalizeCancellationAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "meeting-response", cancellationToken).ConfigureAwait(false);
        }

        var meetingResponse = checkpoint.MeetingResponse ?? throw new InvalidDataException("Meeting response is missing from finalization checkpoint.");
        await _lifecycle.AppendRunStreamAsync(run.RunId, new DurableRunStreamAppend(
            checkpoint.TurnId,
            "delta",
            Delta: meetingResponse,
            IdempotencyKey: $"run:{run.RunId}:turn:{checkpoint.TurnId}:meeting:delta:0"), cancellationToken).ConfigureAwait(false);

        if (checkpoint.AssistantMessageId is null)
        {
            var assistant = await _conversations.AppendMessageAsync(checkpoint.SessionId, "assistant", meetingResponse,
                runId, checkpoint.TurnId, $"run:{run.RunId}:turn:{checkpoint.TurnId}:assistant:v1", cancellationToken).ConfigureAwait(false);
            checkpoint.AssistantMessageId = assistant.Id;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "assistant-persisted", cancellationToken).ConfigureAwait(false);
        }

        var revision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        await _conversations.CompleteTurnAsync(checkpoint.TurnId, runId, checkpoint.AssistantMessageId, revision, "completed", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "user.response", "Meeting agent response persisted.", new
        {
            message_id = checkpoint.AssistantMessageId,
            character_count = meetingResponse.Length,
            supervision_decision = checkpoint.SupervisionDecision
        }, cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendRunStreamAsync(run.RunId, new DurableRunStreamAppend(
            checkpoint.TurnId,
            "done",
            checkpoint.AssistantMessageId,
            UsageJson: Maf18RuntimeAdapter.SerializeUsage(checkpoint.ModelUsage),
            FinishReason: "completed",
            IdempotencyKey: $"run:{run.RunId}:turn:{checkpoint.TurnId}:done"), cancellationToken).ConfigureAwait(false);
        await _lifecycle.CompleteRunAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        checkpoint = await DrainRunDirectivesAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.RunFinalized, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        await _instances.ReleaseRunInstancesAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinalizeCancellationAsync(
        Guid runId,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var state = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        var revision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        await _conversations.CompleteTurnAsync(checkpoint.TurnId, runId, null, revision, "cancelled", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "task.cancelled", "The run was cancelled.", new { run_id = runId }, cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            checkpoint.TurnId,
            "done",
            FinishReason: "cancelled",
            IdempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:cancelled"), cancellationToken).ConfigureAwait(false);
        await DrainRunDirectivesAsync(state, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        await _instances.ReleaseRunInstancesAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Run-terminal drain of orchestration directives queued while this run held
    /// the session. Lanes-disabled runs reject every directive; lanes-enabled
    /// runs accept and mark them consumed for the M4 orchestration port. The
    /// pending-status filter makes a replayed finalize idempotent.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> DrainRunDirectivesAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var pending = await _lifecycle.ListPendingRunDirectivesAsync(checkpoint.RunId, cancellationToken).ConfigureAwait(false);
        foreach (var directive in pending)
        {
            if (!configuration.Orchestration.LanesEnabled)
            {
                await AppendEventAsync(runId, "orchestration.directive.rejected",
                    "A queued directive was rejected because lanes are disabled for this run.",
                    new { directive_id = directive.Id, kind = directive.Kind, code = "lanes_disabled" }, cancellationToken).ConfigureAwait(false);
                await _lifecycle.DrainRunDirectivesAsync(checkpoint.RunId, [directive.Id], "rejected", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AppendEventAsync(runId, "orchestration.directive.accepted",
                    "A queued directive was accepted at run terminal.",
                    new { directive_id = directive.Id, kind = directive.Kind }, cancellationToken).ConfigureAwait(false);
                await _lifecycle.DrainRunDirectivesAsync(checkpoint.RunId, [directive.Id], "drained", cancellationToken).ConfigureAwait(false);
            }
            checkpoint.DirectiveCursor++;
        }
        if (pending.Count > 0)
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "directives-drained", cancellationToken).ConfigureAwait(false);
        }
        return checkpoint;
    }

    private async Task FailRunAsync(Guid runId, FullDuplexCheckpointV1 checkpoint, string category, string message, CancellationToken cancellationToken)
    {
        var run = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (!IsTerminal(run.Status))
        {
            await _lifecycle.SetRunStatusAsync(runId.ToString(), "failed", message, cancellationToken).ConfigureAwait(false);
        }
        var revision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        await _conversations.CompleteTurnAsync(checkpoint.TurnId, runId, null, revision, "failed", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "run.failed", message, new { run_id = runId, error_category = category }, cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            checkpoint.TurnId,
            "error",
            ErrorCategory: category,
            SafeErrorMessage: message,
            IdempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:error:{category}"), cancellationToken).ConfigureAwait(false);
    }

    private async Task FailLegacyRunAsync(Guid runId, RunState run, string category, string message, CancellationToken cancellationToken)
    {
        if (!IsTerminal(run.Status)) await _lifecycle.SetRunStatusAsync(runId.ToString(), "failed", message, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "run.failed", message, new { run_id = runId, error_category = category }, cancellationToken).ConfigureAwait(false);
        if (Guid.TryParse(run.TurnId, out var turnId))
        {
            await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
                turnId,
                "error",
                ErrorCategory: category,
                SafeErrorMessage: message,
                IdempotencyKey: $"run:{runId}:turn:{turnId}:error:{category}"), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<FullDuplexCheckpointV1> SaveCheckpointAsync(
        FullDuplexCheckpointV1 checkpoint,
        long expectedRevision,
        string key,
        CancellationToken cancellationToken,
        string laneKey = "main")
    {
        // The lane segment keeps same-purpose saves from distinct lanes from
        // colliding on one idempotency row, which would return the first body
        // and silently drop the second lane's update.
        var stored = await _lifecycle.SaveRunCheckpointAsync(checkpoint.RunId.ToString(), new RunCheckpointWrite(
            expectedRevision,
            checkpoint.Phase,
            JsonSerializer.Serialize(checkpoint, JsonOptions),
            IdempotencyKey: $"run:{checkpoint.RunId}:{laneKey}:{key}:{expectedRevision + 1}"), cancellationToken).ConfigureAwait(false);
        checkpoint.CheckpointRevision = stored.Revision;
        return checkpoint;
    }

    private async Task<FullDuplexCheckpointV1?> TryReadCheckpointAsync(Guid runId)
    {
        var stored = await _lifecycle.GetCurrentRunCheckpointAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
        if (stored is null) return null;
        var checkpoint = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(stored.Content, JsonOptions);
        if (checkpoint is not null) checkpoint.CheckpointRevision = stored.Revision;
        return checkpoint;
    }

    private async Task<(RuntimeAgentInstance Meeting, RuntimeAgentInstance Planner)> EnsureRootAgentsAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var sessionId = Guid.Parse(run.SessionId);
        var all = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var meeting = checkpoint.MeetingAgentId is { } meetingId ? all.FirstOrDefault(item => item.Id == meetingId) : null;
        var planner = checkpoint.PlannerAgentId is { } plannerId ? all.FirstOrDefault(item => item.Id == plannerId) : null;
        var meetingDefinition = RequiredAgent(configuration.OperationAgents, "meeting");
        var plannerDefinition = RequiredAgent(configuration.ExecutionAgents, "task_planner");
        meeting ??= all.FirstOrDefault(item => !item.Generated && item.AgentVersionId == meetingDefinition.AgentVersionId);
        planner ??= all.FirstOrDefault(item => !item.Generated && item.AgentVersionId == plannerDefinition.AgentVersionId);
        if (meeting is not null) VerifyRootInstance(meeting, meetingDefinition, checkpoint.MeetingAgentId, "meeting");
        if (planner is not null) VerifyRootInstance(planner, plannerDefinition, checkpoint.PlannerAgentId, "task planner");
        if (meeting is null)
        {
            meeting = await _instances.CreateRootAsync(new RuntimeAgentSeed(sessionId, runId, meetingDefinition.Id, meetingDefinition.Layer,
                meetingDefinition.Role, "chat", meetingDefinition.Capabilities, meetingDefinition.AllowedTools, ["workspace"], configuration.Context.DefaultTokenBudget,
                AgentDefinitionId: meetingDefinition.AgentDefinitionId,
                AgentVersionId: meetingDefinition.AgentVersionId,
                VersionContentHash: meetingDefinition.VersionContentHash), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.created", "Meeting agent created.", new { agent_instance_id = meeting.Id, layer = meeting.Layer, role = meeting.Role }, cancellationToken).ConfigureAwait(false);
        }
        if (planner is null)
        {
            planner = await _instances.CreateRootAsync(new RuntimeAgentSeed(sessionId, runId, plannerDefinition.Id, plannerDefinition.Layer,
                plannerDefinition.Role, "chat", plannerDefinition.Capabilities, plannerDefinition.AllowedTools, ["workspace"], configuration.Context.DefaultTokenBudget,
                AgentDefinitionId: plannerDefinition.AgentDefinitionId,
                AgentVersionId: plannerDefinition.AgentVersionId,
                VersionContentHash: plannerDefinition.VersionContentHash), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.created", "Task planning agent created.", new { agent_instance_id = planner.Id, layer = planner.Layer, role = planner.Role }, cancellationToken).ConfigureAwait(false);
        }
        return (meeting, planner);
    }

    private sealed record WorkerCandidate(
        RuntimeAgentDefinition Agent,
        HashSet<string> Tools,
        HashSet<string> Capabilities,
        bool IsGeneral,
        bool Eligible);

    internal sealed record WorkerSelection(RuntimeAgentDefinition Agent, string Reason);

    private async Task<RuntimeAgentInstance> EnsureSupervisorAgentAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RuntimeAgentDefinition definition,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var all = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var supervisor = checkpoint.SupervisorAgentId is { } instanceId
            ? all.FirstOrDefault(item => item.Id == instanceId)
            : null;
        supervisor ??= all.FirstOrDefault(item => !item.Generated && item.AgentVersionId == definition.AgentVersionId);
        if (supervisor is not null)
        {
            VerifyRootInstance(supervisor, definition, checkpoint.SupervisorAgentId, "supervisor");
            return supervisor;
        }

        supervisor = await _instances.CreateRootAsync(new RuntimeAgentSeed(
            Guid.Parse(run.SessionId),
            runId,
            definition.Id,
            definition.Layer,
            definition.Role,
            "chat",
            definition.Capabilities,
            definition.AllowedTools,
            ["workspace"],
            configuration.Context.DefaultTokenBudget,
            AgentDefinitionId: definition.AgentDefinitionId,
            AgentVersionId: definition.AgentVersionId,
            VersionContentHash: definition.VersionContentHash), cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "agent.created", "Supervision agent created.", new
        {
            agent_instance_id = supervisor.Id,
            agent_slug = definition.Id,
            agent_definition_id = supervisor.AgentDefinitionId,
            agent_version_id = supervisor.AgentVersionId,
            layer = supervisor.Layer,
            role = supervisor.Role
        }, cancellationToken).ConfigureAwait(false);
        return supervisor;
    }

    private static void VerifyRootInstance(
        RuntimeAgentInstance instance,
        RuntimeAgentDefinition definition,
        Guid? checkpointInstanceId,
        string purpose)
    {
        if (instance.Generated
            || instance.AgentDefinitionId != definition.AgentDefinitionId
            || instance.AgentVersionId != definition.AgentVersionId
            || !string.Equals(instance.AgentVersionContentHash, definition.VersionContentHash, StringComparison.OrdinalIgnoreCase)
            || checkpointInstanceId is { } expectedId && instance.Id != expectedId)
        {
            throw new InvalidDataException($"Persisted {purpose} instance does not match its frozen agent version.");
        }
    }

    private async Task<ContextPack> BuildContextAsync(RunState run, FrozenRunConfigurationV1 config, string agentId, string taskContext, CancellationToken cancellationToken) =>
        await _contextProvider.BuildContextAsync(new ContextBuildRequest(
            run.SessionId,
            run.RunId,
            config.ApplicationMode,
            config.AgentMode,
            config.RuntimeProfileId,
            agentId,
            taskContext,
            config.Context.DefaultTokenBudget,
            config.Context.RecentMessageLimit,
            config.Memory.RetrievalLimit), cancellationToken).ConfigureAwait(false);

    private async Task<string> GenerateMeetingResponseAsync(
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RuntimeAgentDefinition meetingDefinition,
        ContextPack context,
        CancellationToken cancellationToken)
    {
        var factory = CreateModelFactory(configuration, checkpoint, meetingDefinition, checkpoint.MeetingAgentId, null);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(meetingDefinition, context, cancellationToken).ConfigureAwait(false);
        var escalation = checkpoint.SupervisionDecision == "escalate"
            ? "\n\nIMPORTANT: supervision escalated this run. Explain the unresolved decision clearly and ask the user for direction."
            : string.Empty;
        var evidence = string.Join("\n", checkpoint.Tasks.Select(item => $"- [{item.ResultStatus ?? item.Status}] {item.ResultSummary}"));
        var instructions = assembly.Instructions + "\n\nYou are the meeting agent, the only user-facing agent. Reply directly and honestly in the user's language. Summarize completed work, evidence, limits, and next action. Do not claim tools ran if evidence does not say so." + escalation;
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nExecution evidence:\n{evidence}";
        using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            "operation.meeting",
            "meeting",
            "Produces the only formal user-facing response from governed evidence.",
            new ChatOptions { Instructions = instructions });
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(
            checkpoint.ModelUsage,
            Maf18RuntimeAdapter.NormalizeUsage(response.Usage));
        if (string.IsNullOrWhiteSpace(response.Text)) throw new InvalidOperationException("Meeting agent returned no output.");
        return response.Text;
    }

    private static string PlannerInstructions(FullDuplexCheckpointV1 checkpoint, string baseInstructions)
    {
        if (checkpoint.RecommendedCapabilities.Count == 0) return baseInstructions;
        var lines = string.Join("\n", checkpoint.RecommendedCapabilities.Select(item =>
            $"- {item.Skill} (confidence: {item.Confidence}): {item.Reason}"));
        return baseInstructions
            + "\n\nCapability recommendations from the operation layer (advisory; accept or reject as you see fit):\n"
            + lines;
    }

    internal static List<DurableTaskNode> ValidateAndMaterializeGraph(PlannedTask[] tasks, int maxTasks)
    {
        if (tasks.Length == 0) throw new InvalidTaskGraphException("The planner returned no tasks.");
        if (tasks.Length > maxTasks) throw new InvalidTaskGraphException($"The planner returned {tasks.Length} tasks, above the frozen limit of {maxTasks}.");
        var candidates = new List<(PlannedTask Task, string Key, string Lane)>();
        // Task keys are unique within a lane, not globally: two lanes may each
        // carry their own "test" task. Dependencies still resolve globally so a
        // lane may dock behind another lane's task.
        var seen = new HashSet<(string Lane, string Key)>();
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Title)) throw new InvalidTaskGraphException("Every planned task needs a title.");
            var key = NormalizeTaskKey(task.TaskKey, task.Title);
            var lane = string.IsNullOrWhiteSpace(task.LaneKey) ? "main" : task.LaneKey.Trim();
            if (!seen.Add((lane, key))) throw new InvalidTaskGraphException($"Task key '{key}' is not unique within lane '{lane}'.");
            candidates.Add((task, key, lane));
        }
        var aliases = candidates.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!aliases.ContainsKey(candidate.Task.Title)) aliases[candidate.Task.Title] = candidate.Key;
        }
        var result = candidates.Select(candidate => new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = candidate.Key,
            Title = candidate.Task.Title.Trim(),
            Description = candidate.Task.Description,
            Category = string.IsNullOrWhiteSpace(candidate.Task.Category) ? null : candidate.Task.Category.Trim(),
            SuccessCriteria = candidate.Task.SuccessCriteria.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
            Dependencies = candidate.Task.Dependencies.Select(value => aliases.TryGetValue(value, out var dependency) ? dependency : throw new InvalidTaskGraphException($"Task '{candidate.Key}' references unknown dependency '{value}'.")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiredCapabilities = candidate.Task.RequiredCapabilities.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiredTools = candidate.Task.RequiredTools.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Priority = candidate.Task.Priority,
            Risk = string.IsNullOrWhiteSpace(candidate.Task.Risk) ? "medium" : candidate.Task.Risk.Trim(),
            Status = "pending",
            LaneKey = candidate.Lane
        }).ToList();
        var byKey = result.GroupBy(item => item.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<(string? Lane, string Key)>();
        var visited = new HashSet<(string? Lane, string Key)>();
        foreach (var node in result) Visit(node);
        return result;

        void Visit(DurableTaskNode node)
        {
            var identity = (node.LaneKey, node.TaskKey);
            if (visited.Contains(identity)) return;
            if (!visiting.Add(identity)) throw new InvalidTaskGraphException("The task dependency graph contains a cycle.");
            foreach (var dependency in node.Dependencies)
            {
                foreach (var target in byKey[dependency]) Visit(target);
            }
            visiting.Remove(identity);
            visited.Add(identity);
        }
    }

    internal static List<DurableTaskNode> MergeReplannedGraph(
        IReadOnlyList<DurableTaskNode> existing,
        IReadOnlyList<DurableTaskNode> replacement)
    {
        var completed = existing
            .Where(item => item.Status == "completed")
            .ToDictionary(item => item.TaskKey, StringComparer.OrdinalIgnoreCase);
        return replacement.Select(item =>
        {
            if (!completed.TryGetValue(item.TaskKey, out var prior)) return item;
            return new DurableTaskNode
            {
                TaskId = prior.TaskId,
                TaskKey = item.TaskKey,
                Title = item.Title,
                Description = item.Description,
                Category = item.Category,
                SuccessCriteria = item.SuccessCriteria,
                Dependencies = item.Dependencies,
                RequiredCapabilities = item.RequiredCapabilities,
                RequiredTools = item.RequiredTools,
                Priority = item.Priority,
                Risk = item.Risk,
                Status = prior.Status,
                Attempt = prior.Attempt,
                // Tool-round state below was silently dropped on replan until it
                // was added alongside the lane fields; it is runtime state just
                // like Status and must survive a replan for completed tasks.
                ToolRounds = prior.ToolRounds,
                ToolTurns = prior.ToolTurns,
                PendingToolExecutionId = prior.PendingToolExecutionId,
                PendingToolApprovalId = prior.PendingToolApprovalId,
                WorkerAgentId = prior.WorkerAgentId,
                WorkerAgentSlug = prior.WorkerAgentSlug,
                WorkerAgentDefinitionId = prior.WorkerAgentDefinitionId,
                WorkerAgentVersionId = prior.WorkerAgentVersionId,
                WorkerAgentVersionHash = prior.WorkerAgentVersionHash,
                WorkerAssignmentReason = prior.WorkerAssignmentReason,
                InputContextRevision = prior.InputContextRevision,
                ResultStatus = prior.ResultStatus,
                ResultSummary = prior.ResultSummary,
                Evidence = prior.Evidence,
                StaleEvidence = prior.StaleEvidence,
                CompletedAt = prior.CompletedAt,
                LaneKey = prior.LaneKey,
                Waits = prior.Waits,
                CriteriaVerdicts = prior.CriteriaVerdicts
            };
        }).ToList();
    }

    private static void MarkBlockedDescendants(IReadOnlyList<DurableTaskNode> tasks)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var task in tasks.Where(item => item.Status is "pending" or "ready"))
            {
                if (task.Dependencies.Any(dependency => tasks.Any(other => other.TaskKey == dependency && other.Status is "failed" or "blocked")))
                {
                    task.Status = "blocked";
                    task.ResultStatus = "blocked";
                    task.ResultSummary = "A dependency did not complete.";
                    changed = true;
                }
            }
        }
    }

    private static PlannedTask ToPlannedTask(DurableTaskNode node) => new()
    {
        TaskKey = node.TaskKey,
        Title = node.Title,
        Description = node.Description,
        SuccessCriteria = node.SuccessCriteria.ToArray(),
        Dependencies = node.Dependencies.ToArray(),
        RequiredCapabilities = node.RequiredCapabilities.ToArray(),
        RequiredTools = node.RequiredTools.ToArray(),
        Priority = node.Priority,
        Risk = node.Risk
    };

    private static DmaeaRunContext CreateRunContext(RunState run, FullDuplexCheckpointV1 checkpoint) => new()
    {
        RunId = checkpoint.RunId,
        SessionId = checkpoint.SessionId,
        TriggerMessageId = checkpoint.TriggerMessageId,
        TenantId = Guid.TryParse(run.TenantId, out var tenantId) ? tenantId : Guid.Empty,
        WorkspaceId = Guid.TryParse(run.WorkspaceId, out var workspaceId) ? workspaceId : Guid.Empty,
        UserGoal = checkpoint.UserGoal
    };

    private async Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!await _lifecycle.HeartbeatRunLeaseAsync(runId.ToString(), _ownerId, LeaseDuration, cancellationToken).ConfigureAwait(false))
        {
            throw new RunCheckpointConflictException(runId.ToString(), 0, 0);
        }
    }

    private async Task EvaluateAndDispatchOperationsAsync(
        OperationalTriggerPoint point,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        if (_triggerEvaluator is null || !configuration.Triggers.Enabled) return;
        IReadOnlyList<OperationalTriggerMatch> matches;
        try
        {
            matches = _triggerEvaluator.Evaluate(point, configuration);
        }
        catch (Exception ex)
        {
            TryLogWarning(ex, "Operational trigger evaluation failed for run {RunId}.", run.RunId);
            return;
        }
        if (matches.Count == 0) return;

        foreach (var match in matches)
        {
            try
            {
                await DispatchOperationalRoleAsync(match, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Operational roles are advisory bypass calls. A failure records
                // evidence and continues; it must never fail the run.
                TryLogWarning(ex, "Operational role '{Agent}' dispatch failed for run {RunId}.", match.Agent.Id, run.RunId);
                try
                {
                    await AppendEventAsync(Guid.Parse(run.RunId), "operation.dispatch.failed",
                        $"Operational role '{match.Agent.Id}' failed: {SafeError(ex)}", new
                        {
                            run_id = run.RunId,
                            agent_slug = match.Agent.Id,
                            trigger_point = point.ToString(),
                            trigger_name = match.TriggerName
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception eventEx)
                {
                    TryLogDebug(eventEx, "Could not record operational dispatch failure for run {RunId}.", run.RunId);
                }
            }
        }
    }

    private Task DispatchOperationalRoleAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken) =>
        OperationalRoleKey(match.Agent) switch
        {
            "context_compressor" => DispatchContextCompressionAsync(match, run, configuration, checkpoint, cancellationToken),
            "skill_recommender" => DispatchSkillRecommendationAsync(match, run, configuration, checkpoint, cancellationToken),
            "evolution" => DispatchEvolutionAsync(match, run, configuration, checkpoint, cancellationToken),
            "git_steward" => DispatchGitStewardAsync(match, run, configuration, checkpoint, cancellationToken),
            _ => Task.CompletedTask
        };

    private static string OperationalRoleKey(RuntimeAgentDefinition agent) =>
        (agent.Role?.Trim().ToLowerInvariant(), agent.Id?.Trim().ToLowerInvariant()) switch
        {
            ("context_maintenance", _) or (_, "context_compressor") => "context_compressor",
            ("capability_advisor", _) or (_, "skill_recommender") => "skill_recommender",
            ("experience_curator", _) or (_, "evolution") => "evolution",
            ("git_steward", _) => "git_steward",
            _ => string.Empty
        };

    private async Task DispatchContextCompressionAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        // ToolCallAware guard: never compress while a worker turn or tool
        // execution is in flight; the summary could split an unfinished
        // call/result group.
        if (checkpoint.Tasks.Any(task => task.Status == "running" || !string.IsNullOrWhiteSpace(task.PendingToolExecutionId)))
        {
            await AppendEventAsync(runId, "context.compaction.skipped", "Compression skipped: a task is still executing.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                reason = "active_tasks"
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var context = await BuildContextAsync(run, configuration, match.Agent.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        if (context.EstimatedTokens < configuration.Triggers.ContextTokenThreshold)
        {
            await AppendEventAsync(runId, "context.compaction.skipped", "Compression skipped: context is below the token threshold.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                reason = "below_threshold",
                estimated_tokens = context.EstimatedTokens,
                threshold = configuration.Triggers.ContextTokenThreshold
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(match.Agent, context, cancellationToken).ConfigureAwait(false);
        var evidence = string.Join("\n", context.Evidence.Select(item => $"[{item.Source}] {item.Content}"));
        var instructions = assembly.Instructions
            + "\n\nYou are the context compression agent. Compress the session context into a structured summary with these sections: 当前目标 / 关键约束 / 已完成事项 / 待处理事项 / 重要结论 / 风险点. Preserve the current goal and constraints exactly, keep approval conclusions, and never invent new facts.";
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nSession context evidence:\n{evidence}";
        using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            $"operation.{match.Agent.Id}",
            match.Agent.Id,
            "Compresses session context into a structured summary patch.",
            new ChatOptions { Instructions = instructions });
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(
            checkpoint.ModelUsage,
            Maf18RuntimeAdapter.NormalizeUsage(response.Usage));
        var summary = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(summary)) throw new InvalidOperationException("Context compressor returned no output.");

        var baseRevision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        var result = await _conversations.ApplyContextPatchAsync(new ContextPatchRequest(
            checkpoint.SessionId,
            baseRevision,
            summary,
            $"Compressed session context at revision {baseRevision}.",
            checkpoint.RunId,
            Kind: "compaction"), cancellationToken).ConfigureAwait(false);
        if (string.Equals(result.Status, "applied", StringComparison.OrdinalIgnoreCase))
        {
            await AppendEventAsync(runId, "context.compacted", "Context compression applied as a session patch.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                patch_id = result.PatchId,
                base_context_revision = baseRevision,
                context_revision = result.AppliedRevision
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AppendEventAsync(runId, "context.compacted.stale", "Context compression lost the revision race and was kept for audit.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                patch_id = result.PatchId,
                base_context_revision = baseRevision,
                context_revision = result.CurrentRevision
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchSkillRecommendationAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var context = await BuildContextAsync(run, configuration, match.Agent.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(match.Agent, context, cancellationToken).ConfigureAwait(false);
        var taskSummary = checkpoint.Tasks.Count == 0
            ? "(no task graph yet)"
            : string.Join("\n", checkpoint.Tasks.Select(item =>
                $"- {item.TaskKey}: capabilities=[{string.Join(", ", item.RequiredCapabilities)}] tools=[{string.Join(", ", item.RequiredTools)}]"));
        var evidence = string.Join("\n", context.Evidence.Select(item => $"[{item.Source}] {item.Content}"));
        var instructions = assembly.Instructions
            + "\n\nYou are the capability advisor. Recommend execution capabilities for the current task graph. Respond with strict JSON only: {\"recommendations\":[{\"skill\":\"...\",\"reason\":\"...\",\"confidence\":\"high|medium|low\"}]}. Advise only; never execute, approve, or address the user.";
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nTask graph:\n{taskSummary}\n\nSession context evidence:\n{evidence}";
        using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            $"operation.{match.Agent.Id}",
            match.Agent.Id,
            "Advises the planner on capable execution specialists without deciding.",
            new ChatOptions { Instructions = instructions });
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(
            checkpoint.ModelUsage,
            Maf18RuntimeAdapter.NormalizeUsage(response.Usage));

        var recommendations = ParseRecommendations(response.Text);
        if (recommendations.Count == 0)
        {
            await AppendEventAsync(runId, "capability.recommendation.empty", "Capability advisor produced no usable recommendations.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString()
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var added = 0;
        foreach (var recommendation in recommendations)
        {
            if (checkpoint.RecommendedCapabilities.Count >= 10) break;
            if (checkpoint.RecommendedCapabilities.Any(existing => string.Equals(existing.Skill, recommendation.Skill, StringComparison.OrdinalIgnoreCase))) continue;
            checkpoint.RecommendedCapabilities.Add(recommendation);
            added++;
        }
        if (added > 0)
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "capability-recommended", cancellationToken).ConfigureAwait(false);
        }
        await AppendEventAsync(runId, "capability.recommended", $"Capability advisor recommended {recommendations.Count} skill(s).", new
        {
            run_id = run.RunId,
            agent_slug = match.Agent.Id,
            trigger_point = match.Point.ToString(),
            added,
            recommendations = recommendations.Select(item => new { skill = item.Skill, reason = item.Reason, confidence = item.Confidence }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<RecommendedCapability> ParseRecommendations(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            using var document = JsonDocument.Parse(text.Trim());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("recommendations", out var array)
                || array.ValueKind != JsonValueKind.Array) return [];
            var result = new List<RecommendedCapability>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var skill = item.TryGetProperty("skill", out var skillNode) && skillNode.ValueKind == JsonValueKind.String
                    ? skillNode.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(skill)) continue;
                var reason = item.TryGetProperty("reason", out var reasonNode) && reasonNode.ValueKind == JsonValueKind.String
                    ? reasonNode.GetString() ?? string.Empty
                    : string.Empty;
                var confidence = item.TryGetProperty("confidence", out var confidenceNode) && confidenceNode.ValueKind == JsonValueKind.String
                    ? confidenceNode.GetString() ?? "medium"
                    : "medium";
                result.Add(new RecommendedCapability(skill, reason, confidence));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task DispatchEvolutionAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        if (_longTermMemory is null)
        {
            _logger.LogDebug("Long-term memory service is not registered; skipping experience curation for run {RunId}.", run.RunId);
            return;
        }

        var runId = Guid.Parse(run.RunId);
        var context = await BuildContextAsync(run, configuration, match.Agent.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(match.Agent, context, cancellationToken).ConfigureAwait(false);
        var evidence = string.Join("\n", checkpoint.Tasks.Select(item =>
            $"- [{item.ResultStatus ?? item.Status}] {item.ResultSummary}"));
        var instructions = assembly.Instructions
            + "\n\nYou are the experience curator. Review the finished run and extract reusable experience. Respond with strict JSON only: "
            + "{\"memory_candidates\":[{\"scope\":\"workspace\",\"kind\":\"success_pattern|failure_pattern|fact|preference|decision|task_template|supervision_rule\",\"content\":\"...\",\"confidence\":0.8,\"applicability\":\"...\",\"expiry_condition\":\"...\"}],"
            + "\"agent_candidates\":[{\"name\":\"...\",\"layer\":\"execution\",\"agent_type\":\"task_executor\",\"confidence\":0.7,\"proposal\":{\"slug\":\"...\",\"display_name\":\"...\",\"layer\":\"execution\",\"role\":\"task_executor\",\"system_prompt\":\"...\",\"description\":\"...\",\"capabilities\":[]}}]}. "
            + "Only propose candidates backed by concrete evidence from this run; an empty object means nothing was worth keeping. Candidates are never applied without human review.";
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nExecution evidence:\n{evidence}\n\nSupervision decision: {checkpoint.SupervisionDecision ?? "none"}\n\nSession context evidence:\n{string.Join("\n", context.Evidence.Select(item => $"[{item.Source}] {item.Content}"))}";
        using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            $"operation.{match.Agent.Id}",
            match.Agent.Id,
            "Curates reusable experience from finished runs as reviewable candidates.",
            new ChatOptions { Instructions = instructions });
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(
            checkpoint.ModelUsage,
            Maf18RuntimeAdapter.NormalizeUsage(response.Usage));

        var (memoryCandidates, agentCandidates) = ParseCurationOutput(response.Text);
        // Candidate scope/kind must stay inside the frozen [memory] policy; the
        // curator cannot widen its own write surface.
        memoryCandidates = memoryCandidates
            .Where(item => configuration.Memory.AllowedScopes.Contains(item.Scope, StringComparer.OrdinalIgnoreCase)
                && configuration.Memory.AllowedKinds.Contains(item.Kind, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (memoryCandidates.Count == 0 && agentCandidates.Count == 0) return;

        // Attribution requires a durable curator identity; create it lazily so a
        // run with nothing worth keeping keeps its lineage clean.
        var curator = await EnsureEvolutionAgentAsync(run, configuration, match.Agent, cancellationToken).ConfigureAwait(false);

        foreach (var (scope, kind, content, confidence, applicability, expiryCondition) in memoryCandidates)
        {
            var candidate = await _longTermMemory.CreateCandidateAsync(new MemoryCandidateProposal(
                runId,
                curator.Id,
                scope,
                kind,
                content,
                confidence,
                Applicability: applicability,
                ExpiryCondition: expiryCondition), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "memory.candidate_created", "Experience curator proposed a memory candidate.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                candidate_id = candidate.Id,
                scope = candidate.Scope,
                kind = candidate.Kind,
                confidence = candidate.Confidence
            }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (name, layer, agentType, confidence, proposal) in agentCandidates)
        {
            var candidate = await _instances.CreateCandidateAsync(new AgentCandidateProposal(
                runId,
                curator.Id,
                name,
                layer,
                agentType,
                confidence,
                proposal), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.candidate_proposed", "Experience curator proposed an agent candidate.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                candidate_id = candidate.Id,
                name = candidate.Name,
                layer = candidate.Layer,
                agent_type = candidate.AgentType,
                confidence = candidate.ConfidenceScore
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RuntimeAgentInstance> EnsureEvolutionAgentAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        RuntimeAgentDefinition definition,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var all = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var curator = all.FirstOrDefault(item => !item.Generated && item.AgentVersionId == definition.AgentVersionId);
        if (curator is not null)
        {
            VerifyRootInstance(curator, definition, null, "experience curator");
            return curator;
        }

        curator = await _instances.CreateRootAsync(new RuntimeAgentSeed(
            Guid.Parse(run.SessionId),
            runId,
            definition.Id,
            definition.Layer,
            definition.Role,
            "chat",
            definition.Capabilities,
            definition.AllowedTools,
            ["workspace"],
            configuration.Context.DefaultTokenBudget,
            AgentDefinitionId: definition.AgentDefinitionId,
            AgentVersionId: definition.AgentVersionId,
            VersionContentHash: definition.VersionContentHash), cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "agent.created", "Experience curator created.", new
        {
            agent_instance_id = curator.Id,
            agent_slug = definition.Id,
            layer = curator.Layer,
            role = curator.Role
        }, cancellationToken).ConfigureAwait(false);
        return curator;
    }

    private async Task DispatchGitStewardAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        // Fire only for runs that actually touched git capabilities; other runs
        // keep the journal quiet.
        var touchedGit = checkpoint.Tasks.Any(task =>
            task.RequiredTools.Any(tool => tool.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
            || task.ToolTurns.Any(turn => turn.ToolId.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
            || string.Equals(task.WorkerAgentSlug, "worker.git", StringComparison.Ordinal));
        if (!touchedGit) return;

        var runId = Guid.Parse(run.RunId);
        var context = await BuildContextAsync(run, configuration, match.Agent.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(match.Agent, context, cancellationToken).ConfigureAwait(false);
        var gitTasks = checkpoint.Tasks
            .Where(task => task.RequiredTools.Any(tool => tool.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
                || task.ToolTurns.Any(turn => turn.ToolId.StartsWith("git_", StringComparison.OrdinalIgnoreCase)))
            .Select(task => $"- [{task.ResultStatus ?? task.Status}] {task.TaskKey}: tools=[{string.Join(", ", task.RequiredTools.Concat(task.ToolTurns.Select(turn => turn.ToolId)).Distinct(StringComparer.OrdinalIgnoreCase))}]")
            .ToList();
        var instructions = assembly.Instructions
            + "\n\nYou are the git steward. Review the change scope and commit boundaries this run touched. Respond with a short suggestion covering: change scope, suggested commit boundary, and review risks. Advise only; never execute git operations or bypass approvals.";
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nGit-touching tasks:\n{string.Join("\n", gitTasks)}";
        using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            $"operation.{match.Agent.Id}",
            match.Agent.Id,
            "Reviews git change scope and commit boundaries without executing git.",
            new ChatOptions { Instructions = instructions });
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(
            checkpoint.ModelUsage,
            Maf18RuntimeAdapter.NormalizeUsage(response.Usage));
        var suggestion = string.IsNullOrWhiteSpace(response.Text) ? "No git steward suggestion produced." : response.Text.Trim();
        await AppendEventAsync(runId, "git.steward.reviewed", "Git steward reviewed the run's change scope.", new
        {
            run_id = run.RunId,
            agent_slug = match.Agent.Id,
            trigger_point = match.Point.ToString(),
            suggestion
        }, cancellationToken).ConfigureAwait(false);
    }

    private static (List<(string Scope, string Kind, string Content, double Confidence, string? Applicability, string? ExpiryCondition)> MemoryCandidates,
        List<(string Name, string Layer, string AgentType, double Confidence, JsonElement Proposal)> AgentCandidates) ParseCurationOutput(string? text)
    {
        var memoryCandidates = new List<(string, string, string, double, string?, string?)>();
        var agentCandidates = new List<(string, string, string, double, JsonElement)>();
        if (string.IsNullOrWhiteSpace(text)) return (memoryCandidates, agentCandidates);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text.Trim());
        }
        catch (JsonException)
        {
            return (memoryCandidates, agentCandidates);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (memoryCandidates, agentCandidates);

            if (root.TryGetProperty("memory_candidates", out var memoryNode) && memoryNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in memoryNode.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var scope = StringProperty(item, "scope");
                    var kind = StringProperty(item, "kind");
                    var content = StringProperty(item, "content");
                    if (scope is null || kind is null || content is null) continue;
                    memoryCandidates.Add((scope, kind, content, ConfidenceProperty(item), StringProperty(item, "applicability"), StringProperty(item, "expiry_condition")));
                }
            }

            if (root.TryGetProperty("agent_candidates", out var agentNode) && agentNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in agentNode.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = StringProperty(item, "name");
                    var layer = StringProperty(item, "layer");
                    var agentType = StringProperty(item, "agent_type");
                    if (name is null || layer is null || agentType is null) continue;
                    if (!item.TryGetProperty("proposal", out var proposalNode) || proposalNode.ValueKind != JsonValueKind.Object) continue;
                    agentCandidates.Add((name, layer, agentType, ConfidenceProperty(item), proposalNode.Clone()));
                }
            }
        }
        return (memoryCandidates, agentCandidates);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;

    private static double ConfidenceProperty(JsonElement element) =>
        element.TryGetProperty("confidence", out var property) && property.TryGetDouble(out var value)
            ? Math.Clamp(value, 0, 1)
            : 0.5;

    private Task AppendEventAsync(Guid runId, string type, string summary, object payload, CancellationToken ct, Guid? taskId = null) =>
        _lifecycle.AppendEventAsync(runId, type, payload, summary, taskId: taskId, cancellationToken: ct);

    private static bool IsTerminal(RunStatus status) => status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
    private static string SafeError(Exception ex) =>
        ex is InvalidOperationException or InvalidDataException or WorkerUnavailableException
            ? ex.Message
            : "Unexpected runtime failure.";

    private static StepResult BuildCompletedStepResult(Guid taskId, Guid workerId, string text)
    {
        var summary = ExtractContextPatch(text, out var patchSummary, out var patchContent);
        return new StepResult
        {
            TaskNodeId = taskId,
            AgentId = workerId.ToString("N"),
            Status = "completed",
            Summary = summary,
            Evidence = [summary],
            ProposedPatchSummary = patchSummary,
            ProposedPatchContent = patchContent
        };
    }

    /// <summary>
    /// Extracts the optional worker-proposed patch line. Returns the text with
    /// the marker line removed; patch fields stay null when absent or malformed.
    /// </summary>
    private static string ExtractContextPatch(string text, out string? patchSummary, out string? patchContent)
    {
        patchSummary = null;
        patchContent = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (patchContent is null && trimmed.StartsWith(ContextPatchMarker, StringComparison.OrdinalIgnoreCase))
            {
                var payload = trimmed[ContextPatchMarker.Length..].Trim();
                var separator = payload.IndexOf("||", StringComparison.Ordinal);
                if (separator <= 0 || separator >= payload.Length - 2) continue;
                var summary = payload[..separator].Trim();
                var content = payload[(separator + 2)..].Trim();
                if (summary.Length == 0 || content.Length == 0) continue;
                patchSummary = summary;
                patchContent = content;
                continue;
            }
            kept.Add(line);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1])) kept.RemoveAt(kept.Count - 1);
        var cleaned = string.Join("\n", kept).Trim();
        return cleaned.Length == 0 ? text.Trim() : cleaned;
    }

    private static string NormalizeTaskKey(string? candidate, string title)
    {
        var source = string.IsNullOrWhiteSpace(candidate) ? title : candidate;
        var buffer = new List<char>(source.Length);
        foreach (var value in source.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(value)) buffer.Add(value);
            else if (buffer.Count != 0 && buffer[^1] != '-') buffer.Add('-');
        }
        while (buffer.Count != 0 && buffer[^1] == '-') buffer.RemoveAt(buffer.Count - 1);
        return buffer.Count == 0 ? "task" : new string(buffer.ToArray());
    }

    internal sealed class InvalidTaskGraphException(string message) : InvalidOperationException(message);

    private sealed record TaskExecutionResult(
        Guid TaskId,
        Guid? WorkerAgentId,
        string Status,
        StepResult Result,
        ModelUsage? Usage = null);
    private sealed record ToolTaskExecutionResult(
        FullDuplexCheckpointV1 Checkpoint,
        bool Waiting,
        TaskExecutionResult? Result);
}

internal sealed class FullDuplexCheckpointV1
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public Guid SessionId { get; init; }
    public Guid TurnId { get; init; }
    public Guid TriggerMessageId { get; init; }
    public string UserGoal { get; set; } = string.Empty;
    public string Phase { get; set; } = "planning";
    public string InteractionKind { get; set; } = "new_task";
    public Guid? TargetRunId { get; set; }
    public long CheckpointRevision { get; set; }
    public long ContextRevision { get; set; }
    public int PlanRevision { get; set; }
    public Guid? MeetingAgentId { get; set; }
    public Guid? PlannerAgentId { get; set; }
    public Guid? SupervisorAgentId { get; set; }
    public List<DurableTaskNode> Tasks { get; set; } = [];
    public int SupervisionRound { get; set; }
    public string? SupervisionDecision { get; set; }
    public List<string> SupervisionReasons { get; set; } = [];
    public string? MeetingResponse { get; set; }
    public List<RecommendedCapability> RecommendedCapabilities { get; set; } = [];
    public ModelUsage? ModelUsage { get; set; }
    public Guid? AssistantMessageId { get; set; }

    /// <summary>
    /// Lateral execution lanes (swim lanes) beyond the implicit "main" lane.
    /// Absent (pre-lane checkpoints) or empty means a single implicit main lane.
    /// </summary>
    public List<DurableLane> Lanes { get; set; } = [];

    /// <summary>
    /// Count of run_directives rows this run has drained. Resume safety comes
    /// from the pending-status filter on the drain query, not from this cursor.
    /// </summary>
    public long DirectiveCursor { get; set; }
}

/// <summary>
/// A skill/capability recommendation produced by the operational capability
/// advisor and injected into subsequent planning rounds.
/// </summary>
internal sealed record RecommendedCapability(string Skill, string Reason, string Confidence);

internal sealed class DurableTaskNode
{
    public Guid TaskId { get; init; }
    public string TaskKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Category { get; init; }
    public List<string> SuccessCriteria { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];
    public List<string> RequiredCapabilities { get; init; } = [];
    public List<string> RequiredTools { get; init; } = [];
    public int Priority { get; init; }
    public string Risk { get; init; } = "medium";
    public string Status { get; set; } = "pending";
    public int Attempt { get; set; }
    public int ToolRounds { get; set; }
    public List<WorkerToolTurn> ToolTurns { get; set; } = [];
    public string? PendingToolExecutionId { get; set; }
    public string? PendingToolApprovalId { get; set; }
    public Guid? WorkerAgentId { get; set; }
    public string? WorkerAgentSlug { get; set; }
    public Guid? WorkerAgentDefinitionId { get; set; }
    public Guid? WorkerAgentVersionId { get; set; }
    public string? WorkerAgentVersionHash { get; set; }
    public string? WorkerAssignmentReason { get; set; }
    public long InputContextRevision { get; set; }
    public string? ResultStatus { get; set; }
    public string? ResultSummary { get; set; }
    public List<string> Evidence { get; set; } = [];
    public List<StaleTaskEvidence> StaleEvidence { get; set; } = [];
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The lane this task belongs to. Null on pre-lane checkpoints and reads as
    /// the implicit "main" lane everywhere it is consumed.
    /// </summary>
    public string? LaneKey { get; set; }

    /// <summary>
    /// Lane keys this task is docked behind; the M2 main loop keeps the task
    /// waiting until every listed lane reaches a terminal state.
    /// </summary>
    public List<string> Waits { get; set; } = [];

    /// <summary>
    /// Per-criterion verdicts produced by supervision (M3). Empty means no
    /// lane-gate review has run for this task yet.
    /// </summary>
    public List<CriterionVerdict> CriteriaVerdicts { get; set; } = [];
}

/// <summary>Per-criterion supervised verdict for a lane-gated task.</summary>
internal sealed record CriterionVerdict(string Criterion, bool Satisfied, string? Evidence);

/// <summary>A named execution lane recorded in the run checkpoint.</summary>
internal sealed class DurableLane
{
    public string LaneKey { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
}

internal sealed record StaleTaskEvidence(
    long InputContextRevision,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset RecordedAt);

internal sealed class RunAwaitingExternalDecisionException : Exception;

internal sealed class WorkerUnavailableException(string message) : Exception(message);
