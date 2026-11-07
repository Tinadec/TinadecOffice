using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// A user-authored "team" is a Mode whose leader is the conversation identity and
/// whose members are spawnable templates (node-less bindings). Publishing it must
/// freeze the same snapshot dialect the bundled pack uses, and editing a draft must
/// replace — not merely extend — the stored topology.
/// </summary>
public sealed class AgentModeTeamTopologyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-agent-mode-team-tests", Guid.NewGuid().ToString("N"));
    private TeamModeFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new TeamModeFactory(_root);
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
    public async Task TeamShape_PublishesWithoutExecutionNodes_AndFreezesBindingsAndRelationship()
    {
        var client = _factory!.CreateClient();
        // A custom leader with a real tool face proves the team binding can shed it;
        // the bootstrap meeting agent ships with an empty scope and would pass for free.
        using var leaderCreate = await client.PostAsJsonAsync("/api/v1/agents", new
        {
            slug = "team-leader",
            display_name = "Team Leader",
            layer = "operation",
            role = "session_coordinator",
            tool_scope = new[] { "read_file" },
            capabilities = Array.Empty<string>(),
            enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, leaderCreate.StatusCode);
        var leader = await leaderCreate.Content.ReadFromJsonAsync<JsonElement>();
        using var leaderPublish = await client.PostAsync($"/api/v1/agents/{leader.GetProperty("id").GetGuid()}/publish", null);
        Assert.True(leaderPublish.IsSuccessStatusCode, $"leader publish failed: {leaderPublish.StatusCode} | {_factory!.LastFailure()}");

        var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(agents);
        var member = agents!.First(agent => agent.GetProperty("slug").GetString() == "worker.browser"
            && agent.GetProperty("source_kind").GetString() == "pack");

        using var create = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug = "e2e-team",
            display_name = "E2E Team",
            nodes = new object[]
            {
                new
                {
                    node_key = "leader",
                    agent_definition_id = leader.GetProperty("id").GetGuid(),
                    layer = "operation",
                    label = "Leader",
                    config = new { conversation = true },
                    relationship = new
                    {
                        duty = "Own the goal and dispatch members",
                        agent_types = new[] { "worker.browser" },
                        allowed_dispatch_targets = new[] { "worker.browser" },
                        success_criteria = new[] { "The goal is answered with evidence" }
                    }
                }
            },
            edges = Array.Empty<object>(),
            bindings = new object[]
            {
                new
                {
                    node_key = "leader",
                    tool_switches = new Dictionary<string, bool> { ["*"] = false },
                    envelope = new { spawn = new { max_depth = 2, max_agents_per_run = 4, max_parallel_workers = 2 } },
                    includes_core_reserved = false
                },
                new
                {
                    agent_ref = member.GetProperty("slug").GetString(),
                    envelope = new { resources = new { read = new[] { "" } } },
                    includes_core_reserved = false
                }
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var mode = await create.Content.ReadFromJsonAsync<JsonElement>();
        var modeId = mode.GetProperty("id").GetGuid();

        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        var publishBody = await publish.Content.ReadAsStringAsync();
        Assert.True(publish.IsSuccessStatusCode, $"team publish failed: {publish.StatusCode} {publishBody} | {_factory!.LastFailure()}");

        // GetMode reads the published projection after the draft status flips.
        var published = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
        Assert.Equal("published", published.GetProperty("status").GetString());
        Assert.Equal(1, published.GetProperty("nodes").GetArrayLength());
        var relationship = published.GetProperty("nodes")[0].GetProperty("relationship");
        Assert.Contains(relationship.GetProperty("agent_types").EnumerateArray(), item => item.GetString() == "worker.browser");
        Assert.Equal(2, published.GetProperty("bindings").GetArrayLength());

        // The composer's mode picker reads published rows and submits the version id.
        var selectable = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?status=published");
        Assert.NotNull(selectable);
        var selectableTeam = selectable!.Single(row => row.GetProperty("id").GetGuid() == modeId);
        Assert.False(string.IsNullOrWhiteSpace(selectableTeam.GetProperty("latest_published_mode_version_id").GetString()));

        var versions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{modeId}/versions");
        Assert.NotNull(versions);
        Assert.Single(versions!);
        var snapshot = versions![0].GetProperty("snapshot");
        Assert.Equal("tinadec.mode_version/v1", snapshot.GetProperty("schema").GetString());
        var snapshotNode = snapshot.GetProperty("nodes")[0];
        Assert.True(snapshotNode.GetProperty("config").GetProperty("conversation").GetBoolean());
        // `"*": false` sheds the leader's business tool face, which is what keeps the
        // run in the free_form (team) tier rather than solo_dispatch.
        Assert.Equal(0, snapshotNode.GetProperty("effective_tools").GetArrayLength());
        Assert.Contains(snapshotNode.GetProperty("relationship").GetProperty("agent_types").EnumerateArray(),
            item => item.GetString() == "worker.browser");
        Assert.Equal(2, snapshot.GetProperty("bindings").GetArrayLength());
        Assert.Contains(snapshot.GetProperty("bindings").EnumerateArray(),
            binding => binding.TryGetProperty("agent_ref", out var agentRef) && agentRef.GetString() == "worker.browser");
    }

    [Fact]
    public async Task DraftUpdate_ReplacesOmittedNodesAndEdges_AndKeepsConditionRoundTrip()
    {
        var client = _factory!.CreateClient();
        var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        var meeting = agents!.First(agent => agent.GetProperty("slug").GetString() == "meeting"
            && agent.GetProperty("source_kind").GetString() == "pack");
        var worker = agents!.First(agent => agent.GetProperty("slug").GetString() == "worker.browser"
            && agent.GetProperty("source_kind").GetString() == "pack");

        using var create = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug = "e2e-reconcile",
            display_name = "E2E Reconcile",
            nodes = new object[]
            {
                new { node_key = "leader", agent_definition_id = meeting.GetProperty("id").GetGuid(), layer = "operation", label = "Leader" },
                new { node_key = "worker", agent_definition_id = worker.GetProperty("id").GetGuid(), layer = "execution", label = "Worker" }
            },
            edges = new object[]
            {
                new { edge_key = "leader-worker", source_node_key = "leader", target_node_key = "worker", condition = new { label = "handoff" } }
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var mode = await create.Content.ReadFromJsonAsync<JsonElement>();
        var modeId = mode.GetProperty("id").GetGuid();

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
        Assert.Equal(2, before.GetProperty("nodes").GetArrayLength());
        Assert.Equal("handoff", before.GetProperty("edges")[0].GetProperty("condition").GetProperty("label").GetString());

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-modes/{modeId}/draft")
        {
            Content = JsonContent.Create(new
            {
                nodes = new object[]
                {
                    new { node_key = "leader", agent_definition_id = meeting.GetProperty("id").GetGuid(), layer = "operation", label = "Leader" }
                },
                edges = Array.Empty<object>(),
                bindings = Array.Empty<object>()
            })
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var update = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
        Assert.Equal(1, after.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, after.GetProperty("edges").GetArrayLength());
        Assert.Equal(0, after.GetProperty("bindings").GetArrayLength());
    }

    [Fact]
    public async Task ModeInstructions_RoundTripThroughDraftAndFreezeOnlyWhenPresent()
    {
        var client = _factory!.CreateClient();
        using var leaderCreate = await client.PostAsJsonAsync("/api/v1/agents", new
        {
            slug = "instructions-leader",
            display_name = "Instructions Leader",
            layer = "operation",
            role = "session_coordinator",
            tool_scope = new[] { "read_file" },
            capabilities = Array.Empty<string>(),
            enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, leaderCreate.StatusCode);
        var leader = await leaderCreate.Content.ReadFromJsonAsync<JsonElement>();
        using var leaderPublish = await client.PostAsync($"/api/v1/agents/{leader.GetProperty("id").GetGuid()}/publish", null);
        Assert.True(leaderPublish.IsSuccessStatusCode, $"leader publish failed: {leaderPublish.StatusCode} | {_factory!.LastFailure()}");

        async Task<Guid> CreateMode(string slug, string? instructions)
        {
            using var create = await client.PostAsJsonAsync("/api/v1/agent-modes", new
            {
                slug,
                display_name = slug,
                description = "Team summary",
                instructions,
                nodes = new object[]
                {
                    new
                    {
                        node_key = "leader",
                        agent_definition_id = leader.GetProperty("id").GetGuid(),
                        layer = "operation",
                        label = "Leader",
                        config = new { conversation = true }
                    }
                },
                edges = Array.Empty<object>(),
                bindings = Array.Empty<object>()
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var mode = await create.Content.ReadFromJsonAsync<JsonElement>();
            return mode.GetProperty("id").GetGuid();
        }

        const string collaboration = "Only the leader delegates; always answer with the workers' evidence.";
        var modeId = await CreateMode("e2e-instructions", collaboration);
        var created = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
        Assert.Equal(collaboration, created.GetProperty("instructions").GetString());

        // An explicit null clears the field; a later update restores it.
        using (var clear = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-modes/{modeId}/draft")
        {
            Content = JsonContent.Create(new
            {
                instructions = (string?)null,
                nodes = new object[]
                {
                    new
                    {
                        node_key = "leader",
                        agent_definition_id = leader.GetProperty("id").GetGuid(),
                        layer = "operation",
                        label = "Leader",
                        config = new { conversation = true }
                    }
                },
                edges = Array.Empty<object>(),
                bindings = Array.Empty<object>()
            })
        })
        {
            clear.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            using var cleared = await client.SendAsync(clear);
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        }
        var clearedMode = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
        Assert.False(clearedMode.TryGetProperty("instructions", out var clearedInstructions) && clearedInstructions.ValueKind != JsonValueKind.Null);

        const string revised = "Delegate implementation, then verify and summarize.";
        using (var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent-modes/{modeId}/draft")
        {
            Content = JsonContent.Create(new
            {
                instructions = revised,
                nodes = new object[]
                {
                    new
                    {
                        node_key = "leader",
                        agent_definition_id = leader.GetProperty("id").GetGuid(),
                        layer = "operation",
                        label = "Leader",
                        config = new { conversation = true }
                    }
                },
                edges = Array.Empty<object>(),
                bindings = Array.Empty<object>()
            })
        })
        {
            update.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
            using var updated = await client.SendAsync(update);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        Assert.True(publish.IsSuccessStatusCode, $"instructions mode publish failed: {publish.StatusCode} | {_factory!.LastFailure()}");
        var versions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{modeId}/versions");
        Assert.NotNull(versions);
        Assert.Equal(revised, versions![0].GetProperty("snapshot").GetProperty("instructions").GetString());

        // A mode without instructions must not grow the key: doing so would change
        // the topology hash of every newly published legacy mode.
        var plainModeId = await CreateMode("e2e-no-instructions", null);
        using var plainPublish = await client.PostAsync($"/api/v1/agent-modes/{plainModeId}/publish", null);
        Assert.True(plainPublish.IsSuccessStatusCode, $"plain publish failed: {plainPublish.StatusCode} | {_factory!.LastFailure()}");
        var plainVersions = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/agent-modes/{plainModeId}/versions");
        Assert.False(plainVersions![0].GetProperty("snapshot").TryGetProperty("instructions", out _));
    }

    [Fact]
    public async Task ModeInstructions_RejectsValuesOverTheSnapshotBudget()
    {
        var client = _factory!.CreateClient();
        using var create = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug = "e2e-instructions-too-long",
            display_name = "Too long",
            instructions = new string('x', 4001),
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    private sealed class TeamModeFactory : IsolatedApiFactory
    {
        private readonly string _root;
        public TeamModeFactory(string root) => _root = root;

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
