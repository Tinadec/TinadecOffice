using System.Text;
using System.Text.Json.Serialization;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Git;

// Read-only Git capability set. GitCli owns workspace/link fencing and every
// invocation uses ArgumentList through TerminalRunner.

public sealed class GitStatusArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
}

public sealed class GitStatusEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("previous_path")] public string? PreviousPath { get; set; }
    [JsonPropertyName("staged_status")] public string StagedStatus { get; set; } = "clean";
    [JsonPropertyName("unstaged_status")] public string UnstagedStatus { get; set; } = "clean";
    [JsonPropertyName("status")] public string Status { get; set; } = "clean";
    [JsonPropertyName("is_untracked")] public bool IsUntracked { get; set; }
    [JsonPropertyName("is_conflicted")] public bool IsConflicted { get; set; }
}

public sealed class GitStatusResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("repository_root")] public string? RepositoryRoot { get; set; }
    [JsonPropertyName("branch")] public string Branch { get; set; } = "unknown";
    [JsonPropertyName("detached_head")] public bool DetachedHead { get; set; }
    [JsonPropertyName("upstream")] public string? Upstream { get; set; }
    [JsonPropertyName("ahead")] public int Ahead { get; set; }
    [JsonPropertyName("behind")] public int Behind { get; set; }
    [JsonPropertyName("has_uncommitted_changes")] public bool HasUncommittedChanges { get; set; }
    [JsonPropertyName("files")] public List<GitStatusEntry> Files { get; set; } = new();
}

public sealed class GitPushReadinessArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
}

public sealed class GitPushReadinessResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("needs_push")] public bool NeedsPush { get; set; }
    [JsonPropertyName("blockers")] public List<string> Blockers { get; set; } = new();
    [JsonPropertyName("status")] public GitStatusResult Status { get; set; } = new();
}

public sealed class GitDiffArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    /// <summary>working_tree, staged, ref_range, or all.</summary>
    [JsonPropertyName("target")] public string? Target { get; set; } = "all";
    [JsonPropertyName("base_ref")] public string? BaseRef { get; set; }
    [JsonPropertyName("head_ref")] public string? HeadRef { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
    [JsonPropertyName("max_files")] public int? MaxFiles { get; set; } = 120;
    [JsonPropertyName("max_diff_bytes")] public int? MaxDiffBytes { get; set; } = 180000;
}

public sealed class GitDiffSection
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("base_ref")] public string? BaseRef { get; set; }
    [JsonPropertyName("head_ref")] public string? HeadRef { get; set; }
    [JsonPropertyName("diff")] public string Diff { get; set; } = string.Empty;
    [JsonPropertyName("files")] public List<GitFileChange> Files { get; set; } = new();
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("truncation_reason")] public string? TruncationReason { get; set; }
}

public sealed class GitDiffResult : GitSimpleResult
{
    [JsonPropertyName("sections")] public List<GitDiffSection> Sections { get; set; } = new();
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

public sealed class GitBranchListArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("include_remote")] public bool IncludeRemote { get; set; } = true;
}

public sealed class GitBranch
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("is_current")] public bool IsCurrent { get; set; }
    [JsonPropertyName("is_remote")] public bool IsRemote { get; set; }
    [JsonPropertyName("upstream")] public string? Upstream { get; set; }
    [JsonPropertyName("commit")] public string? Commit { get; set; }
}

public sealed class GitBranchListResult : GitSimpleResult
{
    [JsonPropertyName("branches")] public List<GitBranch> Branches { get; set; } = new();
}

public sealed class GitWorktreeListArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
}

public sealed class GitWorktree
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("head")] public string? Head { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("detached")] public bool Detached { get; set; }
    [JsonPropertyName("is_current")] public bool IsCurrent { get; set; }
}

public sealed class GitWorktreeListResult : GitSimpleResult
{
    [JsonPropertyName("worktrees")] public List<GitWorktree> Worktrees { get; set; } = new();
}

public sealed class GitRefListArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("types")] public List<string>? Types { get; set; }
}

public sealed class GitRefListResult : GitSimpleResult
{
    [JsonPropertyName("refs")] public List<GitRef> Refs { get; set; } = new();
}

public sealed class GitRemoteListArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
}

