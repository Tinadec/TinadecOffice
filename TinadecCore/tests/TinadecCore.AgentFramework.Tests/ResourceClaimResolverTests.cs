using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Which resource a tool call claims. These cases decide whether two agents are allowed to touch the
/// same thing: a claim that is too narrow lets a second writer destroy the first, and one that is too
/// broad blocks work that never collided.
/// </summary>
public sealed class ResourceClaimResolverTests
{
    private static IReadOnlyDictionary<string, string?> Args(params (string Key, string? Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AWriteClaimsTheFileItNames()
    {
        var claim = ResourceClaimResolver.Resolve("write_file", Args(("filepath", "C:\\repo\\a.ts")), "C:\\repo", mutatesWorkspace: true);

        Assert.NotNull(claim);
        Assert.Equal(ResourceLeaseKinds.Path, claim!.Kind);
        Assert.Equal("C:/repo/a.ts", claim.ResourceKey);
        Assert.True(claim.Exclusive);
    }

    [Fact]
    public void AReadClaimsNothingBecauseReadsCannotDestroyEachOther()
    {
        // Two agents reading one file is not a collision, and a shared claim would still have to be
        // released and reconciled for no benefit.
        Assert.Null(ResourceClaimResolver.Resolve("read_file", Args(("filepath", "C:\\repo\\a.ts")), "C:\\repo", mutatesWorkspace: false));
    }

    [Fact]
    public void ACommandClaimsNothingBecauseItsTargetsAreUnknown()
    {
        // The only honest claim for a command is the whole workspace, and holding that until the task
        // ends serializes every parallel run in it. None of the surveyed harnesses lock commands;
        // parallel writers are separated by declared write scopes and worktrees, and what a command
        // changed is checked after it runs.
        Assert.Null(ResourceClaimResolver.Resolve("shell", Args(("command", "npm test")), "C:\\repo", mutatesWorkspace: true));
        Assert.Null(ResourceClaimResolver.Resolve("command_run", Args(("executable", "dotnet")), "C:\\repo", mutatesWorkspace: true));
    }

    [Fact]
    public void AGitMutationClaimsNothingBecauseGitGuardsItsOwnIndex()
    {
        Assert.Null(ResourceClaimResolver.Resolve(
            "git_commit",
            Args(("repository_path", "C:\\repo\\sub"), ("message", "x")),
            workspaceRoot: "C:\\repo",
            mutatesWorkspace: true));
    }

    [Fact]
    public void AWriteScopeResolvesEachEntryAgainstTheWorkspaceAndDropsDuplicates()
    {
        var claims = ResourceClaimResolver.ResolveWriteScope(["src", "C:\\repo\\src\\", " ", "docs/a.md"], "C:\\repo");

        Assert.Equal(["C:/repo/src", "C:/repo/docs/a.md"], claims.Select(claim => claim.ResourceKey));
        Assert.All(claims, claim => Assert.True(claim.Exclusive));
        Assert.All(claims, claim => Assert.Equal(ResourceLeaseKinds.Path, claim.Kind));
    }

    [Fact]
    public void AWorktreeToolClaimsTheWorktreeNotAPathInsideIt()
    {
        var claim = ResourceClaimResolver.Resolve(
            "git_worktree_create",
            Args(("path", "C:\\repo\\wt-1"), ("branch", "feat")),
            workspaceRoot: "C:\\repo",
            mutatesWorkspace: true);

        Assert.Equal(ResourceLeaseKinds.Worktree, claim!.Kind);
        Assert.Equal("C:/repo/wt-1", claim.ResourceKey);
    }

    /// <summary>
    /// Creating without a path claims exactly the worktree the tool will make (its default location),
    /// not the repository — a repository claim would contain every other worktree in it and turn two
    /// parallel worktree creations into a queue.
    /// </summary>
    [Fact]
    public void ACreateWithoutAPathClaimsTheDefaultWorktree_NotTheRepository()
    {
        var claim = ResourceClaimResolver.Resolve(
            "git_worktree_create",
            Args(("repository_path", "C:\\repo"), ("branch", "feat/login page")),
            workspaceRoot: "C:\\repo",
            mutatesWorkspace: true);

        Assert.Equal(ResourceLeaseKinds.Worktree, claim!.Kind);
        Assert.Equal("C:/repo/.tinadec/worktrees/feat-login-page", claim.ResourceKey);
        Assert.Null(ResourceClaimResolver.Resolve("git_worktree_create", Args(("repository_path", "C:\\repo")), "C:\\repo", true));
    }

    [Fact]
    public void RemovingClaimsTheWorktree_AndListingClaimsNothing()
    {
        var remove = ResourceClaimResolver.Resolve("git_worktree_remove", Args(("path", ".tinadec/worktrees/feat"), ("repository_path", "C:\\repo")), "C:\\repo", true);
        Assert.Equal("C:/repo/.tinadec/worktrees/feat", remove!.ResourceKey);
        Assert.Null(ResourceClaimResolver.Resolve("git_worktree_list", Args(("repository_path", "C:\\repo")), "C:\\repo", false));
        Assert.Null(ResourceClaimResolver.Resolve("git_worktree_remove", Args(("repository_path", "C:\\repo")), "C:\\repo", true));
    }

    [Fact]
    public void TwoSpellingsOfOnePathNormalizeToTheSameKey()
    {
        var backslash = ResourceClaimResolver.Resolve("write_file", Args(("filepath", "C:\\repo\\a.ts")), null, true)!;
        var forward = ResourceClaimResolver.Resolve("write_file", Args(("filepath", "C:/repo/a.ts/")), null, true)!;

        Assert.Equal(backslash.ResourceKey, forward.ResourceKey);
        Assert.True(ResourceLeasePolicy.Conflicts(backslash, forward));
    }

    [Fact]
    public void AnOlderArgumentNameIsStillUnderstood()
    {
        // Tools in this tree spell the target `filepath`, `file_path` and `path` depending on age;
        // missing one would silently drop its lease.
        foreach (var key in new[] { "filepath", "file_path", "path" })
        {
            var claim = ResourceClaimResolver.Resolve("write_file", Args((key, "C:\\repo\\a.ts")), null, true);
            Assert.NotNull(claim);
            Assert.Equal("C:/repo/a.ts", claim!.ResourceKey);
        }
    }

    [Fact]
    public void AMutationNamingNoPathAndNoWorkspaceMakesNoClaim()
    {
        // Nothing to lease is the honest answer; inventing a key would serialize unrelated runs.
        Assert.Null(ResourceClaimResolver.Resolve("write_file", Args(), workspaceRoot: null, mutatesWorkspace: true));
    }
}
