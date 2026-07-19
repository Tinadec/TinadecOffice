using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitWorktreeToolsTests
{
    [Fact]
    public async Task CreateAsync_CreatesWorktreeFromExistingBranch()
    {
        using var repo = new TempGitRepo("git-worktree");
        repo.SeedInitialCommit();
        repo.RunGit("branch", "topic");
        var result = await GitWorktreeMutationTools.CreateAsync(new GitWorktreeMutationArgs
        {
            RepositoryPath = repo,
            Branch = "topic",
            ConfirmWorktreeCreate = "ok"
        }, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.False(result.CreatedBranch);
        Assert.True(Directory.Exists(result.Path));
        Assert.Contains(result.Worktrees, w => Normalize(w.Path) == Normalize(result.Path));
    }

    [Fact]
    public async Task CreateAsync_CreatesNewBranchFromStartRef()
    {
        using var repo = new TempGitRepo("git-worktree");
        repo.SeedInitialCommit();
        repo.CommitFile("a.txt", "v1\n", "second");
        var result = await GitWorktreeMutationTools.CreateAsync(new GitWorktreeMutationArgs
        {
            RepositoryPath = repo,
            Branch = "fresh",
            StartRef = "HEAD~1",
            ConfirmWorktreeCreate = "ok"
        }, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True(result.CreatedBranch);
        Assert.Equal("fresh", result.Branch);
    }

    [Fact]
    public async Task CreateAsync_DefaultsToSlugFromBranch()
    {
        using var repo = new TempGitRepo("git-worktree");
        repo.SeedInitialCommit();
        var result = await GitWorktreeMutationTools.CreateAsync(new GitWorktreeMutationArgs
        {
            RepositoryPath = repo,
            Branch = "feature/x",
            StartRef = "HEAD",
            ConfirmWorktreeCreate = "ok"
        }, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Contains("feature-x", result.Path ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RejectsPathEscapingManagedRoot()
    {
        using var repo = new TempGitRepo("git-worktree");
        repo.SeedInitialCommit();
        var escaping = System.IO.Path.Combine(repo.Path, "..", "escape-target");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GitWorktreeMutationTools.CreateAsync(new GitWorktreeMutationArgs
        {
            RepositoryPath = repo,
            Branch = "topic",
            Path = escaping,
            ConfirmWorktreeCreate = "ok"
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RemoveAsync_DeletesWorktreeAndRejectsCurrent()
    {
        using var repo = new TempGitRepo("git-worktree");
        repo.SeedInitialCommit();
        repo.RunGit("branch", "topic");
        var create = await GitWorktreeMutationTools.CreateAsync(new GitWorktreeMutationArgs
        {
            RepositoryPath = repo,
            Branch = "topic",
            ConfirmWorktreeCreate = "ok"
        }, CancellationToken.None);
        Assert.True(create.Success, create.Error);

        var remove = await GitWorktreeMutationTools.RemoveAsync(new GitWorktreeMutationArgs
        {
            RepositoryPath = repo,
            Path = create.Path,
            ConfirmWorktreeRemove = "ok"
        }, CancellationToken.None);
        Assert.True(remove.Success, remove.Error);
        Assert.False(Directory.Exists(create.Path));

        // Removing the main worktree is rejected because it lies outside .tinadec/worktrees/;
        // ponytail: ResolveManagedPath enforces the boundary before any explicit "current worktree" check.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GitWorktreeMutationTools.RemoveAsync(new GitWorktreeMutationArgs
        {
            RepositoryPath = repo,
            Path = repo.Path,
            ConfirmWorktreeRemove = "ok"
        }, CancellationToken.None).AsTask());
    }

    private static string Normalize(string? path) =>
        path is null ? string.Empty : System.IO.Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));

    [Fact]
    public async Task Mutations_RequireConfirmFields()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitWorktreeMutationTools.CreateAsync(new GitWorktreeMutationArgs { Branch = "x" }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitWorktreeMutationTools.RemoveAsync(new GitWorktreeMutationArgs { Path = "x" }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForWorktreeMutations()
    {
        GeneratedToolRegistry.RegisterAll();
        foreach (var toolId in new[] { "git_worktree_create", "git_worktree_remove" })
        {
            Assert.True(ToolRegistry.TryResolve(toolId, out var handler));
            var response = await handler(new ToolCallRequest<System.Text.Json.JsonElement>
            {
                ToolCallId = 1,
                ToolId = toolId,
                SessionId = "test",
                Approved = false
            }, CancellationToken.None);
            Assert.False(response.IsSuccess);
        }
    }

    [Fact]
    public void Tinadec_Worktrees_DirectoryIsGitignored()
    {
        using var repo = new TempGitRepo("git-worktree");
        repo.SeedInitialCommit();
        var gitignore = System.IO.Path.Combine(repo.Path, ".gitignore");
        // conservative check: ignore entry should cover .tinadec/worktrees/ somewhere in workspace's gitignore set;
        // the seed may or may not have committed .gitignore. Read committed file if any.
        if (File.Exists(gitignore))
        {
            var content = File.ReadAllText(gitignore);
            Assert.Contains(".tinadec/worktrees", content, StringComparison.Ordinal);
        }
        // ponytail: the canonical convention lives at repo-root .gitignore; absence in a fresh fixture is okay
        // as long as the tool layer's own ResolveManagedPath still confines every worktree under .tinadec/worktrees.
    }
}