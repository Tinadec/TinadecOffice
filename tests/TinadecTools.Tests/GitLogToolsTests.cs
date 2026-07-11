using System.Diagnostics;
using TinadecTools.Tools.Git;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

public sealed class GitLogToolsTests
{
    // ── temp repo fixture ──────────────────────────────────────────────────────

    private static string CreateTempRepo()
    {
        var dir = CreateWorkspaceTempDirectory("git");
        Directory.CreateDirectory(dir);

        RunGit(dir, "init", "--initial-branch=main");
        RunGit(dir, "config", "user.name", "Test");
        RunGit(dir, "config", "user.email", "test@example.com");
        // 确保有初始提交
        File.WriteAllText(Path.Combine(dir, "README.md"), "# hello\n");
        RunGit(dir, "add", "README.md");
        RunGit(dir, "commit", "-m", "initial");
        return dir;
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    private static void CommitFile(string repo, string path, string content, string msg)
    {
        File.WriteAllText(Path.Combine(repo, path), content);
        RunGit(repo, "add", path);
        RunGit(repo, "commit", "-m", msg);
    }

    // ── git_log_list ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LogList_ReturnsCommitsWithLanes()
    {
        var repo = CreateTempRepo();
        try
        {
            CommitFile(repo, "a.txt", "a\n", "add a");
            CommitFile(repo, "b.txt", "b\n", "add b");

            var result = await GitLogListTool.HandleAsync(
                new GitLogListArgs { RepositoryPath = repo, Limit = 10 },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Commits.Count >= 3);
            var first = result.Commits[0];
            Assert.False(string.IsNullOrEmpty(first.Hash));
            Assert.False(string.IsNullOrEmpty(first.ShortHash));
            Assert.False(string.IsNullOrEmpty(first.Subject));
            Assert.True(first.LaneIndex >= 0);
            // 线性历史：每个 commit 有 0 或 1 个 parent edge
            Assert.True(first.Edges.Count <= 1);
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task LogList_PagingWithSkip()
    {
        var repo = CreateTempRepo();
        try
        {
            for (var i = 0; i < 5; i++)
                CommitFile(repo, $"f{i}.txt", $"{i}\n", $"commit {i}");

            var page1 = await GitLogListTool.HandleAsync(
                new GitLogListArgs { RepositoryPath = repo, Limit = 2, Skip = 0 },
                CancellationToken.None);
            var page2 = await GitLogListTool.HandleAsync(
                new GitLogListArgs { RepositoryPath = repo, Limit = 2, Skip = 2 },
                CancellationToken.None);

            Assert.True(page1.Success && page2.Success);
            Assert.Equal(2, page1.Commits.Count);
            Assert.True(page1.Truncated);
            Assert.Equal(2, page2.Commits.Count);
            // 两页不重叠
            Assert.NotEqual(page1.Commits[0].Hash, page2.Commits[0].Hash);
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task LogList_AfterCommitCursor()
    {
        var repo = CreateTempRepo();
        try
        {
            for (var i = 0; i < 4; i++)
                CommitFile(repo, $"f{i}.txt", $"{i}\n", $"commit {i}");

            var page1 = await GitLogListTool.HandleAsync(
                new GitLogListArgs { RepositoryPath = repo, Limit = 2 },
                CancellationToken.None);
            var page2 = await GitLogListTool.HandleAsync(
                new GitLogListArgs { RepositoryPath = repo, Limit = 2, AfterCommit = page1.NextCursor },
                CancellationToken.None);

            Assert.True(page1.Success && page2.Success);
            Assert.NotEmpty(page1.Commits);
            Assert.NotEmpty(page2.Commits);
            Assert.DoesNotContain(page1.NextCursor, page2.Commits.Select(c => c.Hash));
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task LogList_RejectsOptionInjection()
    {
        var repo = CreateTempRepo();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                GitLogListTool.HandleAsync(
                    new GitLogListArgs { RepositoryPath = repo, Revs = ["--exec=bad"] },
                    CancellationToken.None).AsTask());
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task LogList_NotARepo_ReturnsFailure()
    {
        var dir = CreateWorkspaceTempDirectory("git-norepo");
        Directory.CreateDirectory(dir);
        try
        {
            var result = await GitLogListTool.HandleAsync(
                new GitLogListArgs { RepositoryPath = dir },
                CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(GitCli.NotARepoCode, result.ErrorCode);
        }
        finally
        {
            CleanupRepo(dir);
        }
    }

    [Fact]
    public async Task LogList_StructuredRefs_ContainsHead()
    {
        var repo = CreateTempRepo();
        try
        {
            CommitFile(repo, "c.txt", "c\n", "add c");
            RunGit(repo, "tag", "v1.0");

            var result = await GitLogListTool.HandleAsync(
                new GitLogListArgs { RepositoryPath = repo, Limit = 1 },
                CancellationToken.None);

            Assert.True(result.Success);
            var head = result.Commits[0];
            Assert.Contains(head.Refs, r => r.IsHead && r.Type == "head");
            Assert.Contains(head.Refs, r => r.Type == "branch" && r.Name == "main");
            Assert.Contains(head.Refs, r => r.Type == "tag" && r.Name == "v1.0");
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    // ── git_log_detail ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LogDetail_SingleCommit_ReturnsFiles()
    {
        var repo = CreateTempRepo();
        try
        {
            CommitFile(repo, "a.txt", "line1\nline2\n", "add a file");

            var headHash = RunGitCapture(repo, "rev-parse", "HEAD");
            var result = await GitLogDetailTool.HandleAsync(
                new GitLogDetailArgs { RepositoryPath = repo, Rev = headHash },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(result.Commits);
            Assert.NotEmpty(result.Files);
            var a = result.Files.FirstOrDefault(f => f.NewPath == "a.txt");
            Assert.NotNull(a);
            Assert.Equal("A", a!.Status);
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task LogDetail_IncludePatch_ParsesHunks()
    {
        var repo = CreateTempRepo();
        try
        {
            CommitFile(repo, "a.txt", "line1\n", "add a");
            CommitFile(repo, "a.txt", "line1\nline2\nline3\n", "expand a");

            var result = await GitLogDetailTool.HandleAsync(
                new GitLogDetailArgs { RepositoryPath = repo, Rev = "HEAD", IncludePatch = true },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(result.Patches);
            Assert.NotEmpty(result.Patches!);
            var patch = result.Patches![0];
            Assert.NotEmpty(patch.Hunks);
            Assert.Contains(patch.Hunks.SelectMany(h => h.Lines), l => l.Type == "add");
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task LogDetail_Range_ReturnsMultipleCommits()
    {
        var repo = CreateTempRepo();
        try
        {
            var c1 = RunGitCapture(repo, "rev-parse", "HEAD");
            CommitFile(repo, "x.txt", "x\n", "add x");
            CommitFile(repo, "y.txt", "y\n", "add y");
            var c3 = RunGitCapture(repo, "rev-parse", "HEAD");

            var result = await GitLogDetailTool.HandleAsync(
                new GitLogDetailArgs { RepositoryPath = repo, Rev = $"{c1}..{c3}" },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Commits.Count >= 2);
            Assert.NotEmpty(result.Files);
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task LogDetail_BinaryFile_ReturnsSummaryNoHunks()
    {
        var repo = CreateTempRepo();
        try
        {
            var bytes = new byte[] { 0, 1, 2, 255, 254, 0, 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(repo, "bin.dat"), bytes);
            RunGit(repo, "add", "bin.dat");
            RunGit(repo, "commit", "-m", "add binary");

            var result = await GitLogDetailTool.HandleAsync(
                new GitLogDetailArgs { RepositoryPath = repo, Rev = "HEAD", IncludePatch = true },
                CancellationToken.None);

            Assert.True(result.Success);
            var bin = result.Files.FirstOrDefault(f => f.NewPath == "bin.dat");
            Assert.NotNull(bin);
            Assert.True(bin!.IsBinary);
            Assert.NotNull(bin.BlobHash);
            Assert.True(bin.ByteSize > 0);
            if (result.Patches is not null)
            {
                var pbin = result.Patches.FirstOrDefault(p => p.NewPath == "bin.dat");
                if (pbin is not null)
                {
                    Assert.True(pbin.IsBinary);
                    Assert.Empty(pbin.Hunks);
                }
            }
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    // ── git_file_history ───────────────────────────────────────────────────────

    [Fact]
    public async Task FileHistory_ReturnsEntriesForPath()
    {
        var repo = CreateTempRepo();
        try
        {
            CommitFile(repo, "a.txt", "v1\n", "add a");
            CommitFile(repo, "a.txt", "v1\nv2\n", "edit a");
            CommitFile(repo, "b.txt", "b\n", "add b"); // 不影响 a.txt

            var result = await GitFileHistoryTool.HandleAsync(
                new GitFileHistoryArgs { RepositoryPath = repo, Path = "a.txt" },
                CancellationToken.None);

            Assert.True(result.Success);
            // a.txt 至少被 initial 之后的两次改动涉及（initial 不含 a.txt）
            Assert.True(result.Entries.Count >= 2);
            Assert.All(result.Entries, e => Assert.Equal("a.txt", e.Change.NewPath));
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task FileHistory_FollowRename()
    {
        var repo = CreateTempRepo();
        try
        {
            CommitFile(repo, "old.txt", "content\n", "add old");
            RunGit(repo, "mv", "old.txt", "new.txt");
            RunGit(repo, "commit", "-m", "rename to new");

            var result = await GitFileHistoryTool.HandleAsync(
                new GitFileHistoryArgs { RepositoryPath = repo, Path = "new.txt", Follow = true },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Entries.Count >= 2);
            // 至少有一条记录涉及 old.txt（rename 之前的提交）
            Assert.Contains(result.Entries, e => e.Change.OldPath == "old.txt" || e.Patch?.OldPath == "old.txt");
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    [Fact]
    public async Task FileHistory_IncludePatch()
    {
        var repo = CreateTempRepo();
        try
        {
            CommitFile(repo, "a.txt", "line1\n", "add a");
            CommitFile(repo, "a.txt", "line1\nline2\n", "edit a");

            var result = await GitFileHistoryTool.HandleAsync(
                new GitFileHistoryArgs { RepositoryPath = repo, Path = "a.txt", IncludePatch = true },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.All(result.Entries, e => Assert.NotNull(e.Patch));
            Assert.Contains(result.Entries, e => e.Patch!.Hunks.Count > 0);
        }
        finally
        {
            CleanupRepo(repo);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string CreateWorkspaceTempDirectory(string prefix)
    {
        var dir = Path.Combine(
            FileToolRuntime.WorkspaceRoot,
            ".tinadec-tools-tests",
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout.Trim();
    }

    private static void CleanupRepo(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                // .git 可能只读，先清属性
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best-effort */ }
    }
}
