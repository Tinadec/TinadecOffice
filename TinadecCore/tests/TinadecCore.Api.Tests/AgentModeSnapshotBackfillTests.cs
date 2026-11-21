using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// `SnapshotBackfillAsync` — the read-path repair for modes installed before the row projection
/// carried the team documents.
///
/// The gap this closes: every other test in this suite writes the mode columns *and* the snapshot,
/// so the projection is already complete and `NeedsSnapshotBackfill` returns false. The backfill
/// therefore never executed under test, even though it is the only thing that makes a mode
/// published by an older build readable as a team instead of as an empty graph.
///
/// These tests construct the exact shape that triggers it — a mode row whose BindingsJson,
/// Instructions and OrchestrationTier are empty while the frozen `ModeVersionRecord.SnapshotJson`
/// still holds them — then read the mode back over HTTP and assert the projection was repaired.
/// The read is `AsNoTracking`, so the repair must be visible in the RESPONSE without being
/// written to the database; both halves are asserted.
/// </summary>
public sealed class AgentModeSnapshotBackfillTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-mode-backfill", Guid.NewGuid().ToString("N"));
    private BackfillFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new BackfillFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A mode whose columns are empty but whose newest snapshot carries bindings, instructions,
    /// the explicit tier and a node relationship must read back with all four repaired.
    /// </summary>
    [Fact]
    public async Task ReadBackfillsBindingsInstructionsTierAndNodeRelationshipFromTheSnapshot()
    {
        using var client = _factory!.CreateClient();
        var modeId = await SeedModeWithEmptyColumnsButFullSnapshotAsync(client, _factory!);

        var mode = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");

        Assert.Equal("Route every step through the leader.", mode.GetProperty("instructions").GetString());
        Assert.Equal("self_dispatch", mode.GetProperty("orchestration_tier").GetString());

        var bindings = mode.GetProperty("bindings");
        Assert.Equal(JsonValueKind.Array, bindings.ValueKind);
        Assert.Equal(2, bindings.GetArrayLength());

        var nodeBinding = bindings.EnumerateArray()
            .Single(binding => binding.TryGetProperty("node_key", out _));
        Assert.Equal("leader", nodeBinding.GetProperty("node_key").GetString());
        Assert.False(nodeBinding.GetProperty("tool_switches").GetProperty("*").GetBoolean());

        // The node-less binding is the one WITHOUT `node_key` — `agent_ref` alone is not
        // selective, because the DTO declares both properties on the same type.
        var agentBinding = bindings.EnumerateArray()
            .Single(binding => !binding.TryGetProperty("node_key", out _) && binding.TryGetProperty("agent_ref", out _));
        Assert.Equal("worker.browser", agentBinding.GetProperty("agent_ref").GetString());

        var nodes = mode.GetProperty("nodes");
        var leader = nodes.EnumerateArray().Single(node => node.GetProperty("node_key").GetString() == "leader");
        var relationship = leader.GetProperty("relationship");
        Assert.Equal("Own the goal and dispatch members", relationship.GetProperty("duty").GetString());
    }

    /// <summary>
    /// The repair is a PROJECTION, not a write. The read is `AsNoTracking`, so the stored row must
    /// still be empty afterwards — otherwise a GET would mutate the database and the next publish
    /// would hash a different body than the one that was frozen.
    /// </summary>
    [Fact]
    public async Task ReadBackfillDoesNotWriteToTheDatabase()
    {
        using var client = _factory!.CreateClient();
        var modeId = await SeedModeWithEmptyColumnsButFullSnapshotAsync(client, _factory!);

        // Read twice: the first read repairs the projection, the second proves nothing persisted.
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentConfigurationDbContext>();
        var row = await db.AgentModes.AsNoTracking().SingleAsync(x => x.Id == modeId);

        Assert.True(string.IsNullOrEmpty(row.BindingsJson), $"a read must not persist bindings, got: {row.BindingsJson}");
        Assert.True(string.IsNullOrEmpty(row.Instructions), $"a read must not persist instructions, got: {row.Instructions}");
        Assert.True(string.IsNullOrEmpty(row.OrchestrationTier), $"a read must not persist the tier, got: {row.OrchestrationTier}");
    }

    /// <summary>
    /// A complete projection must not pay for the repair. This is the control: without it, a
    /// `NeedsSnapshotBackfill` that always returned true would pass the tests above.
    /// </summary>
    [Fact]
    public async Task CompleteProjectionReadsBackUnchanged()
    {
        using var client = _factory!.CreateClient();
        var modeId = await SeedModeWithEmptyColumnsButFullSnapshotAsync(client, _factory!);

        // Fill the columns so the projection is complete, then read.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentConfigurationDbContext>();
            var row = await db.AgentModes.SingleAsync(x => x.Id == modeId);
            row.BindingsJson = "[]";
            row.Instructions = "Set explicitly.";
            row.OrchestrationTier = "free_form";
            await db.SaveChangesAsync();
        }

        var mode = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");

        // The stored values win; the snapshot must not overwrite a complete projection.
        Assert.Equal("Set explicitly.", mode.GetProperty("instructions").GetString());
        Assert.Equal("free_form", mode.GetProperty("orchestration_tier").GetString());
        Assert.Equal(0, mode.GetProperty("bindings").GetArrayLength());
    }

    /// <summary>
    /// Seeds a mode row with empty documents and a snapshot carrying them, and returns its id.
    /// The snapshot is written directly because no publish path produces this shape any more —
    /// that is exactly why the repair exists, and why it needs a hand-built fixture to exercise.
    /// </summary>
    private static async Task<Guid> SeedModeWithEmptyColumnsButFullSnapshotAsync(HttpClient client, BackfillFactory factory)
    {
        var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(agents);
        var leader = agents!.First(agent => agent.GetProperty("slug").GetString() == "worker.browser");
        var leaderId = leader.GetProperty("id").GetGuid();
        var leaderSlug = leader.GetProperty("slug").GetString()!;

        var payload = new Dictionary<string, object?>
        {
            ["slug"] = $"backfill-{Guid.NewGuid():N}",
            ["display_name"] = "Backfill Fixture",
            ["nodes"] = new object[]
            {
                new
                {
                    node_key = "leader",
                    agent_definition_id = leaderId,
                    // Must match the agent's own layer: publish validates that a node's layer
                    // agrees with the definition it points at.
                    layer = leader.GetProperty("layer").GetString(),
                    label = "Leader",
                    config = new { conversation = true }
                }
            },
            ["edges"] = Array.Empty<object>()
        };

        using var create = await client.PostAsJsonAsync("/api/v1/agent-modes", payload);
        var body = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"mode create failed: {create.StatusCode} {body}");
        var modeId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        // Publish so a ModeVersionRecord with a real snapshot exists, then blank the row columns.
        using (var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", content: null))
        {
            var publishBody = await publish.Content.ReadAsStringAsync();
            Assert.True(publish.IsSuccessStatusCode, $"mode publish failed: {publish.StatusCode} {publishBody}");
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentConfigurationDbContext>();
        var row = await db.AgentModes.SingleAsync(x => x.Id == modeId);
        row.BindingsJson = null;
        row.Instructions = null;
        row.OrchestrationTier = null;
        await db.SaveChangesAsync();

        // Rewrite the newest snapshot so it carries the documents the blank columns lack.
        var version = await db.ModeVersions
            .Where(x => x.AgentModeId == modeId)
            .OrderByDescending(x => x.Version)
            .FirstAsync();
        version.SnapshotJson = BuildSnapshot(leaderSlug);
        await db.SaveChangesAsync();

        return modeId;
    }

    private static string BuildSnapshot(string memberSlug) => JsonSerializer.Serialize(new
    {
        schema = "tinadec.mode_version/v1",
        instructions = "Route every step through the leader.",
        orchestration_tier = "self_dispatch",
        bindings = new object[]
        {
            new
            {
                node_key = "leader",
                tool_switches = new Dictionary<string, bool> { ["*"] = false },
                envelope = new { spawn = new { max_depth = 2 } }
            },
            new
            {
                agent_ref = memberSlug,
                envelope = new { resources = new { read = new[] { "" } } }
            }
        },
        nodes = new object[]
        {
            new
            {
                node_key = "leader",
                relationship = new
                {
                    duty = "Own the goal and dispatch members",
                    agent_types = new[] { memberSlug },
                    allowed_dispatch_targets = new[] { memberSlug }
                }
            }
        },
        edges = Array.Empty<object>()
    });


    private sealed class BackfillFactory : IsolatedApiFactory
    {
        private readonly string _root;
        public BackfillFactory(string root) => _root = root;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}