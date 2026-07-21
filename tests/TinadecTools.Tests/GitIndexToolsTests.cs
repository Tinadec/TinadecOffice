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
        using var repo = new TempGitRepo("git-index");
        repo.SeedInitialCommit("note.txt", "one\ntwo\nthree\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "one\nTWO\nthree\n");
        var patch = repo.CaptureGit("diff", "--", "note.txt");

        var staged = await GitIndexTools.StageAsync(new GitIndexUpdateArgs
        {
            RepositoryPath = repo.Path,
            Patch = patch
        }, CancellationToken.None);
        Assert.True(staged.Success, staged.Error);
        Assert.Equal("patch", staged.SelectionKind);
        Assert.Equal(["note.txt"], staged.UpdatedPaths);
        Assert.Contains("+TWO", repo.CaptureGit("diff", "--cached", "--", "note.txt"));
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "note.txt")));
        Assert.Empty(repo.CaptureGit("diff", "--", "note.txt"));

        var unstaged = await GitIndexTools.UnstageAsync(new GitIndexUpdateArgs
        {
            RepositoryPath = repo.Path,
            Patch = patch
        }, CancellationToken.None);
        Assert.True(unstaged.Success, unstaged.Error);
        Assert.Equal("patch", unstaged.SelectionKind);
        Assert.Empty(repo.CaptureGit("diff", "--cached", "--", "note.txt"));
        Assert.Contains("+TWO", repo.CaptureGit("diff", "--", "note.txt"));
    }

    [Fact]
    public async Task PathsStageAndUnstage_HandleWholeFileChanges()
    {
        using var repo = new TempGitRepo("git-index");
        repo.SeedInitialCommit("note.txt", "initial\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "note.txt"), "changed\n");
        var staged = await GitIndexTools.StageAsync(new GitIndexUpdateArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["note.txt"]
        }, CancellationToken.None);
        Assert.True(staged.Success, staged.Error);
        Assert.Equal("paths", staged.SelectionKind);
        Assert.Contains("+changed", repo.CaptureGit("diff", "--cached", "--", "note.txt"));

        var unstaged = await GitIndexTools.UnstageAsync(new GitIndexUpdateArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["note.txt"]
        }, CancellationToken.None);
        Assert.True(unstaged.Success, unstaged.Error);
        Assert.Empty(repo.CaptureGit("diff", "--cached", "--", "note.txt"));
    }

    [Fact]
    public async Task TextPatch_RejectsBinaryAndNewFileHeaders()
    {
        using var repo = new TempGitRepo("git-index");
        repo.SeedInitialCommit();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitIndexTools.StageAsync(new GitIndexUpdateArgs
        {
            RepositoryPath = repo.Path,
            Patch = "diff --git a/note.txt b/note.txt\nnew file mode 100644\n@@ -0,0 +1 @@\n+x\n"
        }, CancellationToken.None).AsTask());
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
}
