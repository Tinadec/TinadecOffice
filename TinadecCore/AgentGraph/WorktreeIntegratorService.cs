using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentGraph;

/// <summary>
/// Governed Git worktree preflight inspector and atomic integrator (#33).
/// Enforces per-teammate <c>WriteScope</c> containment and cross-teammate file overlap checks fail-closed
/// before touching the target repository, and guarantees immediate rollback (<c>git merge --abort</c>,
/// <c>git cherry-pick --abort</c>, <c>git reset --merge</c>) if any Git conflict arises.
/// </summary>
public sealed class WorktreeIntegratorService : IWorktreeIntegratorService
{
    private const string GitCommitterName = "Tinadec Worktree Integrator";
    private const string GitCommitterEmail = "integrator@tinadec.local";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TargetLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<WorktreeInspectionReport> InspectAsync(
        string targetPath,
        IReadOnlyList<TeammateWorktreeSpec> worktrees,
        string? targetBranch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(worktrees);

        var canonicalTarget = WorkspacePathSpelling.Canonical(targetPath);
        if (!Directory.Exists(canonicalTarget))
        {
            return new WorktreeInspectionReport(
                canonicalTarget,
                [],
                [],
                CanIntegrateCleanly: false,
                $"Target repository directory '{canonicalTarget}' does not exist.");
        }

        var targetRef = !string.IsNullOrWhiteSpace(targetBranch) ? targetBranch.Trim() : "HEAD";
        var targetHeadResult = await RunGitAsync(canonicalTarget, ["rev-parse", "--verify", targetRef], cancellationToken).ConfigureAwait(false);
        if (targetHeadResult.ExitCode != 0 && !string.Equals(targetRef, "HEAD", StringComparison.Ordinal))
        {
            targetHeadResult = await RunGitAsync(canonicalTarget, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
        }

        if (targetHeadResult.ExitCode != 0)
        {
            return new WorktreeInspectionReport(
                canonicalTarget,
                [],
                [],
                CanIntegrateCleanly: false,
                $"Could not resolve HEAD in target repository '{canonicalTarget}': {FirstLine(targetHeadResult.StdErr)}");
        }

        var targetHead = targetHeadResult.StdOut.Trim();
        var items = new List<WorktreeInspectionItem>(worktrees.Count);
        var missingHandles = new List<string>();

        foreach (var spec in worktrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = string.IsNullOrWhiteSpace(spec.Handle) ? "teammate" : spec.Handle.Trim();
            if (string.IsNullOrWhiteSpace(spec.WorktreePath) || !Directory.Exists(spec.WorktreePath))
            {
                missingHandles.Add(handle);
                items.Add(new WorktreeInspectionItem(
                    handle,
                    spec.WorktreePath ?? string.Empty,
                    spec.BranchName,
                    HeadCommitSha: null,
                    HasUncommittedChanges: false,
                    ModifiedFiles: [],
                    DirtyFiles: [],
                    ScopeViolations: [$"{handle}: worktree directory '{spec.WorktreePath}' does not exist."],
                    AheadCommitCount: 0));
                continue;
            }

            var worktreeRoot = WorkspacePathSpelling.Canonical(spec.WorktreePath);
            var headRes = await RunGitAsync(worktreeRoot, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
            var headSha = headRes.ExitCode == 0 ? headRes.StdOut.Trim() : null;

            var branchName = spec.BranchName;
            if (string.IsNullOrWhiteSpace(branchName))
            {
                var branchRes = await RunGitAsync(worktreeRoot, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
                if (branchRes.ExitCode == 0)
                {
                    var resolvedBranch = branchRes.StdOut.Trim();
                    branchName = string.Equals(resolvedBranch, "HEAD", StringComparison.Ordinal) ? null : resolvedBranch;
                }
            }

            var statusRes = await RunGitAsync(worktreeRoot, ["-c", "core.quotepath=false", "status", "--porcelain=v1", "-uall"], cancellationToken).ConfigureAwait(false);
            var dirtyFiles = ParsePorcelainPaths(statusRes.StdOut);

            var baseRef = targetHead;
            if (!string.IsNullOrWhiteSpace(headSha))
            {
                var mergeBaseRes = await RunGitAsync(worktreeRoot, ["merge-base", targetHead, headSha], cancellationToken).ConfigureAwait(false);
                if (mergeBaseRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(mergeBaseRes.StdOut))
                    baseRef = mergeBaseRes.StdOut.Trim();
            }

            var committedFiles = Array.Empty<string>();
            var aheadCount = 0;
            if (!string.IsNullOrWhiteSpace(headSha) && !string.Equals(baseRef, headSha, StringComparison.OrdinalIgnoreCase))
            {
                var diffRes = await RunGitAsync(worktreeRoot, ["-c", "core.quotepath=false", "diff", "--name-only", $"{baseRef}..{headSha}"], cancellationToken).ConfigureAwait(false);
                if (diffRes.ExitCode == 0)
                    committedFiles = ParseLinePaths(diffRes.StdOut);

                var countRes = await RunGitAsync(worktreeRoot, ["rev-list", "--count", $"{baseRef}..{headSha}"], cancellationToken).ConfigureAwait(false);
                if (countRes.ExitCode == 0 && int.TryParse(countRes.StdOut.Trim(), out var parsedAhead))
                    aheadCount = parsedAhead;
            }

            var workingDiffRes = await RunGitAsync(worktreeRoot, ["-c", "core.quotepath=false", "diff", "--name-only", "HEAD"], cancellationToken).ConfigureAwait(false);
            var workingDiffFiles = workingDiffRes.ExitCode == 0 ? ParseLinePaths(workingDiffRes.StdOut) : [];

            var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var allModified = committedFiles
                .Concat(dirtyFiles)
                .Concat(workingDiffFiles)
                .Distinct(pathComparer)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

            var violations = EvaluateWriteScopeViolations(handle, allModified, spec.WriteScope);
            items.Add(new WorktreeInspectionItem(
                handle,
                worktreeRoot,
                branchName,
                headSha,
                HasUncommittedChanges: dirtyFiles.Length > 0,
                ModifiedFiles: allModified,
                DirtyFiles: dirtyFiles,
                ScopeViolations: violations,
                AheadCommitCount: aheadCount));
        }

        var overlaps = ComputeOverlaps(items);
        var allViolations = items.SelectMany(x => x.ScopeViolations).ToArray();
        var canIntegrate = missingHandles.Count == 0 && overlaps.Count == 0 && allViolations.Length == 0;

        var summary = BuildInspectionSummary(items, overlaps, allViolations, canIntegrate);
        return new WorktreeInspectionReport(
            canonicalTarget,
            items,
            overlaps,
            canIntegrate,
            summary);
    }

    public async Task<WorktreeIntegrationResult> IntegrateAsync(
        WorktreeIntegrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetPath);

        var canonicalTarget = WorkspacePathSpelling.Canonical(request.TargetPath);
        if (!Directory.Exists(canonicalTarget))
        {
            return new WorktreeIntegrationResult(
                Success: false,
                RolledBack: false,
                IntegratedHandles: [],
                ConflictFiles: [],
                ScopeViolations: [],
                MergeCommitSha: null,
                Message: $"Target repository '{canonicalTarget}' does not exist.");
        }

        var targetGate = TargetLocks.GetOrAdd(canonicalTarget, static _ => new SemaphoreSlim(1, 1));
        await targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.AbortOnly)
            {
                var aborted = await AbortInProgressOperationsAsync(canonicalTarget, cancellationToken).ConfigureAwait(false);
                var currentHead = await TryResolveHeadAsync(canonicalTarget, cancellationToken).ConfigureAwait(false);
                return new WorktreeIntegrationResult(
                    Success: true,
                    RolledBack: aborted,
                    IntegratedHandles: [],
                    ConflictFiles: [],
                    ScopeViolations: [],
                    MergeCommitSha: currentHead,
                    Message: aborted
                        ? "Aborted in-progress Git integration and restored clean target repository state."
                        : "No in-progress merge or cherry-pick was active in the target repository.");
            }

            if (request.Worktrees.Count == 0)
            {
                var head = await TryResolveHeadAsync(canonicalTarget, cancellationToken).ConfigureAwait(false);
                return new WorktreeIntegrationResult(
                    Success: true,
                    RolledBack: false,
                    IntegratedHandles: [],
                    ConflictFiles: [],
                    ScopeViolations: [],
                    MergeCommitSha: head,
                    Message: "No teammate worktrees were requested for integration.");
            }

            var preExistingConflicts = await GetUnmergedFilesAsync(canonicalTarget, cancellationToken).ConfigureAwait(false);
            if (preExistingConflicts.Length > 0)
            {
                return new WorktreeIntegrationResult(
                    Success: false,
                    RolledBack: false,
                    IntegratedHandles: [],
                    ConflictFiles: preExistingConflicts,
                    ScopeViolations: [],
                    MergeCommitSha: null,
                    Message: $"Target repository '{canonicalTarget}' already has unresolved Git conflicts: {string.Join(", ", preExistingConflicts)}.");
            }

            var inspection = await InspectAsync(canonicalTarget, request.Worktrees, request.TargetBranch, cancellationToken).ConfigureAwait(false);
            if (!inspection.CanIntegrateCleanly)
            {
                var overlapPaths = inspection.Overlaps.Select(o => o.RelativePath).ToArray();
                return new WorktreeIntegrationResult(
                    Success: false,
                    RolledBack: false,
                    IntegratedHandles: [],
                    ConflictFiles: overlapPaths,
                    ScopeViolations: inspection.AllScopeViolations,
                    MergeCommitSha: null,
                    Message: inspection.Summary);
            }

            if (!string.IsNullOrWhiteSpace(request.TargetBranch))
            {
                var desiredBranch = request.TargetBranch.Trim();
                var currentBranchRes = await RunGitAsync(canonicalTarget, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
                var currentBranch = currentBranchRes.ExitCode == 0 ? currentBranchRes.StdOut.Trim() : string.Empty;
                if (!string.Equals(currentBranch, desiredBranch, StringComparison.Ordinal))
                {
                    var checkoutRes = await RunGitAsync(canonicalTarget, ["checkout", desiredBranch], cancellationToken).ConfigureAwait(false);
                    if (checkoutRes.ExitCode != 0)
                    {
                        return new WorktreeIntegrationResult(
                            Success: false,
                            RolledBack: false,
                            IntegratedHandles: [],
                            ConflictFiles: [],
                            ScopeViolations: [],
                            MergeCommitSha: null,
                            Message: $"Failed to switch target repository to branch '{desiredBranch}': {FirstLine(checkoutRes.StdErr)}");
                    }
                }
            }

            var targetStatus = await RunGitAsync(canonicalTarget, ["-c", "core.quotepath=false", "status", "--porcelain=v1"], cancellationToken).ConfigureAwait(false);
            var targetDirty = ParsePorcelainPaths(targetStatus.StdOut);
            var teammateTouchedSet = new HashSet<string>(
                inspection.Items.SelectMany(i => i.ModifiedFiles),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var dirtyTargetCollisions = targetDirty.Where(teammateTouchedSet.Contains).ToArray();
            if (dirtyTargetCollisions.Length > 0)
            {
                return new WorktreeIntegrationResult(
                    Success: false,
                    RolledBack: false,
                    IntegratedHandles: [],
                    ConflictFiles: dirtyTargetCollisions,
                    ScopeViolations: [],
                    MergeCommitSha: null,
                    Message: $"Target workspace has uncommitted local edits colliding with teammate changes: {string.Join(", ", dirtyTargetCollisions)}.");
            }

            var strategy = WorktreeIntegrationStrategies.Normalize(request.Strategy);
            var prefix = string.IsNullOrWhiteSpace(request.CommitMessagePrefix)
                ? "feat(team)"
                : request.CommitMessagePrefix.Trim();
            var integratedHandles = new List<string>(inspection.Items.Count);
            var initialTargetHead = await TryResolveHeadAsync(canonicalTarget, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < inspection.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = inspection.Items[index];
                var spec = request.Worktrees[index];
                var worktreeHead = item.HeadCommitSha;

                if (item.HasUncommittedChanges)
                {
                    if (!request.AutoCommitDirtyWorktrees)
                    {
                        return new WorktreeIntegrationResult(
                            Success: false,
                            RolledBack: false,
                            IntegratedHandles: integratedHandles,
                            ConflictFiles: item.DirtyFiles,
                            ScopeViolations: [],
                            MergeCommitSha: await TryResolveHeadAsync(canonicalTarget, cancellationToken).ConfigureAwait(false),
                            Message: $"Worktree '{item.Handle}' has uncommitted files ({string.Join(", ", item.DirtyFiles)}) and AutoCommitDirtyWorktrees is false.");
                    }

                    var addRes = await RunGitAsync(item.WorktreePath, ["add", "-A"], cancellationToken).ConfigureAwait(false);
                    if (addRes.ExitCode != 0)
                    {
                        return new WorktreeIntegrationResult(
                            Success: false,
                            RolledBack: false,
                            IntegratedHandles: integratedHandles,
                            ConflictFiles: [],
                            ScopeViolations: [],
                            MergeCommitSha: await TryResolveHeadAsync(canonicalTarget, cancellationToken).ConfigureAwait(false),
                            Message: $"Failed to stage worktree '{item.Handle}' changes: {FirstLine(addRes.StdErr)}");
                    }

                    var commitMsg = $"{prefix}: snapshot worktree {item.Handle}";
                    var commitRes = await RunGitWithIdentityAsync(item.WorktreePath, ["commit", "-m", commitMsg], cancellationToken).ConfigureAwait(false);
                    if (commitRes.ExitCode != 0)
                    {
                        return new WorktreeIntegrationResult(
                            Success: false,
                            RolledBack: false,
                            IntegratedHandles: integratedHandles,
                            ConflictFiles: [],
                            ScopeViolations: [],
                            MergeCommitSha: await TryResolveHeadAsync(canonicalTarget, cancellationToken).ConfigureAwait(false),
                            Message: $"Failed to commit dirty worktree '{item.Handle}': {FirstLine(commitRes.StdErr)}");
                    }

                    worktreeHead = await TryResolveHeadAsync(item.WorktreePath, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(worktreeHead))
                    continue;

                var currentTargetHead = await TryResolveHeadAsync(canonicalTarget, cancellationToken).ConfigureAwait(false);
                if (string.Equals(currentTargetHead, worktreeHead, StringComparison.OrdinalIgnoreCase))
                    continue;

                var ancestorCheck = await RunGitAsync(canonicalTarget, ["merge-base", "--is-ancestor", worktreeHead, "HEAD"], cancellationToken).ConfigureAwait(false);
                if (ancestorCheck.ExitCode == 0)
                {
                    // Worktree HEAD is already an ancestor of target HEAD (nothing ahead to merge).
                    continue;
                }

                var stepResult = await IntegrateSingleWorktreeAsync(
                    canonicalTarget,
                    item.Handle,
                    worktreeHead,
                    strategy,
                    prefix,
                    cancellationToken).ConfigureAwait(false);

                if (!stepResult.Success)
                {
                    // If an earlier teammate in the same batch already committed onto TargetPath and targetDirty was empty,
                    // rewind TargetPath back to initialTargetHead so multi-handle integration is atomic!
                    if (integratedHandles.Count > 0 && targetDirty.Length == 0 && !string.IsNullOrWhiteSpace(initialTargetHead))
                    {
                        await RunGitAsync(canonicalTarget, ["reset", "--hard", initialTargetHead], cancellationToken).ConfigureAwait(false);
                    }

                    return new WorktreeIntegrationResult(
                        Success: false,
                        RolledBack: true,
                        IntegratedHandles: [],
                        ConflictFiles: stepResult.ConflictFiles,
                        ScopeViolations: [],
                        MergeCommitSha: initialTargetHead,
                        Message: stepResult.ErrorMessage);
                }

                integratedHandles.Add(item.Handle);
            }

            var finalHead = await TryResolveHeadAsync(canonicalTarget, cancellationToken).ConfigureAwait(false);
            var msg = integratedHandles.Count == 0
                ? "All inspected teammate worktrees were already up to date with the target branch."
                : $"Cleanly integrated {integratedHandles.Count} teammate worktree(s) ({string.Join(", ", integratedHandles)}) via '{strategy}' at {ShortSha(finalHead)}.";

            return new WorktreeIntegrationResult(
                Success: true,
                RolledBack: false,
                IntegratedHandles: integratedHandles,
                ConflictFiles: [],
                ScopeViolations: [],
                MergeCommitSha: finalHead,
                Message: msg);
        }
        finally
        {
            targetGate.Release();
        }
    }

    private static async Task<(bool Success, string[] ConflictFiles, string ErrorMessage)> IntegrateSingleWorktreeAsync(
        string targetPath,
        string handle,
        string worktreeHead,
        string strategy,
        string commitPrefix,
        CancellationToken ct)
    {
        var mergeTitle = $"{commitPrefix}: integrate teammate '{handle}'";
        if (strategy == WorktreeIntegrationStrategies.Squash)
        {
            var squashRes = await RunGitWithIdentityAsync(targetPath, ["merge", "--squash", worktreeHead], ct).ConfigureAwait(false);
            var conflicts = await GetUnmergedFilesAsync(targetPath, ct).ConfigureAwait(false);
            if (squashRes.ExitCode != 0 || conflicts.Length > 0)
            {
                await AbortInProgressOperationsAsync(targetPath, ct).ConfigureAwait(false);
                var conflictDetail = conflicts.Length > 0
                    ? $"conflicting files: {string.Join(", ", conflicts)}"
                    : FirstLine(squashRes.StdErr.Length > 0 ? squashRes.StdErr : squashRes.StdOut);
                return (false, conflicts, $"Squash integration of '{handle}' failed and was rolled back ({conflictDetail}).");
            }

            var stagedCheck = await RunGitAsync(targetPath, ["diff", "--cached", "--name-only"], ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stagedCheck.StdOut))
            {
                var commitRes = await RunGitWithIdentityAsync(targetPath, ["commit", "-m", mergeTitle], ct).ConfigureAwait(false);
                if (commitRes.ExitCode != 0)
                {
                    await RunGitAsync(targetPath, ["reset", "--merge"], ct).ConfigureAwait(false);
                    return (false, [], $"Failed to commit squashed integration for '{handle}': {FirstLine(commitRes.StdErr)}");
                }
            }

            return (true, [], string.Empty);
        }

        if (strategy == WorktreeIntegrationStrategies.CherryPick)
        {
            var mergeBaseRes = await RunGitAsync(targetPath, ["merge-base", "HEAD", worktreeHead], ct).ConfigureAwait(false);
            var baseSha = mergeBaseRes.ExitCode == 0 ? mergeBaseRes.StdOut.Trim() : string.Empty;
            var range = string.IsNullOrWhiteSpace(baseSha) ? worktreeHead : $"{baseSha}..{worktreeHead}";
            var revList = await RunGitAsync(targetPath, ["rev-list", "--reverse", "--no-merges", range], ct).ConfigureAwait(false);
            var commits = ParseLinePaths(revList.StdOut);
            if (commits.Length == 0)
                commits = [worktreeHead];

            var cpArgs = new List<string>(commits.Length + 1) { "cherry-pick" };
            cpArgs.AddRange(commits);
            var cpRes = await RunGitWithIdentityAsync(targetPath, cpArgs, ct).ConfigureAwait(false);
            var conflicts = await GetUnmergedFilesAsync(targetPath, ct).ConfigureAwait(false);
            if (cpRes.ExitCode != 0 || conflicts.Length > 0)
            {
                await AbortInProgressOperationsAsync(targetPath, ct).ConfigureAwait(false);
                var conflictDetail = conflicts.Length > 0
                    ? $"conflicting files: {string.Join(", ", conflicts)}"
                    : FirstLine(cpRes.StdErr.Length > 0 ? cpRes.StdErr : cpRes.StdOut);
                return (false, conflicts, $"Cherry-pick integration of '{handle}' failed and was rolled back ({conflictDetail}).");
            }

            return (true, [], string.Empty);
        }

        // Default: --no-ff merge
        var mergeRes = await RunGitWithIdentityAsync(targetPath, ["merge", "--no-ff", "--no-edit", "-m", mergeTitle, worktreeHead], ct).ConfigureAwait(false);
        var mergeConflicts = await GetUnmergedFilesAsync(targetPath, ct).ConfigureAwait(false);
        if (mergeRes.ExitCode != 0 || mergeConflicts.Length > 0)
        {
            await AbortInProgressOperationsAsync(targetPath, ct).ConfigureAwait(false);
            var conflictDetail = mergeConflicts.Length > 0
                ? $"conflicting files: {string.Join(", ", mergeConflicts)}"
                : FirstLine(mergeRes.StdErr.Length > 0 ? mergeRes.StdErr : mergeRes.StdOut);
            return (false, mergeConflicts, $"Merge integration of '{handle}' failed and was rolled back ({conflictDetail}).");
        }

        return (true, [], string.Empty);
    }

    private static async Task<bool> AbortInProgressOperationsAsync(string targetPath, CancellationToken ct)
    {
        var aborted = false;
        var mergeHead = await RunGitAsync(targetPath, ["rev-parse", "-q", "--verify", "MERGE_HEAD"], ct).ConfigureAwait(false);
        if (mergeHead.ExitCode == 0)
        {
            await RunGitAsync(targetPath, ["merge", "--abort"], ct).ConfigureAwait(false);
            aborted = true;
        }

        var cherryHead = await RunGitAsync(targetPath, ["rev-parse", "-q", "--verify", "CHERRY_PICK_HEAD"], ct).ConfigureAwait(false);
        if (cherryHead.ExitCode == 0)
        {
            await RunGitAsync(targetPath, ["cherry-pick", "--abort"], ct).ConfigureAwait(false);
            aborted = true;
        }

        var remainingUnmerged = await GetUnmergedFilesAsync(targetPath, ct).ConfigureAwait(false);
        if (remainingUnmerged.Length > 0 || !aborted)
        {
            // Clean up conflicted index/worktree from `git merge --squash` (which does not write MERGE_HEAD).
            if (remainingUnmerged.Length > 0)
            {
                await RunGitAsync(targetPath, ["reset", "--merge"], ct).ConfigureAwait(false);
                aborted = true;
            }
        }

        return aborted;
    }

    private static async Task<string[]> GetUnmergedFilesAsync(string repoPath, CancellationToken ct)
    {
        var res = await RunGitAsync(repoPath, ["-c", "core.quotepath=false", "diff", "--name-only", "--diff-filter=U"], ct).ConfigureAwait(false);
        return res.ExitCode == 0 ? ParseLinePaths(res.StdOut) : [];
    }

    private static async Task<string?> TryResolveHeadAsync(string repoPath, CancellationToken ct)
    {
        var res = await RunGitAsync(repoPath, ["rev-parse", "--verify", "HEAD"], ct).ConfigureAwait(false);
        return res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StdOut) ? res.StdOut.Trim() : null;
    }

    public static IReadOnlyList<string> EvaluateWriteScopeViolations(
        string handle,
        IReadOnlyList<string> modifiedFiles,
        IReadOnlyList<string>? writeScope)
    {
        if (modifiedFiles.Count == 0 || writeScope is null || writeScope.Count == 0)
            return [];

        var normalizedPatterns = writeScope
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeRelativePath)
            .Where(s => s.Length > 0)
            .ToArray();

        if (normalizedPatterns.Length == 0
            || normalizedPatterns.Any(p => p is "*" or "**" or "." or "./"))
        {
            return [];
        }

        var violations = new List<string>();
        foreach (var file in modifiedFiles)
        {
            var normalizedFile = NormalizeRelativePath(file);
            if (string.IsNullOrEmpty(normalizedFile))
                continue;

            if (!MatchesAnyScopePattern(normalizedFile, normalizedPatterns))
            {
                violations.Add($"{handle}: '{normalizedFile}' is outside write_scope [{string.Join(", ", normalizedPatterns)}]");
            }
        }

        return violations;
    }

