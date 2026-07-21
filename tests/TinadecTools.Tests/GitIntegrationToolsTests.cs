using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitIntegrationToolsTests
{
    [Fact]
    public async Task Merge_Start_WithNoConflict_FastForwardsOrCreatesMerge()
    {
        using var repo = new TempGitRepo("git-int");
        repo.SeedInitialCommit("a.txt", "v1\n");
        repo.RunGit("branch", "side");
        repo.CommitFile("a.txt", "v2\n", "main evolves");

        var result = await GitIntegrationTools.MergeAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Branch = "side",
            ConfirmMerge = "ok"
        }, CancellationToken.None);
        Assert.True(result.Success, $"Error: {result.Error} Output: {result.Output}");
        Assert.False(result.Conflict);
    }

    [Fact]
    public async Task Merge_Start_WithConflict_ReportsConflictedFiles()
    {
        using var repo = new TempGitRepo("git-int");
        repo.SeedInitialCommit("a.txt", "line1\nline2\nline3\n");
        repo.RunGit("branch", "side");
        repo.CommitFile("a.txt", "MAIN\nline2\nline3\n", "main edit");
        repo.RunGit("checkout", "side");
        repo.CommitFile("a.txt", "SIDE\nline2\nline3\n", "side edit");
        repo.RunGit("checkout", "main");

        var result = await GitIntegrationTools.MergeAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Branch = "side",
            ConfirmMerge = "ok"
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(result.Conflict);
        Assert.Contains("a.txt", result.ConflictedFiles);
    }

    [Fact]
    public async Task Merge_Abort_ReturnsToCleanMain()
    {
        using var repo = new TempGitRepo("git-int");
        repo.SeedInitialCommit("a.txt", "line1\nline2\nline3\n");
        repo.RunGit("branch", "side");
        repo.CommitFile("a.txt", "MAIN\nline2\nline3\n", "main edit");
        repo.RunGit("checkout", "side");
        repo.CommitFile("a.txt", "SIDE\nline2\nline3\n", "side edit");
        repo.RunGit("checkout", "main");
        await GitIntegrationTools.MergeAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Branch = "side",
            ConfirmMerge = "ok"
        }, CancellationToken.None);

        var abort = await GitIntegrationTools.MergeAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Operation = "abort",
            ConfirmMerge = "ok"
        }, CancellationToken.None);
        Assert.True(abort.Success, abort.Error);
        Assert.False(abort.Conflict);
    }

    [Fact]
    public async Task Merge_RejectsInvalidOperation()
    {
        using var repo = new TempGitRepo("git-int");
        repo.SeedInitialCommit();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitIntegrationTools.MergeAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Operation = "stash",
            ConfirmMerge = "ok"
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rebase_Start_WithNoConflict_Succeeds()
    {
        using var repo = new TempGitRepo("git-int");
        repo.SeedInitialCommit("a.txt", "v1\n");
        repo.RunGit("branch", "side");
        repo.CommitFile("a.txt", "v2\n", "main evolves");
        repo.RunGit("checkout", "side");
        repo.CommitFile("b.txt", "side\n", "side adds file");

        var result = await GitIntegrationTools.RebaseAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Branch = "main",
            ConfirmRebase = "ok"
        }, CancellationToken.None);
        Assert.True(result.Success, $"Error: {result.Error} Output: {result.Output}");
        Assert.False(result.Conflict);
    }

    [Fact]
    public async Task Rebase_Start_WithConflict_DoesNotSilentlySucceed()
    {
        using var repo = new TempGitRepo("git-int");
        repo.SeedInitialCommit("a.txt", "line1\n");
        repo.RunGit("branch", "side");
        repo.CommitFile("a.txt", "MAIN\n", "main edit");
        repo.RunGit("checkout", "side");
        repo.CommitFile("a.txt", "SIDE\n", "side edit");

        var result = await GitIntegrationTools.RebaseAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Branch = "main",
            ConfirmRebase = "ok"
        }, CancellationToken.None);
        // rebases may technically succeed if --no-ff implicit; but conflicted status path matches that of failure to be safe.
        Assert.True(result.Conflict || !result.Success, $"Both success branches acceptable; Error: {result.Error}");
        Assert.Contains("a.txt", result.ConflictedFiles);
    }

    [Fact]
    public async Task Rebase_Abort_ClearsConflictedState()
    {
        using var repo = new TempGitRepo("git-int");
        repo.SeedInitialCommit("a.txt", "line1\n");
        repo.RunGit("branch", "side");
        repo.CommitFile("a.txt", "MAIN\n", "main edit");
        repo.RunGit("checkout", "side");
        repo.CommitFile("a.txt", "SIDE\n", "side edit");
        await GitIntegrationTools.RebaseAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Branch = "main",
            ConfirmRebase = "ok"
        }, CancellationToken.None);

        var abort = await GitIntegrationTools.RebaseAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Operation = "abort",
            ConfirmRebase = "ok"
        }, CancellationToken.None);
        Assert.True(abort.Success, abort.Error);
        Assert.False(abort.Conflict);
    }

    [Fact]
    public async Task Mutations_RequireConfirmFields()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitIntegrationTools.MergeAsync(new GitIntegrationArgs { Branch = "x" }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitIntegrationTools.RebaseAsync(new GitIntegrationArgs { Branch = "x" }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForIntegrationMutations()
    {
        GeneratedToolRegistry.RegisterAll();
        foreach (var toolId in new[] { "git_merge", "git_rebase" })
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