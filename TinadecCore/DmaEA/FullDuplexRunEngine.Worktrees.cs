using System.Text.Json;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

internal sealed partial class FullDuplexRunEngine
{
    private async Task<FullDuplexCheckpointV1> HandleTeamIntegrateAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode callerTask,
        WorkerToolTurn turn,
        IReadOnlyList<WorkerToolDescriptor> declared,
        CancellationToken cancellationToken)
    {
        if (!declared.Any(tool => CoreVirtualToolPolicy.IsTeamIntegrate(tool.ToolId)))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_integrate is not in your tool surface for this task.", checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (!TryParseJsonObject(turn.ArgumentsJson, out var args))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_integrate requires a JSON object with 'action' ('inspect' | 'integrate' | 'abort').",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var action = args.TryGetProperty("action", out var actionEl) && actionEl.ValueKind == JsonValueKind.String
            ? actionEl.GetString()?.Trim().ToLowerInvariant()
            : null;
        if (action is not ("inspect" or "integrate" or "abort"))
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_integrate requires 'action' to be one of: 'inspect', 'integrate', or 'abort'.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var targetRoot = checkpoint.SpaceWorktreePath ?? configuration.Workspace?.RootPath;
        if (string.IsNullOrWhiteSpace(targetRoot) || configuration.Workspace is not { IsGitRepository: true })
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                "team_integrate requires a bound Git repository workspace.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (_worktreeIntegrator is null)
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.ToolError,
                "Worktree integration service is unavailable in this host.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var requestedHandles = ReadStringArray(args, "handles");
        var strategy = args.TryGetProperty("strategy", out var stratEl) && stratEl.ValueKind == JsonValueKind.String
            ? WorktreeIntegrationStrategies.Normalize(stratEl.GetString())
            : WorktreeIntegrationStrategies.NoFf;
        var targetBranch = args.TryGetProperty("target_branch", out var tbEl) && tbEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tbEl.GetString())
            ? tbEl.GetString()!.Trim()
            : null;
        var runId = Guid.Parse(run.RunId);

        if (action == "abort")
        {
            var aborted = await _worktreeIntegrator.AbortAsync(targetRoot, cancellationToken).ConfigureAwait(false);
            turn.ResultJson = JsonSerializer.Serialize(new
            {
                ok = aborted.Success,
                action = "abort",
                rolled_back = aborted.RolledBack,
                message = aborted.Message
            });
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = aborted.Success;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-integrate-abort", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "team.worktree_aborted", aborted.Message, new
            {
                run_id = runId,
                task_id = callerTask.TaskId,
                success = aborted.Success,
                rolled_back = aborted.RolledBack
            }, cancellationToken, callerTask.TaskId, idempotencyKey: $"team-integrate:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);
            return checkpoint;
        }

        var specs = CollectRunWorktreeSpecs(checkpoint, requestedHandles, out var missingHandles);
        if (missingHandles.Count > 0)
        {
            return await FeedToolFailureBackAsync(run, callerTask, turn, RunErrorTaxonomy.InvalidToolArguments,
                $"team_integrate could not find isolated worktrees for handle(s): {string.Join(", ", missingHandles)}.",
                checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (specs.Count == 0)
        {
            turn.ResultJson = JsonSerializer.Serialize(new
            {
                ok = true,
                action,
                can_integrate_cleanly = true,
                worktree_count = 0,
                message = "No isolated teammate worktrees exist in this run."
            });
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = true;
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-integrate-empty", cancellationToken).ConfigureAwait(false);
        }

        if (action == "inspect")
        {
            var report = await _worktreeIntegrator.InspectAsync(targetRoot, specs, targetBranch, cancellationToken).ConfigureAwait(false);
            turn.ResultJson = JsonSerializer.Serialize(new
            {
                ok = true,
                action = "inspect",
                target_path = report.TargetPath,
                can_integrate_cleanly = report.CanIntegrateCleanly,
                summary = report.Summary,
                overlaps = report.Overlaps.Select(o => new { relative_path = o.RelativePath, handles = o.Handles }).ToArray(),
                scope_violations = report.AllScopeViolations,
                items = report.Items.Select(i => new
                {
                    handle = i.Handle,
                    worktree_path = i.WorktreePath,
                    branch_name = i.BranchName,
                    head_commit_sha = i.HeadCommitSha,
                    has_uncommitted_changes = i.HasUncommittedChanges,
                    ahead_commit_count = i.AheadCommitCount,
                    modified_files = i.ModifiedFiles,
                    dirty_files = i.DirtyFiles,
                    scope_violations = i.ScopeViolations
                }).ToArray()
            });
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = true;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-integrate-inspect", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "team.worktree_inspected", report.Summary, new
            {
                run_id = runId,
                task_id = callerTask.TaskId,
                can_integrate_cleanly = report.CanIntegrateCleanly,
                overlap_count = report.Overlaps.Count,
                scope_violation_count = report.AllScopeViolations.Count,
                handles = specs.Select(s => s.Handle).ToArray()
            }, cancellationToken, callerTask.TaskId, idempotencyKey: $"team-integrate:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);
            return checkpoint;
        }

        // action == "integrate"
        var preInspection = await _worktreeIntegrator.InspectAsync(targetRoot, specs, targetBranch, cancellationToken).ConfigureAwait(false);
        var integration = await _worktreeIntegrator.IntegrateAsync(new WorktreeIntegrationRequest(
            targetRoot,
            specs,
            strategy,
            targetBranch,
            AutoCommitDirtyWorktrees: true,
            CommitMessagePrefix: $"chore(team): integrate run {runId.ToString("N")[..8]}"), cancellationToken).ConfigureAwait(false);

        if (integration.Success)
        {
            var confirmMsg = $"Integrated worktree(s) [{string.Join(", ", integration.IntegratedHandles)}] via {strategy}"
                + (string.IsNullOrWhiteSpace(integration.MergeCommitSha) ? "." : $" (commit {integration.MergeCommitSha}).");
            AppendBoardMessage(checkpoint, "coordinator", "all", "update", confirmMsg, wake: false);
            turn.ResultJson = JsonSerializer.Serialize(new
            {
                ok = true,
                action = "integrate",
                strategy,
                integrated_handles = integration.IntegratedHandles,
                merge_commit_sha = integration.MergeCommitSha,
                message = integration.Message
            });
            turn.DispatchStatus = ToolDispatchStatus.Completed;
            turn.ToolSuccess = true;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-integrate-merged", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "team.worktree_integrated", integration.Message, new
            {
                run_id = runId,
                task_id = callerTask.TaskId,
                strategy,
                integrated_handles = integration.IntegratedHandles,
                merge_commit_sha = integration.MergeCommitSha
            }, cancellationToken, callerTask.TaskId, idempotencyKey: $"team-integrate:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);
            return checkpoint;
        }

        // Conflict or scope violation: target repo was rolled back cleanly; post exact diagnostic
        // facts onto the shared board and wake affected persistent teammates for rework!
        var affectedHandles = ResolveAffectedConflictHandles(specs, preInspection, integration);
        var conflictDetail = BuildIntegrationConflictDiagnostic(integration, preInspection, affectedHandles);
        var boardEntry = AppendBoardMessage(checkpoint, "coordinator",
            affectedHandles.Count == 1 ? affectedHandles[0] : "all",
            "review",
            conflictDetail,
            wake: true);

        var wokenHandles = new List<string>();
        foreach (var handle in affectedHandles)
        {
            if (WakeTeammateFromBoard(checkpoint, callerTask.TaskId, handle, boardEntry.Seq, conflictDetail))
            {
                wokenHandles.Add(handle);
            }
        }

        turn.ResultJson = JsonSerializer.Serialize(new
        {
            ok = false,
            action = "integrate",
            strategy,
            rolled_back = integration.RolledBack,
            conflict_files = integration.ConflictFiles,
            scope_violations = integration.ScopeViolations,
            affected_handles = affectedHandles,
            woken_handles = wokenHandles,
            message = integration.Message
        });
        turn.DispatchStatus = ToolDispatchStatus.Completed;
        turn.ToolSuccess = false;
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "team-integrate-conflict", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "team.worktree_integration_conflict", integration.Message, new
        {
            run_id = runId,
            task_id = callerTask.TaskId,
            rolled_back = integration.RolledBack,
            conflict_files = integration.ConflictFiles,
            scope_violations = integration.ScopeViolations,
            woken_handles = wokenHandles
        }, cancellationToken, callerTask.TaskId, idempotencyKey: $"team-integrate:{callerTask.TaskId}:{turn.CallId}").ConfigureAwait(false);
        return checkpoint;
    }

    internal static IReadOnlyList<TeammateWorktreeSpec> CollectRunWorktreeSpecs(
        FullDuplexCheckpointV1 checkpoint,
        IReadOnlyList<string> requestedHandles,
        out List<string> missingHandles)
    {
        missingHandles = [];
        var byHandle = new Dictionary<string, TeammateWorktreeSpec>(StringComparer.OrdinalIgnoreCase);

        foreach (var tm in checkpoint.Teammates)
        {
            if (string.IsNullOrWhiteSpace(tm.Handle) || string.IsNullOrWhiteSpace(tm.WorktreePath)) continue;
            byHandle[tm.Handle] = new TeammateWorktreeSpec(tm.Handle, tm.WorktreePath, tm.BranchName, tm.WriteScope);
        }
        foreach (var task in checkpoint.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskWorktreePath)) continue;
            var handle = task.TeammateHandle ?? task.WorkerHandle ?? task.TaskKey;
            if (!byHandle.ContainsKey(handle))
            {
                byHandle[handle] = new TeammateWorktreeSpec(
                    handle,
                    task.TaskWorktreePath,
                    ResolveOwnWorktreeBranch(task),
                    task.WriteScope);
            }
        }

        if (requestedHandles.Count == 0)
            return byHandle.Values.ToArray();

        var selected = new List<TeammateWorktreeSpec>(requestedHandles.Count);
        foreach (var raw in requestedHandles)
        {
            var handle = raw.Trim();
            if (handle.Length == 0) continue;
            if (byHandle.TryGetValue(handle, out var spec))
            {
                if (!selected.Any(s => string.Equals(s.Handle, spec.Handle, StringComparison.OrdinalIgnoreCase)))
                    selected.Add(spec);
            }
            else
            {
                missingHandles.Add(handle);
            }
        }
        return selected;
    }

    private static IReadOnlyList<string> ResolveAffectedConflictHandles(
        IReadOnlyList<TeammateWorktreeSpec> specs,
        WorktreeInspectionReport preInspection,
        WorktreeIntegrationResult result)
    {
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var overlap in preInspection.Overlaps)
        {
            foreach (var h in overlap.Handles) affected.Add(h);
        }
        foreach (var item in preInspection.Items)
        {
            if (item.ScopeViolations.Count > 0) affected.Add(item.Handle);
            if (result.ConflictFiles.Count > 0
                && item.ModifiedFiles.Concat(item.DirtyFiles).Any(f =>
                    result.ConflictFiles.Contains(f, StringComparer.OrdinalIgnoreCase)))
            {
                affected.Add(item.Handle);
            }
        }
        if (affected.Count == 0)
        {
            foreach (var spec in specs) affected.Add(spec.Handle);
        }
        return affected.ToArray();
    }

    private static string BuildIntegrationConflictDiagnostic(
        WorktreeIntegrationResult result,
        WorktreeInspectionReport preInspection,
        IReadOnlyList<string> affectedHandles)
    {
        var parts = new List<string>
        {
            $"[Worktree Integration Blocked — Target Repository Clean (rolled_back={result.RolledBack.ToString().ToLowerInvariant()})]",
            $"Reason: {result.Message}"
        };
        if (result.ConflictFiles.Count > 0)
            parts.Add($"Conflict files: {string.Join(", ", result.ConflictFiles)}");
        if (result.ScopeViolations.Count > 0)
            parts.Add($"Write-scope violations: {string.Join(", ", result.ScopeViolations)}");
        if (preInspection.Overlaps.Count > 0)
            parts.Add("Overlapping files across teammates: "
                + string.Join("; ", preInspection.Overlaps.Select(o => $"{o.RelativePath} ({string.Join(" vs ", o.Handles)})")));
        if (affectedHandles.Count > 0)
            parts.Add($"Action required from {string.Join(", ", affectedHandles.Select(h => "@" + h))}: revert out-of-scope edits or reconcile conflicting hunks inside your assigned worktree, commit cleanly, and report via team_message.");
        return string.Join("\n", parts);
    }

    private static (string? Path, string? Branch) ResolveTaskWorktree(FullDuplexCheckpointV1 checkpoint, DurableTaskNode task)
    {
        if (!string.IsNullOrWhiteSpace(task.TaskWorktreePath))
            return (task.TaskWorktreePath, ResolveOwnWorktreeBranch(task));
        if (!string.IsNullOrWhiteSpace(task.TeammateHandle))
        {
            var tm = checkpoint.Teammates.FirstOrDefault(t =>
                string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase));
            if (tm is not null && !string.IsNullOrWhiteSpace(tm.WorktreePath))
                return (tm.WorktreePath, tm.BranchName);
        }
        if (task.DispatchedByTaskId is not null)
        {
            var byId = new Dictionary<Guid, DurableTaskNode>();
            foreach (var node in checkpoint.Tasks)
                byId.TryAdd(node.TaskId, node);
            var visited = new HashSet<Guid> { task.TaskId };
            var current = task.TaskId;
            while (byId.TryGetValue(current, out var cur)
                   && cur.DispatchedByTaskId is { } parentId
                   && visited.Add(parentId))
            {
                if (byId.TryGetValue(parentId, out var parentNode) && !string.IsNullOrWhiteSpace(parentNode.TaskWorktreePath))
                    return (parentNode.TaskWorktreePath, ResolveOwnWorktreeBranch(parentNode));
                current = parentId;
            }
        }
        return (null, null);
    }

    internal static string? ResolveOwnWorktreeBranch(DurableTaskNode item)
    {
        if (!string.IsNullOrWhiteSpace(item.TaskWorktreeBranch))
            return item.TaskWorktreeBranch;
        for (var i = item.ToolTurns.Count - 1; i >= 0; i--)
        {
            var turn = item.ToolTurns[i];
            if (!string.Equals(turn.ToolId, "git_worktree_create", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryParseJsonObject(turn.ResultJson ?? "null", out var res)
                && res.TryGetProperty("branch", out var br) && br.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(br.GetString()))
                return br.GetString();
            if (TryParseJsonObject(turn.ArgumentsJson, out var args)
                && args.TryGetProperty("branch", out var argBr) && argBr.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(argBr.GetString()))
                return argBr.GetString();
        }
        return null;
    }

    private async Task<string?> AcquireWriteScopeAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        Guid runId,
        DurableTaskNode task,
        Guid? workerId,
        CancellationToken cancellationToken)
    {
        // Tasks isolated in their own Git worktree write to their private worktree checkout,
        // not the shared main checkout; cross-worktree overlaps and scope boundaries are
        // enforced deterministically at ACP permission check and IWorktreeIntegratorService.
        if (task.Worktree || !string.IsNullOrWhiteSpace(task.TaskWorktreePath))
            return null;
        if (_resourceLeases is null || task.WriteScope is not { Count: > 0 } scope)
            return null;

        Guid? sessionId = Guid.TryParse(run.SessionId, out var parsedSession) ? parsedSession : null;
        foreach (var claim in ResourceClaimResolver.ResolveWriteScope(scope, configuration.Workspace?.RootPath))
        {
            var decision = await _resourceLeases.AcquireAsync(new ResourceAcquireRequest(
                claim, sessionId, runId, task.TaskId, workerId,
                $"Declared write scope of task '{task.TaskKey}'.", ResourceLeasePurposes.WriteScope), cancellationToken).ConfigureAwait(false);
            if (decision.Granted) continue;
            await _resourceLeases.ReleaseTaskAsync(runId, task.TaskId, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, ResourceLeaseMessages.ConflictEventType,
                $"Task '{task.TaskKey}' could not start: its write scope overlaps a resource another run holds.",
                ResourceLeaseMessages.ConflictPayload(claim, decision.Conflicts, runId, task.TaskId, ResourceLeaseMessages.DetectedAtWriteScope), cancellationToken).ConfigureAwait(false);
            await RaiseStandingAsync(run, configuration, GovernanceTopics.LeaseConflict,
                $"Task '{task.TaskKey}' could not start (write_scope): {ResourceLeaseMessages.Describe(claim, decision.Conflicts)}",
                "task", task.TaskId.ToString("N"), cancellationToken).ConfigureAwait(false);
            return ResourceLeaseMessages.Describe(claim, decision.Conflicts)
                + " Dispatch it again once that work is done, give it a separate worktree, or narrow its write_scope.";
        }
        return null;
    }

    private async Task ReleaseRunLeasesAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (_resourceLeases is null) return;
        try
        {
            await _resourceLeases.ReleaseRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Releasing resource leases for run {RunId} failed.", runId);
        }
    }
}
