using System.Diagnostics;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;

namespace TinadecCore.Api.Tests;

public sealed class WorkspaceSnapshotProviderTests
{
    [Fact]
    public async Task FilesystemProviderCapturesAndRestoresWithConflictGuard()
    {
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "one");
            var provider = new FileSystemWorkspaceSnapshotProvider();
            var snapshot = await provider.CaptureAsync(new WorkspaceSnapshotCaptureRequest(root));

            Assert.Equal("filesystem", snapshot.ProviderKind);
            Assert.False(snapshot.IsGit);
            Assert.Equal(2, snapshot.SchemaVersion);
            Assert.Single(snapshot.Files);
            Assert.NotNull(snapshot.Files[0].ContentBase64);

            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "changed");
            await Assert.ThrowsAsync<WorkspaceSnapshotConflictException>(() => provider.RestoreAsync(
                root, snapshot, new WorkspaceSnapshotProviderRestoreRequest()));

            var restored = await provider.RestoreAsync(root, snapshot,
                new WorkspaceSnapshotProviderRestoreRequest(AllowConflicts: true));
            Assert.Equal("restored_with_conflicts", restored.Status);
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(root, "one.txt")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task GitProviderCapturesIndexPatchesAndRestoresDeterministically()
    {
        var root = CreateTempDirectory();
        try
        {
            if (!CanRunGit(root)) return;
            await RunGitAsync(root, "init");
            await RunGitAsync(root, "config", "user.email", "tinadec-tests@example.invalid");
            await RunGitAsync(root, "config", "user.name", "Tinadec Tests");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "base\n");
            await RunGitAsync(root, "add", "tracked.txt");
            await RunGitAsync(root, "commit", "-m", "initial");

            // Capture a staged index and an unstaged worktree independently.
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "staged\n");
            await RunGitAsync(root, "add", "tracked.txt");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "unstaged\n");
            await File.WriteAllTextAsync(Path.Combine(root, "untracked.txt"), "new\n");

            var provider = new GitWorkspaceSnapshotProvider();
            var snapshot = await provider.CaptureAsync(new WorkspaceSnapshotCaptureRequest(root));
            Assert.NotNull(snapshot.Git);
            var git = snapshot.Git!;
            Assert.True(snapshot.IsGit);
            Assert.False(string.IsNullOrWhiteSpace(git.Head));
            Assert.False(string.IsNullOrWhiteSpace(git.Branch));
            Assert.False(string.IsNullOrWhiteSpace(git.IndexTreeHash));
            Assert.Contains("tracked.txt", git.StagedPatch);
            Assert.Contains("tracked.txt", git.UnstagedPatch);
            Assert.Contains("untracked.txt", git.UntrackedPaths);

            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "diverged\n");
            await Assert.ThrowsAsync<WorkspaceSnapshotConflictException>(() => provider.RestoreAsync(
                root, snapshot, new WorkspaceSnapshotProviderRestoreRequest()));

            var restored = await provider.RestoreAsync(root, snapshot,
                new WorkspaceSnapshotProviderRestoreRequest(AllowConflicts: true));
            Assert.Equal("restored_with_conflicts", restored.Status);
            Assert.Equal("unstaged\n", await File.ReadAllTextAsync(Path.Combine(root, "tracked.txt")));
            Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(root, "untracked.txt")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinadec-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempDirectory(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch (IOException) { }
            }
            Directory.Delete(root, recursive: true);
        }
        catch (Exception) { }
    }

    private static bool CanRunGit(string root)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = "git", WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            }};
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    private static async Task RunGitAsync(string root, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, stderr);
    }
}
