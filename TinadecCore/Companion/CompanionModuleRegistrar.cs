using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Companion;

public sealed class CompanionModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "companion";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<CompanionDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<CompanionDbContext>>();
        builder.Services.AddSingleton<CompanionStreamBroker>();
        builder.Services.AddSingleton<ICompanionStreamBroker>(sp => sp.GetRequiredService<CompanionStreamBroker>());
        builder.Services.AddSingleton<CompanionSessionBackflowService>();
        builder.Services.AddSingleton<ICompanionSessionBackflow>(sp => sp.GetRequiredService<CompanionSessionBackflowService>());
        builder.Services.AddSingleton<CompanionService>();
        builder.Services.AddSingleton<CompanionAvatarStore>();
        builder.Services.AddSingleton<CompanionRuntime>();
        builder.Services.AddSingleton<ICompanionRuntime>(sp => sp.GetRequiredService<CompanionRuntime>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CompanionRuntime>());
        builder.Services.AddSingleton<CompanionChannelService>();
        builder.Services.AddSingleton<ICompanionChannel>(sp => sp.GetRequiredService<CompanionChannelService>());
        builder.Services.AddSingleton<CompanionConversationService>();
        builder.Services.AddSingleton<CompanionPlanningService>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CompanionAutonomyService>();
        builder.Services.AddSingleton<CompanionAutonomyWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CompanionAutonomyWorker>());
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.5.0",
            Dependencies = ["abstractions", "persistence", "tenancy"],
            Capabilities =
            [
                "companion_identity",
                "companion_profile",
                "companion_memory",
                "companion_skill_binding",
                "companion_connection_binding",
                "companion_settings",
                "companion_channel",
                "companion_conversations",
                "companion_group_invites",
                "companion_messages",
                "companion_runs",
                "companion_streaming",
                "companion_activity",
                "companion_autonomy",
                "companion_schedules",
                "companion_plans",
                "companion_todos",
                "companion_avatars",
                "companion_live2d_sdk",
                "companion_work_session_connector",
                "companion_session_backflow",
                "companion_browser_connector"
            ],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}
