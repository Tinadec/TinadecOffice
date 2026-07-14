using System.Diagnostics;
using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitReadToolsTests
{
    [Fact]
    public async Task ReadTools_ReturnStructuredStatusDiffRefsAndRevisionFile()
    {
        var repo = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "note.txt"), "changed\n");
            var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = repo }, CancellationToken.None);
            var diff = await GitReadTools.DiffAsync(new GitDiffArgs { RepositoryPath = repo, Target = "working_tree" }, CancellationToken.None);
            var refs = await GitReadTools.RefListAsync(new GitRefListArgs { RepositoryPath = repo }, CancellationToken.None);
            var file = await GitReadTools.FileAtRevisionAsync(new GitFileAtRevisionArgs { RepositoryPath = repo, Path = "note.txt", Rev = "HEAD" }, CancellationToken.None);

            Assert.True(status.Success);
            Assert.True(status.HasUncommittedChanges);
            Assert.Contains(status.Files, item => item.Path == "note.txt");
            Assert.True(diff.Success);
            Assert.Contains("-initial", Assert.Single(diff.Sections).Diff);
            Assert.True(refs.Success);
            Assert.Contains(refs.Refs, item => item.Name == "main" && item.Type == "branch");
            Assert.True(file.Success);
            Assert.Equal("initial\n", file.Content);
        }
        finally { Cleanup(repo); }
    }

    [Fact]
    public async Task ReadTools_RejectLinkTraversalAndOptionLikeRevisions()
    {
        var repo = CreateRepo();
        var external = Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            File.WriteAllText(Path.Combine(external, "outside.txt"), "outside");
            Directory.CreateSymbolicLink(Path.Combine(repo, "outside"), external);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GitReadTools.FileAtRevisionAsync(new GitFileAtRevisionArgs { RepositoryPath = repo, Path = "outside/outside.txt" }, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => GitReadTools.FileAtRevisionAsync(new GitFileAtRevisionArgs { RepositoryPath = repo, Path = "note.txt", Rev = "--output" }, CancellationToken.None).AsTask());
        }
        finally { Cleanup(repo); Cleanup(external); }
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForEveryNewGitReadTool()
    {
        GeneratedToolRegistry.RegisterAll();
        foreach (var toolId in new[] { "git_status", "git_push_readiness", "git_diff", "git_branch_list", "git_worktree_list", "git_ref_list", "git_remote_list", "git_blame", "git_file_at_revision", "git_conflict_preview" })
        {
            Assert.True(ToolRegistry.TryResolve(toolId, out var handler));
            var response = await handler(new ToolCallRequest<System.Text.Json.JsonElement> { ToolCallId = 1, ToolId = toolId, Approved = false }, CancellationToken.None);
            Assert.False(response.IsSuccess);
        }
    }

    private static string CreateRepo()
    {
        var path = Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", $"git-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RunGit(path, "init", "--initial-branch=main");
        RunGit(path, "config", "user.name", "Test");
        RunGit(path, "config", "user.email", "test@example.com");
        RunGit(path, "config", "commit.gpgSign", "false");
        File.WriteAllText(Path.Combine(path, "note.txt"), "initial\n");
        RunGit(path, "add", "note.txt");
        RunGit(path, "commit", "-m", "initial");
        return path;
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
