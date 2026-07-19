using System.Diagnostics;
using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class GitRemoteMutationToolsTests
{
    [Fact]
    public async Task FetchAsync_RejectsUnknownRemote()
    {
        using var repo = new TempGitRepo("git-remote");
        repo.SeedInitialCommit();
        var result = await GitRemoteMutationTools.FetchAsync(new GitRemoteMutationArgs
        {
            RepositoryPath = repo,
            Remote = "ghost",
            ConfirmFetch = "ok"
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("ghost", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_PullsFromConfiguredLocalRemote()
    {
        using var repo = new TempGitRepo("git-remote");
        repo.SeedInitialCommit();
        string bare = NewBareRemote("git-remote-bare");
        repo.RunGit("remote", "add", "origin", bare);
        repo.RunGit("push", "origin", "main");
        var result = await GitRemoteMutationTools.FetchAsync(new GitRemoteMutationArgs
        {
            RepositoryPath = repo,
            Remote = "origin",
            ConfirmFetch = "ok"
        }, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal("origin", result.Remote);
    }

    [Fact]
    public async Task PushAsync_RejectsDirtyWorkingTree()
    {
        using var repo = new TempGitRepo("git-remote");
        repo.SeedInitialCommit("a.txt", "v1\n");
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "a.txt"), "dirty\n");
        string bare = NewBareRemote("git-remote-bare");
        repo.RunGit("remote", "add", "origin", bare);
        var result = await GitRemoteMutationTools.PushAsync(new GitRemoteMutationArgs
        {
            RepositoryPath = repo,
            ConfirmPush = "ok"
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("uncommitted", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PushAsync_RejectsUnknownRemoteDefaultingToOrigin()
    {
        using var repo = new TempGitRepo("git-remote");
        repo.SeedInitialCommit();
        var result = await GitRemoteMutationTools.PushAsync(new GitRemoteMutationArgs
        {
            RepositoryPath = repo,
            ConfirmPush = "ok"
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("origin", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushAsync_RequiresSetUpstreamWhenNoUpstreamConfigured()
    {
        using var repo = new TempGitRepo("git-remote");
        repo.SeedInitialCommit("a.txt", "v1\n");
        string bare = NewBareRemote("git-remote-bare");
        repo.RunGit("remote", "add", "origin", bare);
        repo.CommitFile("a.txt", "v2\n", "second");
        var result = await GitRemoteMutationTools.PushAsync(new GitRemoteMutationArgs
        {
            RepositoryPath = repo,
            ConfirmPush = "ok"
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("upstream", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PushAsync_NoOpWhenAlreadyUpToDate()
    {
        using var repo = new TempGitRepo("git-remote");
        repo.SeedInitialCommit("a.txt", "v1\n");
        string bare = NewBareRemote("git-remote-bare");
        repo.RunGit("remote", "add", "origin", bare);
        repo.CommitFile("a.txt", "v2\n", "second");
        var first = await GitRemoteMutationTools.PushAsync(new GitRemoteMutationArgs
        {
            RepositoryPath = repo,
            SetUpstream = true,
            ConfirmPush = "ok"
        }, CancellationToken.None);
        Assert.True(first.Success, first.Error);

        var second = await GitRemoteMutationTools.PushAsync(new GitRemoteMutationArgs
        {
            RepositoryPath = repo,
            ConfirmPush = "ok"
        }, CancellationToken.None);
        Assert.True(second.Success);
        Assert.False(second.Changed);
    }

    [Fact]
    public async Task PullAsync_RejectsDetachedHead()
    {
        using var repo = new TempGitRepo("git-remote");
        repo.SeedInitialCommit();
        repo.RunGit("checkout", "--detach", "HEAD");
        var result = await GitRemoteMutationTools.PullAsync(new GitRemoteMutationArgs
        {
            RepositoryPath = repo,
            ConfirmPull = "ok"
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("detached", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mutations_RequireConfirmFields()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitRemoteMutationTools.FetchAsync(new GitRemoteMutationArgs { Remote = "x" }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitRemoteMutationTools.PushAsync(new GitRemoteMutationArgs { Remote = "x" }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitRemoteMutationTools.PullAsync(new GitRemoteMutationArgs { Remote = "x" }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GeneratedRegistry_RequiresApprovalForRemoteMutations()
    {
        GeneratedToolRegistry.RegisterAll();
        foreach (var toolId in new[] { "git_fetch", "git_push", "git_pull" })
        {
            Assert.True(ToolRegistry.TryResolve(toolId, out var handler));
            var response = await handler(new ToolCallRequest<System.Text.Json.JsonElement>
            {
                ToolCallId = 1,
                ToolId = toolId,
                SessionId = "test",
                Approved = false
            }, CancellationToken.None);
            Assert.False(response.IsSuccess);
        }
    }

    private static string NewBareRemote(string prefix)
    {
        var dir = System.IO.Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        RunProcess(dir, "git", "init", "--bare", dir);
        return dir;
    }

    private static void RunProcess(string working, string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = working,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed: {stderr}");
    }
}