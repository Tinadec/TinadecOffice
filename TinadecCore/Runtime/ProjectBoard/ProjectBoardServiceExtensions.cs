using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime.ProjectBoard;

/// <summary>
/// DI registration extensions for the single-project and cross-project board subsystem.
/// </summary>
public static class ProjectBoardServiceExtensions
{
    public static IServiceCollection AddTinadecProjectBoard(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        _ = configuration;
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContextFactory<ProjectBoardDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStorageMigrationParticipant, ProjectBoardMigrationParticipant>());
        services.TryAddSingleton<ProjectBoardCoordinator>();
        services.TryAddSingleton<ProjectBoardFileSyncService>();
        services.TryAddSingleton<IProjectBoardSyncHook>(sp => sp.GetRequiredService<ProjectBoardFileSyncService>());
        services.AddHostedService(sp => sp.GetRequiredService<ProjectBoardFileSyncService>());
        services.TryAddSingleton<IBoardCliHarnessExecutor, CatalogBoardCliHarnessExecutor>();
        services.TryAddSingleton<ProjectTaskExecutionService>();
        return services;
    }
}
