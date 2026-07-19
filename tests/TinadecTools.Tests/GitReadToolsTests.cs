using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitReadToolsTests
{
    [Fact]
    public async Task ReadTools_ReturnStructuredStatusDiffRefsAndRevisionFile()
    {
        using var repo = new TempGitRepo("git-read");
        repo.SeedInitialCommit("note.txt", "initial\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "changed\n");
        var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = repo.Path }, CancellationToken.None);
        var diff = await GitReadTools.DiffAsync(new GitDiffArgs { RepositoryPath = repo.Path, Target = "working_tree" }, CancellationToken.None);
        var refs = await GitReadTools.RefListAsync(new GitRefListArgs { RepositoryPath = repo.Path }, CancellationToken.None);
        var file = await GitReadTools.FileAtRevisionAsync(new GitFileAtRevisionArgs { RepositoryPath = repo.Path, Path = "note.txt", Rev = "HEAD" }, CancellationToken.None);

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

    [Fact]
    public async Task ReadTools_RejectLinkTraversalAndOptionLikeRevisions()
    {
        using var repo = new TempGitRepo("git-read");
        repo.SeedInitialCommit("note.txt", "initial\n");
        var external = System.IO.Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            File.WriteAllText(System.IO.Path.Combine(external, "outside.txt"), "outside");
            Directory.CreateSymbolicLink(System.IO.Path.Combine(repo.Path, "outside"), external);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GitReadTools.FileAtRevisionAsync(new GitFileAtRevisionArgs { RepositoryPath = repo.Path, Path = "outside/outside.txt" }, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => GitReadTools.FileAtRevisionAsync(new GitFileAtRevisionArgs { RepositoryPath = repo.Path, Path = "note.txt", Rev = "--output" }, CancellationToken.None).AsTask());
        }
        finally
        {
            try { if (Directory.Exists(external)) Directory.Delete(external, recursive: true); } catch { }
        }
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

    [Fact]
    public async Task Blame_ReturnsLineAuthorAndContent()
    {
        using var repo = new TempGitRepo("git-read");
        repo.SeedInitialCommit("a.txt", "line1\nline2\n");
        repo.CommitFile("a.txt", "line1\nline2\nline3\n", "add line3");
        var blame = await GitReadTools.BlameAsync(new GitBlameArgs { RepositoryPath = repo, Path = "a.txt" }, CancellationToken.None);
        Assert.True(blame.Success, blame.Error);
        Assert.Equal(3, blame.Lines.Count);
        Assert.All(blame.Lines, line => Assert.False(string.IsNullOrEmpty(line.Commit)));
        Assert.Equal("line3", blame.Lines[2].Content);
    }

    [Fact]
    public async Task RemoteList_ReturnsConfiguredRemoteFetchUrl()
    {
        using var repo = new TempGitRepo("git-read");
        repo.SeedInitialCommit();
        var bareDir = NewBareRepo("git-read-bare");
        repo.RunGit("remote", "add", "origin", bareDir);
        var list = await GitReadTools.RemoteListAsync(new GitRemoteListArgs { RepositoryPath = repo }, CancellationToken.None);
        Assert.True(list.Success, list.Error);
        Assert.Contains(list.Remotes, r => r.Name == "origin" && !string.IsNullOrEmpty(r.FetchUrl));
    }

    [Fact]
    public async Task WorktreeList_MarksMainWorktreeAsCurrent()
    {
        using var repo = new TempGitRepo("git-read");
        repo.SeedInitialCommit();
        var list = await GitReadTools.WorktreeListAsync(new GitWorktreeListArgs { RepositoryPath = repo }, CancellationToken.None);
        Assert.True(list.Success, list.Error);
        Assert.True(list.Worktrees.Count >= 1);
        Assert.Contains(list.Worktrees, w => w.IsCurrent && Normalize(w.Path) == Normalize(repo.Path));
    }

    [Fact]
    public async Task PushReadiness_ReportsBlockersForDirtyOrNoUpstream()
    {
        using var repo = new TempGitRepo("git-read");
        repo.SeedInitialCommit("a.txt", "v1\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "a.txt"), "dirty\n");
        var readiness = await GitReadTools.PushReadinessAsync(new GitPushReadinessArgs { RepositoryPath = repo }, CancellationToken.None);
        Assert.True(readiness.Success, readiness.Error);
        Assert.False(readiness.Ready);
        Assert.NotEmpty(readiness.Blockers);
    }

    private static string NewBareRepo(string prefix)
    {
        var dir = System.IO.Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        RunProcess(dir, "git", "init", "--bare", dir);
        return dir;
    }

    private static void RunProcess(string working, string fileName, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = working,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed: {stderr}");
    }

    private static string Normalize(string? path) =>
        path is null ? string.Empty : System.IO.Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));
}
