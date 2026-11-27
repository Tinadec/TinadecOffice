using Microsoft.Extensions.Logging;

namespace TinadecCore.Persistence;

/// <summary>Implemented by business modules; Persistence coordinates but owns no schema.</summary>
public interface IStorageMigrationParticipant
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
}

public interface IStorageMigrationRunner
{
    Task RunAsync(CancellationToken cancellationToken = default);
}

internal sealed class StorageMigrationRunner : IStorageMigrationRunner
{
    private readonly IEnumerable<IStorageMigrationParticipant> _participants;
    private readonly Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions> _options;
    private readonly ILogger<StorageMigrationRunner> _logger;
    private readonly IDatabaseConnectionInfo _connection;

    public StorageMigrationRunner(
        IEnumerable<IStorageMigrationParticipant> participants,
        Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions> options,
        ILogger<StorageMigrationRunner> logger,
        IDatabaseConnectionInfo connection)
    {
        _participants = participants;
        _options = options;
        _logger = logger;
        _connection = connection;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled || (options.Provider == DatabaseProvider.PostgreSql && !options.ApplyMigrationsOnStartup))
        {
            return;
        }

        var participants = _participants.ToList();
        IDisposable? sqliteBatch = null;
        if (options.Provider == DatabaseProvider.Sqlite && _connection.IsConfigured && !string.IsNullOrWhiteSpace(_connection.ConnectionString))
        {
            await DbContextSchemaBootstrapper.ClearStaleSqliteMigrationsLockAsync(
                _connection.ConnectionString,
                options.Sqlite.BusyTimeoutMs,
                options.Sqlite.EnableWal,
                _logger,
                cancellationToken).ConfigureAwait(false);
            sqliteBatch = DbContextSchemaBootstrapper.BeginSqliteBatchSession(_connection.ConnectionString, lockAlreadyCleared: true);
        }
        try
        {
            if (options.Provider == DatabaseProvider.PostgreSql && options.PostgreSql.Schema is { Length: > 0 } schema)
            {
                if (schema.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                    throw new InvalidOperationException("Invalid host-owned PostgreSQL schema.");
                // All participants share a host-minted search_path. Create it before EF's history table.
                await using var connection = new Npgsql.NpgsqlConnection(_connection.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new Npgsql.NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS \"{schema}\"", connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var participant in participants)
            {
                await participant.MigrateAsync(cancellationToken).ConfigureAwait(false);
                if (participant is not IDbContextSchemaMigrationParticipant)
                {
                    DbContextSchemaBootstrapper.InvalidateCurrentSqliteBatchSession();
                }
            }
            _logger.LogInformation("Storage migrations ensured for {Count} context(s) ({Provider}).", participants.Count, options.Provider);
        }
        finally
        {
            sqliteBatch?.Dispose();
        }
    }
}
