using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Models.Harness;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Proves the ACP layer is reachable through the real container rather than only constructed by hand:
/// the module registrar's registrations, the <c>Acp</c> configuration section, and the scratch root
/// resolved against the host's own data root.
/// </summary>
public sealed class AcpHostRegistrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-acp-registration", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void HostRoutersAndOptions_AreRegisteredThroughTheModuleRegistrar()
    {
        var services = _factory!.Services;

        var host = services.GetRequiredService<IAcpSessionHost>();
        var options = services.GetRequiredService<AcpSessionOptions>();
        var routers = services.GetRequiredService<IAcpInteractionRouterFactory>();

        Assert.IsType<AcpSessionHost>(host);
        // Process-wide singleton: sessions are cached per provider instance and the host owns
        // teardown, so a second resolution must not create a second set of process owners.
        Assert.Same(host, services.GetRequiredService<IAcpSessionHost>());
        Assert.Equal(TimeSpan.FromSeconds(90), options.TurnIdleTimeout);
        Assert.Equal("9.9.9", options.ClientVersion);
        Assert.NotSame(routers.Create("session-a"), routers.Create("session-b"));
    }

    [Fact]
    public void UnsetAcpKeys_KeepTheMeasuredDefaults_RatherThanCollapsingToZero()
    {
        var options = _factory!.Services.GetRequiredService<AcpSessionOptions>();

        // Only TurnIdleTimeoutSeconds and ClientVersion are configured in this fixture. Everything
        // else has to come through at its measured value: a hard ceiling that silently became a
        // default timeout would cancel long harness turns.
        Assert.Equal(AcpSessionOptions.Default.PromptHardCeiling, options.PromptHardCeiling);
        Assert.Equal(AcpSessionOptions.Default.DrainQuietWindow, options.DrainQuietWindow);
        Assert.Equal(AcpSessionOptions.Default.CancelGrace, options.CancelGrace);
    }

    [Fact]
    public void ScratchDirectory_IsCreatedUnderTheHostsOwnTempRoot()
    {
        var host = (AcpSessionHost)_factory!.Services.GetRequiredService<IAcpSessionHost>();
        var provider = Guid.NewGuid();

        var scratch = host.ScratchDirectoryFor(provider);

        // The governed scratch root lives under Temp, not Data: StoragePaths.HarnessWorkspace moved
        // it off the older "acp-sessions" name so harness scratch files stay out of the backed-up
        // data tree. Ask the mounted scope where Temp is instead of pinning a location the product
        // has already moved once.
        var temp = _factory.Services.GetRequiredService<IScopeStorageLocations>().Temp;
        Assert.StartsWith(Path.GetFullPath(temp), Path.GetFullPath(scratch), StringComparison.Ordinal);
        Assert.True(Directory.Exists(scratch), "the host must create the governed directory, not only name it");
        // Stable across calls: a chat client is built per model invocation, and a directory that moved
        // between invocations would be read as a moved session root and refused.
        Assert.Equal(scratch, host.ScratchDirectoryFor(provider));

        // One boundary per provider across channels: the headless client asks the roots port, the ACP
        // session asks the host, and both must land in the same directory — otherwise the same harness
        // would edit files in two places depending on which channel a run happened to choose.
        Assert.Equal(scratch, _factory!.Services.GetRequiredService<IHarnessWorkspaceRoots>().ForProvider(provider));
    }

    /// <summary>
    /// The registration is the risk, not the class: the chat client factory takes the ACP session host
    /// through a factory lambda now, so a missing or mistyped line leaves the ACP branch throwing at
    /// run time while every unit test still passes. This resolves the real container and asks it for an
    /// ACP client, which also proves the harness catalog can supply argv for the driver before any
    /// process is spawned.
    /// </summary>
    [Fact]
    public async Task ChatClientFactory_ResolvesAnAcpClientThroughTheRealContainer()
    {
        var factory = _factory!.Services.GetRequiredService<IAgentChatClientFactory>();
        var resolution = new ChatResolution
        {
            Protocol = ChatProtocols.Acp,
            ProviderInstanceId = Guid.NewGuid(),
            BinaryPath = "fake-opencode-binary",
            ModelId = "opencode/deepseek-v3"
        };

        using var client = await factory.CreateAsync(resolution);

        Assert.Equal("AcpStdioChatClient", client.GetType().Name);
    }

    private sealed class Factory : IsolatedApiFactory
    {
        private readonly string _root;

        public Factory(string root) => _root = root;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Acp:TurnIdleTimeoutSeconds"] = "90",
            ["Acp:ClientVersion"] = "9.9.9",
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}
