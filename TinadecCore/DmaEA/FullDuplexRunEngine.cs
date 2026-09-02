using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly IOrchestrationDirectiveValidator? _directiveValidator;
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
        ILoopGuard? loopGuard = null,
        IOrchestrationDirectiveValidator? directiveValidator = null)
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
        _directiveValidator = directiveValidator ?? services.GetService(typeof(IOrchestrationDirectiveValidator)) as IOrchestrationDirectiveValidator;
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

                if (configuration.Orchestration.LanesEnabled)
                {
                    var directivesApplied = await ApplyPendingOrchestrationDirectivesAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
                    if (directivesApplied)
                    {
                        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "orchestration-applied", stoppingToken).ConfigureAwait(false);
                    }
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
        SyncLanes(checkpoint);
        // An escalated lane keeps the run parked on awaiting_user; do not
        // overwrite it with "executing" at tick start.
        if (!checkpoint.Lanes.Any(lane => lane.Escalated))
        {
            await _lifecycle.SetRunStatusAsync(run.RunId, "executing", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // A previous worker may have stopped after PrepareAsync persisted an
        // execution. Resume it before asking a model to produce another call.
        var pending = checkpoint.Tasks.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.PendingToolExecutionId));
        if (pending is not null)
        {
            var resumed = await ResumePendingToolAsync(run, configuration, checkpoint, pending, cancellationToken).ConfigureAwait(false);
            checkpoint = resumed.Checkpoint;
            if (resumed.Waiting)
            {
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
        checkpoint = await ClassifyParkedLanesAsync(runId, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);

        // Per-lane ready sets dispatched under one global worker budget so the
        // active worker peak across lanes never exceeds MaxParallelWorkers.
        // Parked lanes (waiting/gate_review/escalated) never dispatch here: a
        // waiting lane first passes its gate review in the classification pass.
        var budget = Math.Max(1, configuration.Spawn.MaxParallelWorkers);
        var laneOrder = checkpoint.Lanes
            .Where(lane => !lane.Escalated
                && lane.Status is "pending" or "executing"
                && checkpoint.Tasks.Any(task => LaneKeyOf(task) == lane.LaneKey && task.Status is "pending" or "ready" or "running"))
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
            if (checkpoint.Lanes.Any(lane => lane.Escalated))
            {
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lanes-awaiting-user", cancellationToken).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            if (checkpoint.Tasks.All(item => item.Status is "completed" or "failed" or "blocked"))
            {
                checkpoint.Phase = "reviewing";
                return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "execution-complete", cancellationToken).ConfigureAwait(false);
            }
            if (checkpoint.Tasks.Any(item => !string.IsNullOrWhiteSpace(item.PendingToolExecutionId)))
            {
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting", cancellationToken).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            // Anything still dispatchable-but-not-parked is genuinely stuck: a
            // crashed "running" dispatch or an in-lane chain planning validation
            // failed to exclude. That stays an invalid graph. Parked lanes
            // (waiting/gate_review) and escalated lanes are expected here.
            var stuck = checkpoint.Tasks.Any(item => item.Status is "pending" or "ready" or "running"
                && IsLaneStuckEligible(checkpoint, LaneKeyOf(item)));
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
    /// One classification pass over every non-terminal lane. A lane whose
    /// cross-lane waits are unmet parks as "waiting" with the target lane's
    /// facts hash frozen into each wait; a parked lane whose waits now hold
    /// enters "gate_review" and really calls this lane's planner — proceed
    /// lets it dispatch this tick, wait_more re-parks (facts unchanged means
    /// no progress is possible and escalates), and a proceed that contradicts
    /// code facts (stale hash or unsatisfied per-criterion verdicts) is
    /// discarded with gate.review.rejected_stale. The model can only tighten
    /// a gate, never loosen it. A lane whose tasks are all terminal gets its
    /// supervision verdict first so gates can read per-criterion evidence.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> ClassifyParkedLanesAsync(
        Guid runId,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        foreach (var lane in checkpoint.Lanes)
        {
            if (lane.Escalated) continue;
            var laneTasks = checkpoint.Tasks.Where(item => LaneKeyOf(item) == lane.LaneKey).ToList();
            if (laneTasks.Count == 0) continue;
            var terminal = laneTasks.All(item => item.Status is "completed" or "failed" or "blocked");

            if (terminal)
            {
                // 裁决落 lane: a finished lane gets its supervision verdict and
                // per-criterion evidence while other lanes may still run, so a
                // gate waiting on this lane reads real verdicts instead of
                // waiting for the run-level review that cannot start yet.
                if (lane.SupervisionDecision is null
                    && configuration.Supervision.RequiredBeforeFinal
                    && checkpoint.Lanes.Any(other => !string.Equals(other.LaneKey, lane.LaneKey, StringComparison.OrdinalIgnoreCase)
                        && checkpoint.Tasks.Any(task => LaneKeyOf(task) == other.LaneKey && task.Status is "pending" or "ready" or "running")))
                {
                    await SuperviseLaneAsync(runId, run, configuration, checkpoint, lane, laneTasks, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (laneTasks.Any(item => item.Status == "running" || !string.IsNullOrWhiteSpace(item.PendingToolExecutionId))) continue;

            var unmet = UnmetLaneWaits(checkpoint, lane.LaneKey);
            if (unmet.Count > 0)
            {
                foreach (var task in laneTasks.Where(item => item.Status is "pending" or "ready"))
                {
                    task.Waits = [.. unmet];
                }
                if (!string.Equals(lane.Status, "waiting", StringComparison.Ordinal))
                {
                    lane.Status = "waiting";
                    await AppendEventAsync(runId, "orchestration.lane_waiting",
                        $"Lane '{lane.LaneKey}' is waiting on other lanes.",
                        new { lane_key = lane.LaneKey, waits = unmet.Select(wait => new { lane = wait.LaneKey, predicate = wait.Predicate, facts_hash = wait.ObservedFactsHash }) }, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            if (!string.Equals(lane.Status, "waiting", StringComparison.Ordinal)) continue;

            // Waits hold: the parked lane must pass its gate before dispatching.
            lane.Status = "gate_review";
            var targetLanes = GateTargetLanes(checkpoint, lane.LaneKey);
            var factsHash = ComputeLaneFactsHash(checkpoint, targetLanes);
            await AppendEventAsync(runId, "orchestration.gate_review",
                $"Lane '{lane.LaneKey}' entered gate review.",
                new { lane_key = lane.LaneKey, facts_hash = factsHash, targets = targetLanes }, cancellationToken).ConfigureAwait(false);
            var gatePrompt = BuildGatePrompt(checkpoint, lane, targetLanes, factsHash);
            var gateDecision = await RunGateReviewAsync(run, configuration, checkpoint, lane, gatePrompt, cancellationToken).ConfigureAwait(false);

            if (gateDecision.Decision == "proceed")
            {
                var currentHash = ComputeLaneFactsHash(checkpoint, targetLanes);
                if (!string.Equals(currentHash, factsHash, StringComparison.Ordinal))
                {
                    // Facts moved while the gate call was in flight: re-park so
                    // waits re-freeze against the new facts.
                    await RejectStaleGateAsync(runId, lane, "facts hash changed during gate review", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!GateCodeFactsHold(checkpoint, targetLanes))
                {
                    await RejectStaleGateAsync(runId, lane, "model proceeded against unsatisfied criteria verdicts", cancellationToken).ConfigureAwait(false);
                    await EscalateLaneAsync(runId, run, lane, "Gate proceed rejected: per-criterion verdicts do not hold over code facts.", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                lane.LastGateFactsHash = factsHash;
                lane.Status = "executing";
                await AppendEventAsync(runId, "orchestration.gate_review.completed",
                    $"Lane '{lane.LaneKey}' gate approved to proceed.",
                    new { lane_key = lane.LaneKey, decision = "proceed", reasons = gateDecision.Reasons }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (gateDecision.Decision == "wait_more"
                && !string.Equals(lane.LastGateFactsHash, factsHash, StringComparison.Ordinal))
            {
                lane.LastGateFactsHash = factsHash;
                lane.Status = "waiting";
                await AppendEventAsync(runId, "orchestration.gate_review.completed",
                    $"Lane '{lane.LaneKey}' gate asked for more evidence.",
                    new { lane_key = lane.LaneKey, decision = "wait_more", reasons = gateDecision.Reasons }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            // escalate, unparsable output, or wait_more over unchanged facts
            // (nothing can change while parked): freeze only this lane.
            await EscalateLaneAsync(runId, run, lane,
                gateDecision.Reasons.Count > 0 ? string.Join("; ", gateDecision.Reasons) : "Gate review did not approve the lane to proceed.",
                cancellationToken).ConfigureAwait(false);
        }
        return checkpoint;
    }

    private async Task SuperviseLaneAsync(
        Guid runId,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableLane lane,
        List<DurableTaskNode> laneTasks,
        CancellationToken cancellationToken)
    {
        try
        {
            var supervisorDefinition = RequiredAgent(configuration.OperationAgents, "supervisor");
            var supervisorInstance = await EnsureSupervisorAgentAsync(run, configuration, checkpoint, supervisorDefinition, cancellationToken).ConfigureAwait(false);
            checkpoint.SupervisorAgentId = supervisorInstance.Id;
            var context = await BuildContextAsync(run, configuration, supervisorDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
            var assembly = await AssemblePromptAsync(supervisorDefinition, context, cancellationToken).ConfigureAwait(false);
            var supervisor = new SupervisionAgent(CreateModelFactory(configuration, checkpoint, supervisorDefinition,
                supervisorInstance.Id, supervisorInstance.ParentInstanceId), _logger);
            var plans = laneTasks.Select(ToPlannedTask).ToArray();
            var results = laneTasks.Select(item => new StepResult
            {
                TaskNodeId = item.TaskId,
                AgentId = item.WorkerAgentId?.ToString() ?? string.Empty,
                Status = item.ResultStatus ?? item.Status,
                Summary = item.ResultSummary ?? string.Empty,
                Evidence = item.Evidence
            }).ToArray();
            var verdict = await supervisor.ReviewAsync(checkpoint.UserGoal, plans, results, checkpoint.SupervisionRound, assembly.Instructions, cancellationToken).ConfigureAwait(false);
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, supervisor.LastUsage);
            lane.SupervisionRound = checkpoint.SupervisionRound;
            lane.SupervisionDecision = verdict.DecictionString();
            lane.SupervisionReasons = verdict.Reasons.ToList();
            MapCriterionVerdicts(checkpoint, verdict);
            await AppendEventAsync(runId, "supervision.lane_completed",
                $"Lane '{lane.LaneKey}' supervision decision: {lane.SupervisionDecision}.",
                new { lane_key = lane.LaneKey, decision = lane.SupervisionDecision, reasons = lane.SupervisionReasons }, cancellationToken).ConfigureAwait(false);
            if (verdict.Decision == SupervisionDecision.Escalate)
            {
                await EscalateLaneAsync(runId, run, lane, "Lane supervision escalated.", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lane supervision failed; escalating lane.");
            await EscalateLaneAsync(runId, run, lane, "Lane supervision failed: " + ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EscalateLaneAsync(
        Guid runId,
        RunState run,
        DurableLane lane,
        string reason,
        CancellationToken cancellationToken)
    {
        lane.Escalated = true;
        await _lifecycle.SetRunStatusAsync(run.RunId, "awaiting_user", reason, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "supervision.user_review.requested",
            "A lane escalated and is waiting for a user decision while other lanes continue.",
            new { run_id = run.RunId, lane_key = lane.LaneKey, decision = "escalate", reasons = new[] { reason }, options = new[] { "continue", "correct", "cancel" } }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RejectStaleGateAsync(
        Guid runId,
        DurableLane lane,
        string reason,
        CancellationToken cancellationToken)
    {
        lane.Status = "waiting";
        await AppendEventAsync(runId, "gate.review.rejected_stale",
            "A gate proceed was discarded because the frozen code facts no longer hold.",
            new { lane_key = lane.LaneKey, reason }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GateDecision> RunGateReviewAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableLane lane,
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var plannerDefinition = RequiredAgent(configuration.ExecutionAgents, "task_planner");
            // A lane that plans through its own instance is also reviewed by its
            // own instance, so its gate sees the lane's context and the audit
            // trail attributes the verdict to the right agent. Lanes without
            // their own instance — including main — fall back to the shared one.
            var plannerInstanceId = checkpoint.LanePlannerIds.TryGetValue(lane.LaneKey, out var lanePlannerId)
                ? lanePlannerId
                : checkpoint.PlannerAgentId ?? throw new InvalidDataException("Planner instance is missing from checkpoint.");
            var factory = CreateModelFactory(configuration, checkpoint, plannerDefinition, plannerInstanceId, null);
            var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
            if (!resolution.IsAvailable)
            {
                return new GateDecision("escalate", [resolution.Error ?? "Chat route is unavailable."]);
            }
            var chatClient = await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false);
            using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
                chatClient,
                "operation.task_planner",
                "task_planner",
                "Reviews cross-lane gate evidence and may only tighten, never loosen, the gate.",
                new ChatOptions
                {
                    Instructions = "你是任务规划智能体，正在执行门控评审（gate review）。对照给出的代码事实与逐条验收裁决，判断本 lane 是否可以开始执行。只能收紧、不能放宽：任何一条验收裁决不满足或事实哈希不符都必须拒绝放行。仅输出 JSON 对象：{\"decision\":\"proceed|wait_more|escalate\",\"reasons\":[...]}，不要输出其他文字。"
                });
            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, Maf18RuntimeAdapter.NormalizeUsage(response.Usage));
            return ParseGateDecision(response.Text)
                ?? new GateDecision("escalate", ["Gate review response was unparsable; manual confirmation is required."]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GateDecision("escalate", ["Gate review failed: " + ex.Message]);
        }
    }

    private static string BuildGatePrompt(
        FullDuplexCheckpointV1 checkpoint,
        DurableLane lane,
        IReadOnlyCollection<string> targetLanes,
        string factsHash)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Lane: {lane.LaneKey}");
        builder.AppendLine($"门控评审：lane '{lane.LaneKey}' 的等待谓词已满足，请裁决是否放行开始执行。");
        foreach (var key in targetLanes)
        {
            builder.AppendLine($"等待目标 lane '{key}' 逐任务事实:");
            foreach (var task in checkpoint.Tasks
                .Where(item => string.Equals(LaneKeyOf(item), key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.TaskKey, StringComparer.Ordinal))
            {
                builder.AppendLine($"- [{task.TaskKey}] {task.Title} | criteria: {string.Join("; ", task.SuccessCriteria)} | status: {task.ResultStatus ?? task.Status} | result: {task.ResultSummary ?? "-"} | evidence: {string.Join(" / ", task.Evidence)}");
                var verdicts = task.CriteriaVerdicts
                    .OrderBy(verdict => verdict.Round)
                    .Select(verdict => $"{{\"criterion\":\"{verdict.Criterion}\",\"satisfied\":{verdict.Satisfied.ToString().ToLowerInvariant()},\"evidence\":\"{verdict.Evidence ?? string.Empty}\"}}");
                builder.AppendLine($"  verdicts: [{string.Join(", ", verdicts)}]");
            }
        }
        builder.AppendLine($"ObservedFactsHash: {factsHash}");
        builder.AppendLine("仅输出 JSON：{\"decision\":\"proceed|wait_more|escalate\",\"reasons\":[...]}");
        return builder.ToString();
    }

    private static GateDecision? ParseGateDecision(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<GateDecisionBody>(text.Substring(start, end - start + 1), JsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Decision)) return null;
            var decision = parsed.Decision.Trim().ToLowerInvariant() switch
            {
                "proceed" => "proceed",
                "wait_more" => "wait_more",
                _ => "escalate"
            };
            return new GateDecision(decision, parsed.Reasons ?? []);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record GateDecision(string Decision, IReadOnlyList<string> Reasons);

    private sealed class GateDecisionBody
    {
        [System.Text.Json.Serialization.JsonPropertyName("decision")]
        public string? Decision { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("reasons")]
        public string[]? Reasons { get; set; }
    }

    /// <summary>
    /// The structured waits a lane must still honor: derived from unmet
    /// cross-lane dependencies (predicate lane_done, hash frozen now) plus any
    /// explicit wait already recorded on its tasks whose predicate does not
    /// yet hold.
    /// </summary>
    private static List<LaneWait> UnmetLaneWaits(FullDuplexCheckpointV1 checkpoint, string laneKey)
    {
        var laneTasks = checkpoint.Tasks.Where(item => LaneKeyOf(item) == laneKey).ToList();
        var unmet = new List<LaneWait>();
        void Add(LaneWait wait)
        {
            if (EvaluateLaneWait(checkpoint, wait)) return;
            if (!unmet.Any(existing => string.Equals(existing.LaneKey, wait.LaneKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Predicate, wait.Predicate, StringComparison.Ordinal)))
            {
                unmet.Add(wait);
            }
        }

        var dependencies = laneTasks.Where(item => item.Status is "pending" or "ready")
            .SelectMany(item => item.Dependencies)
            .Where(dependency => !checkpoint.Tasks.Any(other => other.TaskKey == dependency && other.Status == "completed"))
            .Select(dependency => checkpoint.Tasks.FirstOrDefault(other => other.TaskKey == dependency))
            .OfType<DurableTaskNode>()
            .Select(other => LaneKeyOf(other))
            .Where(key => !string.Equals(key, laneKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var key in dependencies)
        {
            Add(new LaneWait(key, LaneWait.LaneDone, [], ComputeLaneFactsHash(checkpoint, [key])));
        }
        foreach (var wait in laneTasks.SelectMany(item => item.Waits))
        {
            Add(wait);
        }
        return unmet;
    }

    /// <summary>Eval only trusts code facts, never model self-reports.</summary>
    private static bool EvaluateLaneWait(FullDuplexCheckpointV1 checkpoint, LaneWait wait)
    {
        var targetTasks = checkpoint.Tasks
            .Where(item => string.Equals(LaneKeyOf(item), wait.LaneKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var targetLane = checkpoint.Lanes.FirstOrDefault(item => string.Equals(item.LaneKey, wait.LaneKey, StringComparison.OrdinalIgnoreCase));
        var holds = wait.Predicate switch
        {
            LaneWait.LaneSupervisionPass => targetLane?.SupervisionDecision == "pass",
            _ => targetTasks.Count > 0
                && targetTasks.All(item => item.Status is "completed" or "failed" or "blocked")
                && targetTasks.All(item => item.Status == "completed")
        };
        if (!holds) return false;
        return wait.RequiredCriteria.Count == 0 || CriterionVerdictsHold(targetTasks, wait.RequiredCriteria);
    }

    /// <summary>
    /// Hard gate validation over code facts: every target lane fully completed
    /// and every latest-round criterion verdict satisfied with evidence.
    /// </summary>
    internal static bool GateCodeFactsHold(FullDuplexCheckpointV1 checkpoint, IReadOnlyCollection<string> targetLaneKeys)
    {
        foreach (var key in targetLaneKeys)
        {
            var tasks = checkpoint.Tasks
                .Where(item => string.Equals(LaneKeyOf(item), key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tasks.Count == 0) continue;
            if (tasks.Any(item => item.Status is not ("completed" or "failed" or "blocked"))) return false;
            if (tasks.Any(item => item.Status != "completed")) return false;
            if (!CriterionVerdictsHold(tasks, null)) return false;
        }
        return true;
    }

    private static bool CriterionVerdictsHold(List<DurableTaskNode> tasks, IReadOnlyList<string>? requiredCriteria)
    {
        var latest = tasks.SelectMany(item => item.CriteriaVerdicts.Select(verdict => (TaskKey: item.TaskKey, Verdict: verdict)))
            .GroupBy(pair => (pair.TaskKey, pair.Verdict.Criterion))
            .ToDictionary(group => group.Key, group => group.OrderBy(pair => pair.Verdict.Round).Last().Verdict);
        foreach (var verdict in latest.Values)
        {
            if (requiredCriteria is not null && !requiredCriteria.Contains(verdict.Criterion, StringComparer.Ordinal)) continue;
            if (!verdict.Satisfied || string.IsNullOrWhiteSpace(verdict.Evidence)) return false;
        }
        return true;
    }

    private static List<string> GateTargetLanes(FullDuplexCheckpointV1 checkpoint, string laneKey) =>
        checkpoint.Tasks
            .Where(item => string.Equals(LaneKeyOf(item), laneKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Waits)
            .Select(wait => wait.LaneKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// SHA-256 over the checkpoint revision plus each target lane's supervision
    /// decision and task status vector — the observable-facts identity a gate
    /// is frozen against.
    /// </summary>
    private static string ComputeLaneFactsHash(FullDuplexCheckpointV1 checkpoint, IReadOnlyCollection<string> targetLaneKeys)
    {
        var payload = string.Join(";", targetLaneKeys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                var lane = checkpoint.Lanes.FirstOrDefault(item => string.Equals(item.LaneKey, key, StringComparison.OrdinalIgnoreCase));
                var tasks = checkpoint.Tasks
                    .Where(item => LaneKeyOf(item) == key)
                    .OrderBy(item => item.TaskKey, StringComparer.Ordinal)
                    .Select(item => $"{item.TaskKey}:{item.Status}:{item.ResultStatus ?? "-"}");
                return $"{key}|supervision={lane?.SupervisionDecision ?? "-"}|{string.Join(",", tasks)}";
            }));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"rev={checkpoint.CheckpointRevision}|{payload}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsLaneStuckEligible(FullDuplexCheckpointV1 checkpoint, string laneKey)
    {
        var lane = checkpoint.Lanes.FirstOrDefault(item => string.Equals(item.LaneKey, laneKey, StringComparison.OrdinalIgnoreCase));
        if (lane is null || lane.Escalated) return true;
        return lane.Status is not ("waiting" or "gate_review" or "completed");
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
                        LaneKey = LaneKeyOf(task),
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
                // A parked approval is not the same as one still waiting for a
                // human: the decision window already elapsed, so leaving the lane
                // parked would stall it forever in an unattended run. Freeze only
                // this lane and surface a review request; the other lanes and the
                // run lease keep going.
                if (dispatch.Status == ToolDispatchStatus.AwaitingApproval && dispatch.ParkExpired)
                {
                    SyncLanes(checkpoint);
                    var parkedLane = checkpoint.Lanes.FirstOrDefault(lane =>
                        string.Equals(lane.LaneKey, LaneKeyOf(task), StringComparison.OrdinalIgnoreCase));
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-park-expired", cancellationToken).ConfigureAwait(false);
                    if (parkedLane is not null)
                    {
                        await EscalateLaneAsync(Guid.Parse(run.RunId), run, parkedLane,
                            $"Approval for tool '{pendingTurn.ToolId}' expired its decision window while the run was unattended.",
                            cancellationToken).ConfigureAwait(false);
                        // Return before the generic awaiting handling below can
                        // overwrite the escalated run status with awaiting_approval.
                        return new ToolTaskExecutionResult(checkpoint, Waiting: true, Result: null);
                    }
                }
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
        MapCriterionVerdicts(checkpoint, verdict);
        foreach (var lane in checkpoint.Lanes)
        {
            lane.SupervisionRound = checkpoint.SupervisionRound;
            lane.SupervisionDecision = checkpoint.SupervisionDecision;
            lane.SupervisionReasons = checkpoint.SupervisionReasons.ToList();
        }
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

        if (checkpoint.Lanes.Any(lane => lane.Escalated))
        {
            // A lane escalation is still pending a user decision: the run must
            // not finalize while that gate is unresolved, even though every
            // task is terminal.
            checkpoint.Phase = "awaiting_user";
            await _lifecycle.SetRunStatusAsync(run.RunId, "awaiting_user", "A lane escalation is pending a user decision.", cancellationToken).ConfigureAwait(false);
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lane-escalation-parked", cancellationToken).ConfigureAwait(false);
        }
        checkpoint.Phase = "finalizing";
        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "reviewed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lands the supervision verdict's per-criterion verdicts on the tasks they
    /// name (裁决落 lane): a gate later reads these as code facts and can only
    /// tighten, never loosen, its decision.
    /// </summary>
    private static void MapCriterionVerdicts(FullDuplexCheckpointV1 checkpoint, SupervisionVerdict verdict)
    {
        if (verdict.CriterionVerdicts is not { Count: > 0 }) return;
        foreach (var item in verdict.CriterionVerdicts)
        {
            var task = checkpoint.Tasks.FirstOrDefault(node => string.Equals(node.TaskKey, item.TaskKey, StringComparison.OrdinalIgnoreCase));
            task?.CriteriaVerdicts.Add(new CriterionVerdict(item.Criterion, item.Satisfied, item.Evidence, "supervisor", checkpoint.SupervisionRound));
        }
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
        if (configuration.Orchestration.LanesEnabled && checkpoint.TargetRunId is not null)
        {
            instructions += "\n\n" + LaneOpenProtocol;
        }
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
        return await FinalizeInteractionTextAsync(run, configuration, checkpoint, response.Text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Protocol contract handed to the meeting agent so a deferred user request
    /// ("after it finishes, test and commit") can be expressed as one final
    /// LANE_OPEN line. The engine, not the model, decides whether the lane opens.
    /// </summary>
    private const string LaneOpenProtocol =
        "Orchestration directive protocol: when the user asks for work that must start only after the target run reaches a state, "
        + "append exactly one final line:\n"
        + "LANE_OPEN: {\"lane_key\":\"<unique-key>\",\"goal\":\"<what this lane does>\","
        + "\"tasks\":[{\"task_key\":\"<key>\",\"title\":\"<title>\",\"description\":\"\",\"success_criteria\":[\"<criterion>\"],\"dependencies\":[],\"priority\":1,\"risk\":\"low\"}],"
        + "\"waits\":[{\"lane\":\"main\",\"predicate\":\"lane_done\",\"required_criteria\":[]}]}\n"
        + "Rules: task_key must be unique inside the lane; dependencies may reference only this lane's tasks; waits.lane must name an existing lane of the target run; "
        + "any tool_scope entry must already be authorized for the target run; lanes declaring mutating tools are rejected. "
        + "Output the line only when the user actually defers work; otherwise output none.";

    private const string LaneOpenMarker = "LANE_OPEN:";

    /// <summary>
    /// Strips every protocol line from the meeting text and persists the last
    /// one as a pending orchestration directive on the target run. The line
    /// never reaches the user stream; the user-visible confirmation is
    /// generated here, not by the model.
    /// </summary>
    private async Task<string> FinalizeInteractionTextAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        string text,
        CancellationToken cancellationToken)
    {
        var extracted = ExtractLaneOpenLine(text);
        if (extracted is null) return text;
        var (cleanText, laneKey, payload) = extracted.Value;
        var sourceRunId = Guid.Parse(run.RunId);
        if (checkpoint.TargetRunId is not { } targetRunId)
        {
            await AppendEventAsync(sourceRunId, "orchestration.directive.rejected",
                "A protocol line was dropped because this interaction has no target run.",
                new { verb = "LANE_OPEN", code = "target_run_required" }, cancellationToken).ConfigureAwait(false);
            return cleanText;
        }
        // Admission validates against the TARGET run's frozen facts: the lane
        // will execute under the target's frozen manifest and permission mode.
        // The interaction's own source run never freezes a manifest (only
        // new_task turns do), so validating against the source configuration
        // would reject every tool-scoped lane with a widening error even when
        // the target authorizes every declared tool.
        var targetFrozen = await _lifecycle.GetFrozenRunConfigurationAsync(targetRunId.ToString(), cancellationToken).ConfigureAwait(false);
        FrozenRunConfigurationV1? targetConfiguration = null;
        if (targetFrozen is not null)
        {
            try { targetConfiguration = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(targetFrozen.Content, JsonOptions); }
            catch (JsonException) { }
        }
        if (targetConfiguration is null || !string.Equals(targetConfiguration.ContentHash, targetFrozen!.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            await AppendEventAsync(targetRunId, "orchestration.directive.rejected",
                "A meeting-authored orchestration directive was dropped because the target run's frozen configuration could not be verified.",
                new { verb = "LANE_OPEN", code = "target_configuration_unavailable", source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
            return cleanText;
        }
        if (!targetConfiguration.Orchestration.LanesEnabled)
        {
            await AppendEventAsync(targetRunId, "orchestration.directive.rejected",
                "A meeting-authored orchestration directive was dropped because lanes are disabled for the target run.",
                new { verb = "LANE_OPEN", code = "lanes_disabled", source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
            return cleanText;
        }
        if (_directiveValidator is null)
        {
            await AppendEventAsync(targetRunId, "orchestration.directive.rejected",
                "A meeting-authored orchestration directive was dropped because no directive validator is registered.",
                new { verb = "LANE_OPEN", code = "validator_unavailable", source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
            return cleanText;
        }

        // Admission-side validation runs on conservative facts: the persisted
        // checkpoint projection carries no lane list, so duplicates and budgets
        // are enforced authoritatively at consumption against the live checkpoint.
        var knownLanes = new List<string> { "main" };
        var frozenToolIds = targetConfiguration.ToolManifest
            .Select(entry => entry.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        var mutatingToolIds = targetConfiguration.ToolManifest
            .Where(entry => entry.MutatesWorkspace)
            .Select(entry => entry.Id)
            .ToList();
        var rejection = _directiveValidator.Validate(
            new OrchestrationDirectiveCandidate("LANE_OPEN", laneKey, payload),
            new OrchestrationDirectiveRules(
                targetConfiguration.Orchestration.LanesEnabled,
                1,
                targetConfiguration.Orchestration.MaxLanesPerRun,
                targetConfiguration.Orchestration.MaxTasksPerLane,
                frozenToolIds,
                mutatingToolIds,
                knownLanes,
                targetConfiguration.PermissionMode));
        if (rejection is not null)
        {
            await AppendEventAsync(targetRunId, "orchestration.directive.rejected",
                "A meeting-authored orchestration directive failed validation.",
                new { verb = "LANE_OPEN", code = rejection.Code, reason = rejection.Reason, source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
            return cleanText + $"\n\nOrchestration request rejected ({rejection.Code}): {rejection.Reason}";
        }

        var payloadHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var queued = await _lifecycle.EnqueueRunDirectiveAsync(new RunDirectiveWrite(
            targetRunId,
            checkpoint.SessionId,
            null,
            "orchestration",
            payload,
            $"directive:{targetRunId}:{sourceRunId}:{payloadHash}"), cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(sourceRunId, "orchestration.directive.queued",
            "A meeting-authored orchestration directive was queued for the target run.",
            new { directive_id = queued.Id, verb = "LANE_OPEN", lane_key = laneKey, source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
        return cleanText + $"\n\nOrchestration registered: lane '{laneKey}' will open on the target run once its waits are satisfied.";
    }

    private static (string CleanText, string LaneKey, string PayloadJson)? ExtractLaneOpenLine(string text)
    {
        string? laneKey = null, payload = null;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith(LaneOpenMarker, StringComparison.Ordinal)) continue;
            payload = line[LaneOpenMarker.Length..].Trim();
            laneKey = TryReadLaneKey(payload);
            lines[index] = string.Empty;
        }
        if (payload is null) return null;
        return (string.Join('\n', lines).TrimEnd(), laneKey ?? string.Empty, payload);
    }

    private static string? TryReadLaneKey(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("lane_key", out var key)
                && key.ValueKind == JsonValueKind.String)
            {
                return key.GetString()?.Trim();
            }
        }
        catch (JsonException) { }
        return null;
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
                foreach (var lane in checkpoint.Lanes)
                {
                    lane.Escalated = false;
                    lane.Status = "pending";
                    lane.SupervisionDecision = null;
                    lane.SupervisionReasons = [];
                    lane.LastGateFactsHash = null;
                }
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
    /// the session. Queued interactions keep the M2 semantics; an orchestration
    /// directive arriving at terminal fails closed (a new lane is meaningless on
    /// a finished run). The pending-status filter makes a replayed finalize
    /// idempotent.
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
            if (string.Equals(directive.Kind, "orchestration", StringComparison.Ordinal))
            {
                await AppendEventAsync(runId, "orchestration.directive.rejected",
                    "An orchestration directive arrived at run terminal and cannot materialize a lane.",
                    new { directive_id = directive.Id, code = "run_terminal", kind = directive.Kind }, cancellationToken).ConfigureAwait(false);
                await _lifecycle.DrainRunDirectivesAsync(checkpoint.RunId, [directive.Id], "rejected", cancellationToken).ConfigureAwait(false);
                checkpoint.DirectiveCursor++;
                continue;
            }
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

    /// <summary>
    /// The M4 orchestration port consumer: the target run's owner drains
    /// pending meeting-authored directives beside the context-patch pass and
    /// materializes accepted lanes into the checkpoint. Every failure is
    /// fail-closed and audited; consumed rows are marked so finalize cannot
    /// re-see them.
    /// </summary>
    private async Task<bool> ApplyPendingOrchestrationDirectivesAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var pending = await _lifecycle.ListPendingRunDirectivesAsync(checkpoint.RunId, cancellationToken).ConfigureAwait(false);
        var applied = false;
        foreach (var directive in pending.Where(item => string.Equals(item.Kind, "orchestration", StringComparison.Ordinal)).ToList())
        {
            var rejection = await ConsumeOrchestrationDirectiveAsync(run, configuration, checkpoint, directive, cancellationToken).ConfigureAwait(false);
            await _lifecycle.DrainRunDirectivesAsync(
                checkpoint.RunId,
                [directive.Id],
                rejection is null ? "consumed" : "rejected",
                cancellationToken).ConfigureAwait(false);
            checkpoint.DirectiveCursor++;
            applied = true;
        }
        return applied;
    }

    private async Task<OrchestrationDirectiveRejection?> ConsumeOrchestrationDirectiveAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RunDirective directive,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        if (_directiveValidator is null)
        {
            await AppendEventAsync(runId, "orchestration.directive.rejected",
                "An orchestration directive was rejected because no directive validator is registered.",
                new { directive_id = directive.Id, code = "validator_unavailable" }, cancellationToken).ConfigureAwait(false);
            return new OrchestrationDirectiveRejection("validator_unavailable", "No directive validator is registered.");
        }

        OrchestrationDirectiveCandidate candidate;
        List<LaneWait> waits;
        List<DurableTaskNode> nodes;
        string? goal = null;
        string? title = null;
        try
        {
            using var document = JsonDocument.Parse(directive.PayloadJson);
            var laneKey = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("lane_key", out var key)
                && key.ValueKind == JsonValueKind.String
                    ? key.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
            candidate = new OrchestrationDirectiveCandidate("LANE_OPEN", laneKey, directive.PayloadJson);
            goal = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("goal", out var goalElement)
                && goalElement.ValueKind == JsonValueKind.String
                    ? goalElement.GetString()?.Trim()
                    : null;
            title = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()?.Trim()
                    : null;
            waits = ReadLaneWaits(document.RootElement);
            // A goal-only payload carries no task graph: the lane's own planning
            // agent materializes it, so there is nothing to validate here.
            var laneTasks = ReadLaneTasks(document.RootElement);
            nodes = laneTasks.Length == 0
                ? []
                : ValidateAndMaterializeGraph(laneTasks, configuration.Orchestration.MaxTasksPerLane);
        }
        catch (Exception ex) when (ex is JsonException or InvalidTaskGraphException)
        {
            await AppendEventAsync(runId, "orchestration.directive.rejected",
                "An orchestration directive failed lane materialization.",
                new { directive_id = directive.Id, code = "invalid_lane_payload", reason = ex.Message }, cancellationToken).ConfigureAwait(false);
            return new OrchestrationDirectiveRejection("invalid_lane_payload", ex.Message);
        }

        var knownLanes = checkpoint.Lanes.Select(lane => lane.LaneKey).ToList();
        knownLanes.Add("main");
        var frozenToolIds = configuration.ToolManifest
            .Select(entry => entry.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        var mutatingToolIds = configuration.ToolManifest
            .Where(entry => entry.MutatesWorkspace)
            .Select(entry => entry.Id)
            .ToList();
        var rejection = _directiveValidator.Validate(candidate, new OrchestrationDirectiveRules(
            configuration.Orchestration.LanesEnabled,
            checkpoint.Lanes.Count,
            configuration.Orchestration.MaxLanesPerRun,
            configuration.Orchestration.MaxTasksPerLane,
            frozenToolIds,
            mutatingToolIds,
            knownLanes,
            configuration.PermissionMode));
        if (rejection is not null)
        {
            await AppendEventAsync(runId, "orchestration.directive.rejected",
                "An orchestration directive failed validation at consumption.",
                new { directive_id = directive.Id, code = rejection.Code, reason = rejection.Reason, lane_key = candidate.LaneKey }, cancellationToken).ConfigureAwait(false);
            return rejection;
        }

        foreach (var node in nodes)
        {
            node.LaneKey = candidate.LaneKey;
            if (waits.Count > 0) node.Waits = [.. waits];
        }
        checkpoint.Tasks.AddRange(nodes);
        SyncLanes(checkpoint);

        // A goal-only directive opens the lane in planning and hands the task
        // graph to the lane's own planning agent: literally a second, parallel
        // task-planning agent running inside the same run lease. The lane passes
        // through the planning phase even though the work happens during this
        // consumption, so the event sequence is the same for scripted and real
        // routing.
        if (nodes.Count == 0)
        {
            var lane = checkpoint.Lanes.FirstOrDefault(item =>
                string.Equals(item.LaneKey, candidate.LaneKey, StringComparison.OrdinalIgnoreCase));
            var laneGoal = goal ?? title ?? $"Plan and complete lane '{candidate.LaneKey}'.";
            if (lane is not null) lane.Status = "planning";
            await AppendEventAsync(runId, "orchestration.lane_planning",
                "A goal-only lane opened and its own planning agent is deriving the task graph.",
                new { directive_id = directive.Id, lane_key = candidate.LaneKey, goal = laneGoal },
                cancellationToken).ConfigureAwait(false);
            try
            {
                var lanePlanner = await EnsureLanePlannerAsync(run, configuration, checkpoint, candidate.LaneKey, cancellationToken).ConfigureAwait(false);
                var planned = await PlanLaneTasksAsync(run, configuration, checkpoint, candidate.LaneKey,
                    lanePlanner, laneGoal, cancellationToken).ConfigureAwait(false);
                foreach (var node in planned)
                {
                    node.LaneKey = candidate.LaneKey;
                    if (waits.Count > 0) node.Waits = [.. waits];
                }
                checkpoint.Tasks.AddRange(planned);
                SyncLanes(checkpoint);
                await AppendEventAsync(runId, "orchestration.lane_planned",
                    "A lane's own planning agent materialized the lane's task graph.",
                    new
                    {
                        directive_id = directive.Id,
                        lane_key = candidate.LaneKey,
                        planner_instance_id = lanePlanner.Id,
                        task_count = planned.Count,
                        task_keys = planned.Select(node => node.TaskKey).ToArray()
                    }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidTaskGraphException or InvalidOperationException)
            {
                // The lane's own plan failed. Escalating keeps the user's intent
                // durably parked on a review request instead of dropping the
                // directive or leaving a planning lane that never dispatches.
                await AppendEventAsync(runId, "orchestration.lane_planning_failed",
                    "A lane's own planning agent failed to derive the task graph.",
                    new { directive_id = directive.Id, lane_key = candidate.LaneKey, reason = ex.Message },
                    cancellationToken).ConfigureAwait(false);
                if (lane is not null)
                {
                    await EscalateLaneAsync(runId, run, lane,
                        $"Lane planning failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
                }
                return null;
            }
        }

        // A lane consumed mid-tick would otherwise dispatch in the same tick it
        // was created, because the classification pass already ran. If its waits
        // are unmet it must park now, exactly as the classification pass would
        // have; only a parked lane passes its gate before dispatching.
        var consumedLane = checkpoint.Lanes.FirstOrDefault(item =>
            string.Equals(item.LaneKey, candidate.LaneKey, StringComparison.OrdinalIgnoreCase));
        if (consumedLane is not null && !consumedLane.Escalated && consumedLane.Status is "pending" or "planning")
        {
            var unmetWaits = UnmetLaneWaits(checkpoint, candidate.LaneKey);
            if (unmetWaits.Count > 0)
            {
                consumedLane.Status = "waiting";
                foreach (var task in checkpoint.Tasks.Where(item =>
                    string.Equals(LaneKeyOf(item), candidate.LaneKey, StringComparison.OrdinalIgnoreCase)
                    && item.Status is "pending" or "ready"))
                {
                    task.Waits = [.. unmetWaits];
                }
                await AppendEventAsync(runId, "orchestration.lane_waiting",
                    $"Lane '{candidate.LaneKey}' is waiting on other lanes.",
                    new { lane_key = candidate.LaneKey, waits = unmetWaits.Select(wait => new { lane = wait.LaneKey, predicate = wait.Predicate, facts_hash = wait.ObservedFactsHash }) },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // A directive consumed while every earlier lane already finished would
        // otherwise land on a finalizing checkpoint and never execute.
        if (checkpoint.Phase is "finalizing" or "done" or "completed")
        {
            checkpoint.Phase = "executing";
            await _lifecycle.SetRunStatusAsync(run.RunId, "executing", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await AppendEventAsync(runId, "orchestration.lane_opened",
            "A meeting-authored lane was materialized on this run.",
            new
            {
                directive_id = directive.Id,
                lane_key = candidate.LaneKey,
                task_count = checkpoint.Tasks.Where(item => string.Equals(LaneKeyOf(item), candidate.LaneKey, StringComparison.OrdinalIgnoreCase)).Count(),
                task_keys = checkpoint.Tasks
                    .Where(item => string.Equals(LaneKeyOf(item), candidate.LaneKey, StringComparison.OrdinalIgnoreCase))
                    .Select(node => node.TaskKey).ToArray(),
                waits = waits.Select(wait => new { lane = wait.LaneKey, predicate = wait.Predicate }).ToArray()
            }, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Spawns the lane's own planning agent instance. It reuses the frozen
    /// roster authorization chain through CreateRootAsync, so the instance's
    /// allowed tools and budget come from the run's frozen configuration, and
    /// carries the lane key so the audit trail can attribute planning calls.
    /// </summary>
    private async Task<RuntimeAgentInstance> EnsureLanePlannerAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        string laneKey,
        CancellationToken cancellationToken)
    {
        if (checkpoint.LanePlannerIds.TryGetValue(laneKey, out var existing))
        {
            var all = await _instances.ListByRunAsync(Guid.Parse(run.RunId), cancellationToken).ConfigureAwait(false);
            var found = all.FirstOrDefault(item => item.Id == existing);
            if (found is not null) return found;
        }
        var plannerDefinition = RequiredAgent(configuration.ExecutionAgents, "task_planner");
        var instance = await _instances.CreateRootAsync(new RuntimeAgentSeed(
            Guid.Parse(run.SessionId), Guid.Parse(run.RunId), plannerDefinition.Id, plannerDefinition.Layer,
            plannerDefinition.Role, "chat", plannerDefinition.Capabilities, plannerDefinition.AllowedTools,
            ["workspace"], configuration.Context.DefaultTokenBudget,
            AgentDefinitionId: plannerDefinition.AgentDefinitionId,
            AgentVersionId: plannerDefinition.AgentVersionId,
            VersionContentHash: plannerDefinition.VersionContentHash,
            LaneKey: laneKey), cancellationToken).ConfigureAwait(false);
        checkpoint.LanePlannerIds[laneKey] = instance.Id;
        await AppendEventAsync(Guid.Parse(run.RunId), "agent.created",
            $"Task planning agent created for lane '{laneKey}'.",
            new { agent_instance_id = instance.Id, layer = instance.Layer, role = instance.Role, lane_key = laneKey },
            cancellationToken).ConfigureAwait(false);
        return instance;
    }

    /// <summary>
    /// Plans a lane's task graph through that lane's own planning instance,
    /// reusing the shared planner's model call, roster, and graph validation.
    /// The task budget is the frozen per-lane limit rather than the run limit.
    /// </summary>
    private async Task<List<DurableTaskNode>> PlanLaneTasksAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        string laneKey,
        RuntimeAgentInstance lanePlanner,
        string laneGoal,
        CancellationToken cancellationToken)
    {
        var plannerDefinition = RequiredAgent(configuration.ExecutionAgents, "task_planner");
        var context = await BuildContextAsync(run, configuration, plannerDefinition.Id, laneGoal, cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(plannerDefinition, context, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "context.packed",
            $"Lane '{laneKey}' planner context assembled.", new
            {
                lane_key = laneKey,
                evidence_count = context.Evidence.Count,
                estimated_tokens = context.EstimatedTokens,
                token_budget = context.TokenBudget,
                context_revision = checkpoint.ContextRevision
            }, cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var contextForPlanner = CreateRunContext(run, checkpoint);
                var planner = new PlanningAgent(CreateModelFactory(configuration, checkpoint, plannerDefinition,
                    lanePlanner.Id, null), _logger);
                var planned = await planner.PlanAsync(
                    contextForPlanner,
                    BuildFrozenPlannerRoster(configuration),
                    PlannerInstructions(checkpoint, assembly.Instructions, laneKey),
                    cancellationToken).ConfigureAwait(false);
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, planner.LastUsage);
                return ValidateAndMaterializeGraph(planned, configuration.Orchestration.MaxTasksPerLane);
            }
            catch (InvalidTaskGraphException ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidTaskGraphException("The lane planner returned no tasks.");
    }

    private static PlannedTask[] ReadLaneTasks(JsonElement payload)
    {
        // A goal-only directive carries no task graph: the lane's own planning
        // agent materializes it. An empty plan keeps the lane open without tasks.
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("tasks", out var tasksElement)
            || tasksElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var laneKey = payload.TryGetProperty("lane_key", out var lane) && lane.ValueKind == JsonValueKind.String
            ? lane.GetString()
            : null;
        var tasks = new List<PlannedTask>();
        foreach (var task in tasksElement.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Every directive task must be an object.");
            tasks.Add(new PlannedTask
            {
                TaskKey = task.TryGetProperty("task_key", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null,
                LaneKey = laneKey,
                Title = task.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() ?? string.Empty : string.Empty,
                Description = task.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null,
                SuccessCriteria = ReadStringArray(task, "success_criteria"),
                Dependencies = ReadStringArray(task, "dependencies"),
                RequiredCapabilities = ReadStringArray(task, "required_capabilities"),
                RequiredTools = ReadStringArray(task, "tool_scope"),
                Priority = task.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number && priority.TryGetInt32(out var value) ? value : 1,
                Risk = task.TryGetProperty("risk", out var risk) && risk.ValueKind == JsonValueKind.String ? risk.GetString() ?? "medium" : "medium"
            });
        }
        return [.. tasks];
    }

    private static List<LaneWait> ReadLaneWaits(JsonElement payload)
    {
        var waits = new List<LaneWait>();
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("waits", out var waitsElement)
            || waitsElement.ValueKind != JsonValueKind.Array)
        {
            return waits;
        }
        foreach (var wait in waitsElement.EnumerateArray())
        {
            if (wait.ValueKind != JsonValueKind.Object) continue;
            var lane = wait.TryGetProperty("lane", out var laneElement) && laneElement.ValueKind == JsonValueKind.String
                ? laneElement.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(lane)) continue;
            var predicate = wait.TryGetProperty("predicate", out var predicateElement) && predicateElement.ValueKind == JsonValueKind.String
                ? predicateElement.GetString()?.Trim()
                : null;
            var criteria = wait.TryGetProperty("required_criteria", out var criteriaElement) && criteriaElement.ValueKind == JsonValueKind.Array
                ? criteriaElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToList()
                : [];
            waits.Add(new LaneWait(lane, string.IsNullOrWhiteSpace(predicate) ? LaneWait.LaneDone : predicate!, criteria, null));
        }
        return waits;
    }

    private static string[] ReadStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return arrayElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
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

    private static string PlannerInstructions(FullDuplexCheckpointV1 checkpoint, string baseInstructions, string laneKey = "main")
    {
        // The Lane line lets scripted and real routing tell which lane a
        // planning or gate call belongs to; the initial plan runs on "main".
        baseInstructions += $"\nLane: {laneKey}";
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
    /// Each lane's own planning agent instance. A goal-only lane plans through
    /// its own instance so a deferred instruction literally runs a second
    /// parallel task-planning agent. Absent (pre-M8 checkpoints) or missing keys
    /// fall back to the shared run planner.
    /// </summary>
    public Dictionary<string, Guid> LanePlannerIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// Structured cross-lane waits this task is docked behind; the M3 tick
    /// keeps the task waiting until every wait's predicate holds over code
    /// facts and the frozen facts hash still matches at gate time.
    /// </summary>
    public List<LaneWait> Waits { get; set; } = [];

    /// <summary>
    /// Per-criterion verdicts produced by supervision (M3). Empty means no
    /// lane-gate review has run for this task yet.
    /// </summary>
    public List<CriterionVerdict> CriteriaVerdicts { get; set; } = [];
}

    /// <summary>
    /// Per-criterion supervised verdict for a lane-gated task. ReviewedBy and
    /// Round attribute the verdict to a supervision pass; a gate may only
    /// proceed when every recorded verdict is satisfied with evidence.
    /// </summary>
    internal sealed record CriterionVerdict(string Criterion, bool Satisfied, string? Evidence, string? ReviewedBy = null, int Round = 0);

/// <summary>
/// A structured cross-lane wait: the owning task is docked behind
/// <paramref name="LaneKey"/> until the predicate holds over code facts.
/// ObservedFactsHash freezes the target lane's observable state at park time
/// so a gate cannot proceed against facts that moved underneath it.
/// </summary>
[JsonConverter(typeof(LaneWaitJsonConverter))]
internal sealed record LaneWait(string LaneKey, string Predicate, IReadOnlyList<string> RequiredCriteria, string? ObservedFactsHash)
{
    public const string LaneDone = "lane_done";
    public const string LaneTasksCompleted = "lane_tasks_completed";
    public const string LaneSupervisionPass = "lane_supervision_pass";

    public static LaneWait UntilLaneDone(string laneKey) => new(laneKey, LaneDone, [], null);
}

/// <summary>
/// Reads M2-era waits serialized as plain lane-key strings ("main") as
/// lane_done waits; writes the full structured shape.
/// </summary>
internal sealed class LaneWaitJsonConverter : JsonConverter<LaneWait>
{
    public override LaneWait? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var key = reader.GetString();
            return string.IsNullOrWhiteSpace(key) ? null : new LaneWait(key.Trim(), LaneWait.LaneDone, [], null);
        }
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("LaneWait must be a string or an object.");
        string? laneKey = null, predicate = null, hash = null;
        var criteria = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "lane_key" when reader.TokenType == JsonTokenType.String: laneKey = reader.GetString(); break;
                case "predicate" when reader.TokenType == JsonTokenType.String: predicate = reader.GetString(); break;
                case "observed_facts_hash" when reader.TokenType == JsonTokenType.String: hash = reader.GetString(); break;
                case "required_criteria" when reader.TokenType == JsonTokenType.StartArray:
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String) criteria.Add(reader.GetString() ?? string.Empty);
                    }
                    break;
                default: reader.Skip(); break;
            }
        }
        if (string.IsNullOrWhiteSpace(laneKey)) throw new JsonException("LaneWait requires lane_key.");
        return new LaneWait(laneKey.Trim(), string.IsNullOrWhiteSpace(predicate) ? LaneWait.LaneDone : predicate!, criteria, hash);
    }

    public override void Write(Utf8JsonWriter writer, LaneWait value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("lane_key", value.LaneKey);
        writer.WriteString("predicate", value.Predicate);
        writer.WriteStartArray("required_criteria");
        foreach (var criterion in value.RequiredCriteria) writer.WriteStringValue(criterion);
        writer.WriteEndArray();
        if (value.ObservedFactsHash is { } hash) writer.WriteString("observed_facts_hash", hash);
        writer.WriteEndObject();
    }
}

/// <summary>A named execution lane recorded in the run checkpoint.</summary>
internal sealed class DurableLane
{
    public string LaneKey { get; set; } = string.Empty;

    /// <summary>
    /// Lane phase: planning | executing | waiting | gate_review | reviewing |
    /// finalizing | done | failed. "completed" is the M2 spelling of "done"
    /// and is still written/read for checkpoint compatibility.
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>Supervision attribution copied from the run-level verdict.</summary>
    public int SupervisionRound { get; set; }
    public string? SupervisionDecision { get; set; }
    public List<string> SupervisionReasons { get; set; } = [];

    /// <summary>
    /// Set when the lane's gate escalates (model decision or a rejected stale
    /// proceed). Only this lane freezes; the run status becomes awaiting_user
    /// while other lanes keep advancing.
    /// </summary>
    public bool Escalated { get; set; }

    /// <summary>Facts hash observed at this lane's most recent gate review.</summary>
    public string? LastGateFactsHash { get; set; }
}

internal sealed record StaleTaskEvidence(
    long InputContextRevision,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset RecordedAt);

internal sealed class RunAwaitingExternalDecisionException : Exception;

internal sealed class WorkerUnavailableException(string message) : Exception(message);
