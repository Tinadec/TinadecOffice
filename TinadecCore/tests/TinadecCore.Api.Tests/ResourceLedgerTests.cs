using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The resource ledger against a real database: overlap is found through ancestors and descendants
/// in SQL (not by scanning), acquisition is serialized so concurrent claimants cannot both win, and a
/// lease whose run is gone is reclaimed by the next claimant instead of blocking it forever.
/// </summary>
public sealed class ResourceLedgerTests : IDisposable
{
    private readonly ApiEndpointFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private IResourceLeaseService Ledger(IRunLivenessProbe? probe = null)
    {
        if (probe is null) return _factory.Services.GetRequiredService<IResourceLeaseService>();
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(probe)));
        return factory.Services.GetRequiredService<IResourceLeaseService>();
    }

    private static ResourceAcquireRequest Request(string key, Guid runId, Guid? taskId = null, string purpose = ResourceLeasePurposes.Tool, string kind = ResourceLeaseKinds.Path) =>
        new(new ResourceClaim(kind, key, Exclusive: true), SessionId: null, runId, taskId ?? Guid.NewGuid(), null, "test", purpose);

    [Fact]
    public async Task OverlapIsFoundThroughAncestorsAndDescendantsWhateverTheSpelling()
    {
        var ledger = Ledger();
        var holder = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.True((await ledger.AcquireAsync(Request("C:\\Ws\\Src", holder, purpose: ResourceLeasePurposes.WriteScope))).Granted);

        // A file under the held directory, in another spelling.
        var under = await ledger.AcquireAsync(Request("c:/ws/src/a.ts", other));
        Assert.False(under.Granted);
        Assert.Equal(holder, Assert.Single(under.Conflicts).RunId);
        Assert.Equal(ResourceLeasePurposes.WriteScope, under.Conflicts[0].Purpose);
        // A directory above the held one covers it.
        Assert.False((await ledger.AcquireAsync(Request("C:/ws", other))).Granted);
        // A sibling is free.
        Assert.True((await ledger.AcquireAsync(Request("C:/ws/docs/b.md", other))).Granted);
    }

    [Fact]
    public async Task ReacquiringTheSameClaimReturnsTheSameLeaseAndTheOwnRunNeverConflicts()
    {
        var ledger = Ledger();
        var runId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var first = await ledger.AcquireAsync(Request("C:/ws/a.ts", runId, taskId));
        var again = await ledger.AcquireAsync(Request("C:/ws/a.ts", runId, taskId));
        var sibling = await ledger.AcquireAsync(Request("C:/ws/a.ts", runId, Guid.NewGuid()));

        Assert.Equal(first.Lease!.Id, again.Lease!.Id);
        Assert.False(sibling.Granted);
        Assert.Equal(taskId, Assert.Single(sibling.Conflicts).TaskId);
    }

    [Fact]
    public async Task ConcurrentClaimantsOnOneResourceCannotBothWin()
    {
        var ledger = Ledger();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ledger.AcquireAsync(Request("C:/ws/contended.ts", Guid.NewGuid())))));

        Assert.Single(attempts, decision => decision.Granted);
        Assert.All(attempts.Where(decision => !decision.Granted), decision => Assert.NotEmpty(decision.Conflicts));
    }

    [Fact]
    public async Task ALeaseWhoseRunIsGoneIsReclaimedByTheNextClaimant()
    {
        var crashed = Guid.NewGuid();
        var probe = new FixedLiveness(dead: crashed);
        var ledger = Ledger(probe);
        Assert.True((await ledger.AcquireAsync(Request("C:/ws/src", crashed, purpose: ResourceLeasePurposes.WriteScope))).Granted);

        var next = await ledger.AcquireAsync(Request("C:/ws/src/a.ts", Guid.NewGuid()));

        Assert.True(next.Granted);
        Assert.DoesNotContain(await ledger.ListActiveAsync(), lease => lease.RunId == crashed);
        Assert.Contains(crashed, probe.Asked);
    }

    [Fact]
    public async Task ALiveHolderIsNeverReclaimedAndAProbeWritesNothing()
    {
        var holder = Guid.NewGuid();
        var ledger = Ledger(new FixedLiveness(dead: null));
        Assert.True((await ledger.AcquireAsync(Request("C:/ws/src", holder))).Granted);

        var probed = await ledger.ProbeAsync(new ResourceClaim(ResourceLeaseKinds.Path, "C:/ws/src/x.ts", Exclusive: true), Guid.NewGuid());

        Assert.Equal(holder, Assert.Single(probed).RunId);
        Assert.Single(await ledger.ListActiveAsync());
    }

    [Fact]
    public async Task ReleasingATaskFreesOnlyThatTasksLeases()
    {
        var ledger = Ledger();
        var runId = Guid.NewGuid();
        var done = Guid.NewGuid();
        var running = Guid.NewGuid();
        await ledger.AcquireAsync(Request("C:/ws/a.ts", runId, done));
        await ledger.AcquireAsync(Request("C:/ws/b.ts", runId, running));

        Assert.Equal(1, await ledger.ReleaseTaskAsync(runId, done));

        var left = Assert.Single(await ledger.ListTaskAsync(runId, running));
        Assert.Equal("C:/ws/b.ts", left.ResourceKey);
        Assert.Empty(await ledger.ListTaskAsync(runId, done));
    }

    private sealed class FixedLiveness(Guid? dead) : IRunLivenessProbe
    {
        public List<Guid> Asked { get; } = [];

        public Task<bool> IsLiveAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            lock (Asked) Asked.Add(runId);
            return Task.FromResult(runId != dead);
        }
    }
}
