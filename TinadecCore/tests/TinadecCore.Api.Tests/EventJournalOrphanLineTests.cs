using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// AppendEventAsync fsyncs the journal line before it opens the index transaction, so a
/// SQLITE_BUSY at commit leaves the line behind with no index row and no advance of the run's
/// sequence counter. The next append reuses that sequence under a new event id. ReconcileAsync
/// used to deduplicate on event_id alone, so on the next mount it re-indexed the orphan into
/// UNIQUE(run_id, sequence) and the whole scope failed to mount — one transient lock turned into
/// a project the user cannot open. These tests reproduce that state directly.
/// </summary>
public sealed class EventJournalOrphanLineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-orphan-journal-tests", Guid.NewGuid().ToString("N"));
    private Factory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OrphanedLineSharingAnIndexedSequence_DoesNotFailReconcileOrDuplicateTheRow()
    {
        var (lifecycle, paths, run) = await StartRunAsync("orphan");
        var indexed = await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "orphan-test" }, "Run started");
        Assert.Equal(1, indexed.Sequence);

        var orphanId = await AppendOrphanLineAsync(paths, run.Id, indexed.EventId, sequence: 1);

        // The historical bug: this threw DbUpdateException on UNIQUE(run_id, sequence).
        await lifecycle.ReconcileAsync();

        var rows = await IndexRowsAsync(run.Id);
        var row = Assert.Single(rows);
        Assert.Equal(indexed.EventId, row.EventId);
        Assert.Equal(1, row.Sequence);
        Assert.NotEqual(orphanId, row.EventId);

        // The run is still usable afterwards: the counter advanced for the indexed row,
        // so the next append takes the following sequence rather than colliding again.
        var next = await lifecycle.AppendEventAsync(run.Id, "task.assigned", new { source = "orphan-test" }, "Task assigned");
        Assert.Equal(2, next.Sequence);
        Assert.Equal(2, (await IndexRowsAsync(run.Id)).Count);

        // Reconciling a second time must stay idempotent.
        await lifecycle.ReconcileAsync();
        Assert.Equal(2, (await IndexRowsAsync(run.Id)).Count);
    }

    [Fact]
    public async Task OrphanedLineAheadOfTheCounter_DoesNotAdvancePastTheIndexedRow()
    {
        var (lifecycle, paths, run) = await StartRunAsync("orphan-head");
        var indexed = await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "orphan-test" }, "Run started");

        // Same sequence, different id, and a timestamp far ahead: the orphan must not win
        // the run's head, because the counter belongs to the row that was committed.
        await AppendOrphanLineAsync(paths, run.Id, indexed.EventId, sequence: 1);
        await lifecycle.ReconcileAsync();

        var rows = await IndexRowsAsync(run.Id);
        Assert.Single(rows);
        await using var db = await _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        var stored = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(1, stored.LastEventSequence);
    }

    /// <summary>
    /// The regression side: a line that really is missing from the index — new id, unused
    /// sequence — must still be recovered, and must still move the run's counter. Skipping
    /// every line whose id is unknown would pass the orphan tests and silently lose events.
    /// </summary>
    [Fact]
    public async Task GenuinelyUnindexedLine_IsStillRecoveredAndAdvancesTheCounter()
    {
        var (lifecycle, paths, run) = await StartRunAsync("recover");
        await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "orphan-test" }, "Run started");

        var logPath = paths.EventLog(run.Id);
        var firstLine = (await File.ReadAllLinesAsync(logPath))[0];
        var recoveredId = Guid.NewGuid();
        // Sequence 7 is unused, so this is a real gap rather than an orphan.
        var recoveredLine = firstLine.Replace("\"sequence\":1", "\"sequence\":7", StringComparison.Ordinal);
        Assert.Contains("\"sequence\":7", recoveredLine, StringComparison.Ordinal);
        recoveredLine = ReplaceEventId(recoveredLine, recoveredId);
        await File.AppendAllTextAsync(logPath, recoveredLine + "\n");

        await lifecycle.ReconcileAsync();

        var rows = await IndexRowsAsync(run.Id);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, x => x.EventId == recoveredId && x.Sequence == 7);
        await using var db = await _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        var stored = await db.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(7, stored.LastEventSequence);
    }

    private static string ReplaceEventId(string line, Guid replacement)
    {
        using var document = JsonDocument.Parse(line);
        // The journal record is serialized with JsonSerializerDefaults.Web, so the field is
        // camelCase eventId — not the snake_case event_id the HTTP envelope uses.
        var idProperty = document.RootElement.EnumerateObject()
            .Single(property => property.Name is "eventId" or "event_id" or "EventId");
        var original = idProperty.Value.GetString()!;
        var replaced = line.Replace(original, replacement.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(line, replaced);
        return replaced;
    }

    /// <summary>
    /// Writes a second journal line that carries the same sequence as an already-indexed
    /// event but a different event id — exactly what a failed index transaction leaves behind.
    /// </summary>
    private static async Task<Guid> AppendOrphanLineAsync(StoragePaths paths, Guid runId, Guid indexedEventId, long sequence)
    {
        var logPath = paths.EventLog(runId);
        var lines = await File.ReadAllLinesAsync(logPath);
        var source = lines.Single(line => line.Contains(indexedEventId.ToString(), StringComparison.Ordinal));
        Assert.Contains($"\"sequence\":{sequence}", source, StringComparison.Ordinal);
        var orphanId = Guid.NewGuid();
        var orphan = ReplaceEventId(source, orphanId);
        await File.AppendAllTextAsync(logPath, orphan + "\n");
        return orphanId;
    }

    private async Task<List<EventIndexRecord>> IndexRowsAsync(Guid runId)
    {
        await using var db = await _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        return await db.EventIndex.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Sequence).ToListAsync();
    }

    private async Task<(StorageLifecycleService Lifecycle, StoragePaths Paths, RunRecord Run)> StartRunAsync(string label)
    {
        using var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = $"Orphan {label}", path = Path.Combine(_root, $"workspace-{label}") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = $"Orphan {label}" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "orphan trigger" })).Content.ReadFromJsonAsync<JsonElement>();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, message.GetProperty("id").GetGuid());
        return (lifecycle, _factory.Services.GetRequiredService<StoragePaths>(), run);
    }

    private sealed class Factory : IsolatedApiFactory
    {
        private readonly string _root;
        public Factory(string root) => _root = root;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddDebug(); });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}