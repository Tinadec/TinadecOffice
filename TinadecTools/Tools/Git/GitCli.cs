using System.Diagnostics;
using TinadecTools.Runtime;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tools.Git;

// git CLI execution helper.
// Reuses TerminalRunner; minimal hard guard: repository_path must be a git worktree.
// All args go through ArgumentList (no string concat) to prevent shell injection.

internal sealed record GitExecResult(bool Ok, int ExitCode, string Stdout, string Stderr, bool Truncated = false);

internal static class GitCli
{
    public const string GitNotFoundCode = "git_not_found";
    public const string NotARepoCode = "not_a_repo";

    /// <summary>
    /// Validate repository_path is a git worktree; returns the top-level dir on success.
    /// Returns null and sets <paramref name="error"/> on failure.
    /// </summary>
    public static string? ResolveRepo(string repositoryPath, out string error)
    {
        error = string.Empty;
        string path;
        try
        {
            path = WorkspacePathResolver.ResolveDirectory(
                string.IsNullOrWhiteSpace(repositoryPath) ? "." : repositoryPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }

        var revParse = TerminalRunner.RunAsync(
            "git",
            ["rev-parse", "--show-toplevel"],
            path,
            stdin: null,
            timeoutMs: 10_000).GetAwaiter().GetResult();

        if (!revParse.Success || string.IsNullOrWhiteSpace(revParse.Stdout.Trim()))
        {
            error = $"not a git worktree: {path}";
            return null;
        }

        try
        {
            return WorkspacePathResolver.ResolveDirectory(revParse.Stdout.Trim());
        }
        catch (Exception ex)
        {
            error = $"repository root is outside the workspace: {ex.Message}";
            return null;
        }
    }

    /// <summary>Get blob hash + byte size for rev:path (binary file summary).</summary>
    public static async Task<(string? BlobHash, long? ByteSize)> GetBlobSummaryAsync(
        string repoTopLevel,
        string rev,
        string path,
        CancellationToken cancellationToken)
    {
        var blob = await RunAsync(
            repoTopLevel,
            ["rev-parse", $"{rev}:{path}"],
            stdin: null,
            cancellationToken,
            timeoutMs: 10_000).ConfigureAwait(false);
        if (!blob.Ok || string.IsNullOrWhiteSpace(blob.Stdout.Trim()))
            return (null, null);

        var blobHash = blob.Stdout.Trim();
        var size = await RunAsync(
            repoTopLevel,
            ["cat-file", "-s", blobHash],
            stdin: null,
            cancellationToken,
            timeoutMs: 10_000).ConfigureAwait(false);
        long.TryParse(size.Stdout?.Trim(), out var byteSize);
        return (blobHash, byteSize);
    }

    /// <summary>Run a git command with cwd=repoTopLevel. Returns Ok=false if git missing or command fails.</summary>
    public static async Task<GitExecResult> RunAsync(
        string repoTopLevel,
        IReadOnlyList<string> arguments,
        string? stdin = null,
        CancellationToken cancellationToken = default,
        int timeoutMs = 60_000)
    {
        try
        {
            var r = await TerminalRunner.RunAsync(
                "git",
                arguments,
                repoTopLevel,
                stdin,
                timeoutMs,
                cancellationToken,
                maxOutputChars: 4 * 1024 * 1024).ConfigureAwait(false);

            // git not found: TerminalRunner catches and returns Success=false with ex.Message in Stderr.
            if (!r.Success && r.ExitCode < 0 && r.Stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
                return new GitExecResult(false, r.ExitCode, r.Stdout, GitNotFoundCode);

            if (r.StdoutTruncated || r.StderrTruncated)
                return new GitExecResult(false, r.ExitCode, r.Stdout,
                    "git output exceeded the 4 MiB capture limit.", Truncated: true);

            return new GitExecResult(r.Success, r.ExitCode, r.Stdout, r.Stderr);
        }
        catch (Exception ex)
        {
            return new GitExecResult(false, -1, string.Empty, ex.Message);
        }
    }
}
