using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The opt-in write retry against a real SQLite lock. Rollback journal on purpose: a database that
/// has never completed a write transaction is still in DELETE mode, where a writer blocks everyone.
/// The point of these tests is that a retried transaction lands exactly once, and that a failure
/// which is not contention is never retried.
/// </summary>
public sealed class SqliteWriteRetryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tinadec-write-retry-").FullName;

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
            setup.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE ledger(id INTEGER PRIMARY KEY, note TEXT);";
            await setup.ExecuteNonQueryAsync();
        }
        await using (var lockIt = holder.CreateCommand()) { lockIt.CommandText = "BEGIN EXCLUSIVE"; await lockIt.ExecuteNonQueryAsync(); }
        return (holder, path);
    }

    private static async Task WriteOnceAsync(string path, string note, CancellationToken ct)
    {
        await using var writer = new SqliteConnection($"Data Source={path};Pooling=False");
        await writer.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await writer.BeginTransactionAsync(ct);
        await using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO ledger(note) VALUES ($note)";
            command.Parameters.AddWithValue("$note", note);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task<int> CountAsync(string path, string note)
    {
        await using var reader = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await reader.OpenAsync();
        await using var command = reader.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger WHERE note = $note";
        command.Parameters.AddWithValue("$note", note);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ABriefLockIsWaitedOutAndTheRowLandsExactlyOnce()
    {
        var (holder, path) = await LockedDatabaseAsync();
        await using var _ = holder;
        var firstFailure = new TaskCompletionSource();
        var attempts = 0;

        var write = SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            attempts++;
            try { await WriteOnceAsync(path, "brief", ct); }
            catch (SqliteException) { firstFailure.TrySetResult(); throw; }
        }, CancellationToken.None);

        await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await using (var commit = holder.CreateCommand()) { commit.CommandText = "COMMIT"; await commit.ExecuteNonQueryAsync(); }
        await write;

        Assert.True(attempts >= 2, $"expected the first attempt to hit the lock, got {attempts}");
        Assert.Equal(1, await CountAsync(path, "brief"));
    }

    [Fact]
    public async Task ALockThatNeverReleasesThrowsAndLeavesNoRow()
    {
        var (holder, path) = await LockedDatabaseAsync();
        await using var _ = holder;
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() => SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            attempts++;
            await WriteOnceAsync(path, "never", ct);
        }, CancellationToken.None));

        Assert.Equal(SqliteTransientRetry.MaxAttempts, attempts);
        // The holder still owns the exclusive lock, so counting before releasing it would block
        // for the provider timeout and fail for the wrong reason.
        await using (var release = holder.CreateCommand()) { release.CommandText = "ROLLBACK"; await release.ExecuteNonQueryAsync(); }
        Assert.Equal(0, await CountAsync(path, "never"));
    }

    [Fact]
    public async Task ATransientCodeWrappedBySaveChangesIsStillRetried()
    {
        // EF wraps the provider failure in DbUpdateException, so a write retry that only looked at
        // the outer exception would never retry the case it exists for.
        var attempts = 0;
        var value = await SqliteTransientRetry.WriteInTransactionAsync(_ =>
        {
            attempts++;
            if (attempts < 2) throw new DbUpdateException("update failed", new SqliteException("busy", 5));
            return Task.FromResult(7);
        }, CancellationToken.None);

        Assert.Equal(7, value);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AUniqueViolationIsNotRetried()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => SqliteTransientRetry.WriteInTransactionAsync(_ =>
        {
            attempts++;
            throw new DbUpdateException("update failed", new SqliteException("UNIQUE constraint failed", 19));
        }, CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
    }

    [Fact]
    public async Task TheExtendedBusyCodesAreRetriedToo()
    {
        foreach (var code in new[] { 261, 517 })
        {
            var attempts = 0;
            await SqliteTransientRetry.WriteInTransactionAsync(_ =>
            {
                attempts++;
                if (attempts < 2) throw new SqliteException("busy recovery", code);
                return Task.FromResult(true);
            }, CancellationToken.None);
            Assert.Equal(2, attempts);
        }
    }

    [Fact]
    public async Task CancellationStopsTheWriteRetry()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteTransientRetry.WriteInTransactionAsync(_ =>
        {
            attempts++;
            throw new SqliteException("busy", 5);
        }, cancelled.Token));
        Assert.Equal(0, attempts);
    }

    /// <summary>
    /// A wrapped site, not just the helper. SaveRunCheckpointAsync stores its blob and minted its
    /// row id before the transaction, so a retry must reuse both: one checkpoint row, one revision,
    /// and the reference the caller was handed. The fault lands on the transaction's own context,
    /// which is the second one created — the metadata read happens outside the retry.
    /// </summary>
    [Fact]
    public async Task ACheckpointRetryLandsOneRowAndKeepsTheReferenceItStored()
    {
        await using var factory = new ApiEndpointFactory();
        var inner = factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        string connectionString;
        await using (var probe = await inner.CreateDbContextAsync())
            connectionString = probe.Database.GetConnectionString()!;

        var lifecycle = factory.Services.GetRequiredService<StorageLifecycleService>();
        using var client = factory.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Write retry site", path = Path.Combine(_root, "ws-site") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Write retry site" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "checkpoint" })).Content.ReadFromJsonAsync<JsonElement>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());

        var faults = new FaultOnCreationFactory(connectionString, failOnCreation: 2);
        var retrying = ActivatorUtilities.CreateInstance<StorageLifecycleService>(factory.Services, faults);

        var checkpoint = await retrying.SaveRunCheckpointAsync(run.Id,
            new RunCheckpointWrite(0, "executing", "{\"v\":\"retry-site\"}", IdempotencyKey: "retry-site"));

        Assert.Equal(3, faults.Creations);
        Assert.Equal(1, checkpoint.Revision);
        await using var db = await inner.CreateDbContextAsync();
        var rows = await db.RunCheckpoints.Where(x => x.RunId == run.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(checkpoint.ContentReference, rows[0].ContentReference);
        Assert.Equal(checkpoint.Id, rows[0].Id);
    }

    private sealed class FaultOnCreationFactory(string connectionString, int failOnCreation) : IDbContextFactory<LifecycleDbContext>
    {
        public int Creations { get; private set; }

        public LifecycleDbContext CreateDbContext()
        {
            if (++Creations == failOnCreation) throw new SqliteException("Injected write contention", 5);
            var options = new DbContextOptionsBuilder<LifecycleDbContext>().UseSqlite(connectionString).Options;
            return new LifecycleDbContext(options);
        }

        public Task<LifecycleDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}