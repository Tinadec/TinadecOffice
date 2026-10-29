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
        builder.Services.AddSingleton<CompanionService>();
        builder.Services.AddSingleton<CompanionConversationService>();
        // The run loop is a host singleton: it reads the tenant from the run row, never from a
        // request-scoped accessor, because it runs outside any request.
        builder.Services.AddSingleton<CompanionRuntime>();
        builder.Services.AddSingleton<ICompanionRuntime>(sp => sp.GetRequiredService<CompanionRuntime>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CompanionRuntime>());
        // Autonomy is ticked on demand by an endpoint and on a schedule by this worker; both share
        // one service so the pause/budget/interval rules exist once.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CompanionAutonomyService>();
        builder.Services.AddSingleton<CompanionAutonomyWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CompanionAutonomyWorker>());
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.3.0",
            Dependencies = ["abstractions", "persistence", "tenancy"],
            Capabilities =
            [
                "companion_identity",
                "companion_profile",
                "companion_memory",
                "companion_skill_binding",
                "companion_connection_binding",
                "companion_settings",
                "companion_conversations",
                "companion_group_invites",
                "companion_messages",
                "companion_runs",
                "companion_activity",
                "companion_autonomy"
            ],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}
