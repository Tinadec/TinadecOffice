using TinadecTools.Abstractions;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitBranchToolsTests
{
    [Fact]
    public async Task CreateAsync_CreatesAndSwitchesToNewBranch()
    {
        using var repo = new TempGitRepo("git-branch");
        repo.SeedInitialCommit();

        var result = await GitBranchTools.CreateAsync(new GitBranchMutationArgs
        {
            RepositoryPath = repo,
            Branch = "feature-a",
            ConfirmBranchCreate = "ok"
        }, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal("feature-a", result.Branch);
        Assert.Equal("feature-a", repo.CaptureGit("branch", "--show-current").Trim());
    }

    [Fact]
    public async Task CheckoutAsync_SwitchesToExistingBranch()
    {
        using var repo = new TempGitRepo("git-branch");
        repo.SeedInitialCommit();
        repo.RunGit("branch", "feature-a");

        var result = await GitBranchTools.CheckoutAsync(new GitBranchMutationArgs
        {
            RepositoryPath = repo,
            Branch = "feature-a",
            ConfirmCheckout = "ok"
        }, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal("feature-a", repo.CaptureGit("branch", "--show-current").Trim());
    }

    [Fact]
    public async Task DeleteAsync_RemovesBranchAndRejectsCurrentBranch()
    {
        using var repo = new TempGitRepo("git-branch");
        repo.SeedInitialCommit();
        repo.RunGit("branch", "topic");

        var deleted = await GitBranchTools.DeleteAsync(new GitBranchMutationArgs
        {
            RepositoryPath = repo,
            Branch = "topic",
            ConfirmBranchDelete = "ok"
        }, CancellationToken.None);
        Assert.True(deleted.Success, deleted.Error);
        Assert.DoesNotContain("topic", repo.CaptureGit("branch"));

        var current = await GitBranchTools.DeleteAsync(new GitBranchMutationArgs
        {
            RepositoryPath = repo,
            Branch = "main",
            ConfirmBranchDelete = "ok"
        }, CancellationToken.None);
        Assert.False(current.Success);
        Assert.Contains("current branch", current.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenameAsync_ChangesBranchName()
    {
        using var repo = new TempGitRepo("git-branch");
        repo.SeedInitialCommit();
        repo.RunGit("branch", "old-name");

        var result = await GitBranchTools.RenameAsync(new GitBranchMutationArgs
        {
            RepositoryPath = repo,
            NewName = "new-name",
            ConfirmBranchRename = "ok"
        }, CancellationToken.None);
        // Rename operates on the current branch; main -> new-name
        Assert.True(result.Success, result.Error);
        Assert.Contains("new-name", repo.CaptureGit("branch"));
    }

    [Fact]
    public async Task InvalidBranchName_RejectedByCheckRefFormat()
    {
        using var repo = new TempGitRepo("git-branch");
        repo.SeedInitialCommit();

        var result = await GitBranchTools.CreateAsync(new GitBranchMutationArgs
        {
            RepositoryPath = repo,
            Branch = "bad name",
            ConfirmBranchCreate = "ok"
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Invalid branch name", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutations_RequireConfirmFieldBeforeReporting()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitBranchTools.CreateAsync(new GitBranchMutationArgs
        {
            Branch = "x"
        }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitBranchTools.DeleteAsync(new GitBranchMutationArgs
        {
            Branch = "x"
        }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitBranchTools.RenameAsync(new GitBranchMutationArgs
        {
            NewName = "x"
        }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitBranchTools.CheckoutAsync(new GitBranchMutationArgs
        {
            Branch = "x"
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForBranchMutations()
    {
        GeneratedToolRegistry.RegisterAll();
        foreach (var toolId in new[] { "git_checkout", "git_branch_create", "git_branch_delete", "git_branch_rename" })
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
}