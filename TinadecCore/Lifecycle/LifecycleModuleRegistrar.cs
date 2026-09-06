using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Lifecycle;

/// <summary>
/// Lifecycle module registrar. Registers lifecycle manager for run/task/agent state.
/// </summary>
public sealed class LifecycleModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "lifecycle";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<LifecycleDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<StorageDiagnostics>();
        builder.Services.AddSingleton<StorageLifecycleService>();
        builder.Services.AddSingleton<IWorkspaceSnapshotProvider, GitWorkspaceSnapshotProvider>();
        builder.Services.AddSingleton<IWorkspaceSnapshotProvider, FileSystemWorkspaceSnapshotProvider>();
        builder.Services.AddSingleton<IWorkspaceSnapshotService, WorkspaceSnapshotService>();
        builder.Services.AddSingleton<IStorageMigrationParticipant>(sp => sp.GetRequiredService<StorageLifecycleService>());
        builder.Services.AddSingleton<ILifecycleManager, LifecycleManager>();
        // Startup orphan recovery moved to Runtime.RecoveryCoordinator (plan §4.3
        // item 5), which runs the pass under the shared RecoveryPolicy awaiting-*
        // protection; the engine lease scan consumes the same policy in steady state.
        builder.Services.AddOptions<TinadecApprovalOptions>().BindConfiguration(TinadecApprovalOptions.SectionName);
        builder.Services.AddSingleton<ToolApprovalCoordinator>();
        builder.Services.AddSingleton<IToolApprovalCoordinator>(sp => sp.GetRequiredService<ToolApprovalCoordinator>());
        builder.Services.AddSingleton<IToolExecutionCoordinator>(sp => sp.GetRequiredService<ToolApprovalCoordinator>());
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies"],
            Capabilities = ["run_management", "task_state", "agent_state", "tool_state", "approval_state", "audit_events"],
            Language = "C#",
            MafPrimitives = ["session", "checkpoint"],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}

/// <summary>
/// In-memory lifecycle manager for skeleton state.
/// Core owns run/task/agent/tool/approval state and audit events.
/// MAF session/checkpoint is execution runtime state only.
/// </summary>
internal sealed class LifecycleManager : ILifecycleManager
{
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, RunState> _fallbackRuns = new();

    public LifecycleManager(IServiceProvider services) => _services = services;

    public async Task<string> StartRunAsync(
        string sessionId,
        string? triggerMessageId = null,
        CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is not null && Guid.TryParse(sessionId, out var parsedSessionId))
        {
            var mid = Guid.TryParse(triggerMessageId, out var parsedMid) ? parsedMid : Guid.NewGuid();
            var run = await storage.StartRunAsync(parsedSessionId, mid, cancellationToken).ConfigureAwait(false);
            return run.Id.ToString();
        }

