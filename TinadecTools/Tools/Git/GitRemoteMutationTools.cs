using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

public sealed class GitRemoteMutationArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("remote")] public string? Remote { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("set_upstream")] public bool SetUpstream { get; set; }
    [JsonPropertyName("prune")] public bool Prune { get; set; } = true;
}

public sealed class GitRemoteMutationResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("remote")] public string? Remote { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("changed")] public bool Changed { get; set; }
    [JsonPropertyName("set_upstream")] public bool SetUpstream { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("status")] public GitStatusResult? Status { get; set; }
    [JsonPropertyName("branches")] public List<GitBranch> Branches { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitRemoteMutationArgs))]
[JsonSerializable(typeof(GitRemoteMutationResult))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitStatusEntry))]
[JsonSerializable(typeof(GitBranch))]
internal partial class GitRemoteMutationToolsJsonContext : JsonSerializerContext;

internal static class GitRemoteMutationTools
{
    [ToolFunction("git_fetch", RequiresApproval = true)]
    public static async ValueTask<GitRemoteMutationResult> FetchAsync(GitRemoteMutationArgs args, CancellationToken ct)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Failure("fetch", error);
        var remote = string.IsNullOrWhiteSpace(args.Remote) ? null : args.Remote.Trim();
        if (remote is not null && !await RemoteExistsAsync(repo, remote, ct).ConfigureAwait(false)) return Failure("fetch", $"Remote '{remote}' is not configured.");
        var command = new List<string> { "-c", "credential.interactive=never", "fetch" };
        if (args.Prune) command.Add("--prune");
        command.Add(remote ?? "--all");
        var execution = await GitCli.RunAsync(repo, command, cancellationToken: ct, timeoutMs: 60_000).ConfigureAwait(false);
        if (!execution.Ok) return Failure("fetch", execution.Stderr, remote);
        var status = await StatusAsync(args.RepositoryPath, ct).ConfigureAwait(false);
        var branches = await GitReadTools.BranchListAsync(new GitBranchListArgs { RepositoryPath = args.RepositoryPath, IncludeRemote = true }, ct).ConfigureAwait(false);
        return new GitRemoteMutationResult { Success = true, Action = "fetch", Remote = remote ?? "--all", Changed = true, Output = JoinOutput(execution), Status = status, Branch = status.Branch, Branches = branches.Branches };
    }

    [ToolFunction("git_push", RequiresApproval = true)]
    public static async ValueTask<GitRemoteMutationResult> PushAsync(GitRemoteMutationArgs args, CancellationToken ct)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Failure("push", error);
        var status = await StatusAsync(args.RepositoryPath, ct).ConfigureAwait(false);
        if (!status.Success) return Failure("push", status.Error ?? "Unable to read Git status.");
        if (status.DetachedHead) return Failure("push", "Cannot push a detached HEAD.");
        if (status.HasUncommittedChanges) return Failure("push", "Working tree has uncommitted changes.");
        if (status.Behind > 0) return Failure("push", $"Branch is behind upstream by {status.Behind} commit(s).");
        var remote = string.IsNullOrWhiteSpace(args.Remote) ? "origin" : args.Remote.Trim();
        if (!await RemoteExistsAsync(repo, remote, ct).ConfigureAwait(false)) return Failure("push", $"Remote '{remote}' is not configured.");
        var branch = string.IsNullOrWhiteSpace(args.Branch) ? status.Branch : args.Branch.Trim();
        var valid = await GitCli.RunAsync(repo, ["check-ref-format", "--branch", branch], cancellationToken: ct).ConfigureAwait(false);
        if (!valid.Ok) return Failure("push", $"Invalid branch name '{branch}'.");
        if (!string.IsNullOrWhiteSpace(status.Upstream) && status.Ahead == 0)
            return new GitRemoteMutationResult { Success = true, Action = "push", Remote = remote, Branch = branch, Changed = false, Status = status };
        if (string.IsNullOrWhiteSpace(status.Upstream) && !args.SetUpstream) return Failure("push", "No upstream branch is configured; set_upstream is required.");
        var command = new List<string> { "-c", "credential.interactive=never", "push" };
        if (string.IsNullOrWhiteSpace(status.Upstream)) command.AddRange(["-u", remote, branch]);
        var execution = await GitCli.RunAsync(repo, command, cancellationToken: ct, timeoutMs: 60_000).ConfigureAwait(false);
        if (!execution.Ok) return Failure("push", execution.Stderr, remote, branch);
        var after = await StatusAsync(args.RepositoryPath, ct).ConfigureAwait(false);
        return new GitRemoteMutationResult { Success = true, Action = "push", Remote = remote, Branch = branch, Changed = true, SetUpstream = string.IsNullOrWhiteSpace(status.Upstream), Output = JoinOutput(execution), Status = after };
    }

    [ToolFunction("git_pull", RequiresApproval = true)]
    public static async ValueTask<GitRemoteMutationResult> PullAsync(GitRemoteMutationArgs args, CancellationToken ct)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Failure("pull", error);
        var status = await StatusAsync(args.RepositoryPath, ct).ConfigureAwait(false);
        if (!status.Success) return Failure("pull", status.Error ?? "Unable to read Git status.");
        if (status.DetachedHead) return Failure("pull", "Cannot pull into a detached HEAD.");
        if (status.HasUncommittedChanges) return Failure("pull", "Working tree has uncommitted changes.");
        var remote = string.IsNullOrWhiteSpace(args.Remote) ? null : args.Remote.Trim();
        var branch = string.IsNullOrWhiteSpace(args.Branch) ? null : args.Branch.Trim();
        if ((remote is null) != (branch is null)) throw new InvalidOperationException("remote and branch must be provided together.");
        if (remote is not null && !await RemoteExistsAsync(repo, remote, ct).ConfigureAwait(false)) return Failure("pull", $"Remote '{remote}' is not configured.");
        if (remote is null && string.IsNullOrWhiteSpace(status.Upstream)) return Failure("pull", "No upstream branch is configured.");
        var command = new List<string> { "-c", "credential.interactive=never", "pull", "--ff-only" };
        if (remote is not null) command.AddRange([remote, branch!]);
        var execution = await GitCli.RunAsync(repo, command, cancellationToken: ct, timeoutMs: 60_000).ConfigureAwait(false);
        if (!execution.Ok) return Failure("pull", execution.Stderr, remote, branch);
        var after = await StatusAsync(args.RepositoryPath, ct).ConfigureAwait(false);
        return new GitRemoteMutationResult { Success = true, Action = "pull", Remote = remote ?? status.Upstream?.Split('/')[0], Branch = branch ?? status.Branch, Changed = !execution.Stdout.Contains("Already up to date", StringComparison.OrdinalIgnoreCase), Output = JoinOutput(execution), Status = after };
    }

    private static Task<GitStatusResult> StatusAsync(string? repositoryPath, CancellationToken ct) => GitReadTools.StatusAsync(new GitStatusArgs { RepositoryPath = repositoryPath }, ct).AsTask();
    private static async Task<bool> RemoteExistsAsync(string repo, string remote, CancellationToken ct)
    {
        if (remote.StartsWith("-", StringComparison.Ordinal) || remote.Any(char.IsWhiteSpace)) return false;
        return (await GitCli.RunAsync(repo, ["remote", "get-url", remote], cancellationToken: ct).ConfigureAwait(false)).Ok;
    }
    private static string JoinOutput(GitExecResult result) => string.Join('\n', new[] { result.Stdout.Trim(), result.Stderr.Trim() }.Where(value => value.Length > 0));
    private static GitRemoteMutationResult Failure(string action, string error, string? remote = null, string? branch = null) => new() { Success = false, Action = action, Error = string.IsNullOrWhiteSpace(error) ? $"Git {action} failed." : error.Trim(), Remote = remote, Branch = branch };
}
