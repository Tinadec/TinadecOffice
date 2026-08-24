using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// HTTP tests for POST /api/v1/model-providers/cli/connect: connecting a reachable CLI
/// runtime persists an enabled cli provider (idempotent), unreachable servers fail with
/// 502 CLI_CONNECT_FAILED, and invalid input fails with 400.
/// </summary>
public sealed class ConnectCliRuntimeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-cli-connect", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;
    private FakeAcpServer? _server;
    private string _bin = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_bin);
        _factory = new Factory(_root);
        _server = await FakeAcpServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Connect_ReachableServer_PersistsEnabledCliProvider()
    {
        var binary = Path.Combine(_bin, "claude.exe");
        File.WriteAllText(binary, "");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/cli/connect", JsonContent(new
        {
            driver = "claude-cli",
            display_name = "Claude CLI",
            binary_path = binary,
            server_url = _server!.Url
        }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("claude-cli", body.GetProperty("driver").GetString());
        Assert.Equal("cli", body.GetProperty("connection_kind").GetString());
        Assert.Equal("acp", body.GetProperty("protocol").GetString());
        Assert.Equal(_server.Url, body.GetProperty("server_url").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.True(body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out _));

        var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers")).RootElement;
        var row = list.EnumerateArray().Single(x => x.GetProperty("driver").GetString() == "claude-cli");
        Assert.Equal(_server.Url, row.GetProperty("server_url").GetString());

        var again = await client.PostAsync("/api/v1/model-providers/cli/connect", JsonContent(new
        {
            driver = "claude-cli",
            binary_path = binary,
            server_url = _server.Url
        }));
        var againBody = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(body.GetProperty("id").GetString(), againBody.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Connect_UnreachableServer_FailsWith502()
    {
        var binary = Path.Combine(_bin, "codex.cmd");
        File.WriteAllText(binary, "@exit 1\r\n");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/cli/connect", JsonContent(new
        {
            driver = "codex-cli",
            binary_path = binary,
            server_url = "http://127.0.0.1:59999"
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("CLI_CONNECT_FAILED", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Connect_InvalidInput_Returns400()
    {
        var client = _factory!.CreateClient();

        var missing = await client.PostAsync("/api/v1/model-providers/cli/connect", JsonContent(new
        {
            driver = "claude-cli",
            binary_path = Path.Combine(_bin, "does-not-exist.exe")
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("CLI_CONNECT_INVALID", JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        var notCli = await client.PostAsync("/api/v1/model-providers/cli/connect", JsonContent(new
        {
            driver = "openai",
            binary_path = Path.Combine(_bin, "openai.exe")
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, notCli.StatusCode);
    }

    [Fact]
    public async Task Connect_PersistedProviderShowsAsConfiguredInDiscovery()
    {
        var binary = Path.Combine(_bin, "claude.cmd");
        File.WriteAllText(binary, "@echo ok\r\n");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/cli/connect", JsonContent(new
        {
            driver = "claude-cli",
            binary_path = binary,
            server_url = _server!.Url
        }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var discovery = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers/cli/discover")).RootElement;
        var claude = discovery.GetProperty("cli_runtimes").EnumerateArray().Single(x => x.GetProperty("driver").GetString() == "claude-cli");
        Assert.Equal("configured", claude.GetProperty("status").GetString());
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public Factory(string root) => _root = root;
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}