    public static bool MatchesAnyScopePattern(string relativeFile, IReadOnlyList<string> normalizedPatterns)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var pattern in normalizedPatterns)
        {
            if (pattern is "*" or "**")
                return true;

            var trimmedDir = pattern.TrimEnd('/');
            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                if (string.Equals(relativeFile, trimmedDir, comparison)
                    || relativeFile.StartsWith(trimmedDir + "/", comparison))
                {
                    return true;
                }
                continue;
            }

            if (GlobMatches(relativeFile, pattern))
                return true;
        }

        return false;
    }

    private static bool GlobMatches(string candidate, string pattern)
    {
        var regexBuilder = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '*')
            {
                var isDoubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDoubleStar)
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        regexBuilder.Append("(?:.*/)?");
                    }
                    else
                    {
                        regexBuilder.Append(".*");
                    }
                }
                else
                {
                    regexBuilder.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                regexBuilder.Append("[^/]");
            }
            else
            {
                regexBuilder.Append(Regex.Escape(ch.ToString()));
            }
        }
        regexBuilder.Append('$');
        var options = OperatingSystem.IsWindows()
            ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            : RegexOptions.CultureInvariant;
        return Regex.IsMatch(candidate, regexBuilder.ToString(), options);
    }

    private static IReadOnlyList<WorktreeOverlapConflict> ComputeOverlaps(IReadOnlyList<WorktreeInspectionItem> items)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var map = new Dictionary<string, List<string>>(comparer);

        foreach (var item in items)
        {
            foreach (var path in item.ModifiedFiles)
            {
                var normalized = NormalizeRelativePath(path);
                if (string.IsNullOrEmpty(normalized)) continue;
                if (!map.TryGetValue(normalized, out var handles))
                {
                    handles = [];
                    map[normalized] = handles;
                }
                if (!handles.Contains(item.Handle, StringComparer.OrdinalIgnoreCase))
                    handles.Add(item.Handle);
            }
        }

        return map
            .Where(kvp => kvp.Value.Count > 1)
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new WorktreeOverlapConflict(kvp.Key, kvp.Value.ToArray()))
            .ToArray();
    }

    private static string BuildInspectionSummary(
        IReadOnlyList<WorktreeInspectionItem> items,
        IReadOnlyList<WorktreeOverlapConflict> overlaps,
        IReadOnlyList<string> scopeViolations,
        bool canIntegrate)
    {
        if (canIntegrate)
        {
            var totalFiles = items.Sum(i => i.ModifiedFiles.Count);
            return $"Preflight clean across {items.Count} worktree(s) ({totalFiles} modified file(s), 0 overlaps, 0 scope violations).";
        }

        var parts = new List<string>();
        if (overlaps.Count > 0)
        {
            var overlapDesc = string.Join("; ", overlaps.Select(o => $"{o.RelativePath} ({string.Join(" vs ", o.Handles)})"));
            parts.Add($"{overlaps.Count} multi-teammate file overlap(s): {overlapDesc}");
        }
        if (scopeViolations.Count > 0)
        {
            parts.Add($"{scopeViolations.Count} write_scope violation(s): {string.Join("; ", scopeViolations)}");
        }
        return "Worktree integration refused (fail-closed): " + string.Join(" | ", parts);
    }

    private static string[] ParsePorcelainPaths(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var results = new HashSet<string>(comparer);

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 4) continue;
            var payload = line[3..].Trim();
            if (payload.Length == 0) continue;

            var renameArrow = payload.IndexOf(" -> ", StringComparison.Ordinal);
            if (renameArrow > 0)
            {
                var before = NormalizeRelativePath(Unquote(payload[..renameArrow]));
                var after = NormalizeRelativePath(Unquote(payload[(renameArrow + 4)..]));
                if (before.Length > 0) results.Add(before);
                if (after.Length > 0) results.Add(after);
            }
            else
            {
                var single = NormalizeRelativePath(Unquote(payload));
                if (single.Length > 0) results.Add(single);
            }
        }

        return results.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static string[] ParseLinePaths(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => NormalizeRelativePath(Unquote(line.Trim())))
            .Where(line => line.Length > 0)
            .Distinct(comparer)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "git command failed";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return line.Length > 280 ? line[..280] : line;
    }

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "HEAD" : sha.Length > 8 ? sha[..8] : sha;

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunGitWithIdentityAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var fullArgs = new List<string>(args.Count + 4)
        {
            "-c", $"user.name={GitCommitterName}",
            "-c", $"user.email={GitCommitterEmail}",
        };
        fullArgs.AddRange(args);
        return RunGitAsync(workingDirectory, fullArgs, ct);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return (-1, string.Empty, "Failed to start git process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
