using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Models.Harness;

namespace TinadecCore.Api.Tests;

/// <summary>
/// POST /api/v1/model-providers/harnesses/connect for the stdio ACP channel.
/// <para>
/// This is the seam where the fabrication lived: connect used to hand an ACP harness to the HTTP
/// process host, which appended a port flag the binary never implemented and then polled a URL that
/// could only answer for <c>opencode serve</c>. What must be true now is narrower and checkable:
/// readiness is a handshake, and what gets stored is the process description (binary_path) rather
/// than an endpoint nobody probed.
/// </para>
/// </summary>
public sealed class ConnectAcpHandshakeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-acp-connect", Guid.NewGuid().ToString("N"));
    private readonly RecordingProber _prober = new();
    private Factory? _factory;
    private string _bin = "";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_bin);
        _factory = new Factory(_root, _prober);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Connect_AcpChannel_ProbesByHandshake_AndStoresNoEndpoint()
    {
        var binary = WriteStub("codebuddy.cmd");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "codebuddy",
            channel = "acp",
            binary_path = binary,
            home_path = "C:\\fake\\home"
        }));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("acp", body.GetProperty("protocol").GetString());
        var providerId = body.GetProperty("id").GetGuid();

        // Readiness came from a handshake attempt for exactly this binary — not from a port answering.
        var probe = Assert.Single(_prober.Calls);
        Assert.Equal("codebuddy", probe.HarnessId);
        Assert.Equal(binary, probe.BinaryPath);
        Assert.Equal("C:\\fake\\home", probe.HomePath);
        // The row did not exist yet, so the probe ran under a throwaway identity rather than reusing
        // some other provider's session root.
        Assert.Null(probe.ProviderInstanceId);

        var row = await GetProviderRowAsync(client, "codebuddy");
        Assert.Equal(binary, row.GetProperty("binary_path").GetString());

        // An ACP session has no URL. Storing one is what made a provider look connected while it could
        // not start, and letting it survive into resolution is the C2 failure: the read path then
        // demands an endpoint this channel never has.
        Assert.False(row.TryGetProperty("server_url", out var serverUrl) && serverUrl.ValueKind != JsonValueKind.Null,
            $"an acp provider must not carry a server_url, got {serverUrl}");
        Assert.Equal("--acp", row.GetProperty("launch_args").GetString());

        // A second connect of the same driver probes as the stored provider, so its session root and
        // any warm session line up with the row that is being re-verified.
        _prober.Calls.Clear();
        var again = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "codebuddy",
            channel = "acp",
            binary_path = binary
        }));
        Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync());
        Assert.Equal(providerId, Assert.Single(_prober.Calls).ProviderInstanceId);
    }

    [Fact]
    public async Task Connect_AcpChannel_HandshakeFailure_ReportsBadGateway_AndStoresNothing()
    {
        var binary = WriteStub("kimi-code.cmd");
        _prober.FailWith = new InvalidOperationException("ACP harness 'kimi-code' exited during startup.");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "kimi-code",
            channel = "acp",
            binary_path = binary
        }));

        Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("CLI_CONNECT_FAILED", body.GetProperty("code").GetString());
        Assert.Contains("exited during startup", body.GetProperty("message").GetString(), StringComparison.Ordinal);

        var providers = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers")).RootElement;
        Assert.DoesNotContain(providers.EnumerateArray(), x => x.GetProperty("driver").GetString() == "kimi-code");
    }

    private string WriteStub(string name)
    {
        var binary = Path.Combine(_bin, name);
        File.WriteAllText(binary, "@echo ok\r\n");
        return binary;
    }

    private static async Task<JsonElement> GetProviderRowAsync(HttpClient client, string driver)
    {
        var providers = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers")).RootElement;
        return providers.EnumerateArray().Single(x => x.GetProperty("driver").GetString() == driver);
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    internal sealed record ProbeCall(Guid? ProviderInstanceId, string HarnessId, string BinaryPath, string? HomePath);

    internal sealed class RecordingProber : IAcpHarnessProber
    {
        public List<ProbeCall> Calls { get; } = [];

        public Exception? FailWith { get; set; }

        public Task<AcpHarnessProbeResult> ProbeAsync(Guid? providerInstanceId, string harnessId, string binaryPath, string? homePath, CancellationToken cancellationToken = default)
        {
            Calls.Add(new ProbeCall(providerInstanceId, harnessId, binaryPath, homePath));
            return FailWith is null
                ? Task.FromResult(new AcpHarnessProbeResult(false, true, false))
                : Task.FromException<AcpHarnessProbeResult>(FailWith);
        }
    }

    private sealed class Factory(string root, IAcpHarnessProber prober) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }))
            .ConfigureTestServices(services =>
            {
                var registered = services.First(d => d.ServiceType == typeof(IAcpHarnessProber));
                services.Remove(registered);
                services.AddSingleton(prober);
            });
    }
}
