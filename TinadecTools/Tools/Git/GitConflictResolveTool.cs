using System.Text;
using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

public sealed class GitConflictResolveArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("strategy")] public string Strategy { get; set; } = "auto";
}

public sealed class GitConflictResolveResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("strategy")] public string? Strategy { get; set; }
    [JsonPropertyName("auto_merged")] public bool AutoMerged { get; set; }
    [JsonPropertyName("conflict_count")] public int ConflictCount { get; set; }
    [JsonPropertyName("remaining_conflicts")] public List<string> RemainingConflicts { get; set; } = new();
    [JsonPropertyName("status")] public GitStatusResult? Status { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitConflictResolveArgs))]
[JsonSerializable(typeof(GitConflictResolveResult))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitStatusEntry))]
internal partial class GitConflictResolveToolJsonContext : JsonSerializerContext;

internal static class GitConflictResolveTool
{
    [ToolFunction("git_conflict_resolve", RequiresApproval = true)]
    public static async ValueTask<GitConflictResolveResult> ResolveAsync(GitConflictResolveArgs args, CancellationToken ct)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Failure(error);
        var path = GitCli.ResolveRepositoryRelativePath(repo, args.Path);
        var strategy = args.Strategy.Trim().ToLowerInvariant();
        if (strategy is not ("auto" or "ours" or "theirs" or "both")) throw new InvalidOperationException("strategy must be auto, ours, theirs, or both.");
        var baseStage = await ReadStageAsync(repo, 1, path, ct).ConfigureAwait(false);
        var oursStage = await ReadStageAsync(repo, 2, path, ct).ConfigureAwait(false);
        var theirsStage = await ReadStageAsync(repo, 3, path, ct).ConfigureAwait(false);
        if (!baseStage.Exists && !oursStage.Exists && !theirsStage.Exists) return Failure($"Path '{path}' is not an unresolved Git conflict.", path, strategy);
        var binary = new[] { baseStage.Text, oursStage.Text, theirsStage.Text }.Any(text => text?.IndexOf('\0') >= 0);
        string? resolvedText;
        var conflictCount = 0;
        var autoMerged = false;
        if (strategy == "ours") resolvedText = oursStage.Exists ? oursStage.Text : null;
        else if (strategy == "theirs") resolvedText = theirsStage.Exists ? theirsStage.Text : null;
        else
        {
            if (binary) return Failure("auto and both strategies do not support binary conflicts; choose ours or theirs.", path, strategy);
            var resolution = strategy == "both" ? TextConflictResolution.Both : TextConflictResolution.Markers;
            var merge = ThreeWayTextMerge.Merge(baseStage.Text ?? string.Empty, oursStage.Text ?? string.Empty, theirsStage.Text ?? string.Empty, resolution);
            conflictCount = merge.Conflicts.Count;
            if (strategy == "auto" && !merge.IsClean) return Failure($"Automatic merge left {conflictCount} overlapping conflict(s).", path, strategy, conflictCount);
            resolvedText = merge.Text;
            autoMerged = merge.IsClean;
        }

        var fullPath = GitCli.ResolveRepositoryRelativePath(repo, path);
        var absolutePath = System.IO.Path.GetFullPath(fullPath, repo);
        if (resolvedText is null)
        {
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
        }
        else await File.WriteAllTextAsync(absolutePath, resolvedText, new UTF8Encoding(false), ct).ConfigureAwait(false);
        var add = await GitCli.RunAsync(repo, ["add", "-A", "--", path], cancellationToken: ct).ConfigureAwait(false);
        if (!add.Ok) return Failure(add.Stderr, path, strategy, conflictCount);
        var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = args.RepositoryPath }, ct).ConfigureAwait(false);
        return new GitConflictResolveResult { Success = true, Path = path, Strategy = strategy, AutoMerged = autoMerged, ConflictCount = conflictCount, RemainingConflicts = status.Files.Where(file => file.IsConflicted).Select(file => file.Path).ToList(), Status = status };
    }

    private static async Task<(bool Exists, string? Text)> ReadStageAsync(string repo, int stage, string path, CancellationToken ct)
    {
        var result = await GitCli.RunAsync(repo, ["show", $":{stage}:{path}"], cancellationToken: ct, maxOutputChars: 2_000_000).ConfigureAwait(false);
        return (result.Ok, result.Ok ? result.Stdout : null);
    }

    private static GitConflictResolveResult Failure(string error, string? path = null, string? strategy = null, int conflictCount = 0) => new() { Success = false, Error = string.IsNullOrWhiteSpace(error) ? "Git conflict resolution failed." : error.Trim(), Path = path, Strategy = strategy, ConflictCount = conflictCount };
}
