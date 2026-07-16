using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TinadecCore.Persistence;

internal sealed class TinadecDatabaseConfigurer : ITinadecDatabaseConfigurer
{
    private readonly IDatabaseConnectionInfo _connectionInfo;
    private readonly TinadecPersistenceOptions _options;

    public TinadecDatabaseConfigurer(
        IDatabaseConnectionInfo connectionInfo,
        IOptions<TinadecPersistenceOptions> options)
    {
        _connectionInfo = connectionInfo;
        _options = options.Value;
    }

    public void Configure(DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "TinadecPersistence is disabled. Set TinadecPersistence:Enabled=true to use the database.");
        }

        if (!_connectionInfo.IsConfigured || string.IsNullOrWhiteSpace(_connectionInfo.ConnectionString))
        {
            throw new InvalidOperationException(
                _connectionInfo.Provider == DatabaseProvider.PostgreSql
                    ? "PostgreSQL is not configured. Set ConnectionStrings:TinadecCore (or the configured name)."
                    : "SQLite is not configured. Set TinadecPersistence:Sqlite:DatabasePath.");
        }

        switch (_connectionInfo.Provider)
        {
            case DatabaseProvider.PostgreSql:
                options.UseNpgsql(_connectionInfo.ConnectionString, db => db.MigrationsAssembly("TinadecCore.Storage.Migrations.PostgreSql"));
                break;
            default:
                options.UseSqlite(_connectionInfo.ConnectionString, db => db.MigrationsAssembly("TinadecCore.Storage.Migrations.Sqlite"));
                break;
        }
    }
}
