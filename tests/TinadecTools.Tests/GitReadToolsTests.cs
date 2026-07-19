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

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
