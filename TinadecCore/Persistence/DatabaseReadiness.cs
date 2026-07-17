using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace TinadecCore.Persistence;

internal sealed class DatabaseReadiness : IDatabaseReadiness
{
    private readonly IDatabaseConnectionInfo _connectionInfo;
    private readonly TinadecPersistenceOptions _options;
    private readonly ILogger<DatabaseReadiness> _logger;

    public DatabaseReadiness(
        IDatabaseConnectionInfo connectionInfo,
        IOptions<TinadecPersistenceOptions> options,
        ILogger<DatabaseReadiness> logger)
    {
        _connectionInfo = connectionInfo;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DatabaseReadinessResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var providerName = _connectionInfo.ProviderName;

        if (!_options.Enabled || !_connectionInfo.IsConfigured ||
            string.IsNullOrWhiteSpace(_connectionInfo.ConnectionString))
        {
            return new DatabaseReadinessResult
            {
                Provider = providerName,
                State = DatabaseReadinessState.NotConfigured,
                Detail = "Database is disabled or connection settings are incomplete."
            };
        }

        var timeoutSeconds = Math.Clamp(_options.ProbeTimeoutSeconds, 1, 30);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            if (_connectionInfo.Provider == DatabaseProvider.PostgreSql)
            {
                await ProbePostgreSqlAsync(_connectionInfo.ConnectionString, timeoutSeconds, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                await ProbeSqliteAsync(_connectionInfo.ConnectionString, timeoutCts.Token)
                    .ConfigureAwait(false);
            }

            return new DatabaseReadinessResult
            {
                Provider = providerName,
                State = DatabaseReadinessState.Ready,
                Detail = "SELECT 1 succeeded."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Database readiness probe failed for {Provider}.", providerName);
            return new DatabaseReadinessResult
            {
                Provider = providerName,
                State = DatabaseReadinessState.Unavailable,
                Detail = ex.Message
            };
        }
    }

    private static async Task ProbeSqliteAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (!string.IsNullOrWhiteSpace(dataSource) &&
            !string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!IsOne(result))
        {
            throw new InvalidOperationException($"Unexpected SELECT 1 result: {result}");
        }
    }

    private static async Task ProbePostgreSqlAsync(
        string connectionString,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT 1", connection)
        {
            CommandTimeout = timeoutSeconds
        };
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!IsOne(result))
        {
            throw new InvalidOperationException($"Unexpected SELECT 1 result: {result}");
        }
    }

    private static bool IsOne(object? value) =>
        value is int i && i == 1 ||
        value is long l && l == 1 ||
        value is string s && s == "1";
}
