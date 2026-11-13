using System.Text.Json;
using TinadecTools.Runtime;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

/// <summary>
/// Hook suppression contract for agent-initiated git. The list is fail-closed: a
/// subcommand that is not known to be hook-free gets <c>-c core.hooksPath=</c>, so a
/// missing entry costs two argv tokens on a read-only command while a wrong entry
/// would let a planted hook execute outside the sandbox with the host's environment.
/// </summary>
public sealed class GitHookPolicyTests : IDisposable
{
    private readonly string _repo = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "tinadec-hookpolicy-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static JsonElement Context(string settings) => JsonDocument.Parse(
        "{\"schema_version\":1,\"settings_hash\":\"test\",\"settings\":" + settings
        + ",\"allowed_tool_ids\":[\"git_commit\"],\"read_roots\":[],\"mcp_servers\":[]}").RootElement;

    private static string SuppressedPath(string[] prefix)
    {
        Assert.Equal(2, prefix.Length);
        Assert.Equal("-c", prefix[0]);
        Assert.StartsWith("core.hooksPath=", prefix[1], StringComparison.Ordinal);
        return prefix[1]["core.hooksPath=".Length..];
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("push")]
    [InlineData("pull")]
    [InlineData("fetch")]
    [InlineData("checkout")]
    [InlineData("am")]
    [InlineData("cherry-pick")]
    [InlineData("stash")]
    [InlineData("worktree")]
    [InlineData("apply")]
    [InlineData("reset")]
    [InlineData("clean")]
    [InlineData("branch")]
    [InlineData("tag")]
    public void SubcommandThatMayReachAHook_IsSuppressed(string subcommand)
    {
        var prefix = GitHookPolicy.Prefix([subcommand, "-m", "x"], _repo);
        var directory = SuppressedPath(prefix);
        Assert.True(Directory.Exists(directory), "hooksPath must name a real directory.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData("rev-parse")]
    [InlineData("status")]
    [InlineData("log")]
    [InlineData("diff")]
    [InlineData("show")]
    [InlineData("cat-file")]
    [InlineData("ls-files")]
    [InlineData("ls-tree")]
    [InlineData("for-each-ref")]
    [InlineData("config")]
    [InlineData("remote")]
    [InlineData("blame")]
    [InlineData("grep")]
    [InlineData("fsck")]
    [InlineData("version")]
    [InlineData("symbolic-ref")]
    public void SubcommandThatNeverRunsAHook_IsLeftAlone(string subcommand) =>
        Assert.Empty(GitHookPolicy.Prefix([subcommand, "HEAD"], _repo));

    [Fact]
    public void GlobalOptionsAreSkippedAlongWithTheirValues()
    {
        Assert.Empty(GitHookPolicy.Prefix(["-C", _repo, "status"], _repo));
        Assert.Empty(GitHookPolicy.Prefix(["--git-dir=" + Path.Combine(_repo, ".git"), "log"], _repo));
        Assert.Empty(GitHookPolicy.Prefix(["--no-pager", "diff"], _repo));
        SuppressedPath(GitHookPolicy.Prefix(["-c", "core.editor=true", "merge", "topic"], _repo));
        SuppressedPath(GitHookPolicy.Prefix(["-C", _repo, "commit", "-m", "x"], _repo));
        SuppressedPath(GitHookPolicy.Prefix(["--work-tree", _repo, "checkout", "main"], _repo));
    }

    [Fact]
    public void NoSubcommand_IsSuppressedRatherThanAssumedSafe() =>
        SuppressedPath(GitHookPolicy.Prefix(["--version-ish"], _repo));

    [Fact]
    public void WithoutAnAmbientContext_HooksAreStillSuppressed() =>
        SuppressedPath(GitHookPolicy.Prefix(["commit", "-m", "x"], _repo));

    [Fact]
    public void EmptySettings_DefaultToSuppression()
    {
        using var scope = ToolExecutionContext.Enter(Context("{}"), "git_commit");
        SuppressedPath(GitHookPolicy.Prefix(["commit", "-m", "x"], _repo));
    }

    [Fact]
    public void AllowHooksFalse_IsSuppression()
    {
        using var scope = ToolExecutionContext.Enter(Context("{\"git\":{\"allow_hooks\":false}}"), "git_commit");
        SuppressedPath(GitHookPolicy.Prefix(["commit", "-m", "x"], _repo));
    }

    [Fact]
    public void AllowHooksTrue_RestoresHooksForEverySubcommand()
    {
        using var scope = ToolExecutionContext.Enter(Context("{\"git\":{\"allow_hooks\":true}}"), "git_commit");
        Assert.True(GitHookPolicy.HooksEnabled);
        Assert.Empty(GitHookPolicy.Prefix(["commit", "-m", "x"], _repo));
        Assert.Empty(GitHookPolicy.Prefix(["push", "origin", "topic"], _repo));
        Assert.Empty(GitHookPolicy.Prefix(["rebase", "main"], _repo));
    }

    [Fact]
    public void SuppressionIsScopedToTheAmbientCallAndDoesNotLeak()
    {
        using (ToolExecutionContext.Enter(Context("{\"git\":{\"allow_hooks\":true}}"), "git_commit"))
            Assert.Empty(GitHookPolicy.Prefix(["commit", "-m", "x"], _repo));
        SuppressedPath(GitHookPolicy.Prefix(["commit", "-m", "x"], _repo));
    }
}