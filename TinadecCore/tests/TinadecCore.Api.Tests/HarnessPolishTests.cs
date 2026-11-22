using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness;

namespace TinadecCore.Api.Tests;

public sealed class HarnessPolishTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("harness-polish");
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
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public void CatalogAndResolver_SupportNewHarnessesCustomAcpPs1AndNodeShebang()
    {
        foreach (var id in new[] { "agy", "gemini-cli", "qwen-code", "minimax-code", "pi", "custom-acp" })
        {
            var spec = HarnessCatalog.Find(id);
            Assert.NotNull(spec);
            Assert.Equal(ChatProtocols.Acp, HarnessCatalog.ProtocolFor(id, AgentChannels.Acp));
        }

        Assert.Empty(HarnessCatalog.ResolveAcpArgv("custom-acp", null));
        Assert.Equal(["--acp", "--model", "fast"], HarnessCatalog.ResolveAcpArgv("custom-acp", "--acp --model \"fast\""));

        var binDir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(binDir);
        var ps1 = Path.Combine(binDir, "pi.ps1");
        File.WriteAllText(ps1, "Write-Output '1.0.0'\r\n");
        var nodeScript = Path.Combine(binDir, "codebuddy");
        File.WriteAllText(nodeScript, "#!/usr/bin/env node\nconsole.log('1.0.0');\n");
        var shScript = Path.Combine(binDir, "dsh");
        File.WriteAllText(shScript, "#!/bin/sh\necho 1.0.0\n");

        var resolver = new HarnessBinaryResolver(new Dictionary<string, string>(StringComparer.Ordinal));
        var resolvedPi = resolver.Resolve("pi", [binDir]);
        var resolvedNode = resolver.Resolve("codebuddy", [binDir]);
        var resolvedSh = resolver.Resolve("dsh", [binDir]);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ps1, resolvedPi?.BinaryPath);
            Assert.Equal(nodeScript, resolvedNode?.BinaryPath);
            Assert.Null(resolvedSh);

            var psPsi = new ProcessStartInfo();
            HarnessProcessCommand.Apply(psPsi, ps1, ["--acp"]);
            Assert.Equal("powershell.exe", psPsi.FileName);
            Assert.Contains("-File", psPsi.ArgumentList);

            var nodePsi = new ProcessStartInfo();
            HarnessProcessCommand.Apply(nodePsi, nodeScript, ["--acp"]);
            Assert.Equal("node", nodePsi.FileName);
            Assert.Equal([nodeScript, "--acp"], nodePsi.ArgumentList);
        }
        else
        {
            Assert.NotNull(resolvedNode);
        }
    }

    [Fact]
    public async Task DiscoverAndConnectHarness_SurfacePreconfigCapabilitiesAndCustomAcp()
    {
        var fakeExe = Path.Combine(_root, "my-custom-acp.exe");
        await File.WriteAllTextAsync(fakeExe, "stub");

        using var client = _factory!.CreateClient();

        var discoverRes = await client.GetAsync("/api/v1/model-providers/harnesses/discover");
        Assert.Equal(HttpStatusCode.OK, discoverRes.StatusCode);
        var discoverJson = await discoverRes.Content.ReadFromJsonAsync<JsonElement>();
        var runtimes = discoverJson.GetProperty("cli_runtimes").EnumerateArray().ToList();

        var custom = runtimes.Single(r => r.GetProperty("driver").GetString() == "custom-acp");
        Assert.True(custom.GetProperty("is_template").GetBoolean());
        Assert.Equal("template", custom.GetProperty("status").GetString());
        Assert.Equal("acp", custom.GetProperty("default_channel").GetString());
        Assert.Equal("acp", custom.GetProperty("default_protocol").GetString());

        var agy = runtimes.Single(r => r.GetProperty("driver").GetString() == "agy");
        Assert.Equal("acp", agy.GetProperty("default_channel").GetString());
        Assert.Equal("acp", agy.GetProperty("default_launch_args").GetString());
        Assert.Contains("GEMINI_API_KEY", agy.GetProperty("env_vars").EnumerateArray().Select(x => x.GetString()));

        // Connect custom-acp with custom launch_args and verify acp_capabilities round-trip
        var connectRes = await client.PostAsJsonAsync("/api/v1/model-providers/harnesses/connect", new
        {
            driver = "custom-acp",
            channel = "acp",
            protocol = "acp",
            display_name = "My Custom ACP",
            binary_path = fakeExe,
            launch_args = "--stdio --mode fast",
            home_path = _root,
            model = "custom-default",
            models = new[] { "custom-default" }
        });
        Assert.Equal(HttpStatusCode.OK, connectRes.StatusCode);
        Assert.Equal("--stdio --mode fast", _factory.Prober.LastLaunchArgs);

        var listRes = await client.GetAsync("/api/v1/model-providers");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var providers = await listRes.Content.ReadFromJsonAsync<JsonElement[]>();
        var saved = providers!.Single(p => p.GetProperty("driver").GetString() == "custom-acp");
        var caps = saved.GetProperty("acp_capabilities");
        Assert.True(caps.GetProperty("load_session").GetBoolean());
        Assert.True(caps.GetProperty("image_input").GetBoolean());
        Assert.False(caps.GetProperty("session_fork").GetBoolean());
        Assert.Equal("acp_initialize", caps.GetProperty("source").GetString());

        // Actionable diagnostic hint when ACP probe fails
        _factory.Prober.NextException = new InvalidOperationException("ACP unauthorized: login required");
        var failRes = await client.PostAsJsonAsync("/api/v1/model-providers/harnesses/connect", new
        {
            driver = "agy",
            channel = "acp",
            protocol = "acp",
            binary_path = fakeExe
        });
        Assert.Equal(HttpStatusCode.BadGateway, failRes.StatusCode);
        var failJson = await failRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CLI_CONNECT_FAILED", failJson.GetProperty("code").GetString());
        Assert.Contains("GEMINI_API_KEY", failJson.GetProperty("hint").GetString());
    }

    private sealed class FakeAcpProber : IAcpHarnessProber
    {
        public string? LastLaunchArgs { get; private set; }
        public Exception? NextException { get; set; }

        public Task<AcpHarnessProbeResult> ProbeAsync(Guid? providerInstanceId, string harnessId, string binaryPath, string? homePath, CancellationToken cancellationToken = default) =>
            ProbeAsync(providerInstanceId, harnessId, binaryPath, homePath, null, cancellationToken);

        public Task<AcpHarnessProbeResult> ProbeAsync(Guid? providerInstanceId, string harnessId, string binaryPath, string? homePath, string? launchArgs, CancellationToken cancellationToken = default)
        {
            LastLaunchArgs = launchArgs;
            if (NextException is { } ex) return Task.FromException<AcpHarnessProbeResult>(ex);
            return Task.FromResult(new AcpHarnessProbeResult(LoadSession: true, ImageInput: true, SessionFork: false));
        }
    }

    private sealed class Factory(string root) : IsolatedApiFactory
    {
        public FakeAcpProber Prober { get; } = new();

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IAcpHarnessProber>(Prober));
            });
        }
    }
}
