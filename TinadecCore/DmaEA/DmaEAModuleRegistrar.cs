using Microsoft.Extensions.DependencyInjection;
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
        Maf18RuntimeAdapter.EnsureCompatible();
        builder.Services.AddDbContextFactory<AgentControlDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<AgentControlDbContext>>();
        builder.Services.AddSingleton<AgentRuntimeConfigurationStore>();
        builder.Services.AddSingleton<IAgentRuntimeConfiguration>(sp => sp.GetRequiredService<AgentRuntimeConfigurationStore>());
        builder.Services.AddSingleton<IAgentRuntimeConfigurationResolver, AgentRuntimeConfigurationResolver>();
        builder.Services.AddSingleton<IRuntimeContextSettings, RuntimeContextSettingsAdapter>();
        builder.Services.AddSingleton<IAgentChatClientFactory, AgentChatClientFactory>();
        builder.Services.AddSingleton<CliRuntime.CliProcessManager>();
        builder.Services.AddSingleton<CliRuntime.ICliProcessManager>(sp => sp.GetRequiredService<CliRuntime.CliProcessManager>());
        builder.Services.AddSingleton<AgentInstanceService>();
        builder.Services.AddSingleton<IAgentInstanceService>(sp => sp.GetRequiredService<AgentInstanceService>());
        builder.Services.AddSingleton<IAgentToolAuthorization>(sp => sp.GetRequiredService<AgentInstanceService>());
        builder.Services.AddSingleton<FullDuplexRunEngine>();
        builder.Services.AddSingleton<IFullDuplexRunEngine>(sp => sp.GetRequiredService<FullDuplexRunEngine>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FullDuplexRunEngine>());
        builder.Services.AddSingleton<IFullDuplexRunCoordinator, FullDuplexRunCoordinator>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence", "lifecycle", "models", "memory", "context", "prompts", "loop_guard", "tools"],
            Capabilities = ["dual_layer_orchestration", "task_dispatch", "collaboration", "scheduling", "result_aggregation"],
            Language = "C#",
            MafPrimitives = ["agent", "workflow"],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}