public sealed class GitRemote
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("fetch_url")] public string? FetchUrl { get; set; }
    [JsonPropertyName("push_url")] public string? PushUrl { get; set; }
}

public sealed class GitRemoteListResult : GitSimpleResult
{
    [JsonPropertyName("remotes")] public List<GitRemote> Remotes { get; set; } = new();
}

public sealed class GitBlameArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("rev")] public string? Rev { get; set; } = "HEAD";
    [JsonPropertyName("start_line")] public int? StartLine { get; set; }
    [JsonPropertyName("end_line")] public int? EndLine { get; set; }
    [JsonPropertyName("max_output_bytes")] public int? MaxOutputBytes { get; set; } = 262144;
}

public sealed class GitBlameLine
{
    [JsonPropertyName("commit")] public string Commit { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
    [JsonPropertyName("author_time")] public string? AuthorTime { get; set; }
    [JsonPropertyName("line_number")] public int LineNumber { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

public sealed class GitBlameResult : GitSimpleResult
{
    [JsonPropertyName("lines")] public List<GitBlameLine> Lines { get; set; } = new();
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("truncation_reason")] public string? TruncationReason { get; set; }
}

public sealed class GitFileAtRevisionArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("rev")] public string Rev { get; set; } = "HEAD";
    [JsonPropertyName("max_output_bytes")] public int? MaxOutputBytes { get; set; } = 524288;
}

public sealed class GitFileAtRevisionResult : GitSimpleResult
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("rev")] public string? Rev { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("is_binary")] public bool IsBinary { get; set; }
    [JsonPropertyName("blob_hash")] public string? BlobHash { get; set; }
    [JsonPropertyName("byte_size")] public long? ByteSize { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("truncation_reason")] public string? TruncationReason { get; set; }
}

public sealed class GitConflictPreviewArgs
{
    [JsonPropertyName("repository_path")] public string? RepositoryPath { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("max_output_bytes")] public int? MaxOutputBytes { get; set; } = 524288;
}

public sealed class GitConflictBlock
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("start_line")] public int StartLine { get; set; }
    [JsonPropertyName("end_line")] public int EndLine { get; set; }
    [JsonPropertyName("base")] public string? Base { get; set; }
    [JsonPropertyName("ours")] public string? Ours { get; set; }
    [JsonPropertyName("theirs")] public string? Theirs { get; set; }
}

public sealed class GitConflictFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("stages")] public List<int> Stages { get; set; } = new();
    [JsonPropertyName("is_binary")] public bool IsBinary { get; set; }
    [JsonPropertyName("blocks")] public List<GitConflictBlock> Blocks { get; set; } = new();
}

public sealed class GitConflictPreviewResult : GitSimpleResult
{
    [JsonPropertyName("operation")] public string? Operation { get; set; }
    [JsonPropertyName("files")] public List<GitConflictFile> Files { get; set; } = new();
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

public class GitSimpleResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitStatusArgs))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitPushReadinessArgs))]
[JsonSerializable(typeof(GitPushReadinessResult))]
[JsonSerializable(typeof(GitDiffArgs))]
[JsonSerializable(typeof(GitDiffResult))]
[JsonSerializable(typeof(GitBranchListArgs))]
[JsonSerializable(typeof(GitBranchListResult))]
[JsonSerializable(typeof(GitWorktreeListArgs))]
[JsonSerializable(typeof(GitWorktreeListResult))]
[JsonSerializable(typeof(GitRefListArgs))]
[JsonSerializable(typeof(GitRefListResult))]
[JsonSerializable(typeof(GitRemoteListArgs))]
[JsonSerializable(typeof(GitRemoteListResult))]
[JsonSerializable(typeof(GitBlameArgs))]
[JsonSerializable(typeof(GitBlameResult))]
[JsonSerializable(typeof(GitFileAtRevisionArgs))]
[JsonSerializable(typeof(GitFileAtRevisionResult))]
[JsonSerializable(typeof(GitConflictPreviewArgs))]
[JsonSerializable(typeof(GitConflictPreviewResult))]
[JsonSerializable(typeof(GitStatusEntry))]
[JsonSerializable(typeof(GitDiffSection))]
[JsonSerializable(typeof(GitBranch))]
[JsonSerializable(typeof(GitWorktree))]
[JsonSerializable(typeof(GitRef))]
[JsonSerializable(typeof(GitRemote))]
[JsonSerializable(typeof(GitBlameLine))]
[JsonSerializable(typeof(GitConflictBlock))]
[JsonSerializable(typeof(GitConflictFile))]
[JsonSerializable(typeof(GitFileChange))]
internal partial class GitReadToolsJsonContext : JsonSerializerContext;

