using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

public sealed class GitDiscardArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }

    /// <summary>Explicit file paths to discard. Must be non-empty; directories are never removed.</summary>
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }

    /// <summary>When true, untracked files in <c>paths</c> are deleted via <c>git clean</c>. Irreversible.</summary>
    [JsonPropertyName("include_untracked")] public bool IncludeUntracked { get; set; }

    [JsonPropertyName("confirm_discard")] public string? ConfirmDiscard { get; set; }
}

public sealed class GitDiscardResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }

    /// <summary>Tracked paths restored to HEAD (index + worktree).</summary>
    [JsonPropertyName("discarded_paths")] public List<string> DiscardedPaths { get; set; } = new();

    /// <summary>Untracked paths deleted via <c>git clean</c>.</summary>
    [JsonPropertyName("removed_untracked")] public List<string> RemovedUntracked { get; set; } = new();

    /// <summary>Requested paths left untouched (untracked without opt-in, staged-new without opt-in, directories).</summary>
    [JsonPropertyName("skipped_paths")] public List<string> SkippedPaths { get; set; } = new();

    [JsonPropertyName("status")] public GitStatusResult? Status { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitDiscardArgs))]
[JsonSerializable(typeof(GitDiscardResult))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitStatusEntry))]
internal partial class GitDiscardToolsJsonContext : JsonSerializerContext { }

internal static class GitDiscardTools
{
    private const int MaxPaths = 100;

    [ToolFunction("git_discard", RequiresApproval = true, ConfirmationFields = ["confirm_discard"])]
    public static async ValueTask<GitDiscardResult> DiscardAsync(GitDiscardArgs args, CancellationToken cancellationToken)
    {
        ToolConfirmations.Require(args.ConfirmDiscard, nameof(args.ConfirmDiscard));
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError);
        if (repo is null) return Failure(repoError, GitCli.NotARepoCode);

        var rawPaths = args.Paths?.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (rawPaths.Count == 0)
            throw new InvalidOperationException("paths is required and must not be empty.");
        if (rawPaths.Count > MaxPaths)
            throw new InvalidOperationException($"paths must not exceed {MaxPaths} entries.");

        var resolved = rawPaths.Select(path => GitCli.ResolveRepositoryRelativePath(repo, path)).Distinct(StringComparer.Ordinal).ToList();

        // Never touch conflicted paths here: they belong to the conflict-resolution flow.
        var unmerged = await GitCli.RunAsync(repo, ["ls-files", "-u", "-z", "--", .. resolved], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!unmerged.Ok) return Failure(unmerged.Stderr, unmerged.ExitCode);
        var conflicted = SplitNullSeparated(unmerged.Stdout).Select(ParseUnmergedPath).Where(path => path is not null).Distinct(StringComparer.Ordinal).ToList()!;
        if (conflicted.Count > 0)
            return Failure($"Refusing to discard conflicted path(s): {string.Join(", ", conflicted)}. Resolve conflicts first.", null, conflicted);

        // Classify: tracked (in index) vs untracked.
        var trackedOut = await GitCli.RunAsync(repo, ["ls-files", "-z", "--", .. resolved], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!trackedOut.Ok) return Failure(trackedOut.Stderr, trackedOut.ExitCode);
        var tracked = new HashSet<string>(SplitNullSeparated(trackedOut.Stdout), StringComparer.Ordinal);
        var untrackedRequested = resolved.Where(path => !tracked.Contains(path)).ToList();

        var discarded = new List<string>();
        var removed = new List<string>();
        var skipped = new List<string>();

        if (tracked.Count > 0)
        {
            // 1) Unstage everything first; safe for all tracked entries including staged-new files.
            var unstage = await GitCli.RunAsync(repo, ["restore", "--staged", "--", .. tracked], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!unstage.Ok) return Failure(unstage.Stderr, unstage.ExitCode);

            // 2) Restore worktree content for paths present in HEAD (no-op set on unborn HEAD).
            var inHead = await ListHeadPathsAsync(repo, tracked, cancellationToken).ConfigureAwait(false);
            if (inHead.Count > 0)
            {
                var restore = await GitCli.RunAsync(repo, ["restore", "--source=HEAD", "--worktree", "--", .. inHead], cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!restore.Ok) return Failure(restore.Stderr, restore.ExitCode);
                discarded.AddRange(inHead);
            }

            // 3) Staged-new files are untracked again after unstage: delete only with opt-in.
            foreach (var path in tracked.Where(path => !inHead.Contains(path)))
            {
                if (args.IncludeUntracked) removed.Add(path);
                else skipped.Add(path);
            }
        }

        foreach (var path in untrackedRequested)
        {
            if (args.IncludeUntracked) removed.Add(path);
            else skipped.Add(path);
        }

        // Directories are never removed: git clean without -d refuses them, so pre-filter for a clear report.
        var cleanTargets = new List<string>();
        foreach (var path in removed.Distinct(StringComparer.Ordinal).ToList())
        {
            var full = Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(full) && !File.Exists(full))
            {
                skipped.Add(path);
            }
            else
            {
                cleanTargets.Add(path);
            }
        }
        removed = cleanTargets;

        if (cleanTargets.Count > 0)
        {
            var clean = await GitCli.RunAsync(repo, ["clean", "-f", "--", .. cleanTargets], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!clean.Ok) return Failure(clean.Stderr, clean.ExitCode);
        }

        var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = args.RepositoryPath }, cancellationToken);
        return new GitDiscardResult
        {
            Success = true,
            DiscardedPaths = discarded.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            RemovedUntracked = removed.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            SkippedPaths = skipped.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToList(),
            Status = status
        };
    }

    private static async Task<HashSet<string>> ListHeadPathsAsync(string repo, HashSet<string> candidates, CancellationToken ct)
    {
        // Unborn HEAD (no commits yet): ls-tree fails; treat as empty set.
        var listed = await GitCli.RunAsync(repo, ["ls-tree", "-r", "--name-only", "-z", "HEAD", "--", .. candidates], cancellationToken: ct).ConfigureAwait(false);
        if (!listed.Ok) return [];
        return new HashSet<string>(SplitNullSeparated(listed.Stdout), StringComparer.Ordinal);
    }

    private static List<string> SplitNullSeparated(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToList();

    private static string? ParseUnmergedPath(string entry)
    {
        // `git ls-files -u` lines look like "<mode> <hash> <stage>\t<path>".
        var tab = entry.LastIndexOf('\t');
        var path = (tab >= 0 ? entry[(tab + 1)..] : entry).Trim();
        return path.Length > 0 ? path : null;
    }

    private static GitDiscardResult Failure(string error, string? errorCode) => new()
    {
        Success = false,
        Error = string.IsNullOrWhiteSpace(error) ? "Git discard failed." : error.Trim(),
        ErrorCode = errorCode
    };

    private static GitDiscardResult Failure(string error, int exitCode) =>
        Failure(error, exitCode < 0 ? GitCli.GitNotFoundCode : null);

    private static GitDiscardResult Failure(string error, string? errorCode, List<string> conflicted) => new()
    {
        Success = false,
        Error = string.IsNullOrWhiteSpace(error) ? "Git discard failed." : error.Trim(),
        ErrorCode = errorCode ?? "has_conflicts",
        SkippedPaths = conflicted
    };
}
