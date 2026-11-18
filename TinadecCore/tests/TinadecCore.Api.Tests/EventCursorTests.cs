using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.AspNetCore.Endpoints;
using TinadecCore.Lifecycle;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The event feed's cursor is a per-run vector because sequences are allocated per run. These
/// tests pin the parse/format contract, that a resumed read returns only the unconsumed rows,
/// and that the read is served by the unique index rather than a scan.
/// </summary>
public sealed class EventCursorTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-event-cursor-tests", Guid.NewGuid().ToString("N"));
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
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public void AVectorRoundTripsThroughParseAndEncode()
    {
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        var text = $"{runA:N}:5,{runB:N}:1";

        Assert.True(EventCursor.TryParse(text, out var watermarks));
        Assert.Equal(5, watermarks[runA]);
        Assert.Equal(1, watermarks[runB]);
        Assert.Equal(text, EventCursor.EncodeVector(watermarks));
        Assert.Equal($"{runA:N}:5", EventCursor.EncodeEvent(runA, 5));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid:1")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("00000000000000000000000000000000:x")]
    [InlineData("00000000000000000000000000000000:-1")]
    [InlineData("00000000000000000000000000000000:1:2")]
    public void AnUnparseableCursorIsRejected(string text)
    {
        Assert.False(EventCursor.TryParse(text, out _));
    }

    [Fact]
    public void ARepeatedRunKeepsItsLastPosition()
    {
        var run = Guid.NewGuid();
        Assert.True(EventCursor.TryParse($"{run:N}:1,{run:N}:4", out var watermarks));
        Assert.Equal(4, watermarks[run]);
        Assert.Single(watermarks);
    }

    [Fact]
    public async Task AResumedReadReturnsOnlyTheUnconsumedRows()
    {
        var (lifecycle, runA, runB) = await SeedTwoRunsAsync();

        var partial = new Dictionary<Guid, long> { [runA] = 2 };
        var resumed = await lifecycle.FollowEventsAsync(null, partial);
        Assert.Equal(4, resumed.Count);
        Assert.Equal(3, partial[runA]);
        Assert.Equal(3, partial[runB]);

        var consumed = new Dictionary<Guid, long> { [runA] = 3, [runB] = 3 };
        Assert.Empty(await lifecycle.FollowEventsAsync(null, consumed));

        var missingRun = new Dictionary<Guid, long> { [runA] = 3 };
        Assert.Equal(3, (await lifecycle.FollowEventsAsync(null, missingRun)).Count);
    }

    [Fact]
    public async Task TheEventIndexHasOneRunSequenceIndexAndTheReadUsesIt()
    {
        var (lifecycle, runA, _) = await SeedTwoRunsAsync();
        await using var db = await _factory!.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        var connectionString = db.Database.GetConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var indexes = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "select name from sqlite_master where type = 'index' and tbl_name = 'event_index' order by name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
        }

        var runSequenceIndexes = indexes.Where(name => name.Contains("run_id_sequence", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(runSequenceIndexes.Count == 1,
            $"event_index must carry exactly one (run_id, sequence) index; found [{string.Join(", ", indexes)}].");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"explain query plan select event_id, run_id, sequence, timestamp, byte_offset, byte_length from event_index where run_id = '{runA:N}' and sequence > 0;";
            var plan = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) plan.Add(reader.GetString(reader.FieldCount - 1));
            var detail = string.Join(" | ", plan);
            Assert.DoesNotContain("SCAN", detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(runSequenceIndexes[0], detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<(StorageLifecycleService Lifecycle, Guid RunA, Guid RunB)> SeedTwoRunsAsync()
    {
        using var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Cursor seed", path = Path.Combine(_root, "workspace-seed") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Cursor seed" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();

        var first = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "seed a" })).Content.ReadFromJsonAsync<JsonElement>();
        var runA = await lifecycle.StartRunAsync(sessionId, first.GetProperty("id").GetGuid());
        var second = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "seed b" })).Content.ReadFromJsonAsync<JsonElement>();
        var runB = await lifecycle.StartRunAsync(sessionId, second.GetProperty("id").GetGuid());
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            await lifecycle.AppendEventAsync(runA.Id, "run.progress", new { source = "cursor-test", step = sequence }, $"A {sequence}");
            await lifecycle.AppendEventAsync(runB.Id, "run.progress", new { source = "cursor-test", step = sequence }, $"B {sequence}");
        }
        return (lifecycle, runA.Id, runB.Id);
    }

    private sealed class Factory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddDebug(); });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}