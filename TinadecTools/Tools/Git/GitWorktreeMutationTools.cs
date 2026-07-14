using System.Text.Json.Serialization;
using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tools.Git;

public sealed class GitWorktreeMutationArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("start_ref")] public string? StartRef { get; set; }
    [JsonPropertyName("force")] public bool Force { get; set; }
}

public sealed class GitWorktreeMutationResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("created_branch")] public bool CreatedBranch { get; set; }
    [JsonPropertyName("force")] public bool Force { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("worktrees")] public List<GitWorktree> Worktrees { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitWorktreeMutationArgs))]
[JsonSerializable(typeof(GitWorktreeMutationResult))]
[JsonSerializable(typeof(GitWorktree))]
internal partial class GitWorktreeMutationToolsJsonContext : JsonSerializerContext;

internal static class GitWorktreeMutationTools
{
    [ToolFunction("git_worktree_create", RequiresApproval = true)]
    public static async ValueTask<GitWorktreeMutationResult> CreateAsync(GitWorktreeMutationArgs args, CancellationToken ct)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Failure("create", error);
        var branch = args.Branch?.Trim();
        if (string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("branch is required.");
        var valid = await GitCli.RunAsync(repo, ["check-ref-format", "--branch", branch], cancellationToken: ct).ConfigureAwait(false);
        if (!valid.Ok) return Failure("create", $"Invalid branch name '{branch}'.");

        var target = ResolveManagedPath(repo, args.Path, branch);
        if (Directory.Exists(target) || File.Exists(target)) return Failure("create", $"Worktree path '{target}' already exists.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var exists = await GitCli.RunAsync(repo, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken: ct).ConfigureAwait(false);
        var command = new List<string> { "worktree", "add" };
        if (!exists.Ok)
        {
            var startRef = string.IsNullOrWhiteSpace(args.StartRef) ? "HEAD" : args.StartRef.Trim();
            GitCli.ValidateRevision(startRef, "start_ref");
            command.AddRange(["-b", branch, target, startRef]);
        }
        else command.AddRange([target, branch]);
        var execution = await GitCli.RunAsync(repo, command, cancellationToken: ct).ConfigureAwait(false);
        if (!execution.Ok) return Failure("create", execution.Stderr, target, branch);
        return await SuccessAsync(args.RepositoryPath, "create", target, branch, !exists.Ok, false, execution.Stdout, ct).ConfigureAwait(false);
    }

    [ToolFunction("git_worktree_remove", RequiresApproval = true)]
    public static async ValueTask<GitWorktreeMutationResult> RemoveAsync(GitWorktreeMutationArgs args, CancellationToken ct)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Failure("remove", error);
        if (string.IsNullOrWhiteSpace(args.Path)) throw new InvalidOperationException("path is required.");
        var target = ResolveManagedPath(repo, args.Path, null);
        if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(target), System.IO.Path.TrimEndingDirectorySeparator(repo), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Failure("remove", "Cannot remove the current worktree.", target);
        var command = new List<string> { "worktree", "remove" };
        if (args.Force) command.Add("--force");
        command.Add(target);
        var execution = await GitCli.RunAsync(repo, command, cancellationToken: ct).ConfigureAwait(false);
        if (!execution.Ok) return Failure("remove", execution.Stderr, target, force: args.Force);
        return await SuccessAsync(args.RepositoryPath, "remove", target, null, false, args.Force, execution.Stdout, ct).ConfigureAwait(false);
    }

    private static string ResolveManagedPath(string repo, string? requested, string? branch)
    {
        var managedRoot = WorkspacePathResolver.ResolvePath(System.IO.Path.Combine(repo, ".tinadec", "worktrees"));
        var target = string.IsNullOrWhiteSpace(requested)
            ? WorkspacePathResolver.ResolvePath(System.IO.Path.Combine(managedRoot, Slug(branch!)))
            : WorkspacePathResolver.ResolvePath(System.IO.Path.GetFullPath(requested, repo));
        var relative = System.IO.Path.GetRelativePath(managedRoot, target);
        if (relative == "." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("Worktree paths must stay inside .tinadec/worktrees.");
        return target;
    }

    private static string Slug(string value) => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')).Trim('-');

    private static async Task<GitWorktreeMutationResult> SuccessAsync(string? repositoryPath, string action, string path, string? branch, bool createdBranch, bool force, string output, CancellationToken ct)
    {
        var list = await GitReadTools.WorktreeListAsync(new GitWorktreeListArgs { RepositoryPath = repositoryPath }, ct).ConfigureAwait(false);
        return new GitWorktreeMutationResult { Success = true, Action = action, Path = path, Branch = branch, CreatedBranch = createdBranch, Force = force, Output = output.Trim(), Worktrees = list.Worktrees };
    }

    private static GitWorktreeMutationResult Failure(string action, string error, string? path = null, string? branch = null, bool force = false) =>
        new() { Success = false, Action = action, Error = string.IsNullOrWhiteSpace(error) ? "Git worktree operation failed." : error.Trim(), Path = path, Branch = branch, Force = force };
}
