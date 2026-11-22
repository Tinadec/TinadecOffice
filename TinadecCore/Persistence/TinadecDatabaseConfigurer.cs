using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore.Infrastructure;

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
                var connection = new Npgsql.NpgsqlConnectionStringBuilder(_connectionInfo.ConnectionString);
                if (_options.PostgreSql.Schema is { Length: > 0 } schema)
                {
                    if (schema.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                        throw new InvalidOperationException("Invalid host-owned PostgreSQL schema.");
                    connection.SearchPath = schema;
                }
                options.ReplaceService<IModelCacheKeyFactory, ScopeModelCacheKeyFactory>();
                options.UseNpgsql(connection.ConnectionString, db =>
                {
                    db.MigrationsAssembly("TinadecCore.Storage.Migrations.PostgreSql");
                    if (_options.PostgreSql.Schema is { } historySchema)
                        db.MigrationsHistoryTable("__EFMigrationsHistory", historySchema);
                    db.UseVector();
                });
                break;
            default:
                var commandTimeoutSeconds = Math.Clamp((_options.Sqlite.BusyTimeoutMs + 999) / 1000, 15, 120);
                options.UseSqlite(_connectionInfo.ConnectionString, db =>
                {
                    db.MigrationsAssembly("TinadecCore.Storage.Migrations.Sqlite");
                    db.CommandTimeout(commandTimeoutSeconds);
                });
                options.AddInterceptors(new SqliteConnectionPragmaInterceptor(_options.Sqlite.BusyTimeoutMs, _options.Sqlite.EnableWal));
                break;
        }
    }
}
