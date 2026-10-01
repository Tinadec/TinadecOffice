using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The resource ledger's conflict rule, tested as a pure function. These are the cases that decide
/// whether two agents may touch the same thing: getting them wrong either lets a second writer
/// silently destroy the first, or blocks work that was never in conflict.
/// </summary>
public sealed class ResourceLeasePolicyTests
{
    private static ResourceLeaseInfo Lease(string key, bool exclusive, Guid? runId, string kind = ResourceLeaseKinds.Path, string status = ResourceLeaseStatuses.Active) =>
        new(Guid.NewGuid(), kind, key, exclusive, Guid.NewGuid(), runId, null, null, status);

    private static ResourceClaim Claim(string key, bool exclusive, string kind = ResourceLeaseKinds.Path) => new(kind, key, exclusive);

    [Fact]
    public void AnExclusiveClaimCollidesWithAnythingHoldingTheSameKey()
    {
        var writer = Claim("/repo/a.ts", exclusive: true);

        Assert.True(ResourceLeasePolicy.Conflicts(writer, new ResourceClaim(ResourceLeaseKinds.Path, "/repo/a.ts", Exclusive: true)));
        // A writer also collides with a reader: the reader may be mid-way through what the write changes.
        Assert.True(ResourceLeasePolicy.Conflicts(writer, new ResourceClaim(ResourceLeaseKinds.Path, "/repo/a.ts", Exclusive: false)));
    }

    [Fact]
    public void SharedReadersCoexist()
    {
        Assert.False(ResourceLeasePolicy.Conflicts(
            Claim("/repo/a.ts", exclusive: false),
            new ResourceClaim(ResourceLeaseKinds.Path, "/repo/a.ts", Exclusive: false)));
    }

    [Fact]
    public void DifferentKeysNeverCollide()
    {
        Assert.False(ResourceLeasePolicy.Conflicts(
            Claim("/repo/a.ts", exclusive: true),
            new ResourceClaim(ResourceLeaseKinds.Path, "/repo/b.ts", Exclusive: true)));
        // Segment boundaries, not string prefixes: /repo does not cover /repository.
        Assert.False(ResourceLeasePolicy.Conflicts(
            Claim("/repo", exclusive: true),
            new ResourceClaim(ResourceLeaseKinds.Path, "/repository/a.ts", Exclusive: true)));
        // Opaque kinds compare by kind and name, never by containment.
        Assert.False(ResourceLeasePolicy.Conflicts(
            Claim("env-1", exclusive: true, kind: ResourceLeaseKinds.Environment),
            new ResourceClaim(ResourceLeaseKinds.Terminal, "env-1", Exclusive: true)));
    }

    [Fact]
    public void AWorktreeAndAPathShareTheFilesystemSoContainmentApplies()
    {
        // A worktree assigned to one run is a directory: another run's write inside it must collide,
        // and a directory claim (a declared write scope) covers the files under it.
        Assert.True(ResourceLeasePolicy.Conflicts(
            Claim("/repo/wt-1", exclusive: true, kind: ResourceLeaseKinds.Worktree),
            new ResourceClaim(ResourceLeaseKinds.Path, "/repo/wt-1/a.ts", Exclusive: true)));
        Assert.True(ResourceLeasePolicy.Conflicts(
            Claim("/repo/src/a.ts", exclusive: true),
            new ResourceClaim(ResourceLeaseKinds.Path, "/repo/src", Exclusive: true)));
    }

    [Fact]
    public void NormalizationFoldsCaseAndSeparatorsAndAncestorsWalkToTheRoot()
    {
        Assert.Equal("c:/repo/src/a.ts", ResourceLeasePolicy.Normalize("C:\\Repo\\src\\A.ts\\"));
        Assert.Equal(["c:/repo/src/a.ts", "c:/repo/src", "c:/repo", "c:"], ResourceLeasePolicy.SelfAndAncestors("c:/repo/src/a.ts"));
        Assert.Equal(["/repo/a.ts", "/repo"], ResourceLeasePolicy.SelfAndAncestors("/repo/a.ts"));
    }

