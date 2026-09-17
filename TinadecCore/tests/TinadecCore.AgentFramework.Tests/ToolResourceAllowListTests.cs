using TinadecCore.Tools;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// WS-4/WS-8 pinning: the resource-allow decision step. An empty grant list is
/// the fail-closed "no workspace authorization" state (never "unrestricted"),
/// the historical coarse tokens are not grants any more, and a call that carries
/// a workspace-relative target must fall inside a matching level-appropriate
/// prefix. Write implies read; a tool without a single path stays level-only.
/// </summary>
public sealed class ToolResourceAllowListTests
{
    [Fact]
    public void EmptyGrant_IsFailClosed_NoWorkspaceAuthorization()
    {
        var decision = ToolResourceAllowList.Evaluate([], "src/app.cs", mutating: false);
        Assert.False(decision.Allowed);
        Assert.Equal(ResourceAllowBasis.NoGrant, decision.Basis);
        Assert.False(ToolResourceAllowList.IsAllowed([]));
    }

    [Fact]
    public void EmptyPrefix_GrantsTheWholeWorkspace()
    {
        var read = ToolResourceAllowList.Evaluate(["read:"], "any/nested/path.txt", mutating: false);
        Assert.True(read.Allowed);
        Assert.Equal(ResourceAllowBasis.Granted, read.Basis);

        var write = ToolResourceAllowList.Evaluate(["write:"], "any/nested/path.txt", mutating: true);
        Assert.True(write.Allowed);

        Assert.True(ToolResourceAllowList.IsAllowed(["read:src"]));
    }

    [Fact]
    public void PrefixGrant_AllowsInside_AndDeniesOutside()
    {
        Assert.True(ToolResourceAllowList.Evaluate(["read:src/docs"], "src/docs/guide.md", false).Allowed);
        Assert.True(ToolResourceAllowList.Evaluate(["read:src/docs"], "src/docs", false).Allowed);

        var outside = ToolResourceAllowList.Evaluate(["read:src/docs"], "src/other/guide.md", false);
        Assert.False(outside.Allowed);
        Assert.Equal(ResourceAllowBasis.PathDenied, outside.Basis);

        // Matching needs a path-segment boundary: "src/doc" must not cover "src/docs/x".
        Assert.False(ToolResourceAllowList.Evaluate(["read:src/doc"], "src/docs/x.md", false).Allowed);
    }

    [Fact]
    public void ReadGrant_DoesNotActAsAWriteGrant_SoTheCallUpgradesToAnApproval()
    {
        // The envelope never implies write access, but it must not be the thing that
        // refuses either: refusing here is what made "write a file" unreachable,
        // because the request never reached the gate that could authorize it.
        var upgrade = ToolResourceAllowList.Evaluate(["read:src"], "src/app.cs", mutating: true);
        Assert.False(upgrade.Allowed);
        Assert.True(upgrade.RequiresApproval);
        Assert.Equal(ResourceAllowBasis.LevelDenied, upgrade.Basis);

        Assert.True(ToolResourceAllowList.Evaluate(["write:src"], "src/app.cs", mutating: false).Allowed);
        Assert.True(ToolResourceAllowList.Evaluate(["write:src"], "src/app.cs", mutating: true).Allowed);
    }

    [Fact]
    public void PathlessTool_DecidesOnLevelAlone()
    {
        // shell / mcp_* / git_* carry no single path: a level-matching grant authorizes.
        Assert.True(ToolResourceAllowList.Evaluate(["read:src"], null, mutating: false).Allowed);
        Assert.True(ToolResourceAllowList.Evaluate(["write:src"], null, mutating: true).Allowed);

        // The level still applies: a read-only grant needs approval to mutate.
        var upgrade = ToolResourceAllowList.Evaluate(["read:src"], null, mutating: true);
        Assert.False(upgrade.Allowed);
        Assert.True(upgrade.RequiresApproval);
        Assert.Equal(ResourceAllowBasis.LevelDenied, upgrade.Basis);
    }

    [Fact]
    public void UnregisteredGrantForms_AreNotGrants_AndNeverUpgrade()
    {
        // The retired coarse tokens ("workspace"/"project") are not level grants.
        Assert.False(ToolResourceAllowList.Evaluate(["workspace"], "src/app.cs", false).Allowed);
        Assert.False(ToolResourceAllowList.Evaluate(["workspace"], null, false).Allowed);
        Assert.Equal(ResourceAllowBasis.LevelDenied, ToolResourceAllowList.Evaluate(["workspace"], null, false).Basis);

        // An envelope whose grants are ALL unrecognized has nothing to upgrade from:
        // it must refuse rather than ask, or a corrupt grant list would become a
        // route to an approval the operator never provisioned.
        var corrupt = ToolResourceAllowList.Evaluate(["workspace"], null, mutating: true);
        Assert.False(corrupt.Allowed);
        Assert.False(corrupt.RequiresApproval);
        Assert.Equal(ResourceAllowBasis.LevelDenied, corrupt.Basis);
    }

    [Fact]
    public void EnvelopeEscapes_AndEmptyEnvelopes_NeverUpgrade()
    {
        // A target outside the workspace is an escape attempt, not a missing grant —
        // even when a write grant exists at some other prefix.
        var escape = ToolResourceAllowList.Evaluate(["write:"], "../outside.txt", mutating: true);
        Assert.False(escape.Allowed);
        Assert.False(escape.RequiresApproval);
        Assert.Equal(ResourceAllowBasis.PathDenied, escape.Basis);

        // A granted level whose prefix does not cover the target is likewise refused.
        var outsidePrefix = ToolResourceAllowList.Evaluate(["write:docs"], "src/app.cs", mutating: true);
        Assert.False(outsidePrefix.Allowed);
        Assert.False(outsidePrefix.RequiresApproval);
        Assert.Equal(ResourceAllowBasis.PathDenied, outsidePrefix.Basis);

        // No envelope at all: nothing to upgrade, so it never asks.
        var noGrant = ToolResourceAllowList.Evaluate([], "src/app.cs", mutating: true);
        Assert.False(noGrant.Allowed);
        Assert.False(noGrant.RequiresApproval);
        Assert.Equal(ResourceAllowBasis.NoGrant, noGrant.Basis);
    }
}
