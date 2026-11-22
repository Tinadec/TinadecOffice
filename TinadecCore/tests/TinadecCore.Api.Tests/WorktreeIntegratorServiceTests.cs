using System.Diagnostics;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentGraph;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Direct verification of <see cref="WorktreeIntegratorService"/> (#33):
/// clean disjoint multi-worktree merges, fail-closed preflight refusal on cross-teammate file
/// overlaps and <c>WriteScope</c> violations, and atomic <c>git merge --abort</c> rollback on
/// divergent conflict so the target repository is never left half-merged.
/// </summary>
public sealed class WorktreeIntegratorServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tinadec-wt-integrator-{Guid.NewGuid():N}");
    private readonly WorktreeIntegratorService _integrator = new();

    public WorktreeIntegratorServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task CleanDisjointWorktrees_InspectAndIntegrateNoFfCleanly()
    {
        var repo = InitGitRepo("clean-repo");
        var wtFe = CreateWorktree(repo, "wt-fe", "team/fe");
        var wtBe = CreateWorktree(repo, "wt-be", "team/be");

        WriteAndCommit(wtFe, "src/ui.ts", "export const ui = 'v2';\n", "fe: update ui");
        WriteAndCommit(wtBe, "api/routes.ts", "export const routes = 'v2';\n", "be: update routes");

        var specs = new[]
        {
            new TeammateWorktreeSpec("fe", wtFe, "team/fe", ["src"]),
            new TeammateWorktreeSpec("be", wtBe, "team/be", ["api"])
        };

        var report = await _integrator.InspectAsync(repo, specs);
        Assert.True(report.CanIntegrateCleanly, report.Summary);
        Assert.Empty(report.Overlaps);
        Assert.Empty(report.AllScopeViolations);
        Assert.Equal(2, report.Items.Count);

        var result = await _integrator.IntegrateAsync(new WorktreeIntegrationRequest(
            repo,
            specs,
            Strategy: WorktreeIntegrationStrategies.NoFf));
        Assert.True(result.Success, result.Message);
        Assert.False(result.RolledBack);
        Assert.Equal(new[] { "fe", "be" }, result.IntegratedHandles);
        Assert.True(File.Exists(Path.Combine(repo, "src", "ui.ts")));
        Assert.True(File.Exists(Path.Combine(repo, "api", "routes.ts")));
        Assert.Equal(string.Empty, RunGit(repo, "status", "--porcelain").Trim());
    }

    [Fact]
    public async Task OverlappingFilesAndWriteScopeViolations_RefuseIntegrationFailClosed()
    {
        var repo = InitGitRepo("overlap-repo");
        var headBefore = RunGit(repo, "rev-parse", "HEAD").Trim();
        var wtFe = CreateWorktree(repo, "wt-fe-overlap", "team/fe-overlap");
        var wtBe = CreateWorktree(repo, "wt-be-overlap", "team/be-overlap");

        // Both touch src/shared.ts, AND @fe writes docs/unauthorized.md outside its ["src"] WriteScope.
        WriteAndCommit(wtFe, "src/shared.ts", "export const v = 'fe';\n", "fe: shared");
        WriteAndCommit(wtFe, "docs/unauthorized.md", "# out of scope\n", "fe: out of scope");
        WriteAndCommit(wtBe, "src/shared.ts", "export const v = 'be';\n", "be: shared");

        var specs = new[]
        {
            new TeammateWorktreeSpec("fe", wtFe, "team/fe-overlap", ["src"]),
            new TeammateWorktreeSpec("be", wtBe, "team/be-overlap", ["src"])
        };

        var report = await _integrator.InspectAsync(repo, specs);
        Assert.False(report.CanIntegrateCleanly);
        var overlap = Assert.Single(report.Overlaps);
        Assert.Equal("src/shared.ts", overlap.RelativePath);
        Assert.Contains("fe", overlap.Handles);
        Assert.Contains("be", overlap.Handles);
        Assert.Contains(report.AllScopeViolations, v => v.Contains("docs/unauthorized.md", StringComparison.OrdinalIgnoreCase));

        var result = await _integrator.IntegrateAsync(new WorktreeIntegrationRequest(repo, specs));
        Assert.False(result.Success);
        Assert.NotEmpty(result.ScopeViolations);
        Assert.Equal(headBefore, RunGit(repo, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task ConflictingTargetDivergence_RunsGitMergeAbortAndLeavesTargetClean()
    {
        var repo = InitGitRepo("conflict-repo");
        WriteAndCommit(repo, "api/service.ts", "export const mode = 'initial';\n", "seed service.ts");

        var wtBe = CreateWorktree(repo, "wt-be-conflict", "team/be-conflict");
        WriteAndCommit(wtBe, "api/service.ts", "export const mode = 'teammate-branch';\n", "be: edit service.ts");

        // Advance target repo HEAD on the exact same line so git merge hits a real content conflict.
        WriteAndCommit(repo, "api/service.ts", "export const mode = 'main-diverged';\n", "main: conflicting edit");
        var headBeforeMerge = RunGit(repo, "rev-parse", "HEAD").Trim();

        var specs = new[]
        {
            new TeammateWorktreeSpec("be", wtBe, "team/be-conflict", ["api"])
        };

        var preflight = await _integrator.InspectAsync(repo, specs);
        Assert.True(preflight.CanIntegrateCleanly, preflight.Summary);

        var result = await _integrator.IntegrateAsync(new WorktreeIntegrationRequest(
            repo,
            specs,
            Strategy: WorktreeIntegrationStrategies.NoFf));

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Contains(result.ConflictFiles, f => f.EndsWith("api/service.ts", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")), "MERGE_HEAD must be cleared by git merge --abort");
        Assert.Equal(string.Empty, RunGit(repo, "status", "--porcelain").Trim());
        Assert.Equal(headBeforeMerge, RunGit(repo, "rev-parse", "HEAD").Trim());
        Assert.Contains("main-diverged", File.ReadAllText(Path.Combine(repo, "api", "service.ts")));
    }

    private string InitGitRepo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "config", "user.email", "qa@tinadec.local");
        RunGit(path, "config", "user.name", "Tinadec QA");
        WriteAndCommit(path, "README.md", "# Test Repo\n", "initial commit");
        return path;
    }

    private string CreateWorktree(string repoPath, string folderName, string branchName)
    {
        var wtPath = Path.Combine(_root, folderName);
        RunGit(repoPath, "worktree", "add", "-b", branchName, wtPath);
        RunGit(wtPath, "config", "user.email", "qa@tinadec.local");
        RunGit(wtPath, "config", "user.name", "Tinadec QA");
        return wtPath;
    }

    private static void WriteAndCommit(string repoPath, string relativeFile, string content, string message)
    {
        var fullPath = Path.Combine(repoPath, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        RunGit(repoPath, "add", relativeFile);
        RunGit(repoPath, "commit", "-m", message);
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }
}
