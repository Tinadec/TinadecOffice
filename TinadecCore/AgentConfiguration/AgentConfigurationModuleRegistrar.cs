using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Persistence;

namespace TinadecCore.AgentConfiguration;

public sealed class AgentConfigurationModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "agent_configuration";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<AgentConfigurationDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<AgentConfigurationDbContext>>();
        builder.Services.AddSingleton<AgentConfigurationService>();
        builder.Services.AddSingleton<IAgentConfigurationService>(sp => sp.GetRequiredService<AgentConfigurationService>());
        builder.Services.AddSingleton<AgentPackService>();
        builder.Services.AddSingleton<IAgentPackService>(sp => sp.GetRequiredService<AgentPackService>());
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence", "tenancy"],
            Capabilities = ["agent_definition", "agent_version", "agent_mode", "prompt_pipeline", "workspace_defaults", "draft_publish", "agent_pack_lifecycle", "versioned_agent_configuration", "frozen_agent_version_binding"],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}
