using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitConflictResolveToolTests
{
    private static async Task<(TempGitRepo repo, string conflictPath)> SetupConflictAsync(string prefix)
    {
        var repo = new TempGitRepo(prefix);
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
        return (repo, "a.txt");
    }

    [Fact]
    public async Task ResolveAsync_Auto_FailsWhenConflictsRemain()
    {
        var (repo, path) = await SetupConflictAsync("git-resolve");
        using (repo)
        {
            var result = await GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
            {
                RepositoryPath = repo,
                Path = path,
                Strategy = "auto",
                ConfirmResolve = "ok"
            }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.True(result.ConflictCount > 0);
        }
    }

    [Fact]
    public async Task ResolveAsync_Auto_SucceedsForDisjointMergeWithoutConflict()
    {
        // ponytail: 当两边编辑互不重叠时 git 自动合并无冲突，再调 resolve 必须如实报告"不是冲突"
        using var repo = new TempGitRepo("git-resolve");
        repo.SeedInitialCommit("a.txt", "line1\nline2\nline3\n");
        repo.RunGit("branch", "side");
        repo.CommitFile("a.txt", "line0\nline2\nline3\n", "main edit");
        repo.RunGit("checkout", "side");
        repo.CommitFile("a.txt", "line1\nline2\nLINE3\n", "side edit");
        repo.RunGit("checkout", "main");
        await GitIntegrationTools.MergeAsync(new GitIntegrationArgs
        {
            RepositoryPath = repo,
            Branch = "side",
            ConfirmMerge = "ok"
        }, CancellationToken.None);

        var result = await GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
        {
            RepositoryPath = repo,
            Path = "a.txt",
            Strategy = "auto",
            ConfirmResolve = "ok"
        }, CancellationToken.None);
        // git auto-merged without leaving stages, so resolve must report not-a-conflict rather than fabricate a merge.
        Assert.False(result.Success);
        Assert.Contains("not an unresolved", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_Ours_TakesMainLineChange()
    {
        var (repo, path) = await SetupConflictAsync("git-resolve");
        using (repo)
        {
            var result = await GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
            {
                RepositoryPath = repo,
                Path = path,
                Strategy = "ours",
                ConfirmResolve = "ok"
            }, CancellationToken.None);
            Assert.True(result.Success, result.Error);
            var content = await File.ReadAllTextAsync(System.IO.Path.Combine(repo.Path, path));
            Assert.Contains("MAIN", content);
            Assert.DoesNotContain("<<<<<<< ours", content);
            // ours/theirs-of-stage2 take raw stage-2 content; side edit text remains absent after ours.
            Assert.DoesNotContain("SIDE\nline2", content);
        }
    }

    [Fact]
    public async Task ResolveAsync_Theirs_TakesSideChange()
    {
        var (repo, path) = await SetupConflictAsync("git-resolve");
        using (repo)
        {
            var result = await GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
            {
                RepositoryPath = repo,
                Path = path,
                Strategy = "theirs",
                ConfirmResolve = "ok"
            }, CancellationToken.None);
            Assert.True(result.Success, result.Error);
            var content = await File.ReadAllTextAsync(System.IO.Path.Combine(repo.Path, path));
            Assert.Contains("SIDE", content);
        }
    }

    [Fact]
    public async Task ResolveAsync_Both_And_AutoReStagesFile()
    {
        var (repo, path) = await SetupConflictAsync("git-resolve");
        using (repo)
        {
            var result = await GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
            {
                RepositoryPath = repo,
                Path = path,
                Strategy = "both",
                ConfirmResolve = "ok"
            }, CancellationToken.None);
            Assert.True(result.Success, result.Error);
            Assert.Empty(result.RemainingConflicts);
            // git status: file no longer listed as conflicted
            Assert.True(result.Status is not null && !result.Status.Files.Any(f => f.IsConflicted));
        }
    }

    [Fact]
    public async Task ResolveAsync_RejectsInvalidStrategy()
    {
        using var repo = new TempGitRepo("git-resolve");
        repo.SeedInitialCommit();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
        {
            RepositoryPath = repo,
            Path = "x.txt",
            Strategy = "random",
            ConfirmResolve = "ok"
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_RequiresConfirmField()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
        {
            Path = "x.txt"
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForConflictResolve()
    {
        GeneratedToolRegistry.RegisterAll();
        Assert.True(ToolRegistry.TryResolve("git_conflict_resolve", out var handler));
        var response = await handler(new ToolCallRequest<System.Text.Json.JsonElement>
        {
            ToolCallId = 1,
            ToolId = "git_conflict_resolve",
            SessionId = "test",
            Approved = false
        }, CancellationToken.None);
        Assert.False(response.IsSuccess);
    }
}