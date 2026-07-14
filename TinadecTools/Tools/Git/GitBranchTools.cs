using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

public sealed class GitBranchMutationArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("new_name")] public string? NewName { get; set; }
    [JsonPropertyName("force")] public bool Force { get; set; }
}

public sealed class GitBranchMutationResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("new_name")] public string? NewName { get; set; }
    [JsonPropertyName("force")] public bool Force { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("status")] public GitStatusResult? Status { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitBranchMutationArgs))]
[JsonSerializable(typeof(GitBranchMutationResult))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitStatusEntry))]
internal partial class GitBranchToolsJsonContext : JsonSerializerContext;

internal static class GitBranchTools
{
    [ToolFunction("git_checkout", RequiresApproval = true)]
    public static ValueTask<GitBranchMutationResult> CheckoutAsync(GitBranchMutationArgs args, CancellationToken ct) =>
        ExecuteAsync(args, "checkout", ct);

    [ToolFunction("git_branch_create", RequiresApproval = true)]
    public static ValueTask<GitBranchMutationResult> CreateAsync(GitBranchMutationArgs args, CancellationToken ct) =>
        ExecuteAsync(args, "create", ct);

    [ToolFunction("git_branch_delete", RequiresApproval = true)]
    public static ValueTask<GitBranchMutationResult> DeleteAsync(GitBranchMutationArgs args, CancellationToken ct) =>
        ExecuteAsync(args, "delete", ct);

    [ToolFunction("git_branch_rename", RequiresApproval = true)]
    public static ValueTask<GitBranchMutationResult> RenameAsync(GitBranchMutationArgs args, CancellationToken ct) =>
        ExecuteAsync(args, "rename", ct);

    private static async ValueTask<GitBranchMutationResult> ExecuteAsync(GitBranchMutationArgs args, string action, CancellationToken ct)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Failure(action, error);
        var name = action == "rename" ? args.NewName?.Trim() : args.Branch?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException(action == "rename" ? "new_name is required." : "branch is required.");
        var valid = await GitCli.RunAsync(repo, ["check-ref-format", "--branch", name], cancellationToken: ct).ConfigureAwait(false);
        if (!valid.Ok) return Failure(action, $"Invalid branch name '{name}'.");

        IReadOnlyList<string> command = action switch
        {
            "checkout" => ["checkout", name],
            "create" => ["checkout", "-b", name],
            "delete" => ["branch", args.Force ? "-D" : "-d", name],
            "rename" => ["branch", "-m", name],
            _ => throw new InvalidOperationException($"Unsupported branch action '{action}'.")
        };
        if (action == "delete")
        {
            var current = await GitCli.RunAsync(repo, ["branch", "--show-current"], cancellationToken: ct).ConfigureAwait(false);
            if (current.Ok && string.Equals(current.Stdout.Trim(), name, StringComparison.Ordinal))
                return Failure(action, $"Cannot delete the current branch '{name}'.");
        }

        var execution = await GitCli.RunAsync(repo, command, cancellationToken: ct).ConfigureAwait(false);
        if (!execution.Ok) return Failure(action, execution.Stderr, name, args.Force);
        var status = await GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = args.RepositoryPath }, ct).ConfigureAwait(false);
        return new GitBranchMutationResult { Success = true, Action = action, Branch = action == "rename" ? status.Branch : name, NewName = action == "rename" ? name : null, Force = args.Force, Output = execution.Stdout.Trim(), Status = status };
    }

    private static GitBranchMutationResult Failure(string action, string error, string? branch = null, bool force = false) =>
        new() { Success = false, Action = action, Branch = branch, Force = force, Error = string.IsNullOrWhiteSpace(error) ? "Git branch operation failed." : error.Trim() };
}