    [Fact]
    public void OnlyAWriteScopeDefinesWhatIsOutsideIt()
    {
        var runId = Guid.NewGuid();
        var scope = new ResourceLeaseInfo(Guid.NewGuid(), ResourceLeaseKinds.Path, "C:/repo/src", true, null, runId, Guid.NewGuid(), null,
            ResourceLeaseStatuses.Active, ResourceLeasePurposes.WriteScope);
        var toolClaim = scope with { ResourceKey = "C:/repo/README.md", Purpose = ResourceLeasePurposes.Tool };

        Assert.Equal(["C:/repo/docs/x.md"], ResourceLeasePolicy.OutsideScope(["C:/repo/src/a.ts", "C:/repo/docs/x.md"], [scope, toolClaim]));
        // No declared scope: nothing is "outside" it, the envelope is the only bound.
        Assert.Empty(ResourceLeasePolicy.OutsideScope(["C:/repo/docs/x.md"], [toolClaim]));
    }

    [Fact]
    public void KeyComparisonIsCaseInsensitiveBecauseWindowsPathsAre()
    {
        Assert.True(ResourceLeasePolicy.Conflicts(
            Claim("C:/Repo/A.ts", exclusive: true),
            new ResourceClaim(ResourceLeaseKinds.Path, "c:/repo/a.ts", Exclusive: true)));
    }

    [Fact]
    public void ARunDoesNotConflictWithItsOwnLease()
    {
        var runId = Guid.NewGuid();
        var held = Lease("/repo/a.ts", exclusive: true, runId);

        Assert.Empty(ResourceLeasePolicy.Conflicts(Claim("/repo/a.ts", exclusive: true), runId, [held]));
    }

    [Fact]
    public void AnotherRunHoldingTheSamePathIsReportedAsTheConflict()
    {
        var held = Lease("/repo/a.ts", exclusive: true, Guid.NewGuid());

        var conflicts = ResourceLeasePolicy.Conflicts(Claim("/repo/a.ts", exclusive: true), Guid.NewGuid(), [held]);

        Assert.Same(held, Assert.Single(conflicts));
    }

    [Fact]
    public void ReleasedLeasesNeverConflict()
    {
        var released = Lease("/repo/a.ts", exclusive: true, Guid.NewGuid(), status: ResourceLeaseStatuses.Released);

        Assert.Empty(ResourceLeasePolicy.Conflicts(Claim("/repo/a.ts", exclusive: true), Guid.NewGuid(), [released]));
    }

    [Fact]
    public void AHostHeldLeaseBlocksEveryRunBecauseItBelongsToNoRun()
    {
        // A lease with no run was taken by the host itself. Treating it as "nobody" would let a run
        // take a resource the host had already reserved.
        var hostHeld = Lease("/repo/a.ts", exclusive: true, runId: null);

        Assert.Single(ResourceLeasePolicy.Conflicts(Claim("/repo/a.ts", exclusive: true), Guid.NewGuid(), [hostHeld]));
    }

    [Fact]
    public void AcquiringUnderNoRunStillDoesNotCollideWithItself()
    {
        var lease = Lease("/repo/a.ts", exclusive: true, runId: null);

        Assert.Empty(ResourceLeasePolicy.Conflicts(Claim("/repo/a.ts", exclusive: true), null, [lease]));
    }

    [Fact]
    public void EveryActiveConflictIsReportedNotJustTheFirst()
    {
        var first = Lease("/repo/a.ts", exclusive: true, Guid.NewGuid());
        var second = Lease("/repo/a.ts", exclusive: true, Guid.NewGuid());

        Assert.Equal(2, ResourceLeasePolicy.Conflicts(Claim("/repo/a.ts", exclusive: true), Guid.NewGuid(), [first, second]).Count);
    }
}
