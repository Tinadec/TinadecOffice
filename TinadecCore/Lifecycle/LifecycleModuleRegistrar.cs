using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Lifecycle;

/// <summary>
/// Lifecycle module registrar. Registers lifecycle manager for run/task/agent state.
/// </summary>
public sealed class LifecycleModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "lifecycle";

    public void Register(ITinadecCoreBuilder builder)
    {
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
    private readonly ConcurrentDictionary<string, RunState> _runs = new();

    public Task<string> StartRunAsync(
        string sessionId,
        string? parentRunId = null,
        CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        var state = new RunState
        {
            RunId = runId,
            SessionId = sessionId,
            Status = RunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
        _runs[runId] = state;
        return Task.FromResult(runId);
    }

    public Task CompleteRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (_runs.TryGetValue(runId, out var state))
        {
            _runs[runId] = state with { Status = RunStatus.Completed, CompletedAt = DateTimeOffset.UtcNow };
        }
        return Task.CompletedTask;
    }

    public Task<RunState> GetRunStateAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        _runs.TryGetValue(runId, out var state);
        return Task.FromResult(state ?? new RunState { RunId = runId, Status = RunStatus.Pending });
    }
}
