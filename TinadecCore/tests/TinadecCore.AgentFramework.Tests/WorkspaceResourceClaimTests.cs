using TinadecCore.Tools;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// WS-8 claim coverage and denial explanations: every provider tool with a single
/// workspace target contributes a resource path claim (so prefix grants are really
/// enforced), tools without one fall back to the level-only decision, and a denial
/// says what was missing instead of a bare "explicitly denies".
/// </summary>
public sealed class WorkspaceResourceClaimTests
{
    private const string Root = @"C:\work\app";

    private static string Params(string json) => json;

    [Theory]
    [InlineData("read_file", "filepath")]
    [InlineData("write_file", "filepath")]
    [InlineData("replace_lines", "filepath")]
    [InlineData("replace_bytes", "filepath")]
    [InlineData("insert_line", "filepath")]
    [InlineData("insert_bytes", "filepath")]
    [InlineData("insert_byte", "filepath")]
    [InlineData("delete_line", "filepath")]
    [InlineData("delete_bytes", "filepath")]
    [InlineData("ls", "path")]
    [InlineData("stat", "path")]
    [InlineData("file_search", "path")]
    [InlineData("shell", "cwd")]
    [InlineData("command_run", "working_directory")]
    [InlineData("git_status", "repository_path")]
    [InlineData("git_commit", "repository_path")]
    [InlineData("git_worktree_create", "repository_path")]
    public void RegisteredTools_ContributeTheirTargetPath(string toolId, string parameter)
    {
        var claim = ToolResourcePathRegistry.TryBuildResourceClaim(
            toolId,
            Params($"{{\"{parameter}\":\"src/app.ts\"}}"),
            Root,
            mutating: false);

        Assert.NotNull(claim);
        Assert.Equal("resource.access", claim!.Capability);
        Assert.Equal("path://src/app.ts", claim.Resource);
    }

    [Fact]
    public void AbsoluteTarget_IsNormalizedToAWorkspaceRelativeClaim()
    {
        var claim = ToolResourcePathRegistry.TryBuildResourceClaim(
            "read_file",
            Params("""{"filepath":"C:\\work\\app\\src\\app.ts"}"""),
            Root,
            mutating: false);
        Assert.Equal("path://src/app.ts", claim!.Resource);

        // A target outside the workspace yields no path claim, and the tool process
        // refuses it independently — the fallback never widens access.
        Assert.Null(ToolResourcePathRegistry.TryBuildResourceClaim(
            "read_file",
            Params("""{"filepath":"C:\\elsewhere\\app.ts"}"""),
            Root,
            mutating: false));
        Assert.Null(ToolResourcePathRegistry.TryBuildResourceClaim(
            "read_file",
            Params("""{"filepath":"../../escape.ts"}"""),
            Root,
            mutating: false));
    }

    [Fact]
    public void TargetlessTools_KeepTheLevelOnlyDecision()
    {
        Assert.False(ToolResourcePathRegistry.IsRegistered("mcp_search"));
        Assert.False(ToolResourcePathRegistry.IsRegistered("mcp_invoke"));
        Assert.Null(ToolResourcePathRegistry.TryBuildResourceClaim("mcp_invoke", Params("""{"server_id":"x"}"""), Root, mutating: false));
        // The claim still names the tool and the level it was requested at.
        var levelOnly = ToolResourcePathRegistry.TryBuildResourceClaim("mcp_search", Params("""{"query":"x"}"""), Root, mutating: false);
        Assert.Null(levelOnly);
    }

    [Fact]
    public void DenialExplanations_NameTheMissingLevelTheGrantsAndTheRoot()
    {
        var readOnly = new[] { "read:" };
        var target = "src/app.ts";

        var noGrant = ResourceDenialExplanation.Describe(
            ToolResourceAllowList.Evaluate([], target, mutating: false), [], "read_file", Root, target);
        Assert.Contains("holds no workspace resource grant", noGrant, StringComparison.Ordinal);

        var outsidePrefix = ResourceDenialExplanation.Describe(
            ToolResourceAllowList.Evaluate(["read:docs"], target, mutating: false), ["read:docs"], "read_file", Root, target);
        Assert.Contains(target, outsidePrefix, StringComparison.Ordinal);
        Assert.Contains("read:docs", outsidePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// A write against a read-only envelope is not a denial any more: it is an
    /// upgrade, so the text the operator sees must be the upgrade explanation (what
    /// the envelope is missing, and that approving issues the write) rather than the
    /// denial text — that text belongs only to the variants that really refuse.
    /// </summary>
    [Fact]
    public void MissingWriteLevel_ExplainsTheUpgrade_NotADenial()
    {
        var readOnly = new[] { "read:" };
        var target = "src/app.ts";
        var decision = ToolResourceAllowList.Evaluate(readOnly, target, mutating: true);
        Assert.True(decision.RequiresApproval);

        var upgrade = ResourceDenialExplanation.DescribeUpgrade(readOnly, "write_file", Root, target);
        Assert.Contains("needs approval", upgrade, StringComparison.Ordinal);
        Assert.Contains("write-level grant", upgrade, StringComparison.Ordinal);
        Assert.Contains("read:", upgrade, StringComparison.Ordinal);
        Assert.Contains(Root, upgrade, StringComparison.Ordinal);
        Assert.Contains(target, upgrade, StringComparison.Ordinal);
    }

    [Fact]
    public void GrantLevels_DecideReadAndWriteClaims()
    {
        Assert.True(ToolResourceAllowList.Evaluate(["read:"], "src/app.ts", mutating: false).Allowed);
        // A read-only envelope never authorizes a mutation outright; it upgrades it.
        var upgrade = ToolResourceAllowList.Evaluate(["read:"], "src/app.ts", mutating: true);
        Assert.False(upgrade.Allowed);
        Assert.True(upgrade.RequiresApproval);
        Assert.True(ToolResourceAllowList.Evaluate(["write:"], "src/app.ts", mutating: true).Allowed);
        Assert.True(ToolResourceAllowList.Evaluate(["write:src"], "src/app.ts", mutating: true).Allowed);
        // A write grant that does not cover the target is an escape, not an upgrade.
        var outside = ToolResourceAllowList.Evaluate(["write:docs"], "src/app.ts", mutating: true);
        Assert.False(outside.Allowed);
        Assert.False(outside.RequiresApproval);
        // Level-only tools (no path claim) pass on any grant of the right level.
        Assert.True(ToolResourceAllowList.Evaluate(["read:"], null, mutating: false).Allowed);
        var pathlessUpgrade = ToolResourceAllowList.Evaluate(["read:"], null, mutating: true);
        Assert.False(pathlessUpgrade.Allowed);
        Assert.True(pathlessUpgrade.RequiresApproval);
    }
}