internal static class GitReadTools
{
    [ToolFunction("git_status", RequiresApproval = true)]
    public static async ValueTask<GitStatusResult> StatusAsync(GitStatusArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return StatusFailure(error);
        var status = await GitCli.RunAsync(repo, ["status", "--porcelain=v1", "--branch"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return status.Ok ? ParseStatus(repo, status.Stdout) : StatusFailure(status.Stderr, status.ExitCode);
    }

    [ToolFunction("git_push_readiness", RequiresApproval = true)]
    public static async ValueTask<GitPushReadinessResult> PushReadinessAsync(GitPushReadinessArgs args, CancellationToken cancellationToken)
    {
        var status = await StatusAsync(new GitStatusArgs { RepositoryPath = args.RepositoryPath }, cancellationToken).ConfigureAwait(false);
        if (!status.Success) return new GitPushReadinessResult { Success = false, Error = status.Error, ErrorCode = status.ErrorCode, Status = status };
        var blockers = new List<string>();
        if (status.DetachedHead) blockers.Add("HEAD is detached.");
        if (string.IsNullOrWhiteSpace(status.Upstream)) blockers.Add("No upstream branch is configured.");
        if (status.Behind > 0) blockers.Add($"Branch is behind upstream by {status.Behind} commit(s).");
        if (status.HasUncommittedChanges) blockers.Add("Working tree has uncommitted changes.");
        return new GitPushReadinessResult { Success = true, Status = status, NeedsPush = status.Ahead > 0, Ready = blockers.Count == 0, Blockers = blockers };
    }

    [ToolFunction("git_diff", RequiresApproval = true)]
    public static async ValueTask<GitDiffResult> DiffAsync(GitDiffArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Fail<GitDiffResult>(error);
        var target = args.Target ?? "all";
        if (target is not ("all" or "working_tree" or "staged" or "ref_range")) throw new InvalidOperationException("target must be all, working_tree, staged, or ref_range.");
        var maxFiles = Math.Clamp(args.MaxFiles ?? 120, 1, 500);
        var maxBytes = Math.Clamp(args.MaxDiffBytes ?? 180000, 1024, 1_000_000);
        var sections = new List<GitDiffSection>();
        if (target is "all" or "working_tree") sections.Add(await BuildDiffAsync(repo, "working_tree", [], args.Paths, maxFiles, maxBytes, cancellationToken).ConfigureAwait(false));
        if (target is "all" or "staged") sections.Add(await BuildDiffAsync(repo, "staged", ["--cached"], args.Paths, maxFiles, maxBytes, cancellationToken).ConfigureAwait(false));
        if (target is "all" or "ref_range")
        {
            var baseRef = args.BaseRef;
            var headRef = args.HeadRef ?? "HEAD";
            if (!string.IsNullOrWhiteSpace(baseRef))
            {
                GitCli.ValidateRevision(baseRef, "base_ref");
                GitCli.ValidateRevision(headRef, "head_ref");
                sections.Add(await BuildDiffAsync(repo, "ref_range", [$"{baseRef}...{headRef}"], args.Paths, maxFiles, maxBytes, cancellationToken, baseRef, headRef).ConfigureAwait(false));
            }
        }
        return new GitDiffResult { Success = true, Sections = sections, Truncated = sections.Any(section => section.Truncated) };
    }

    [ToolFunction("git_branch_list", RequiresApproval = true)]
    public static async ValueTask<GitBranchListResult> BranchListAsync(GitBranchListArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Fail<GitBranchListResult>(error);
        var branchArgs = new List<string> { "for-each-ref", "--format=%(HEAD)\t%(refname)\t%(refname:short)\t%(upstream:short)\t%(objectname)", "refs/heads" };
        if (args.IncludeRemote) branchArgs.Add("refs/remotes");
        var exec = await GitCli.RunAsync(repo, branchArgs, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!exec.Ok) return Fail<GitBranchListResult>(exec.Stderr, exec.ExitCode);
        return new GitBranchListResult { Success = true, Branches = exec.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')).Where(fields => fields.Length == 5).Select(fields => new GitBranch { IsCurrent = fields[0] == "*", Name = fields[2], Upstream = NullIfEmpty(fields[3]), Commit = NullIfEmpty(fields[4]), IsRemote = fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal) }).ToList() };
    }

    [ToolFunction("git_worktree_list", RequiresApproval = true)]
    public static async ValueTask<GitWorktreeListResult> WorktreeListAsync(GitWorktreeListArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Fail<GitWorktreeListResult>(error);
        var exec = await GitCli.RunAsync(repo, ["worktree", "list", "--porcelain"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!exec.Ok) return Fail<GitWorktreeListResult>(exec.Stderr, exec.ExitCode);
        var result = new List<GitWorktree>();
        GitWorktree? current = null;
        foreach (var line in exec.Stdout.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) { current = new GitWorktree { Path = line[9..] }; result.Add(current); }
            else if (current is not null && line.StartsWith("HEAD ", StringComparison.Ordinal)) current.Head = line[5..];
            else if (current is not null && line.StartsWith("branch ", StringComparison.Ordinal)) current.Branch = line[7..].Replace("refs/heads/", "", StringComparison.Ordinal);
            else if (current is not null && line == "detached") current.Detached = true;
        }
        foreach (var item in result) item.IsCurrent = PathsEqual(item.Path, repo);
        return new GitWorktreeListResult { Success = true, Worktrees = result };
    }

    [ToolFunction("git_ref_list", RequiresApproval = true)]
    public static async ValueTask<GitRefListResult> RefListAsync(GitRefListArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Fail<GitRefListResult>(error);
        var requested = args.Types is { Count: > 0 } ? args.Types.Select(type => type.ToLowerInvariant()).ToHashSet() : new HashSet<string>(["branch", "tag", "remote"]);
        if (!requested.All(type => type is "branch" or "tag" or "remote")) throw new InvalidOperationException("types may contain branch, tag, or remote only.");
        var exec = await GitCli.RunAsync(repo, ["for-each-ref", "--format=%(HEAD)\t%(refname)\t%(refname:short)"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!exec.Ok) return Fail<GitRefListResult>(exec.Stderr, exec.ExitCode);
        var refs = new List<GitRef>();
        foreach (var fields in exec.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')).Where(fields => fields.Length == 3))
        {
            var type = fields[1].StartsWith("refs/tags/", StringComparison.Ordinal) ? "tag" : fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal) ? "remote" : "branch";
            if (requested.Contains(type)) refs.Add(new GitRef { Name = fields[2], Type = type, IsHead = fields[0] == "*" });
        }
        return new GitRefListResult { Success = true, Refs = refs };
    }

    [ToolFunction("git_remote_list", RequiresApproval = true)]
    public static async ValueTask<GitRemoteListResult> RemoteListAsync(GitRemoteListArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Fail<GitRemoteListResult>(error);
        var names = await GitCli.RunAsync(repo, ["remote"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!names.Ok) return Fail<GitRemoteListResult>(names.Stderr, names.ExitCode);
        var remotes = new List<GitRemote>();
        foreach (var name in names.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fetch = await GitCli.RunAsync(repo, ["remote", "get-url", name], cancellationToken: cancellationToken).ConfigureAwait(false);
            var push = await GitCli.RunAsync(repo, ["remote", "get-url", "--push", name], cancellationToken: cancellationToken).ConfigureAwait(false);
            remotes.Add(new GitRemote { Name = name, FetchUrl = fetch.Ok ? fetch.Stdout.Trim() : null, PushUrl = push.Ok ? push.Stdout.Trim() : null });
        }
        return new GitRemoteListResult { Success = true, Remotes = remotes };
    }

    [ToolFunction("git_blame", RequiresApproval = true)]
    public static async ValueTask<GitBlameResult> BlameAsync(GitBlameArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.Path)) throw new InvalidOperationException("path is required.");
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Fail<GitBlameResult>(error);
        var path = GitCli.ResolveRepositoryRelativePath(repo, args.Path);
        var rev = args.Rev ?? "HEAD";
        GitCli.ValidateRevision(rev, "rev");
        var gitArgs = new List<string> { "blame", "--line-porcelain" };
        if (args.StartLine is > 0 && args.EndLine is int endLine && endLine >= args.StartLine.Value) gitArgs.AddRange(["-L", $"{args.StartLine},{endLine}"]);
        gitArgs.AddRange([rev, "--", path]);
        var maxChars = Math.Clamp((args.MaxOutputBytes ?? 262144) * 2, 2048, 2_000_000);
        var exec = await GitCli.RunAsync(repo, gitArgs, cancellationToken: cancellationToken, maxOutputChars: maxChars).ConfigureAwait(false);
        if (!exec.Ok && !exec.Truncated) return Fail<GitBlameResult>(exec.Stderr, exec.ExitCode);
        return ParseBlame(exec.Stdout, exec.Truncated);
    }

    [ToolFunction("git_file_at_revision", RequiresApproval = true)]
    public static async ValueTask<GitFileAtRevisionResult> FileAtRevisionAsync(GitFileAtRevisionArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.Path)) throw new InvalidOperationException("path is required.");
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Fail<GitFileAtRevisionResult>(error);
        var path = GitCli.ResolveRepositoryRelativePath(repo, args.Path);
        GitCli.ValidateRevision(args.Rev, "rev");
        var (hash, size) = await GitCli.GetBlobSummaryAsync(repo, args.Rev, path, cancellationToken).ConfigureAwait(false);
        if (hash is null) return Fail<GitFileAtRevisionResult>($"Path '{path}' was not found at {args.Rev}.");
        var maxBytes = Math.Clamp(args.MaxOutputBytes ?? 524288, 1024, 2_000_000);
        if (size is > 0 && size > maxBytes) return new GitFileAtRevisionResult { Success = true, Path = path, Rev = args.Rev, BlobHash = hash, ByteSize = size, Truncated = true, TruncationReason = "max_output_bytes" };
        var content = await GitCli.RunAsync(repo, ["show", $"{args.Rev}:{path}"], cancellationToken: cancellationToken, maxOutputChars: maxBytes * 2).ConfigureAwait(false);
        if (!content.Ok && !content.Truncated) return Fail<GitFileAtRevisionResult>(content.Stderr, content.ExitCode);
        var binary = content.Stdout.IndexOf('\0') >= 0;
        return new GitFileAtRevisionResult { Success = true, Path = path, Rev = args.Rev, BlobHash = hash, ByteSize = size, IsBinary = binary, Content = binary ? null : content.Stdout, Truncated = content.Truncated, TruncationReason = content.Truncated ? "max_output_bytes" : null };
    }

    [ToolFunction("git_conflict_preview", RequiresApproval = true)]
    public static async ValueTask<GitConflictPreviewResult> ConflictPreviewAsync(GitConflictPreviewArgs args, CancellationToken cancellationToken)
    {
        var repo = GitCli.ResolveRepo(args.RepositoryPath ?? string.Empty, out var error);
        if (repo is null) return Fail<GitConflictPreviewResult>(error);
        var requestedPath = string.IsNullOrWhiteSpace(args.Path) ? null : GitCli.ResolveRepositoryRelativePath(repo, args.Path);
        var index = await GitCli.RunAsync(repo, ["ls-files", "-u", "-z"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!index.Ok) return Fail<GitConflictPreviewResult>(index.Stderr, index.ExitCode);
        var stageByPath = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var record in index.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var parts = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[2], out var stage)) continue;
            var path = record[(tab + 1)..];
            if (requestedPath is null || path == requestedPath)
            {
                if (!stageByPath.TryGetValue(path, out var stages)) stageByPath[path] = stages = new List<int>();
                stages.Add(stage);
            }
        }
        var files = new List<GitConflictFile>();
        var truncated = false;
        var remaining = Math.Clamp(args.MaxOutputBytes ?? 524288, 1024, 2_000_000);
        foreach (var (path, stages) in stageByPath)
        {
            var baseText = await ReadStageAsync(repo, 1, path, cancellationToken).ConfigureAwait(false);
            var ours = await ReadStageAsync(repo, 2, path, cancellationToken).ConfigureAwait(false);
            var theirs = await ReadStageAsync(repo, 3, path, cancellationToken).ConfigureAwait(false);
            var isBinary = new[] { baseText, ours, theirs }.Any(text => text?.IndexOf('\0') >= 0);
            var file = new GitConflictFile { Path = path, Stages = stages.Distinct().Order().ToList(), IsBinary = isBinary };
            if (!isBinary)
            {
                var merge = ThreeWayTextMerge.Merge(baseText ?? string.Empty, ours ?? string.Empty, theirs ?? string.Empty);
                var counter = 0;
                foreach (var conflict in merge.Conflicts)
                {
                    var block = new GitConflictBlock { Id = $"{path}:{++counter}", StartLine = conflict.StartLine, EndLine = conflict.EndLine, Base = conflict.Base, Ours = conflict.Ours, Theirs = conflict.Theirs };
                    var bytes = Encoding.UTF8.GetByteCount((block.Base ?? "") + block.Ours + block.Theirs);
                    if (bytes > remaining) { truncated = true; break; }
                    remaining -= bytes;
                    file.Blocks.Add(block);
                }
            }
            files.Add(file);
            if (truncated) break;
        }
        return new GitConflictPreviewResult { Success = true, Operation = await DetectOperationAsync(repo, cancellationToken).ConfigureAwait(false), Files = files, Truncated = truncated };
    }

    private static async Task<GitDiffSection> BuildDiffAsync(string repo, string kind, List<string> modeArgs, List<string>? paths, int maxFiles, int maxBytes, CancellationToken ct, string? baseRef = null, string? headRef = null)
    {
        var common = new List<string>(modeArgs);
        var numstatArgs = new List<string> { "diff" }; numstatArgs.AddRange(common); numstatArgs.AddRange(["--numstat", "-z", "-M"]);
        var nameArgs = new List<string> { "diff" }; nameArgs.AddRange(common); nameArgs.AddRange(["--name-status", "-z", "-M"]);
        var diffArgs = new List<string> { "diff" }; diffArgs.AddRange(common); diffArgs.AddRange(["--no-ext-diff", "--no-color", "-M"]);
        if (paths is { Count: > 0 })
        {
            var safePaths = paths.Select(path => GitCli.ResolveRepositoryRelativePath(repo, path)).ToList();
            numstatArgs.Add("--"); numstatArgs.AddRange(safePaths);
            nameArgs.Add("--"); nameArgs.AddRange(safePaths);
            diffArgs.Add("--"); diffArgs.AddRange(safePaths);
        }
        var numstat = await GitCli.RunAsync(repo, numstatArgs, cancellationToken: ct).ConfigureAwait(false);
        var nameStatus = await GitCli.RunAsync(repo, nameArgs, cancellationToken: ct).ConfigureAwait(false);
        var diff = await GitCli.RunAsync(repo, diffArgs, cancellationToken: ct, maxOutputChars: maxBytes * 2).ConfigureAwait(false);
        if (!numstat.Ok || !nameStatus.Ok || (!diff.Ok && !diff.Truncated)) throw new InvalidOperationException((!diff.Ok ? diff.Stderr : !numstat.Ok ? numstat.Stderr : nameStatus.Stderr).Trim());
        var files = DiffParser.MergeFileChanges(DiffParser.ParseNameStatus(nameStatus.Stdout), DiffParser.ParseNumstat(numstat.Stdout));
        var fileTruncated = files.Count > maxFiles;
        if (fileTruncated) files = files.Take(maxFiles).ToList();
        return new GitDiffSection { Kind = kind, BaseRef = baseRef, HeadRef = headRef, Diff = diff.Stdout, Files = files, Truncated = fileTruncated || diff.Truncated, TruncationReason = diff.Truncated ? "max_diff_bytes" : fileTruncated ? "max_files" : null };
    }

    private static GitStatusResult ParseStatus(string repo, string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines.FirstOrDefault(line => line.StartsWith("## ", StringComparison.Ordinal)) ?? "## unknown";
        var branchText = header[3..];
        var rawBranch = branchText.Split("...")[0].Split(" [")[0].Trim();
        var detached = rawBranch.StartsWith("HEAD (", StringComparison.Ordinal) || rawBranch == "HEAD";
        var upstream = branchText.Contains("...", StringComparison.Ordinal) ? NullIfEmpty(branchText.Split("...")[1].Split(" [")[0].Trim()) : null;
        var entries = lines.Where(line => !line.StartsWith("## ", StringComparison.Ordinal)).Where(line => line.Length >= 3).Select(ParseStatusEntry).ToList();
        return new GitStatusResult { Success = true, RepositoryRoot = repo, Branch = rawBranch.StartsWith("No commits yet on ", StringComparison.Ordinal) ? rawBranch[18..] : rawBranch, DetachedHead = detached, Upstream = upstream, Ahead = MatchNumber(branchText, "ahead "), Behind = MatchNumber(branchText, "behind "), HasUncommittedChanges = entries.Count > 0, Files = entries };
    }

    private static GitStatusEntry ParseStatusEntry(string line)
    {
        var staged = line[0]; var unstaged = line[1]; var raw = line[3..];
        var rename = raw.Split(" -> ", 2, StringSplitOptions.None);
        var conflicted = staged == 'U' || unstaged == 'U' || new[] { "AA", "DD", "AU", "UA", "DU", "UD" }.Contains($"{staged}{unstaged}");
        return new GitStatusEntry { Path = rename.Length == 2 ? rename[1] : raw, PreviousPath = rename.Length == 2 ? rename[0] : null, StagedStatus = StatusLabel(staged), UnstagedStatus = StatusLabel(unstaged), Status = conflicted ? "conflicted" : staged == '?' && unstaged == '?' ? "untracked" : staged != ' ' && unstaged != ' ' ? "staged_and_modified" : staged != ' ' ? $"staged_{StatusLabel(staged)}" : StatusLabel(unstaged), IsUntracked = staged == '?' && unstaged == '?', IsConflicted = conflicted };
    }

    private static GitBlameResult ParseBlame(string output, bool truncated)
    {
        var lines = new List<GitBlameLine>();
        string hash = string.Empty, author = string.Empty, time = string.Empty; var number = 0;
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > 40 && line[40] == ' ') { hash = line[..40]; var fields = line[41..].Split(' '); int.TryParse(fields.ElementAtOrDefault(1), out number); }
            else if (line.StartsWith("author ", StringComparison.Ordinal)) author = line[7..];
            else if (line.StartsWith("author-time ", StringComparison.Ordinal)) time = line[12..];
            else if (line.StartsWith('\t')) lines.Add(new GitBlameLine { Commit = hash, Author = author, AuthorTime = time, LineNumber = number, Content = line[1..] });
        }
        return new GitBlameResult { Success = true, Lines = lines, Truncated = truncated, TruncationReason = truncated ? "max_output_bytes" : null };
    }

    private static async Task<string?> ReadStageAsync(string repo, int stage, string path, CancellationToken ct)
    {
        var content = await GitCli.RunAsync(repo, ["show", $":{stage}:{path}"], cancellationToken: ct, maxOutputChars: 1_000_000).ConfigureAwait(false);
        return content.Ok ? content.Stdout : null;
    }

    private static async Task<string?> DetectOperationAsync(string repo, CancellationToken ct)
    {
        foreach (var operation in new[] { ("rebase-merge", "rebase"), ("rebase-apply", "rebase"), ("MERGE_HEAD", "merge") })
        {
            var path = await GitCli.RunAsync(repo, ["rev-parse", "--git-path", operation.Item1], cancellationToken: ct).ConfigureAwait(false);
            if (path.Ok && (Directory.Exists(path.Stdout.Trim()) || File.Exists(path.Stdout.Trim()))) return operation.Item2;
        }
        return null;
    }
    private static string StatusLabel(char code) => code switch { 'A' => "added", 'M' => "modified", 'D' => "deleted", 'R' => "renamed", 'C' => "copied", 'U' => "unmerged", '?' => "untracked", ' ' => "clean", _ => code.ToString() };
    private static int MatchNumber(string value, string prefix) { var start = value.IndexOf(prefix, StringComparison.Ordinal); if (start < 0) return 0; var digits = new string(value[(start + prefix.Length)..].TakeWhile(char.IsDigit).ToArray()); return int.TryParse(digits, out var number) ? number : 0; }
    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static GitStatusResult StatusFailure(string error, int code = 1) => new() { Success = false, Error = error.Trim(), ErrorCode = code < 0 ? GitCli.GitNotFoundCode : GitCli.NotARepoCode };
    private static T Fail<T>(string error, int code = 1) where T : GitSimpleResult, new() => new() { Success = false, Error = error.Trim(), ErrorCode = code < 0 ? GitCli.GitNotFoundCode : GitCli.NotARepoCode };
}
