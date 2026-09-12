using TinadecCore.Tools;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// S9 Phase 1 pinning: the named resource-allow decision step must preserve the
/// historical boolean behavior exactly — empty grant unrestricted, coarse
/// workspace/project token unrestricted, otherwise full-path or prefix match —
/// and must expose WHY it decided, so the Phase 2 envelope takeover (replacing
/// the six ["workspace"] seeds with per-instance grants) cannot silently change
/// semantics.
/// </summary>
public sealed class ToolResourceAllowListTests
{
    [Fact]
    public void EmptyGrant_IsUnrestricted_ByHistoricalBehaviour()
    {
        var decision = ToolResourceAllowList.Evaluate([], @"C:\any\where\file.txt");
        Assert.True(decision.Allowed);
        Assert.Equal(ResourceAllowBasis.UnrestrictedEmptyGrant, decision.Basis);
    }

    [Fact]
    public void CoarseWorkspaceToken_IsUnrestricted()
    {
        var decision = ToolResourceAllowList.Evaluate(["workspace"], @"C:\any\where\file.txt");
        Assert.True(decision.Allowed);
        Assert.Equal(ResourceAllowBasis.CoarseToken, decision.Basis);
        Assert.Equal("workspace", decision.MatchedGrant);
    }

    [Fact]
    public void CoarseProjectToken_IsUnrestricted()
    {
        var decision = ToolResourceAllowList.Evaluate(["project"], "/tmp/x");
        Assert.True(decision.Allowed);
        Assert.Equal(ResourceAllowBasis.CoarseToken, decision.Basis);
    }

    [Fact]
    public void PathGrant_AllowsEqualOrNestedPaths_Only()
    {
        var grants = new[] { @"C:\ws\repo" };
        Assert.True(ToolResourceAllowList.Evaluate(grants, @"C:\ws\repo").Allowed);
        Assert.True(ToolResourceAllowList.Evaluate(grants, @"C:\ws\repo\src\file.cs").Allowed);
        // sibling prefix must not match (repo2 is not under repo)
        Assert.False(ToolResourceAllowList.Evaluate(grants, @"C:\ws\repo2\file.cs").Allowed);
        Assert.Equal(ResourceAllowBasis.PathMatch, ToolResourceAllowList.Evaluate(grants, @"C:\ws\repo\src\file.cs").Basis);
    }

    [Fact]
    public void DeniedDecision_IsRecorded()
    {
        var decision = ToolResourceAllowList.Evaluate([@"C:\ws\repo"], @"D:\elsewhere\file.txt");
        Assert.False(decision.Allowed);
        Assert.Equal(ResourceAllowBasis.Denied, decision.Basis);
    }

    [Fact]
    public void LegacyBooleanProjection_MatchesDecision()
    {
        Assert.True(ToolResourceAllowList.IsAllowed([], @"C:\x"));
        Assert.True(ToolResourceAllowList.IsAllowed(["workspace"], @"C:\x"));
        Assert.False(ToolResourceAllowList.IsAllowed([@"C:\ws"], @"D:\x"));
    }
}
