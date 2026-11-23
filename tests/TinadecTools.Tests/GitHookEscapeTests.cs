using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TinadecTools.Runtime;
using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

/// <summary>
/// The escape chain this closes: an agent writes .git/hooks/pre-commit, then calls
/// git_commit, and the hook runs as an ordinary child of TinadecTools — outside the
/// command sandbox, with the host's ambient environment and whatever credentials it
/// holds. Both directions are pinned: suppression must let the commit through, and
/// git.allow_hooks must genuinely hand control back to the hook. A test that only
/// asserts the first would stay green if suppression were unconditional.
///
/// Only local commits run here. No push, no remote, nothing that touches a user session.
/// </summary>
public sealed class GitHookEscapeTests
{
    private const string FailingHook = "#!/bin/sh\necho HOOK_RAN >&2\nexit 1\n";

    private static JsonElement Context(string settings) => JsonDocument.Parse(
        "{\"schema_version\":1,\"settings_hash\":\"test\",\"settings\":" + settings
        + ",\"allowed_tool_ids\":[\"git_commit\",\"git_conflict_resolve\"],\"read_roots\":[],\"mcp_servers\":[]}").RootElement;

    private static void PlantFailingPreCommit(TempGitRepo repo)
    {
        var hooks = Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        var hookPath = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hookPath, FailingHook);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        // Repository-level hooksPath, so the test also proves -c outranks it rather
        // than only working on a repository that never configured one.
        repo.RunGit("config", "core.hooksPath", ".git/hooks");
    }

    private static async Task<TinadecTools.Tools.Git.GitCommitResult> CommitAsync(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, "note.txt"), "changed\n");
        return await GitCommitTool.CommitAsync(new GitCommitArgs
        {
            RepositoryPath = repo.Path,
            Message = "agent commit",
            IncludeAll = true,
            ConfirmCommit = "ok"
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PlantedFailingPreCommit_DoesNotBlockTheAgentCommit()
    {
        using var repo = new TempGitRepo("git-hook-escape");
        repo.SeedInitialCommit();
        PlantFailingPreCommit(repo);

        var result = await CommitAsync(repo);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("HOOK_RAN", result.CommitOutput ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowHooks_LetsThePlantedHookBlockTheCommit()
    {
        using var repo = new TempGitRepo("git-hook-allowed");
        repo.SeedInitialCommit();
        PlantFailingPreCommit(repo);

        using var scope = ToolExecutionContext.Enter(Context("{\"git\":{\"allow_hooks\":true}}"), "git_commit");
        var result = await CommitAsync(repo);

        Assert.False(result.Success);
        Assert.Contains("HOOK_RAN", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuppressionIsTheDefaultWithoutAnyAmbientContext()
    {
        using var repo = new TempGitRepo("git-hook-default");
        repo.SeedInitialCommit();
        PlantFailingPreCommit(repo);

        Assert.Null(ToolExecutionContext.Current);
        Assert.True((await CommitAsync(repo)).Success);
    }

    /// <summary>
    /// git_conflict_resolve writes the working tree with File.Delete/File.WriteAllTextAsync
    /// instead of going through the file tools, so it carries its own boundary assertion.
    ///
    /// Coverage is split honestly. Git refuses to index a path inside .git ("Ignoring path
    /// .git/hooks/pre-commit"), so a conflict there cannot be constructed and the tool
    /// returns "not an unresolved Git conflict" before reaching the write. The assertion's
    /// *wiring* is therefore not behaviourally reachable and is NOT covered here; what is
    /// covered is the observable contract below, and the predicate itself in
    /// WorkspacePathBoundaryTests.GitMetadata_IsRefusedForWrite.
    /// </summary>
    [Fact]
    public async Task ConflictResolve_OnAGitMetadataPath_FailsAndCreatesNothing()
    {
        using var repo = new TempGitRepo("git-hook-conflict");
        repo.SeedInitialCommit();
        const string target = ".git/hooks/pre-commit";
        PlantIndexConflict(repo, target);

        var result = await GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
        {
            RepositoryPath = repo.Path,
            Path = target,
            Strategy = "ours",
            ConfirmResolve = "ok"
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(repo.Path, ".git", "hooks", "pre-commit")));
    }

    [Fact]
    public async Task ConflictResolve_StillResolvesAnOrdinaryWorkingTreeFile()
    {
        using var repo = new TempGitRepo("git-conflict-ordinary");
        repo.SeedInitialCommit();
        PlantIndexConflict(repo, "note.txt");

        var result = await GitConflictResolveTool.ResolveAsync(new GitConflictResolveArgs
        {
            RepositoryPath = repo.Path,
            Path = "note.txt",
            Strategy = "ours",
            ConfirmResolve = "ok"
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("ours\n", File.ReadAllText(Path.Combine(repo.Path, "note.txt")));
    }

    /// <summary>
    /// Writes stages 1/2/3 for one path so the tool sees a real unresolved conflict.
    /// Git silently ignores paths inside .git, so for those the index stays clean and
    /// the tool reports "not an unresolved Git conflict" — which is the behaviour the
    /// metadata test above asserts.
    /// </summary>
    private static void PlantIndexConflict(TempGitRepo repo, string relativePath)
    {
        string Blob(string content)
        {
            var scratch = Path.Combine(repo.Path, ".git", "conflict-fixture");
            File.WriteAllText(scratch, content);
            var hash = repo.CaptureGit("hash-object", "-w", scratch).Trim();
            File.Delete(scratch);
            return hash;
        }

        var info = new StringBuilder()
            .Append("100644 ").Append(Blob("base\n")).Append(" 1\t").Append(relativePath).Append('\n')
            .Append("100644 ").Append(Blob("ours\n")).Append(" 2\t").Append(relativePath).Append('\n')
            .Append("100644 ").Append(Blob("theirs\n")).Append(" 3\t").Append(relativePath).Append('\n')
            .ToString();

        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repo.Path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("update-index");
        start.ArgumentList.Add("--index-info");
        using var process = Process.Start(start)!;
        process.StandardInput.Write(info);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git update-index --index-info failed: {error}");
    }
}