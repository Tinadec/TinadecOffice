using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TinadecCore.Models.Harness;
using TinadecCore.Models.Harness.Acp;
using TinadecCore.Persistence;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// Session lifetime policy: one live harness per provider instance, a governed scratch root that is
/// Core's to mint, and teardown that leaves no agent server running.
/// </summary>
public sealed class AcpSessionHostTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "tinadec-acp-host", Guid.NewGuid().ToString("N"));

    public AcpSessionHostTests() => Directory.CreateDirectory(_contentRoot);

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            try
            {
                Directory.Delete(_contentRoot, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static (AcpSessionHost Host, FakeAcpGenerationPool Pool) CreateHost(string contentRoot)
    {
        var paths = new StoragePaths(contentRoot, Options.Create(new TinadecPersistenceOptions { DataRoot = "data" }));
        var pool = new FakeAcpGenerationPool();
        var host = new AcpSessionHost(AcpSessionTestSupport.FastOptions(), new HarnessWorkspaceRoots(paths), new RefusingAcpInteractionRouterFactory(),
            NullLogger<AcpSessionHost>.Instance, pool.Create);
        return (host, pool);
    }

    [Fact]
    public async Task Acquire_ReusesOneSessionPerProviderInstance()
    {
        var (host, pool) = CreateHost(_contentRoot);
        var scratch = host.ScratchDirectoryFor(Guid.NewGuid());
        var request = AcpSessionTestSupport.Request(scratch);

        var first = await host.AcquireAsync(request);
        var second = await host.AcquireAsync(request);

        // An ACP agent is a long-running process holding its own context; reconnecting per message
        // would throw away exactly the continuity the channel exists to provide.
        Assert.Same(first, second);
        Assert.Equal(1, pool.Count);

        await host.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_WhenTheScratchRootMoved_IsRefusedRatherThanSilentlyReused()
    {
        var (host, pool) = CreateHost(_contentRoot);
        var provider = Guid.NewGuid();

        await host.AcquireAsync(AcpSessionTestSupport.Request(host.ScratchDirectoryFor(provider), provider));

        // The directory was decided when the session opened and the agent has already written inside
        // it. Running the next turn somewhere else would silently change what the harness can reach.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.AcquireAsync(AcpSessionTestSupport.Request(AcpSessionTestSupport.ScratchRoot("moved-after-open"), provider)));

        Assert.Contains("already has a session rooted at", error.Message, StringComparison.Ordinal);

        await host.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_AfterTheSessionFaulted_OpensAFreshHarness()
    {
        var (host, pool) = CreateHost(_contentRoot);
        pool.Configure = agent => agent.SuspendPrompts = true;
        var scratch = host.ScratchDirectoryFor(Guid.NewGuid());
        var request = AcpSessionTestSupport.Request(scratch);

        var faulted = await host.AcquireAsync(request);
        var observer = new RecordingAcpTurnObserver();
        await Assert.ThrowsAsync<AcpTurnIdleException>(() => faulted.PromptAsync("hi", observer, CancellationToken.None));

        pool.Configure = agent => agent.SuspendPrompts = false;
        var reopened = await host.AcquireAsync(request);

        Assert.NotSame(faulted, reopened);
        Assert.Equal(2, pool.Count);

        await host.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_KillsEveryHostedHarness_SoNoneOutlivesTheHost()
    {
        var (host, pool) = CreateHost(_contentRoot);
        var first = await host.AcquireAsync(AcpSessionTestSupport.Request(host.ScratchDirectoryFor(Guid.NewGuid())));
        var second = await host.AcquireAsync(AcpSessionTestSupport.Request(host.ScratchDirectoryFor(Guid.NewGuid())));

        Assert.NotSame(first, second);
        Assert.Equal(2, pool.Count);

        await host.DisposeAsync();

        Assert.True(pool.TransportAt(0).WasKilled);
        Assert.True(pool.TransportAt(1).WasKilled);
    }

    [Fact]
    public async Task Drop_ClosesTheCachedSessionSoTheNextAcquireReconnects()
    {
        var (host, pool) = CreateHost(_contentRoot);
        var request = AcpSessionTestSupport.Request(host.ScratchDirectoryFor(Guid.NewGuid()));

        var session = await host.AcquireAsync(request);
        await host.DropAsync(request.ProviderInstanceId);
        var replacement = await host.AcquireAsync(request);

        Assert.NotSame(session, replacement);
        Assert.True(pool.TransportAt(0).WasKilled);

        await host.DisposeAsync();
    }

    [Fact]
    public void ScratchDirectoryFor_IsStablePerProvider_AndDistinctAcrossProviders()
    {
        // Stability is what makes a session reusable: a chat client is built per model invocation, so
        // a directory minted per call would be read as a moved scratch root and refused. Distinctness
        // across providers is what keeps two harnesses from writing into each other's tree.
        var (host, _) = CreateHost(_contentRoot);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // The scratch root lives under Temp, not Data: StoragePaths.HarnessWorkspace moved it off
        // "acp-sessions" so harness scratch files stay out of the backed-up data tree. Asking
        // StoragePaths for the root keeps this assertion honest across the next move.
        var paths = new StoragePaths(_contentRoot, Options.Create(new TinadecPersistenceOptions { DataRoot = "data" }));
        var root = Path.GetFullPath(paths.Locations.Temp);
        var actual = Path.GetFullPath(host.ScratchDirectoryFor(first));
        Assert.StartsWith(root, actual, StringComparison.Ordinal);
        Assert.Contains("harness", actual, StringComparison.Ordinal);
        Assert.Equal(host.ScratchDirectoryFor(first), host.ScratchDirectoryFor(first));
        Assert.True(Directory.Exists(host.ScratchDirectoryFor(first)));
        Assert.NotEqual(host.ScratchDirectoryFor(first), host.ScratchDirectoryFor(second));
        // The path is Core-minted from GUIDs, so nothing an agent names can steer it out of the root.
        Assert.DoesNotContain("..", host.ScratchDirectoryFor(first), StringComparison.Ordinal);
    }

    [Fact]
    public void DrainWindow_CannotOutliveTheIdleBudget()
    {
        // A settling turn produces no progress by definition, so a drain window longer than the idle
        // budget would fault every turn that ends without a final text chunk.
        var options = AcpSessionOptions.Default with
        {
            TurnIdleTimeout = TimeSpan.FromMilliseconds(100),
            DrainMaxWindow = TimeSpan.FromSeconds(5)
        };

        Assert.Equal(TimeSpan.FromMilliseconds(100), options.EffectiveDrainMaxWindow);

        var sane = AcpSessionOptions.Default;
        Assert.Equal(sane.DrainMaxWindow, sane.EffectiveDrainMaxWindow);
    }

    [Fact]
    public void SessionOptions_WithoutConfiguration_KeepTheMeasuredDefaults()
    {
        // The defaults are measured values from a live adapter, not round numbers: an absent
        // configuration section must not quietly collapse a 2-hour ceiling into a default timeout.
        var options = AcpSessionOptions.From(null);

        Assert.Equal(AcpSessionOptions.Default.PromptHardCeiling, options.PromptHardCeiling);
        Assert.Equal(AcpSessionOptions.Default.TurnIdleTimeout, options.TurnIdleTimeout);
        Assert.Equal(AcpSessionOptions.Default.DrainQuietWindow, options.DrainQuietWindow);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.DrainQuietWindow);
        Assert.Equal("tinadec-core", options.ClientName);
    }
}
