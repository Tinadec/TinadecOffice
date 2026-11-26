using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
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
    Task FinalizeCancelledRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task FinalizeFailedRunAsync(
        Guid runId,
        string? errorCategory = null,
        string? safeErrorMessage = null,
        CancellationToken cancellationToken = default);
    Task ReconcileTerminalRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

internal sealed partial class FullDuplexRunEngine : BackgroundService, IFullDuplexRunEngine, ITeamRunBoardActionSink, ITeammateControlPort
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

    /// <summary>Consecutive empty worker responses tolerated before closeout.</summary>
    private const int MaxConsecutiveEmptyResponses = 3;

    /// <summary>Fingerprints handed to repeat detection.</summary>
    private const int FingerprintTail = 3;

    /// <summary>Durable transient provider retry ceiling.</summary>
    internal const int MaxDurableModelRetries = 5;

    /// <summary>Upper bound on teammate-to-teammate board wake relays (#33).</summary>
    public const int MaxTeammateRelayDepth = 8;

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

    private const string ToolManifestUnavailableCategory = RunErrorTaxonomy.ToolManifestUnavailable;
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
    private readonly IContextMemoryService? _contextMemory;
    private readonly IWorktreeIntegratorService? _worktreeIntegrator;
    private readonly ITeamBoardMcpBridge? _teamBoardMcpBridge;
    private readonly ICompanionSessionBackflow? _companionBackflow;
    private readonly IPlanLedger? _planLedger;
    private readonly AutonomousExplorationCoordinator? _autonomousPlanning;
    private readonly IExplorationGraph? _exploration;
    private readonly IContextDigest? _contextDigest;
    private readonly WorkerSettlementService? _workerSettlement;
    private readonly IResourceLeaseService? _resourceLeases;
    private readonly SessionPlanService? _sessionPlan;
    private ISessionOrganization? _organization;
    private bool _organizationResolved;
    private IRunInterrupts? _interrupts;
    private bool _interruptsResolved;

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
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<LiveMcpBoardPost>> _liveMcpBoardPosts = new();
    private readonly ConcurrentDictionary<Guid, RunTeammateActorHub> _runActorHubs = new();
    private readonly AsyncLocal<CheckpointGateHolder?> _checkpointGateHolder = new();
    private readonly ConcurrentDictionary<Guid, long> _compactionRecommendedRevisions = new();
    private readonly AsyncLocal<RunLeaseEpoch?> _currentLeaseEpoch = new();
    private readonly AsyncLocal<bool> _deferParallelTaskCheckpointWrites = new();
    private readonly ConcurrentDictionary<Guid, byte> _wakeAfterRun = new();
    private readonly ConcurrentDictionary<Guid, AnswerDraft> _answerDrafts = new();
    private readonly RunStreamProjection? _projection;
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
        IOrchestrationDirectiveValidator? directiveValidator = null,
        IWorktreeIntegratorService? worktreeIntegrator = null,
        ITeamBoardMcpBridge? teamBoardMcpBridge = null,
        IContextMemoryService? contextMemory = null,
        ICompanionSessionBackflow? companionBackflow = null)
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
        _worktreeIntegrator = worktreeIntegrator ?? services.GetService(typeof(IWorktreeIntegratorService)) as IWorktreeIntegratorService;
        _teamBoardMcpBridge = teamBoardMcpBridge ?? services.GetService(typeof(ITeamBoardMcpBridge)) as ITeamBoardMcpBridge;
        _contextMemory = contextMemory ?? services.GetService(typeof(IContextMemoryService)) as IContextMemoryService;
        _companionBackflow = companionBackflow ?? services.GetService(typeof(ICompanionSessionBackflow)) as ICompanionSessionBackflow;
        _longTermMemory = services.GetService(typeof(ILongTermMemoryService)) as ILongTermMemoryService;
        _planLedger = services.GetService(typeof(IPlanLedger)) as IPlanLedger;
        _autonomousPlanning = services.GetService(typeof(AutonomousExplorationCoordinator)) as AutonomousExplorationCoordinator;
        _exploration = services.GetService(typeof(IExplorationGraph)) as IExplorationGraph;
        _contextDigest = services.GetService(typeof(IContextDigest)) as IContextDigest;
        _workerSettlement = services.GetService(typeof(WorkerSettlementService)) as WorkerSettlementService;
        _resourceLeases = services.GetService(typeof(IResourceLeaseService)) as IResourceLeaseService;
        _sessionPlan = services.GetService(typeof(SessionPlanService)) as SessionPlanService;
        _projection = services.GetService(typeof(RunStreamProjection)) as RunStreamProjection;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (_runActorHubs.TryGetValue(runId, out var hub))
        {
            hub.SignalWake();
        }
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
            await PersistFailureTerminalAsync(runId, state, checkpoint, category, message, cancellationToken).ConfigureAwait(false);
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
        _liveMcpBoardPosts.TryRemove(runId, out _);
        _runActorHubs.TryRemove(runId, out _);
        _compactionRecommendedRevisions.TryRemove(runId, out _);
        try
        {
            _teamBoardMcpBridge?.RevokeRunTokens(runId);
        }
        catch (Exception ex)
        {
            _logger.TryLogDebug(ex, "Could not revoke team MCP tokens for terminal run {RunId}.", runId);
        }
        try
        {
            await _instances.ReleaseRunInstancesAsync(runId, cancellationToken).ConfigureAwait(false);
            await ReleaseRunLeasesAsync(runId, cancellationToken).ConfigureAwait(false);
            if (_services.GetService<IToolExecutionContextLifecycle>() is { } contexts)
                await contexts.ReleaseAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
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

            TimeSpan waitDelay;
            if (running.Count > 0)
            {
                waitDelay = TimeSpan.FromMilliseconds(100);
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                var nextWake = nextScan;
                foreach (var retryAt in _scheduledModelRetries.Values)
                {
                    if (retryAt < nextWake) nextWake = retryAt;
                }
                waitDelay = nextWake - now;
                if (waitDelay < TimeSpan.FromMilliseconds(50))
                    waitDelay = TimeSpan.FromMilliseconds(50);
                else if (waitDelay > ScanInterval)
                    waitDelay = ScanInterval;
            }

            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                waitCts.CancelAfter(waitDelay);
                await _queue.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Wake timeout elapsed cleanly; re-evaluate due scans/retries/running completions.
            }
        }

        try { await Task.WhenAll(running.Values).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task ExecuteRunAsync(Guid runId, CancellationToken stoppingToken)
    {
        RunLease? lease = null;
        for (var attempt = 0; attempt < 20 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            lease = await _lifecycle.TryAcquireRunLeaseAsync(runId.ToString(), _ownerId, LeaseDuration, stoppingToken).ConfigureAwait(false);
            if (lease.Acquired) break;
            await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
        }
        if (lease is null or { Acquired: false }) return;

        var releaseLease = true;
        _checkpointGateHolder.Value = new CheckpointGateHolder();
        var leaseEpoch = new RunLeaseEpoch(runId, _ownerId, lease.RecoveryCount);
        _currentLeaseEpoch.Value = leaseEpoch;
        var runHub = GetOrCreateActorHub(runId);
        runHub.LiveLeaseEpoch = leaseEpoch;
        using var boardRegistration = _teamBoardMcpBridge?.RegisterRunBoardHandler(runId, HandleLiveMcpBoardCallAsync);

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
                RunFreezeGate.Validate(null, configuration.OperationAgents, configuration.ExecutionAgents, configuration.Graph);
            }
            catch (RunAdmissionException ex) when (string.Equals(ex.Code, "run_schema_superseded", StringComparison.Ordinal))
            {
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
            if (CorrectionRuntimePolicy.IsStructuredInvestigation(configuration.RuntimePolicy))
                checkpoint = await EnsureCorrectionAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
            if (chatInput is not null && checkpoint.UserGoal != chatInput.Content)
            {
                await FailRunAsync(runId, checkpoint, "tina_chat_input_locked", "The checkpoint goal no longer matches the accepted intent.", stoppingToken).ConfigureAwait(false);
                return;
            }

            if (checkpoint.ModelRetryNotBefore is { } retryNotBefore)
            {
                var now = DateTimeOffset.UtcNow;
                if (retryNotBefore > now)
                {
                    ScheduleModelRetryInMemory(runId, retryNotBefore);
                    releaseLease = !await TryRetainLeaseUntilAsync(runId, retryNotBefore, lease.RecoveryCount, stoppingToken).ConfigureAwait(false);
                    return;
                }

                var retryNumber = checkpoint.TransientModelRetryCount;
                var retryCategory = checkpoint.LastTransientModelErrorCategory;
                checkpoint.ModelRetryNotBefore = null;
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, $"model-retry-resumed-{retryNumber}", stoppingToken).ConfigureAwait(false);
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
                    _logger.TryLogWarning(ex, "Could not append the model retry resumed event for run {RunId}.", runId);
                }
            }

            var interrupted = checkpoint.Tasks
                .Where(item => item.Status == "running" && string.IsNullOrWhiteSpace(item.PendingToolExecutionId))
                .Where(item => !IsTaskPausedByTeammate(checkpoint, item))
                .ToList();
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

            if (!IsTerminal(run.Status)
                && checkpoint.Phase == "awaiting_user"
                && run.Status is not RunStatus.AwaitingUser and not RunStatus.Executing)
            {
                if (checkpoint.SupervisionDecision == "escalate" && !checkpoint.AwaitingApprovalExpiryReview)
                    await PublishSupervisionReviewAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false);
                await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review.", stoppingToken).ConfigureAwait(false);
                return;
            }
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

                if (checkpoint.AwaitingApprovalExpiryReview)
                {
                    await TrySetRunStatusAsync(run.RunId, "awaiting_user", null, stoppingToken).ConfigureAwait(false);
                    return;
                }

                if (await DrainLiveMcpBoardPostsAsync(run, configuration, checkpoint, stoppingToken).ConfigureAwait(false))
                {
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "mcp-board-drained", stoppingToken).ConfigureAwait(false);
                }

                if (await ApplyPendingContextPatchesAsync(run, checkpoint, stoppingToken).ConfigureAwait(false))
                {
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "context-patch", stoppingToken).ConfigureAwait(false);
                }

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
                        if (CorrectionRuntimePolicy.IsStructuredInvestigation(configuration.RuntimePolicy)
                            && checkpoint.Correction is not null)
                        {
                            var unanswered = checkpoint.Correction.OpenQuestions.Any(question => question.Blocking && question.AnsweredAt is null);
                            if (!unanswered)
                            {
                                var completionUnresolved = CorrectionTracePolicy.EvaluateCompletion(
                                    checkpoint.Correction,
                                    configuration.RuntimePolicy!,
                                    checkpoint.UserGoal,
                                    checkpoint.Correction.Completion.ModifiedArtifacts.Count > 0);
                                if (completionUnresolved is null)
                                {
                                    checkpoint.Correction.Status = "ready";
                                    checkpoint.Correction.Phase = "CONVERGE";
                                    checkpoint.Correction.Revision++;
                                    checkpoint.Correction.UpdatedAt = DateTimeOffset.UtcNow;
                                    checkpoint.Phase = "finalizing";
                                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "correction-finalizing", stoppingToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    checkpoint.Correction.Status = "investigating";
                                    checkpoint.Correction.Revision++;
                                    checkpoint.Correction.UpdatedAt = DateTimeOffset.UtcNow;
                                    checkpoint.Phase = "executing";
                                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "correction-resumed", stoppingToken).ConfigureAwait(false);
                                }
                                await AppendCorrectionTraceEventAsync(run, checkpoint.Correction, stoppingToken).ConfigureAwait(false);
                            }
                            break;
                        }
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

                checkpoint = await ResetTransientModelRetryAfterProgressAsync(runId, checkpoint, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (RunAwaitingExternalDecisionException)
        {
        }
        catch (RunInterruptedException interrupted) when (!stoppingToken.IsCancellationRequested)
        {
            Interrupts?.TakePending(runId);
            await AppendRunInterruptedAsync(runId, interrupted, stoppingToken).ConfigureAwait(false);
            await EnqueueAsync(runId, stoppingToken).ConfigureAwait(false);
        }
        catch (RunCheckpointConflictException)
        {
        }
        catch (Exception ex)
        {
            var run = await _lifecycle.GetRunStateAsync(runId.ToString(), CancellationToken.None).ConfigureAwait(false);
            var checkpoint = await TryReadCheckpointAsync(runId).ConfigureAwait(false);
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
                var scheduled = await TryScheduleModelRetryAsync(runId, checkpoint, modelFailure, lease.RecoveryCount).ConfigureAwait(false);
                if (scheduled.Scheduled)
                {
                    releaseLease = !scheduled.LeaseRetained;
                    return;
                }
                if (scheduled.ExitWithoutFailure) return;
            }

            if (!await TryRetainLeaseUntilAsync(runId, DateTimeOffset.UtcNow.Add(LeaseDuration), lease.RecoveryCount, CancellationToken.None).ConfigureAwait(false))
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
            _logger.TryLogError(ex, "Full-duplex run {RunId} failed in durable engine.", runId);
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
            runHub.LiveCheckpoint = null;
            runHub.LiveConfiguration = null;
            runHub.LiveRun = null;
            runHub.LiveLeaseEpoch = null;
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

        var leaseRetained = await TryRetainLeaseUntilAsync(runId, retryAt, leaseRecoveryCount, CancellationToken.None).ConfigureAwait(false);
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
                checkpoint, expectedRevision, checkpointKey, CancellationToken.None,
                expectedRunStatus: expectedRunStatus, requireCompletionUnclaimed: true).ConfigureAwait(false);
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
            try
            {
                checkpoint = await SaveCheckpointAsync(
                    checkpoint, expectedRevision, checkpointKey, CancellationToken.None,
                    expectedRunStatus: expectedRunStatus, requireCompletionUnclaimed: true).ConfigureAwait(false);
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
                        && string.Equals(observed.LastTransientModelErrorCategory, failure.Category, StringComparison.Ordinal))
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
                            "The idempotent retry checkpoint write for run {RunId} also failed; releasing the lease for recovery.",
                            runId);
                        return new ModelRetryScheduleResult(false, false, true);
                    }

                    if (observed is not null && observed.CheckpointRevision > expectedRevision)
                    {
                        return new ModelRetryScheduleResult(false, true, true);
                    }

                    _logger.TryLogWarning(retrySaveException,
                        "The retry checkpoint for run {RunId} was confirmed absent after two failed writes; failing closed.",
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
            if (await _lifecycle.HeartbeatRunLeaseAsync(runId.ToString(), _ownerId, duration, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var reacquired = await _lifecycle.TryAcquireRunLeaseAsync(runId.ToString(), _ownerId, duration, cancellationToken).ConfigureAwait(false);
            if (!reacquired.Acquired) return false;
            if (reacquired.RecoveryCount == expectedRecoveryCount + 1)
            {
                if (_currentLeaseEpoch.Value is { RunId: var epochRunId } epoch && epochRunId == runId)
                {
                    epoch.RecoveryCount = reacquired.RecoveryCount;
                }
                else
                {
                    var refreshed = new RunLeaseEpoch(runId, _ownerId, reacquired.RecoveryCount);
                    _currentLeaseEpoch.Value = refreshed;
                    if (_runActorHubs.TryGetValue(runId, out var activeHub))
                        activeHub.LiveLeaseEpoch = refreshed;
                }
                return true;
            }

            try
            {
                await _lifecycle.ReleaseRunLeaseAsync(runId.ToString(), _ownerId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception releaseException)
            {
                _logger.TryLogDebug(releaseException, "Could not release stale reacquired lease for run {RunId}.", runId);
            }
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not retain the run lease through {RetryAt}; recovery scanning remains the fallback.", retryAt);
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
            ContextRevision = Math.Max(sessionRevision, run.ContextRevision),
            PlanRevision = 0
        };
        return await SaveCheckpointAsync(initial, run.CheckpointRevision, "admitted", cancellationToken).ConfigureAwait(false);
    }

    private async Task<FullDuplexCheckpointV1> EnsureCorrectionAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Correction is not null) return checkpoint;

        CorrectionTraceState trace;
        if (string.Equals(run.RunKind, "correction_reopen", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(run.ParentRunId, out var parentRunId))
        {
            var parentCheckpoint = await _lifecycle.GetCurrentRunCheckpointAsync(parentRunId.ToString(), cancellationToken).ConfigureAwait(false);
            if (parentCheckpoint is null)
                throw new InvalidDataException("The correction episode has no parent checkpoint.");
            var parent = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(parentCheckpoint.Content, JsonOptions)
                ?? throw new InvalidDataException("The correction episode parent checkpoint is empty.");
            trace = parent.Correction is null
                ? CorrectionTracePolicy.Create(run, checkpoint.UserGoal, parentRunId)
                : CorrectionTracePolicy.Clone(parent.Correction, run);
            trace.ParentRunId = parentRunId;
        }
        else
        {
            trace = CorrectionTracePolicy.Create(run, checkpoint.UserGoal);
        }

        checkpoint.Correction = trace;
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "correction-started", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "correction.trace.updated",
            "Correction trace initialized.",
            new { run_id = run.RunId, episode_id = trace.EpisodeId, revision = trace.Revision, trace = trace.ToDto() },
            cancellationToken, idempotencyKey: $"correction-started:{run.RunId}").ConfigureAwait(false);
        return checkpoint;
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
        await using var _ = await EnterCheckpointGateAsync(checkpoint.RunId, cancellationToken).ConfigureAwait(false);
        var effectiveExpected = Math.Max(expectedRevision, checkpoint.CheckpointRevision);
        var leaseEpoch = LeaseEpochFor(checkpoint.RunId);
        var stored = await _lifecycle.SaveRunCheckpointAsync(checkpoint.RunId.ToString(), new RunCheckpointWrite(
            effectiveExpected,
            checkpoint.Phase,
            JsonSerializer.Serialize(checkpoint, JsonOptions),
            IdempotencyKey: $"run:{checkpoint.RunId}:{laneKey}:{key}:{effectiveExpected + 1}",
            ExpectedRunStatus: expectedRunStatus,
            RequireCompletionUnclaimed: requireCompletionUnclaimed,
            ExpectedLeaseOwner: leaseEpoch?.OwnerId,
            ExpectedRecoveryCount: leaseEpoch?.RecoveryCount), cancellationToken).ConfigureAwait(false);
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

    private async Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!await _lifecycle.HeartbeatRunLeaseAsync(runId.ToString(), _ownerId, LeaseDuration, cancellationToken).ConfigureAwait(false))
        {
            throw new RunCheckpointConflictException(runId.ToString(), 0, 0);
        }
    }

    private async Task<RunExecutionAuthority> RequireToolExecutionAuthorityAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var epoch = LeaseEpochFor(runId)
            ?? throw new RunCheckpointConflictException(runId.ToString(), 0, 0);
        var expectedRecoveryCount = epoch.RecoveryCount;
        if (!await TryRetainLeaseUntilAsync(runId, DateTimeOffset.UtcNow.Add(LeaseDuration), expectedRecoveryCount, cancellationToken).ConfigureAwait(false))
        {
            throw new RunCheckpointConflictException(runId.ToString(), 0, 0);
        }

        epoch = LeaseEpochFor(runId)
            ?? throw new RunCheckpointConflictException(runId.ToString(), 0, 0);
        return new RunExecutionAuthority(epoch.OwnerId, epoch.RecoveryCount);
    }

    private Task AppendEventAsync(Guid runId, string type, string summary, object payload, CancellationToken ct, Guid? taskId = null, string? idempotencyKey = null) =>
        _lifecycle.AppendEventAsync(runId, type, payload, summary, taskId: taskId, idempotencyKey: idempotencyKey, cancellationToken: ct);

    private async Task<DurableRunStreamChunk> PublishRunStreamAsync(
        string runId, DurableRunStreamAppend item, CancellationToken cancellationToken)
    {
        var chunk = await _lifecycle.AppendRunStreamAsync(runId, item, cancellationToken).ConfigureAwait(false);
        if (_projection is not null && Guid.TryParse(runId, out _))
        {
            _projection.Publish(FullDuplexRunCoordinator.ToStreamChunk(chunk));
            if (chunk.Kind is "done" or "error") _projection.Seal(Guid.Parse(runId));
        }
        return chunk;
    }

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

    private static bool HasLivePendingExecution(DurableTaskNode task) =>
        !IsTaskTerminal(task) && !string.IsNullOrWhiteSpace(task.PendingToolExecutionId);

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

    private Task SetRunStatusFencedAsync(string runId, string status, string? message, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(runId, out var parsedRunId) && LeaseEpochFor(parsedRunId) is { } epoch)
        {
            return _lifecycle.SetRunStatusUnderLeaseAsync(runId, status, message, epoch.OwnerId, epoch.RecoveryCount, cancellationToken);
        }
        return _lifecycle.SetRunStatusAsync(runId, status, message, cancellationToken);
    }

    private Task SetRunFailedFencedAsync(string runId, string message, string category, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(runId, out var parsedRunId) && LeaseEpochFor(parsedRunId) is { } epoch)
        {
            return _lifecycle.SetRunFailedUnderLeaseAsync(runId, message, category, epoch.OwnerId, epoch.RecoveryCount, cancellationToken);
        }
        return _lifecycle.SetRunStatusAsync(runId, "failed", message, cancellationToken);
    }

    private RunLeaseEpoch? LeaseEpochFor(Guid runId) =>
        _currentLeaseEpoch.Value is { RunId: var epochRunId } epoch && epochRunId == runId
            ? epoch
            : (_runActorHubs.TryGetValue(runId, out var hub) ? hub.LiveLeaseEpoch : null);

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

    internal sealed class InvalidTaskGraphException(string message) : InvalidOperationException(message);

    internal sealed class WorkerAssignmentException(string message, Exception? inner = null) : Exception(message, inner);

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
        public bool PausedByHuman { get; init; }
        public bool ParkedOnWait { get; init; }
    }

    private readonly record struct ModelRetryScheduleResult(
        bool Scheduled,
        bool LeaseRetained,
        bool ExitWithoutFailure);

    internal sealed class RunLeaseEpoch(Guid runId, string ownerId, int recoveryCount)
    {
        public Guid RunId { get; } = runId;
        public string OwnerId { get; } = ownerId;
        public int RecoveryCount { get; set; } = recoveryCount;
    }
}
