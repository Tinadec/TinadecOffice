using TinadecTools.Tools.Git;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

public sealed class GitLogToolsTests
{
    // ── git_log_list ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LogList_ReturnsCommitsWithLanes()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("a.txt", "a\n", "add a");
            repo.CommitFile("b.txt", "b\n", "add b");

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
            repo.Dispose();
        }
    }

    [Fact]
    public async Task LogList_PagingWithSkip()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            for (var i = 0; i < 5; i++)
                repo.CommitFile($"f{i}.txt", $"{i}\n", $"commit {i}");

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
            repo.Dispose();
        }
    }

    [Fact]
    public async Task LogList_AfterCommitCursor()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            for (var i = 0; i < 4; i++)
                repo.CommitFile($"f{i}.txt", $"{i}\n", $"commit {i}");

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
            repo.Dispose();
        }
    }

    [Fact]
    public async Task LogList_RejectsOptionInjection()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                GitLogListTool.HandleAsync(
                    new GitLogListArgs { RepositoryPath = repo, Revs = ["--exec=bad"] },
                    CancellationToken.None).AsTask());
        }
        finally
        {
            repo.Dispose();
        }
    }

    [Fact]
    public async Task LogList_NotARepo_ReturnsFailure()
    {
        var dir = CreateWorkspaceDir("git-norepo");
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
            DeleteWorkspaceDir(dir);
        }
    }

    [Fact]
    public async Task LogList_StructuredRefs_ContainsHead()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("c.txt", "c\n", "add c");
            repo.RunGit("tag", "v1.0");

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
            repo.Dispose();
        }
    }

    // ── git_log_detail ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LogDetail_SingleCommit_ReturnsFiles()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("a.txt", "line1\nline2\n", "add a file");

            var headHash = repo.CaptureGit("rev-parse", "HEAD");
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
            repo.Dispose();
        }
    }

    [Fact]
    public async Task LogDetail_IncludePatch_ParsesHunks()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("a.txt", "line1\n", "add a");
            repo.CommitFile("a.txt", "line1\nline2\nline3\n", "expand a");

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
            repo.Dispose();
        }
    }

    [Fact]
    public async Task LogDetail_Range_ReturnsMultipleCommits()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            var c1 = repo.CaptureGit("rev-parse", "HEAD");
            repo.CommitFile("x.txt", "x\n", "add x");
            repo.CommitFile("y.txt", "y\n", "add y");
            var c3 = repo.CaptureGit("rev-parse", "HEAD");

            var result = await GitLogDetailTool.HandleAsync(
                new GitLogDetailArgs { RepositoryPath = repo, Rev = $"{c1}..{c3}" },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Commits.Count >= 2);
            Assert.NotEmpty(result.Files);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [Fact]
    public async Task LogDetail_BinaryFile_ReturnsSummaryNoHunks()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            var bytes = new byte[] { 0, 1, 2, 255, 254, 0, 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(repo, "bin.dat"), bytes);
            repo.RunGit("add", "bin.dat");
            repo.RunGit("commit", "-m", "add binary");

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
            repo.Dispose();
        }
    }

    [Fact]
    public async Task LogDetail_PatchBudget_PreservesMetadataWithoutPatch()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("large.txt", new string('x', 2_048) + "\n", "add large");

            var result = await GitLogDetailTool.HandleAsync(
                new GitLogDetailArgs
                {
                    RepositoryPath = repo,
                    Rev = "HEAD",
                    IncludePatch = true,
                    MaxPatchBytes = 32
                },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotEmpty(result.Commits);
            Assert.Contains(result.Files, file => file.NewPath == "large.txt");
            Assert.True(result.Truncated);
            Assert.Equal("patch_output_limit", result.TruncationReason);
            Assert.Null(result.Patches);
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ── git_file_history ───────────────────────────────────────────────────────

    [Fact]
    public async Task FileHistory_ReturnsEntriesForPath()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("a.txt", "v1\n", "add a");
            repo.CommitFile("a.txt", "v1\nv2\n", "edit a");
            repo.CommitFile("b.txt", "b\n", "add b"); // 不影响 a.txt

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
            repo.Dispose();
        }
    }

    [Fact]
    public async Task FileHistory_FollowRename()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("old.txt", "content\n", "add old");
            repo.RunGit("mv", "old.txt", "new.txt");
            repo.RunGit("commit", "-m", "rename to new");

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
            repo.Dispose();
        }
    }

    [Fact]
    public async Task FileHistory_IncludePatch()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("a.txt", "line1\n", "add a");
            repo.CommitFile("a.txt", "line1\nline2\n", "edit a");

            var result = await GitFileHistoryTool.HandleAsync(
                new GitFileHistoryArgs { RepositoryPath = repo, Path = "a.txt", IncludePatch = true },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.All(result.Entries, e => Assert.NotNull(e.Patch));
            Assert.Contains(result.Entries, e => e.Patch!.Hunks.Count > 0);
        }
        finally
        {
            repo.Dispose();
        }
    }

    [Fact]
    public async Task FileHistory_RejectsPathOutsideRepository()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        var external = CreateWorkspaceDir("git-external");
        try
        {
            var externalFile = Path.Combine(external, "outside.txt");
            File.WriteAllText(externalFile, "outside\n");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                GitFileHistoryTool.HandleAsync(
                    new GitFileHistoryArgs { RepositoryPath = repo, Path = externalFile },
                    CancellationToken.None).AsTask());
        }
        finally
        {
            repo.Dispose();
            DeleteWorkspaceDir(external);
        }
    }

    [Fact]
    public async Task FileHistory_RejectsLinkTraversal()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        var external = CreateWorkspaceDir("git-link-target");
        var link = Path.Combine(repo, "external-link");
        try
        {
            File.WriteAllText(Path.Combine(external, "secret.txt"), "secret\n");
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                GitFileHistoryTool.HandleAsync(
                    new GitFileHistoryArgs { RepositoryPath = repo, Path = "external-link/secret.txt" },
                    CancellationToken.None).AsTask());
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            repo.Dispose();
            DeleteWorkspaceDir(external);
        }
    }

    [Fact]
    public async Task FileHistory_PatchBudget_PreservesEntriesWithoutPatch()
    {
        var repo = new TempGitRepo("git-log"); repo.SeedInitialCommit();
        try
        {
            repo.CommitFile("large.txt", new string('x', 2_048) + "\n", "add large");

            var result = await GitFileHistoryTool.HandleAsync(
                new GitFileHistoryArgs
                {
                    RepositoryPath = repo,
                    Path = "large.txt",
                    IncludePatch = true,
                    MaxPatchBytes = 32
                },
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotEmpty(result.Entries);
            Assert.Contains(result.Entries, entry => entry.Change.NewPath == "large.txt");
            Assert.True(result.Truncated);
            Assert.Equal("patch_output_limit", result.TruncationReason);
            Assert.All(result.Entries, entry => Assert.Null(entry.Patch));
        }
        finally
        {
            repo.Dispose();
        }
    }

    // ponytail: 仅在该文件内的非 git 工作区目录 fixture；用 TempGitRepo 的 git 场景走 fixture.
    private static string CreateWorkspaceDir(string prefix)
    {
        var dir = System.IO.Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteWorkspaceDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
