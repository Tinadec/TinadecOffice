using System.Diagnostics;
using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitIndexToolsTests
{
    [Fact]
    public async Task StageAndUnstage_TextPatch_OnlyUpdateTheGitIndex()
    {
        var repo = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "note.txt"), "one\nTWO\nthree\n");
            var patch = RunGitCapture(repo, "diff", "--", "note.txt");

            var staged = await GitIndexTools.StageAsync(new GitIndexUpdateArgs
            {
                RepositoryPath = repo,
                Patch = patch
            }, CancellationToken.None);
            Assert.True(staged.Success);
            Assert.Equal("patch", staged.SelectionKind);
            Assert.Equal(["note.txt"], staged.UpdatedPaths);
            Assert.Contains("+TWO", RunGitCapture(repo, "diff", "--cached", "--", "note.txt"));
            Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(Path.Combine(repo, "note.txt")));
            Assert.Empty(RunGitCapture(repo, "diff", "--", "note.txt"));

            var unstaged = await GitIndexTools.UnstageAsync(new GitIndexUpdateArgs
            {
                RepositoryPath = repo,
                Patch = patch
            }, CancellationToken.None);
            Assert.True(unstaged.Success);
            Assert.Equal("patch", unstaged.SelectionKind);
            Assert.Empty(RunGitCapture(repo, "diff", "--cached", "--", "note.txt"));
            Assert.Contains("+TWO", RunGitCapture(repo, "diff", "--", "note.txt"));
        }
        finally { Cleanup(repo); }
    }

    [Fact]
    public async Task PathsStageAndUnstage_HandleWholeFileChanges()
    {
        var repo = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "note.txt"), "changed\n");
            var staged = await GitIndexTools.StageAsync(new GitIndexUpdateArgs
            {
                RepositoryPath = repo,
                Paths = ["note.txt"]
            }, CancellationToken.None);
            Assert.True(staged.Success);
            Assert.Equal("paths", staged.SelectionKind);
            Assert.Contains("+changed", RunGitCapture(repo, "diff", "--cached", "--", "note.txt"));

            var unstaged = await GitIndexTools.UnstageAsync(new GitIndexUpdateArgs
            {
                RepositoryPath = repo,
                Paths = ["note.txt"]
            }, CancellationToken.None);
            Assert.True(unstaged.Success);
            Assert.Empty(RunGitCapture(repo, "diff", "--cached", "--", "note.txt"));
        }
        finally { Cleanup(repo); }
    }

    [Fact]
    public async Task TextPatch_RejectsBinaryAndNewFileHeaders()
    {
        var repo = CreateRepo();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => GitIndexTools.StageAsync(new GitIndexUpdateArgs
            {
                RepositoryPath = repo,
                Patch = "diff --git a/note.txt b/note.txt\nnew file mode 100644\n@@ -0,0 +1 @@\n+x\n"
            }, CancellationToken.None).AsTask());
        }
        finally { Cleanup(repo); }
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForIndexUpdates()
    {
        GeneratedToolRegistry.RegisterAll();
        foreach (var toolId in new[] { "git_stage", "git_unstage" })
        {
            Assert.True(ToolRegistry.TryResolve(toolId, out var handler));
            var response = await handler(new ToolCallRequest<JsonElement>
            {
                ToolCallId = 1,
                ToolId = toolId,
                SessionId = "test",
                Approved = false
            }, CancellationToken.None);
            Assert.False(response.IsSuccess);
        }
    }

    private static string CreateRepo()
    {
        var path = Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", $"git-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RunGit(path, "init", "--initial-branch=main");
        RunGit(path, "config", "user.name", "Test");
        RunGit(path, "config", "user.email", "test@example.com");
        RunGit(path, "config", "commit.gpgSign", "false");
        File.WriteAllText(Path.Combine(path, "note.txt"), "one\ntwo\nthree\n");
        RunGit(path, "add", "note.txt");
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
        var start = new ProcessStartInfo { FileName = "git", WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static void Cleanup(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
