using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The mode-graph write path M2 rebuilt: a draft save is delete-aware (nodes and edges
/// absent from the submission go away), edges carry their own stable <c>edge_key</c>
/// instead of being minted a new GUID per save, and publish gates on
/// <c>GraphValidation</c> rather than the retired "meeting + execution node" rule — so a
/// Solo-shaped draft (one operation node, zero execution nodes) can be published.
/// </summary>
public sealed class AgentModeTopologyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-agent-mode-topology-tests", Guid.NewGuid().ToString("N"));
    private AgentModeTopologyFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new AgentModeTopologyFactory(_root);
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
    public async Task DraftSave_DeletesNodesAndEdgesAbsentFromTheSubmission()
    {
        var client = _factory!.CreateClient();
        var (meeting, worker) = await AgentsAsync(client);
        using var created = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug = "topology-delete",
            display_name = "Topology Delete",
            nodes = new object[]
            {
                new { node_key = "meeting", agent_definition_id = meeting, layer = "operation", label = "meeting" },
                new { node_key = "worker", agent_definition_id = worker, layer = "execution", label = "worker" },
            },
            edges = new object[]
            {
                new { edge_key = "meeting->worker", source_node_key = "meeting", target_node_key = "worker" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var mode = await created.Content.ReadFromJsonAsync<JsonElement>();
        var modeId = mode.GetProperty("id").GetGuid();
        Assert.Equal(2, mode.GetProperty("nodes").GetArrayLength());
        Assert.Equal(1, mode.GetProperty("edges").GetArrayLength());

        // Submit only the conversation node: the worker and the edge that reached it are
        // part of the old draft projection and must disappear, not linger beside the new state.
        using var saved = await client.PutAsJsonAsync($"/api/v1/agent-modes/{modeId}/draft", new
        {
            nodes = new object[]
            {
                new { node_key = "meeting", agent_definition_id = meeting, layer = "operation", label = "meeting" },
            },
            edges = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedBody = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, savedBody.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, savedBody.GetProperty("edges").GetArrayLength());

        var reread = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
        Assert.Equal(1, reread.GetProperty("nodes").GetArrayLength());
        Assert.Equal("meeting", reread.GetProperty("nodes")[0].GetProperty("node_key").GetString());
        Assert.Equal(0, reread.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public async Task DraftSave_WithoutEdgeKey_IsRefusedInsteadOfMintingOne()
    {
        var client = _factory!.CreateClient();
        var (meeting, worker) = await AgentsAsync(client);
        using var response = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug = "topology-edge-key",
            display_name = "Topology Edge Key",
            nodes = new object[]
            {
                new { node_key = "meeting", agent_definition_id = meeting, layer = "operation", label = "meeting" },
                new { node_key = "worker", agent_definition_id = worker, layer = "execution", label = "worker" },
            },
            edges = new object[]
            {
                new { source_node_key = "meeting", target_node_key = "worker" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("edge_key_required", problem.GetProperty("code").GetString());

        // Nothing was written for the refused create: no draft with that slug exists.
        var drafts = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?status=draft");
        Assert.DoesNotContain(drafts!, mode => mode.GetProperty("slug").GetString() == "topology-edge-key");
    }

    [Fact]
    public async Task DraftSave_WithAnUnknownEdgeEndpoint_ReturnsStructuredDiagnostics()
    {
        var client = _factory!.CreateClient();
        var (meeting, _) = await AgentsAsync(client);
        using var created = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug = "topology-diagnostics",
            display_name = "Topology Diagnostics",
            nodes = new object[]
            {
                new { node_key = "meeting", agent_definition_id = meeting, layer = "operation", label = "meeting" },
            },
            edges = Array.Empty<object>(),
        });
        var modeId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var saved = await client.PutAsJsonAsync($"/api/v1/agent-modes/{modeId}/draft", new
        {
            nodes = new object[]
            {
                new { node_key = "meeting", agent_definition_id = meeting, layer = "operation", label = "meeting" },
            },
            edges = new object[]
            {
                new { edge_key = "dangling", source_node_key = "meeting", target_node_key = "ghost" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        var problem = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mode_graph_invalid", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("message").GetString()!.Contains("unknown node", StringComparison.Ordinal));

        // The refused save left the stored draft alone: still one node, still no edges.
        var reread = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
        Assert.Equal(1, reread.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, reread.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public async Task SoloShapedDraft_PublishesOnTheGraphContract()
    {
        var client = _factory!.CreateClient();
        var (meeting, _) = await AgentsAsync(client);
        using var created = await client.PostAsJsonAsync("/api/v1/agent-modes", new
        {
            slug = "topology-solo",
            display_name = "Solo Shape",
            nodes = new object[]
            {
                new { node_key = "meeting", agent_definition_id = meeting, layer = "operation", label = "meeting" },
            },
            edges = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var modeId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // One operation node, zero execution nodes: the retired gate refused this shape.
        using var publish = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var published = await publish.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, published.GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(published.GetProperty("topology_hash").GetString()));

        // The published projection still shows the graph the editor saved (draft rows move
        // to published instead of vanishing from GET).
        var reread = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent-modes/{modeId}");
        Assert.Equal("published", reread.GetProperty("status").GetString());
        Assert.Equal(1, reread.GetProperty("nodes").GetArrayLength());
        Assert.Equal("meeting", reread.GetProperty("nodes")[0].GetProperty("node_key").GetString());

        // A published mode is not a draft: publishing again is a conflict, not a second version.
        using var again = await client.PostAsync($"/api/v1/agent-modes/{modeId}/publish", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("not_draft", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static async Task<(Guid meeting, Guid worker)> AgentsAsync(HttpClient client)
    {
        var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(agents);
        var meeting = agents!.Single(agent => agent.GetProperty("slug").GetString() == "meeting").GetProperty("id").GetGuid();
        var worker = agents!.Single(agent => agent.GetProperty("slug").GetString() == "worker.general").GetProperty("id").GetGuid();
        return (meeting, worker);
    }

    private sealed class AgentModeTopologyFactory : IsolatedApiFactory
    {
        private readonly string _root;
        public AgentModeTopologyFactory(string root) => _root = root;

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
