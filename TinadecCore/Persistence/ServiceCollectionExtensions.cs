using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TinadecCore.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared database abstraction (options, connection info, EF configurer, readiness).
    /// Does not register business DbContexts, create tables, or run migrations.
    /// </summary>
    public static IServiceCollection AddTinadecPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TinadecPersistenceOptions>()
            .Bind(configuration.GetSection(TinadecPersistenceOptions.SectionName))
            .Validate(
                o => o.ProbeTimeoutSeconds is >= 1 and <= 30,
                "TinadecPersistence:ProbeTimeoutSeconds must be between 1 and 30.")
            .ValidateOnStart();

        var root = contentRootPath ?? Directory.GetCurrentDirectory();

        services.TryAddSingleton<IDatabaseConnectionInfo>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TinadecPersistenceOptions>>().Value;
            return ResolveConnectionInfo(options, configuration, root);
        });

        services.TryAddSingleton<ITinadecDatabaseConfigurer, TinadecDatabaseConfigurer>();
        services.TryAddSingleton<IDatabaseReadiness, DatabaseReadiness>();
        services.TryAddSingleton<StoragePaths>(sp =>
            new StoragePaths(root, sp.GetRequiredService<IOptions<TinadecPersistenceOptions>>()));
        services.TryAddSingleton<IContentStore, LocalFileContentStore>();
        services.TryAddSingleton<ISecretStore, EnvironmentSecretStore>();
        services.TryAddSingleton<IStorageMigrationRunner, StorageMigrationRunner>();
        return services;
    }

    /// <summary>
    /// Applies the active Tinadec database provider to a module DbContext registration.
    /// </summary>
    public static DbContextOptionsBuilder UseTinadecDatabase(
        this DbContextOptionsBuilder options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        serviceProvider.GetRequiredService<ITinadecDatabaseConfigurer>().Configure(options);
        return options;
    }

    internal static DatabaseConnectionInfo ResolveConnectionInfo(
        TinadecPersistenceOptions options,
        IConfiguration configuration,
        string contentRootPath)
    {
        if (!options.Enabled)
        {
            return new DatabaseConnectionInfo(options.Provider, null);
        }

        return options.Provider switch
        {
            DatabaseProvider.PostgreSql => ResolvePostgreSql(options, configuration),
            _ => ResolveSqlite(options, contentRootPath)
        };
    }

    private static DatabaseConnectionInfo ResolveSqlite(
        TinadecPersistenceOptions options,
        string contentRootPath)
    {
        var path = options.Sqlite.DatabasePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DatabaseConnectionInfo(DatabaseProvider.Sqlite, null);
        }

        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(contentRootPath, path));

        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = fullPath
        }.ToString();

        return new DatabaseConnectionInfo(DatabaseProvider.Sqlite, connectionString);
    }

    private static DatabaseConnectionInfo ResolvePostgreSql(
        TinadecPersistenceOptions options,
        IConfiguration configuration)
    {
        var name = options.PostgreSql.ConnectionStringName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return new DatabaseConnectionInfo(DatabaseProvider.PostgreSql, null);
        }

        var connectionString = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new DatabaseConnectionInfo(DatabaseProvider.PostgreSql, null);
        }

        return new DatabaseConnectionInfo(DatabaseProvider.PostgreSql, connectionString);
    }
}
