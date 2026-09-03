using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitDiscardToolsTests
{
    [Fact]
    public async Task Discard_UnstagedModification_RestoresWorktree()
    {
        using var repo = new TempGitRepo("git-discard");
        repo.SeedInitialCommit("note.txt", "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(repo.Path, "note.txt"), "one\nTWO\nthree\n");

        var result = await GitDiscardTools.DiscardAsync(new GitDiscardArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["note.txt"],
            ConfirmDiscard = "test"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["note.txt"], result.DiscardedPaths);
        Assert.Empty(result.RemovedUntracked);
        Assert.Equal("one\ntwo\nthree\n", Normalize(File.ReadAllText(Path.Combine(repo.Path, "note.txt"))));
        Assert.Empty(repo.CaptureGit("diff", "--", "note.txt"));
    }

    [Fact]
    public async Task Discard_StagedModification_RestoresIndexAndWorktree()
    {
        using var repo = new TempGitRepo("git-discard");
        repo.SeedInitialCommit("note.txt", "initial\n");
        File.WriteAllText(Path.Combine(repo.Path, "note.txt"), "changed\n");
        repo.RunGit("add", "--", "note.txt");

        var result = await GitDiscardTools.DiscardAsync(new GitDiscardArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["note.txt"],
            ConfirmDiscard = "test"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["note.txt"], result.DiscardedPaths);
        Assert.Equal("initial\n", Normalize(File.ReadAllText(Path.Combine(repo.Path, "note.txt"))));
        Assert.Empty(repo.CaptureGit("diff", "--cached", "--", "note.txt"));
    }

    [Fact]
    public async Task Discard_UntrackedFile_WithoutOptIn_KeepsFile()
    {
        using var repo = new TempGitRepo("git-discard");
        repo.SeedInitialCommit();
        File.WriteAllText(Path.Combine(repo.Path, "new.txt"), "fresh\n");

        var result = await GitDiscardTools.DiscardAsync(new GitDiscardArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["new.txt"],
            ConfirmDiscard = "test"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.DiscardedPaths);
        Assert.Empty(result.RemovedUntracked);
        Assert.Equal(["new.txt"], result.SkippedPaths);
        Assert.True(File.Exists(Path.Combine(repo.Path, "new.txt")));
    }

    [Fact]
    public async Task Discard_UntrackedFile_WithOptIn_RemovesFile()
    {
        using var repo = new TempGitRepo("git-discard");
        repo.SeedInitialCommit();
        File.WriteAllText(Path.Combine(repo.Path, "new.txt"), "fresh\n");

        var result = await GitDiscardTools.DiscardAsync(new GitDiscardArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["new.txt"],
            IncludeUntracked = true,
            ConfirmDiscard = "test"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["new.txt"], result.RemovedUntracked);
        Assert.False(File.Exists(Path.Combine(repo.Path, "new.txt")));
    }

    [Fact]
    public async Task Discard_StagedNewFile_WithOptIn_RemovesFile()
    {
        using var repo = new TempGitRepo("git-discard");
        repo.SeedInitialCommit();
        File.WriteAllText(Path.Combine(repo.Path, "added.txt"), "staged new\n");
        repo.RunGit("add", "--", "added.txt");

        var result = await GitDiscardTools.DiscardAsync(new GitDiscardArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["added.txt"],
            IncludeUntracked = true,
            ConfirmDiscard = "test"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["added.txt"], result.RemovedUntracked);
        Assert.False(File.Exists(Path.Combine(repo.Path, "added.txt")));
        Assert.Empty(repo.CaptureGit("ls-files", "--", "added.txt").Trim());
    }

    [Fact]
    public async Task Discard_ConflictedPath_IsRefused()
    {
        using var repo = new TempGitRepo("git-discard");
        repo.SeedInitialCommit("note.txt", "base\n");
        repo.RunGit("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(repo.Path, "note.txt"), "feature\n");
        repo.RunGit("commit", "-am", "feature change");
        repo.RunGit("checkout", "main");
        File.WriteAllText(Path.Combine(repo.Path, "note.txt"), "main\n");
        repo.RunGit("commit", "-am", "main change");
        try
        {
            repo.RunGit("merge", "feature");
        }
        catch (InvalidOperationException)
        {
            // Expected: merge conflict leaves the worktree conflicted.
        }

        var result = await GitDiscardTools.DiscardAsync(new GitDiscardArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["note.txt"],
            IncludeUntracked = true,
            ConfirmDiscard = "test"
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("conflict", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("note.txt", result.SkippedPaths);
    }

    [Fact]
    public async Task Discard_EmptyPaths_Throws()
    {
        using var repo = new TempGitRepo("git-discard");
        repo.SeedInitialCommit();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitDiscardTools.DiscardAsync(new GitDiscardArgs
        {
            RepositoryPath = repo.Path,
            Paths = [],
            ConfirmDiscard = "test"
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Discard_MissingConfirmation_Throws()
    {
        using var repo = new TempGitRepo("git-discard");
        repo.SeedInitialCommit();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitDiscardTools.DiscardAsync(new GitDiscardArgs
        {
            RepositoryPath = repo.Path,
            Paths = ["README.md"]
        }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForDiscard()
    {
        GeneratedToolRegistry.RegisterAll();
        Assert.True(ToolRegistry.TryResolve("git_discard", out var handler));
        var response = await handler(new ToolCallRequest<JsonElement>
        {
            ToolCallId = 1,
            ToolId = "git_discard",
            SessionId = "test",
            Approved = false
        }, CancellationToken.None);
        Assert.False(response.IsSuccess);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
