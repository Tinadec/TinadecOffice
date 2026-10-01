using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The human-facing MCP inventory. These routes used to be stubs returning <c>[]</c> while the
/// gateway advertised a connect/disconnect/status/call family that Core never had, so the page
/// could not tell an unconfigured machine from a broken one and a missing server from an outage.
///
/// The assertions here are chosen around that one distinction: a read always says where its answer
/// came from, and never reports "not configured" unless it actually looked.
/// </summary>
public sealed class McpInventoryApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-mcp-inventory-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

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
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    private InventoryProvider Provider => ((Factory)_factory!).Provider;

    [Fact]
    public async Task Inventory_AnswersFromTheProvider_AndNamesWhatItRead()
    {
        // The provider answers this route with a null schema (it honoured include_schema=false);
        // Core must drop the key rather than turn it into an empty object the UI would render as
        // "this tool takes no parameters".
        Provider.Payload = ServerPayload(includeSchema: false, withUnreachable: true);

        var body = await ReadAsync("/api/v1/mcp/servers");

        var call = Assert.Single(Provider.Requests);
        Assert.Equal("mcp_list", call.ToolId);
        Assert.False(call.Approved);
        Assert.True(call.Params!.Value.TryGetProperty("include_schema", out var asked),
            "The list route must ask for no schemas — but asking nothing at all is not the same as asking for none.");
        Assert.False(asked.GetBoolean());

        Assert.Equal("tool_provider", body.GetProperty("source").GetString());
        Assert.Equal("/etc/tinadec/mcp_servers.json", body.GetProperty("config_path").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("workspace_root").GetString()));

        var servers = body.GetProperty("servers");
        Assert.Equal(2, servers.GetArrayLength());
        var connected = servers[0];
        Assert.Equal("github", connected.GetProperty("id").GetString());
        Assert.Equal("connected", connected.GetProperty("status").GetString());
        Assert.False(connected.TryGetProperty("error", out _));
        Assert.Equal("create_issue", connected.GetProperty("tools")[0].GetProperty("id").GetString());

        // The list route deliberately asks for no schemas; a null from the provider must stay
        // absent rather than become an empty object the UI would render as "no parameters".
        Assert.False(connected.GetProperty("tools")[0].TryGetProperty("input_schema", out _));

        var unreachable = servers[1];
        Assert.Equal("error", unreachable.GetProperty("status").GetString());
        Assert.Contains("exited unexpectedly", unreachable.GetProperty("error").GetString());
        Assert.Equal(0, unreachable.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Tools_NamedServer_CarriesTheSchemaTheServerOffered()
    {
        Provider.Payload = ServerPayload(includeSchema: true, withUnreachable: false);

        var body = await ReadAsync("/api/v1/mcp/servers/GITHUB/tools");

        Assert.True(Provider.Requests[0].Params!.Value.GetProperty("include_schema").GetBoolean());
        Assert.Equal("tool_provider", body.GetProperty("source").GetString());
        Assert.Equal("GITHUB", body.GetProperty("server_id").GetString());

        var schema = body.GetProperty("server").GetProperty("tools")[0].GetProperty("input_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.TryGetProperty("properties", out _));
    }

    [Fact]
    public async Task Tools_UnknownServer_FailsWithAMachineCodeOnlyAfterACompletedRead()
    {
        Provider.Payload = ServerPayload(includeSchema: true, withUnreachable: false);

        var response = await Client.GetAsync("/api/v1/mcp/servers/nope/tools");
        await _factory!.AssertStatusAsync(response, HttpStatusCode.NotFound, "GET /api/v1/mcp/servers/nope/tools");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("mcp_server_not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Tools_AreNeverReportedMissing_WhileTheProviderCannotAnswer()
    {
        Provider.Throw = new InvalidOperationException(
            "TinadecTools executable path is not configured.");

        var response = await Client.GetAsync("/api/v1/mcp/servers/github/tools");
        await _factory!.AssertStatusAsync(response, HttpStatusCode.OK, "GET /api/v1/mcp/servers/github/tools");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("tool_provider_unavailable", body.GetProperty("source").GetString());
        Assert.Contains("executable path is not configured", body.GetProperty("reason").GetString());
        Assert.False(body.TryGetProperty("server", out _),
            "A 404 here would tell the user their server is gone because a child process failed to start.");
    }

    [Fact]
    public async Task Inventory_ReportsARejectedCall_AsUnavailableNotEmpty()
    {
        Provider.Payload = new ToolWireResponseDto { IsSuccess = false, Error = "provider is draining" };

        var body = await ReadAsync("/api/v1/mcp/servers");

        Assert.Equal("tool_provider_unavailable", body.GetProperty("source").GetString());
        Assert.Equal("provider is draining", body.GetProperty("reason").GetString());
        Assert.Equal(0, body.GetProperty("servers").GetArrayLength());
    }

    [Fact]
    public async Task Inventory_KeepsEveryIdentifiableRow_AndSaysHowManyItDropped()
    {
        Provider.Payload = Ok(JsonDocument.Parse("""
            {"config_path":"/tmp/mcp.json","servers":[
              {"id":"a","name":"A","status":"connected","tools":[]},
              {"name":"no id, so it cannot be clicked","status":"connected","tools":[]},
              {"id":"b","name":"B","status":"sideways","tools":[]}
            ]}
            """).RootElement);

        var body = await ReadAsync("/api/v1/mcp/servers");

        Assert.Equal(2, body.GetProperty("servers").GetArrayLength());
        Assert.Equal(1, body.GetProperty("dropped_rows").GetInt32());
        // An unrecognised provider status passes through untouched: upgrading it to "connected"
        // would be Core claiming a handshake it never observed.
        Assert.Equal("sideways", body.GetProperty("servers")[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Inventory_CallsAMalformedPayloadMalformed_NotEmptyConfiguration()
    {
        Provider.Payload = Ok(JsonDocument.Parse("""{"unexpected":true}""").RootElement);

        var body = await ReadAsync("/api/v1/mcp/servers");

        Assert.Equal("tool_provider_unavailable", body.GetProperty("source").GetString());
        Assert.Contains("no servers array", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task OpenApi_TypesBothMcpReadBodies_SoFieldRenamesReachTheDriftGate()
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync("/openapi/core.json"));
        var paths = doc.RootElement.GetProperty("paths");

        foreach (var (route, component) in new[]
                 {
                     ("/api/v1/mcp/servers", "McpInventoryDto"),
                     ("/api/v1/mcp/servers/{serverId}/tools", "McpServerToolsDto"),
                 })
        {
            var response = paths.GetProperty(route).GetProperty("get")
                .GetProperty("responses").GetProperty("200");
            Assert.True(response.TryGetProperty("content", out var content),
                $"{route} must keep a typed 200; an untyped body is how {component} drifts past the gate.");
            Assert.Equal(
                $"#/components/schemas/{component}",
                content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        }

        var server = doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("McpServerDto").GetProperty("properties");
        foreach (var field in new[] { "id", "name", "status", "error", "tools" })
            Assert.True(server.TryGetProperty(field, out _), $"McpServerDto lost '{field}'.");
    }

    [Fact]
    public async Task StubbedMcpControlSurface_IsGoneRatherThanPretending()
    {
        // The gateway used to proxy these four; none ever existed in Core, so every one of them
        // was a click that could only 404. Deleting the proxies is only honest if Core stays
        // empty here too — a future "connect" stub would re-legitimise them.
        foreach (var path in new[]
                 {
                     "/api/v1/mcp/servers/github/reload",
                     "/api/v1/mcp/servers/github/connect",
                     "/api/v1/mcp/servers/github/disconnect",
                     "/api/v1/mcp/servers/github/status",
                     "/api/v1/mcp/servers/github/tools/create_issue/call",
                 })
        {
            var response = await Client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private HttpClient Client => _factory!.CreateClient();

    private async Task<JsonElement> ReadAsync(string path)
    {
        var response = await Client.GetAsync(path);
        await _factory!.AssertStatusAsync(response, HttpStatusCode.OK, $"GET {path}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static ToolWireResponseDto Ok(JsonElement result) => new() { IsSuccess = true, Result = result };

    /// <summary>
    /// Key-for-key what the real provider puts on the wire, taken from a live probe against a
    /// stand-in MCP server (<c>config_path</c>, then per-server <c>id/name/status/error/tools</c>,
    /// with <c>error</c> and <c>input_schema</c> present as null rather than omitted). Written as
    /// text on purpose: an anonymous object would let the fake invent a friendlier shape than the
    /// one Core actually has to read.
    /// </summary>
    private static ToolWireResponseDto ServerPayload(bool includeSchema, bool withUnreachable)
    {
        var schema = includeSchema
            ? """{"type":"object","properties":{"title":{"type":"string"}}}"""
            : "null";
        var github = $$$"""{"id":"github","name":"GitHub","status":"connected","error":null,"tools":[{"id":"create_issue","name":"create_issue","description":"Open an issue","input_schema":{{{schema}}}}]}"""
            ;
        var ghost = """{"id":"ghost","name":"Ghost","status":"error","error":"MCP server process exited unexpectedly (exit code: 1)","tools":[]}"""
            ;
        var servers = withUnreachable ? $"[{github},{ghost}]" : $"[{github}]";

        return Ok(JsonDocument.Parse(
            $$"""{"config_path":"/etc/tinadec/mcp_servers.json","servers":{{servers}}}""").RootElement);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public InventoryProvider Provider { get; } = new();

        public Factory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(_root, "workspace"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolProvider>();
                services.AddSingleton<IToolProvider>(Provider);
            });
        }
    }

    private sealed class InventoryProvider : IToolProvider
    {
        public List<ToolWireRequestDto> Requests { get; } = [];
        public ToolWireResponseDto? Payload { get; set; }
        public Exception? Throw { get; set; }

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolManifestDto());

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolManifestDto());

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ToolWireResponseDto> CallAsync(
            string workspaceRoot,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Throw is { } failure) throw failure;
            Assert.NotNull(timeout);
            Assert.True(timeout > TimeSpan.Zero, "An inventory read must be bounded; this one feeds a page.");
            return Task.FromResult(Payload ?? Ok(default));
        }
    }
}
