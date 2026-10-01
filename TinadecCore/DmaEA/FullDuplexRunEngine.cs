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
    Task FinalizeCancelledRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task FinalizeFailedRunAsync(
        Guid runId,
        string? errorCategory = null,
        string? safeErrorMessage = null,
        CancellationToken cancellationToken = default);
    Task ReconcileTerminalRunAsync(Guid runId, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// One model call already performs three short same-candidate attempts. If that
    /// entire call still ends in a transient provider failure, keep the durable run
    /// alive and retry the phase later instead of turning a brief 429/5xx/network
    /// outage into an irreversible user-visible run failure.
    /// </summary>
    internal const int MaxDurableModelRetries = 5;

    internal static bool IsDurableModelRetryCategory(string? category) => category is
        "rate_limited" or "provider_server_error" or "timeout" or "connection_failed" or "provider_error";

    internal static TimeSpan DurableModelRetryBackoff(int retryNumber) => retryNumber switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(15),
        4 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(60)
    };

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
    /// <summary>
    /// The resource ledger, when the host registered one. Optional so a trimmed composition still runs;
    /// the ledger only ever refuses a call, never grants one.
    /// </summary>
    private readonly IResourceLeaseService? _resourceLeases;
    private ISessionOrganization? _organization;
    private bool _organizationResolved;
    private IRunInterrupts? _interrupts;
    private bool _interruptsResolved;

    /// <summary>
    /// The hard-insert signal (todo D2), when the host registered one. Without it every insert is
    /// soft: the steering is read at the run's next model call.
    /// </summary>
    private IRunInterrupts? Interrupts
    {
        get
        {
            if (_interruptsResolved) return _interrupts;
            _interrupts = _services.GetService(typeof(IRunInterrupts)) as IRunInterrupts;
            _interruptsResolved = true;
            return _interrupts;
        }
    }

    /// <summary>
    /// The session's TinaChat organization, resolved on first use (the communication module is optional
    /// in a host). Every call through it is best-effort: communication is a view over the work, so a
    /// failure to enrol or to notify is logged and never fails a run.
    /// </summary>
    private ISessionOrganization? Organization
    {
        get
        {
            if (_organizationResolved) return _organization;
            _organization = _services.GetService(typeof(ISessionOrganization)) as ISessionOrganization;
            _organizationResolved = true;
            return _organization;
        }
    }
    private readonly ILogger<FullDuplexRunEngine> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _scheduledModelRetries = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _terminalFinalizationGates = new();
    private readonly AsyncLocal<RunLeaseEpoch?> _currentLeaseEpoch = new();
    /// <summary>
    /// Parallel ready workers run their model/tool loop against a private checkpoint snapshot. Their
    /// durable writes are merged by the owning run tick; nested SaveCheckpoint calls must therefore
    /// remain local until that merge completes.
    /// </summary>
    private readonly AsyncLocal<bool> _deferParallelTaskCheckpointWrites = new();
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
        _resourceLeases = services.GetService(typeof(IResourceLeaseService)) as IResourceLeaseService;
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

    public async Task FinalizeCancelledRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var gate = _terminalFinalizationGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (state.Status != RunStatus.Cancelled) return;
            var checkpoint = await ResolveTerminalCheckpointAsync(runId, state, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                await ReleaseRunInstancesBestEffortAsync(runId, cancellationToken).ConfigureAwait(false);
                return;
            }
            await PersistCancellationTerminalAsync(runId, checkpoint, cancellationToken).ConfigureAwait(false);
            await ReleaseTerminalQueuedInteractionsAsync(state, cancellationToken).ConfigureAwait(false);
            await ReleaseRunInstancesBestEffortAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task FinalizeFailedRunAsync(
        Guid runId,
        string? errorCategory = null,
        string? safeErrorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var gate = _terminalFinalizationGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (state.Status != RunStatus.Failed) return;
            var checkpoint = await ResolveTerminalCheckpointAsync(runId, state, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                await ReleaseRunInstancesBestEffortAsync(runId, cancellationToken).ConfigureAwait(false);
                return;
            }
            var category = string.IsNullOrWhiteSpace(errorCategory)
                ? string.IsNullOrWhiteSpace(state.TerminalErrorCategory)
                    ? RunErrorTaxonomy.Runtime
                    : state.TerminalErrorCategory
                : errorCategory.Trim();
            var message = string.IsNullOrWhiteSpace(safeErrorMessage)
                ? string.IsNullOrWhiteSpace(state.Summary) ? "The run failed before producing a final response." : state.Summary
                : safeErrorMessage.Trim();
            await PersistFailureTerminalAsync(runId, checkpoint, category, message, cancellationToken).ConfigureAwait(false);
            await ReleaseTerminalQueuedInteractionsAsync(state, cancellationToken).ConfigureAwait(false);
            await ReleaseRunInstancesBestEffortAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReconcileTerminalRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var state = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        switch (state.Status)
        {
            case RunStatus.Cancelled:
                await FinalizeCancelledRunAsync(runId, cancellationToken).ConfigureAwait(false);
                break;
            case RunStatus.Failed:
                await FinalizeFailedRunAsync(runId, cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case RunStatus.Completed:
                await ReleaseTerminalQueuedInteractionsAsync(state, cancellationToken).ConfigureAwait(false);
                await ReleaseRunInstancesBestEffortAsync(runId, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task<FullDuplexCheckpointV1?> ResolveTerminalCheckpointAsync(
        Guid runId,
        RunState state,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(state.SessionId, out var sessionId)
            || !Guid.TryParse(state.TurnId, out var turnId))
        {
            _logger.TryLogWarning("Terminal run {RunId} has no valid session/turn identity for finalization.", runId);
            return null;
        }

        var fallback = new FullDuplexCheckpointV1
        {
            RunId = runId,
            SessionId = sessionId,
            TurnId = turnId,
            TriggerMessageId = Guid.TryParse(state.TriggerMessageId, out var triggerId) ? triggerId : Guid.Empty,
            ContextRevision = state.ContextRevision
        };
        try
        {
            var stored = await TryReadCheckpointAsync(runId).ConfigureAwait(false);
            if (stored is not null
                && stored.RunId == runId
                && stored.SessionId == sessionId
                && stored.TurnId == turnId)
            {
                return stored;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex,
                "Terminal run {RunId} has an unreadable checkpoint; authoritative run identity will close the turn and stream.",
                runId);
        }
        return fallback;
    }

    private async Task ReleaseRunInstancesBestEffortAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            await _instances.ReleaseRunInstancesAsync(runId, cancellationToken).ConfigureAwait(false);
            await ReleaseRunLeasesAsync(runId, cancellationToken).ConfigureAwait(false);
            await SetRunMembersOfflineAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not release agent instances for terminal run {RunId}.", runId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new Dictionary<Guid, Task>();
        var nextScan = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            await EnqueueDueModelRetriesAsync(stoppingToken).ConfigureAwait(false);

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

        var releaseLease = true;
        _currentLeaseEpoch.Value = new RunLeaseEpoch(runId, _ownerId, lease.RecoveryCount);

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

            TinaChatExecutionReservation? chatInput = null;
            try
            {
                if (_services.GetService(typeof(ITinaChatRunInput)) is ITinaChatRunInput chatInputs)
                    chatInput = await chatInputs.GetForSessionAsync(sessionId, stoppingToken).ConfigureAwait(false);
                if (configuration.TinaChatInput is not null || chatInput is not null)
                {
                    var binding = configuration.TinaChatInput;
                    if (chatInput is null || binding is null || binding.ExecutionId != chatInput.Execution.Id
                        || binding.IntentId != chatInput.Execution.IntentId || binding.ParticipantId != chatInput.Execution.ParticipantId
                        || trigger.Content != chatInput.Content || trigger.ClientMessageId != chatInput.ClientMessageId)
                        throw new TinaChatException(403, "tina_chat_input_locked", "The run input does not match its authorized communication handoff.");
                }
            }
            catch (TinaChatException ex)
            {
                await FailLegacyRunAsync(runId, run, ex.Code, ex.Message, stoppingToken).ConfigureAwait(false);
                return;
            }
            var checkpoint = await LoadOrCreateCheckpointAsync(runId, run, sessionId, turnId, trigger, stoppingToken).ConfigureAwait(false);
            if (checkpoint is null) return;
            if (chatInput is not null && checkpoint.UserGoal != chatInput.Content)
            {
                await FailRunAsync(runId, checkpoint, "tina_chat_input_locked", "The checkpoint goal no longer matches the accepted intent.", stoppingToken).ConfigureAwait(false);
                return;
            }

            // A prior model call exhausted its short in-call retry budget. The
            // checkpoint is the durable schedule authority; the in-memory queue is
            // only an acceleration so a live host need not wait for the recovery
            // scan. A retained lease prevents the scan from reacquiring this same
            // run every two seconds and inflating RecoveryCount while it is cooling
            // down. If the host dies, the retained lease expires shortly after the
            // retry time and normal recovery resumes the run.
            if (checkpoint.ModelRetryNotBefore is { } retryNotBefore)
            {
                var now = DateTimeOffset.UtcNow;
                if (retryNotBefore > now)
                {
                    ScheduleModelRetryInMemory(runId, retryNotBefore);
                    releaseLease = !await TryRetainLeaseUntilAsync(
                        runId,
                        retryNotBefore,
                        lease.RecoveryCount,
                        stoppingToken).ConfigureAwait(false);
                    return;
                }

                var retryNumber = checkpoint.TransientModelRetryCount;
                var retryCategory = checkpoint.LastTransientModelErrorCategory;
                checkpoint.ModelRetryNotBefore = null;
                checkpoint = await SaveCheckpointAsync(
                    checkpoint,
                    checkpoint.CheckpointRevision,
                    $"model-retry-resumed-{retryNumber}",
                    stoppingToken).ConfigureAwait(false);
                try
                {
                    await AppendEventAsync(runId, "run.model_retry_resumed",
                        $"Transient model retry {retryNumber}/{MaxDurableModelRetries} resumed.",
                        new
                        {
                            run_id = runId,
                            retry_number = retryNumber,
                            retry_limit = MaxDurableModelRetries,
                            error_category = retryCategory
                        }, stoppingToken,
                        idempotencyKey: $"run:{runId}:model-retry:{retryNumber}:resumed").ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // The checkpoint already cleared the durable retry deadline.
                    // Audit is a side channel and must not fail the resumed run.
                    _logger.TryLogWarning(ex, "Could not append the model retry resumed event for run {RunId}.", runId);
                }
            }

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
                if (checkpoint.SupervisionDecision == "escalate" && !checkpoint.AwaitingApprovalExpiryReview)
                    await PublishSupervisionReviewAsync(run, checkpoint, stoppingToken).ConfigureAwait(false);
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
                    await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
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

                // A pending hard insert (todo D2) is taken only where it is acted on: the tool loop (calls
                // not started are not run), or the catch of a model call it cut off. A model call that
                // starts while one is pending is cut off at once and redone, which costs nothing.
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
                        // continue with the recorded incomplete outcome. Corrections
                        // are applied above as context patches and move the checkpoint
                        // back to planning instead.
                        checkpoint.SupervisionDecision = "user_accepted";
                        checkpoint.UserAcceptedIncompleteOutcome = true;
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

                checkpoint = await ResetTransientModelRetryAfterProgressAsync(
                    runId,
                    checkpoint,
                    stoppingToken).ConfigureAwait(false);
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
        catch (RunInterruptedException interrupted) when (!stoppingToken.IsCancellationRequested)
        {
            // A hard insert cut a model call off mid-step (todo D2). Everything durable up to the
            // last checkpoint stands: this pass ends and the next one redoes the step from there,
            // with the steering applied first. That is the resume a lease recovery performs,
            // started at once instead of by the scan.
            Interrupts?.TakePending(runId);
            await AppendRunInterruptedAsync(runId, interrupted, stoppingToken).ConfigureAwait(false);
            await EnqueueAsync(runId, stoppingToken).ConfigureAwait(false);
        }
        catch (RunCheckpointConflictException)
        {
            // Another owner made progress after a lease hand-off. The scan will load it.
        }
        catch (Exception ex)
        {
            var run = await _lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
            var checkpoint = await TryReadCheckpointAsync(runId).ConfigureAwait(false);
            // A user cancellation/terminal decision can win while a provider call is
            // still in flight. Its late 429/5xx must never resurrect the run by
            // appending retry state to a terminal checkpoint.
            if (IsTerminal(run.Status)
                || RecoveryPolicy.IsAwaitingDecision(run.Status)
                || run.Status == RunStatus.Paused
                || IsCompletionClaimed(run))
            {
                return;
            }
            if (ex is ModelInvocationExhaustedException modelFailure
                && checkpoint is not null
                && IsDurableModelRetryCategory(modelFailure.Category)
                && checkpoint.TransientModelRetryCount < MaxDurableModelRetries)
            {
                var scheduled = await TryScheduleModelRetryAsync(
                    runId,
                    checkpoint,
                    modelFailure,
                    lease.RecoveryCount).ConfigureAwait(false);
                if (scheduled.Scheduled)
                {
                    releaseLease = !scheduled.LeaseRetained;
                    return;
                }
                if (scheduled.ExitWithoutFailure) return;
            }

            // A model call or other external operation can outlive the ordinary
            // lease. Terminal failure is just as stateful as retry scheduling, so a
            // stale owner must not fail a run that a newer owner is already
            // progressing. Fence ownership first, then re-read user-controlled
            // pause/decision/terminal state under the renewed lease.
            if (!await TryRetainLeaseUntilAsync(
                    runId,
                    DateTimeOffset.UtcNow.Add(LeaseDuration),
                    lease.RecoveryCount,
                    CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }
            run = await _lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
            if (IsTerminal(run.Status)
                || RecoveryPolicy.IsAwaitingDecision(run.Status)
                || run.Status == RunStatus.Paused
                || IsCompletionClaimed(run))
            {
                return;
            }
            // A logging provider failure must not prevent the durable failure
            // transition below from publishing the terminal stream event. Retryable
            // model outages return above and therefore do not masquerade as terminal
            // engine crashes in the operational log.
            _logger.TryLogError(ex, "Full-duplex run {RunId} failed in durable engine.", runId);
            // The taxonomy already had model categories; nothing ever wrote them, so a
            // provider outage and a genuine engine defect were indistinguishable in the
            // durable record AND in what the user was told. Classify instead of collapsing.
            var failureCode = ex switch
            {
                WorkerUnavailableException => RunErrorTaxonomy.WorkerUnavailable,
                ModelInvocationExhaustedException { Category: "candidate_unavailable" } => RunErrorTaxonomy.ModelUnavailable,
                ModelInvocationExhaustedException exhausted => ModelFailureCategory(exhausted.Category),
                _ => RunErrorTaxonomy.Runtime
            };
            if (checkpoint is not null) await FailRunAsync(runId, checkpoint, failureCode, SafeError(ex), CancellationToken.None).ConfigureAwait(false);
            else await FailLegacyRunAsync(runId, run, failureCode, SafeError(ex), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (releaseLease)
            {
                try { await _lifecycle.ReleaseRunLeaseAsync(runId.ToString(), _ownerId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger.TryLogDebug(ex, "Could not release run lease {RunId}.", runId); }
            }
            if (_currentLeaseEpoch.Value is { RunId: var currentRunId } && currentRunId == runId)
            {
                _currentLeaseEpoch.Value = null;
            }
        }
    }

    private async Task EnqueueDueModelRetriesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var scheduled in _scheduledModelRetries.ToArray())
        {
            if (scheduled.Value > now) continue;
            if (_scheduledModelRetries.TryRemove(scheduled.Key, out _))
            {
                await EnqueueAsync(scheduled.Key, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ScheduleModelRetryInMemory(Guid runId, DateTimeOffset retryAt) =>
        _scheduledModelRetries.AddOrUpdate(
            runId,
            retryAt,
            (_, current) => current <= retryAt ? current : retryAt);

    private async Task<ModelRetryScheduleResult> TryScheduleModelRetryAsync(
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        ModelInvocationExhaustedException failure,
        int leaseRecoveryCount)
    {
        var retryNumber = checkpoint.TransientModelRetryCount + 1;
        var delay = DurableModelRetryBackoff(retryNumber);
        var retryAt = DateTimeOffset.UtcNow.Add(delay);

        // Fence the write before touching the checkpoint. A provider call can
        // outlive the ordinary 30-second lease; in that case another host may have
        // acquired and progressed the run. Heartbeat/reacquire proves this owner is
        // still authoritative and extends the lease through the cooling-off window.
        var leaseRetained = await TryRetainLeaseUntilAsync(
            runId,
            retryAt,
            leaseRecoveryCount,
            CancellationToken.None).ConfigureAwait(false);
        if (!leaseRetained)
        {
            _logger.TryLogWarning(
                "Transient model failure for run {RunId} arrived after lease ownership was lost; the current owner will recover it.",
                runId);
            return new ModelRetryScheduleResult(false, false, true);
        }

        var fencedRun = await _lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
        if (IsTerminal(fencedRun.Status)
            || RecoveryPolicy.IsAwaitingDecision(fencedRun.Status)
            || fencedRun.Status == RunStatus.Paused
            || IsCompletionClaimed(fencedRun))
        {
            // Control/status writes are authoritative. In particular, cancel owns
            // its own terminal finalization path, so a provider response arriving
            // afterwards must not persist a retry checkpoint over that decision.
            return new ModelRetryScheduleResult(false, true, true);
        }

        checkpoint.TransientModelRetryCount = retryNumber;
        checkpoint.ModelRetryNotBefore = retryAt;
        checkpoint.LastTransientModelErrorCategory = failure.Category;

        var expectedRevision = checkpoint.CheckpointRevision;
        var checkpointKey = $"model-retry-scheduled-{retryNumber}";
        var expectedRunStatus = RunStatusStorageValue(fencedRun.Status);
        try
        {
            checkpoint = await SaveCheckpointAsync(
                checkpoint,
                expectedRevision,
                checkpointKey,
                CancellationToken.None,
                expectedRunStatus: expectedRunStatus,
                requireCompletionUnclaimed: true).ConfigureAwait(false);
        }
        catch (RunCheckpointConflictException ex)
        {
            _logger.TryLogDebug(ex,
                "Transient model retry checkpoint for run {RunId} lost a CAS race; another owner made progress.",
                runId);
            return new ModelRetryScheduleResult(false, true, true);
        }
        catch (Exception firstSaveException)
        {
            // A database/content-store acknowledgement can be lost after the
            // checkpoint transaction committed. Retry the exact same idempotent
            // write once; if its acknowledgement is also ambiguous, read the
            // current checkpoint and accept it only when the retry state matches.
            try
            {
                checkpoint = await SaveCheckpointAsync(
                    checkpoint,
                    expectedRevision,
                    checkpointKey,
                    CancellationToken.None,
                    expectedRunStatus: expectedRunStatus,
                    requireCompletionUnclaimed: true).ConfigureAwait(false);
            }
            catch (Exception retrySaveException)
            {
                FullDuplexCheckpointV1? confirmed = null;
                var confirmationReadSucceeded = false;
                FullDuplexCheckpointV1? observed = null;
                try
                {
                    observed = await TryReadCheckpointAsync(runId).ConfigureAwait(false);
                    confirmationReadSucceeded = true;
                    if (observed is not null
                        && observed.TransientModelRetryCount == retryNumber
                        && observed.ModelRetryNotBefore == retryAt
                        && string.Equals(
                            observed.LastTransientModelErrorCategory,
                            failure.Category,
                            StringComparison.Ordinal))
                    {
                        confirmed = observed;
                    }
                }
                catch (Exception confirmationException)
                {
                    _logger.TryLogWarning(confirmationException,
                        "Could not confirm the ambiguous model retry checkpoint for run {RunId}.",
                        runId);
                }

                if (confirmed is null)
                {
                    _logger.TryLogWarning(firstSaveException,
                        "The first model retry checkpoint write for run {RunId} returned an ambiguous failure.",
                        runId);
                    if (!confirmationReadSucceeded)
                    {
                        _logger.TryLogWarning(retrySaveException,
                            "The idempotent retry checkpoint write for run {RunId} also failed and its commit outcome could not be read; releasing the lease for recovery instead of emitting a contradictory terminal failure.",
                            runId);
                        return new ModelRetryScheduleResult(false, false, true);
                    }

                    if (observed is not null && observed.CheckpointRevision > expectedRevision)
                    {
                        _logger.TryLogDebug(retrySaveException,
                            "A newer checkpoint won while scheduling a model retry for run {RunId}; the current execution will stop without failing that progress.",
                            runId);
                        return new ModelRetryScheduleResult(false, true, true);
                    }

                    _logger.TryLogWarning(retrySaveException,
                        "The retry checkpoint for run {RunId} was confirmed absent after two failed writes; the run will fail closed so the finite model retry fuse cannot be bypassed.",
                        runId);
                    return new ModelRetryScheduleResult(false, true, false);
                }

                checkpoint = confirmed;
            }
        }

        var stateAfterCheckpoint = await _lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
        if (IsTerminal(stateAfterCheckpoint.Status)
            || RecoveryPolicy.IsAwaitingDecision(stateAfterCheckpoint.Status)
            || stateAfterCheckpoint.Status == RunStatus.Paused
            || IsCompletionClaimed(stateAfterCheckpoint))
        {
            return new ModelRetryScheduleResult(false, true, true);
        }

        ScheduleModelRetryInMemory(runId, retryAt);
        try
        {
            await AppendEventAsync(runId, "run.model_retry_scheduled",
                $"The model provider is temporarily unavailable; retry {retryNumber}/{MaxDurableModelRetries} is scheduled.",
                new
                {
                    run_id = runId,
                    retry_number = retryNumber,
                    retry_limit = MaxDurableModelRetries,
                    retry_at = retryAt,
                    delay_ms = (long)delay.TotalMilliseconds,
                    error_category = failure.Category,
                    message = failure.Message
                }, CancellationToken.None,
                idempotencyKey: $"run:{runId}:model-retry:{retryNumber}:scheduled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The checkpoint and queue already carry the retry. Event logging is a
            // side channel and must not turn a recoverable provider outage into a
            // terminal run failure.
            _logger.TryLogWarning(ex, "Could not append the model retry event for run {RunId}.", runId);
        }

        return new ModelRetryScheduleResult(true, true, false);
    }

    private async Task<FullDuplexCheckpointV1> ResetTransientModelRetryAfterProgressAsync(
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.TransientModelRetryCount <= 0 || checkpoint.ModelRetryNotBefore is not null)
        {
            return checkpoint;
        }

        var recoveredRetryCount = checkpoint.TransientModelRetryCount;
        var recoveredCategory = checkpoint.LastTransientModelErrorCategory;
        checkpoint.TransientModelRetryCount = 0;
        checkpoint.LastTransientModelErrorCategory = null;
        checkpoint = await SaveCheckpointAsync(
            checkpoint,
            checkpoint.CheckpointRevision,
            $"model-retry-recovered-{recoveredRetryCount}",
            cancellationToken).ConfigureAwait(false);

        try
        {
            await AppendEventAsync(runId, "run.model_retry_recovered",
                "The run made durable progress after a transient model outage; the consecutive retry fuse was reset.",
                new
                {
                    run_id = runId,
                    recovered_retry_count = recoveredRetryCount,
                    error_category = recoveredCategory
                }, cancellationToken,
                idempotencyKey: $"run:{runId}:model-retry-recovered:{checkpoint.CheckpointRevision}").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not append the model retry recovered event for run {RunId}.", runId);
        }

        return checkpoint;
    }

    private async Task<bool> TryRetainLeaseUntilAsync(
        Guid runId,
        DateTimeOffset retryAt,
        int expectedRecoveryCount,
        CancellationToken cancellationToken)
    {
        var remaining = retryAt - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.FromSeconds(1)) remaining = TimeSpan.FromSeconds(1);
        var duration = remaining + TimeSpan.FromSeconds(5);
        try
        {
            if (await _lifecycle.HeartbeatRunLeaseAsync(
                runId.ToString(),
                _ownerId,
                duration,
                cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            // The lease may have expired during a long provider call without being
            // taken by another host. Reacquire it atomically; a false result means a
            // different owner won and this stale execution must stop writing.
            var reacquired = await _lifecycle.TryAcquireRunLeaseAsync(
                runId.ToString(),
                _ownerId,
                duration,
                cancellationToken).ConfigureAwait(false);
            if (!reacquired.Acquired) return false;
            if (reacquired.RecoveryCount == expectedRecoveryCount + 1)
            {
                if (_currentLeaseEpoch.Value is { RunId: var epochRunId } epoch && epochRunId == runId)
                {
                    // AsyncLocal value replacement inside this awaited child method
                    // would not flow back to the caller's ExecutionContext. Mutate
                    // the shared token object so every parent frame observes R+1.
                    epoch.RecoveryCount = reacquired.RecoveryCount;
                }
                else
                {
                    _currentLeaseEpoch.Value = new RunLeaseEpoch(runId, _ownerId, reacquired.RecoveryCount);
                }
                return true;
            }

            // The lease was acquired and released by at least one other execution
            // epoch while this model call was in flight. OwnerId is process-wide, so
            // matching it is not a sufficient fence; RecoveryCount distinguishes the
            // obsolete call from the newer execution. Release the accidentally
            // reacquired lease immediately and let the current checkpoint owner win.
            try
            {
                await _lifecycle.ReleaseRunLeaseAsync(
                    runId.ToString(),
                    _ownerId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception releaseException)
            {
                _logger.TryLogDebug(releaseException,
                    "Could not release stale reacquired lease for run {RunId}.",
                    runId);
            }
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex,
                "Could not retain the run lease through {RetryAt}; recovery scanning remains the fallback.",
                retryAt);
            return false;
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
                // The instance pool is recovery state like the rest: a restored pool whose shape
                // does not hold (members without an identity, a main that is not the identity)
                // fails closed instead of resuming under an identity nobody froze.
                RunFreezeGate.ValidatePool(new RunFreezeGate.PoolState(
                    checkpoint.ConversationIdentityInstanceId, checkpoint.InstancePoolIds, checkpoint.MainInstanceId));
                return checkpoint;
            }
            catch (RunAdmissionException ex)
            {
                await FailLegacyRunAsync(runId, run, ex.Code, ex.Message, cancellationToken).ConfigureAwait(false);
                return null;
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
        await JoinConversationPoolAsync(run, checkpoint, author.Id, cancellationToken).ConfigureAwait(false);
        var plannerDefinition = RequiredConversationAgent(configuration);
        await EnrolConversationAsync(run, configuration, author.Id, cancellationToken).ConfigureAwait(false);
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
            // The names, in pack order: what the run was actually told. The count alone cannot answer
            // the question anyone asks when the model misbehaves, which is "did it get the project
            // rules at all" — a budget that crowded out an item leaves no other trace.
            sources = context.Evidence.Select(item => item.Source).ToArray(),
            // One row per evidence item, in the same order as `sources`, so a reader can pair an item
            // with its price by index. Aggregating here would lose the difference between one
            // expensive history and eight cheap memories, which is the difference between raising a
            // budget and narrowing retrieval.
            source_tokens = BudgetShares(context.Evidence),
            dropped_sources = BudgetShares(context.Dropped),
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
                    : ValidateAndMaterializeGraph(planned, configuration.Spawn.MaxAgentsPerRun, DispatchRosterOf(configuration));
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
            .Where(item => IsDispatchable(item, checkpoint.Tasks))
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
            // A task resuming from task_wait continues its own attempt: its tool-call keys
            // must stay stable so already-prepared calls are not prepared a second time.
            if (task.Status != "waiting") task.Attempt++;
            task.Status = "running";
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
            if (await AssignWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false) is { } assigned)
                workers[task.TaskId] = assigned;
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

        var parallelToolTasks = toolCapable
            .Where(task => task.ToolTurns.Count == 0 && string.IsNullOrWhiteSpace(task.PendingToolExecutionId))
            // Dispatch/wait can consume durable directives and materialize child tasks. That
            // shared graph belongs to the run owner, not to a disposable worker snapshot.
            .Where(task => !surfaces[task.TaskId].Any(tool =>
                CoreVirtualToolPolicy.IsTaskDispatch(tool.ToolId) || CoreVirtualToolPolicy.IsTaskWait(tool.ToolId)))
            .ToList();
        var serialToolTasks = toolCapable.Except(parallelToolTasks).ToList();
        var usageBeforeParallel = checkpoint.ModelUsage;
        ToolTaskExecutionResult[] parallelResults = parallelToolTasks.Count == 0
            ? []
            : await Task.WhenAll(parallelToolTasks.Select(task => ExecuteParallelToolTaskAsync(
                run, configuration, checkpoint, plannerId, task, cancellationToken, surfaces[task.TaskId]))).ConfigureAwait(false);
        var parallelWaiting = false;
        foreach (var result in parallelResults)
        {
            MergeParallelTaskCheckpoint(checkpoint, result.Checkpoint, result.TaskId);
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage,
                Maf18RuntimeAdapter.UsageSince(result.Checkpoint.ModelUsage, usageBeforeParallel));
            if (result.Waiting)
            {
                parallelWaiting = true;
                continue;
            }
            if (result.Result is not null)
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result.Result, cancellationToken).ConfigureAwait(false);
        }
        if (parallelWaiting)
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken).ConfigureAwait(false);
            throw new RunAwaitingExternalDecisionException();
        }

        foreach (var task in serialToolTasks)
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

    private async Task<ToolTaskExecutionResult> ExecuteParallelToolTaskAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken,
        IReadOnlyList<WorkerToolDescriptor> descriptors)
    {
        var local = CloneCheckpoint(checkpoint);
        var localTask = local.Tasks.Single(item => item.TaskId == task.TaskId);
        var prior = _deferParallelTaskCheckpointWrites.Value;
        _deferParallelTaskCheckpointWrites.Value = true;
        try
        {
            var result = await ExecuteToolTaskAsync(run, configuration, local, plannerId, localTask, cancellationToken, descriptors).ConfigureAwait(false);
            return result with { TaskId = task.TaskId };
        }
        finally
        {
            _deferParallelTaskCheckpointWrites.Value = prior;
        }
    }

    private static FullDuplexCheckpointV1 CloneCheckpoint(FullDuplexCheckpointV1 checkpoint)
    {
        var clone = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(JsonSerializer.Serialize(checkpoint, JsonOptions), JsonOptions)
            ?? throw new InvalidDataException("Could not clone the run checkpoint for a parallel worker.");
        clone.CheckpointRevision = checkpoint.CheckpointRevision;
        return clone;
    }

    private static void MergeParallelTaskCheckpoint(
        FullDuplexCheckpointV1 target,
        FullDuplexCheckpointV1 source,
        Guid taskId)
    {
        var updated = source.Tasks.FirstOrDefault(item => item.TaskId == taskId)
            ?? throw new InvalidDataException($"Parallel worker checkpoint lost task {taskId}.");
        var index = target.Tasks.FindIndex(item => item.TaskId == taskId);
        if (index < 0) throw new InvalidDataException($"Parallel worker task {taskId} is missing from the owner checkpoint.");
        target.Tasks[index] = updated;
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
                : BuildWorkerStepResult(task, worker.Id, model.Text);
            return new TaskExecutionResult(task.TaskId, worker.Id, result.Status, result, model.Usage);
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
            // A hard insert that arrived while a tool call ran, or between rounds (todo D2): the calls
            // the model asked for that have not started are not run, and the next turn reads the
            // steering. A call already running was left to finish — its outcome must stay known.
            if (Interrupts?.TakePending(Guid.Parse(run.RunId)) is { } steering)
                checkpoint = await SkipUnstartedToolCallsAsync(run, checkpoint, task, steering, cancellationToken).ConfigureAwait(false);

            // At most one unresolved call is resumed at a time. A model response
            // may contain multiple calls, but each call gets its own durable
            // prepare/resume boundary and approval decision.
            var pendingTurn = task.ToolTurns.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.ResultJson));
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsTaskWait(pendingTurn.ToolId))
            {
                // task_wait is executed here, not by the dispatcher: it reads and parks the task
                // graph this engine owns. Parking hands the tick back so the sub-tasks can run.
                var wait = await HandleTaskWaitAsync(run, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                checkpoint = wait.Checkpoint;
                if (wait.Parked) return new ToolTaskExecutionResult(checkpoint, Waiting: false, Result: null);
                continue;
            }
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsPlanUpdate(pendingTurn.ToolId))
            {
                // plan_update is state on the task node, so the engine answers it in place.
                checkpoint = await HandlePlanUpdateAsync(run, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                continue;
            }
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

                if (dispatcher is not ILeaseFencedToolDispatcher fencedDispatcher)
                {
                    throw new InvalidOperationException(
                        "The configured tool dispatcher does not support run-lease fenced execution.");
                }
                var executionAuthority = await RequireToolExecutionAuthorityAsync(
                    Guid.Parse(run.RunId),
                    cancellationToken).ConfigureAwait(false);
                dispatch = await fencedDispatcher.ResumeAsync(
                    pendingTurn.ExecutionId!,
                    executionAuthority,
                    cancellationToken).ConfigureAwait(false);
                if (string.Equals(dispatch.ErrorCategory, RunErrorTaxonomy.RunLeaseLost, StringComparison.Ordinal))
                {
                    // This engine instance is stale. Do not turn the fence rejection
                    // into a tool result and then try to checkpoint it under the old
                    // epoch; leave the durable run to the current owner instead.
                    throw new RunCheckpointConflictException(
                        run.RunId,
                        checkpoint.CheckpointRevision,
                        checkpoint.CheckpointRevision);
                }
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

                // The dispatch DTO intentionally stays transport-shaped: a provider
                // response may be wire-successful while the tool's own payload says
                // { success:false }. Pull the durable execution row so that business
                // outcome becomes a code-owned checkpoint fact instead of being left
                // only inside opaque result JSON for the model to interpret.
                var snapshot = await executionCoordinator.FindAsync(
                    Guid.Parse(pendingTurn.ExecutionId!), cancellationToken).ConfigureAwait(false);
                var resultJson = dispatch.Result?.GetRawText();
                if (string.IsNullOrWhiteSpace(resultJson)) resultJson = snapshot?.ResultJson ?? "null";
                pendingTurn.ResultJson = resultJson;
                pendingTurn.DispatchStatus = ToolDispatchStatus.Completed;
                pendingTurn.ToolSuccess = snapshot?.ToolSuccess;
                if (pendingTurn.ToolSuccess == false)
                {
                    pendingTurn.ErrorCategory = RunErrorTaxonomy.ToolError;
                }
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
            catch (RunInterruptedException interrupted) when (!cancellationToken.IsCancellationRequested)
            {
                // The user cut this round off. It leaves a record the worker reads on its next turn —
                // what it had said so far, what the user sent — and the loop goes round again.
                Interrupts?.TakePending(Guid.Parse(run.RunId));
                checkpoint = await RecordWorkerInterruptionAsync(run, checkpoint, task, interrupted.Request, interrupted.PartialText, [], cancellationToken).ConfigureAwait(false);
                continue;
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
                    var result = BuildWorkerStepResult(task, worker.Id, model.Text);
                    return new ToolTaskExecutionResult(checkpoint, Waiting: false,
                        new TaskExecutionResult(task.TaskId, worker.Id, result.Status, result));
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
        else if (!string.IsNullOrWhiteSpace(task.RequestedAgent))
        {
            // The coordinator named the executor by responsibility: honor it. Inferring a worker
            // from required_tools is only the fallback for tasks that name none — least-privilege
            // matching picks the NARROWEST holder of the listed tools, which routed evidence work
            // to a reviewer and code edits to a doc writer in the 2026-09-27 dispatch sampling.
            (selected, spawnable) = ResolveRequestedWorker(configuration, task);
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
        var lineage = ResolveDispatcherLineage(instances, checkpoint.Tasks, task);
        EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, configuration.Spawn.MaxDepth, task.TaskKey);
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug))
        {
            task.WorkerAgentSlug = selected.Agent.Id;
            task.WorkerAgentDefinitionId = selected.Agent.AgentDefinitionId;
            task.WorkerAgentVersionId = selected.Agent.AgentVersionId;
            task.WorkerAgentVersionHash = selected.Agent.VersionContentHash;
            task.WorkerAssignmentReason = selected.Reason;
            task.WorkerHandle = NextWorkerHandle(checkpoint.Tasks, selected.Agent.Id);
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-selected", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "worker.assigned", $"Task assigned to {task.WorkerHandle}.", new
            {
                task_id = task.TaskId,
                task_key = task.TaskKey,
                handle = task.WorkerHandle,
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
                VersionContentHash: selected.Agent.VersionContentHash,
                AllowedDispatchTargets: spawnable?.AllowedDispatchTargets ?? selected.Agent.AllowedDispatchTargets,
                // Lineage so the created worker carries a real depth instead of starting the
                // count over: the ceiling above was checked against the dispatcher's depth, and
                // the same value must land on the row or a later sub-task would see depth 0.
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
    internal static void EnsureGraphWorkerBudget(IReadOnlyList<RuntimeAgentInstance> instances, int maxAgentsPerRun, string taskKey)
    {
        if (instances.Count >= maxAgentsPerRun)
            throw new WorkerAssignmentException(
                $"Graph-tier worker budget exhausted for task '{taskKey}': the run already carries {instances.Count} instances (ceiling {maxAgentsPerRun}).");
    }

    /// <summary>
    /// Resolves the lineage for the worker about to be created for <paramref name="task"/>:
    /// its parent instance and the depth it will carry. A root task (no
    /// <c>DispatchedByTaskId</c>) is depth 0; a <c>task_dispatch</c> sub-task sits one level
    /// below the worker that dispatched it. <c>Depth</c> is null — never 0 — when the
    /// dispatcher cannot be resolved, and callers distinguish "root" from "unresolvable" by
    /// <c>Dispatched</c>.
    /// </summary>
    /// <remarks>
    /// The dispatcher's worker row is expected to exist because <c>WorkersAssigned</c>
    /// durably assigns a whole ready batch before any task in it executes, so a sub-task
    /// cannot become ready before its dispatcher's worker was persisted. The one case that
    /// does miss is a lane directive dispatching across runs: the dispatcher's worker lives
    /// under a different run's instance set, so the lookup here finds nothing and we fail
    /// closed. Lanes are unreachable today (<c>RunFreezeGate</c> rejects
    /// <c>lanes_enabled</c>, <c>graph_tier_lanes_unsupported</c>), so this is latent rather
    /// than live — but it must stay a refusal, never a silent depth-0 restart.
    /// </remarks>
    internal static (bool Dispatched, Guid? InstanceId, int? Depth) ResolveDispatcherLineage(
        IReadOnlyList<RuntimeAgentInstance> instances,
        IReadOnlyList<DurableTaskNode> tasks,
        DurableTaskNode task)
    {
        if (task.DispatchedByTaskId is not { } dispatchedBy) return (false, null, 0);
        var dispatcher = tasks.FirstOrDefault(item => item.TaskId == dispatchedBy);
        if (dispatcher?.WorkerAgentId is not { } dispatcherInstanceId) return (true, null, null);
        var dispatcherInstance = instances.FirstOrDefault(item => item.Id == dispatcherInstanceId);
        return (true, dispatcherInstanceId, dispatcherInstance?.GenerationDepth + 1);
    }

    /// <summary>
    /// Depth ceiling for the engine-authored root path. <see cref="EnsureGraphWorkerBudget"/>
    /// above stays run-scoped and must not be folded in here: it counts the run's whole
    /// instance population, and the four non-worker root call sites (conversation author,
    /// supervisor, curator, lane planner) are legitimately exempt from it. Depth is the
    /// per-instance question — "how far below the run root does this worker sit" — and the
    /// engine is the only party that can answer it, because SpawnAsync's own check sits on a
    /// path the engine never takes.
    /// </summary>
    /// <param name="dispatched">
    /// Whether the task came from a <c>task_dispatch</c> sub-task at all.
    /// </param>
    /// <param name="workerDepth">
    /// The depth the worker about to be created will carry, as returned by
    /// <see cref="ResolveDispatcherLineage"/> — already relative to the run root, not
    /// relative to its dispatcher. Null when the dispatcher could not be resolved; a
    /// <c>task_dispatch</c> sub-task is never legitimately a root, so that fails closed
    /// rather than silently restarting the count.
    /// </param>
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
    /// Per-item token prices in the spelling both <c>context.packed</c> mint sites write, so the wire
    /// keys cannot drift apart between the main planner and a lane planner. Content stays out: events
    /// are durable and readable by anything holding the run id, so quoting a dropped paragraph there
    /// would copy workspace text outside the frozen root that was never granted to the reader.
    /// </summary>
    private static object[] BudgetShares(IReadOnlyList<ContextEvidence> items) =>
        [.. items.Select(item => new { source = item.Source, tokens = item.EstimatedTokens })];

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
            Description = template.Description,
            SystemPrompt = template.SystemPrompt ?? string.Empty,
            AllowedDispatchTargets = template.AllowedDispatchTargets,
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
            $"Task: {task.Title}\nDescription: {task.Description}\nSuccess criteria: {string.Join("; ", task.SuccessCriteria)}"
                + (task.WriteScope is { Count: > 0 } scope
                    ? $"\nWrite scope (reserved for you; change files only inside it — changes elsewhere are reported to the reviewer): {string.Join("; ", scope)}"
                    : string.Empty),
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
        // An instance that may call task_dispatch must see who it can name: the tool's `agent`
        // field is validated against this same frozen roster, so the prompt and the check agree.
        var dispatchSection = worker.AllowedTools.Any(CoreVirtualToolPolicy.IsTaskDispatch)
            ? "\n\n" + BuildDispatchRosterSection(DispatchRosterOf(configuration))
            : string.Empty;
        return await new ExecutionAgent(CreateModelFactory(configuration, checkpoint, workerDefinition,
            worker.Id, worker.ParentInstanceId, task: task), _logger).GetNextTurnAsync(
            CreateRunContext(run, checkpoint),
            agent,
            ToPlannedTask(task),
            task.ToolTurns,
            tools,
            // The close-out instruction leads: on that turn the model must hand off,
            // not continue, so it must not be buried under the normal task framing.
            closeoutPrompt is null
                ? assembly.Instructions + dispatchSection + WorkerPatchProtocol
                : closeoutPrompt + "\n\n" + assembly.Instructions + dispatchSection + WorkerPatchProtocol,
            configuration.Context.RecentMessageLimit,
            cancellationToken,
            task.Interruptions).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the executor a coordinator named (<see cref="DurableTaskNode.RequestedAgent"/>):
    /// an enabled roster worker first, then a spawnable template — which still needs the tier's
    /// spawn authority. Edge authority and the agent budget are checked by the caller exactly as
    /// for an inferred worker, so naming a target never widens what the mode permits.
    /// </summary>
    internal static (WorkerSelection Selection, FrozenSpawnableTemplate? Spawnable) ResolveRequestedWorker(
        FrozenRunConfigurationV1 configuration,
        DurableTaskNode task)
    {
        var requested = task.RequestedAgent!.Trim();
        // Last line of the allowed_dispatch_targets gate: planning and task_dispatch already check
        // the frozen roster, but a task restored from an older checkpoint (or replanned) reaches here
        // without passing either.
        if (ConversationDispatchTargets(configuration) is { } allowed
            && !allowed.Contains(requested, StringComparer.OrdinalIgnoreCase))
            throw new WorkerAssignmentException(
                $"Task '{task.TaskKey}' names executor '{requested}', which this mode does not allow its coordinator to dispatch to. {DescribeDispatchChoices(DispatchRosterOf(configuration))}");
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

    /// <summary>
    /// Run-scoped handle for a new assignment: <c>&lt;agent&gt;#&lt;n&gt;</c>, where n counts that
    /// agent's assignments in this run. Deterministic from the checkpoint (a replayed assignment
    /// reproduces it) and readable — no random names for a model or a person to misquote.
    /// </summary>
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
    /// <summary>
    /// The run's dispatch roster: the frozen list when the body carries one, else computed from
    /// the frozen roster (bodies admitted before the list was frozen).
    /// </summary>
    internal static IReadOnlyList<FrozenDispatchTarget> DispatchRosterOf(FrozenRunConfigurationV1 configuration) =>
        configuration.DispatchRoster ?? ComputeDispatchRoster(configuration);

    /// <summary>
    /// Computes the dispatch roster; the coordinator freezes its result at admission. When the
    /// conversation node's relationship file declares <c>allowed_dispatch_targets</c>, the roster is
    /// narrowed to it here — once — so the planner's roster, the solo roster section and the
    /// call-time <c>task_dispatch</c> check all enforce the same declaration.
    /// </summary>
    internal static IReadOnlyList<FrozenDispatchTarget> ComputeDispatchRoster(FrozenRunConfigurationV1 configuration)
    {
        var allowed = ConversationDispatchTargets(configuration);
        return BuildFrozenPlannerRoster(configuration)
            .Where(agent => allowed is null || allowed.Contains(agent.Name, StringComparer.OrdinalIgnoreCase))
            .Select(agent => new FrozenDispatchTarget(agent.Name, agent.Description ?? string.Empty, agent.AllowedTools))
            .ToArray();
    }

    /// <summary>
    /// The conversation node's declared dispatch targets, or null when it declares none (the tier's
    /// roster applies). Declared targets only narrow: a target the tier would refuse stays refused.
    /// </summary>
    internal static IReadOnlyList<string>? ConversationDispatchTargets(FrozenRunConfigurationV1 configuration)
    {
        var slug = configuration.Graph?.ConversationTemplateSlug;
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return configuration.OperationAgents
            .FirstOrDefault(agent => string.Equals(agent.Id, slug, StringComparison.OrdinalIgnoreCase))?
            .AllowedDispatchTargets;
    }

    /// <summary>The roster section for an instance that holds <c>task_dispatch</c>.</summary>
    internal static string BuildDispatchRosterSection(IReadOnlyList<FrozenDispatchTarget> roster)
    {
        if (roster.Count == 0)
            return "## 可派发执行者\n本次运行没有可派发的执行者：task_dispatch 不可用，需要的工作请自己完成或如实告知用户。";
        var lines = roster.Select(target =>
            $"- {target.Id}: {(string.IsNullOrWhiteSpace(target.Description) ? "（未写职责）" : target.Description.Trim())} | 工具: {(target.Tools.Count == 0 ? "无" : string.Join(", ", target.Tools))}");
        return "## 可派发执行者（task_dispatch 的 agent 取值）\n"
            + "按职责选：看谁的职责与边界匹配这项子任务，把它的 id 原样填进 agent。description 要自足——执行者看不到你的对话。\n"
            + string.Join("\n", lines);
    }

    internal static IReadOnlyList<AgentDefinition> BuildFrozenPlannerRoster(FrozenRunConfigurationV1 configuration)
    {
        var graph = configuration.Graph;
        if (graph is null)
            return configuration.ExecutionAgents
                .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
                .OrderBy(agent => agent.RosterOrder)
                .ThenBy(agent => agent.Id, StringComparer.Ordinal)
                .Select(agent => ToPlannerRosterEntry(agent.AgentDefinitionId ?? Guid.Empty, agent.Id, agent.Role, agent.Capabilities, agent.AllowedTools, agent.Description))
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
                roster.Add(ToPlannerRosterEntry(agent.AgentDefinitionId ?? Guid.Empty, agent.Id, agent.Role, agent.Capabilities, agent.AllowedTools, agent.Description));
                continue;
            }
            var template = graph.SpawnableTemplates.FirstOrDefault(candidate =>
                string.Equals(candidate.Slug, slug, StringComparison.OrdinalIgnoreCase));
            if (template is not null)
                roster.Add(ToPlannerRosterEntry(template.AgentDefinitionId, template.Slug, template.Role, template.Capabilities, template.ToolCeiling, template.Description));
        }
        return roster;
    }

    private static AgentDefinition ToPlannerRosterEntry(
        Guid definitionId,
        string slug,
        string role,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> allowedTools,
        string? description) => new()
    {
        Id = definitionId,
        Name = slug,
        Description = description,
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
            string? agent = null;
            string? followUpOf = null;
            Guid? dispatchId = null;
            Guid? dispatchedBy = null;
            string? title = null;
            string? description = null;
            string[] criteria = [];
            string[] tools = [];
            string[] capabilities = [];
            string[] writeScope = [];
            try
            {
                using var document = JsonDocument.Parse(directive.PayloadJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("agent", out var agentElement) && agentElement.ValueKind == JsonValueKind.String)
                        agent = agentElement.GetString()?.Trim();
                    if (root.TryGetProperty("follow_up_of", out var followElement) && followElement.ValueKind == JsonValueKind.String)
                        followUpOf = followElement.GetString()?.Trim();
                    if (root.TryGetProperty("dispatch_id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        && Guid.TryParse(idElement.GetString(), out var parsedId))
                        dispatchId = parsedId;
                    if (root.TryGetProperty("task_id", out var byElement) && byElement.ValueKind == JsonValueKind.String
                        && Guid.TryParse(byElement.GetString(), out var parsedBy))
                        dispatchedBy = parsedBy;
                    if (root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
                        title = titleElement.GetString()?.Trim();
                    if (root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
                        description = descriptionElement.GetString()?.Trim();
                    criteria = ReadStringArray(root, "success_criteria");
                    tools = ReadStringArray(root, "required_tools");
                    capabilities = ReadStringArray(root, "required_capabilities");
                    writeScope = ReadStringArray(root, "write_scope");
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
                var prior = string.IsNullOrWhiteSpace(followUpOf) ? null : FindDispatchedTask(checkpoint.Tasks, dispatchedBy, followUpOf);
                checkpoint.Tasks.Add(new DurableTaskNode
                {
                    // The dispatch id IS the sub-task's id: the write-scope leases taken when
                    // task_dispatch was called are keyed by it, and they are released when this
                    // task reaches a terminal status.
                    TaskId = dispatchId is { } id && checkpoint.Tasks.All(item => item.TaskId != id) ? id : Guid.NewGuid(),
                    TaskKey = key,
                    Title = title,
                    Description = prior is null ? description : FollowUpBrief(prior, description),
                    DispatchId = dispatchId,
                    DispatchedByTaskId = dispatchedBy,
                    WriteScope = NormalizeWriteScope(writeScope),
                    SuccessCriteria = criteria.Length > 0 ? [.. criteria] : ["The sub-task's stated goal is met."],
                    Dependencies = [],
                    RequiredCapabilities = [.. capabilities],
                    RequiredTools = [.. tools],
                    RequestedAgent = string.IsNullOrWhiteSpace(agent) ? null : agent,
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
                        dispatch_id = dispatchId,
                        dispatched_by = checkpoint.Tasks.FirstOrDefault(item => item.TaskId == dispatchedBy)?.WorkerHandle,
                        task_key = key,
                        title,
                        agent,
                        follow_up_of = prior?.WorkerHandle ?? prior?.TaskKey,
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
        Guid? parentInstanceId,
        bool streamAnswer = false,
        bool observeOutput = true,
        DurableTaskNode? task = null,
        bool interruptible = true)
    {
        var resolver = _modelResolver ?? throw new InvalidOperationException("The agent model resolver is not registered.");
        var plan = definition.ModelPlan ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no model plan.");
        var definitionId = definition.AgentDefinitionId ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no definition id.");
        var versionId = definition.AgentVersionId ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no version id.");
        var modeVersionId = configuration.Bindings.Single(binding => binding.ConfigurationKind == "agent_mode_version").ConfigurationVersionId;
        return new ModelInvocationChatFactory(resolver, _chatClients, plan, new ModelInvocationContext(
            checkpoint.SessionId, checkpoint.RunId, checkpoint.TurnId, instanceId, parentInstanceId,
            definitionId, versionId, modeVersionId, plan.StrategySource), observeOutput ? async (frame, ct) =>
            {
                var state = await _lifecycle.GetRunStateAsync(checkpoint.RunId.ToString(), ct).ConfigureAwait(false);
                var epoch = LeaseEpochFor(checkpoint.RunId);
                if (IsTerminal(state.Status) || state.Status == RunStatus.Paused
                    || epoch is null || state.LeaseOwner != epoch.OwnerId || state.RecoveryCount != epoch.RecoveryCount)
                    return;
                // These are provider-produced output projections, never prompts or tool
                // arguments. Lifecycle events and tool dispatch share this journal clock.
                if (frame.Kind != "delta" || frame.Channel == "reasoning")
                    await AppendEventAsync(checkpoint.RunId, $"model.output.{frame.Kind}",
                        "Model output update.", new
                        {
                            run_id = checkpoint.RunId, turn_id = checkpoint.TurnId,
                            response_id = frame.ResponseId, agent_name = definition.Id,
                            // Which instance is thinking: two parallel search#n workers share
                            // agent_name, and only the handle tells their reasoning apart.
                            instance_id = instanceId, task_id = task?.TaskId, handle = task?.WorkerHandle,
                            channel = frame.Channel, delta = frame.Delta
                        }, ct).ConfigureAwait(false);
                // An answer the user cut off is withdrawn like a failed one; the redone step streams anew.
                if (streamAnswer && (frame.Kind is "started" or "failed" or "interrupted" || frame.Channel == "text"))
                    await _lifecycle.AppendRunStreamAsync(checkpoint.RunId.ToString(), new DurableRunStreamAppend(
                        checkpoint.TurnId, frame.Kind == "interrupted" ? "answer.failed" : $"answer.{frame.Kind}", Delta: frame.Delta), ct).ConfigureAwait(false);
            } : null,
            // Only calls whose step the engine redoes may be cut off: the operations that run
            // after an answer (observeOutput: false) are not.
            interruptible ? Interrupts : null);
    }

    /// <summary>
    /// Releases every resource lease the run still holds at its terminal boundary. A task releases its
    /// own leases when it finishes, so this is the safety net: a task killed by a crash, an expired
    /// approval or a cancelled run never reaches that boundary, and its leases would otherwise keep
    /// blocking sibling runs forever.
    /// Failure is swallowed on purpose — a ledger that cannot be reached must not turn a completed run
    /// into a failed one. A missed release is not permanent: the next run that collides with the lease
    /// finds its holder terminal and reclaims it (<see cref="IRunLivenessProbe"/>).
    /// </summary>
    private async Task ReleaseRunLeasesAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (_resourceLeases is null) return;
        try
        {
            await _resourceLeases.ReleaseRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Releasing resource leases for run {RunId} failed.", runId);
        }
    }

    /// <summary>
    /// Resolves a ready task's worker, leases its declared write scope and enrols it in the session's
    /// organization. Null when the task was failed instead (it then carries the reason): a worker that
    /// cannot be created, or a write scope another run holds, fails only its own task.
    /// </summary>
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
        // A declared write scope is leased when the task starts (idempotent for a resumed task and for a
        // sub-task whose scope task_dispatch already leased). Losing it fails only this task, naming the
        // holder: waiting in place would spin the run with nothing to wake it.
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

    /// <summary>
    /// Makes the conversation identity (through its authoring instance) and every standing governance
    /// role members of the session's organization when a run starts planning. Idempotent: the author
    /// instance binds once, standing members are keyed by role.
    /// </summary>
    private async Task EnrolConversationAsync(RunState run, FrozenRunConfigurationV1 configuration, Guid authorId, CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || OrganizationScopeOf(run) is not { } scope) return;
        try
        {
            var conversation = configuration.OperationAgents.FirstOrDefault(agent => agent.DirectUserOutput)
                ?? configuration.OperationAgents.FirstOrDefault();
            await organization.EnrolAsync(new OrganizationMemberEnrolment(scope, OrganizationRoles.Conversation,
                conversation?.Id ?? "conversation", conversation?.Id ?? "conversation", Guid.Parse(run.RunId), authorId,
                AgentDefinitionId: conversation?.AgentDefinitionId, Description: conversation?.Description), cancellationToken).ConfigureAwait(false);
            foreach (var standing in GovernanceSubscribers.Standing(configuration))
            {
                await organization.EnrolAsync(new OrganizationMemberEnrolment(scope, OrganizationRoles.Governance, standing.Id, standing.Id,
                    Guid.Parse(run.RunId), AgentDefinitionId: standing.AgentDefinitionId, Description: standing.Description), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not enrol run {RunId} in its session's organization.", run.RunId);
        }
    }

    /// <summary>
    /// Enrols a task's worker: its own member for an executor (addressed by its handle, joined to its
    /// dispatcher's plan room), or the conversation member when the solo master works the task itself,
    /// so all of the master's instances speak with one voice.
    /// </summary>
    private async Task EnrolWorkerAsync(RunState run, FrozenRunConfigurationV1 configuration, FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task, RuntimeAgentInstance worker, CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || OrganizationScopeOf(run) is not { } scope) return;
        try
        {
            var slug = string.IsNullOrWhiteSpace(task.WorkerAgentSlug) ? worker.Role : task.WorkerAgentSlug!;
            var definition = configuration.OperationAgents.Concat(configuration.ExecutionAgents)
                .FirstOrDefault(agent => string.Equals(agent.Id, slug, StringComparison.OrdinalIgnoreCase));
            var isConversation = string.Equals(worker.Layer, "operation", StringComparison.Ordinal)
                && (definition?.DirectUserOutput ?? true);
            var dispatcher = task.DispatchedByTaskId is { } dispatchedBy
                ? checkpoint.Tasks.FirstOrDefault(item => item.TaskId == dispatchedBy)?.WorkerAgentId
                : checkpoint.PlannerAgentId;
            await organization.EnrolAsync(isConversation
                ? new OrganizationMemberEnrolment(scope, OrganizationRoles.Conversation, slug, slug,
                    Guid.Parse(run.RunId), worker.Id, task.TaskId, AgentDefinitionId: worker.AgentDefinitionId)
                : new OrganizationMemberEnrolment(scope, OrganizationRoles.Executor, slug,
                    string.IsNullOrWhiteSpace(task.WorkerHandle) ? slug : task.WorkerHandle,
                    Guid.Parse(run.RunId), worker.Id, task.TaskId, dispatcher ?? checkpoint.PlannerAgentId,
                    worker.AgentDefinitionId, definition?.Description), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not enrol task {TaskKey} of run {RunId} in its session's organization.", task.TaskKey, run.RunId);
        }
    }

    /// <summary>
    /// Tells the run's standing subscribers of <paramref name="topic"/> through their organization inbox.
    /// They take their own turn later; this run never waits for them.
    /// </summary>
    private async Task RaiseStandingAsync(RunState run, FrozenRunConfigurationV1 configuration, string topic, string summary,
        string? subjectKind, string? subjectId, CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || OrganizationScopeOf(run) is not { } scope) return;
        var subscribers = GovernanceSubscribers.For(configuration, topic);
        if (subscribers.Count == 0) return;
        foreach (var agent in subscribers)
        {
            try
            {
                await organization.NotifyAsync(new OrganizationNotice(scope, Guid.Parse(run.RunId), topic, agent.Id, summary, subjectKind, subjectId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.TryLogWarning(ex, "Could not tell standing member {Agent} about {Topic} in run {RunId}.", agent.Id, topic, run.RunId);
            }
        }
    }

    private async Task SetMemberOfflineAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (Organization is not { } organization) return;
        try
        {
            await organization.SetInstanceOfflineAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not mark instance {InstanceId} offline.", instanceId);
        }
    }

    private async Task SetRunMembersOfflineAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (Organization is not { } organization) return;
        try
        {
            await organization.SetRunOfflineAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not mark the members of run {RunId} offline.", runId);
        }
    }

    /// <summary>
    /// Keeps a lower layer's own words in the session's evidence archive (todo R4), so a summary can
    /// always be traced back to what was said. Best-effort: archiving never changes a run's outcome.
    /// </summary>
    private async Task ArchiveEvidenceAsync(RunState run, Guid? taskId, string kind, string title, string? author, string content,
        string sourceKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content) || OrganizationScopeOf(run) is not { } scope) return;
        if (_services.GetService(typeof(IEvidenceArchive)) is not IEvidenceArchive archive) return;
        try
        {
            await archive.AppendAsync(new EvidenceEntry(scope.TenantId, scope.WorkspaceId, scope.SessionId, Guid.Parse(run.RunId), taskId,
                kind, title, author, content, sourceKey), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not archive {Kind} evidence of run {RunId}.", kind, run.RunId);
        }
    }

    /// <summary>A finished task in full: status, who worked it, the answer, the evidence and every criterion verdict.</summary>
    internal static string TaskEvidenceText(DurableTaskNode task)
    {
        var text = new System.Text.StringBuilder();
        text.Append("Status: ").AppendLine(task.Status);
        text.Append("Task: ").Append(task.TaskKey).Append(" — ").AppendLine(task.Title);
        if (!string.IsNullOrWhiteSpace(task.Description)) text.Append("Brief: ").AppendLine(task.Description);
        var worker = task.WorkerHandle ?? task.WorkerAgentSlug;
        if (!string.IsNullOrWhiteSpace(worker)) text.Append("Worker: ").AppendLine(worker);
        if (task.WriteScope is { Count: > 0 } scope) text.Append("Write scope: ").AppendLine(string.Join(", ", scope));
        text.AppendLine("Result:").AppendLine(string.IsNullOrWhiteSpace(task.ResultSummary) ? "(no result text)" : task.ResultSummary);
        if (task.Evidence.Count > 0)
        {
            text.AppendLine("Evidence:");
            foreach (var item in task.Evidence) text.Append("- ").AppendLine(item);
        }
        if (task.CriteriaVerdicts.Count > 0)
        {
            text.AppendLine("Criteria:");
            foreach (var verdict in task.CriteriaVerdicts)
                text.Append(verdict.Satisfied ? "- ✓ " : "- ✗ ").Append(verdict.Criterion)
                    .AppendLine(string.IsNullOrWhiteSpace(verdict.Evidence) ? string.Empty : " — " + verdict.Evidence);
        }
        return text.ToString();
    }

    private static OrganizationScope? OrganizationScopeOf(RunState run) =>
        Guid.TryParse(run.TenantId, out var tenantId) && Guid.TryParse(run.WorkspaceId, out var workspaceId)
        && Guid.TryParse(run.InitiatedByPrincipalId, out var principalId) && Guid.TryParse(run.SessionId, out var sessionId)
            ? new OrganizationScope(tenantId, workspaceId, principalId, sessionId)
            : null;

    private static string DefaultSummary(OperationalTriggerPoint point, FullDuplexCheckpointV1 checkpoint) => point switch
    {
        OperationalTriggerPoint.TaskGraphCreated => $"The task graph was (re)planned: {checkpoint.Tasks.Count} task(s): {string.Join(", ", checkpoint.Tasks.Take(12).Select(task => task.TaskKey))}.",
        OperationalTriggerPoint.RunFinalized => "The run finished and its answer was delivered.",
        _ => point.ToString()
    };

    private static string Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "(no summary)" : value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// Leases a task's declared write scope, all-or-nothing. Returns null when the scope is held (or
    /// the task declared none), else a correctable sentence naming what is held and by whom; a
    /// partially acquired scope is released so a refused task never keeps half its claims.
    /// </summary>
    private async Task<string?> AcquireWriteScopeAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        Guid runId,
        DurableTaskNode task,
        Guid? workerId,
        CancellationToken cancellationToken)
    {
        if (_resourceLeases is null || task.WriteScope is not { Count: > 0 } scope) return null;
        Guid? sessionId = Guid.TryParse(run.SessionId, out var parsedSession) ? parsedSession : null;
        foreach (var claim in ResourceClaimResolver.ResolveWriteScope(scope, configuration.Workspace?.RootPath))
        {
            var decision = await _resourceLeases.AcquireAsync(new ResourceAcquireRequest(
                claim, sessionId, runId, task.TaskId, workerId,
                $"Declared write scope of task '{task.TaskKey}'.", ResourceLeasePurposes.WriteScope), cancellationToken).ConfigureAwait(false);
            if (decision.Granted) continue;
            await _resourceLeases.ReleaseTaskAsync(runId, task.TaskId, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, ResourceLeaseMessages.ConflictEventType,
                $"Task '{task.TaskKey}' could not start: its write scope overlaps a resource another run holds.",
                ResourceLeaseMessages.ConflictPayload(claim, decision.Conflicts, runId, task.TaskId, ResourceLeaseMessages.DetectedAtWriteScope), cancellationToken).ConfigureAwait(false);
            await RaiseStandingAsync(run, configuration, GovernanceTopics.LeaseConflict,
                $"Task '{task.TaskKey}' could not start (write_scope): {ResourceLeaseMessages.Describe(claim, decision.Conflicts)}",
                "task", task.TaskId.ToString("N"), cancellationToken).ConfigureAwait(false);
            return ResourceLeaseMessages.Describe(claim, decision.Conflicts)
                + " Dispatch it again once that work is done, give it a separate worktree, or narrow its write_scope.";
        }
        return null;
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
        foreach (var verdict in execution.Result.CriterionVerdicts)
        {
            task.CriteriaVerdicts.Add(new CriterionVerdict(
                verdict.Criterion,
                verdict.Satisfied,
                verdict.Evidence,
                $"worker:{execution.WorkerAgentId?.ToString("N") ?? "unknown"}",
                checkpoint.SupervisionRound));
        }
        task.CompletedAt = execution.Status is "completed" or "failed" or "blocked" ? DateTimeOffset.UtcNow : null;
        if (execution.WorkerAgentId is { } workerId) task.WorkerAgentId = workerId;
        if (execution.Status is "completed" or "failed" or "blocked")
        {
            // Free the resources this task claimed. Releasing only at run end would keep a finished
            // task's files locked for the rest of the run, which is the opposite of what the ledger
            // is for: two runs of one session are meant to work in parallel on different files.
            if (_resourceLeases is not null)
            {
                await _resourceLeases.ReleaseTaskAsync(runId, task.TaskId, cancellationToken).ConfigureAwait(false);
            }
            if (task.WorkerAgentId is { } finishedWorker) await SetMemberOfflineAsync(finishedWorker, cancellationToken).ConfigureAwait(false);
            // The result is kept verbatim, per attempt: the meeting and the summaries compress it, the archive never does.
            await ArchiveEvidenceAsync(run, task.TaskId, EvidenceKinds.TaskResult, task.Title, task.WorkerHandle ?? task.WorkerAgentSlug,
                TaskEvidenceText(task), $"task:{task.TaskId:N}:{task.Attempt}", cancellationToken).ConfigureAwait(false);
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
            criteria_verdicts = task.CriteriaVerdicts,
            updated_at = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
        var eventType = execution.Status switch
        {
            "completed" => "worker.completed",
            "blocked" => "worker.blocked",
            _ => "worker.failed"
        };
        await AppendEventAsync(runId, eventType, execution.Result.Summary, new
        {
            task_id = task.TaskId,
            task_key = task.TaskKey,
            agent_instance_id = execution.WorkerAgentId,
            status = execution.Result.Status,
            summary = execution.Result.Summary,
            evidence = execution.Result.Evidence,
            criteria_verdicts = execution.Result.CriterionVerdicts
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
            await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.TaskCompleted, run, configuration, checkpoint, cancellationToken,
                $"Task '{task.TaskKey}' ({task.WorkerHandle ?? task.WorkerAgentSlug}) completed: {Clip(task.ResultSummary, 240)}", "task", task.TaskId.ToString("N")).ConfigureAwait(false);
        }
        else if (execution.Status is "failed" or "blocked")
        {
            // The built-in behaviours only ever answered completed tasks; a standing member subscribed
            // to task_closed is told about every terminal task, because a failure is what it watches for.
            await RaiseStandingAsync(run, configuration, GovernanceTopics.TaskClosed,
                $"Task '{task.TaskKey}' ({task.WorkerHandle ?? task.WorkerAgentSlug}) ended {execution.Status}: {Clip(task.ResultSummary, 240)}",
                "task", task.TaskId.ToString("N"), cancellationToken).ConfigureAwait(false);
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

    /// <summary>Dispatch status of a call a hard insert kept from starting (todo D2).</summary>
    internal const string NotRunAfterInterrupt = "not_run";

    /// <summary>Interruptions kept per task; the newest are the ones the worker must act on.</summary>
    private const int MaxTaskInterruptions = 5;

    /// <summary>
    /// A hard insert arrived while this task's tool calls were in progress: every call the model
    /// asked for that has not started gets a "not run" result (the transcript needs one per call, and
    /// the worker must know it did not happen). Not an error — the loop guard does not count it.
    /// When nothing was left to skip, nothing was cut off: the next turn simply reads the steering.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> SkipUnstartedToolCallsAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RunInterruptRequest steering,
        CancellationToken cancellationToken)
    {
        var unstarted = task.ToolTurns
            .Where(turn => string.IsNullOrWhiteSpace(turn.ResultJson) && string.IsNullOrWhiteSpace(turn.ExecutionId))
            .ToList();
        if (unstarted.Count == 0) return checkpoint;
        foreach (var turn in unstarted)
        {
            turn.DispatchStatus = NotRunAfterInterrupt;
            turn.ResultJson = JsonSerializer.Serialize(new
            {
                tool_id = turn.ToolId,
                status = NotRunAfterInterrupt,
                reason = "interrupted",
                message = "Not run: the user sent new instructions before this call started. Read them and decide again whether it is still needed."
            });
        }
        return await RecordWorkerInterruptionAsync(run, checkpoint, task, steering, null,
            unstarted.Select(turn => turn.ToolId).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the record of a round the user cut off on the task (shown to the worker where it
    /// happened, see <see cref="WorkerInterruption"/>) and says so on the run's journal.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> RecordWorkerInterruptionAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RunInterruptRequest steering,
        string? partialText,
        IReadOnlyList<string> skippedTools,
        CancellationToken cancellationToken)
    {
        var partial = string.IsNullOrWhiteSpace(partialText) ? null : Clip(partialText.Trim(), 2000);
        var interruptions = task.Interruptions ??= [];
        interruptions.Add(new WorkerInterruption(task.ToolTurns.Count, Clip(steering.Content.Trim(), 2000), partial, skippedTools.Count, DateTimeOffset.UtcNow));
        if (interruptions.Count > MaxTaskInterruptions) interruptions.RemoveRange(0, interruptions.Count - MaxTaskInterruptions);
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-interrupted", cancellationToken).ConfigureAwait(false);
        var runId = Guid.Parse(run.RunId);
        await AppendEventAsync(runId, "worker.interrupted",
            $"The user interrupted {task.WorkerHandle ?? task.WorkerAgentSlug ?? task.TaskKey}; it continues with the new instructions.",
            new
            {
                run_id = runId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                handle = task.WorkerHandle,
                lane_key = LaneKeyOf(task),
                patch_id = steering.PatchId,
                during = skippedTools.Count > 0 ? "tool_calls" : "model_call",
                skipped_tools = skippedTools,
                partial_characters = partial?.Length ?? 0
            }, cancellationToken, task.TaskId,
            idempotencyKey: $"worker-interrupted:{task.TaskId}:{steering.PatchId:N}:{task.ToolTurns.Count}").ConfigureAwait(false);
        return checkpoint;
    }

    /// <summary>Sibling runs read for the pool; the active-run limit keeps the real number small.</summary>
    private const int MaxPoolSiblings = 16;

    /// <summary>
    /// Writes this run's place in the session's conversation pool (todo D3), once: its conversation
    /// identity instance is the run's main instance, and the pool is that instance plus the identity
    /// instances of the session's other unfinished runs at the time — the parallel instances of the
    /// same identity this one shares the session context with. The pool is what they were at planning;
    /// the live picture is the session topology.
    /// </summary>
    private async Task JoinConversationPoolAsync(RunState run, FullDuplexCheckpointV1 checkpoint, Guid identityInstanceId, CancellationToken cancellationToken)
    {
        if (checkpoint.ConversationIdentityInstanceId is not null) return;
        var pool = new List<Guid> { identityInstanceId };
        try
        {
            var siblings = await _lifecycle.ListActiveRunsAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
            foreach (var sibling in siblings.Where(item => item.RunId != run.RunId).Take(MaxPoolSiblings))
            {
                var stored = await _lifecycle.GetCurrentRunCheckpointAsync(sibling.RunId, cancellationToken).ConfigureAwait(false);
                if (stored is null) continue;
                var other = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(stored.Content, JsonOptions);
                if (other?.ConversationIdentityInstanceId is { } instance && !pool.Contains(instance)) pool.Add(instance);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The pool is a record of who ran alongside; failing to read a sibling never stops the run.
            _logger.TryLogDebug(ex, "Could not read the sibling runs of {RunId} for its conversation pool.", run.RunId);
        }
        checkpoint.ConversationIdentityInstanceId = identityInstanceId;
        checkpoint.MainInstanceId = identityInstanceId;
        checkpoint.InstancePoolIds = pool;
        if (pool.Count > 1)
            await AppendEventAsync(checkpoint.RunId, "conversation.pool_joined",
                "This run's conversation identity runs alongside other instances of it in the session and shares their context.",
                new { run_id = checkpoint.RunId, instance_id = identityInstanceId, pool = pool }, cancellationToken,
                idempotencyKey: $"run:{checkpoint.RunId}:pool").ConfigureAwait(false);
    }

    /// <summary>The run-level record of a step a hard insert cut off and the engine is redoing.</summary>
    private async Task AppendRunInterruptedAsync(Guid runId, RunInterruptedException interrupted, CancellationToken cancellationToken)
    {
        try
        {
            await AppendEventAsync(runId, "run.interrupted",
                "The user interrupted the run; the step in progress is redone with the new instructions.",
                new
                {
                    run_id = runId,
                    patch_id = interrupted.Request.PatchId,
                    redone = "step",
                    partial_characters = interrupted.PartialText.Length
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not append the interruption event for run {RunId}.", runId);
        }
    }

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
    /// Ends a task that a budget, fuse, or loop guard stopped. The hand-off text is
    /// still preserved, but an early stop is never silently promoted to completion:
    /// without an explicit completed outcome it remains blocked, and a hard ceiling
    /// remains blocked even when the model claims success because the loop itself was
    /// abnormal and requires review.
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
        var outcome = string.IsNullOrWhiteSpace(text)
            ? new WorkerOutcomeProtocol.Parsed(
                "blocked",
                $"Stopped before completion ({category}): {reason}",
                Criteria: [],
                Explicit: false)
            : WorkerOutcomeProtocol.Parse(text, requireExplicit: true);
        var evaluation = EvaluateWorkerOutcome(task, outcome);
        var status = task.CloseoutHardCeiling && evaluation.Status == "completed"
            ? "blocked"
            : evaluation.Status;
        var summary = outcome.Summary;
        if (evaluation.MissingCriteria.Count > 0)
        {
            summary += "\nMissing criterion evidence: " + string.Join("; ", evaluation.MissingCriteria);
        }
        if (outcome.Status == "completed" && evaluation.UnresolvedToolFailure)
        {
            summary += "\nCompletion rejected: the latest explicit tool outcome reports success=false and no later successful tool outcome resolved it.";
        }
        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary)) evidence.Add(summary);
        evidence.Add(DispatchEvidence(task));
        evidence.Add($"worker_outcome:{status}:{OutcomeBasis(outcome)}");
        evidence.AddRange(outcome.Criteria.Select(item =>
            $"criterion_evidence:{item.Criterion}||{item.Evidence}"));
        evidence.AddRange(evaluation.MissingCriteria.Select(item =>
            $"missing_criterion_evidence:{item}"));
        if (evaluation.UnresolvedToolFailure) evidence.Add("tool_outcome_fact:unresolved_failure");
        evidence.Add($"closeout:{category}");
        evidence.Add($"closeout_reason:{reason}");
        if (task.CloseoutHardCeiling) evidence.Add("hard_ceiling:true");
        var result = new StepResult
        {
            TaskNodeId = task.TaskId,
            AgentId = worker.Id.ToString("N"),
            Status = status,
            Summary = summary,
            Evidence = evidence,
            CriterionVerdicts = evaluation.Verdicts
        };
        return new ToolTaskExecutionResult(checkpoint, Waiting: false,
            new TaskExecutionResult(task.TaskId, worker.Id, status, result, model.Usage));
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
            // A call a hard insert kept from running was not a repeat of anything.
            if (string.IsNullOrWhiteSpace(turn.ResultJson) || turn.DispatchStatus == NotRunAfterInterrupt) continue;
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
        var supervisionSkipped = false;
        if (configuration.Supervision.RequiredBeforeFinal)
        {
            // Modes without a supervisor in their frozen roster (e.g.
            // conversation.ask/vibe) skip the review gate instead of failing:
            // the roster is the authority for which roles take part in a run.
            var supervisorDefinition = configuration.OperationAgents
                .SingleOrDefault(item => string.Equals(item.Id, "supervisor", StringComparison.Ordinal));
            if (supervisorDefinition is null || !supervisorDefinition.Enabled)
            {
                supervisionSkipped = true;
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
            supervisionSkipped = true;
            verdict = new SupervisionVerdict(SupervisionDecision.Pass, [], []);
            await AppendEventAsync(runId, "supervision.skipped", "Supervision skipped: the frozen mode does not require a supervisor.", new
            {
                run_id = run.RunId,
                revision_round = checkpoint.SupervisionRound,
                reason = "not_required"
            }, cancellationToken).ConfigureAwait(false);
        }
        MapCriterionVerdicts(checkpoint, verdict);
        verdict = EnforceTaskOutcomeFacts(
            checkpoint.Tasks,
            verdict,
            checkpoint.SupervisionRound,
            configuration.Supervision.MaxRevisionRounds);
        verdict = EnforceCriterionFacts(
            checkpoint.Tasks,
            verdict,
            checkpoint.SupervisionRound,
            configuration.Supervision.MaxRevisionRounds,
            requireSupervisorEvidence: !supervisionSkipped);
        verdict = EnforceRevisionBudget(
            verdict,
            checkpoint.SupervisionRound,
            configuration.Supervision.MaxRevisionRounds);
        checkpoint.SupervisionDecision = supervisionSkipped && verdict.Decision == SupervisionDecision.Pass
            ? "skipped"
            : verdict.DecictionString();
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
            await PublishSupervisionReviewAsync(run, checkpoint, cancellationToken).ConfigureAwait(false);
            await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review before this run can finish.", cancellationToken).ConfigureAwait(false);
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
    /// Code-owned task outcomes outrank a model verdict, and modes without a supervisor
    /// cannot turn the absence of review into a fabricated success: every task that did
    /// not complete is reported in the verdict reasons. A task that never reached a
    /// verdict (blocked/pending/running) additionally drives the run — replan while the
    /// revision budget remains, then let the user decide. A task the execution loop
    /// already closed as failed is not replanned: it has an answer, and reopening it
    /// only reproduces the same failure. When the graph holds no completed task at all,
    /// that closed failure is instead escalated to the user, because passing it would
    /// report success where there is none.
    /// </summary>
    internal static SupervisionVerdict EnforceTaskOutcomeFacts(
        IReadOnlyList<DurableTaskNode> tasks,
        SupervisionVerdict verdict,
        int revisionRound,
        int maxRevisionRounds)
    {
        var incomplete = tasks
            .Select((task, index) => (Task: task, Index: index))
            .Where(item => !string.Equals(item.Task.Status, "completed", StringComparison.Ordinal))
            .ToArray();
        if (incomplete.Length == 0) return verdict;

        var reasons = verdict.Reasons.ToList();
        foreach (var item in incomplete)
        {
            var summary = string.IsNullOrWhiteSpace(item.Task.ResultSummary)
                ? "No completion evidence was recorded."
                : TruncateEvidence(item.Task.ResultSummary);
            var reason = $"task_outcome:{item.Task.TaskKey}:{item.Task.Status} — {summary}";
            if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
        }

        // A closed failure has an answer: replanning it re-dispatches the same demand
        // and, for a frozen-manifest defect such as `worker_assignment_invalid`, fails
        // it again on every round until the budget parks the run.
        var unresolved = incomplete
            .Where(item => !string.Equals(item.Task.Status, "failed", StringComparison.Ordinal))
            .ToArray();
        if (unresolved.Length == 0)
        {
            // Nothing is left to replan. Partial success is a result the user can read:
            // the reasons above and the per-task evidence the meeting prompt is built
            // from both carry the failure, so the answer reports what was not achieved.
            // But a graph with no completed task at all must not wash pure failure into a
            // pass — that is the case code-owned facts exist for, and the user decides.
            if (tasks.Any(item => string.Equals(item.Status, "completed", StringComparison.Ordinal)))
                return verdict with { Reasons = reasons };

            reasons.Add("Every task failed and no completed work remains; the outcome requires user review.");
            return verdict with
            {
                Decision = SupervisionDecision.Escalate,
                Reasons = reasons,
                ReviseTaskIndexes = []
            };
        }

        if (verdict.Decision == SupervisionDecision.Escalate)
        {
            return verdict with { Reasons = reasons };
        }

        if (revisionRound < maxRevisionRounds)
        {
            var reviseIndexes = verdict.ReviseTaskIndexes
                .Concat(unresolved.Select(item => item.Index))
                .Where(index => index >= 0 && index < tasks.Count)
                .Distinct()
                .Order()
                .ToArray();
            return verdict with
            {
                Decision = SupervisionDecision.Revise,
                Reasons = reasons,
                ReviseTaskIndexes = reviseIndexes
            };
        }

        reasons.Add("Automatic revision budget is exhausted; incomplete task outcomes require user review.");
        return verdict with
        {
            Decision = SupervisionDecision.Escalate,
            Reasons = reasons,
            ReviseTaskIndexes = []
        };
    }

    /// <summary>
    /// A completed status is only a claim. Every declared success criterion must
    /// have a satisfied verdict with non-empty evidence. Modes with a supervisor
    /// require a verdict from the current supervisor round; modes that intentionally
    /// skip supervision rely on the worker's structured criterion evidence.
    /// </summary>
    internal static SupervisionVerdict EnforceCriterionFacts(
        IReadOnlyList<DurableTaskNode> tasks,
        SupervisionVerdict verdict,
        int revisionRound,
        int maxRevisionRounds,
        bool requireSupervisorEvidence)
    {
        var issues = new List<(int Index, string Reason)>();
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            if (!string.Equals(task.Status, "completed", StringComparison.Ordinal)) continue;
            foreach (var criterion in task.SuccessCriteria)
            {
                var candidates = task.CriteriaVerdicts
                    .Where(item => string.Equals(item.Criterion, criterion, StringComparison.Ordinal));
                candidates = requireSupervisorEvidence
                    ? candidates.Where(item => string.Equals(item.ReviewedBy, "supervisor", StringComparison.Ordinal)
                        && item.Round == revisionRound)
                    : candidates.Where(item => item.ReviewedBy?.StartsWith("worker:", StringComparison.Ordinal) == true);
                var latest = candidates.OrderBy(item => item.Round).LastOrDefault();
                if (latest is { Satisfied: true } && !string.IsNullOrWhiteSpace(latest.Evidence)) continue;
                var source = requireSupervisorEvidence ? "supervisor" : "worker";
                var detail = latest is null
                    ? $"no {source} verdict"
                    : latest.Satisfied ? "satisfied verdict has no evidence" : "criterion was not satisfied";
                issues.Add((index, $"criterion:{task.TaskKey}:{criterion} — {detail}"));
            }
        }
        if (issues.Count == 0) return verdict;

        var reasons = verdict.Reasons.ToList();
        foreach (var issue in issues)
        {
            if (!reasons.Contains(issue.Reason, StringComparer.Ordinal)) reasons.Add(issue.Reason);
        }
        if (verdict.Decision == SupervisionDecision.Escalate)
        {
            return verdict with { Reasons = reasons };
        }
        if (revisionRound < maxRevisionRounds)
        {
            return verdict with
            {
                Decision = SupervisionDecision.Revise,
                Reasons = reasons,
                ReviseTaskIndexes = verdict.ReviseTaskIndexes
                    .Concat(issues.Select(issue => issue.Index))
                    .Where(index => index >= 0 && index < tasks.Count)
                    .Distinct()
                    .OrderBy(index => index)
                    .ToArray()
            };
        }
        reasons.Add("Automatic revision budget is exhausted; unresolved success criteria require user review.");
        return verdict with
        {
            Decision = SupervisionDecision.Escalate,
            Reasons = reasons,
            ReviseTaskIndexes = []
        };
    }

    internal static SupervisionVerdict EnforceRevisionBudget(
        SupervisionVerdict verdict,
        int revisionRound,
        int maxRevisionRounds)
    {
        if (verdict.Decision != SupervisionDecision.Revise || revisionRound < maxRevisionRounds)
        {
            return verdict;
        }
        var reasons = verdict.Reasons.ToList();
        const string reason = "Supervision still requires revision, but the automatic revision budget is exhausted; user review is required.";
        if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
        return verdict with
        {
            Decision = SupervisionDecision.Escalate,
            Reasons = reasons,
            ReviseTaskIndexes = []
        };
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
                    ValidateAndMaterializeGraph(planned, configuration.Spawn.MaxAgentsPerRun, DispatchRosterOf(configuration)));
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
        if (!resolution.IsAvailable)
        {
            throw new MeetingResponseUnavailableException(
                RunErrorTaxonomy.ModelUnavailable,
                resolution.Error ?? "Chat route is unavailable.");
        }
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
        // A TinaChat handoff is an immutable accepted brief. Legacy session
        // patches are retained as evidence but cannot replace its input goal.
        // New requirements must be proposed and accepted as another intent.
        if (_services.GetService(typeof(ITinaChatRunInput)) is ITinaChatRunInput chatInputs
            && await chatInputs.GetForSessionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false) is not null)
            return false;
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
                checkpoint.UserAcceptedIncompleteOutcome = false;
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

    // The decision evidence must be durable before the externally visible waiting status.
    // Recovery uses the same key if the host stopped between checkpoint/event/status writes.
    private Task PublishSupervisionReviewAsync(RunState run, FullDuplexCheckpointV1 checkpoint, CancellationToken ct) =>
        AppendEventAsync(Guid.Parse(run.RunId), "supervision.user_review.requested",
            "Supervision escalated the run and is waiting for a user decision.", new
            {
                run_id = run.RunId,
                decision = "escalate",
                reasons = checkpoint.SupervisionReasons,
                options = new[] { "continue", "correct", "cancel" }
            }, ct, idempotencyKey: $"run:{run.RunId}:supervision-review:{checkpoint.PlanRevision}:{checkpoint.SupervisionRound}");

    private async Task FinalizeAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);

        // Defensive finalization gate for legacy or partially committed checkpoints.
        // Unless the user explicitly accepted an incomplete outcome, finalization
        // requires a resolved pass/skipped decision plus completed tasks and evidence
        // for every success criterion. A corrupted phase cannot manufacture success.
        if (!checkpoint.UserAcceptedIncompleteOutcome)
        {
            var supervisorRequired = configuration.Supervision.RequiredBeforeFinal
                && configuration.OperationAgents.Any(agent =>
                    agent.Enabled && string.Equals(agent.Id, "supervisor", StringComparison.Ordinal));
            var facts = new SupervisionVerdict(SupervisionDecision.Pass, [], []);
            facts = EnforceTaskOutcomeFacts(
                checkpoint.Tasks,
                facts,
                checkpoint.SupervisionRound,
                checkpoint.SupervisionRound);
            facts = EnforceCriterionFacts(
                checkpoint.Tasks,
                facts,
                checkpoint.SupervisionRound,
                checkpoint.SupervisionRound,
                requireSupervisorEvidence: supervisorRequired);
            var unresolvedDecision = checkpoint.SupervisionDecision is not ("pass" or "skipped");
            if (unresolvedDecision || facts.Decision != SupervisionDecision.Pass)
            {
                checkpoint.SupervisionDecision = "escalate";
                checkpoint.SupervisionReasons = checkpoint.SupervisionReasons
                    .Concat(facts.Reasons)
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (unresolvedDecision)
                {
                    checkpoint.SupervisionReasons.Add("The persisted supervision decision is unresolved; user review is required.");
                }
                checkpoint.Phase = "awaiting_user";
                checkpoint.MeetingResponse = null;
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "finalization-facts-rejected", cancellationToken).ConfigureAwait(false);
                if (run.Status != RunStatus.AwaitingUser)
                {
                    await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Completion facts require user review before this run can finish.", cancellationToken).ConfigureAwait(false);
                }
                return;
            }
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
                await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            await PersistRunEvidenceAsync(runId, run, checkpoint, cancellationToken).ConfigureAwait(false);
            var meetingDefinition = RequiredConversationAgent(configuration);
            var context = await BuildContextAsync(run, configuration, meetingDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
            try
            {
                checkpoint.MeetingResponse = await GenerateMeetingResponseAsync(configuration, checkpoint, meetingDefinition, context, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelInvocationExhaustedException modelFailure) when (CanUseMeetingEvidenceFallback(checkpoint, modelFailure))
            {
                checkpoint.MeetingResponse = BuildMeetingEvidenceFallback(checkpoint, modelFailure.Category);
                await AppendEventAsync(runId, "meeting.response_fallback",
                    "The final meeting model was unavailable; persisted execution evidence was returned directly.",
                    new
                    {
                        run_id = runId,
                        error_category = modelFailure.Category,
                        retry_count = checkpoint.TransientModelRetryCount,
                        task_count = checkpoint.Tasks.Count
                    }, cancellationToken,
                    idempotencyKey: $"run:{runId}:meeting-response-fallback").ConfigureAwait(false);
            }
            catch (MeetingResponseUnavailableException meetingFailure) when (CanUseMeetingEvidenceFallback(checkpoint))
            {
                checkpoint.MeetingResponse = BuildMeetingEvidenceFallback(checkpoint, meetingFailure.Category);
                await AppendEventAsync(runId, "meeting.response_fallback",
                    "The final meeting response was unavailable; persisted execution evidence was returned directly.",
                    new
                    {
                        run_id = runId,
                        error_category = meetingFailure.Category,
                        retry_count = checkpoint.TransientModelRetryCount,
                        task_count = checkpoint.Tasks.Count
                    }, cancellationToken,
                    idempotencyKey: $"run:{runId}:meeting-response-fallback").ConfigureAwait(false);
            }
            var stateAfterMeeting = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            if (stateAfterMeeting.Status == RunStatus.Cancelled)
            {
                checkpoint.MeetingResponse = null;
                await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (IsTerminal(stateAfterMeeting.Status)
                || RecoveryPolicy.IsAwaitingDecision(stateAfterMeeting.Status)
                || stateAfterMeeting.Status == RunStatus.Paused)
            {
                checkpoint.MeetingResponse = null;
                return;
            }
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "meeting-response", cancellationToken).ConfigureAwait(false);
        }

        var meetingResponse = checkpoint.MeetingResponse ?? throw new InvalidDataException("Meeting response is missing from finalization checkpoint.");

        // Claim successful completion before publishing any success-only artifact.
        // The claim is the run's CompletedAt written while status remains active:
        // cancel/fail can no longer win, but a host crash still leaves the run
        // lease-eligible so recovery can finish the turn/stream from this checkpoint.
        var completionSource = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        if (completionSource.Status == RunStatus.Cancelled)
        {
            await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (IsTerminal(completionSource.Status)
            || RecoveryPolicy.IsAwaitingDecision(completionSource.Status)
            || completionSource.Status == RunStatus.Paused)
        {
            return;
        }
        var expectedCompletionStatus = RunStatusStorageValue(completionSource.Status);
        if (expectedCompletionStatus is not ("executing" or "reviewing")) return;
        var claimedCompletion = await _lifecycle.TryClaimRunCompletionAsync(
            run.RunId,
            expectedCompletionStatus,
            LeaseEpochFor(runId)?.OwnerId,
            LeaseEpochFor(runId)?.RecoveryCount,
            cancellationToken).ConfigureAwait(false);
        var claimedState = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        if (!claimedCompletion && claimedState.CompletedAt is null)
        {
            if (claimedState.Status == RunStatus.Cancelled)
            {
                await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        if (claimedState.Status is RunStatus.Cancelled or RunStatus.Failed)
        {
            if (claimedState.Status == RunStatus.Cancelled)
            {
                await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

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
        await ReleaseRunLeasesAsync(runId, cancellationToken).ConfigureAwait(false);
        await SetRunMembersOfflineAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinalizeCancellationCoreAsync(
        Guid runId,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        await FinalizeCancelledRunAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistCancellationTerminalAsync(
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var revision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        await _conversations.CompleteTurnAsync(checkpoint.TurnId, runId, null, revision, "cancelled", cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            checkpoint.TurnId,
            "done",
            FinishReason: "cancelled",
            IdempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:cancelled"), cancellationToken).ConfigureAwait(false);
        try
        {
            await AppendEventAsync(runId, "task.cancelled", "The run was cancelled.", new { run_id = runId }, cancellationToken,
                idempotencyKey: $"run:{runId}:event:task.cancelled").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not append the cancellation audit event for run {RunId}.", runId);
        }
    }

    private async Task PersistFailureTerminalAsync(
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        string category,
        string message,
        CancellationToken cancellationToken)
    {
        var revision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        await _conversations.CompleteTurnAsync(checkpoint.TurnId, runId, null, revision, "failed", cancellationToken).ConfigureAwait(false);
        await _lifecycle.AppendRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            checkpoint.TurnId,
            "error",
            ErrorCategory: category,
            SafeErrorMessage: message,
            IdempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:error:{category}"), cancellationToken).ConfigureAwait(false);
        try
        {
            await AppendEventAsync(runId, "run.failed", message, new { run_id = runId, error_category = category }, cancellationToken,
                idempotencyKey: $"run:{runId}:event:run.failed").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not append the failure audit event for run {RunId}.", runId);
        }
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
        if (IsTerminal(run.Status)
            || RecoveryPolicy.IsAwaitingDecision(run.Status)
            || run.Status == RunStatus.Paused)
        {
            return;
        }
        try
        {
            await SetRunFailedFencedAsync(runId.ToString(), message, category, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A user cancellation/pause or another owner can win between the fresh
            // read above and the status transition. Re-read before deciding whether
            // this is a genuine transition bug; protected state always wins and
            // must not receive a failed turn/event/stream afterwards.
            run = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (IsTerminal(run.Status)
                || RecoveryPolicy.IsAwaitingDecision(run.Status)
                || run.Status == RunStatus.Paused)
            {
                return;
            }
            throw;
        }
        await FinalizeFailedRunAsync(runId, category, message, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FailLegacyRunAsync(Guid runId, RunState run, string category, string message, CancellationToken cancellationToken)
    {
        run = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        if (IsTerminal(run.Status)
            || RecoveryPolicy.IsAwaitingDecision(run.Status)
            || run.Status == RunStatus.Paused)
        {
            return;
        }
        try
        {
            await SetRunFailedFencedAsync(runId.ToString(), message, category, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            run = await _lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (IsTerminal(run.Status)
                || RecoveryPolicy.IsAwaitingDecision(run.Status)
                || run.Status == RunStatus.Paused)
            {
                return;
            }
            throw;
        }
        await FinalizeFailedRunAsync(runId, category, message, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<FullDuplexCheckpointV1> SaveCheckpointAsync(
        FullDuplexCheckpointV1 checkpoint,
        long expectedRevision,
        string key,
        CancellationToken cancellationToken,
        string laneKey = "main",
        string? expectedRunStatus = null,
        bool requireCompletionUnclaimed = false)
    {
        if (_deferParallelTaskCheckpointWrites.Value)
            return checkpoint;
        // The lane segment keeps same-purpose saves from distinct lanes from
        // colliding on one idempotency row, which would return the first body
        // and silently drop the second lane's update.
        var stored = await _lifecycle.SaveRunCheckpointAsync(checkpoint.RunId.ToString(), new RunCheckpointWrite(
            expectedRevision,
            checkpoint.Phase,
            JsonSerializer.Serialize(checkpoint, JsonOptions),
            IdempotencyKey: $"run:{checkpoint.RunId}:{laneKey}:{key}:{expectedRevision + 1}",
            ExpectedRunStatus: expectedRunStatus,
            RequireCompletionUnclaimed: requireCompletionUnclaimed,
            ExpectedLeaseOwner: LeaseEpochFor(checkpoint.RunId)?.OwnerId,
            ExpectedRecoveryCount: LeaseEpochFor(checkpoint.RunId)?.RecoveryCount), cancellationToken).ConfigureAwait(false);
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
            AllowedDispatchTargets: conversationDefinition.AllowedDispatchTargets,
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
            VersionContentHash: definition.VersionContentHash,
            AllowedDispatchTargets: definition.AllowedDispatchTargets), cancellationToken).ConfigureAwait(false);
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

    private async Task<ContextPack> BuildContextAsync(RunState run, FrozenRunConfigurationV1 config, string agentId, string taskContext, CancellationToken cancellationToken)
    {
        var pack = await _contextProvider.BuildContextAsync(new ContextBuildRequest(
            run.SessionId,
            run.RunId,
            config.RuntimeProfileId,
            agentId,
            taskContext,
            config.Context.DefaultTokenBudget,
            config.Context.RecentMessageLimit,
            config.Memory.RetrievalLimit,
            config.TinaChatInput)
        {
            // Same rule as the prompt assembly: the root comes from the freeze, so the context
            // builder reads the project's own instructions from the directory this run was granted.
            Workspace = config.Workspace,
        }, cancellationToken).ConfigureAwait(false);
        return await WithOpenReportsAsync(run, config, agentId, pack, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the conversation identity the open warning and blocking reports of its session's
    /// organization. In the coordinator modes it holds no tools (a tool surface would make its mode
    /// a solo tier), so reading reports has to arrive as context rather than as a tool call; this is
    /// how a standing reviewer's judgement reaches the next plan and the answer the user reads. The
    /// reports are observations: the evidence says so, and deciding stays with whoever holds the verb.
    /// </summary>
    private async Task<ContextPack> WithOpenReportsAsync(RunState run, FrozenRunConfigurationV1 config, string agentId, ContextPack pack, CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || !Guid.TryParse(run.SessionId, out var sessionId)) return pack;
        var conversation = config.OperationAgents.FirstOrDefault(agent => agent.DirectUserOutput);
        if (conversation is null || !string.Equals(conversation.Id, agentId, StringComparison.OrdinalIgnoreCase)) return pack;
        IReadOnlyList<OrganizationReportDigest> reports;
        try
        {
            reports = await organization.ListOpenReportsAsync(sessionId, 8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not read the open reports of session {SessionId}.", sessionId);
            return pack;
        }
        var relevant = reports.Where(report => report.Severity is "warning" or "blocking").Take(5).ToArray();
        if (relevant.Length == 0) return pack;
        var text = "Open governance reports in this session's organization (observations by standing roles; they change nothing by themselves. "
            + "Take them into account, say so to the user when one blocks the work, and do not claim a proposal was carried out unless evidence shows it):\n"
            + string.Join("\n", relevant.Select(report =>
                $"- [{report.Severity}/{report.ReportKind}] {report.AuthorDisplayName}: {Clip(report.Finding, 300)}"
                + (report.SubjectKind is null ? string.Empty : $" (about {report.SubjectKind} {report.SubjectId})")
                + (report.ProposedVerb is null ? string.Empty : $"; proposes {report.ProposedVerb}")));
        var evidence = new ContextEvidence { Source = "organization_reports", Content = text, EstimatedTokens = Math.Max(1, text.Length / 2) };
        return new ContextPack
        {
            SessionId = pack.SessionId,
            RunId = pack.RunId,
            TokenBudget = pack.TokenBudget,
            EstimatedTokens = pack.EstimatedTokens + evidence.EstimatedTokens,
            Evidence = [.. pack.Evidence, evidence],
            Dropped = pack.Dropped,
            Metadata = pack.Metadata
        };
    }

    private async Task<string> GenerateMeetingResponseAsync(
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RuntimeAgentDefinition meetingDefinition,
        ContextPack context,
        CancellationToken cancellationToken)
    {
        var factory = CreateModelFactory(configuration, checkpoint, meetingDefinition, checkpoint.MeetingAgentId, null, streamAnswer: true);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(configuration, meetingDefinition, context, cancellationToken).ConfigureAwait(false);
        var escalation = checkpoint.SupervisionDecision == "escalate"
            ? "\n\nIMPORTANT: supervision escalated this run. Explain the unresolved decision clearly and ask the user for direction."
            : string.Empty;
        var evidence = string.Join("\n", checkpoint.Tasks.Select(FormatTaskEvidenceForMeeting));
        var instructions = assembly.Instructions + "\n\nYou are the conversation agent responsible for this run's reply. Reply directly and honestly in the user's language. Summarize completed work, evidence, limits, and next action. Do not claim tools ran if evidence does not say so." + escalation;
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
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new MeetingResponseUnavailableException("empty_model_response", "Meeting agent returned no output.");
        }
        return answer;
    }

    private const int MeetingEvidenceLineLimit = 400;

    /// <summary>
    /// One task as the conversation agent reads it at close-out: status, what the task was, who
    /// did it (the run-scoped handle), what it reported, and the per-criterion evidence. A bare
    /// "[status] summary" line left the coordinator unable to say which sub-task a result
    /// belonged to or whether each success criterion actually had evidence.
    /// </summary>
    internal static string FormatTaskEvidenceForMeeting(DurableTaskNode item)
    {
        var who = item.WorkerHandle ?? item.WorkerAgentSlug ?? "unassigned";
        var builder = new System.Text.StringBuilder()
            .Append("- [").Append(item.ResultStatus ?? item.Status).Append("] ")
            .Append(item.Title).Append(" (").Append(who).Append("): ")
            .Append(Bound(item.ResultSummary ?? "no result reported"));
        foreach (var entry in item.Evidence)
        {
            if (entry.StartsWith("criterion_evidence:", StringComparison.Ordinal))
            {
                var body = entry["criterion_evidence:".Length..];
                var split = body.IndexOf("||", StringComparison.Ordinal);
                builder.Append("\n    - ✓ ")
                    .Append(split < 0 ? Bound(body) : $"{Bound(body[..split])} — {Bound(body[(split + 2)..])}");
            }
            else if (entry.StartsWith("missing_criterion_evidence:", StringComparison.Ordinal))
            {
                builder.Append("\n    - ✗ no evidence: ").Append(Bound(entry["missing_criterion_evidence:".Length..]));
            }
        }
        return builder.ToString();

        static string Bound(string value)
        {
            var text = value.ReplaceLineEndings(" ").Trim();
            return text.Length <= MeetingEvidenceLineLimit ? text : text[..MeetingEvidenceLineLimit] + "…";
        }
    }

    internal static bool CanUseMeetingEvidenceFallback(FullDuplexCheckpointV1 checkpoint)
    {
        if (checkpoint.Tasks.Count == 0) return false;
        var terminal = checkpoint.Tasks.All(task => task.Status is "completed" or "failed" or "blocked");
        return terminal && (checkpoint.Tasks.All(task => task.Status == "completed")
            || checkpoint.UserAcceptedIncompleteOutcome);
    }

    internal static bool CanUseMeetingEvidenceFallback(
        FullDuplexCheckpointV1 checkpoint,
        ModelInvocationExhaustedException failure) =>
        CanUseMeetingEvidenceFallback(checkpoint)
        && (!IsDurableModelRetryCategory(failure.Category)
            || checkpoint.TransientModelRetryCount >= MaxDurableModelRetries);

    internal static string BuildMeetingEvidenceFallback(FullDuplexCheckpointV1 checkpoint, string errorCategory)
    {
        var chinese = checkpoint.UserGoal.Any(character => character is >= '\u4e00' and <= '\u9fff');
        var allCompleted = checkpoint.Tasks.All(task => task.Status == "completed");
        var lines = new List<string>
        {
            chinese
                ? allCompleted
                    ? "执行任务已经完成，但负责生成最终汇总的模型在重试后仍不可用。下面内容直接来自已持久化的执行证据："
                    : "用户已选择在部分任务未完成的情况下继续，但最终汇总模型不可用。下面内容直接来自已持久化的执行证据："
                : allCompleted
                    ? "The execution tasks completed, but the model responsible for the final summary remained unavailable after retry. The following comes directly from persisted execution evidence:"
                    : "The user chose to continue with incomplete tasks, but the final summary model was unavailable. The following comes directly from persisted execution evidence:"
        };
        foreach (var task in checkpoint.Tasks)
        {
            var summary = string.IsNullOrWhiteSpace(task.ResultSummary)
                ? (chinese ? "已完成，但没有留下摘要。" : "Completed without a stored summary.")
                : TruncateEvidence(task.ResultSummary);
            lines.Add($"- [{task.Status}] {task.Title}: {summary}");
            foreach (var evidence in task.Evidence
                .Where(item => !string.IsNullOrWhiteSpace(item)
                    && !string.Equals(item, task.ResultSummary, StringComparison.Ordinal)
                    && !item.StartsWith("dispatch:", StringComparison.Ordinal))
                .Take(2))
            {
                lines.Add(chinese
                    ? $"  证据：{TruncateEvidence(evidence)}"
                    : $"  Evidence: {TruncateEvidence(evidence)}");
            }
        }
        foreach (var reason in checkpoint.SupervisionReasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).Take(3))
        {
            lines.Add(chinese
                ? $"- 验收说明：{TruncateEvidence(reason)}"
                : $"- Review note: {TruncateEvidence(reason)}");
        }
        lines.Add(chinese
            ? $"说明：这份答复未经过 meeting 模型润色；模型错误类别为 {errorCategory}。"
            : $"Note: this response was not rewritten by the meeting model; model error category: {errorCategory}.");
        return string.Join("\n", lines);
    }


    internal static List<DurableTaskNode> ValidateAndMaterializeGraph(
        PlannedTask[] tasks,
        int maxTasks,
        IReadOnlyList<FrozenDispatchTarget>? dispatchRoster = null)
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
            RequestedAgent = ResolveAssignee(candidate.Key, candidate.Task.Assignee, dispatchRoster),
            WriteScope = NormalizeWriteScope(candidate.Task.WriteScope),
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

    /// <summary>
    /// Canonicalizes a coordinator-named executor against the dispatch roster. An id the roster
    /// does not hold is a correctable planning error — the message lists the valid ids with their
    /// responsibilities so the planner retry can fix it — never a silent re-route.
    /// </summary>
    internal static string? ResolveAssignee(string taskKey, string? assignee, IReadOnlyList<FrozenDispatchTarget>? roster)
    {
        if (string.IsNullOrWhiteSpace(assignee)) return null;
        var requested = assignee.Trim();
        if (roster is null) return requested;
        var match = roster.FirstOrDefault(target => string.Equals(target.Id, requested, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Id;
        throw new InvalidTaskGraphException(
            $"Task '{taskKey}' names assignee '{requested}', which is not a dispatchable executor. {DescribeDispatchChoices(roster)}");
    }

    /// <summary>A declared write scope, trimmed and de-duplicated; null when nothing was declared.</summary>
    internal static List<string>? NormalizeWriteScope(IEnumerable<string>? entries)
    {
        var scope = (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
        return scope.Count == 0 ? null : scope;
    }

    /// <summary>One-line-per-executor listing used in every "pick a valid executor" error.</summary>
    internal static string DescribeDispatchChoices(IReadOnlyList<FrozenDispatchTarget> roster) =>
        roster.Count == 0
            ? "This run has no dispatchable executor."
            : "Valid ids: " + string.Join("; ", roster.Select(target =>
                string.IsNullOrWhiteSpace(target.Description) ? target.Id : $"{target.Id} ({target.Description})")) + ".";

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
                RequestedAgent = prior.RequestedAgent,
                WorkerHandle = prior.WorkerHandle,
                DispatchId = prior.DispatchId,
                WriteScope = prior.WriteScope,
                DispatchedByTaskId = prior.DispatchedByTaskId,
                AwaitingTaskIds = prior.AwaitingTaskIds,
                ReportedTaskIds = prior.ReportedTaskIds,
                Plan = prior.Plan,
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

    /// <summary>
    /// Refreshes (or safely reacquires) the run lease immediately before an
    /// external tool execution and returns the exact epoch the dispatcher must
    /// present to Lifecycle. A model/tool-manifest turn may outlive the ordinary
    /// 30-second lease, so relying on the loop-top heartbeat is not sufficient.
    /// </summary>
    private async Task<RunExecutionAuthority> RequireToolExecutionAuthorityAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var epoch = LeaseEpochFor(runId)
            ?? throw new RunCheckpointConflictException(runId.ToString(), 0, 0);
        var expectedRecoveryCount = epoch.RecoveryCount;
        if (!await TryRetainLeaseUntilAsync(
                runId,
                DateTimeOffset.UtcNow.Add(LeaseDuration),
                expectedRecoveryCount,
                cancellationToken).ConfigureAwait(false))
        {
            throw new RunCheckpointConflictException(runId.ToString(), 0, 0);
        }

        epoch = LeaseEpochFor(runId)
            ?? throw new RunCheckpointConflictException(runId.ToString(), 0, 0);
        return new RunExecutionAuthority(epoch.OwnerId, epoch.RecoveryCount);
    }

    private async Task EvaluateAndDispatchOperationsAsync(
        OperationalTriggerPoint point,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken,
        string? summary = null,
        string? subjectKind = null,
        string? subjectId = null)
    {
        // Standing members hear the fact through their organization inbox and take their own turn
        // later; the built-in behaviours below run inline. Neither ever holds this run up.
        await RaiseStandingAsync(run, configuration, OperationalTriggerEvaluator.TopicOf(point),
            summary ?? DefaultSummary(point, checkpoint), subjectKind, subjectId, cancellationToken).ConfigureAwait(false);
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
            if (OperationalRoles.BuiltInKind(match.Agent) is null) continue;
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
        if (checkpoint.Tasks.Any(task => task.Status is "running" or "waiting" || !string.IsNullOrWhiteSpace(task.PendingToolExecutionId)))
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

        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null, observeOutput: false, interruptible: false);
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
        // The upper layer is archived next to what it summarised, so recall can walk from one to the other.
        await ArchiveEvidenceAsync(run, null, EvidenceKinds.Summary, $"Context summary at revision {baseRevision}", match.Agent.Id,
            summary, $"summary:{result.PatchId}", cancellationToken).ConfigureAwait(false);
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
        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null, observeOutput: false, interruptible: false);
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
        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null, observeOutput: false, interruptible: false);
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
            VersionContentHash: definition.VersionContentHash,
            AllowedDispatchTargets: definition.AllowedDispatchTargets), cancellationToken).ConfigureAwait(false);
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
        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null, observeOutput: false, interruptible: false);
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

    private static string ModelFailureCategory(string category) => category switch
    {
        RunErrorTaxonomy.RateLimited => RunErrorTaxonomy.RateLimited,
        RunErrorTaxonomy.ProviderServerError => RunErrorTaxonomy.ProviderServerError,
        RunErrorTaxonomy.ConnectionFailed => RunErrorTaxonomy.ConnectionFailed,
        RunErrorTaxonomy.ProviderError => RunErrorTaxonomy.ProviderError,
        RunErrorTaxonomy.RequestError => RunErrorTaxonomy.RequestError,
        RunErrorTaxonomy.AuthenticationOrAuthorization => RunErrorTaxonomy.AuthenticationOrAuthorization,
        RunErrorTaxonomy.ToolTimeout => RunErrorTaxonomy.ToolTimeout,
        _ => RunErrorTaxonomy.Model
    };

    private static bool IsCompletionClaimed(RunState run) =>
        run.CompletedAt is not null && !IsTerminal(run.Status);

    private static string RunStatusStorageValue(RunStatus status) => status switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown run status.")
    };

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
            await SetRunStatusFencedAsync(runId, status, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogWarning(ex, "Could not move run {RunId} to {Status}; the durable checkpoint remains authoritative.", runId, status);
        }
    }

    private Task SetRunStatusFencedAsync(
        string runId,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(runId, out var parsedRunId)
            && LeaseEpochFor(parsedRunId) is { } epoch)
        {
            return _lifecycle.SetRunStatusUnderLeaseAsync(
                runId,
                status,
                message,
                epoch.OwnerId,
                epoch.RecoveryCount,
                cancellationToken);
        }
        return _lifecycle.SetRunStatusAsync(runId, status, message, cancellationToken);
    }

    private Task SetRunFailedFencedAsync(
        string runId,
        string message,
        string category,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(runId, out var parsedRunId)
            && LeaseEpochFor(parsedRunId) is { } epoch)
        {
            return _lifecycle.SetRunFailedUnderLeaseAsync(
                runId,
                message,
                category,
                epoch.OwnerId,
                epoch.RecoveryCount,
                cancellationToken);
        }
        return _lifecycle.SetRunStatusAsync(runId, "failed", message, cancellationToken);
    }

    private RunLeaseEpoch? LeaseEpochFor(Guid runId) =>
        _currentLeaseEpoch.Value is { RunId: var epochRunId } epoch && epochRunId == runId
            ? epoch
            : null;

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
    private sealed record TaskWaitOutcome(FullDuplexCheckpointV1 Checkpoint, bool Parked);

    /// <summary>
    /// Executes a <c>task_wait</c> call. Queued dispatches are materialized first (a sub-task the
    /// caller dispatched in the same response must be waitable at once). When every awaited
    /// sub-task is terminal the call completes with their outcomes; otherwise the caller parks as
    /// <c>waiting</c> and the tick moves on, so the sub-tasks run — the caller is dispatched again
    /// once they are all terminal and re-enters here with the same pending call.
    /// </summary>
    private async Task<TaskWaitOutcome> HandleTaskWaitAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsTaskWait(tool.ToolId)))
        {
            return new(await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                "task_wait is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false), false);
        }
        if (await ApplyPendingTaskDispatchesAsync(run, checkpoint, cancellationToken).ConfigureAwait(false))
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-dispatched", cancellationToken).ConfigureAwait(false);
        }

        var requested = TryParseJsonObject(turn.ArgumentsJson, out var parameters)
            ? ReadStringArray(parameters, "dispatch_ids")
            : [];
        var targets = ResolveWaitTargets(checkpoint.Tasks, task, requested, out var unknown);
        if (unknown.Count > 0)
        {
            var own = checkpoint.Tasks.Where(item => item.DispatchedByTaskId == task.TaskId)
                .Select(item => $"{item.DispatchId} ({item.WorkerHandle ?? item.RequestedAgent ?? item.TaskKey}: {item.Title})")
                .ToArray();
            var choices = own.Length == 0 ? "You have not dispatched any sub-task in this task." : "Your sub-tasks: " + string.Join("; ", own) + ".";
            return new(await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                $"task_wait does not know {string.Join(", ", unknown.Select(id => $"'{id}'"))}. {choices}",
                checkpoint, cancellationToken).ConfigureAwait(false), false);
        }

        if (targets.All(item => IsTerminalTaskStatus(item.Status)))
        {
            turn.ResultJson = BuildTaskWaitResult(targets);
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = true;
            foreach (var item in targets.Where(item => !task.ReportedTaskIds.Contains(item.TaskId)))
                task.ReportedTaskIds.Add(item.TaskId);
            var resumed = task.AwaitingTaskIds.Count > 0;
            task.AwaitingTaskIds = [];
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-wait-resolved", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(Guid.Parse(run.RunId), "task.wait_resolved",
                $"{targets.Count} sub-task result(s) returned to '{task.TaskKey}'.",
                new
                {
                    run_id = run.RunId,
                    task_id = task.TaskId,
                    task_key = task.TaskKey,
                handle = task.WorkerHandle,
                    call_id = turn.CallId,
                    resumed,
                    awaited = targets.Select(item => new { task_key = item.TaskKey, handle = item.WorkerHandle, status = item.ResultStatus ?? item.Status }).ToArray()
                }, cancellationToken, task.TaskId, idempotencyKey: $"task-wait-resolved:{task.TaskId}:{turn.CallId}").ConfigureAwait(false);
            return new(checkpoint, false);
        }

        task.AwaitingTaskIds = targets.Select(item => item.TaskId).ToList();
        task.Status = "waiting";
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-waiting", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "task.wait_started",
            $"'{task.TaskKey}' is waiting for {targets.Count} sub-task(s).",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                handle = task.WorkerHandle,
                call_id = turn.CallId,
                awaited = targets.Select(item => new { task_key = item.TaskKey, handle = item.WorkerHandle, title = item.Title, agent = item.RequestedAgent, status = item.Status }).ToArray()
            }, cancellationToken, task.TaskId, idempotencyKey: $"task-wait-started:{task.TaskId}:{turn.CallId}").ConfigureAwait(false);
        return new(checkpoint, true);
    }

    /// <summary>
    /// Executes plan_update: validates the steps, stores them on the task node and emits
    /// plan.updated. A malformed plan goes back to the model as a correctable tool error.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> HandlePlanUpdateAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsPlanUpdate(tool.ToolId)))
        {
            return await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                "plan_update is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false);
        }
        var error = TryParsePlan(turn.ArgumentsJson, out var steps, out var explanation);
        if (error is not null)
        {
            return await FeedToolFailureBackAsync(run, task, turn, RunErrorTaxonomy.InvalidToolArguments,
                error, checkpoint, cancellationToken).ConfigureAwait(false);
        }

        task.Plan = steps;
        turn.ResultJson = JsonSerializer.Serialize(new
        {
            ok = true,
            steps = steps.Count,
            completed = steps.Count(step => step.Status == "completed")
        });
        turn.DispatchStatus = ToolDispatchStatus.Completed;
        turn.ToolSuccess = true;
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "plan-updated", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "plan.updated",
            $"'{task.TaskKey}' updated its plan ({steps.Count} step(s)).",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                handle = task.WorkerHandle,
                explanation,
                steps = steps.Select(step => new { step = step.Step, status = step.Status }).ToArray()
            }, cancellationToken, task.TaskId, idempotencyKey: $"plan-updated:{task.TaskId}:{turn.CallId}").ConfigureAwait(false);
        return checkpoint;
    }

    /// <summary>Parses plan_update arguments; returns the model-facing error, or null when the plan is valid.</summary>
    internal static string? TryParsePlan(string? argumentsJson, out List<TaskPlanStep> steps, out string? explanation)
    {
        steps = [];
        explanation = null;
        const string shape = "Send {\"steps\": [{\"step\": \"...\", \"status\": \"pending\" | \"in_progress\" | \"completed\"}]}.";
        if (string.IsNullOrWhiteSpace(argumentsJson)
            || !TryParseJsonObject(argumentsJson, out var parameters)
            || !parameters.TryGetProperty("steps", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return "plan_update needs a steps array. " + shape;
        }
        if (parameters.TryGetProperty("explanation", out var why) && why.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(why.GetString()))
        {
            explanation = BoundText(why.GetString()!.Trim(), 500);
        }
        foreach (var item in array.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("step", out var stepValue)
                && stepValue.ValueKind == JsonValueKind.String ? stepValue.GetString()?.Trim() : null;
            var status = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("status", out var statusValue)
                && statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString()?.Trim().ToLowerInvariant() : null;
            if (string.IsNullOrEmpty(text))
                return $"Step {steps.Count + 1} has no step text. " + shape;
            if (status is not ("pending" or "in_progress" or "completed"))
                return $"Step {steps.Count + 1} ('{BoundText(text, 80)}') has status '{status}'; use pending, in_progress or completed.";
            steps.Add(new TaskPlanStep(BoundText(text, 300), status));
        }
        if (steps.Count == 0)
            return "The plan is empty. " + shape;
        if (steps.Count > CoreVirtualToolPolicy.PlanUpdateMaxSteps)
            return $"The plan has {steps.Count} steps; keep it to {CoreVirtualToolPolicy.PlanUpdateMaxSteps} or fewer by merging steps.";
        if (steps.Count(step => step.Status == "in_progress") > 1)
            return "More than one step is in_progress; keep exactly the step you are working on in_progress.";
        return null;
    }

    internal static bool IsTerminalTaskStatus(string status) => status is "completed" or "failed" or "blocked";

    /// <summary>Dispatchable this tick: pending/ready, or parked on task_wait with every awaited sub-task terminal.</summary>
    internal static bool IsDispatchable(DurableTaskNode task, IReadOnlyList<DurableTaskNode> tasks) =>
        task.Status is "pending" or "ready" || IsWaitSatisfied(task, tasks);

    internal static bool IsWaitSatisfied(DurableTaskNode task, IReadOnlyList<DurableTaskNode> tasks) =>
        task.Status == "waiting"
        && task.AwaitingTaskIds.All(id => tasks.FirstOrDefault(other => other.TaskId == id) is not { } awaited
            || IsTerminalTaskStatus(awaited.Status));

    /// <summary>
    /// The sub-tasks a task_wait call names: by dispatch_id or handle among the caller's own
    /// dispatches, or — when it names none — every one of them not yet reported to it.
    /// </summary>
    internal static List<DurableTaskNode> ResolveWaitTargets(
        IReadOnlyList<DurableTaskNode> tasks,
        DurableTaskNode waiter,
        IReadOnlyList<string> requested,
        out List<string> unknown)
    {
        unknown = [];
        var own = tasks.Where(item => item.DispatchedByTaskId == waiter.TaskId).ToList();
        if (requested.Count == 0)
            return own.Where(item => !waiter.ReportedTaskIds.Contains(item.TaskId)).ToList();
        var targets = new List<DurableTaskNode>();
        foreach (var reference in requested)
        {
            var match = FindDispatchedTask(own, waiter.TaskId, reference);
            if (match is null) unknown.Add(reference);
            else if (!targets.Contains(match)) targets.Add(match);
        }
        return targets;
    }

    /// <summary>A dispatched sub-task by dispatch_id, handle or task key, among one dispatcher's sub-tasks.</summary>
    internal static DurableTaskNode? FindDispatchedTask(IReadOnlyList<DurableTaskNode> tasks, Guid? dispatcher, string reference)
    {
        var value = reference.Trim();
        var candidates = tasks.Where(item => dispatcher is null || item.DispatchedByTaskId == dispatcher).ToList();
        return candidates.FirstOrDefault(item => Guid.TryParse(value, out var id) && item.DispatchId == id)
            ?? candidates.FirstOrDefault(item => string.Equals(item.WorkerHandle, value, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(item => string.Equals(item.TaskKey, value, StringComparison.OrdinalIgnoreCase));
    }

    private const int TaskWaitFieldLimit = 2000;

    /// <summary>task_wait's result: one entry per awaited sub-task, bounded, evidence per criterion.</summary>
    internal static string BuildTaskWaitResult(IReadOnlyList<DurableTaskNode> targets) =>
        JsonSerializer.Serialize(new
        {
            tasks = targets.Select(item => new
            {
                dispatch_id = item.DispatchId,
                handle = item.WorkerHandle,
                agent = item.WorkerAgentSlug ?? item.RequestedAgent,
                title = item.Title,
                status = item.ResultStatus ?? item.Status,
                summary = BoundText(item.ResultSummary ?? string.Empty, TaskWaitFieldLimit),
                criteria = item.Evidence
                    .Where(entry => entry.StartsWith("criterion_evidence:", StringComparison.Ordinal)
                        || entry.StartsWith("missing_criterion_evidence:", StringComparison.Ordinal))
                    .Select(entry => entry.StartsWith("criterion_evidence:", StringComparison.Ordinal)
                        ? SplitCriterion(entry["criterion_evidence:".Length..])
                        : new CriterionResult(BoundText(entry["missing_criterion_evidence:".Length..], 400), false, null))
                    .ToArray()
            }).ToArray()
        });

    private sealed record CriterionResult(
        [property: JsonPropertyName("criterion")] string Criterion,
        [property: JsonPropertyName("met")] bool Met,
        [property: JsonPropertyName("evidence")] string? Evidence);

    private static CriterionResult SplitCriterion(string body)
    {
        var split = body.IndexOf("||", StringComparison.Ordinal);
        return split < 0
            ? new CriterionResult(BoundText(body, 400), true, null)
            : new CriterionResult(BoundText(body[..split], 400), true, BoundText(body[(split + 2)..], 400));
    }

    private static string BoundText(string value, int limit)
    {
        var text = value.Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }

    /// <summary>
    /// The brief of a follow-up sub-task: the earlier sub-task's title, outcome and evidence ahead of
    /// the new instruction. A new executor instance does not share the earlier one's conversation, so
    /// this is the context it continues from.
    /// </summary>
    internal static string FollowUpBrief(DurableTaskNode prior, string? instruction)
    {
        var builder = new System.Text.StringBuilder()
            .Append("This continues an earlier sub-task: ").Append(prior.Title)
            .Append(" (").Append(prior.WorkerHandle ?? prior.WorkerAgentSlug ?? prior.TaskKey).Append(", ")
            .Append(prior.ResultStatus ?? prior.Status).Append(").\nIts result: ")
            .Append(BoundText(prior.ResultSummary ?? "no result reported yet", TaskWaitFieldLimit));
        foreach (var entry in prior.Evidence.Where(entry => entry.StartsWith("criterion_evidence:", StringComparison.Ordinal)))
            builder.Append("\n- ").Append(BoundText(entry["criterion_evidence:".Length..].Replace("||", " — "), 400));
        builder.Append("\n\nNew instruction: ").Append(string.IsNullOrWhiteSpace(instruction) ? "(see title)" : instruction.Trim());
        return builder.ToString();
    }

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
        var taskEvidence = checkpoint.Tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ResultSummary) || task.Evidence.Count > 0)
            .TakeLast(MaxRunEvidenceTurns)
            .Select(task => TruncateEvidence(
                $"task {task.TaskKey} {task.Status}: {task.ResultSummary ?? string.Empty}; "
                + string.Join(" | ", task.Evidence
                    .Where(item => !string.IsNullOrWhiteSpace(item)
                        && !string.Equals(item, task.ResultSummary, StringComparison.Ordinal))
                    .Take(2))))
            .ToList();
        var toolEvidence = turns
            .TakeLast(MaxRunEvidenceTurns)
            .Select(turn =>
            {
                var status = string.IsNullOrWhiteSpace(turn.DispatchStatus) ? "completed" : turn.DispatchStatus;
                var category = string.IsNullOrWhiteSpace(turn.ErrorCategory) ? string.Empty : $" ({turn.ErrorCategory})";
                return TruncateEvidence($"{turn.ToolId} {status}{category}: {turn.ResultJson ?? string.Empty}");
            })
            .ToList();
        if (taskEvidence.Count == 0 && toolEvidence.Count == 0) return;

        var sections = new List<string>();
        if (taskEvidence.Count > 0)
        {
            sections.Add($"task_evidence — {checkpoint.Tasks.Count} task(s), showing {taskEvidence.Count}\n"
                + string.Join('\n', taskEvidence));
        }
        if (toolEvidence.Count > 0)
        {
            sections.Add($"tool_evidence — {turns.Count} tool round(s), showing the last {toolEvidence.Count}\n"
                + string.Join('\n', toolEvidence));
        }
        var content = string.Join("\n\n", sections);
        await _conversations.AppendMessageAsync(checkpoint.SessionId, "tool_evidence", content,
            runId, checkpoint.TurnId, $"run:{run.RunId}:turn:{checkpoint.TurnId}:tool-evidence:v1", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "run.evidence_recorded",
            "The run's tool activity was persisted into the conversation history.",
            new
            {
                task_count = checkpoint.Tasks.Count,
                task_evidence_recorded = taskEvidence.Count,
                turn_count = turns.Count,
                tool_evidence_recorded = toolEvidence.Count
            }, cancellationToken,
            idempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:evidence-recorded").ConfigureAwait(false);
    }

    private static string TruncateEvidence(string value)
    {
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxRunEvidenceLineLength ? collapsed : collapsed[..MaxRunEvidenceLineLength] + "…";
    }

    private static StepResult BuildWorkerStepResult(DurableTaskNode task, Guid workerId, string text)
    {
        var outcome = WorkerOutcomeProtocol.Parse(text, requireExplicit: true);
        var evaluation = EvaluateWorkerOutcome(task, outcome);
        var summary = ExtractContextPatch(outcome.Summary, out var patchSummary, out var patchContent);
        if (evaluation.MissingCriteria.Count > 0)
        {
            summary += "\nMissing criterion evidence: " + string.Join("; ", evaluation.MissingCriteria);
        }
        if (outcome.Status == "completed" && evaluation.UnresolvedToolFailure)
        {
            summary += "\nCompletion rejected: the latest explicit tool outcome reports success=false and no later successful tool outcome resolved it.";
        }
        var taskId = task.TaskId;
        var evidence = new List<string>
        {
            summary,
            DispatchEvidence(task),
            $"worker_outcome:{evaluation.Status}:{OutcomeBasis(outcome)}"
        };
        evidence.AddRange(outcome.Criteria.Select(item =>
            $"criterion_evidence:{item.Criterion}||{item.Evidence}"));
        evidence.AddRange(evaluation.MissingCriteria.Select(item =>
            $"missing_criterion_evidence:{item}"));
        if (evaluation.UnresolvedToolFailure) evidence.Add("tool_outcome_fact:unresolved_failure");
        return new StepResult
        {
            TaskNodeId = taskId,
            AgentId = workerId.ToString("N"),
            Status = evaluation.Status,
            Summary = summary,
            Evidence = evidence,
            CriterionVerdicts = evaluation.Verdicts,
            ProposedPatchSummary = patchSummary,
            ProposedPatchContent = patchContent
        };
    }

    private static string OutcomeBasis(WorkerOutcomeProtocol.Parsed outcome) =>
        outcome.ContradictedCompletion ? "explicit_contradiction"
            : outcome.Explicit ? "explicit"
            : outcome.MissingRequiredMarker ? "missing_marker"
            : "legacy_inferred";

    private static WorkerOutcomeEvaluation EvaluateWorkerOutcome(
        DurableTaskNode task,
        WorkerOutcomeProtocol.Parsed outcome)
    {
        var criteria = outcome.Criteria
            .Where(item => !string.IsNullOrWhiteSpace(item.Criterion) && !string.IsNullOrWhiteSpace(item.Evidence))
            .GroupBy(item => item.Criterion.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Evidence.Trim(), StringComparer.Ordinal);
        var verdicts = task.SuccessCriteria
            .Select(criterion =>
            {
                var normalized = criterion.Trim();
                var found = criteria.TryGetValue(normalized, out var evidence);
                return new LaneCriterionVerdict(task.TaskKey, criterion, found, found ? evidence : null);
            })
            .ToArray();
        var missing = verdicts
            .Where(item => !item.Satisfied || string.IsNullOrWhiteSpace(item.Evidence))
            .Select(item => item.Criterion)
            .ToArray();
        var unresolvedToolFailure = LatestEmbeddedToolOutcome(task) == false;
        var status = EnforceLatestToolOutcomeFact(task,
            outcome.Status == "completed" && missing.Length > 0 ? "blocked" : outcome.Status);
        return new WorkerOutcomeEvaluation(status, verdicts, missing, unresolvedToolFailure);
    }

    /// <summary>
    /// A model cannot close a task as completed while the most recent explicit
    /// business-level tool outcome is a failure. A later explicit tool success is
    /// evidence that the worker actually attempted a correction and clears this
    /// particular fence; criterion/supervision gates still decide whether the whole
    /// task goal was satisfied.
    /// </summary>
    internal static string EnforceLatestToolOutcomeFact(DurableTaskNode task, string proposedStatus) =>
        string.Equals(proposedStatus, "completed", StringComparison.Ordinal)
        && LatestEmbeddedToolOutcome(task) == false
            ? "failed"
            : proposedStatus;

    /// <summary>
    /// Returns the most recent explicit business-level tool outcome. A failed tool
    /// result remains unresolved until the worker performs a later tool call that
    /// explicitly succeeds. Results without an embedded success flag are neutral:
    /// they cannot erase a known failure merely because their transport completed.
    /// </summary>
    internal static bool? LatestEmbeddedToolOutcome(DurableTaskNode task)
    {
        for (var index = task.ToolTurns.Count - 1; index >= 0; index--)
        {
            if (task.ToolTurns[index].ToolSuccess is { } success) return success;
        }
        return null;
    }

    private sealed record WorkerOutcomeEvaluation(
        string Status,
        IReadOnlyList<LaneCriterionVerdict> Verdicts,
        IReadOnlyList<string> MissingCriteria,
        bool UnresolvedToolFailure);

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
    internal sealed class WorkerAssignmentException(string message, Exception? inner = null)
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
        TaskExecutionResult? Result)
    {
        public Guid TaskId { get; init; }
    }
    private readonly record struct ModelRetryScheduleResult(
        bool Scheduled,
        bool LeaseRetained,
        bool ExitWithoutFailure);
    private sealed class RunLeaseEpoch(Guid runId, string ownerId, int recoveryCount)
    {
        public Guid RunId { get; } = runId;
        public string OwnerId { get; } = ownerId;
        public int RecoveryCount { get; set; } = recoveryCount;
    }
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UserAcceptedIncompleteOutcome { get; set; }
    public string? MeetingResponse { get; set; }
    public List<RecommendedCapability> RecommendedCapabilities { get; set; } = [];
    public ModelUsage? ModelUsage { get; set; }
    public Guid? AssistantMessageId { get; set; }

    /// <summary>
    /// Number of consecutive full model calls that exhausted their own short
    /// retry/fallback chain without durable phase progress. This is a finite fuse,
    /// not a model-call attempt counter: each increment already represents up to
    /// three provider attempts recorded in model_invocations. Any successful phase
    /// transition resets it, so unrelated outages across a long run do not add up.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TransientModelRetryCount { get; set; }

    /// <summary>
    /// Durable cooling-off deadline after a transient model outage. A live host
    /// also mirrors it in an in-memory scheduler; after restart the checkpoint is
    /// sufficient for the recovery scan to resume at or after this time.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ModelRetryNotBefore { get; set; }

    /// <summary>The last retryable model failure category, retained for audit events.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastTransientModelErrorCategory { get; set; }

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

    /// <summary>
    /// The executor the coordinator named for this task (planner <c>assignee</c> or
    /// <c>task_dispatch.agent</c>). When set, worker selection honors it instead of inferring
    /// a worker from required tools. Null-suppressed for pre-existing checkpoint bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedAgent { get; set; }

    /// <summary>
    /// Run-scoped instance handle <c>&lt;agent&gt;#&lt;n&gt;</c> assigned with the worker: stable,
    /// readable, and what evidence and the final answer refer to the executor by.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkerHandle { get; set; }

    /// <summary>The task_dispatch id this sub-task was created from (what task_wait names it by).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DispatchId { get; set; }

    /// <summary>
    /// Paths the dispatcher declared this task will write (<c>write_scope</c>). Leased exclusively
    /// for the task's lifetime and used as the yardstick for what it actually changed. Null when
    /// undeclared: the task is then bounded only by its resource envelope.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? WriteScope { get; set; }

    /// <summary>The task whose tool loop dispatched this sub-task; only it may wait for it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DispatchedByTaskId { get; set; }

    /// <summary>
    /// Sub-tasks this task is parked on (status <c>waiting</c>, via task_wait). The task becomes
    /// dispatchable again once every one of them is terminal. Empty when not waiting.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<Guid> AwaitingTaskIds { get; set; } = [];

    /// <summary>Sub-tasks whose results task_wait already handed to this task.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<Guid> ReportedTaskIds { get; set; } = [];

    /// <summary>The agent's own working plan, as last sent through plan_update. Null until it sends one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TaskPlanStep>? Plan { get; set; }

    /// <summary>
    /// Rounds a hard insert cut off (todo D2), oldest first; each is shown to the worker where it
    /// happened. Null until one is, so earlier checkpoints keep their bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WorkerInterruption>? Interruptions { get; set; }

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

/// <summary>One step of an agent's plan_update plan; status is pending, in_progress or completed.</summary>
public sealed record TaskPlanStep(
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("status")] string Status);


internal sealed record StaleTaskEvidence(
    long InputContextRevision,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    DateTimeOffset RecordedAt);

internal sealed class RunAwaitingExternalDecisionException : Exception;

internal sealed class WorkerUnavailableException(string message) : Exception(message);

internal sealed class MeetingResponseUnavailableException(string category, string message) : Exception(message)
{
    public string Category { get; } = category;
}
