using System.Collections.Concurrent;
using System.Text;
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

    private readonly ILifecycleManager _lifecycle;
    private readonly IConversationStore _conversations;
    private readonly IAgentInstanceService _instances;
    private readonly IContextProvider _contextProvider;
    private readonly IPromptAssembler _promptAssembler;
    private readonly IAgentChatClientFactory _chatClients;
    private readonly IServiceProvider _services;
    private readonly ILogger<FullDuplexRunEngine> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    private readonly string _ownerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public FullDuplexRunEngine(
        ILifecycleManager lifecycle,
        IConversationStore conversations,
        IAgentInstanceService instances,
        IContextProvider contextProvider,
        IPromptAssembler promptAssembler,
        IAgentChatClientFactory chatClients,
        IServiceProvider services,
        ILogger<FullDuplexRunEngine> logger)
    {
        _lifecycle = lifecycle;
        _conversations = conversations;
        _instances = instances;
        _contextProvider = contextProvider;
        _promptAssembler = promptAssembler;
        _chatClients = chatClients;
        _services = services;
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
                    _logger.LogWarning(ex, "Durable full-duplex recovery scan failed.");
                }
                nextScan = DateTimeOffset.UtcNow.Add(ScanInterval);
            }

            while (_queue.Reader.TryRead(out var runId))
            {
                _queued.TryRemove(runId, out _);
                if (running.ContainsKey(runId)) continue;
                // Calling an async method establishes BackgroundService ownership without
                // using request-bound Task.Run. It starts at its first awaited I/O.
                running[runId] = ExecuteRunAsync(runId, stoppingToken);
            }

            foreach (var completed in running.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToArray())
            {
                try { await running[completed].ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) { _logger.LogError(ex, "Durable engine task {RunId} ended unexpectedly.", completed); }
                running.Remove(completed);
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
        var lease = await _lifecycle.TryAcquireRunLeaseAsync(runId.ToString(), _ownerId, LeaseDuration, stoppingToken).ConfigureAwait(false);
        if (!lease.Acquired) return;

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

            while (!stoppingToken.IsCancellationRequested)
            {
                await HeartbeatAsync(runId, stoppingToken).ConfigureAwait(false);
                run = await _lifecycle.GetRunStateAsync(runId.ToString(), stoppingToken).ConfigureAwait(false);
                if (run.Status == RunStatus.Cancelled)
                {
                    await FinalizeCancellationAsync(runId, checkpoint, stoppingToken).ConfigureAwait(false);
                    return;
                }
                if (run.Status == RunStatus.Paused) return;
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
                        checkpoint = await RespondToInteractionAsync(run, checkpoint, stoppingToken).ConfigureAwait(false);
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
            _logger.LogError(ex, "Full-duplex run {RunId} failed in durable engine.", runId);
            var run = await _lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
            var checkpoint = await TryReadCheckpointAsync(runId).ConfigureAwait(false);
            if (checkpoint is not null) await FailRunAsync(runId, checkpoint, "runtime", SafeError(ex), CancellationToken.None).ConfigureAwait(false);
            else await FailLegacyRunAsync(runId, run, "runtime", SafeError(ex), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { await _lifecycle.ReleaseRunLeaseAsync(runId.ToString(), _ownerId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not release run lease {RunId}.", runId); }
        }
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

        var initial = new FullDuplexCheckpointV1
        {
            RunId = runId,
            SessionId = sessionId,
            TurnId = turnId,
            TriggerMessageId = trigger.Id,
            UserGoal = trigger.Content,
            Phase = "planning",
            InteractionKind = "new_task",
            ContextRevision = run.ContextRevision,
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

        var context = await BuildContextAsync(run, configuration, "task_planner", checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
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
                planned = await new PlanningAgent(_chatClients, _logger).PlanAsync(contextForPlanner, [], cancellationToken).ConfigureAwait(false);
                var materialized = ValidateAndMaterializeGraph(planned, configuration.Spawn.MaxAgentsPerRun);
                checkpoint.Tasks = checkpoint.PlanRevision == 0
                    ? materialized
                    : MergeReplannedGraph(checkpoint.Tasks, materialized);
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
        return checkpoint;
    }

    private async Task<FullDuplexCheckpointV1> ExecuteReadyTasksAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
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
                await ApplyTaskResultAsync(runId, checkpoint, resumed.Result, cancellationToken).ConfigureAwait(false);
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
            if (!contextChanged)
            {
                await ApplyTaskResultAsync(runId, checkpoint, result, cancellationToken).ConfigureAwait(false);
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
                await ApplyTaskResultAsync(runId, checkpoint, result.Result, cancellationToken).ConfigureAwait(false);
            }
        }

        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision,
            "tasks-completed", cancellationToken).ConfigureAwait(false);
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
                return new TaskExecutionResult(task.TaskId, worker.Id, "failed", failed);
            }
            if (model.Calls.Count != 0)
            {
                var failed = new StepResult { TaskNodeId = task.TaskId, AgentId = worker.Id.ToString("N"), Status = "failed", Summary = "The model requested a tool that was not advertised to this worker.", Evidence = [] };
                return new TaskExecutionResult(task.TaskId, worker.Id, "failed", failed);
            }
            var result = string.IsNullOrWhiteSpace(model.Text)
                ? new StepResult { TaskNodeId = task.TaskId, AgentId = worker.Id.ToString("N"), Status = "failed", Summary = "Execution returned no output.", Evidence = [] }
                : new StepResult { TaskNodeId = task.TaskId, AgentId = worker.Id.ToString("N"), Status = "completed", Summary = model.Text, Evidence = [model.Text] };
            return new TaskExecutionResult(task.TaskId, worker.Id, result.Status == "completed" ? "completed" : "failed", result);
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
        IReadOnlyList<WorkerToolDescriptor> descriptors;
        try
        {
            descriptors = await GetWorkerToolsAsync(run, configuration, task, cancellationToken).ConfigureAwait(false);
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
                if (dispatch.Status is ToolDispatchStatus.AwaitingApproval or ToolDispatchStatus.AwaitingResume)
                {
                    if (dispatch.Status == ToolDispatchStatus.AwaitingApproval)
                    {
                        await _lifecycle.SetRunStatusAsync(run.RunId, "awaiting_approval", dispatch.Message, cancellationToken).ConfigureAwait(false);
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
            if (!model.IsAvailable || model.Error is not null)
            {
                return FailedToolTask(checkpoint, task, worker, "model_unavailable", model.Error ?? "Worker model is unavailable.");
            }
            if (model.Calls.Count == 0)
            {
                var status = string.IsNullOrWhiteSpace(model.Text) ? "failed" : "completed";
                var summary = string.IsNullOrWhiteSpace(model.Text) ? "Execution returned no output." : model.Text;
                return new ToolTaskExecutionResult(checkpoint, Waiting: false,
                    new TaskExecutionResult(task.TaskId, worker.Id, status,
                        new StepResult
                        {
                            TaskNodeId = task.TaskId,
                            AgentId = worker.Id.ToString("N"),
                            Status = status,
                            Summary = summary,
                            Evidence = string.IsNullOrWhiteSpace(model.Text) ? [] : [model.Text]
                        }));
            }

            task.ToolRounds++;
            if (task.ToolRounds > configuration.Tools.MaxToolRounds)
            {
                return FailedToolTask(checkpoint, task, worker, "tool_round_limit", $"The worker exceeded the frozen max_tool_rounds limit ({configuration.Tools.MaxToolRounds}).");
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
        CancellationToken cancellationToken)
    {
        if (task.RequiredTools.Count == 0) return [];
        var sessions = _services.GetRequiredService<ISessionLocator>();
        var sessionId = Guid.Parse(run.SessionId);
        var session = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found while resolving worker tools.");
        var project = await sessions.FindProjectAsync(session.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found while resolving worker tools.");
        var processes = _services.GetRequiredService<IToolProcessManager>();
        var manifest = await processes.GetManifestAsync(project.RootPath, cancellationToken).ConfigureAwait(false);
        if (manifest.ProtocolVersion < 2) throw new InvalidOperationException("TinadecTools manifest v2 is required for autonomous workers.");
        if (!string.IsNullOrWhiteSpace(configuration.ToolManifestHash)
            && !string.Equals(configuration.ToolManifestHash, manifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The live TinadecTools manifest differs from the frozen run manifest.");

        var entries = new List<WorkerToolDescriptor>(task.RequiredTools.Count);
        foreach (var required in task.RequiredTools)
        {
            var entry = manifest.Tools.FirstOrDefault(item => string.Equals(item.Id, required, StringComparison.OrdinalIgnoreCase));
            if (entry is null) throw new KeyNotFoundException($"Required tool '{required}' is not present in the frozen manifest.");
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
        var instances = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (task.WorkerAgentId is { } assigned)
        {
            var existing = instances.FirstOrDefault(item => item.Id == assigned);
            if (existing is not null) return existing;
        }

        var worker = instances.FirstOrDefault(item => item.Generated && item.TaskId == task.TaskId && item.Status is "created" or "running");
        if (worker is null)
        {
            var parent = instances.FirstOrDefault(item => item.Id == plannerId)
                ?? throw new InvalidDataException("Planner instance is missing from the run lineage.");
            worker = await _instances.SpawnAsync(new AgentSpawnRequest(
                parent.Id,
                task.Description ?? task.Title,
                task.SuccessCriteria,
                ["session_history", "task_context", "reviewed_memory"],
                "chat",
                task.RequiredTools,
                ["workspace"],
                configuration.Context.DefaultTokenBudget,
                task.TaskId,
                "worker",
                new AgentSpawnLimits(configuration.Spawn.MaxDepth, configuration.Spawn.MaxAgentsPerRun, configuration.Spawn.MaxParallelWorkers)), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.created", "Execution worker created.", new
            {
                agent_instance_id = worker.Id,
                parent_instance_id = worker.ParentInstanceId,
                task_id = task.TaskId,
                layer = worker.Layer,
                role = worker.Role
            }, cancellationToken, task.TaskId).ConfigureAwait(false);
        }

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
        var context = await BuildContextAsync(run, configuration, worker.Id.ToString(),
            $"Task: {task.Title}\nDescription: {task.Description}\nSuccess criteria: {string.Join("; ", task.SuccessCriteria)}",
            cancellationToken).ConfigureAwait(false);
        var assembly = await _promptAssembler.AssembleAsync(worker.Id.ToString(), context, cancellationToken).ConfigureAwait(false);
        var agent = new AgentDefinition
        {
            Id = worker.Id,
            Name = worker.Role,
            Layer = worker.Layer,
            AgentType = worker.Role,
            ModelRoutePurpose = "chat",
            AllowedTools = worker.AllowedTools,
            Enabled = true
        };
        return await new ExecutionAgent(_chatClients, _logger).GetNextTurnAsync(
            CreateRunContext(run, checkpoint),
            agent,
            ToPlannedTask(task),
            task.ToolTurns,
            tools,
            assembly.Instructions,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyTaskResultAsync(
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
        _ = await BuildContextAsync(run, configuration, "supervisor", checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
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
        var verdict = configuration.Supervision.RequiredBeforeFinal
            ? await new SupervisionAgent(_chatClients, _logger).ReviewAsync(checkpoint.UserGoal, plans, results, checkpoint.SupervisionRound, cancellationToken).ConfigureAwait(false)
            : new SupervisionVerdict(SupervisionDecision.Pass, [], []);
        checkpoint.SupervisionDecision = verdict.DecictionString();
        checkpoint.SupervisionReasons = verdict.Reasons.ToList();
        await AppendEventAsync(runId, "supervision.completed", $"Supervision decision: {checkpoint.SupervisionDecision}.", new
        {
            decision = checkpoint.SupervisionDecision,
            revision_round = checkpoint.SupervisionRound,
            reasons = checkpoint.SupervisionReasons,
            revise_task_indexes = verdict.ReviseTaskIndexes
        }, cancellationToken).ConfigureAwait(false);

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
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        await _lifecycle.SetRunStatusAsync(run.RunId, "executing", cancellationToken: cancellationToken).ConfigureAwait(false);
        checkpoint.MeetingResponse = checkpoint.InteractionKind switch
        {
            "status_query" => await BuildStatusResponseAsync(checkpoint, cancellationToken).ConfigureAwait(false),
            _ => "I need a clearer instruction before changing the active task. Please state whether you want a status update, additional constraints, or a new goal."
        };
        checkpoint.SupervisionDecision = "pass";
        checkpoint.Phase = "finalizing";
        await AppendEventAsync(Guid.Parse(run.RunId), "meeting.interaction.responded", "A non-execution meeting interaction was completed.", new
        {
            interaction_kind = checkpoint.InteractionKind,
            target_run_id = checkpoint.TargetRunId,
            context_revision = checkpoint.ContextRevision
        }, cancellationToken).ConfigureAwait(false);
        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "interaction-response", cancellationToken).ConfigureAwait(false);
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
            if (patch.Kind == "goal_adjustment")
            {
                checkpoint.UserGoal = patch.Content;
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
        await _lifecycle.SetRunStatusAsync(run.RunId, "finalizing", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(checkpoint.MeetingResponse))
        {
            var context = await BuildContextAsync(run, configuration, "meeting", checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
            checkpoint.MeetingResponse = await GenerateMeetingResponseAsync(checkpoint, context, cancellationToken).ConfigureAwait(false);
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
        var finish = checkpoint.SupervisionDecision == "escalate" ? "completed_with_escalation" : "completed";
        await _lifecycle.AppendRunStreamAsync(run.RunId, new DurableRunStreamAppend(
            checkpoint.TurnId,
            "done",
            checkpoint.AssistantMessageId,
            FinishReason: finish,
            IdempotencyKey: $"run:{run.RunId}:turn:{checkpoint.TurnId}:done"), cancellationToken).ConfigureAwait(false);
        await _lifecycle.CompleteRunAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        await _instances.ReleaseRunInstancesAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinalizeCancellationAsync(Guid runId, FullDuplexCheckpointV1 checkpoint, CancellationToken cancellationToken)
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
        await _instances.ReleaseRunInstancesAsync(runId, cancellationToken).ConfigureAwait(false);
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

    private async Task<FullDuplexCheckpointV1> SaveCheckpointAsync(FullDuplexCheckpointV1 checkpoint, long expectedRevision, string key, CancellationToken cancellationToken)
    {
        var stored = await _lifecycle.SaveRunCheckpointAsync(checkpoint.RunId.ToString(), new RunCheckpointWrite(
            expectedRevision,
            checkpoint.Phase,
            JsonSerializer.Serialize(checkpoint, JsonOptions),
            IdempotencyKey: $"run:{checkpoint.RunId}:{key}:{expectedRevision + 1}"), cancellationToken).ConfigureAwait(false);
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
        var meetingDefinition = configuration.OperationAgents.FirstOrDefault(item => item.Id == "meeting")
            ?? throw new InvalidDataException("Frozen profile has no meeting agent.");
        var plannerDefinition = configuration.ExecutionAgents.FirstOrDefault(item => item.Id == "task_planner")
            ?? throw new InvalidDataException("Frozen profile has no task planner.");
        meeting ??= all.FirstOrDefault(item => !item.Generated && item.Role == meetingDefinition.Role);
        planner ??= all.FirstOrDefault(item => !item.Generated && item.Role == plannerDefinition.Role);
        if (meeting is null)
        {
            meeting = await _instances.CreateRootAsync(new RuntimeAgentSeed(sessionId, runId, meetingDefinition.Id, meetingDefinition.Layer,
                meetingDefinition.Role, "chat", meetingDefinition.Capabilities, [], ["workspace"], configuration.Context.DefaultTokenBudget), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.created", "Meeting agent created.", new { agent_instance_id = meeting.Id, layer = meeting.Layer, role = meeting.Role }, cancellationToken).ConfigureAwait(false);
        }
        if (planner is null)
        {
            planner = await _instances.CreateRootAsync(new RuntimeAgentSeed(sessionId, runId, plannerDefinition.Id, plannerDefinition.Layer,
                plannerDefinition.Role, "chat", plannerDefinition.Capabilities, ["*"], ["workspace"], configuration.Context.DefaultTokenBudget), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.created", "Task planning agent created.", new { agent_instance_id = planner.Id, layer = planner.Layer, role = planner.Role }, cancellationToken).ConfigureAwait(false);
        }
        return (meeting, planner);
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

    private async Task<string> GenerateMeetingResponseAsync(FullDuplexCheckpointV1 checkpoint, ContextPack context, CancellationToken cancellationToken)
    {
        var resolution = await _chatClients.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await _promptAssembler.AssembleAsync("meeting", context, cancellationToken).ConfigureAwait(false);
        var escalation = checkpoint.SupervisionDecision == "escalate"
            ? "\n\nIMPORTANT: supervision escalated this run. Explain the unresolved decision clearly and ask the user for direction."
            : string.Empty;
        var evidence = string.Join("\n", checkpoint.Tasks.Select(item => $"- [{item.ResultStatus ?? item.Status}] {item.ResultSummary}"));
        var instructions = assembly.Instructions + "\n\nYou are the meeting agent, the only user-facing agent. Reply directly and honestly in the user's language. Summarize completed work, evidence, limits, and next action. Do not claim tools ran if evidence does not say so." + escalation;
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nExecution evidence:\n{evidence}";
        var agent = new ChatClientAgent(_chatClients.Create(resolution), new ChatClientAgentOptions
        {
            Name = "meeting",
            ChatOptions = new ChatOptions { Instructions = instructions }
        });
        var response = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text)) response.Append(update.Text);
        }
        if (response.Length == 0) throw new InvalidOperationException("Meeting agent returned no output.");
        return response.ToString();
    }

    private static List<DurableTaskNode> ValidateAndMaterializeGraph(PlannedTask[] tasks, int maxTasks)
    {
        if (tasks.Length == 0) throw new InvalidTaskGraphException("The planner returned no tasks.");
        if (tasks.Length > maxTasks) throw new InvalidTaskGraphException($"The planner returned {tasks.Length} tasks, above the frozen limit of {maxTasks}.");
        var candidates = new List<(PlannedTask Task, string Key)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Title)) throw new InvalidTaskGraphException("Every planned task needs a title.");
            var key = NormalizeTaskKey(task.TaskKey, task.Title);
            if (!seen.Add(key)) throw new InvalidTaskGraphException($"Task key '{key}' is not unique.");
            candidates.Add((task, key));
        }
        var aliases = candidates.ToDictionary(item => item.Key, item => item.Key, StringComparer.OrdinalIgnoreCase);
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
            SuccessCriteria = candidate.Task.SuccessCriteria.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
            Dependencies = candidate.Task.Dependencies.Select(value => aliases.TryGetValue(value, out var dependency) ? dependency : throw new InvalidTaskGraphException($"Task '{candidate.Key}' references unknown dependency '{value}'.")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiredCapabilities = candidate.Task.RequiredCapabilities.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiredTools = candidate.Task.RequiredTools.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Priority = candidate.Task.Priority,
            Risk = string.IsNullOrWhiteSpace(candidate.Task.Risk) ? "medium" : candidate.Task.Risk.Trim(),
            Status = "pending"
        }).ToList();
        var byKey = result.ToDictionary(item => item.TaskKey, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in result) Visit(node.TaskKey);
        return result;

        void Visit(string key)
        {
            if (visited.Contains(key)) return;
            if (!visiting.Add(key)) throw new InvalidTaskGraphException("The task dependency graph contains a cycle.");
            foreach (var dependency in byKey[key].Dependencies) Visit(dependency);
            visiting.Remove(key);
            visited.Add(key);
        }
    }

    private static List<DurableTaskNode> MergeReplannedGraph(
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
                SuccessCriteria = item.SuccessCriteria,
                Dependencies = item.Dependencies,
                RequiredCapabilities = item.RequiredCapabilities,
                RequiredTools = item.RequiredTools,
                Priority = item.Priority,
                Risk = item.Risk,
                Status = prior.Status,
                Attempt = prior.Attempt,
                WorkerAgentId = prior.WorkerAgentId,
                InputContextRevision = prior.InputContextRevision,
                ResultStatus = prior.ResultStatus,
                ResultSummary = prior.ResultSummary,
                Evidence = prior.Evidence,
                StaleEvidence = prior.StaleEvidence,
                CompletedAt = prior.CompletedAt
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

    private Task AppendEventAsync(Guid runId, string type, string summary, object payload, CancellationToken ct, Guid? taskId = null) =>
        _lifecycle.AppendEventAsync(runId, type, payload, summary, taskId: taskId, cancellationToken: ct);

    private static bool IsTerminal(RunStatus status) => status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
    private static string SafeError(Exception ex) => ex is InvalidOperationException or InvalidDataException ? ex.Message : "Unexpected runtime failure.";

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

    private sealed class InvalidTaskGraphException(string message) : InvalidOperationException(message);

    private sealed record TaskExecutionResult(Guid TaskId, Guid? WorkerAgentId, string Status, StepResult Result);
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
    public List<DurableTaskNode> Tasks { get; set; } = [];
    public int SupervisionRound { get; set; }
    public string? SupervisionDecision { get; set; }
    public List<string> SupervisionReasons { get; set; } = [];
    public string? MeetingResponse { get; set; }
    public Guid? AssistantMessageId { get; set; }
}

internal sealed class DurableTaskNode
{
    public Guid TaskId { get; init; }
    public string TaskKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
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
    public long InputContextRevision { get; set; }
    public string? ResultStatus { get; set; }
    public string? ResultSummary { get; set; }
    public List<string> Evidence { get; set; } = [];
    public List<StaleTaskEvidence> StaleEvidence { get; set; } = [];
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed record StaleTaskEvidence(
    long InputContextRevision,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset RecordedAt);

internal sealed class RunAwaitingExternalDecisionException : Exception;
