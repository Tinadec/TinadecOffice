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
        builder.Services.AddSingleton<IStorageMigrationParticipant>(sp => sp.GetRequiredService<StorageLifecycleService>());
        builder.Services.AddSingleton<ILifecycleManager, LifecycleManager>();
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
        _fallbackRuns[runId] = new RunState { RunId = runId, SessionId = sessionId, Status = RunStatus.Running, StartedAt = DateTimeOffset.UtcNow };
        return runId;
    }

    public async Task<long> AppendEventAsync(
        Guid runId,
        string eventType,
        object? payload,
        string summary,
        string severity = "info",
        Guid? taskId = null,
        CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null) return 0L;
        var index = await storage.AppendEventAsync(runId, eventType, payload, summary, severity, taskId: taskId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return index.Sequence;
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

    public async Task<RunState> GetRunStateAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var storage = TryStorage();
        if (storage is null || !Guid.TryParse(runId, out var id))
        {
            return _fallbackRuns.TryGetValue(runId, out var fallback) ? fallback : new RunState { RunId = runId, Status = RunStatus.Pending };
        }
        var run = await storage.FindRunAsync(id, cancellationToken).ConfigureAwait(false);
        return run is null
            ? new RunState { RunId = runId, Status = RunStatus.Pending }
            : new RunState { RunId = run.Id.ToString(), SessionId = run.SessionId.ToString(), Status = Enum.TryParse<RunStatus>(run.Status, true, out var status) ? status : RunStatus.Pending, StartedAt = run.CreatedAt, CompletedAt = run.CompletedAt };
    }

    private StorageLifecycleService? TryStorage()
    {
        try { return _services.GetService<StorageLifecycleService>(); }
        catch (InvalidOperationException) { return null; }
    }
}
