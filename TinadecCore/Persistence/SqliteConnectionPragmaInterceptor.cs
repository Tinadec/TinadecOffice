using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SQLitePCL;

namespace TinadecCore.Persistence;

/// <summary>
/// Configures SQLite connections for multi-reader / multi-context concurrency on open:
/// <list type="bullet">
/// <item><c>PRAGMA busy_timeout</c> prevents immediate <c>SQLITE_BUSY (Error 5)</c> when another connection holds a write transaction.</item>
/// <item><c>PRAGMA journal_mode = WAL</c> + <c>PRAGMA synchronous = NORMAL</c> allow readers and writers to proceed concurrently even before EF's first write transaction.</item>
/// </list>
/// </summary>
public sealed class SqliteConnectionPragmaInterceptor : DbConnectionInterceptor
{
    public const int DefaultBusyTimeoutMs = 15_000;

    private sealed record AppliedPragmaState(int BusyTimeoutMs, bool EnableWal);

    private static readonly ConcurrentDictionary<(string Path, long CreationTicks), byte> WalInitializedFiles = new();
    private static readonly ConditionalWeakTable<sqlite3, AppliedPragmaState> ConfiguredHandles = new();

    private readonly int _busyTimeoutMs;
    private readonly bool _enableWal;

    public SqliteConnectionPragmaInterceptor(int busyTimeoutMs = DefaultBusyTimeoutMs, bool enableWal = true)
    {
        _busyTimeoutMs = Math.Clamp(busyTimeoutMs, 1_000, 120_000);
        _enableWal = enableWal;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqlite)
        {
            ApplyPragmas(sqlite, _busyTimeoutMs, _enableWal);
        }
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqlite)
        {
            await ApplyPragmasAsync(sqlite, _busyTimeoutMs, _enableWal, cancellationToken).ConfigureAwait(false);
        }
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    public static void ApplyPragmas(
        SqliteConnection connection,
        int busyTimeoutMs = DefaultBusyTimeoutMs,
        bool enableWal = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var clampedTimeout = Math.Clamp(busyTimeoutMs, 1_000, 120_000);
        var handle = connection.State == ConnectionState.Open ? connection.Handle : null;
        if (handle is { IsInvalid: false, IsClosed: false }
            && ConfiguredHandles.TryGetValue(handle, out var applied)
            && applied.BusyTimeoutMs == clampedTimeout
            && (!enableWal || applied.EnableWal))
        {
            return;
        }

        (string Path, long CreationTicks) walKey = (string.Empty, 0);
        var needsWal = enableWal && ShouldEnsureWal(connection, out walKey);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = needsWal
            ? $"PRAGMA busy_timeout = {clampedTimeout}; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;"
            : $"PRAGMA busy_timeout = {clampedTimeout}; PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();

        if (needsWal && walKey.Path.Length > 0)
        {
            WalInitializedFiles.TryAdd(walKey, 1);
        }
        if (handle is { IsInvalid: false, IsClosed: false })
        {
            ConfiguredHandles.AddOrUpdate(handle, new AppliedPragmaState(clampedTimeout, enableWal));
        }
    }

    public static async Task ApplyPragmasAsync(
        SqliteConnection connection,
        int busyTimeoutMs = DefaultBusyTimeoutMs,
        bool enableWal = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var clampedTimeout = Math.Clamp(busyTimeoutMs, 1_000, 120_000);
        var handle = connection.State == ConnectionState.Open ? connection.Handle : null;
        if (handle is { IsInvalid: false, IsClosed: false }
            && ConfiguredHandles.TryGetValue(handle, out var applied)
            && applied.BusyTimeoutMs == clampedTimeout
            && (!enableWal || applied.EnableWal))
        {
            return;
        }

        (string Path, long CreationTicks) walKey = (string.Empty, 0);
        var needsWal = enableWal && ShouldEnsureWal(connection, out walKey);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = needsWal
            ? $"PRAGMA busy_timeout = {clampedTimeout}; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;"
            : $"PRAGMA busy_timeout = {clampedTimeout}; PRAGMA synchronous = NORMAL;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (needsWal && walKey.Path.Length > 0)
        {
            WalInitializedFiles.TryAdd(walKey, 1);
        }
        if (handle is { IsInvalid: false, IsClosed: false })
        {
            ConfiguredHandles.AddOrUpdate(handle, new AppliedPragmaState(clampedTimeout, enableWal));
        }
    }

    private static bool ShouldEnsureWal(SqliteConnection connection, out (string Path, long CreationTicks) walKey)
    {
        walKey = (string.Empty, 0);
        var dataSource = connection.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
            if (builder.Mode == SqliteOpenMode.Memory || builder.Mode == SqliteOpenMode.ReadOnly)
            {
                return false;
            }
            var fullPath = Path.GetFullPath(dataSource);
            var ticks = File.Exists(fullPath) ? File.GetCreationTimeUtc(fullPath).Ticks : 0L;
            walKey = (fullPath, ticks);
            return !WalInitializedFiles.ContainsKey(walKey);
        }
        catch
        {
            return true;
        }
    }
}
