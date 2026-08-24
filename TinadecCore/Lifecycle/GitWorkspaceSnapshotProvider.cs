using System.ComponentModel;
using System.Diagnostics;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Lifecycle;

/// <summary>
/// Git-aware workspace provider. Every Git invocation uses
/// <see cref="ProcessStartInfo.ArgumentList"/>; no shell command is assembled
/// from workspace or branch input.
/// </summary>
public sealed class GitWorkspaceSnapshotProvider : IWorkspaceSnapshotProvider
{
    public string Kind => "git";

    public bool CanHandle(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return false;
        var root = Path.GetFullPath(workspaceRoot);
        return Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"));
    }

    public async Task<WorkspaceSnapshotDocument> CaptureAsync(
        WorkspaceSnapshotCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandle(request.WorkspaceRoot))
            throw new InvalidOperationException("The workspace is not a Git repository.");
        var root = Path.GetFullPath(request.WorkspaceRoot);
        var (files, hash) = await WorkspaceSnapshotProviderSupport.CaptureFilesAsync(request, cancellationToken).ConfigureAwait(false);
        var state = await CaptureGitStateAsync(root, hash, cancellationToken).ConfigureAwait(false);
        return new WorkspaceSnapshotDocument(
            WorkspaceSnapshotProviderSupport.SnapshotSchemaVersion,
            Kind,
            IsGit: true,
            request.IncludeHidden,
            hash,
            files,
            state);
    }

    public async Task<WorkspaceSnapshotProviderRestoreResult> RestoreAsync(
        string workspaceRoot,
        WorkspaceSnapshotDocument snapshot,
        WorkspaceSnapshotProviderRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandle(workspaceRoot))
            throw new DirectoryNotFoundException("The Git workspace was not found.");
        if (snapshot.Git is null)
            throw new InvalidDataException("Git snapshot metadata is missing.");

        var root = Path.GetFullPath(workspaceRoot);
        var current = await CaptureAsync(new WorkspaceSnapshotCaptureRequest(
            root, snapshot.IncludeHidden, 100_000, 1024L * 1024 * 1024), cancellationToken).ConfigureAwait(false);
        var expectedHash = string.IsNullOrWhiteSpace(request.ExpectedWorkspaceHash)
            ? snapshot.WorkspaceHash
            : request.ExpectedWorkspaceHash.Trim();
        var conflicts = CompareGitState(snapshot, current, expectedHash);
        if (conflicts.Count != 0 && !request.AllowConflicts)
            throw new WorkspaceSnapshotConflictException("Git workspace changed after the snapshot was created.", conflicts);

        var applied = 0;
        var git = snapshot.Git;
        // Restore references first. A reference update is explicit and scoped to
        // the captured branch; it never interpolates a shell command.
        if (git.HeadReferenceExists && !string.IsNullOrWhiteSpace(git.HeadReference) && !string.IsNullOrWhiteSpace(git.Head))
        {
            var update = await RunGitAsync(root, ["update-ref", git.HeadReference, git.Head], null, cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(update, "Git reference restoration failed.");
        }

        if (!string.IsNullOrWhiteSpace(git.Head))
        {
            if (!string.IsNullOrWhiteSpace(git.Branch) && git.HeadReferenceExists)
            {
                var checkout = await RunGitAsync(root, ["checkout", "--force", git.Branch], null, cancellationToken).ConfigureAwait(false);
                if (!checkout.Succeeded)
                {
                    // A branch may not be checked out when a repository was
                    // captured detached. Detach at the captured commit in that
                    // case and continue with the durable index/worktree restore.
                    var detach = await RunGitAsync(root, ["checkout", "--force", "--detach", git.Head], null, cancellationToken).ConfigureAwait(false);
                    EnsureGitSuccess(detach, "Git HEAD restoration failed.");
                }
            }
            var reset = await RunGitAsync(root, ["reset", "--hard", git.Head], null, cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(reset, "Git HEAD restoration failed.");
        }

        var fileSnapshot = snapshot with { WorkspaceHash = expectedHash };
        var fileResult = await WorkspaceSnapshotProviderSupport.RestoreFilesAsync(
            root, fileSnapshot, request.AllowConflicts, cancellationToken).ConfigureAwait(false);
        applied += fileResult.AppliedFileCount;
        conflicts.AddRange(fileResult.Conflicts.Where(x => !conflicts.Contains(x, StringComparer.Ordinal)));

        if (!string.IsNullOrWhiteSpace(git.IndexTreeHash))
        {
            var readTree = await RunGitAsync(root, ["read-tree", git.IndexTreeHash], null, cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(readTree, "Git index restoration failed.");
        }

        // A second capture is the final idempotency/conflict check. In an
        // allow-conflicts restore it determines whether the result is marked as
        // restored_with_conflicts rather than claiming an exact restoration.
        var restored = await CaptureAsync(new WorkspaceSnapshotCaptureRequest(
            root, snapshot.IncludeHidden, 100_000, 1024L * 1024 * 1024), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(restored.WorkspaceHash, snapshot.WorkspaceHash, StringComparison.OrdinalIgnoreCase))
            conflicts.Add("workspace_hash_after_restore");
        if (restored.Git is null || !GitStateEquivalent(snapshot.Git, restored.Git))
            conflicts.Add("git_state_after_restore");

        conflicts = conflicts.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new WorkspaceSnapshotProviderRestoreResult(
            conflicts.Count == 0 ? "restored" : "restored_with_conflicts",
            snapshot.WorkspaceHash,
            conflicts,
            applied);
    }

    private static async Task<GitWorkspaceSnapshotState> CaptureGitStateAsync(
        string root,
        string worktreeHash,
        CancellationToken cancellationToken)
    {
        var headResult = await RunGitAsync(root, ["rev-parse", "--verify", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        var head = headResult.Succeeded ? FirstLine(headResult.Stdout) : null;
        var branchResult = await RunGitAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        var branch = branchResult.Succeeded ? FirstLine(branchResult.Stdout) : null;
        var headReference = string.IsNullOrWhiteSpace(branch) ? null : $"refs/heads/{branch}";
        var referenceResult = headReference is null
            ? new GitCommandResult(1, string.Empty, string.Empty)
            : await RunGitAsync(root, ["show-ref", "--verify", "--quiet", headReference], null, cancellationToken).ConfigureAwait(false);
        var indexResult = await RunGitAsync(root, ["write-tree"], null, cancellationToken).ConfigureAwait(false);
        var stagedResult = await RunGitAsync(root, ["diff", "--cached", "--binary", "--no-ext-diff", "--no-textconv"], null, cancellationToken).ConfigureAwait(false);
        var unstagedResult = await RunGitAsync(root, ["diff", "--binary", "--no-ext-diff", "--no-textconv"], null, cancellationToken).ConfigureAwait(false);
        var untrackedResult = await RunGitAsync(root, ["ls-files", "--others", "--exclude-standard", "-z"], null, cancellationToken).ConfigureAwait(false);
        var deletedResult = head is null
            ? await RunGitAsync(root, ["diff", "--name-only", "--diff-filter=D", "-z"], null, cancellationToken).ConfigureAwait(false)
            : await RunGitAsync(root, ["diff", "--name-only", "--diff-filter=D", "HEAD", "-z"], null, cancellationToken).ConfigureAwait(false);
        var conflictsResult = await RunGitAsync(root, ["ls-files", "--unmerged", "-z"], null, cancellationToken).ConfigureAwait(false);
        var refsResult = await RunGitAsync(root, ["for-each-ref", "--format=%(refname)"], null, cancellationToken).ConfigureAwait(false);

        return new GitWorkspaceSnapshotState(
            head,
            branch,
            headReference,
            referenceResult.Succeeded,
            indexResult.Succeeded ? FirstLine(indexResult.Stdout) : null,
            worktreeHash,
            stagedResult.Succeeded ? stagedResult.Stdout : string.Empty,
            unstagedResult.Succeeded ? unstagedResult.Stdout : string.Empty,
            ParseNullSeparated(untrackedResult.Stdout),
            ParseNullSeparated(deletedResult.Stdout),
            ParseUnmergedPaths(conflictsResult.Stdout),
            refsResult.Succeeded ? Lines(refsResult.Stdout) : []);
    }

    private static List<string> CompareGitState(
        WorkspaceSnapshotDocument snapshot,
        WorkspaceSnapshotDocument current,
        string expectedWorkspaceHash)
    {
        var conflicts = new List<string>();
        if (!WorkspaceSnapshotProviderSupport.FixedEquals(expectedWorkspaceHash, current.WorkspaceHash)) conflicts.Add("workspace_hash");
        if (snapshot.Git is null || current.Git is null)
        {
            conflicts.Add("git_state_missing");
            return conflicts;
        }
        if (!string.Equals(snapshot.Git.Head, current.Git.Head, StringComparison.OrdinalIgnoreCase)) conflicts.Add("git.head");
        if (!string.Equals(snapshot.Git.Branch, current.Git.Branch, StringComparison.Ordinal)) conflicts.Add("git.branch");
        if (!string.Equals(snapshot.Git.IndexTreeHash, current.Git.IndexTreeHash, StringComparison.OrdinalIgnoreCase)) conflicts.Add("git.index");
        if (!string.Equals(snapshot.Git.WorktreeHash, current.Git.WorktreeHash, StringComparison.OrdinalIgnoreCase)) conflicts.Add("git.worktree");
        if (snapshot.Git.ConflictPaths.Count != current.Git.ConflictPaths.Count
            || !snapshot.Git.ConflictPaths.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(current.Git.ConflictPaths.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
            conflicts.Add("git.conflicts");
        return conflicts;
    }

    private static bool GitStateEquivalent(GitWorkspaceSnapshotState expected, GitWorkspaceSnapshotState actual) =>
        string.Equals(expected.Head, actual.Head, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.Branch, actual.Branch, StringComparison.Ordinal)
        && string.Equals(expected.IndexTreeHash, actual.IndexTreeHash, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.WorktreeHash, actual.WorktreeHash, StringComparison.OrdinalIgnoreCase)
        && expected.ConflictPaths.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(actual.ConflictPaths.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static IReadOnlyList<string> Lines(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> ParseNullSeparated(string value) => value
        .Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => x.Length != 0)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<string> ParseUnmergedPaths(string value)
    {
        // ls-files --unmerged -z emits mode, object and stage fields followed by
        // the path. Grouping by path avoids reporting all three index stages.
        return ParseNullSeparated(value)
            .Select(x => x[(x.LastIndexOf('\t') + 1)..])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static void EnsureGitSuccess(GitCommandResult result, string message)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"{message} {SafeOutput(result.Stderr)}");
    }

    private static string SafeOutput(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, 1024)];

    private static async Task<GitCommandResult> RunGitAsync(
        string workspaceRoot,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path.GetFullPath(workspaceRoot),
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new GitCommandResult(-1, string.Empty, "git could not be started");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new GitCommandResult(-1, string.Empty, ex.Message);
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new GitCommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private sealed record GitCommandResult(int ExitCode, string Stdout, string Stderr)
    {
        public bool Succeeded => ExitCode == 0;
    }
}
