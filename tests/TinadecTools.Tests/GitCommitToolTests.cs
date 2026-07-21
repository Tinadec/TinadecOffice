using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitCommitToolTests
{
    [Fact]
    public async Task CommitStagedOnly_CommitsTheIndexAndLeavesWorkingTreeChanges()
    {
        using var repo = new TempGitRepo("git-commit");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "initial\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "other.txt"), "other\n");
        repo.RunGit("add", "--", "note.txt", "other.txt");
        repo.RunGit("commit", "-m", "initial");

        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "staged\n");
        repo.RunGit("add", "--", "note.txt");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "unstaged\n");

        var result = await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "commit staged change",
            CommitStagedOnly = true,
            ConfirmCommit = "ok"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("staged_only", result.Mode);
        Assert.Equal(["note.txt"], result.StagedFiles);
        Assert.Equal(repo.CaptureGit("rev-parse", "HEAD").Trim(), result.CommitHash);
        Assert.Equal("staged\n", repo.CaptureGit("show", "HEAD:note.txt"));
        Assert.Equal("unstaged\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "note.txt")));
        Assert.NotNull(result.Status);
        Assert.True(result.Status.HasUncommittedChanges);
        Assert.Contains(result.Status.Files, file => file.Path == "note.txt" && file.UnstagedStatus == "modified");
    }

    [Fact]
    public async Task IncludeAll_CommitsTrackedAndUntrackedChanges()
    {
        using var repo = new TempGitRepo("git-commit");
        repo.SeedInitialCommit("note.txt", "initial\n");

        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "changed\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "new.txt"), "new\n");

        var result = await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "commit all changes",
            IncludeAll = true,
            ConfirmCommit = "ok"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("include_all", result.Mode);
        Assert.Equal(["new.txt", "note.txt"], result.StagedFiles.Order().ToList());
        Assert.Equal("changed\n", repo.CaptureGit("show", "HEAD:note.txt"));
        Assert.Equal("new\n", repo.CaptureGit("show", "HEAD:new.txt"));
        Assert.NotNull(result.Status);
        Assert.False(result.Status.HasUncommittedChanges);
    }

    [Fact]
    public async Task ExplicitPaths_CommitOnlyTheRequestedWorkingTreeChanges()
    {
        using var repo = new TempGitRepo("git-commit");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "initial\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "other.txt"), "other\n");
        repo.RunGit("add", "--", "note.txt", "other.txt");
        repo.RunGit("commit", "-m", "initial");

        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "selected\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "other.txt"), "not selected\n");

        var result = await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "commit selected path",
            Paths = ["note.txt"],
            ConfirmCommit = "ok"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("paths", result.Mode);
        Assert.Equal(["note.txt"], result.StagedFiles);
        Assert.Equal("selected\n", repo.CaptureGit("show", "HEAD:note.txt"));
        Assert.Equal("other\n", repo.CaptureGit("show", "HEAD:other.txt"));
        Assert.NotNull(result.Status);
        Assert.Contains(result.Status.Files, file => file.Path == "other.txt" && file.UnstagedStatus == "modified");
    }

    [Fact]
    public async Task Commit_RequiresMessageAndExactlyOneMode()
    {
        using var repo = new TempGitRepo("git-commit");
        repo.SeedInitialCommit();

        await Assert.ThrowsAsync<InvalidOperationException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = " ",
            CommitStagedOnly = true,
            ConfirmCommit = "ok"
        }, CancellationToken.None).AsTask());

        await Assert.ThrowsAsync<InvalidOperationException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "ambiguous",
            IncludeAll = true,
            CommitStagedOnly = true,
            ConfirmCommit = "ok"
        }, CancellationToken.None).AsTask());

        await Assert.ThrowsAsync<InvalidOperationException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "missing mode",
            ConfirmCommit = "ok"
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ExplicitPaths_RejectPathsOutsideTheRepositoryBeforeStaging()
    {
        using var repo = new TempGitRepo("git-commit");
        repo.SeedInitialCommit("note.txt", "initial\n");
        var outside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(repo.Path)!, $"outside-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "changed\n");
            File.WriteAllText(outside, "outside\n");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo.Path,
                Message = "unsafe path",
                Paths = [outside],
                ConfirmCommit = "ok"
            }, CancellationToken.None).AsTask());

            Assert.Empty(repo.CaptureGit("diff", "--cached", "--name-only"));
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public async Task CommitStagedOnly_ReturnsStructuredFailureWhenTheIndexIsClean()
    {
        using var repo = new TempGitRepo("git-commit");
        repo.SeedInitialCommit();

        var result = await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "nothing to commit",
            CommitStagedOnly = true,
            ConfirmCommit = "ok"
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("no_staged_changes", result.ErrorCode);
        Assert.Null(result.CommitHash);
    }

    [Fact]
    public async Task Commit_RequiresConfirmCommitBeforeAnyOtherValidation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
        {
            Message = "x",
            CommitStagedOnly = true
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForCommit()
    {
        GeneratedToolRegistry.RegisterAll();
        Assert.True(ToolRegistry.TryResolve("git_commit", out var handler));
        var response = await handler(new ToolCallRequest<JsonElement>
        {
            ToolCallId = 1,
            ToolId = "git_commit",
            SessionId = "test",
            Approved = false
        }, CancellationToken.None);
        Assert.False(response.IsSuccess);
    }
}
