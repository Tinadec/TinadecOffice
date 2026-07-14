using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

public sealed class GitIntegrationArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("operation")] public string? Operation { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("strategy")] public string? Strategy { get; set; }
}

public sealed class GitIntegrationResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("operation")] public string Operation { get; set; } = string.Empty;
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("strategy")] public string? Strategy { get; set; }
    [JsonPropertyName("conflict")] public bool Conflict { get; set; }
    [JsonPropertyName("conflicted_files")] public List<string> ConflictedFiles { get; set; } = new();
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("status")] public GitStatusResult? Status { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitIntegrationArgs))]
[JsonSerializable(typeof(GitIntegrationResult))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitStatusEntry))]
internal partial class GitIntegrationToolsJsonContext : JsonSerializerContext;

internal static class GitIntegrationTools
{
    [ToolFunction("git_merge", RequiresApproval = true)]
    public static ValueTask<GitIntegrationResult> MergeAsync(GitIntegrationArgs args, CancellationToken ct) => ExecuteAsync(args, "merge", ct);

    [ToolFunction("git_rebase", RequiresApproval = true)]
    public static ValueTask<GitIntegrationResult> RebaseAsync(GitIntegrationArgs args, CancellationToken ct) => ExecuteAsync(args, "rebase", ct);

    private static async ValueTask<GitIntegrationResult> ExecuteAsync(GitIntegrationArgs args, string action, CancellationToken ct)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Failure(action, args.Operation ?? "start", error);
        var operation = string.IsNullOrWhiteSpace(args.Operation) ? "start" : args.Operation.Trim().ToLowerInvariant();
        var allowed = action == "merge" ? new[] { "start", "continue", "abort" } : new[] { "start", "continue", "abort", "skip" };
        if (!allowed.Contains(operation, StringComparer.Ordinal)) throw new InvalidOperationException($"Unsupported {action} operation '{operation}'.");
        var command = new List<string> { "-c", "core.editor=true", "-c", "sequence.editor=true", action };
        string? branch = null;
        if (operation == "start")
        {
            branch = args.Branch?.Trim();
            if (string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("branch is required for start.");
            GitCli.ValidateRevision(branch, "branch");
            if (action == "merge")
            {
                var strategy = args.Strategy?.Trim().ToLowerInvariant();
                if (strategy is not (null or "no_ff" or "ff_only" or "squash")) throw new InvalidOperationException("strategy must be no_ff, ff_only, or squash.");
                if (strategy == "no_ff") command.Add("--no-ff");
                else if (strategy == "ff_only") command.Add("--ff-only");
                else if (strategy == "squash") command.Add("--squash");
                command.Add("--no-edit");
            }
            command.Add(branch);
        }
        else command.Add($"--{operation}");

        var execution = await GitCli.RunAsync(repo, command, cancellationToken: ct, timeoutMs: 60_000).ConfigureAwait(false);
        var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = args.RepositoryPath }, ct).ConfigureAwait(false);
        var conflicted = status.Files.Where(file => file.IsConflicted).Select(file => file.Path).ToList();
        if (!execution.Ok) return new GitIntegrationResult { Success = false, Error = string.IsNullOrWhiteSpace(execution.Stderr) ? $"Git {action} failed." : execution.Stderr.Trim(), Action = action, Operation = operation, Branch = branch, Strategy = args.Strategy, Conflict = conflicted.Count > 0, ConflictedFiles = conflicted, Output = JoinOutput(execution), Status = status };
        return new GitIntegrationResult { Success = true, Action = action, Operation = operation, Branch = branch, Strategy = args.Strategy, Conflict = false, Output = JoinOutput(execution), Status = status };
    }

    private static string JoinOutput(GitExecResult result) => string.Join('\n', new[] { result.Stdout.Trim(), result.Stderr.Trim() }.Where(value => value.Length > 0));
    private static GitIntegrationResult Failure(string action, string operation, string error) => new() { Success = false, Action = action, Operation = operation, Error = string.IsNullOrWhiteSpace(error) ? $"Git {action} failed." : error.Trim() };
}