        var runId = Guid.NewGuid().ToString("N");
        _fallbackRuns[runId] = new RunState { RunId = runId, SessionId = sessionId, Status = RunStatus.Planning, StartedAt = DateTimeOffset.UtcNow };
        return runId;
    }

    public async Task<string> StartRunAsync(RunStartRequest request, CancellationToken cancellationToken = default) =>
        (await StartOrGetRunAsync(request, cancellationToken).ConfigureAwait(false)).RunId;

    public async Task<RunStartResult> StartOrGetRunAsync(RunStartRequest request, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is not null && Guid.TryParse(request.SessionId, out var sessionId) && Guid.TryParse(request.TriggerMessageId, out var messageId))
        {
            var result = await storage.StartOrGetRunAsync(sessionId, messageId, new RunStartOptions(
                Guid.TryParse(request.TurnId, out var turnId) ? turnId : null,
                request.ContextRevision, request.ConfigurationVersion, request.ConfigurationHash,
                request.ApplicationMode, request.AgentMode, request.PermissionMode, request.RuntimeProfileId,
                request.InitiatedByPrincipalId), cancellationToken).ConfigureAwait(false);
            return new RunStartResult(result.Run.Id.ToString(), result.Existing);
        }
        var runId = await StartRunAsync(request.SessionId, request.TriggerMessageId, cancellationToken).ConfigureAwait(false);
        return new RunStartResult(runId, Existing: false);
    }

    public async Task<RunState?> FindRunByTriggerMessageAsync(string sessionId, string triggerMessageId, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(sessionId, out var parsedSessionId) || !Guid.TryParse(triggerMessageId, out var parsedTriggerMessageId)) return null;
        var run = await storage.FindRunByTriggerMessageAsync(parsedSessionId, parsedTriggerMessageId, cancellationToken).ConfigureAwait(false);
        return run is null ? null : ToRunState(run);
    }

    public async Task<long> AppendEventAsync(
        Guid runId,
        string eventType,
        object? payload,
        string summary,
        string severity = "info",
        Guid? taskId = null,
        Guid? approvalId = null,
        string? toolId = null,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        var storage = TryStorage();
        if (storage is null) return 0L;
        var index = await storage.AppendEventAsync(runId, eventType, payload, summary, severity, taskId: taskId, approvalId: approvalId, toolId: toolId, idempotencyKey: idempotencyKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        return index.Sequence;
    }

    public Task<string> StartToolExecutionAsync(ToolExecutionStart start, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage() ?? throw new InvalidOperationException("Tool execution persistence requires relational storage.");
        return StartToolExecutionCoreAsync(storage, start, cancellationToken);
    }

    private static async Task<string> StartToolExecutionCoreAsync(StorageLifecycleService storage, ToolExecutionStart start, CancellationToken cancellationToken)
    {
        var record = await storage.StartToolExecutionAsync(start, cancellationToken).ConfigureAwait(false);
        return record.Id.ToString();
    }

    public Task CompleteToolExecutionAsync(string executionId, ToolExecutionCompletion completion, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(executionId, out var id)) return Task.CompletedTask;
        return storage.CompleteToolExecutionAsync(id, completion, cancellationToken);
    }

    public Task FailToolExecutionAsync(string executionId, string errorCategory, string safeMessage, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(executionId, out var id)) return Task.CompletedTask;
        return storage.FailToolExecutionAsync(id, errorCategory, safeMessage, cancellationToken);
    }

    public async Task<IReadOnlyList<RunState>> ListNonTerminalRunsAsync(CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null) return [];
        var runs = await storage.ListNonTerminalRunsAsync(cancellationToken).ConfigureAwait(false);
        return runs.Select(ToRunState).ToList();
    }

    public async Task<IReadOnlyList<RunState>> ListLeaseEligibleRunsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null) return [];
        var runs = await storage.ListLeaseEligibleRunsAsync(now, cancellationToken).ConfigureAwait(false);
        return runs.Select(ToRunState).ToList();
    }

    public async Task<FrozenRunConfiguration> FreezeRunConfigurationAsync(string runId, FrozenRunConfigurationWrite configuration, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage() ?? throw new InvalidOperationException("Frozen configuration persistence requires relational storage.");
        if (!Guid.TryParse(runId, out var id)) throw new ArgumentException("Run id must be a valid Guid.", nameof(runId));
        return await storage.FreezeRunConfigurationAsync(id, configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FrozenRunConfiguration?> GetFrozenRunConfigurationAsync(string runId, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(runId, out var id)) return null;
        return await storage.GetFrozenRunConfigurationAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunCheckpoint> SaveRunCheckpointAsync(string runId, RunCheckpointWrite checkpoint, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage() ?? throw new InvalidOperationException("Run checkpoint persistence requires relational storage.");
        if (!Guid.TryParse(runId, out var id)) throw new ArgumentException("Run id must be a valid Guid.", nameof(runId));
        return await storage.SaveRunCheckpointAsync(id, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunCheckpoint?> GetCurrentRunCheckpointAsync(string runId, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(runId, out var id)) return null;
        return await storage.GetCurrentRunCheckpointAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunDirective>> ListPendingRunDirectivesAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null) return [];
        return await storage.ListPendingRunDirectivesAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunDirectiveDrainResult> DrainRunDirectivesAsync(Guid runId, IReadOnlyList<Guid> directiveIds, string drainedStatus, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null) return new RunDirectiveDrainResult(0);
        return await storage.DrainRunDirectivesAsync(runId, directiveIds, drainedStatus, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunDirective> EnqueueRunDirectiveAsync(RunDirectiveWrite write, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage() ?? throw new InvalidOperationException("Run directive persistence requires relational storage.");
        return await storage.EnqueueRunDirectiveAsync(write, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunLease> TryAcquireRunLeaseAsync(string runId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage() ?? throw new InvalidOperationException("Run lease persistence requires relational storage.");
        if (!Guid.TryParse(runId, out var id)) throw new ArgumentException("Run id must be a valid Guid.", nameof(runId));
        return await storage.TryAcquireRunLeaseAsync(id, ownerId, duration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HeartbeatRunLeaseAsync(string runId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(runId, out var id)) return false;
        return await storage.HeartbeatRunLeaseAsync(id, ownerId, duration, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseRunLeaseAsync(string runId, string ownerId, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(runId, out var id)) return;
        await storage.ReleaseRunLeaseAsync(id, ownerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateTaskSnapshotAsync(
        Guid runId,
        object taskNode,
        CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null) return;
        await storage.UpdateTaskAsync(runId, taskNode, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is not null && Guid.TryParse(runId, out var id))
        {
            await storage.CompleteRunAsync(id, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_fallbackRuns.TryGetValue(runId, out var state)) _fallbackRuns[runId] = state with { Status = RunStatus.Completed, CompletedAt = DateTimeOffset.UtcNow };
    }

    public async Task SetRunStatusAsync(string runId, string status, string? summary = null, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is not null && Guid.TryParse(runId, out var id))
        {
            await storage.SetRunStatusAsync(id, status, summary, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_fallbackRuns.TryGetValue(runId, out var state) && Enum.TryParse<RunStatus>(status, true, out var parsed))
            _fallbackRuns[runId] = state with { Status = parsed, CompletedAt = parsed is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled ? DateTimeOffset.UtcNow : null };
    }

    public async Task<int> CountActiveRunsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        return storage is not null && Guid.TryParse(sessionId, out var id)
            ? await storage.CountActiveRunsAsync(id, cancellationToken).ConfigureAwait(false)
            : _fallbackRuns.Values.Count(x => x.SessionId == sessionId && x.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled));
    }

    public async Task<RunState> GetRunStateAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(runId, out var id))
        {
            return _fallbackRuns.TryGetValue(runId, out var fallback) ? fallback : new RunState { RunId = runId, Status = RunStatus.Planning };
        }
        var run = await storage.FindRunAsync(id, cancellationToken).ConfigureAwait(false);
        return run is null
            ? new RunState { RunId = runId, Status = RunStatus.Planning }
            : ToRunState(run);
    }

    public async Task AdvanceRunContextRevisionAsync(string runId, long contextRevision, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is not null && Guid.TryParse(runId, out var id))
        {
            await storage.AdvanceRunContextRevisionAsync(id, contextRevision, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_fallbackRuns.TryGetValue(runId, out var state) && contextRevision > state.ContextRevision)
        {
            _fallbackRuns[runId] = state with { ContextRevision = contextRevision };
        }
    }

    public async Task<IReadOnlyList<TinadecCore.Contracts.Events.EventEnvelope>> ReplayEventsAsync(
        Guid? sessionId,
        long afterSequence,
        CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        return storage is null ? [] : await storage.ReplayEventsAsync(sessionId, afterSequence, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableRunStreamChunk> AppendRunStreamAsync(string runId, DurableRunStreamAppend item, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage() ?? throw new InvalidOperationException("Run stream persistence requires relational storage.");
        if (!Guid.TryParse(runId, out var id)) throw new ArgumentException("Run id must be a valid Guid.", nameof(runId));
        return await storage.AppendRunStreamAsync(id, item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DurableRunStreamChunk>> ReplayRunStreamAsync(string runId, Guid? turnId, long afterSequence, CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(runId, out var id)) return [];
        return await storage.ReplayRunStreamAsync(id, turnId, afterSequence, cancellationToken).ConfigureAwait(false);
    }

    private static RunState ToRunState(RunRecord run) => new()
    {
        RunId = run.Id.ToString(),
        SessionId = run.SessionId.ToString(),
        TriggerMessageId = run.TriggerMessageId.ToString(),
        TurnId = run.TurnId?.ToString(),
        Status = ParseRunStatus(run.Status),
        ContextRevision = run.ContextRevision,
        ConfigurationVersion = run.ConfigurationVersion,
        ConfigurationHash = run.ConfigurationHash,
        ApplicationMode = run.ApplicationMode,
        AgentMode = run.AgentMode,
        PermissionMode = run.PermissionMode,
        RuntimeProfileId = run.RuntimeProfileId,
        TenantId = run.TenantId.ToString(),
        WorkspaceId = run.WorkspaceId.ToString(),
        InitiatedByPrincipalId = run.InitiatedByPrincipalId == Guid.Empty ? null : run.InitiatedByPrincipalId.ToString(),
        CheckpointRevision = run.CheckpointRevision,
        FrozenConfigurationHash = run.FrozenConfigurationHash,
        LeaseOwner = run.LeaseOwner,
        LeaseExpiresAt = run.LeaseExpiresAt,
        LeaseHeartbeatAt = run.LeaseHeartbeatAt,
        RecoveryCount = run.RecoveryCount,
        Summary = run.Summary,
        StartedAt = run.CreatedAt,
        CompletedAt = run.CompletedAt
    };

    private StorageLifecycleService? TryStorage()
    {
        try { return _services.GetService<StorageLifecycleService>(); }
        catch (InvalidOperationException) { return null; }
    }

    private static RunStatus ParseRunStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "planning" => RunStatus.Planning,
        "understanding" => RunStatus.Understanding,
        "executing" => RunStatus.Executing,
        "replanning" => RunStatus.Replanning,
        "awaiting_approval" => RunStatus.AwaitingApproval,
        "awaiting_delegate" => RunStatus.AwaitingDelegate,
        "awaiting_user" => RunStatus.AwaitingUser,
        "paused" => RunStatus.Paused,
        "reviewing" => RunStatus.Reviewing,
        "completed" => RunStatus.Completed,
        "failed" => RunStatus.Failed,
        "cancelled" => RunStatus.Cancelled,
        _ => RunStatus.Planning
    };
}
