using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// DmaEA endpoint contract tests: interaction admission appends the user message itself and
/// the durable run stream carries a structured SSE chunk; without a stored API key the run
/// fails cleanly and the chunk is kind=error (never a fabricated done). Orchestration
/// projections return the documented snapshot keys. The legacy invoke-stream wire is retired.
/// </summary>
public sealed class DmaeaEndpointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-dmaea-api-tests", Guid.NewGuid().ToString("N"));
    private DmaeaFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new DmaeaFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Dmaea project", path = Path.Combine(_root, "workspace") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Dmaea session" })).Content.ReadFromJsonAsync<JsonElement>();
        return session.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Interactions_WithoutStoredApiKey_AdmitsThenFailsCleanlyWithErrorMessage()
    {
        var client = _factory!.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new { content = "测试目标", client_message_id = "no-key-1", dispatch_mode = "queued" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = receipt.GetProperty("run_id").GetString();

        var streamResponse = await client.GetAsync($"/api/v1/runs/{runId}/stream?after_seq=0");
        Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
        var body = await streamResponse.Content.ReadAsStringAsync();
        Assert.Contains("data: ", body);
        Assert.Contains("\"kind\":\"error\"", body);
        Assert.Contains("\"error_category\"", body);

        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        var message = Assert.Single(messages!);
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("测试目标", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Interactions_MissingSession_ReturnsNotFound()
    {
        var client = _factory!.CreateClient();
        var missing = Guid.NewGuid();

        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{missing}/interactions", new { content = "目标", dispatch_mode = "queued" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not_found", body);
    }

    [Fact]
    public async Task InvokeStream_RetiredWire_ReturnsNotFound()
    {
        var client = _factory!.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/invoke-stream", new { content = "目标" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Interactions_EmptyContent_ReturnsBadRequest()
    {
        var client = _factory!.CreateClient();
        var sessionId = await CreateSessionAsync(client);

        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new { content = "", dispatch_mode = "queued" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_request", body);
    }

    [Fact]
    public async Task Orchestration_ReturnsSnapshotKeys()
    {
        var client = _factory!.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new { content = "测试目标", client_message_id = "orch-1", dispatch_mode = "queued" });

        var snapshot = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/orchestration");

        Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
        Assert.True(snapshot.TryGetProperty("run", out var run));
        Assert.True(run.ValueKind is JsonValueKind.Null or JsonValueKind.Object);
        Assert.True(snapshot.TryGetProperty("nodes", out _));
        Assert.True(snapshot.TryGetProperty("step_results", out _));
        Assert.True(snapshot.TryGetProperty("graph", out _));
        Assert.True(snapshot.TryGetProperty("assignments", out _));
        Assert.True(snapshot.TryGetProperty("context_packs", out _));
        Assert.True(snapshot.TryGetProperty("supervision_findings", out _));
    }

    [Fact]
    public async Task Orchestration_MissingSession_ReturnsNotFound()
    {
        var client = _factory!.CreateClient();

        var response = await client.GetAsync($"/api/v1/sessions/{Guid.NewGuid()}/orchestration");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class DmaeaFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public DmaeaFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IToolManifestSnapshotResolver, EmptyToolManifestSnapshotResolver>();
            });
        }
    }

    private sealed class EmptyToolManifestSnapshotResolver : IToolManifestSnapshotResolver
    {
        private static readonly ToolManifestSnapshot Snapshot = new(
            2,
            ToolManifestHasher.Compute(Array.Empty<FrozenToolManifestEntry>()),
            []);

        public Task<ToolManifestSnapshot> ResolveAsync(
            ToolManifestSnapshotRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }
}
