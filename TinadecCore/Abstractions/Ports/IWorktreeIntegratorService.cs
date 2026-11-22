namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Supported Git integration strategies for <see cref="IWorktreeIntegratorService"/>.
/// </summary>
public static class WorktreeIntegrationStrategies
{
    public const string NoFf = "no_ff";
    public const string Squash = "squash";
    public const string CherryPick = "cherry_pick";

    public static string Normalize(string? strategy) => strategy?.Trim().ToLowerInvariant() switch
    {
        Squash => Squash,
        CherryPick or "cherry-pick" => CherryPick,
        _ => NoFf,
    };
}

/// <summary>
/// Describes one spawned teammate's isolated Git worktree and its authorized write boundary.
/// </summary>
public sealed record TeammateWorktreeSpec(
    string Handle,
    string WorktreePath,
    string? BranchName,
    IReadOnlyList<string>? WriteScope);

/// <summary>
/// Per-worktree inspection snapshot collected before any merge/integration attempt.
/// </summary>
public sealed record WorktreeInspectionItem(
    string Handle,
    string WorktreePath,
    string? BranchName,
    string? HeadCommitSha,
    bool HasUncommittedChanges,
    IReadOnlyList<string> ModifiedFiles,
    IReadOnlyList<string> DirtyFiles,
    IReadOnlyList<string> ScopeViolations,
    int AheadCommitCount = 0);

/// <summary>
/// Reports a workspace-relative file touched by two or more teammate worktrees simultaneously.
/// </summary>
public sealed record WorktreeOverlapConflict(
    string RelativePath,
    IReadOnlyList<string> Handles);

/// <summary>
/// Aggregate preflight report across all inspected teammate worktrees relative to <paramref name="TargetPath"/>.
/// </summary>
public sealed record WorktreeInspectionReport(
    string TargetPath,
    IReadOnlyList<WorktreeInspectionItem> Items,
    IReadOnlyList<WorktreeOverlapConflict> Overlaps,
    bool CanIntegrateCleanly,
    string Summary)
{
    public IReadOnlyList<string> AllScopeViolations =>
        Items.SelectMany(item => item.ScopeViolations)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// Request to integrate (or abort an integration in) <paramref name="TargetPath"/>.
/// </summary>
public sealed record WorktreeIntegrationRequest(
    string TargetPath,
    IReadOnlyList<TeammateWorktreeSpec> Worktrees,
    string Strategy = WorktreeIntegrationStrategies.NoFf,
    string? TargetBranch = null,
    bool AutoCommitDirtyWorktrees = true,
    string? CommitMessagePrefix = null,
    bool AbortOnly = false);

/// <summary>
/// Outcome of <see cref="IWorktreeIntegratorService.IntegrateAsync"/>. On any conflict or violation,
/// <see cref="Success"/> is <c>false</c> and the target repository is guaranteed clean (never left half-merged).
/// </summary>
public sealed record WorktreeIntegrationResult(
    bool Success,
    bool RolledBack,
    IReadOnlyList<string> IntegratedHandles,
    IReadOnlyList<string> ConflictFiles,
    IReadOnlyList<string> ScopeViolations,
    string? MergeCommitSha,
    string Message);

/// <summary>
/// Governed Git worktree inspection and integration port (#33). Implementations inspect modified and
/// uncommitted files across teammate worktrees, enforce per-teammate <c>WriteScope</c> boundaries and
/// cross-teammate overlap checks fail-closed, and execute atomic Git merges with automatic rollback on conflict.
/// </summary>
public interface IWorktreeIntegratorService
{
    Task<WorktreeInspectionReport> InspectAsync(
        string targetPath,
        IReadOnlyList<TeammateWorktreeSpec> worktrees,
        string? targetBranch = null,
        CancellationToken cancellationToken = default);

    Task<WorktreeIntegrationResult> IntegrateAsync(
        WorktreeIntegrationRequest request,
        CancellationToken cancellationToken = default);

    Task<WorktreeIntegrationResult> AbortAsync(
        string targetPath,
        CancellationToken cancellationToken = default) =>
        IntegrateAsync(new WorktreeIntegrationRequest(targetPath, [], AbortOnly: true), cancellationToken);
}
