using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

// ── git_stash_* ───────────────────────────────────────────────────────────────

public sealed class GitStashListArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; } = 50;
}

public sealed class GitStashEntry
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("ref_name")] public string RefName { get; set; } = string.Empty;
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
    [JsonPropertyName("subject")] public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
}

public sealed class GitStashListResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("entries")] public List<GitStashEntry> Entries { get; set; } = new();
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

public sealed class GitStashShowArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("stash_ref")] public string? StashRef { get; set; } = "stash@{0}";
    [JsonPropertyName("include_patch")] public bool IncludePatch { get; set; } = true;
    [JsonPropertyName("max_files")] public int? MaxFiles { get; set; } = 200;
    [JsonPropertyName("max_patch_bytes")] public int? MaxPatchBytes { get; set; } = 262144;
}

public sealed class GitStashShowResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("stash_ref")] public string StashRef { get; set; } = "stash@{0}";
    [JsonPropertyName("files")] public List<GitFileChange> Files { get; set; } = new();
    [JsonPropertyName("patches")] public List<GitPatchFile>? Patches { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("truncation_reason")] public string? TruncationReason { get; set; }
}

public sealed class GitStashPushArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("include_untracked")] public bool IncludeUntracked { get; set; }
    [JsonPropertyName("keep_index")] public bool KeepIndex { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
    [Description("Required: a short non-empty note restating what this stash push will save.")]
    [JsonPropertyName("confirm_stash_push")] public string? ConfirmStashPush { get; set; }
}

public sealed class GitStashMutationArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("stash_ref")] public string? StashRef { get; set; }
    [JsonPropertyName("reinstate_index")] public bool ReinstateIndex { get; set; }
    [Description("Required for git_stash_apply: short confirmation note.")]
    [JsonPropertyName("confirm_stash_apply")] public string? ConfirmStashApply { get; set; }
    [Description("Required for git_stash_pop: short confirmation note.")]
    [JsonPropertyName("confirm_stash_pop")] public string? ConfirmStashPop { get; set; }
    [Description("Required for git_stash_drop: short confirmation note naming the stash to drop.")]
    [JsonPropertyName("confirm_stash_drop")] public string? ConfirmStashDrop { get; set; }
}

public sealed class GitStashMutationResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("stash_ref")] public string? StashRef { get; set; }
    [JsonPropertyName("created")] public bool Created { get; set; }
    [JsonPropertyName("has_conflicts")] public bool HasConflicts { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("status")] public GitStatusResult? Status { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitStashListArgs))]
[JsonSerializable(typeof(GitStashListResult))]
[JsonSerializable(typeof(GitStashEntry))]
[JsonSerializable(typeof(GitStashShowArgs))]
[JsonSerializable(typeof(GitStashShowResult))]
[JsonSerializable(typeof(GitStashPushArgs))]
[JsonSerializable(typeof(GitStashMutationArgs))]
[JsonSerializable(typeof(GitStashMutationResult))]
[JsonSerializable(typeof(GitFileChange))]
[JsonSerializable(typeof(GitPatchFile))]
[JsonSerializable(typeof(GitDiffHunk))]
[JsonSerializable(typeof(GitDiffLine))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitStatusEntry))]
internal partial class GitStashToolsJsonContext : JsonSerializerContext { }

