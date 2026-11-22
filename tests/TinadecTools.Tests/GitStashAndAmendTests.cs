using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitStashAndAmendTests
{
    [Fact]
    public async Task CommitAmend_RejectsUnbornHeadAndAmendsMessageAndChanges()
    {
        using var repo = new TempGitRepo("git-amend");
        File.WriteAllText(Path.Combine(repo.Path, "note.txt"), "draft\n");

        var unborn = await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "amend unborn",
            IncludeAll = true,
            Amend = true,
            ConfirmCommit = "amend test"
        }, CancellationToken.None);

        Assert.False(unborn.Success);
        Assert.Equal("unborn_head", unborn.ErrorCode);

        var initial = await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "initial commit",
            IncludeAll = true,
            ConfirmCommit = "initial"
        }, CancellationToken.None);
        Assert.True(initial.Success, initial.Error);

        // Amend message only (no new staged files)
        var amendedMsg = await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "initial commit (amended)",
            Amend = true,
            ConfirmCommit = "amend message"
        }, CancellationToken.None);
        Assert.True(amendedMsg.Success, amendedMsg.Error);
        Assert.True(amendedMsg.Amended);
        Assert.Equal("initial commit (amended)\n", repo.CaptureGit("log", "-1", "--pretty=%s"));

        // Amend with additional file staged
        File.WriteAllText(Path.Combine(repo.Path, "extra.txt"), "more\n");
        var amendedFiles = await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "initial commit with extra",
            IncludeAll = true,
            Amend = true,
            ConfirmCommit = "amend with extra"
        }, CancellationToken.None);
        Assert.True(amendedFiles.Success, amendedFiles.Error);
        Assert.Contains("extra.txt", repo.CaptureGit("show", "--name-only", "--pretty=", "HEAD"));
    }

    [Fact]
    public async Task StashLifecycle_PushListShowApplyDropAndPop_WorksEndToEnd()
    {
        using var repo = new TempGitRepo("git-stash");
        repo.SeedInitialCommit("tracked.txt", "v1\n");

        File.WriteAllText(Path.Combine(repo.Path, "tracked.txt"), "v2\n");
        File.WriteAllText(Path.Combine(repo.Path, "untracked.txt"), "new\n");

        var pushRes = await GitStashTools.PushAsync(new GitStashPushArgs
        {
            RepositoryPath = repo.Path,
            Message = "wip changes",
            IncludeUntracked = true,
            ConfirmStashPush = "stash wip"
        }, CancellationToken.None);
        Assert.True(pushRes.Success, pushRes.Error);
        Assert.True(pushRes.Created);
        Assert.Equal("stash@{0}", pushRes.StashRef);
        Assert.False(File.Exists(Path.Combine(repo.Path, "untracked.txt")));

        var listRes = await GitStashTools.ListAsync(new GitStashListArgs { RepositoryPath = repo.Path }, CancellationToken.None);
        Assert.True(listRes.Success, listRes.Error);
        var entry = Assert.Single(listRes.Entries);
        Assert.Equal(0, entry.Index);
        Assert.Equal("stash@{0}", entry.RefName);
        Assert.Contains("wip changes", entry.Message);

        var showRes = await GitStashTools.ShowAsync(new GitStashShowArgs
        {
            RepositoryPath = repo.Path,
            StashRef = "stash@{0}",
            IncludePatch = true
        }, CancellationToken.None);
        Assert.True(showRes.Success, showRes.Error);
        Assert.Contains(showRes.Files, f => f.NewPath == "tracked.txt");
        Assert.NotNull(showRes.Patches);
        Assert.NotEmpty(showRes.Patches!);

        var applyRes = await GitStashTools.ApplyAsync(new GitStashMutationArgs
        {
            RepositoryPath = repo.Path,
            StashRef = "stash@{0}",
            ConfirmStashApply = "apply wip"
        }, CancellationToken.None);
        Assert.True(applyRes.Success, applyRes.Error);
        Assert.Equal("v2\n", File.ReadAllText(Path.Combine(repo.Path, "tracked.txt")).Replace("\r\n", "\n"));

        var dropRes = await GitStashTools.DropAsync(new GitStashMutationArgs
        {
            RepositoryPath = repo.Path,
            StashRef = "stash@{0}",
            ConfirmStashDrop = "drop stash@{0}"
        }, CancellationToken.None);
        Assert.True(dropRes.Success, dropRes.Error);

        var afterDrop = await GitStashTools.ListAsync(new GitStashListArgs { RepositoryPath = repo.Path }, CancellationToken.None);
        Assert.Empty(afterDrop.Entries);

        // Push again and pop
        var push2 = await GitStashTools.PushAsync(new GitStashPushArgs
        {
            RepositoryPath = repo.Path,
            Message = "pop test",
            IncludeUntracked = true,
            ConfirmStashPush = "stash pop test"
        }, CancellationToken.None);
        Assert.True(push2.Success, push2.Error);

        var popRes = await GitStashTools.PopAsync(new GitStashMutationArgs
        {
            RepositoryPath = repo.Path,
            StashRef = "stash@{0}",
            ConfirmStashPop = "pop stash"
        }, CancellationToken.None);
        Assert.True(popRes.Success, popRes.Error);
        Assert.Empty((await GitStashTools.ListAsync(new GitStashListArgs { RepositoryPath = repo.Path }, CancellationToken.None)).Entries);
    }
}
