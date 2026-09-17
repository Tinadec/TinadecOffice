using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.DmaEA;

/// <summary>
/// Durable execution owner for full-duplex runs. Admission and HTTP follow operations
/// never execute model work; this hosted service leases and resumes persisted runs.
/// </summary>
public interface IFullDuplexRunEngine
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default);
}

internal sealed partial class FullDuplexRunEngine : BackgroundService, IFullDuplexRunEngine
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

    /// <summary>
    /// Consecutive empty worker responses tolerated before the task is closed out
    /// (codex: <c>consecutive_empty_turns &gt;= 3</c>). One blank answer is noise;
    /// three in a row means the model has nothing to say about this task.
    /// </summary>
    private const int MaxConsecutiveEmptyResponses = 3;

    /// <summary>
    /// Fingerprints handed to repeat detection. It only inspects the trailing run
    /// of identical calls, so passing the tail keeps every round O(1) instead of
    /// rebuilding the whole transcript and turning a long task quadratic.
    /// </summary>
    private const int FingerprintTail = 3;

    /// <summary>Task-level failure category for a declaration surface that cannot be resolved.</summary>
    private const string ToolManifestUnavailableCategory = RunErrorTaxonomy.ToolManifestUnavailable;

    /// <summary>
    /// Task-level failure category for worker resolution/creation failures rooted in
    /// the frozen configuration or its persisted bindings. These fail only their own
    /// task; genuine engine invariants (e.g. a missing planner instance) still fail
    /// the run.
    /// </summary>
    private const string WorkerAssignmentInvalidCategory = RunErrorTaxonomy.WorkerAssignmentInvalid;

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
                    _logger.TryLogWarning(ex, "Durable full-duplex recovery scan failed.");
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
                catch (Exception ex) { _logger.TryLogError(ex, "Durable engine task {RunId} ended unexpectedly.", completed); }
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
            // R5 clean break: bodies frozen under a superseded schema are audit
            // records, not resumable state. Fail closed with an explicit code
            // instead of double-reading old shapes into the current model.
            if (!string.Equals(frozen.SchemaVersion, FrozenRunConfigurationV1.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                await FailLegacyRunAsync(runId, run, "run_schema_superseded",
                    $"The run was admitted under frozen-configuration schema '{frozen.SchemaVersion}' (superseded by '{FrozenRunConfigurationV1.CurrentSchemaVersion}') and cannot resume.", stoppingToken).ConfigureAwait(false);
                return;
            }

            FrozenRunConfigurationV1 configuration;
            try
            {
                configuration = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(frozen.Content, JsonOptions)
                    ?? throw new InvalidDataException("Frozen configuration is empty.");
                if (!string.Equals(configuration.ContentHash, frozen.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Frozen configuration hash does not match its stored body.");
                // Gate 3 re-verification on recovery: the operation deny floor, roster
                // topology and graph shape must hold for the resumed document too.
                // Session identity re-lock happens through the frozen bindings (null
                // here keeps the legacy semantics for pre-identity runs).
                RunFreezeGate.Validate(null, configuration.OperationAgents, configuration.ExecutionAgents, configuration.Graph);
            }
            catch (RunAdmissionException ex) when (string.Equals(ex.Code, "run_schema_superseded", StringComparison.Ordinal))
            {
                // R5 clean break: the lifecycle read already rejected the superseded
                // schema; record it on the run so the failure is self-explanatory.
                await FailLegacyRunAsync(runId, run, "run_schema_superseded", "The run was admitted under a superseded frozen-configuration schema and cannot resume.", stoppingToken).ConfigureAwait(false);
                return;
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

            // Lease recovery compensation (plan §4.3 item 5): "running" tasks with
            // no pending tool execution are orphans of a previous owner's dispatch
            // (e.g. a host crash mid-call). Reset them for re-execution before the
            // phase switch, or the persisted graph has no dispatchable task and the
            // run is failed as invalid. Tasks awaiting an external decision keep
            // their PendingToolExecutionId and go through the resume path instead.
            var interrupted = checkpoint.Tasks
                .Where(item => item.Status == "running" && string.IsNullOrWhiteSpace(item.PendingToolExecutionId))
                .ToList();
            // A run parked on a user decision (supervision escalation or a
            // graph-tier approval expiry) must not re-dispatch its tasks through
            // recovery compensation: the parked task is already terminal and the
            // run waits on an explicit user choice.
            if (run.Status == RunStatus.AwaitingUser || checkpoint.AwaitingApprovalExpiryReview) interrupted.Clear();
            if (interrupted.Count > 0)
            {
                foreach (var task in interrupted) task.Status = "pending";
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "recovery-interrupted-tasks", stoppingToken).ConfigureAwait(false);
                await AppendEventAsync(runId, "recovery.interrupted_tasks", "Interrupted running task(s) reset for re-execution after lease recovery.", new
                {
                    run_id = runId,
                    task_keys = interrupted.Select(item => item.TaskKey).ToArray()
                }, stoppingToken).ConfigureAwait(false);
            }

            // A supervision checkpoint is written before its run status. If a host
            // stops in that small window, repair the status before considering the
            // phase. Never let a recovered escalation fall through to finalization.
            // A graph-tier approval-expiry review is the same shape but NOT a
            // supervision escalation: it stays parked on the user decision no
            // matter how many stray ticks wake the run.
            if (!IsTerminal(run.Status)
                && checkpoint.Phase == "awaiting_user"
                && run.Status is not RunStatus.AwaitingUser and not RunStatus.Executing)
            {
                await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review.", stoppingToken).ConfigureAwait(false);
                return;
            }
            // A wake while the review is parked must re-park, never fall through to
            // the awaiting_user switch (which reads as "user chose continue").
            if (checkpoint.AwaitingApprovalExpiryReview && checkpoint.Phase == "awaiting_user")
            {
                await TrySetRunStatusAsync(run.RunId, "awaiting_user", null, stoppingToken).ConfigureAwait(false);
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

                // A graph-tier approval-expiry review parks the checkpoint on
                // awaiting_user even though the run status could not move there
                // (executing → awaiting_user is not a legal transition). Stay
                // parked until an explicit user decision clears the flag.
                if (checkpoint.AwaitingApprovalExpiryReview)
                {
                    await TrySetRunStatusAsync(run.RunId, "awaiting_user", null, stoppingToken).ConfigureAwait(false);
                    return;
                }

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

                // Sub-tasks the conversation identity queued through task_dispatch. Consumed
                // on the ORDINARY tick, not at run terminal: a dispatched sub-task has to
                // actually run, and the master keeps working in its own loop meanwhile.
                if (await ApplyPendingTaskDispatchesAsync(run, checkpoint, stoppingToken).ConfigureAwait(false))
                {
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-dispatched", stoppingToken).ConfigureAwait(false);
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
            _logger.TryLogError(ex, "Full-duplex run {RunId} failed in durable engine.", runId);
            var run = await _lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
            var checkpoint = await TryReadCheckpointAsync(runId).ConfigureAwait(false);
            // The taxonomy already had model categories; nothing ever wrote them, so a
            // provider outage and a genuine engine defect were indistinguishable in the
            // durable record AND in what the user was told. Classify instead of collapsing.
            var failureCode = ex switch
            {
                WorkerUnavailableException => RunErrorTaxonomy.WorkerUnavailable,
                ModelInvocationExhaustedException { Category: "candidate_unavailable" } => RunErrorTaxonomy.ModelUnavailable,
                ModelInvocationExhaustedException => RunErrorTaxonomy.Model,
                _ => RunErrorTaxonomy.Runtime
            };
            if (checkpoint is not null) await FailRunAsync(runId, checkpoint, failureCode, SafeError(ex), CancellationToken.None).ConfigureAwait(false);
            else await FailLegacyRunAsync(runId, run, failureCode, SafeError(ex), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { await _lifecycle.ReleaseRunLeaseAsync(runId.ToString(), _ownerId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.TryLogDebug(ex, "Could not release run lease {RunId}.", runId); }
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
        await TrySetRunStatusAsync(run.RunId,
            checkpoint.PlanRevision == 0 ? "understanding" : "replanning",
            null, cancellationToken).ConfigureAwait(false);

        // The CONVERSATION IDENTITY authors the task graph (PlannerAgentId
        // semantics = task-graph author instance). Schema v2 freezes a Graph
        // section for every mode, so there is no legacy planner fallback.
        var graph = configuration.Graph
            ?? throw new InvalidDataException("Frozen run configuration carries no declared graph (schema v2 freezes one for every mode).");
        var author = await EnsureConversationAuthorAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        checkpoint.MeetingAgentId = author.Id;
        checkpoint.PlannerAgentId = author.Id;
        var plannerDefinition = RequiredConversationAgent(configuration);
        if (!checkpoint.GraphTierAnnounced)
        {
            var graphRunId = Guid.Parse(run.RunId);
            await AppendEventAsync(graphRunId, "orchestration.mode_tier_decided",
                "Declared-graph tier decided at admission.", new
                {
                    run_id = run.RunId,
                    tier = graph.Tier,
                    conversation_slug = graph.ConversationTemplateSlug,
                    edge_count = graph.Edges.Count
                }, cancellationToken, idempotencyKey: $"run:{run.RunId}:tier").ConfigureAwait(false);
            checkpoint.GraphTierAnnounced = true;
        }

        // solo_dispatch: the master does the work itself, so there is no task graph to
        // author — planning a graph and then having one instance execute it would be a
        // detour with a wasted model call. The master's own work is modelled as ONE task
        // that it executes, which is what lets it reuse the checkpointed tool-turn loop
        // (approval parking, failure feedback, loop guard, tool timeline) instead of
        // needing a second, ungoverned execution path.
        if (string.Equals(graph.Tier, FrozenGraphTiers.SoloDispatch, StringComparison.Ordinal)
            && checkpoint.PlanRevision == 0)
        {
            checkpoint.Tasks = BuildSoloMasterTask(configuration, checkpoint);
            SyncLanes(checkpoint);
            checkpoint.PlanRevision++;
            checkpoint.Phase = "executing";
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "solo-plan", cancellationToken).ConfigureAwait(false);
            await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
            var soloRunId = Guid.Parse(run.RunId);
            var soloTask = checkpoint.Tasks[0];
            await AppendEventAsync(soloRunId, "task_graph.created",
                "1 solo task planned: the conversation identity executes the goal itself.", new
                {
                    run_id = run.RunId,
                    plan_revision = checkpoint.PlanRevision,
                    task_count = checkpoint.Tasks.Count,
                    task_keys = checkpoint.Tasks.Select(item => item.TaskKey).ToArray(),
                    tier = graph.Tier
                }, cancellationToken).ConfigureAwait(false);
            // The assignment is pre-resolved (no planner ran), so announce it here: the
            // ordinary dispatch emits worker.assigned when IT picks the worker, and the
            // desktop's activity view reads this event to name the active agent.
            await AppendEventAsync(soloRunId, "worker.assigned",
                $"Solo master '{plannerDefinition.Id}' takes its own task.", new
                {
                    task_id = soloTask.TaskId,
                    task_key = soloTask.TaskKey,
                    agent_slug = plannerDefinition.Id,
                    agent_definition_id = plannerDefinition.AgentDefinitionId,
                    agent_version_id = plannerDefinition.AgentVersionId,
                    agent_version_hash = plannerDefinition.VersionContentHash,
                    reason = soloTask.WorkerAssignmentReason,
                    required_capabilities = soloTask.RequiredCapabilities,
                    required_tools = soloTask.RequiredTools
                }, cancellationToken, soloTask.TaskId).ConfigureAwait(false);
            return checkpoint;
        }
        var context = await BuildContextAsync(run, configuration, plannerDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(configuration, plannerDefinition, context, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "context.packed", "Planner context assembled.", new
        {
            evidence_count = context.Evidence.Count,
            estimated_tokens = context.EstimatedTokens,
            token_budget = context.TokenBudget,
            context_revision = checkpoint.ContextRevision
        }, cancellationToken).ConfigureAwait(false);

        PlannedTask[] planned = [];
        Exception? lastError = null;
        var plannerHead = string.Empty;
        var plannerParseError = string.Empty;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var contextForPlanner = CreateRunContext(run, checkpoint);
                var planner = new PlanningAgent(CreateModelFactory(configuration, checkpoint, plannerDefinition,
                    checkpoint.PlannerAgentId, null), _logger);
                // Attempt 2 carries the first attempt's failure back to the model: a
                // byte-identical blind resend re-commits the same mistake (the 2026-09-17
                // plan_parse_failed cluster failed both attempts with the same scalar
                // success_criteria shape).
                var instructionsForAttempt = attempt == 0
                    ? PlannerInstructions(checkpoint, assembly.Instructions)
                    : PlannerInstructions(checkpoint, assembly.Instructions) + BuildPlannerRetryHint(lastError, plannerParseError);
                planned = await planner.PlanAsync(
                    contextForPlanner,
                    BuildFrozenPlannerRoster(configuration),
                    instructionsForAttempt,
                    cancellationToken).ConfigureAwait(false);
                plannerHead = planner.LastResponseHead;
                plannerParseError = planner.LastParseErrorDetail;
                RefuseSilentPlanningFallbackOnDeclaredEdges(configuration, planned);
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, planner.LastUsage);
                // A PARSED empty array is a legitimate plan, not a failure: a greeting or a
                // pure question has no subtasks, and the meeting agent answers it from the
                // conversation. Two steps must therefore be skipped for it — the fallback
                // refusal (there is no fallback; the parse succeeded) and materialization
                // (which rejects an empty list). The run then carries zero tasks, the
                // dispatch pass finds nothing ready, sees every task terminal, marks the
                // phase "reviewing" and goes straight to the answer.
                var noWorkToPlan = planner.LastPlanWasParsed && planned.Length == 0 && checkpoint.PlanRevision == 0;
                var materialized = noWorkToPlan
                    ? new List<DurableTaskNode>()
                    : ValidateAndMaterializeGraph(planned, configuration.Spawn.MaxAgentsPerRun);
                checkpoint.Tasks = checkpoint.PlanRevision == 0
                    ? materialized
                    : MergeReplannedGraph(checkpoint.Tasks, materialized);
                SyncLanes(checkpoint);
                if (noWorkToPlan)
                {
                    await AppendEventAsync(Guid.Parse(run.RunId), "task_graph.empty",
                        "The planner produced an empty plan; this goal needs no execution work.",
                        new { run_id = run.RunId, user_goal = checkpoint.UserGoal }, cancellationToken).ConfigureAwait(false);
                }
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
            // Carry the planner's actual answer onto the run. The failure message alone
            // ("not a task array") cannot be acted on: it does not distinguish prose,
            // truncated JSON, and an empty plan — and an empty plan is legitimate, so
            // without this the difference is invisible to anyone not holding the console.
            await AppendEventAsync(Guid.Parse(run.RunId), "run.plan_diagnostic",
                "Planner raw answer captured because the plan was refused.",
                new
                {
                    run_id = run.RunId,
                    reason = lastError.Message,
                    parse_error = plannerParseError,
                    response_head = plannerHead,
                    response_head_length = plannerHead.Length
                }, cancellationToken).ConfigureAwait(false);
            await FailRunAsync(Guid.Parse(run.RunId), checkpoint, "invalid_task_graph", lastError.Message, cancellationToken).ConfigureAwait(false);
            return checkpoint;
        }

        checkpoint.PlanRevision++;
        checkpoint.Phase = "executing";
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "planned", cancellationToken).ConfigureAwait(false);
        await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
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
        await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);

        // A previous worker may have stopped after PrepareAsync persisted an
        // execution. Resume it before asking a model to produce another call.
        // Terminal tasks never carry a live pending execution (failed/expired
        // executions are detached when the task reaches its terminal state).
        var pending = checkpoint.Tasks.FirstOrDefault(HasLivePendingExecution);
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
            // A graph-tier approval expiry with no lane row marks its task failed
            // and parks the checkpoint on awaiting_user. Keep that boundary: the
            // engine must not finalize past a pending user review (a stray wake
            // would otherwise swallow the escalation).
            if (string.Equals(checkpoint.Phase, "awaiting_user", StringComparison.Ordinal))
                throw new RunAwaitingExternalDecisionException();
            if (checkpoint.Tasks.All(item => item.Status is "completed" or "failed" or "blocked"))
            {
                checkpoint.Phase = "reviewing";
                return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "execution-complete", cancellationToken).ConfigureAwait(false);
            }
            if (checkpoint.Tasks.Any(HasLivePendingExecution))
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
        // writes from independent workers. A worker that cannot be resolved or
        // created from the frozen configuration fails only its own task.
        var workers = new Dictionary<Guid, RuntimeAgentInstance>();
        foreach (var task in ready)
        {
            try
            {
                workers[task.TaskId] = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkerAssignmentException ex)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint,
                    FailedTaskResult(task, task.WorkerAgentId, WorkerAssignmentInvalidCategory, SafeError(ex)), cancellationToken).ConfigureAwait(false);
            }
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "workers-assigned", cancellationToken).ConfigureAwait(false);

        // Every ready task's tool declaration surface resolves through the same
        // catalog, and the text/tool split keys off the resolved surface — not the
        // planner's required_tools hint. An empty-required_tools task whose worker
        // holds an authorized catalog joins the durable tool loop, so a model call
        // against the catalog can never fail as "not advertised". A task whose
        // surface cannot be resolved fails on its own; the run continues.
        var surfaces = new Dictionary<Guid, IReadOnlyList<WorkerToolDescriptor>>();
        foreach (var task in ready)
        {
            if (!workers.TryGetValue(task.TaskId, out var worker)) continue;
            try
            {
                surfaces[task.TaskId] = await GetWorkerToolsAsync(run, configuration, task, worker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint,
                    FailedTaskResult(task, worker.Id, ToolManifestUnavailableCategory, SafeError(ex)), cancellationToken).ConfigureAwait(false);
            }
        }

        // Tool-capable workers are advanced by the single run owner below. Plain
        // model-only workers can still make their first model turn in parallel.
        var textOnly = ready.Where(task => surfaces.TryGetValue(task.TaskId, out var tools) && tools.Count == 0).ToList();
        var toolCapable = ready.Where(task => surfaces.TryGetValue(task.TaskId, out var tools) && tools.Count != 0).ToList();
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
            var result = await ExecuteToolTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken, surfaces[task.TaskId]).ConfigureAwait(false);
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
            // The dispatch split already resolved this task's declaration surface to
            // empty, so nothing is advertised here; a model call in this state means
            // the worker genuinely holds no authorized tool.
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
                ? new StepResult { TaskNodeId = task.TaskId, AgentId = worker.Id.ToString("N"), Status = "failed", Summary = "Execution returned no output.", Evidence = [DispatchEvidence(task)] }
                : BuildCompletedStepResult(task, worker.Id, model.Text);
            return new TaskExecutionResult(task.TaskId, worker.Id, result.Status == "completed" ? "completed" : "failed", result, model.Usage);
        }
        catch (Exception ex) when (ex is WorkerAssignmentException or InvalidDataException)
        {
            // Frozen worker binding/config drift: fail this task, not the run.
            return FailedTaskResult(task, task.WorkerAgentId, WorkerAssignmentInvalidCategory, SafeError(ex));
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
        CancellationToken cancellationToken,
        IReadOnlyList<WorkerToolDescriptor>? resolvedTools = null)
    {
        RuntimeAgentInstance worker;
        try
        {
            worker = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerAssignmentException ex)
        {
            return FailedToolTask(checkpoint, task, null, WorkerAssignmentInvalidCategory, SafeError(ex));
        }
        var dispatcher = _services.GetRequiredService<IToolDispatcher>();
        var executionCoordinator = _services.GetRequiredService<IToolExecutionCoordinator>();
        var roundLimit = configuration.Tools.ResolveTaskRoundLimit(task.Category, task.Risk);
        IReadOnlyList<WorkerToolDescriptor> descriptors;
        if (resolvedTools is not null)
        {
            descriptors = resolvedTools;
        }
        else
        {
            try
            {
                descriptors = await GetWorkerToolsAsync(run, configuration, task, worker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FailedToolTask(checkpoint, task, worker, ToolManifestUnavailableCategory, SafeError(ex));
            }
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
                        // Bad arguments are the single most correctable failure a model
                        // makes, and this exit used to end the task WITHOUT even writing
                        // the reason into the turn — the model could not see what it got
                        // wrong, so it could not fix it.
                        checkpoint = await FeedToolFailureBackAsync(run, task, pendingTurn,
                            RunErrorTaxonomy.InvalidToolArguments, "The worker returned invalid tool arguments.",
                            checkpoint, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var toolCallKey = $"run:{run.RunId}:task:{task.TaskKey}:attempt:{task.Attempt}:round:{task.ToolRounds}:call:{pendingTurn.CallId}";
                    dispatch = await dispatcher.PrepareAsync(new ToolDispatchRequestDto
                    {
                        RunId = run.RunId,
                        TaskId = task.TaskId.ToString(),
                        AgentInstanceId = worker.Id.ToString(),
                        ToolId = pendingTurn.ToolId,
                        ToolCallKey = toolCallKey,
                        // One call, one single-use ticket. The lease budget is
                        // decoupled from the task's round budget: a lease is
                        // consumed exactly once per execution (the dispatcher's
                        // consumption idempotency key is derived from the
                        // execution id, so resume/replay replays the receipt
                        // instead of spending a second use), which means the
                        // former "remaining rounds" allowance only ever handed
                        // out unconsumed uses. Keeping it at 1 restores the
                        // one-time nonce semantics and, crucially, stops the
                        // round limit from capping the lease ceiling (33+ rounds
                        // used to trip the coordinator's 1..32 validation).
                        LeaseUses = 1,
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
                        // A prepare that produced no execution has already written the
                        // structured reason into the turn; killing the task right after
                        // was self-contradictory — the reason existed precisely so the
                        // model could read it and adapt.
                        checkpoint = await FeedToolFailureBackAsync(run, task, pendingTurn,
                            dispatch.ErrorCategory ?? RunErrorTaxonomy.ToolPrepareFailed,
                            dispatch.Message ?? "The tool call could not be prepared.",
                            checkpoint, cancellationToken).ConfigureAwait(false);
                        continue;
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
                    // The parked execution is dead: detach it so later ticks never
                    // resume it (the escalation is the durable wake-up), and
                    // annotate the unresolved turn for audit.
                    ClearPendingToolExecution(task, RunErrorTaxonomy.ApprovalExpired);
                    // Graph tiers carry no authoring lane of their own: the run
                    // parks directly on the escalation review and the task is
                    // terminal, so a stray tick can never re-dispatch it into a
                    // fresh approval loop (the approval row is already expired).
                    if (configuration.Graph is not null && parkedLane is not null)
                    {
                        task.Status = "failed";
                        task.ResultStatus = "failed";
                        task.ResultSummary = "Approval expired while the run was unattended.";
                        task.CompletedAt = DateTimeOffset.UtcNow;
                        checkpoint.Phase = "awaiting_user";
                        checkpoint.AwaitingApprovalExpiryReview = true;
                    }
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-park-expired", cancellationToken).ConfigureAwait(false);
                    if (parkedLane is not null && configuration.Graph is null)
                    {
                        await EscalateLaneAsync(Guid.Parse(run.RunId), run, parkedLane,
                            $"Approval for tool '{pendingTurn.ToolId}' expired its decision window while the run was unattended.",
                            cancellationToken).ConfigureAwait(false);
                        // Return before the generic awaiting handling below can
                        // overwrite the escalated run status with awaiting_approval.
                        return new ToolTaskExecutionResult(checkpoint, Waiting: true, Result: null);
                    }
                    // Graph tiers: park the run directly on the escalation review so
                    // an unattended expiry never fails the run silently — the user
                    // decides whether to continue (replan the rest) or cancel.
                    var expiryReason = $"Approval for tool '{pendingTurn.ToolId}' expired its decision window while the run was unattended.";
                    await TrySetRunStatusAsync(run.RunId, "awaiting_user", expiryReason, cancellationToken).ConfigureAwait(false);
                    await AppendEventAsync(Guid.Parse(run.RunId), "worker.failed",
                        "The tool task failed after its approval expired unattended.",
                        new { run_id = run.RunId, task_id = task.TaskId, task_key = task.TaskKey, error_category = RunErrorTaxonomy.ApprovalExpired }, cancellationToken).ConfigureAwait(false);
                    await AppendEventAsync(Guid.Parse(run.RunId), "supervision.user_review.requested",
                        "The run is waiting for a user decision after an approval expiry.",
                        new { run_id = run.RunId, lane_key = "main", decision = "escalate", reasons = new[] { expiryReason }, options = new[] { "continue", "correct", "cancel" } }, cancellationToken).ConfigureAwait(false);
                    return new ToolTaskExecutionResult(checkpoint, Waiting: true, Result: null);
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
                        // A park while another lane already waits on the user is a
                        // legal coexistence; a rejected transition must not fail the run.
                        await TrySetRunStatusAsync(run.RunId, runStatus, dispatch.Message, cancellationToken).ConfigureAwait(false);
                    }
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting", cancellationToken).ConfigureAwait(false);
                    return new ToolTaskExecutionResult(checkpoint, Waiting: true, Result: null);
                }
                if (dispatch.Status != ToolDispatchStatus.Completed)
                {
                    var category = dispatch.ErrorCategory ?? dispatch.Status;
                    var summary = dispatch.Message ?? $"Tool '{pendingTurn.ToolId}' returned {dispatch.Status}.";
                    // A call that did not complete is a tool RESULT, not a terminal
                    // task outcome. The structured error (the provider wire contract's
                    // shape) lands on the turn exactly as a successful result would,
                    // and the pending linkage is dropped so the next pass cannot
                    // re-drive a dead execution. The worker model then reads why the
                    // call failed and decides: retry with corrected arguments, switch
                    // tool, or hand off.
                    //
                    // This used to fail the task on the first non-completed dispatch,
                    // which made the loop non-iterative by construction — the model
                    // was never told what went wrong, so one refusal, timeout, or bad
                    // argument ended the work instead of being corrected. The
                    // outcome_unknown case was worse: it parked the entire run, hiding
                    // a recoverable ambiguity behind a resume only a human could give.
                    //
                    // Convergence is carried by the loop guard (repeated calls,
                    // consecutive errors, consecutive empty responses) and the token
                    // budgets — not by killing the task at the first failure.
                    pendingTurn.DispatchStatus = dispatch.Status;
                    pendingTurn.ErrorCategory ??= category;
                    pendingTurn.ResultJson = ToolCallFailureJson(pendingTurn.ToolId, category, summary);
                    ClearPendingToolExecution(task);
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-failure", cancellationToken).ConfigureAwait(false);
                    await AppendEventAsync(Guid.Parse(run.RunId), "worker.tool_failed",
                        $"Tool '{pendingTurn.ToolId}' failed ({category}); the failure was returned to the worker as a tool result.",
                        new
                        {
                            run_id = run.RunId,
                            task_id = task.TaskId,
                            task_key = task.TaskKey,
                            lane_key = LaneKeyOf(task),
                            tool_id = pendingTurn.ToolId,
                            call_id = pendingTurn.CallId,
                            dispatch_status = dispatch.Status,
                            error_category = category,
                            message = summary,
                            consecutive_errors = TrailingConsecutiveErrors(task),
                            returned_to_worker = true
                        }, cancellationToken, task.TaskId, idempotencyKey: $"tool-failure:{task.TaskId}:{pendingTurn.CallId}").ConfigureAwait(false);
                    continue;
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

            WorkerModelTurn model;
            try
            {
                // The close-out turn is text-only by construction: the declarations
                // are withheld so the model cannot start another side effect, and the
                // prompt asks it to hand off instead. A budget or loop trigger already
                // decided that no further tool use is affordable.
                IReadOnlyList<WorkerToolDescriptor> declaredTools = descriptors;
                string? closeoutPrompt = null;
                if (task.CloseoutRequested)
                {
                    declaredTools = [];
                    closeoutPrompt = CloseoutPrompts.Build(new CloseoutPrompts.CloseoutContext(
                        task.CloseoutCategory ?? RunErrorTaxonomy.Runtime,
                        task.CloseoutReason ?? "The worker budget was exhausted before the task could finish.",
                        task.CloseoutHardCeiling,
                        task.ToolRounds,
                        roundLimit,
                        configuration.Tools.ResolveTaskRoundLimitSource(task.Category, task.Risk),
                        task.ToolTurns.Count,
                        configuration.Tools.MaxToolCalls,
                        task.TokensUsed,
                        configuration.Context.DefaultTokenBudget,
                        Maf18RuntimeAdapter.BudgetTokens(checkpoint.ModelUsage),
                        configuration.Context.RunTokenBudget,
                        task.ToolTurns
                            .TakeLast(3)
                            .Select(turn => turn.ToolId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToArray()));
                }
                model = await GetWorkerModelTurnAsync(run, configuration, checkpoint, task, worker, declaredTools, cancellationToken, closeoutPrompt).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                // The frozen worker binding vanished or drifted mid-run: fail this
                // task instead of the whole run.
                return FailedToolTask(checkpoint, task, worker, WorkerAssignmentInvalidCategory, SafeError(ex));
            }
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, model.Usage);
            AccumulateTaskTokens(task, model.Usage);
            if (!model.IsAvailable || model.Error is not null)
            {
                return FailedToolTask(checkpoint, task, worker, RunErrorTaxonomy.ModelUnavailable, model.Error ?? "Worker model is unavailable.");
            }
            if (task.CloseoutRequested)
            {
                // Tools were withheld for exactly this turn, so the loop ends here
                // whatever came back: a stray call is dropped rather than dispatched,
                // and the hand-off text (or, when the model says nothing, the
                // recorded stop reason) becomes the task's evidence.
                return CompleteCloseout(checkpoint, task, worker, model);
            }

            task.ToolRounds++;
            if (model.Calls.Count == 0 && string.IsNullOrWhiteSpace(model.Text))
            {
                // An empty response used to fail the task outright. Count the streak
                // instead (codex: consecutive_empty_turns >= 3) so the model gets a
                // chance to answer before the task is closed out.
                task.ConsecutiveEmptyResponses++;
            }
            else
            {
                task.ConsecutiveEmptyResponses = 0;
            }

            // One structured line per round, emitted before the gates so the event
            // stream shows how the task walked to its end — including the round the
            // stop fires on — instead of only the last sentence it produced.
            await EmitToolRoundEventAsync(run, checkpoint, task, configuration, roundLimit, model, cancellationToken).ConfigureAwait(false);

            // Round gate: 0 (or less) means unlimited, so this only fires for a
            // configuration that deliberately narrows a task class. Convergence is
            // carried by the loop guard and the token budgets below, not by counting
            // rounds — the round limit survives as an optional ceiling.
            if (roundLimit > 0 && task.ToolRounds > roundLimit)
            {
                RequestCloseout(task, RunErrorTaxonomy.ToolRoundLimit,
                    $"The worker exhausted its tool-round budget ({DescribeToolBudget(configuration, task, roundLimit)}).");
            }

            if (!task.CloseoutRequested && _loopGuard is { } guard)
            {
                // The guard runs on EVERY round now. It used to engage only once
                // ToolRounds passed the frozen global default, so a worker looping
                // inside its first four rounds was invisible to repeat/error
                // detection, and its stop was misreported as a spent round budget —
                // the two causes point at opposite fixes.
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
                        TokensUsed = task.TokensUsed,
                        TokenBudget = configuration.Context.DefaultTokenBudget,
                        ToolCallCount = task.ToolTurns.Count,
                        MaxToolCalls = configuration.Tools.MaxToolCalls,
                        // Repeat detection only inspects the trailing run, so the tail
                        // is all it needs; rebuilding the whole transcript every round
                        // made a long task quadratic.
                        RecentToolCallFingerprints = TrailingFingerprints(task, FingerprintTail),
                        ConsecutiveErrors = TrailingConsecutiveErrors(task)
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!decision.ShouldContinue)
                {
                    RequestCloseout(task, decision.Category ?? RunErrorTaxonomy.ToolLoopDetected,
                        decision.Reason ?? "The loop guard stopped further tool rounds for this task.",
                        decision.HardCeiling);
                }
            }

            if (!task.CloseoutRequested)
            {
                // Run-level counterpart of the task budget: the whole run, across
                // every task and every role, gets one fuse of its own.
                var runTokens = Maf18RuntimeAdapter.BudgetTokens(checkpoint.ModelUsage);
                if (configuration.Context.RunTokenBudget > 0 && runTokens >= configuration.Context.RunTokenBudget)
                {
                    RequestCloseout(task, RunErrorTaxonomy.RunTokenBudgetExhausted,
                        $"The run exhausted its token budget ({runTokens}/{configuration.Context.RunTokenBudget}).");
                }
            }

            if (!task.CloseoutRequested && task.ConsecutiveEmptyResponses >= MaxConsecutiveEmptyResponses)
            {
                RequestCloseout(task, RunErrorTaxonomy.EmptyResponseLimit,
                    $"The worker returned {task.ConsecutiveEmptyResponses} consecutive empty responses.");
            }

            if (task.CloseoutRequested)
            {
                // This round's calls are intentionally dropped: the next turn asks for
                // a hand-off rather than more side effects, and the loop can never
                // re-arm from the close-out state.
                await EmitCloseoutRequestedAsync(run, checkpoint, task, configuration, roundLimit, model, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-closeout", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (model.Calls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(model.Text))
                {
                    return new ToolTaskExecutionResult(checkpoint, Waiting: false,
                        new TaskExecutionResult(task.TaskId, worker.Id, "completed",
                            BuildCompletedStepResult(task, worker.Id, model.Text)));
                }
                // Blank answer below the streak threshold: give the model another
                // turn instead of failing the task on one empty response.
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-empty-response", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var existingCallIds = task.ToolTurns.Select(item => item.CallId).ToHashSet(StringComparer.Ordinal);
            foreach (var call in model.Calls)
            {
                if (!existingCallIds.Add(call.CallId))
                {
                    return FailedToolTask(checkpoint, task, worker, RunErrorTaxonomy.DuplicateToolCall, $"The worker reused tool call id '{call.CallId}'.", call.ToolId);
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
        // The declaration surface is the intersection of this instance's grant and the
        // run-frozen manifest.  The live provider manifest is deliberately not consulted:
        // a tool that appeared in a child process after admission is not authority for
        // what a worker may be told it can call.
        var catalog = _services.GetRequiredService<IFrozenToolManifestCatalog>();
        var authorized = await catalog.ListAuthorizedAsync(
            Guid.Parse(run.RunId), task.TaskId, worker.Id, cancellationToken).ConfigureAwait(false);

        if (task.RequiredTools.Count == 0)
        {
            // Reasoning/loose planners (and the parse-failure fallback task) frequently
            // emit no required_tools. Handing the worker zero declarations makes the model
            // improvise shell commands as plain text, which never executes and gets
            // escalated by supervision. Instead offer the worker's full authorized catalog
            // (grant ∩ frozen manifest) so it can explore (read_file/list_dir/...). Dispatch
            // stays policy-, approval-, and loop-guarded: this widens only the declaration
            // surface, never the authority to act.
            return authorized
                .Select(entry => new WorkerToolDescriptor(entry.Id, entry.Description, entry.InputSchema))
                .ToArray();
        }

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
        var graph = configuration.Graph;
        var instances = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);

        // Resolve the dispatch target: a persisted spawnable-template assignment
        // wins, then the frozen roster, then (graph tiers) the spawnable whitelist
        // for tasks the roster cannot cover — that fallback IS the spawn demand.
        var spawnable = ResolvePersistedSpawnable(configuration, task);
        // solo_dispatch: a task assigned to the CONVERSATION IDENTITY is executed by the
        // master, which lives in the operation roster — ResolveOrSelectWorker below only
        // searches execution agents and would report the master as unavailable. The master
        // gets its OWN instance for the task (the conversation instance authored the run
        // and carries no task id, and VerifyWorkerInstance rightly requires the executing
        // instance to be bound to the task), which is also what keeps its tool turns, its
        // approvals and its tool-timeline row attached to a real lineage entry.
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
        else
        {
            try
            {
                selected = ResolveOrSelectWorker(configuration, task);
            }
            catch (WorkerUnavailableException)
            {
                await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.CapabilityMissing, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
                // A task no roster worker covers is a spawn demand. The frozen
                // spawnable whitelist decides what it means: nothing covers it →
                // the loud worker_unavailable failure (run-level, unchanged); a
                // template covers it but the tier forbids spawn → the explicit
                // graph_tier_spawn_denied rejection; otherwise spawn it.
                var coverage = GraphSpawnAuthority.FindCoverage(graph, task.RequiredTools, task.RequiredCapabilities);
                if (coverage is null) throw;
                // Single source of truth: an inline list of spawn-authority tiers here
                // had to be found and edited separately from GraphSpawnAuthority every
                // time a tier was added, which is exactly how a new tier silently loses
                // (or gains) spawn rights.
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
                // Frozen manifest/roster drift against the persisted assignment is a
                // configuration failure scoped to this task, not an engine invariant.
                throw new WorkerAssignmentException(ex.Message, ex);
            }
        }

        // Dispatch authority and budget run BEFORE the assignment is persisted: a
        // doomed selection must never reach the checkpoint. The instance
        // population also feeds the graph-tier budget guard, so it is loaded
        // before the assignment write instead of after it.
        //
        // The solo master is exempt: edge authority answers "which worker may this node
        // dispatch TO", and the master is the conversation node itself, not a target of
        // its own edges. Asking that question about the master would deny it its own work.
        if (spawnable is null && soloMaster is null && graph is not null
            && !GraphEdgeAuthority.IsDispatchAllowed(graph, selected.Agent.Id))
            throw new WorkerAssignmentException(
                $"Task '{task.TaskKey}' worker '{selected.Agent.Id}' is not a declared dispatch target of the mode graph (tier {graph.Tier}).");
        EnsureGraphWorkerBudget(instances, configuration.Spawn.MaxAgentsPerRun, task.TaskKey);
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
            if (selected.Agent.AgentDefinitionId is not { } definitionId
                || selected.Agent.AgentVersionId is not { } versionId
                || string.IsNullOrWhiteSpace(selected.Agent.VersionContentHash))
                throw new WorkerAssignmentException($"Frozen specialist '{selected.Agent.Id}' has no immutable version binding.");

            // Engine-authoritative root path for declared-graph tiers: the
            // worker's authority comes from the frozen roster definition or the
            // frozen spawnable template (the seed carries it), NOT from a parent
            // grant — meeting as parent would fail SpawnAsync's layer and subset
            // gates by construction. Budget already guarded above; lineage audit
            // carries the author.
            IReadOnlyList<string> seedTools;
            if (spawnable is not null)
            {
                // The author narrows within the frozen template ceiling: every
                // required tool must sit inside it (spawn_tool_ceiling_exceeded);
                // a task with no required tools gets the whole ceiling.
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
            worker = await _instances.CreateRootAsync(new RuntimeAgentSeed(
                Guid.Parse(run.SessionId),
                runId,
                selected.Agent.Id,
                selected.Agent.Layer,
                selected.Agent.Role,
                "chat",
                selected.Agent.Capabilities,
                seedTools,
                ResourceSeed(spawnable?.ResourceGrants ?? selected.Agent.ResourceGrants),
                configuration.Context.DefaultTokenBudget,
                TaskId: task.TaskId,
                AgentDefinitionId: definitionId,
                AgentVersionId: versionId,
                VersionContentHash: selected.Agent.VersionContentHash), cancellationToken).ConfigureAwait(false);
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

        if (task.WorkerAgentId != worker.Id)
        {
            task.WorkerAgentId = worker.Id;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-assigned", cancellationToken).ConfigureAwait(false);
        }
        return worker;
    }

    /// <summary>
    /// Graph-tier instance budget. The root-instance creation path bypasses
    /// SpawnAsync's per-run generated budget, so the ceiling is enforced here
    /// against the run's FULL instance population (author + workers) — the closed
    /// boundary against planner sprawl. Concurrency remains bounded by the ready
    /// dispatch window; per-task tool spend stays behind approval and loop-guard.
    /// </summary>
    private static void EnsureGraphWorkerBudget(IReadOnlyList<RuntimeAgentInstance> instances, int maxAgentsPerRun, string taskKey)
    {
        if (instances.Count >= maxAgentsPerRun)
            throw new WorkerAssignmentException(
                $"Graph-tier worker budget exhausted for task '{taskKey}': the run already carries {instances.Count} instances (ceiling {maxAgentsPerRun}).");
    }

    /// <summary>
    /// Serializes frozen resource grants into the instance grant strings the Tools
    /// layer consumes ("read:prefix" / "write:prefix"; write implies read at the
    /// PDP resource_access boundary). Empty grants seed an empty list — the
    /// instance then holds no workspace authorization and provider tool calls are
    /// denied (fail-closed WS-4 semantics; the historical "workspace" coarse
    /// token is retired).
    /// </summary>
    private static IReadOnlyList<string> ResourceSeed(IReadOnlyList<FrozenResourceGrant> grants) =>
        grants.Count == 0
            ? []
            : grants.Select(grant => $"{grant.Level}:{grant.PathPrefix}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// A persisted assignment naming a spawnable-template slug routes to that
    /// template instead of the roster; version binding drift still fails closed.
    /// </summary>
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

    /// <summary>
    /// Materializes a spawnable template as the worker definition the downstream
    /// flows (tool surface, model turn, instance verification) already consume.
    /// Its authority is the frozen template itself: ceiling as AllowedTools,
    /// on_demand lifecycle, execution layer. The template's model strategy is
    /// "inherit" by construction, so the model plan is the session default — the
    /// conversation identity's frozen plan.
    /// </summary>
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
            ModelPlan = conversation.ModelPlan,
            Enabled = true
        };
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
        var workerDefinition = GetAssignedWorkerDefinition(configuration, task);
        var context = await BuildContextAsync(run, configuration, workerDefinition.Id,
            $"Task: {task.Title}\nDescription: {task.Description}\nSuccess criteria: {string.Join("; ", task.SuccessCriteria)}",
            cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(configuration, workerDefinition, context, cancellationToken).ConfigureAwait(false);
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
            // The close-out instruction leads: on that turn the model must hand off,
            // not continue, so it must not be buried under the normal task framing.
            closeoutPrompt is null
                ? assembly.Instructions + WorkerPatchProtocol
                : closeoutPrompt + "\n\n" + assembly.Instructions + WorkerPatchProtocol,
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

        // 冻结 roster 里是否存在启用的通用兜底 worker（worker.general）。问答/规划/规范 等
        // 精简模式的执行层是 worker 子集、不含 worker.general；无要求任务不能因此直接
        // fail-closed 让整段对话不可用——此时回退到能覆盖空需求的在册 worker（最小权限优先）。
        var hasGeneralWorker = configuration.ExecutionAgents.Any(agent =>
            agent.Enabled && string.Equals(agent.Id, "worker.general", StringComparison.Ordinal));
        var unclassifiedTask = requiredTools.Count == 0 && requiredCapabilities.Count == 0;
        // Every enabled execution-roster member is a worker candidate; the
        // execution layer holds no planner (excluded explicitly) and no other
        // non-worker role, so the old worker.*/role-prefix filter is gone.
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
                // 无要求任务优先只交给通用兜底 worker；roster 无 worker.general 时放开到任意在册 worker。
                if (unclassifiedTask) eligible = eligible && (isGeneral || !hasGeneralWorker);
                return new WorkerCandidate(agent, tools, capabilities, isGeneral, eligible);
            })
            .Where(candidate => candidate.Eligible)
            .OrderBy(candidate => candidate.IsGeneral)
            // An open-ended task declares nothing to narrow against, so "fewest extra
            // tools" used to hand it to the NARROWEST worker — which is how a request
            // to write a file landed on a read-only executor that then reported it had
            // no way to do the job. With no requirement to satisfy, breadth is the only
            // meaningful signal left: the most capable worker is the one that can
            // actually attempt an open goal. A task that DOES declare requirements
            // keeps the least-privilege ordering (fewest extra tools, then fewest extra
            // capabilities).
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

    // 缺口①修复 A：规划名册图原生。规划模型可见的择人面 = 本 run 的合法派发目标——
    // 声明边目标 ∪（tier 携带 spawn 权时）冻结 spawnable 白名单；role 字符串从此只是
    // 展示/审计元数据，不再做硬白名单（worker.*/task_executor 过滤退役：GraphSeedPack
    // 能进名册纯属 role 恰好叫 task_executor，下一个自定角色词汇的包会隐形）。
    // Graph 为 null 仅存在于手工构造的测试配置，回落为全部启用执行智能体。
    internal static IReadOnlyList<AgentDefinition> BuildFrozenPlannerRoster(FrozenRunConfigurationV1 configuration)
    {
        var graph = configuration.Graph;
        if (graph is null)
            return configuration.ExecutionAgents
                .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
                .OrderBy(agent => agent.RosterOrder)
                .ThenBy(agent => agent.Id, StringComparer.Ordinal)
                .Select(agent => ToPlannerRosterEntry(agent.AgentDefinitionId ?? Guid.Empty, agent.Id, agent.Role, agent.Capabilities, agent.AllowedTools))
                .ToArray();

        var slugByNodeKey = graph.Nodes.ToDictionary(node => node.NodeKey, node => node.AgentSlug, StringComparer.Ordinal);
        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddSlug(string? slug)
        {
            if (!string.IsNullOrWhiteSpace(slug) && seen.Add(slug)) slugs.Add(slug);
        }
        foreach (var edge in graph.Edges)
            AddSlug(slugByNodeKey.TryGetValue(edge.TargetNodeKey, out var target) ? target : null);
        if (GraphSpawnAuthority.CarriesSpawnAuthority(graph.Tier))
        {
            foreach (var template in graph.SpawnableTemplates)
                AddSlug(template.Slug);
            // free_form 对任意启用 worker 放行（IsDispatchAllowed 无条件 true）：包未声明
            // agent_types 白名单的 legacy 形态（office 对话模式、测试夹具）的执行层必须仍在
            // 名册上，否则规划模型对所有 worker 失明。
            if (string.Equals(graph.Tier, FrozenGraphTiers.FreeForm, StringComparison.Ordinal))
                foreach (var agent in configuration.ExecutionAgents
                    .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
                    .OrderBy(agent => agent.RosterOrder)
                    .ThenBy(agent => agent.Id, StringComparer.Ordinal))
                    AddSlug(agent.Id);
        }

        var enabledBySlug = configuration.ExecutionAgents
            .Where(agent => agent.Enabled)
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.First().Id, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var roster = new List<AgentDefinition>();
        foreach (var slug in slugs)
        {
            if (enabledBySlug.TryGetValue(slug, out var agent))
            {
                roster.Add(ToPlannerRosterEntry(agent.AgentDefinitionId ?? Guid.Empty, agent.Id, agent.Role, agent.Capabilities, agent.AllowedTools));
                continue;
            }
            var template = graph.SpawnableTemplates.FirstOrDefault(candidate =>
                string.Equals(candidate.Slug, slug, StringComparison.OrdinalIgnoreCase));
            if (template is not null)
                roster.Add(ToPlannerRosterEntry(template.AgentDefinitionId, template.Slug, template.Role, template.Capabilities, template.ToolCeiling));
        }
        return roster;
    }

    private static AgentDefinition ToPlannerRosterEntry(
        Guid definitionId,
        string slug,
        string role,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> allowedTools) => new()
    {
        Id = definitionId,
        Name = slug,
        Layer = "execution",
        AgentType = role,
        Capabilities = capabilities,
        AllowedTools = allowedTools,
        Enabled = true
    };

    // 缺口①修复 C：声明边模式下，解析失败产生的回落单任务（整句目标、零需求）不得静默派发——
    // 就近覆盖平局规则会把它派给最窄工具面的模板（run d035fa40：写文件目标被派给只读 search，
    // worker 如实报告无 dispatch 工具，run 假完成）。抛 InvalidTaskGraphException 走既有的
    // 两次规划重试，二次失败即 run 失败，失败原因带 plan_parse_failed 可见。free_form（无边）
    // 保留回落：总监体接管目标本就是合法语义，无错误靶点风险。
    internal static void RefuseSilentPlanningFallbackOnDeclaredEdges(FrozenRunConfigurationV1 configuration, PlannedTask[] planned)
    {
        if (configuration.Graph is { Edges.Count: > 0 } && planned.Any(task => task.IsFallback))
            throw new InvalidTaskGraphException(
                "The planner output could not be parsed as a task array (plan_parse_failed); "
                + "refusing to dispatch the silent fallback goal-task along declared edges.");
    }

    // 2026-09-17 plan_parse_failed 集群（feb146e4/05a89fc3，qwen3.8-27b 把 success_criteria
    // 写成标量字符串）：两个 attempt 逐字节盲发同一提示词，模型必然重犯同一错误。重试必须携带
    // 上一次失败的具体原因（解析详情或图校验错误），并把形状约束重复一次。
    internal static string BuildPlannerRetryHint(Exception? lastError, string parseErrorDetail)
    {
        var detail = !string.IsNullOrWhiteSpace(parseErrorDetail) ? $"（解析错误: {parseErrorDetail}）" : string.Empty;
        var reason = lastError?.Message ?? "unknown";
        return "\n\n【重试纠正】你上一次的输出未能构成合法任务图（" + reason + "）" + detail + "。再次输出时："
            + "只输出一个裸 JSON 任务数组，不要用散文拒绝或解释；"
            + "success_criteria、dependencies、required_capabilities、required_tools 四个字段必须写成字符串数组（例如 \"success_criteria\": [\"判据\"]），绝不能写成单个字符串；"
            + "dependencies 只能引用本次已声明的 task_key，不得成环；"
            + "若目标无需执行任何子任务，直接输出 []。";
    }

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
        // solo_dispatch assigns the conversation identity a task it executes itself, and
        // the identity lives in the OPERATION roster. Without this lookup the frozen
        // assignment could not be re-resolved (recovery, resume, verification) and the
        // master's own task would fail as roster drift.
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

    /// <summary>
    /// Materializes sub-tasks queued by the conversation identity through
    /// <c>task_dispatch</c>. A delivered task node has no dependencies, so the ordinary
    /// dispatch loop picks it up on its own — worker selection, spawnable templates,
    /// approval and convergence are exactly the paths a planned task takes.
    ///
    /// Each row is drained exactly once (the pending-status filter), and a row with no
    /// title is rejected rather than turned into an untitled task nobody can act on.
    /// </summary>
    private async Task<bool> ApplyPendingTaskDispatchesAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var pending = await _lifecycle.ListPendingRunDirectivesAsync(checkpoint.RunId, cancellationToken).ConfigureAwait(false);
        var dispatches = pending
            .Where(item => string.Equals(item.Kind, "task_dispatch", StringComparison.Ordinal))
            .ToList();
        if (dispatches.Count == 0) return false;

        var runId = Guid.Parse(run.RunId);
        var added = 0;
        foreach (var directive in dispatches)
        {
            string? title = null;
            string? description = null;
            string[] criteria = [];
            string[] tools = [];
            string[] capabilities = [];
            try
            {
                using var document = JsonDocument.Parse(directive.PayloadJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
                        title = titleElement.GetString()?.Trim();
                    if (root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
                        description = descriptionElement.GetString()?.Trim();
                    criteria = ReadStringArray(root, "success_criteria");
                    tools = ReadStringArray(root, "required_tools");
                    capabilities = ReadStringArray(root, "required_capabilities");
                }
            }
            catch (JsonException)
            {
                // Falls through to the rejection below, same as a missing title.
            }

            var status = "consumed";
            if (string.IsNullOrWhiteSpace(title))
            {
                status = "rejected";
                await AppendEventAsync(runId, "task.dispatch_rejected",
                    "A queued sub-task carried no title and was rejected.",
                    new { directive_id = directive.Id, code = "dispatch_payload_unreadable" }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var key = NormalizeTaskKey(null, title);
                // Duplicate keys are tolerated rather than fatal: NormalizeTaskKey only
                // guarantees uniqueness within a plan, and a dispatch can repeat a title.
                // De-duplicating here would silently swallow a legitimate second request,
                // so the key is disambiguated instead.
                if (checkpoint.Tasks.Any(item => string.Equals(item.TaskKey, key, StringComparison.OrdinalIgnoreCase)))
                {
                    key = $"{key}-{checkpoint.Tasks.Count(node => node.TaskKey.StartsWith(key, StringComparison.OrdinalIgnoreCase)) + 1}";
                }
                checkpoint.DirectiveCursor++;
                checkpoint.Tasks.Add(new DurableTaskNode
                {
                    TaskId = Guid.NewGuid(),
                    TaskKey = key,
                    Title = title,
                    Description = description,
                    SuccessCriteria = criteria.Length > 0 ? [.. criteria] : ["The sub-task's stated goal is met."],
                    Dependencies = [],
                    RequiredCapabilities = [.. capabilities],
                    RequiredTools = [.. tools],
                    Priority = 2,
                    Risk = "medium",
                    Status = "pending"
                });
                added++;
                await AppendEventAsync(runId, "task.dispatched",
                    $"Sub-task '{title}' was accepted into the run's task graph.",
                    new
                    {
                        directive_id = directive.Id,
                        task_key = key,
                        title,
                        required_tools = tools,
                        required_capabilities = capabilities
                    }, cancellationToken).ConfigureAwait(false);
            }
            await _lifecycle.DrainRunDirectivesAsync(checkpoint.RunId, [directive.Id], status, cancellationToken).ConfigureAwait(false);
        }
        return added > 0;
    }

    /// <summary>
    /// Resolves a task whose assigned worker IS the conversation identity, in the
    /// solo_dispatch tier. Returns null for every other tier and every other worker, so
    /// the ordinary execution-roster resolution stays the single path for them.
    /// </summary>
    private static WorkerSelection? ResolveSoloMaster(
        FrozenRunConfigurationV1 configuration,
        FrozenGraph? graph,
        DurableTaskNode task)
    {
        if (graph is not { Tier: FrozenGraphTiers.SoloDispatch }) return null;
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug)) return null;
        if (!string.Equals(task.WorkerAgentSlug, graph.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase)) return null;
        var master = configuration.OperationAgents.SingleOrDefault(agent =>
            string.Equals(agent.Id, task.WorkerAgentSlug, StringComparison.Ordinal)
            && agent.AgentDefinitionId == task.WorkerAgentDefinitionId
            && agent.AgentVersionId == task.WorkerAgentVersionId
            && string.Equals(agent.VersionContentHash, task.WorkerAgentVersionHash, StringComparison.OrdinalIgnoreCase));
        return master is null ? null : new WorkerSelection(master, task.WorkerAssignmentReason ?? "solo_master");
    }

    /// <summary>
    /// The single task a solo master executes itself: the user's goal, assigned to the
    /// conversation identity and pre-bound to its frozen agent version so the ordinary
    /// dispatch path needs no planner.
    ///
    /// <c>RequiredTools</c> is deliberately left EMPTY. A non-empty requirement is checked
    /// tool-by-tool against the instance's authorized catalog (grant ∩ frozen manifest)
    /// and throws on the first mismatch, which would turn any manifest/grant gap into a
    /// dead task. Empty instead takes the "offer the whole authorized catalog" path — and
    /// that catalog IS the master's authority, so the declaration surface matches the
    /// authority exactly rather than a guess made one layer up.
    /// </summary>
    private static List<DurableTaskNode> BuildSoloMasterTask(
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint)
    {
        var master = RequiredConversationAgent(configuration);
        var task = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "solo",
            Title = checkpoint.UserGoal,
            Description = null,
            SuccessCriteria = ["The goal is satisfied and the answer reports what what actually happened."],
            Dependencies = [],
            RequiredCapabilities = master.Capabilities.ToList(),
            RequiredTools = [],
            Priority = 1,
            Risk = "medium",
            Status = "pending"
        };
        task.WorkerAgentSlug = master.Id;
        task.WorkerAgentDefinitionId = master.AgentDefinitionId;
        task.WorkerAgentVersionId = master.AgentVersionId;
        task.WorkerAgentVersionHash = master.VersionContentHash;
        task.WorkerAssignmentReason = "solo_master";
        return [task];
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

    /// <summary>
    /// Assembles one agent's instructions from the frozen inputs. The frozen
    /// workspace travels with the request: every agent of the run must know the
    /// absolute root its path arguments are validated against.
    /// </summary>
    private async Task<PromptAssemblyResult> AssemblePromptAsync(
        FrozenRunConfigurationV1 configuration,
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
            definition.PromptGraphJson)
        {
            Workspace = configuration.Workspace
        }, cancellationToken).ConfigureAwait(false);

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
        if (execution.Status is "completed" or "failed" or "blocked")
        {
            // A terminal task never carries a pending execution into later ticks:
            // without this, a failed task would be resumed (and re-evented) on every
            // wake for the rest of the run.
            task.PendingToolExecutionId = null;
            task.PendingToolApprovalId = null;
        }

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
        RuntimeAgentInstance? worker,
        string category,
        string message,
        string? toolId = null)
    {
        // A failed task is terminal: drop the pending execution linkage so later
        // ticks never resume a dead execution or re-emit the failure. The
        // unresolved turn keeps the category as audit evidence.
        ClearPendingToolExecution(task, category);
        return new(checkpoint, Waiting: false,
            FailedTaskResult(task, worker?.Id ?? task.WorkerAgentId, category, message, toolId));
    }

    private static TaskExecutionResult FailedTaskResult(DurableTaskNode task, Guid? workerId, string category, string message, string? toolId = null) =>
        new(task.TaskId, workerId, "failed", new StepResult
        {
            TaskNodeId = task.TaskId,
            AgentId = workerId?.ToString("N") ?? string.Empty,
            Status = "failed",
            Summary = message,
            // The tool id travels next to the category: "which call failed" is the
            // first thing a reader (or the model reviewing evidence) needs, and the
            // per-task evidence used to carry the category alone.
            Evidence = toolId is null
                ? [$"error_category:{category}", DispatchEvidence(task)]
                : [$"error_category:{category}", $"tool:{toolId}", DispatchEvidence(task)]
        });

    /// <summary>
    /// Audit evidence for WHY this task landed on this worker. Without it, "the task
    /// was assigned to X" is the only surviving fact, and the decision rule that chose
    /// X (declared requirements vs. open-ended breadth, general vs. specialist) is
    /// invisible in replay — which is exactly how a wrong dispatch stayed undiagnosed.
    /// </summary>
    private static string DispatchEvidence(DurableTaskNode task)
    {
        var slug = string.IsNullOrWhiteSpace(task.WorkerAgentSlug) ? "unassigned" : task.WorkerAgentSlug;
        var reason = string.IsNullOrWhiteSpace(task.WorkerAssignmentReason) ? "unknown" : task.WorkerAssignmentReason;
        return $"dispatch:{slug}:{reason}";
    }

    /// <summary>
    /// The structured tool error stored on a turn whose call did not complete.
    /// It mirrors the provider wire contract (<c>error_category</c> + message) so
    /// Core, the tools process, and the model all describe a failure the same way.
    /// </summary>
    private static string ToolCallFailureJson(string toolId, string category, string message) =>
        JsonSerializer.Serialize(new { tool_id = toolId, error_category = category, message });

    private static void ClearPendingToolExecution(DurableTaskNode task, string? category = null)
    {
        task.PendingToolExecutionId = null;
        task.PendingToolApprovalId = null;
        if (category is null) return;
        foreach (var turn in task.ToolTurns)
        {
            if (string.IsNullOrWhiteSpace(turn.ResultJson)) turn.ErrorCategory ??= category;
        }
    }

    /// <summary>
    /// Enters the close-out state exactly once per task: the first trigger wins, so
    /// the reported reason is the one that actually stopped the work instead of
    /// whichever check happened to run last.
    /// </summary>
    private static void RequestCloseout(DurableTaskNode task, string category, string reason, bool hardCeiling = false)
    {
        if (task.CloseoutRequested) return;
        task.CloseoutRequested = true;
        task.CloseoutCategory = category;
        task.CloseoutReason = reason;
        task.CloseoutHardCeiling = hardCeiling;
    }

    /// <summary>
    /// Ends a task that a budget, fuse, or loop guard stopped. The task is recorded
    /// as completed on purpose: the hand-off text is the explicit channel for
    /// "stopped early, here is what is left", and failing the task would bury the
    /// partial work behind a generic failure. The stop reason travels in the
    /// evidence either way so supervision and the final answer can see it.
    /// </summary>
    private static ToolTaskExecutionResult CompleteCloseout(
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        WorkerModelTurn model)
    {
        var category = task.CloseoutCategory ?? RunErrorTaxonomy.Runtime;
        var reason = task.CloseoutReason ?? "The worker budget was exhausted before the task could finish.";
        var text = model.Text?.Trim();
        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(text)) evidence.Add(text);
        evidence.Add(DispatchEvidence(task));
        evidence.Add($"closeout:{category}");
        evidence.Add($"closeout_reason:{reason}");
        if (task.CloseoutHardCeiling) evidence.Add("hard_ceiling:true");
        var result = new StepResult
        {
            TaskNodeId = task.TaskId,
            AgentId = worker.Id.ToString("N"),
            Status = "completed",
            // Without hand-off text the system still knows why it stopped, so the
            // reason becomes the summary rather than an empty "no output".
            Summary = string.IsNullOrWhiteSpace(text)
                ? $"Stopped before completion ({category}): {reason}"
                : text,
            Evidence = evidence
        };
        return new ToolTaskExecutionResult(checkpoint, Waiting: false,
            new TaskExecutionResult(task.TaskId, worker.Id, "completed", result, model.Usage));
    }

    /// <summary>Adds one model call's tokens to the task's own budget counter.</summary>
    private static void AccumulateTaskTokens(DurableTaskNode task, ModelUsage? usage)
    {
        var tokens = Maf18RuntimeAdapter.BudgetTokens(usage);
        if (tokens <= 0) return;
        task.TokensUsed = (int)Math.Min(int.MaxValue, task.TokensUsed + tokens);
    }

    /// <summary>
    /// Trailing consecutive failed calls. A turn counts as failed while it carries
    /// an error category, which is exactly how the dispatcher records a refusal,
    /// an approval expiry, or a provider error; any success resets the streak.
    /// </summary>
    private static int TrailingConsecutiveErrors(DurableTaskNode task)
    {
        var count = 0;
        for (var index = task.ToolTurns.Count - 1; index >= 0; index--)
        {
            if (string.IsNullOrWhiteSpace(task.ToolTurns[index].ErrorCategory)) break;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Newest-last fingerprints of the trailing completed calls, bounded to the few
    /// entries repeat detection actually inspects.
    /// </summary>
    private static IReadOnlyList<string> TrailingFingerprints(DurableTaskNode task, int max)
    {
        var tail = new List<string>(max);
        for (var index = task.ToolTurns.Count - 1; index >= 0 && tail.Count < max; index--)
        {
            var turn = task.ToolTurns[index];
            if (string.IsNullOrWhiteSpace(turn.ResultJson)) continue;
            tail.Add($"{turn.ToolId}:{turn.ArgumentsJson}");
        }
        tail.Reverse();
        return tail;
    }

    /// <summary>
    /// The operator-facing budget context for a round-limit stop: which limit is in
    /// force, where it came from, how much was used, and on which tools. Prose alone
    /// ("exceeded max_tool_rounds") sent readers looking at the wrong dial.
    /// </summary>
    private static string DescribeToolBudget(FrozenRunConfigurationV1 configuration, DurableTaskNode task, int roundLimit)
    {
        var source = configuration.Tools.ResolveTaskRoundLimitSource(task.Category, task.Risk);
        var limit = roundLimit > 0
            ? $"{roundLimit} (source: {source})"
            : $"unlimited (source: {source}; max_tool_calls fuse: {configuration.Tools.MaxToolCalls})";
        var lastTools = task.ToolTurns
            .TakeLast(3)
            .Select(turn => turn.ToolId)
            .Where(id => !string.IsNullOrWhiteSpace(id));
        return $"effective round limit {limit}, category {task.Category ?? "(none)"}, risk {task.Risk}, "
            + $"rounds used {task.ToolRounds}, calls issued {task.ToolTurns.Count}, "
            + $"last tools [{string.Join(", ", lastTools)}]";
    }

    /// <summary>
    /// One structured line per tool round, so the event stream shows how a task
    /// walked to its end instead of only the last sentence it produced.
    /// </summary>
    private async Task EmitToolRoundEventAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        FrozenRunConfigurationV1 configuration,
        int roundLimit,
        WorkerModelTurn model,
        CancellationToken cancellationToken)
    {
        // Deterministic idempotency key: a crash between the model turn and the
        // checkpoint save re-runs this round, and the journal must not grow a
        // second line for it.
        var idempotencyKey = $"worker-tool-round:{run.RunId}:{task.TaskId:N}:{task.ToolRounds}";
        await AppendEventAsync(Guid.Parse(run.RunId), "worker.tool_round",
            $"Worker round {task.ToolRounds}: {model.Calls.Count} call(s) this round, {task.ToolTurns.Count + model.Calls.Count} in total.",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                lane_key = LaneKeyOf(task),
                round = task.ToolRounds,
                effective_round_limit = roundLimit,
                round_limit_source = configuration.Tools.ResolveTaskRoundLimitSource(task.Category, task.Risk),
                task_category = task.Category,
                calls_this_round = model.Calls.Count,
                calls_total = task.ToolTurns.Count + model.Calls.Count,
                task_tokens_used = task.TokensUsed,
                task_token_budget = configuration.Context.DefaultTokenBudget,
                run_tokens_used = Maf18RuntimeAdapter.BudgetTokens(checkpoint.ModelUsage),
                run_token_budget = configuration.Context.RunTokenBudget
            }, cancellationToken, taskId: task.TaskId, idempotencyKey: idempotencyKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Announces that the worker is wrapping up rather than continuing. Carries the
    /// full budget context because the event is the durable record of *why* a task
    /// stopped; <c>hard_ceiling</c> marks the absolute-fuse path, which usually
    /// means a bug rather than an exhausted budget.
    /// </summary>
    private async Task EmitCloseoutRequestedAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        FrozenRunConfigurationV1 configuration,
        int roundLimit,
        WorkerModelTurn model,
        CancellationToken cancellationToken)
    {
        await AppendEventAsync(Guid.Parse(run.RunId), "worker.budget_exhausted",
            task.CloseoutReason ?? "The worker stopped tool use and is wrapping up.",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                lane_key = LaneKeyOf(task),
                category = task.CloseoutCategory,
                reason = task.CloseoutReason,
                hard_ceiling = task.CloseoutHardCeiling,
                effective_round_limit = roundLimit,
                round_limit_source = configuration.Tools.ResolveTaskRoundLimitSource(task.Category, task.Risk),
                task_category = task.Category,
                rounds_used = task.ToolRounds,
                calls_issued = task.ToolTurns.Count,
                dropped_calls = model.Calls.Count,
                task_tokens_used = task.TokensUsed,
                task_token_budget = configuration.Context.DefaultTokenBudget,
                run_tokens_used = Maf18RuntimeAdapter.BudgetTokens(checkpoint.ModelUsage),
                run_token_budget = configuration.Context.RunTokenBudget,
                max_tool_calls = configuration.Tools.MaxToolCalls
            }, cancellationToken).ConfigureAwait(false);
    }

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
        await TrySetRunStatusAsync(run.RunId, "reviewing", null, cancellationToken).ConfigureAwait(false);
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
            // Modes without a supervisor in their frozen roster (e.g.
            // conversation.ask/vibe) skip the review gate instead of failing:
            // the roster is the authority for which roles take part in a run.
            var supervisorDefinition = configuration.OperationAgents
                .SingleOrDefault(item => string.Equals(item.Id, "supervisor", StringComparison.Ordinal));
            if (supervisorDefinition is null || !supervisorDefinition.Enabled)
            {
                verdict = new SupervisionVerdict(SupervisionDecision.Pass,
                    ["The frozen roster carries no supervisor; the review gate is skipped for this mode."], []);
                await AppendEventAsync(runId, "supervision.skipped", "Supervision skipped: no supervisor in the frozen roster.", new
                {
                    run_id = run.RunId,
                    revision_round = checkpoint.SupervisionRound
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ValidateFrozenAgent(supervisorDefinition, "supervisor");
                var supervisorInstance = await EnsureSupervisorAgentAsync(run, configuration, checkpoint, supervisorDefinition, cancellationToken).ConfigureAwait(false);
                checkpoint.SupervisorAgentId = supervisorInstance.Id;
                var context = await BuildContextAsync(run, configuration, supervisorDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
                var assembly = await AssemblePromptAsync(configuration, supervisorDefinition, context, cancellationToken).ConfigureAwait(false);
                var supervisor = new SupervisionAgent(CreateModelFactory(configuration, checkpoint, supervisorDefinition,
                    supervisorInstance.Id, supervisorInstance.ParentInstanceId), _logger);
                verdict = await supervisor.ReviewAsync(checkpoint.UserGoal, plans, results, checkpoint.SupervisionRound, assembly.Instructions, cancellationToken).ConfigureAwait(false);
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, supervisor.LastUsage);
            }
        }
        else
        {
            verdict = new SupervisionVerdict(SupervisionDecision.Pass, [], []);
        }
        checkpoint.SupervisionDecision = verdict.DecictionString();
        checkpoint.SupervisionReasons = verdict.Reasons.ToList();
        // A task that stopped on a budget, fuse, or loop trigger must not read as an
        // ordinary pass. Surfacing the trigger in the supervision reasons keeps
        // "completed but the goal was not reached" visible in the final answer
        // instead of only in the event log.
        foreach (var stopped in checkpoint.Tasks.Where(item => item.CloseoutRequested))
        {
            var reason = $"closeout:{stopped.TaskKey}:{stopped.CloseoutCategory}"
                + (stopped.CloseoutHardCeiling ? " (hard_ceiling)" : string.Empty)
                + $" — {stopped.CloseoutReason}";
            if (!checkpoint.SupervisionReasons.Contains(reason, StringComparer.Ordinal)) checkpoint.SupervisionReasons.Add(reason);
        }
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
            await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review before this run can finish.", cancellationToken).ConfigureAwait(false);
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
            var reviseIndexes = verdict.ReviseTaskIndexes.Where(index => index >= 0 && index < checkpoint.Tasks.Count).ToArray();
            // A revise verdict is a replan, not a retry: the planner sees the
            // supervision feedback and returns a revised graph that is merged
            // over the current one (completed work survives by task_key via
            // MergeReplannedGraph). Only when the planner cannot produce a
            // valid graph do we fall back to resetting the flagged nodes.
            var replanned = await TryReplanFromSupervisionAsync(run, configuration, checkpoint, verdict, reviseIndexes, cancellationToken).ConfigureAwait(false);
            if (!replanned)
            {
                foreach (var index in reviseIndexes)
                {
                    var node = checkpoint.Tasks[index];
                    node.Status = "pending";
                    node.ResultStatus = null;
                    node.ResultSummary = null;
                    node.Evidence = [];
                }
                await AppendEventAsync(runId, "supervision.replan_fallback", "The supervision replan produced no valid graph; flagged tasks were reset for re-execution.", new
                {
                    run_id = run.RunId,
                    revision_round = checkpoint.SupervisionRound,
                    task_keys = reviseIndexes.Select(index => checkpoint.Tasks[index].TaskKey).ToArray()
                }, cancellationToken).ConfigureAwait(false);
            }
            checkpoint.SupervisionRound++;
            checkpoint.Phase = "executing";
            await TrySetRunStatusAsync(run.RunId, "replanning", null, cancellationToken).ConfigureAwait(false);
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "replanned", cancellationToken).ConfigureAwait(false);
        }

        if (checkpoint.Lanes.Any(lane => lane.Escalated))
        {
            // A lane escalation is still pending a user decision: the run must
            // not finalize while that gate is unresolved, even though every
            // task is terminal.
            checkpoint.Phase = "awaiting_user";
            await TrySetRunStatusAsync(run.RunId, "awaiting_user", "A lane escalation is pending a user decision.", cancellationToken).ConfigureAwait(false);
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lane-escalation-parked", cancellationToken).ConfigureAwait(false);
        }
        checkpoint.Phase = "finalizing";
        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "reviewed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the planner again with the supervision verdict as feedback and merges
    /// the revised graph over the current checkpoint. Returns false when no valid
    /// graph could be produced (or a replan is unsafe), in which case the caller
    /// keeps the legacy reset-and-retry behavior.
    /// </summary>
    private async Task<bool> TryReplanFromSupervisionAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        SupervisionVerdict verdict,
        int[] reviseIndexes,
        CancellationToken cancellationToken)
    {
        // A replan while a lane escalation awaits a user decision would rewrite
        // tasks the user is about to judge. Leave those runs on retry semantics.
        if (checkpoint.Lanes.Any(lane => lane.Escalated)) return false;

        // Declared-graph replan: the conversation identity stays the task-graph
        // author (same author instance, no task_planner requirement).
        var author = await EnsureConversationAuthorAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        checkpoint.MeetingAgentId = author.Id;
        checkpoint.PlannerAgentId = author.Id;
        var plannerDefinition = RequiredConversationAgent(configuration);

        var context = await BuildContextAsync(run, configuration, plannerDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(configuration, plannerDefinition, context, cancellationToken).ConfigureAwait(false);
        var instructions = SupervisionReplanInstructions(checkpoint, assembly.Instructions, verdict, reviseIndexes);
        // Revise indexes address the pre-replan list, which the merge below
        // replaces: capture the flagged keys now so they can be forced back to
        // pending afterwards (merge preserves completed work by task_key).
        var reviseKeys = new HashSet<string>(
            reviseIndexes.Select(index => checkpoint.Tasks[index].TaskKey),
            StringComparer.OrdinalIgnoreCase);

        PlannedTask[] planned = [];
        Exception? lastError = null;
        var replanParseError = string.Empty;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var contextForPlanner = CreateRunContext(run, checkpoint);
                var planner = new PlanningAgent(CreateModelFactory(configuration, checkpoint, plannerDefinition,
                    checkpoint.PlannerAgentId, null), _logger);
                var instructionsForAttempt = attempt == 0
                    ? PlannerInstructions(checkpoint, instructions)
                    : PlannerInstructions(checkpoint, instructions) + BuildPlannerRetryHint(lastError, replanParseError);
                planned = await planner.PlanAsync(
                    contextForPlanner,
                    BuildFrozenPlannerRoster(configuration),
                    instructionsForAttempt,
                    cancellationToken).ConfigureAwait(false);
                replanParseError = planner.LastParseErrorDetail;
                RefuseSilentPlanningFallbackOnDeclaredEdges(configuration, planned);
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, planner.LastUsage);
                checkpoint.Tasks = MergeReplannedGraph(checkpoint.Tasks,
                    ValidateAndMaterializeGraph(planned, configuration.Spawn.MaxAgentsPerRun));
                // A revise verdict explicitly rejects the flagged tasks' results,
                // so those keys re-execute even when the planner keeps them.
                foreach (var node in checkpoint.Tasks.Where(node => reviseKeys.Contains(node.TaskKey)))
                {
                    node.Status = "pending";
                    node.ResultStatus = null;
                    node.ResultSummary = null;
                    node.Evidence = [];
                    node.CompletedAt = null;
                }
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
            _logger.TryLogWarning(lastError, "Supervision replan produced no valid graph for run {RunId}; falling back to task reset.", run.RunId);
            return false;
        }

        checkpoint.PlanRevision++;
        await AppendEventAsync(Guid.Parse(run.RunId), "task_graph.created", $"{checkpoint.Tasks.Count} task(s) replanned from supervision revision.", new
        {
            run_id = run.RunId,
            plan_revision = checkpoint.PlanRevision,
            supervision_round = checkpoint.SupervisionRound,
            replan = true,
            task_count = checkpoint.Tasks.Count,
            task_keys = checkpoint.Tasks.Select(item => item.TaskKey).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.TaskGraphCreated, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string SupervisionReplanInstructions(
        FullDuplexCheckpointV1 checkpoint,
        string baseInstructions,
        SupervisionVerdict verdict,
        int[] reviseIndexes)
    {
        var parts = new List<string>
        {
            "Supervision rejected the previous execution and requests a revised plan (this is a replan, not a fresh plan).",
            $"Supervision round: {checkpoint.SupervisionRound}."
        };
        var reasons = verdict.Reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).ToArray();
        if (reasons.Length != 0)
        {
            parts.Add("Supervision reasons:");
            parts.AddRange(reasons.Select(reason => $"- {reason}"));
        }
        if (reviseIndexes.Length != 0)
        {
            parts.Add("Flagged tasks and their last outcomes (repair or replace these; keep their task_key when the work continues):");
            foreach (var index in reviseIndexes)
            {
                var node = checkpoint.Tasks[index];
                parts.Add($"- task_key '{node.TaskKey}' (lane '{node.LaneKey ?? "main"}'): {node.Title}");
                if (!string.IsNullOrWhiteSpace(node.ResultSummary))
                    parts.Add($"  last summary: {Truncate(node.ResultSummary, 400)}");
                var unsatisfied = node.CriteriaVerdicts.Where(item => !item.Satisfied).ToArray();
                foreach (var item in unsatisfied)
                    parts.Add($"  unsatisfied criterion '{item.Criterion}': {Truncate(item.Evidence, 300)}");
                foreach (var evidence in node.Evidence.Where(item => !string.IsNullOrWhiteSpace(item)).Take(3))
                    parts.Add($"  evidence: {Truncate(evidence, 300)}");
            }
        }
        parts.Add("Completed tasks (by task_key) are preserved automatically; do not rename completed work. Keep the graph within the frozen worker roster and tool manifest.");
        return baseInstructions + "\n\n" + string.Join("\n", parts);
    }

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength] + "…";


    private async Task<FullDuplexCheckpointV1> RespondToInteractionAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
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
        var meetingDefinition = RequiredConversationAgent(configuration);
        var context = await BuildContextAsync(run, configuration, meetingDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var factory = CreateModelFactory(configuration, checkpoint, meetingDefinition, checkpoint.MeetingAgentId, null);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(configuration, meetingDefinition, context, cancellationToken).ConfigureAwait(false);

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
        // Strip inline reasoning before the user-facing answer and before lane-protocol
        // line extraction, so thinking markup never reaches the user or the directive parser.
        var answer = ModelOutputText.AnswerText(response.Text);
        if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("Meeting agent returned no output.");
        return await FinalizeInteractionTextAsync(run, configuration, checkpoint, answer, cancellationToken).ConfigureAwait(false);
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
                    lane.Status = LaneStatus.Pending;
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
                await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review before this run can finish.", cancellationToken).ConfigureAwait(false);
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
            var meetingDefinition = RequiredConversationAgent(configuration);
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

        // Persisted BEFORE the turn's closing revision is read, so the evidence message
        // is part of the turn the next request inherits.
        await PersistRunEvidenceAsync(runId, run, checkpoint, cancellationToken).ConfigureAwait(false);

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
        await AppendEventAsync(runId, "task.cancelled", "The run was cancelled.", new { run_id = runId }, cancellationToken, idempotencyKey: $"run:{runId}:event:task.cancelled").ConfigureAwait(false);
        await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            checkpoint.TurnId,
            "done",
            FinishReason: "cancelled",
            IdempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:cancelled"), cancellationToken).ConfigureAwait(false);
        await DrainRunDirectivesAsync(state, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        await _instances.ReleaseRunInstancesAsync(runId, cancellationToken).ConfigureAwait(false);
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
        await AppendEventAsync(runId, "run.failed", message, new { run_id = runId, error_category = category }, cancellationToken, idempotencyKey: $"run:{runId}:event:run.failed").ConfigureAwait(false);
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
        await AppendEventAsync(runId, "run.failed", message, new { run_id = runId, error_category = category }, cancellationToken, idempotencyKey: $"run:{runId}:event:run.failed").ConfigureAwait(false);
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

    /// <summary>
    /// Graph-tier root: ensure only the conversation identity exists (it authors
    /// the task graph); no task_planner requirement. Reuses the existing meeting
    /// instance across replans.
    /// </summary>
    private async Task<RuntimeAgentInstance> EnsureConversationAuthorAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var conversationDefinition = RequiredConversationAgent(configuration);
        var runId = Guid.Parse(run.RunId);
        var all = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var author = checkpoint.MeetingAgentId is { } meetingId ? all.FirstOrDefault(item => item.Id == meetingId) : null;
        author ??= all.FirstOrDefault(item => !item.Generated && item.AgentVersionId == conversationDefinition.AgentVersionId);
        if (author is not null)
        {
            VerifyRootInstance(author, conversationDefinition, checkpoint.MeetingAgentId, "meeting");
            return author;
        }
        author = await _instances.CreateRootAsync(new RuntimeAgentSeed(
            Guid.Parse(run.SessionId),
            runId,
            conversationDefinition.Id,
            conversationDefinition.Layer,
            conversationDefinition.Role,
            "chat",
            conversationDefinition.Capabilities,
            conversationDefinition.AllowedTools,
            ResourceSeed(conversationDefinition.ResourceGrants),
            configuration.Context.DefaultTokenBudget,
            AgentDefinitionId: conversationDefinition.AgentDefinitionId,
            AgentVersionId: conversationDefinition.AgentVersionId,
            VersionContentHash: conversationDefinition.VersionContentHash,
            DirectUserOutput: true), cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "agent.created", "Conversation author agent created.", new
        {
            agent_instance_id = author.Id,
            agent_slug = conversationDefinition.Id,
            layer = author.Layer,
            role = author.Role
        }, cancellationToken).ConfigureAwait(false);
        return author;
    }

    /// <summary>
    /// Locates the conversation-identity definition in the frozen operation roster.
    /// Graph tiers always carry the slug; the literal meeting slug is the tolerance
    /// fallback for bodies frozen before identity existed.
    /// </summary>
    private static RuntimeAgentDefinition RequiredConversationAgent(FrozenRunConfigurationV1 configuration)
    {
        var slug = configuration.Graph?.ConversationTemplateSlug ?? "meeting";
        return configuration.OperationAgents.SingleOrDefault(item =>
            string.Equals(item.Id, slug, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"The frozen operation roster has no conversation agent '{slug}'.");
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
            ResourceSeed(definition.ResourceGrants),
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
        var assembly = await AssemblePromptAsync(configuration, meetingDefinition, context, cancellationToken).ConfigureAwait(false);
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
        // Strip inline reasoning so the only user-facing answer never carries thinking markup.
        var answer = ModelOutputText.AnswerText(response.Text);
        if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("Meeting agent returned no output.");
        return answer;
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
            _logger.TryLogWarning(ex, "Operational trigger evaluation failed for run {RunId}.", run.RunId);
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
                _logger.TryLogWarning(ex, "Operational role '{Agent}' dispatch failed for run {RunId}.", match.Agent.Id, run.RunId);
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
                    _logger.TryLogDebug(eventEx, "Could not record operational dispatch failure for run {RunId}.", run.RunId);
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
        var assembly = await AssemblePromptAsync(configuration, match.Agent, context, cancellationToken).ConfigureAwait(false);
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
        // Strip inline reasoning so the compressed context patch is clean prose, not thinking markup.
        var summary = ModelOutputText.AnswerText(response.Text);
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
        var assembly = await AssemblePromptAsync(configuration, match.Agent, context, cancellationToken).ConfigureAwait(false);
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
        // The advisor is a reasoning model too: its {"recommendations":[...]} object may
        // be wrapped in thinking prose or fences, so try each balanced object candidate.
        foreach (var candidate in ModelOutputText.ExtractJsonCandidates(text, array: false))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("recommendations", out var array)
                    || array.ValueKind != JsonValueKind.Array) continue;
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
                // Not the recommendations object; try the next balanced candidate.
            }
        }
        return [];
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
        var assembly = await AssemblePromptAsync(configuration, match.Agent, context, cancellationToken).ConfigureAwait(false);
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
            ResourceSeed(definition.ResourceGrants),
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
        var assembly = await AssemblePromptAsync(configuration, match.Agent, context, cancellationToken).ConfigureAwait(false);
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
        var suggestionText = ModelOutputText.AnswerText(response.Text);
        var suggestion = string.IsNullOrWhiteSpace(suggestionText) ? "No git steward suggestion produced." : suggestionText;
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
        // The curator is a reasoning model: its curation object may be wrapped in
        // thinking prose or fences, so try each balanced object candidate until one
        // yields candidates. Cloned proposal elements outlive each disposed document.
        foreach (var candidate in ModelOutputText.ExtractJsonCandidates(text, array: false))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(candidate);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

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

            if (memoryCandidates.Count > 0 || agentCandidates.Count > 0) break;
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

    private Task AppendEventAsync(Guid runId, string type, string summary, object payload, CancellationToken ct, Guid? taskId = null, string? idempotencyKey = null) =>
        _lifecycle.AppendEventAsync(runId, type, payload, summary, taskId: taskId, idempotencyKey: idempotencyKey, cancellationToken: ct);

    private static bool IsTerminal(RunStatus status) => status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;

    private static bool IsTaskTerminal(DurableTaskNode task) =>
        task.Status is "completed" or "failed" or "blocked";

    /// <summary>
    /// A pending tool execution counts only while its task is still alive. A task
    /// that reached a terminal state keeps no resumable execution — the linkage is
    /// cleared on termination, and this predicate is the belt-and-braces guard for
    /// checkpoints written before that rule existed.
    /// </summary>
    private static bool HasLivePendingExecution(DurableTaskNode task) =>
        !IsTaskTerminal(task) && !string.IsNullOrWhiteSpace(task.PendingToolExecutionId);

    /// <summary>
    /// Runtime run-status writes are advisory: durable state (checkpoint phase,
    /// lane flags, pending executions) is authoritative, and a rejected transition
    /// must never escalate into a run failure. A lane parked on an approval while
    /// another lane already waits on the user is a legal coexistence — the write is
    /// skipped, not retried. Terminal transitions (failed/completed) stay on the
    /// direct path in FailRunAsync/FinalizeAsync.
    /// </summary>
    private async Task TrySetRunStatusAsync(string runId, string status, string? message, CancellationToken cancellationToken)
    {
        try
        {
            var current = await _lifecycle.GetRunStateAsync(runId, cancellationToken).ConfigureAwait(false);
            if (!RunStatusMachine.CanTransition(ToStatusVocabulary(current.Status), status))
            {
                _logger.TryLogDebug("Run {RunId} status write '{Status}' skipped: current state is {Current}.", runId, status, current.Status);
                return;
            }
            await _lifecycle.SetRunStatusAsync(runId, status, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogWarning(ex, "Could not move run {RunId} to {Status}; the durable checkpoint remains authoritative.", runId, status);
        }
    }

    private static string ToStatusVocabulary(RunStatus status) => status switch
    {
        RunStatus.Planning => "planning",
        RunStatus.Understanding => "understanding",
        RunStatus.Executing => "executing",
        RunStatus.Replanning => "replanning",
        RunStatus.AwaitingApproval => "awaiting_approval",
        RunStatus.AwaitingDelegate => "awaiting_delegate",
        RunStatus.AwaitingUser => "awaiting_user",
        RunStatus.Paused => "paused",
        RunStatus.Reviewing => "reviewing",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => "planning"
    };

    private static string SafeError(Exception ex) =>
        ex is InvalidOperationException or InvalidDataException or WorkerUnavailableException or WorkerAssignmentException
            ? ex.Message
            : "Unexpected runtime failure.";

    /// <summary>How many tool rounds the persisted run digest carries, newest last.</summary>
    private const int MaxRunEvidenceTurns = 12;

    /// <summary>
    /// Hands a failed tool call back to the worker as a tool RESULT and lets the loop
    /// continue. Shared by every failure exit that happens BEFORE a dispatch exists
    /// (unparseable arguments, a prepare that produced no execution), so none of them
    /// can quietly keep the old behaviour — killing the task on the first failure, with
    /// the model never told why. Convergence stays with the loop guard and the token
    /// budgets, not with this exit.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> FeedToolFailureBackAsync(
        RunState run,
        DurableTaskNode task,
        WorkerToolTurn turn,
        string category,
        string message,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        turn.DispatchStatus ??= category;
        turn.ErrorCategory ??= category;
        turn.ResultJson = ToolCallFailureJson(turn.ToolId, category, message);
        ClearPendingToolExecution(task);
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-failure", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "worker.tool_failed",
            $"Tool '{turn.ToolId}' failed ({category}); the failure was returned to the worker as a tool result.",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                lane_key = LaneKeyOf(task),
                tool_id = turn.ToolId,
                call_id = turn.CallId,
                dispatch_status = turn.DispatchStatus,
                error_category = category,
                message,
                consecutive_errors = TrailingConsecutiveErrors(task),
                returned_to_worker = true
            }, cancellationToken, task.TaskId, idempotencyKey: $"tool-failure:{task.TaskId}:{turn.CallId}").ConfigureAwait(false);
        return checkpoint;
    }

    /// <summary>Per-line cap, so one long result cannot dominate the digest.</summary>
    private const int MaxRunEvidenceLineLength = 240;

    /// <summary>
    /// Persists a bounded digest of this run's tool activity as a conversation
    /// message, so the NEXT turn's history assembly can see what actually happened.
    ///
    /// Tool calls and their outcomes used to live only in the checkpoint: the
    /// conversation kept the prose summary and nothing else, so a follow-up question
    /// ("why did that fail?") was answered by a model with no memory of its own
    /// tooling. That is what made a mis-dispatched worker and a refused write
    /// invisible in the one place the user actually looks.
    /// </summary>
    private async Task PersistRunEvidenceAsync(Guid runId, RunState run, FullDuplexCheckpointV1 checkpoint, CancellationToken cancellationToken)
    {
        var turns = checkpoint.Tasks.SelectMany(task => task.ToolTurns).ToList();
        if (turns.Count == 0) return;

        var rendered = turns
            .TakeLast(MaxRunEvidenceTurns)
            .Select(turn =>
            {
                var status = string.IsNullOrWhiteSpace(turn.DispatchStatus) ? "completed" : turn.DispatchStatus;
                var category = string.IsNullOrWhiteSpace(turn.ErrorCategory) ? string.Empty : $" ({turn.ErrorCategory})";
                return TruncateEvidence($"{turn.ToolId} {status}{category}: {turn.ResultJson ?? string.Empty}");
            })
            .ToList();

        var content = $"tool_evidence — {turns.Count} tool round(s), showing the last {rendered.Count}\n"
            + string.Join('\n', rendered);
        await _conversations.AppendMessageAsync(checkpoint.SessionId, "tool_evidence", content,
            runId, checkpoint.TurnId, $"run:{run.RunId}:turn:{checkpoint.TurnId}:tool-evidence:v1", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "run.evidence_recorded",
            "The run's tool activity was persisted into the conversation history.",
            new { turn_count = turns.Count, recorded = rendered.Count }, cancellationToken).ConfigureAwait(false);
    }

    private static string TruncateEvidence(string value)
    {
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxRunEvidenceLineLength ? collapsed : collapsed[..MaxRunEvidenceLineLength] + "…";
    }

    private static StepResult BuildCompletedStepResult(DurableTaskNode task, Guid workerId, string text)
    {
        var summary = ExtractContextPatch(text, out var patchSummary, out var patchContent);
        var taskId = task.TaskId;
        return new StepResult
        {
            TaskNodeId = taskId,
            AgentId = workerId.ToString("N"),
            Status = "completed",
            Summary = summary,
            Evidence = [summary, DispatchEvidence(task)],
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

    /// <summary>
    /// A worker resolution/creation failure rooted in the frozen configuration or
    /// its persisted bindings (unknown required tool, missing version binding,
    /// grant/template subset rejection, spawn budget). Callers convert it into a
    /// task-level failure; genuine engine invariants (e.g. a missing planner
    /// instance) keep throwing plain InvalidDataException and fail the run.
    /// </summary>
    private sealed class WorkerAssignmentException(string message, Exception? inner = null)
        : Exception(message, inner);

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

    /// <summary>
    /// Parallel instance pool carrying the conversation identity (DmaEA graph
    /// orchestration). The conversation-identity instance IS the main instance:
    /// user-facing output is emitted only through it, pool members execute
    /// envelope-bounded sub-work while sharing context_revision and tools.
    /// Tolerance read: an absent/empty pool (checkpoints written before this
    /// field) means the legacy singleton path — identity/planner/supervisor are
    /// the Guid? fields above and no pool scheduling applies. Both shapes are
    /// written during the transition; the singleton fields are retired in Phase 2.
    /// </summary>
    public Guid? ConversationIdentityInstanceId { get; set; }
    public List<Guid> InstancePoolIds { get; set; } = [];
    public Guid? MainInstanceId { get; set; }
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

    /// <summary>
    /// Set in the same CAS save as the first planning tick once the
    /// orchestration.mode_tier_decided event has been appended, so concurrent
    /// ticks cannot double-announce (the checkpoint is the single-writer state
    /// machine; no reliance on event-dedupe). Absent on pre-graph checkpoints.
    /// </summary>
    public bool GraphTierAnnounced { get; set; }

    /// <summary>
    /// Set when a graph-tier (lane-less) approval expiry parks the run on the
    /// user-review escalation. Unlike a supervision escalation, a stray wake must
    /// NOT be read as "user chose continue": the flag keeps the run parked until
    /// an explicit user decision clears it. Absent on non-graph checkpoints.
    /// </summary>
    public bool AwaitingApprovalExpiryReview { get; set; }
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

    /// <summary>
    /// Tokens this task has consumed across all of its model turns (the task's
    /// own context budget). Additive and default-suppressed so checkpoints
    /// written before this field existed serialize to identical bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TokensUsed { get; set; }

    /// <summary>Trailing consecutive empty model responses (no text, no calls).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ConsecutiveEmptyResponses { get; set; }

    /// <summary>
    /// Set once a budget or loop-guard trigger stopped tool use. The next round
    /// is text-only (no tool declarations, close-out prompt) and ends the task.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CloseoutRequested { get; set; }

    /// <summary>RunErrorTaxonomy category of the stop that requested the close-out.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CloseoutCategory { get; set; }

    /// <summary>Human-readable stop reason with the concrete budget numbers.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CloseoutReason { get; set; }

    /// <summary>True when the stop came from the absolute call fuse, i.e. an
    /// abnormal path rather than a spent budget.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CloseoutHardCeiling { get; set; }

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


internal sealed record StaleTaskEvidence(
    long InputContextRevision,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset RecordedAt);

internal sealed class RunAwaitingExternalDecisionException : Exception;

internal sealed class WorkerUnavailableException(string message) : Exception(message);
