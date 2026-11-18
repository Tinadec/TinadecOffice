using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The read retry against a real SQLite lock, not a mock. Rollback journal on purpose: under WAL
/// a writer does not block a reader at all, and a database that has never completed a write
/// transaction is still in DELETE mode, which is the state a freshly created scope is in.
/// </summary>
public sealed class SqliteTransientRetryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tinadec-read-retry-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<(SqliteConnection Holder, string Path)> LockedDatabaseAsync()
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db");
        var holder = new SqliteConnection($"Data Source={path};Pooling=False");
        await holder.OpenAsync();
        await using (var setup = holder.CreateCommand())
        {
            setup.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE facts(x); INSERT INTO facts VALUES (42);";
            await setup.ExecuteNonQueryAsync();
        }
        await using (var lockIt = holder.CreateCommand()) { lockIt.CommandText = "BEGIN EXCLUSIVE"; await lockIt.ExecuteNonQueryAsync(); }
        return (holder, path);
    }

    private static async Task<int> ReadFactAsync(string path, CancellationToken ct)
    {
        await using var reader = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await reader.OpenAsync(ct);
        await using var command = reader.CreateCommand();
        command.CommandText = "SELECT x FROM facts";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task ABriefLockIsWaitedOutAndTheReadReturnsTheRealRow()
    {
        var (holder, path) = await LockedDatabaseAsync();
        await using var _ = holder;
        // Release only after a real BUSY has been observed. A fixed delay let a loaded machine
        // open the reader connection after the release, so the read succeeded on attempt one and
        // the test passed with the retry removed.
        var firstFailure = new TaskCompletionSource();
        var attempts = 0;
        var read = SqliteTransientRetry.ReadAsync(async ct =>
        {
            attempts++;
            try { return await ReadFactAsync(path, ct); }
            catch (SqliteException) { firstFailure.TrySetResult(); throw; }
        }, CancellationToken.None);

        await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var elapsed = Stopwatch.StartNew();
        await using (var commit = holder.CreateCommand()) { commit.CommandText = "COMMIT"; await commit.ExecuteNonQueryAsync(); }
        var value = await read;
        elapsed.Stop();

        Assert.Equal(42, value);
        Assert.True(attempts >= 2, $"expected the first attempt to hit the lock, got {attempts}");
        Assert.True(elapsed.ElapsedMilliseconds < 1500, $"read took {elapsed.ElapsedMilliseconds} ms after the lock was released");
    }

    [Fact]
    public async Task ALockThatNeverReleasesStillThrowsRatherThanReturningNothing()
    {
        var (holder, path) = await LockedDatabaseAsync();
        await using var _ = holder;
        var attempts = 0;

        var error = await Assert.ThrowsAsync<SqliteException>(() => SqliteTransientRetry.ReadAsync(ct =>
        {
            attempts++;
            return ReadFactAsync(path, ct);
        }, CancellationToken.None));

        Assert.Contains(error.SqliteErrorCode & 0xFF, new[] { 5, 6 });
        Assert.Equal(SqliteTransientRetry.MaxAttempts, attempts);
    }

    [Fact]
    public async Task CancellationStopsTheRetry()
    {
        var (holder, path) = await LockedDatabaseAsync();
        await using var _ = holder;
        using var cancelled = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteTransientRetry.ReadAsync(ct =>
        {
            attempts++;
            cancelled.Cancel();
            return ReadFactAsync(path, ct);
        }, cancelled.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ANonTransientCodeIsRethrownUnchangedAfterOneAttempt()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<SqliteException>(() => SqliteTransientRetry.ReadAsync<int>(ct =>
        {
            attempts++;
            throw new SqliteException("no such table", 1);
        }, CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal(1, error.SqliteErrorCode);
    }

    /// <summary>
    /// The hazard the retry itself introduces: FollowEventsAsync advances the caller's per-run
    /// watermarks. If it advanced them while an attempt was still in flight, a retry would start
    /// past the rows the failed attempt had already read and the subscriber would never see them.
    /// The fault lands on the second run's query, after the first run's rows were read.
    /// </summary>
    [Fact]
    public async Task FollowEvents_DoesNotLoseTheFirstRunsRowsWhenARetryFollowsAMidReadFailure()
    {
        await using var factory = new ApiEndpointFactory();
        var inner = factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        string connectionString;
        await using (var probe = await inner.CreateDbContextAsync())
            connectionString = probe.Database.GetConnectionString()!;

        var lifecycle = factory.Services.GetRequiredService<StorageLifecycleService>();
        var first = await StartRunWithEventAsync(factory, lifecycle, "first");
        var second = await StartRunWithEventAsync(factory, lifecycle, "second");

        // Command 1 is the runs' heads, 2 and 3 are the two runs' row reads.
        var faults = new FaultOnCommandFactory(connectionString, failOnCommand: 3);
        var retrying = ActivatorUtilities.CreateInstance<StorageLifecycleService>(factory.Services, faults);
        var watermarks = new Dictionary<Guid, long>();

        var events = await retrying.FollowEventsAsync(null, watermarks);

        Assert.Equal(2, faults.Attempts);
        Assert.Contains(events, x => Guid.Parse(x.RunId) == first);
        Assert.Contains(events, x => Guid.Parse(x.RunId) == second);
        Assert.Equal(1, watermarks[first]);
        Assert.Equal(1, watermarks[second]);
    }

    private static async Task<Guid> StartRunWithEventAsync(ApiEndpointFactory factory, StorageLifecycleService lifecycle, string label)
    {
        using var client = factory.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Retry " + label, path = Path.Combine(Path.GetTempPath(), "tinadec-retry-" + label + "-" + Guid.NewGuid().ToString("N")) })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Retry " + label })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "retry trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "retry-test" }, "Run started");
        return run.Id;
    }

    private sealed class FaultOnCommandFactory(string connectionString, int failOnCommand) : IDbContextFactory<LifecycleDbContext>
    {
        public int Attempts { get; private set; }
        public int Commands { get; set; }
        private readonly int _failOnCommand = failOnCommand;

        public LifecycleDbContext CreateDbContext()
        {
            Attempts++;
            var options = new DbContextOptionsBuilder<LifecycleDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new FaultInterceptor(this))
                .Options;
            return new LifecycleDbContext(options);
        }

        public Task<LifecycleDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());

        private sealed class FaultInterceptor(FaultOnCommandFactory owner) : DbCommandInterceptor
        {
            public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                if (++owner.Commands == owner._failOnCommand) throw new SqliteException("Injected mid-read contention", 5);
                return result;
            }

            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
                InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
                => new(ReaderExecuting(command, eventData, result));
        }
    }
}