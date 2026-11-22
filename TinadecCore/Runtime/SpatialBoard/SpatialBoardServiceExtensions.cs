using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime.SpatialBoard;

/// <summary>
/// DI registration extensions for the multiplayer spatial board runtime subsystem.
/// </summary>
public static class SpatialBoardServiceExtensions
{
    public static IServiceCollection AddTinadecSpatialBoard(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        _ = configuration;
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContextFactory<SpatialBoardDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStorageMigrationParticipant, SpatialBoardMigrationParticipant>());
        services.TryAddSingleton<SpatialPresenceHub>();
        services.TryAddSingleton<SpatialBoardCoordinator>();
        return services;
    }
}
