using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

public sealed class GitCommitArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("include_all")] public bool IncludeAll { get; set; }
    [JsonPropertyName("commit_staged_only")] public bool CommitStagedOnly { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
}

public sealed class GitCommitResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("staged_files")] public List<string> StagedFiles { get; set; } = new();
    [JsonPropertyName("commit_hash")] public string? CommitHash { get; set; }
    [JsonPropertyName("commit_output")] public string? CommitOutput { get; set; }
    [JsonPropertyName("status")] public GitStatusResult? Status { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitCommitArgs))]
[JsonSerializable(typeof(GitCommitResult))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitStatusEntry))]
internal partial class GitCommitToolJsonContext : JsonSerializerContext;

internal static class GitCommitTool
{
    [ToolFunction("git_commit", RequiresApproval = true)]
    public static async ValueTask<GitCommitResult> CommitAsync(
        GitCommitArgs args,
        CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var repoError);
        if (repo is null) return Failure(repoError, GitCli.NotARepoCode);

        var message = args.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("message is required.");
        if (message.IndexOf('\0') >= 0)
            throw new InvalidOperationException("message must not contain a null character.");

        var rawPaths = args.Paths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList() ?? [];
        var modeCount = (args.IncludeAll ? 1 : 0) + (args.CommitStagedOnly ? 1 : 0) + (rawPaths.Count > 0 ? 1 : 0);
        if (modeCount != 1)
            throw new InvalidOperationException("Choose exactly one commit mode: include_all, commit_staged_only, or paths.");

        var mode = args.CommitStagedOnly ? "staged_only" : args.IncludeAll ? "include_all" : "paths";
        if (!args.CommitStagedOnly)
        {
            var addArguments = new List<string> { "add" };
            if (args.IncludeAll)
            {
                addArguments.Add("-A");
            }
            else
            {
                var resolvedPaths = rawPaths
                    .Select(path => GitCli.ResolveRepositoryRelativePath(repo, path))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                addArguments.Add("--");
                addArguments.AddRange(resolvedPaths);
            }

            var add = await GitCli.RunAsync(repo, addArguments, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!add.Ok) return Failure(add.Stderr, add.ExitCode, mode);
        }

        var staged = await GitCli.RunAsync(
            repo,
            ["diff", "--cached", "--name-only", "-z"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!staged.Ok) return Failure(staged.Stderr, staged.ExitCode, mode);

        var stagedFiles = staged.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (stagedFiles.Count == 0)
        {
            var status = await ReadStatusAsync(args.RepositoryPath, cancellationToken).ConfigureAwait(false);
            return new GitCommitResult
            {
                Success = false,
                Error = "No staged changes are available to commit.",
                ErrorCode = "no_staged_changes",
                Mode = mode,
                Status = status
            };
        }

        var commit = await GitCli.RunAsync(
            repo,
            ["commit", "-m", message],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!commit.Ok) return Failure(commit.Stderr, commit.ExitCode, mode, stagedFiles);

        var hash = await GitCli.RunAsync(
            repo,
            ["rev-parse", "HEAD"],
            cancellationToken: cancellationToken,
            timeoutMs: 10_000).ConfigureAwait(false);
        var statusAfterCommit = await ReadStatusAsync(args.RepositoryPath, cancellationToken).ConfigureAwait(false);
        return new GitCommitResult
        {
            Success = true,
            Mode = mode,
            StagedFiles = stagedFiles,
            CommitHash = hash.Ok ? hash.Stdout.Trim() : null,
            CommitOutput = commit.Stdout.Trim(),
            Status = statusAfterCommit
        };
    }

    private static async Task<GitStatusResult> ReadStatusAsync(
        string? repositoryPath,
        CancellationToken cancellationToken) =>
        await GitReadTools.StatusAsync(
            new GitStatusArgs { RepositoryPath = repositoryPath },
            cancellationToken).ConfigureAwait(false);

    private static GitCommitResult Failure(
        string error,
        string? errorCode,
        string? mode = null,
        List<string>? stagedFiles = null) => new()
        {
            Success = false,
            Error = string.IsNullOrWhiteSpace(error) ? "Git commit failed." : error.Trim(),
            ErrorCode = errorCode,
            Mode = mode,
            StagedFiles = stagedFiles ?? []
        };

    private static GitCommitResult Failure(
        string error,
        int exitCode,
        string? mode = null,
        List<string>? stagedFiles = null) =>
        Failure(error, exitCode < 0 ? GitCli.GitNotFoundCode : null, mode, stagedFiles);
}
