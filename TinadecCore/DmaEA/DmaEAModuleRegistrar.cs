using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.DmaEA;

/// <summary>
/// DmaEA module registrar. Registers the dual-layer agent orchestrator.
/// </summary>
public sealed class DmaEAModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "dma_ea";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<AgentControlDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<AgentControlDbContext>>();
        builder.Services.AddSingleton<IAgentOrchestrator, DualLayerAgentOrchestrator>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence"],
            Capabilities = ["dual_layer_orchestration", "task_dispatch", "collaboration", "scheduling", "result_aggregation"],
            Language = "C#",
            MafPrimitives = ["agent", "workflow"],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Skeleton dual-layer agent orchestrator.
/// Planning layer: proactive planning and supervision.
/// Execution layer: passive task execution.
/// </summary>
internal sealed class DualLayerAgentOrchestrator : IAgentOrchestrator
{
    public Task<OrchestrationResult> OrchestrateAsync(
        string sessionId,
        string userGoal,
        CancellationToken cancellationToken = default)
    {
        // Skeleton: not implemented in this round.
        var result = new OrchestrationResult
        {
            RunId = Guid.NewGuid().ToString("N"),
            Success = false,
            Summary = "DmaEA orchestrator is not configured. Skeleton only.",
            Warnings = ["dma_ea module is in skeleton state — no agents are registered."]
        };
        return Task.FromResult(result);
    }
}