internal static partial class GitStashTools
{
    [GeneratedRegex(@"^stash@\{(\d+)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex StashRefPattern();

    [GeneratedRegex(@"^(?:WIP on|On)\s+([^:]+):\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex StashSubjectPattern();

    [ToolFunction("git_stash_list", Description = "List git stash entries (newest first) with index, ref_name, hash, branch, message, author and timestamp. repository_path must be an absolute path inside the workspace and point at a git worktree.")]
    public static async ValueTask<GitStashListResult> ListAsync(GitStashListArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError);
        if (repo is null)
            return new GitStashListResult { Success = false, Error = repoError, ErrorCode = GitCli.NotARepoCode };

        var limit = Math.Clamp(args.Limit ?? 50, 1, 200);
        var exec = await GitCli.RunAsync(
            repo,
            ["stash", "list", $"--max-count={limit + 1}", "--format=%gd%x1f%H%x1f%gs%x1f%cI%x1f%cn"],
            cancellationToken: cancellationToken,
            timeoutMs: 15_000).ConfigureAwait(false);
        if (!exec.Ok)
            return new GitStashListResult { Success = false, Error = exec.Stderr.Trim(), ErrorCode = exec.ExitCode.ToString() };

        var entries = new List<GitStashEntry>();
        foreach (var rawLine in exec.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            var parts = line.Split('\x1f');
            if (parts.Length < 3) continue;
            var refName = parts[0].Trim();
            var idxMatch = StashRefPattern().Match(refName);
            var index = idxMatch.Success && int.TryParse(idxMatch.Groups[1].Value, out var parsedIdx) ? parsedIdx : entries.Count;
            var subject = parts[2];
            var subMatch = StashSubjectPattern().Match(subject);
            var branch = subMatch.Success ? subMatch.Groups[1].Value.Trim() : null;
            var message = subMatch.Success ? subMatch.Groups[2].Value.Trim() : subject.Trim();

            entries.Add(new GitStashEntry
            {
                Index = index,
                RefName = refName,
                Hash = parts[1].Trim(),
                Subject = subject,
                Branch = branch,
                Message = message,
                CreatedAt = parts.Length > 3 ? parts[3].Trim() : string.Empty,
                Author = parts.Length > 4 ? parts[4].Trim() : string.Empty
            });
        }

        var truncated = entries.Count > limit;
        if (truncated) entries = entries.Take(limit).ToList();

        return new GitStashListResult
        {
            Success = true,
            Entries = entries,
            Truncated = truncated
        };
    }

    [ToolFunction("git_stash_show", Description = "Show changed files and optional structured diff hunks for a stash entry (defaults to stash@{0}). repository_path must be an absolute path inside the workspace and point at a git worktree.")]
    public static async ValueTask<GitStashShowResult> ShowAsync(GitStashShowArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError);
        if (repo is null)
            return new GitStashShowResult { Success = false, Error = repoError, ErrorCode = GitCli.NotARepoCode };

        var stashRef = NormalizeStashRef(args.StashRef, defaultToLatest: true);
        var maxFiles = args.MaxFiles is > 0 ? args.MaxFiles.Value : 200;
        var maxPatchBytes = args.MaxPatchBytes is > 0 ? args.MaxPatchBytes.Value : 262144;

        var ns = await GitCli.RunAsync(repo, ["stash", "show", "--include-untracked", "--name-status", "-M", stashRef], cancellationToken: cancellationToken, timeoutMs: 15_000).ConfigureAwait(false);
        if (!ns.Ok)
            return new GitStashShowResult { Success = false, Error = ns.Stderr.Trim(), ErrorCode = ns.ExitCode.ToString(), StashRef = stashRef };

        var stat = await GitCli.RunAsync(repo, ["stash", "show", "--include-untracked", "--numstat", "-M", stashRef], cancellationToken: cancellationToken, timeoutMs: 15_000).ConfigureAwait(false);
        if (!stat.Ok)
            return new GitStashShowResult { Success = false, Error = stat.Stderr.Trim(), ErrorCode = stat.ExitCode.ToString(), StashRef = stashRef };

        var files = DiffParser.MergeFileChanges(DiffParser.ParseNameStatus(ns.Stdout), DiffParser.ParseNumstat(stat.Stdout));
        var truncated = false;
        string? truncationReason = null;
        if (files.Count > maxFiles)
        {
            files = files.Take(maxFiles).ToList();
            truncated = true;
            truncationReason = "max_files";
        }

        List<GitPatchFile>? patches = null;
        if (args.IncludePatch && !truncated)
        {
            var patch = await GitPatchLoader.LoadAsync(repo, ["stash", "show", "--include-untracked", "-p", "-M", stashRef], maxPatchBytes, cancellationToken).ConfigureAwait(false);
            if (patch.Patches is not null)
            {
                patches = patch.Patches;
            }
            else if (patch.Truncated)
            {
                truncated = true;
                truncationReason ??= patch.TruncationReason;
            }
        }

        return new GitStashShowResult
        {
            Success = true,
            StashRef = stashRef,
            Files = files,
            Patches = patches,
            Truncated = truncated,
            TruncationReason = truncationReason
        };
    }

    [ToolFunction("git_stash_push", RequiresApproval = true, ConfirmationFields = ["confirm_stash_push"], Description = "Save uncommitted working tree / index changes onto the git stash stack. Optionally include untracked files (include_untracked=true), keep_index=true, message, or explicit paths=[...]. repository_path must be an absolute path inside the workspace and point at a git worktree.")]
    public static async ValueTask<GitStashMutationResult> PushAsync(GitStashPushArgs args, CancellationToken cancellationToken)
    {
        ToolConfirmations.Require(args.ConfirmStashPush, nameof(args.ConfirmStashPush));
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError, writable: true);
        if (repo is null)
            return MutationFailure("push", repoError, GitCli.NotARepoCode);

        var gitArgs = new List<string> { "stash", "push" };
        if (args.IncludeUntracked) gitArgs.Add("--include-untracked");
        if (args.KeepIndex) gitArgs.Add("--keep-index");
        if (!string.IsNullOrWhiteSpace(args.Message))
        {
            var msg = args.Message.Trim();
            if (msg.IndexOf('\0') >= 0) throw new InvalidOperationException("message must not contain a null character.");
            gitArgs.Add("-m");
            gitArgs.Add(msg);
        }
        var rawPaths = args.Paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        if (rawPaths.Count > 0)
        {
            gitArgs.Add("--");
            gitArgs.AddRange(rawPaths.Select(p => GitCli.ResolveRepositoryRelativePath(repo, p)).Distinct(StringComparer.Ordinal));
        }

        var exec = await GitCli.RunAsync(repo, gitArgs, cancellationToken: cancellationToken, timeoutMs: 30_000).ConfigureAwait(false);
        if (!exec.Ok)
            return MutationFailure("push", exec.Stderr, exec.ExitCode.ToString());

        var output = exec.Stdout.Trim();
        var noChanges = output.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase);
        var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = args.RepositoryPath }, cancellationToken).ConfigureAwait(false);
        return new GitStashMutationResult
        {
            Success = true,
            Action = "push",
            StashRef = noChanges ? null : "stash@{0}",
            Created = !noChanges,
            Output = output,
            Status = status
        };
    }

    [ToolFunction("git_stash_apply", RequiresApproval = true, ConfirmationFields = ["confirm_stash_apply"], Description = "Apply a stash entry (defaults to stash@{0}) without removing it from the stash stack. repository_path must be an absolute path inside the workspace and point at a git worktree.")]
    public static ValueTask<GitStashMutationResult> ApplyAsync(GitStashMutationArgs args, CancellationToken cancellationToken) =>
        ApplyOrPopAsync("apply", args, cancellationToken);

    [ToolFunction("git_stash_pop", RequiresApproval = true, ConfirmationFields = ["confirm_stash_pop"], Description = "Apply a stash entry (defaults to stash@{0}) and drop it on clean apply. repository_path must be an absolute path inside the workspace and point at a git worktree.")]
    public static ValueTask<GitStashMutationResult> PopAsync(GitStashMutationArgs args, CancellationToken cancellationToken) =>
        ApplyOrPopAsync("pop", args, cancellationToken);

    [ToolFunction("git_stash_drop", RequiresApproval = true, ConfirmationFields = ["confirm_stash_drop"], Description = "Permanently drop an explicit stash entry (stash_ref required, e.g. stash@{0}). Confirm with confirm_stash_drop. repository_path must be an absolute path inside the workspace and point at a git worktree.")]
    public static async ValueTask<GitStashMutationResult> DropAsync(GitStashMutationArgs args, CancellationToken cancellationToken)
    {
        ToolConfirmations.Require(args.ConfirmStashDrop, nameof(args.ConfirmStashDrop));
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError, writable: true);
        if (repo is null)
            return MutationFailure("drop", repoError, GitCli.NotARepoCode);

        var stashRef = NormalizeStashRef(args.StashRef, defaultToLatest: false);
        var exec = await GitCli.RunAsync(repo, ["stash", "drop", stashRef], cancellationToken: cancellationToken, timeoutMs: 15_000).ConfigureAwait(false);
        if (!exec.Ok)
            return MutationFailure("drop", exec.Stderr, exec.ExitCode.ToString(), stashRef);

        var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = args.RepositoryPath }, cancellationToken).ConfigureAwait(false);
        return new GitStashMutationResult
        {
            Success = true,
            Action = "drop",
            StashRef = stashRef,
            Output = exec.Stdout.Trim(),
            Status = status
        };
    }

    private static async ValueTask<GitStashMutationResult> ApplyOrPopAsync(string action, GitStashMutationArgs args, CancellationToken cancellationToken)
    {
        ToolConfirmations.Require(
            action == "pop" ? args.ConfirmStashPop : args.ConfirmStashApply,
            action == "pop" ? nameof(args.ConfirmStashPop) : nameof(args.ConfirmStashApply));

        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError, writable: true);
        if (repo is null)
            return MutationFailure(action, repoError, GitCli.NotARepoCode);

        var stashRef = NormalizeStashRef(args.StashRef, defaultToLatest: true);
        var gitArgs = new List<string> { "stash", action };
        if (args.ReinstateIndex) gitArgs.Add("--index");
        gitArgs.Add(stashRef);

        var exec = await GitCli.RunAsync(repo, gitArgs, cancellationToken: cancellationToken, timeoutMs: 30_000).ConfigureAwait(false);
        var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = args.RepositoryPath }, cancellationToken).ConfigureAwait(false);
        var hasConflicts = status.Files.Any(f => f.IsConflicted)
            || exec.Stderr.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase);

        if (!exec.Ok)
        {
            return new GitStashMutationResult
            {
                Success = false,
                Action = action,
                StashRef = stashRef,
                HasConflicts = hasConflicts,
                Error = string.IsNullOrWhiteSpace(exec.Stderr) ? exec.Stdout.Trim() : exec.Stderr.Trim(),
                ErrorCode = hasConflicts ? "stash_conflict" : exec.ExitCode.ToString(),
                Output = exec.Stdout.Trim(),
                Status = status
            };
        }

        return new GitStashMutationResult
        {
            Success = true,
            Action = action,
            StashRef = stashRef,
            HasConflicts = hasConflicts,
            Output = exec.Stdout.Trim(),
            Status = status
        };
    }

    private static string NormalizeStashRef(string? raw, bool defaultToLatest)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            if (!defaultToLatest)
                throw new InvalidOperationException("stash_ref is required (for example, stash@{0}).");
            return "stash@{0}";
        }
        GitCli.ValidateRevision(trimmed, "stash_ref");
        if (!StashRefPattern().IsMatch(trimmed))
            throw new InvalidOperationException("stash_ref must match stash@{n} (for example, stash@{0}).");
        return trimmed;
    }

    private static GitStashMutationResult MutationFailure(string action, string error, string? errorCode, string? stashRef = null) => new()
    {
        Success = false,
        Action = action,
        StashRef = stashRef,
        Error = string.IsNullOrWhiteSpace(error) ? $"Git stash {action} failed." : error.Trim(),
        ErrorCode = errorCode
    };
}
