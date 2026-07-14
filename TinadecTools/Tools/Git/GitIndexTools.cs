using System.Text;
using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

public sealed class GitIndexUpdateArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
    [JsonPropertyName("patch")] public string? Patch { get; set; }
    [JsonPropertyName("max_patch_bytes")] public int? MaxPatchBytes { get; set; } = 524288;
}

public sealed class GitIndexUpdateResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("updated_paths")] public List<string> UpdatedPaths { get; set; } = new();
    [JsonPropertyName("selection_kind")] public string SelectionKind { get; set; } = string.Empty;
    [JsonPropertyName("status")] public GitStatusResult? Status { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitIndexUpdateArgs))]
[JsonSerializable(typeof(GitIndexUpdateResult))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitStatusEntry))]
internal partial class GitIndexToolsJsonContext : JsonSerializerContext;

internal static class GitIndexTools
{
    private const int DefaultMaxPatchBytes = 524288;

    [ToolFunction("git_stage", RequiresApproval = true)]
    public static ValueTask<GitIndexUpdateResult> StageAsync(GitIndexUpdateArgs args, CancellationToken cancellationToken) =>
        UpdateAsync(args, "stage", cancellationToken);

    [ToolFunction("git_unstage", RequiresApproval = true)]
    public static ValueTask<GitIndexUpdateResult> UnstageAsync(GitIndexUpdateArgs args, CancellationToken cancellationToken) =>
        UpdateAsync(args, "unstage", cancellationToken);

    private static async ValueTask<GitIndexUpdateResult> UpdateAsync(
        GitIndexUpdateArgs args,
        string action,
        CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError);
        if (repo is null) return Failure(repoError, GitCli.NotARepoCode);

        var paths = args.Paths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList() ?? [];
        var hasPatch = !string.IsNullOrWhiteSpace(args.Patch);
        if (paths.Count == 0 && !hasPatch)
            throw new InvalidOperationException("paths or patch is required.");
        if (paths.Count > 0 && hasPatch)
            throw new InvalidOperationException("Provide paths or patch, not both.");

        if (paths.Count > 0)
        {
            var resolved = paths.Select(path => GitCli.ResolveRepositoryRelativePath(repo, path)).Distinct(StringComparer.Ordinal).ToList();
            var command = action == "stage"
                ? new List<string> { "add", "--" }
                : new List<string> { "restore", "--staged", "--" };
            command.AddRange(resolved);
            var result = await GitCli.RunAsync(repo, command, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Ok) return Failure(result.Stderr, result.ExitCode);
            return await SuccessAsync(args.RepositoryPath, resolved, "paths", cancellationToken).ConfigureAwait(false);
        }

        var patch = args.Patch!;
        var maxBytes = Math.Clamp(args.MaxPatchBytes ?? DefaultMaxPatchBytes, 1024, 2_000_000);
        if (Encoding.UTF8.GetByteCount(patch) > maxBytes)
            return Failure($"patch exceeds the {maxBytes:N0}-byte limit.", "patch_output_limit");

        var patchPaths = ValidateTextPatch(repo, patch);
        var applyArgs = new List<string> { "apply", "--cached", "--check", "--whitespace=nowarn" };
        if (action == "unstage") applyArgs.Add("--reverse");
        applyArgs.Add("-");
        var check = await GitCli.RunAsync(repo, applyArgs, patch, cancellationToken, timeoutMs: 30_000).ConfigureAwait(false);
        if (!check.Ok) return Failure(check.Stderr, check.ExitCode);

        applyArgs.Remove("--check");
        var apply = await GitCli.RunAsync(repo, applyArgs, patch, cancellationToken, timeoutMs: 30_000).ConfigureAwait(false);
        if (!apply.Ok) return Failure(apply.Stderr, apply.ExitCode);
        return await SuccessAsync(args.RepositoryPath, patchPaths, "patch", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitIndexUpdateResult> SuccessAsync(
        string? repositoryPath,
        List<string> paths,
        string selectionKind,
        CancellationToken cancellationToken)
    {
        var status = await GitReadTools.StatusAsync(
            new GitStatusArgs { RepositoryPath = repositoryPath }, cancellationToken).ConfigureAwait(false);
        return new GitIndexUpdateResult
        {
            Success = true,
            UpdatedPaths = paths,
            SelectionKind = selectionKind,
            Status = status
        };
    }

    private static List<string> ValidateTextPatch(string repo, string patch)
    {
        if (patch.IndexOf('\0') >= 0)
            throw new InvalidOperationException("patch must be text.");
        if (patch.Contains("GIT binary patch", StringComparison.Ordinal) ||
            patch.Contains("Binary files ", StringComparison.Ordinal) ||
            patch.Contains("rename from ", StringComparison.Ordinal) ||
            patch.Contains("rename to ", StringComparison.Ordinal) ||
            patch.Contains("new file mode ", StringComparison.Ordinal) ||
            patch.Contains("deleted file mode ", StringComparison.Ordinal))
            throw new InvalidOperationException("patch selections support tracked text files only; use paths for binary, rename, add, or delete changes.");

        var paths = new List<string>();
        foreach (var line in patch.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith("diff --git ", StringComparison.Ordinal)) continue;
            var parts = line["diff --git ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !parts[0].StartsWith("a/", StringComparison.Ordinal) || !parts[1].StartsWith("b/", StringComparison.Ordinal))
                throw new InvalidOperationException("patch has an invalid diff header.");
            var oldPath = parts[0][2..];
            var newPath = parts[1][2..];
            if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
                throw new InvalidOperationException("patch selections must not rename files.");
            paths.Add(GitCli.ResolveRepositoryRelativePath(repo, oldPath));
        }

        if (paths.Count == 0 || !patch.Contains("@@ ", StringComparison.Ordinal))
            throw new InvalidOperationException("patch must contain one or more unified text hunks.");
        return paths.Distinct(StringComparer.Ordinal).ToList();
    }

    private static GitIndexUpdateResult Failure(string error, string? errorCode = null) => new()
    {
        Success = false,
        Error = string.IsNullOrWhiteSpace(error) ? "Git index update failed." : error.Trim(),
        ErrorCode = errorCode
    };

    private static GitIndexUpdateResult Failure(string error, int exitCode) =>
        Failure(error, exitCode < 0 ? GitCli.GitNotFoundCode : null);
}
