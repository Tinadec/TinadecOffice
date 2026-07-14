using System.Diagnostics;
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
        var repo = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "note.txt"), "staged\n");
            RunGit(repo, "add", "--", "note.txt");
            File.WriteAllText(Path.Combine(repo, "note.txt"), "unstaged\n");

            var result = await GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo,
                Message = "commit staged change",
                CommitStagedOnly = true
            }, CancellationToken.None);

            Assert.True(result.Success, result.Error);
            Assert.Equal("staged_only", result.Mode);
            Assert.Equal(["note.txt"], result.StagedFiles);
            Assert.Equal(RunGitCapture(repo, "rev-parse", "HEAD").Trim(), result.CommitHash);
            Assert.Equal("staged\n", RunGitCapture(repo, "show", "HEAD:note.txt"));
            Assert.Equal("unstaged\n", File.ReadAllText(Path.Combine(repo, "note.txt")));
            Assert.NotNull(result.Status);
            Assert.True(result.Status.HasUncommittedChanges);
            Assert.Contains(result.Status.Files, file => file.Path == "note.txt" && file.UnstagedStatus == "modified");
        }
        finally { Cleanup(repo); }
    }

    [Fact]
    public async Task IncludeAll_CommitsTrackedAndUntrackedChanges()
    {
        var repo = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "note.txt"), "changed\n");
            File.WriteAllText(Path.Combine(repo, "new.txt"), "new\n");

            var result = await GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo,
                Message = "commit all changes",
                IncludeAll = true
            }, CancellationToken.None);

            Assert.True(result.Success, result.Error);
            Assert.Equal("include_all", result.Mode);
            Assert.Equal(["new.txt", "note.txt"], result.StagedFiles.Order().ToList());
            Assert.Equal("changed\n", RunGitCapture(repo, "show", "HEAD:note.txt"));
            Assert.Equal("new\n", RunGitCapture(repo, "show", "HEAD:new.txt"));
            Assert.NotNull(result.Status);
            Assert.False(result.Status.HasUncommittedChanges);
        }
        finally { Cleanup(repo); }
    }

    [Fact]
    public async Task ExplicitPaths_CommitOnlyTheRequestedWorkingTreeChanges()
    {
        var repo = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "note.txt"), "selected\n");
            File.WriteAllText(Path.Combine(repo, "other.txt"), "not selected\n");

            var result = await GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo,
                Message = "commit selected path",
                Paths = ["note.txt"]
            }, CancellationToken.None);

            Assert.True(result.Success, result.Error);
            Assert.Equal("paths", result.Mode);
            Assert.Equal(["note.txt"], result.StagedFiles);
            Assert.Equal("selected\n", RunGitCapture(repo, "show", "HEAD:note.txt"));
            Assert.Equal("other\n", RunGitCapture(repo, "show", "HEAD:other.txt"));
            Assert.NotNull(result.Status);
            Assert.Contains(result.Status.Files, file => file.Path == "other.txt" && file.UnstagedStatus == "modified");
        }
        finally { Cleanup(repo); }
    }

    [Fact]
    public async Task Commit_RequiresMessageAndExactlyOneMode()
    {
        var repo = CreateRepo();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo,
                Message = " ",
                CommitStagedOnly = true
            }, CancellationToken.None).AsTask());

            await Assert.ThrowsAsync<InvalidOperationException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo,
                Message = "ambiguous",
                IncludeAll = true,
                CommitStagedOnly = true
            }, CancellationToken.None).AsTask());

            await Assert.ThrowsAsync<InvalidOperationException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo,
                Message = "missing mode"
            }, CancellationToken.None).AsTask());
        }
        finally { Cleanup(repo); }
    }

    [Fact]
    public async Task ExplicitPaths_RejectPathsOutsideTheRepositoryBeforeStaging()
    {
        var repo = CreateRepo();
        var outside = Path.Combine(Path.GetDirectoryName(repo)!, $"outside-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(Path.Combine(repo, "note.txt"), "changed\n");
            File.WriteAllText(outside, "outside\n");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo,
                Message = "unsafe path",
                Paths = [outside]
            }, CancellationToken.None).AsTask());

            Assert.Empty(RunGitCapture(repo, "diff", "--cached", "--name-only"));
        }
        finally
        {
            try { File.Delete(outside); } catch { }
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task CommitStagedOnly_ReturnsStructuredFailureWhenTheIndexIsClean()
    {
        var repo = CreateRepo();
        try
        {
            var result = await GitCommitTool.CommitAsync(new GitCommitArgs
            {
                RepositoryPath = repo,
                Message = "nothing to commit",
                CommitStagedOnly = true
            }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("no_staged_changes", result.ErrorCode);
            Assert.Null(result.CommitHash);
        }
        finally { Cleanup(repo); }
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

    private static string CreateRepo()
    {
        var path = Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", $"git-commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RunGit(path, "init", "--initial-branch=main");
        RunGit(path, "config", "user.name", "Test");
        RunGit(path, "config", "user.email", "test@example.com");
        RunGit(path, "config", "commit.gpgSign", "false");
        File.WriteAllText(Path.Combine(path, "note.txt"), "initial\n");
        File.WriteAllText(Path.Combine(path, "other.txt"), "other\n");
        RunGit(path, "add", "--", "note.txt", "other.txt");
        RunGit(path, "commit", "-m", "initial");
        return path;
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var start = StartInfo(cwd, args);
        start.RedirectStandardOutput = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }

    private static void RunGit(string cwd, params string[] args)
    {
        using var process = Process.Start(StartInfo(cwd, args))!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }

    private static ProcessStartInfo StartInfo(string cwd, string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static void Cleanup(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
