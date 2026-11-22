using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.DmaEA;

internal sealed partial class FullDuplexRunEngine
{
    private const int MaxTeammateTranscriptSummaryChars = 2400;
    private const int MaxTeammateOutcomeSummaryChars = 1200;

    private readonly ConcurrentDictionary<(Guid RunId, string Handle), ScopedTeamMcpLease> _runTeammateMcpLeases = new();

    private async Task<FullDuplexCheckpointV1> ExecuteReadyTasksAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);

        var pending = checkpoint.Tasks.FirstOrDefault(HasLivePendingExecution);
        if (pending is not null)
        {
            var resumed = await ResumePendingToolAsync(run, configuration, checkpoint, pending, cancellationToken).ConfigureAwait(false);
            checkpoint = resumed.Checkpoint;
            if (resumed.Waiting)
            {
                throw new RunAwaitingExternalDecisionException();
            }
            if (resumed.Result is not null)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, resumed.Result, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-resumed", cancellationToken).ConfigureAwait(false);
            }
        }

        MarkBlockedDescendants(checkpoint.Tasks);
        var ready = checkpoint.Tasks
            .Where(item => IsDispatchable(item, checkpoint))
            .Where(item => item.Dependencies.All(dependency =>
                checkpoint.Tasks.Any(other => other.TaskKey == dependency && other.Status == "completed")))
            .Take(Math.Max(1, configuration.Spawn.MaxParallelWorkers))
            .ToList();

        if (ready.Count == 0)
        {
            if (string.Equals(checkpoint.Phase, "awaiting_user", StringComparison.Ordinal))
                throw new RunAwaitingExternalDecisionException();

            if (checkpoint.Tasks.All(item => item.Status is "completed" or "failed" or "blocked"))
            {
                if (configuration.SpaceOptions?.WorkflowModeVersionId is not null
                    && checkpoint.SpacePreparationComplete
                    && checkpoint.Tasks.Any(item => item.IsSpacePreparation))
                {
                    checkpoint.Tasks.Clear();
                    checkpoint.PlanRevision = 0;
                    checkpoint.Phase = "planning";
                    return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "space-prepared", cancellationToken).ConfigureAwait(false);
                }
                checkpoint.Phase = "reviewing";
                return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "execution-complete", cancellationToken).ConfigureAwait(false);
            }

            if (checkpoint.Tasks.Any(HasLivePendingExecution))
            {
                throw new RunAwaitingExternalDecisionException();
            }

            var waitingTasks = checkpoint.Tasks.Where(item => item.Status == "waiting").ToList();
            if (waitingTasks.Count > 0)
            {
                var deadlines = waitingTasks
                    .Where(item => item.TeamWaitDeadline.HasValue)
                    .Select(item => item.TeamWaitDeadline!.Value)
                    .ToArray();
                if (deadlines.Length > 0)
                {
                    var earliest = deadlines.Min();
                    ScheduleModelRetryInMemory(runId, earliest);
                    throw new RunAwaitingExternalDecisionException();
                }
            }

            await FailRunAsync(runId, checkpoint, "invalid_task_graph", "No dependency-ready task remains in the persisted graph.", cancellationToken).ConfigureAwait(false);
            return checkpoint;
        }

        foreach (var task in ready)
        {
            if (task.Status != "waiting") task.Attempt++;
            task.Status = "running";
            task.InputContextRevision = checkpoint.ContextRevision;
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-dispatched", cancellationToken).ConfigureAwait(false);

        var plannerId = checkpoint.PlannerAgentId ?? throw new InvalidDataException("Planner instance is missing from checkpoint.");
        var workers = new Dictionary<Guid, RuntimeAgentInstance>();
        foreach (var task in ready)
        {
            if (await AssignWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false) is { } assigned)
            {
                workers[task.TaskId] = assigned;
                var (wtPath, wtBranch) = ResolveTaskWorktree(checkpoint, task);
                await TrySyncTaskTransitionToSessionPlanAsync(
                    checkpoint.SessionId, runId, task, summary: null, evidence: null,
                    worktreePath: wtPath, worktreeBranch: wtBranch, cancellationToken).ConfigureAwait(false);
            }
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "workers-assigned", cancellationToken).ConfigureAwait(false);

        var surfaces = new Dictionary<Guid, IReadOnlyList<WorkerToolDescriptor>>();
        foreach (var task in ready)
        {
            if (!workers.TryGetValue(task.TaskId, out var worker)) continue;
            try
            {
                surfaces[task.TaskId] = await GetWorkerToolsAsync(run, configuration, task, worker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint,
                    FailedTaskResult(task, worker.Id, ToolManifestUnavailableCategory, SafeError(ex)), cancellationToken).ConfigureAwait(false);
            }
        }

        var textOnly = ready.Where(task => !task.IsSpacePreparation && surfaces.TryGetValue(task.TaskId, out var tools) && tools.Count == 0).ToList();
        var toolCapable = ready.Where(task => surfaces.TryGetValue(task.TaskId, out var tools) && (tools.Count != 0 || task.IsSpacePreparation)).ToList();
        var results = await Task.WhenAll(textOnly.Select(task => ExecuteTextTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken))).ConfigureAwait(false);
        var contextChanged = await ApplyPendingContextPatchesAsync(run, checkpoint, cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, result.Usage);
            if (!contextChanged)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var staleTask = checkpoint.Tasks.FirstOrDefault(item => item.TaskId == result.TaskId);
                if (staleTask is not null)
                {
                    staleTask.StaleEvidence.Add(new StaleTaskEvidence(
                        staleTask.InputContextRevision,
                        result.Status,
                        result.Result.Summary,
                        result.Result.Evidence,
                        DateTimeOffset.UtcNow));
                    staleTask.Status = "pending";
                    staleTask.ResultStatus = null;
                    staleTask.ResultSummary = null;
                    staleTask.Evidence = [];
                    staleTask.CompletedAt = null;
                }
            }
        }

        if (contextChanged)
        {
            foreach (var task in toolCapable)
            {
                task.Status = "pending";
                task.ResultStatus = null;
                task.ResultSummary = null;
                task.Evidence = [];
                task.CompletedAt = null;
            }
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-stale-after-context", cancellationToken).ConfigureAwait(false);
        }

        var parallelToolTasks = toolCapable
            .Where(task => task.ToolTurns.Count == 0 && string.IsNullOrWhiteSpace(task.PendingToolExecutionId))
            .Where(task => !surfaces[task.TaskId].Any(tool =>
                CoreVirtualToolPolicy.IsTaskDispatch(tool.ToolId)
                || CoreVirtualToolPolicy.IsTaskWait(tool.ToolId)
                || CoreVirtualToolPolicy.IsTeamSpawn(tool.ToolId)
                || CoreVirtualToolPolicy.IsTeamWait(tool.ToolId)
                || CoreVirtualToolPolicy.IsTeamIntegrate(tool.ToolId)))
            .Where(task => !task.IsSpacePreparation)
            .ToList();
        var serialToolTasks = toolCapable.Except(parallelToolTasks).ToList();
        var usageBeforeParallel = checkpoint.ModelUsage;
        var boardWatermarkBeforeParallel = checkpoint.NextTeamBoardSeq;

        ToolTaskExecutionResult[] parallelResults = parallelToolTasks.Count == 0
            ? []
            : await Task.WhenAll(parallelToolTasks.Select(task => ExecuteParallelToolTaskAsync(
                run, configuration, checkpoint, plannerId, task, cancellationToken, surfaces[task.TaskId]))).ConfigureAwait(false);

        var parallelWaiting = false;
        var correctionMerged = false;
        foreach (var result in parallelResults)
        {
            correctionMerged |= MergeParallelTaskCheckpoint(
                checkpoint,
                result.Checkpoint,
                result.TaskId,
                boardWatermarkBeforeParallel,
                configuration.RuntimePolicy,
                checkpoint.UserGoal);
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage,
                Maf18RuntimeAdapter.UsageSince(result.Checkpoint.ModelUsage, usageBeforeParallel));
            if (result.Waiting)
            {
                parallelWaiting = true;
                PropagateExpiryPark(checkpoint, result.Checkpoint);
                continue;
            }
            if (result.Result is not null)
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result.Result, cancellationToken).ConfigureAwait(false);
        }
        if (correctionMerged && checkpoint.Correction is not null)
            await AppendCorrectionTraceEventAsync(run, checkpoint.Correction, cancellationToken).ConfigureAwait(false);
        if (parallelWaiting)
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken).ConfigureAwait(false);
            throw new RunAwaitingExternalDecisionException();
        }

        foreach (var task in serialToolTasks)
        {
            var result = await ExecuteToolTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken, surfaces[task.TaskId]).ConfigureAwait(false);
            checkpoint = result.Checkpoint;
            if (result.Waiting)
            {
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            if (result.Result is not null)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result.Result, cancellationToken).ConfigureAwait(false);
            }
        }

        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-completed", cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolTaskExecutionResult> ExecuteParallelToolTaskAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken,
        IReadOnlyList<WorkerToolDescriptor> descriptors)
    {
        var local = CloneCheckpoint(checkpoint);
        var localTask = local.Tasks.Single(item => item.TaskId == task.TaskId);
        var prior = _deferParallelTaskCheckpointWrites.Value;
        _deferParallelTaskCheckpointWrites.Value = true;
        try
        {
            var result = await ExecuteToolTaskAsync(run, configuration, local, plannerId, localTask, cancellationToken, descriptors).ConfigureAwait(false);
            return result with { TaskId = task.TaskId };
        }
        finally
        {
            _deferParallelTaskCheckpointWrites.Value = prior;
        }
    }

    private static FullDuplexCheckpointV1 CloneCheckpoint(FullDuplexCheckpointV1 checkpoint)
    {
        var clone = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(JsonSerializer.Serialize(checkpoint, JsonOptions), JsonOptions)
            ?? throw new InvalidDataException("Could not clone the run checkpoint for a parallel worker.");
        clone.CheckpointRevision = checkpoint.CheckpointRevision;
        return clone;
    }

    private static bool MergeParallelTaskCheckpoint(
        FullDuplexCheckpointV1 target,
        FullDuplexCheckpointV1 source,
        Guid taskId,
        long baseBoardSeq,
        ModeRuntimePolicyDto? runtimePolicy,
        string goal)
    {
        var updated = source.Tasks.FirstOrDefault(item => item.TaskId == taskId)
            ?? throw new InvalidDataException($"Parallel worker checkpoint lost task {taskId}.");
        var index = target.Tasks.FindIndex(item => item.TaskId == taskId);
        if (index < 0) throw new InvalidDataException($"Parallel worker task {taskId} is missing from the owner checkpoint.");
        target.Tasks[index] = updated;

        if (!string.IsNullOrWhiteSpace(updated.TeammateHandle))
        {
            var sourceTeammate = source.Teammates.FirstOrDefault(t =>
                string.Equals(t.Handle, updated.TeammateHandle, StringComparison.OrdinalIgnoreCase));
            var targetTeammate = target.Teammates.FirstOrDefault(t =>
                string.Equals(t.Handle, updated.TeammateHandle, StringComparison.OrdinalIgnoreCase));
            if (sourceTeammate is not null && targetTeammate is not null)
            {
                targetTeammate.WorktreePath = updated.TaskWorktreePath ?? sourceTeammate.WorktreePath ?? targetTeammate.WorktreePath;
                targetTeammate.BranchName = ResolveOwnWorktreeBranch(updated) ?? sourceTeammate.BranchName ?? targetTeammate.BranchName;
                targetTeammate.RuntimeBadge = sourceTeammate.RuntimeBadge ?? targetTeammate.RuntimeBadge;
                targetTeammate.ProviderInstanceId = sourceTeammate.ProviderInstanceId ?? targetTeammate.ProviderInstanceId;
                targetTeammate.ModelId = sourceTeammate.ModelId ?? targetTeammate.ModelId;
                targetTeammate.LastDeliveredBoardSeq = Math.Max(targetTeammate.LastDeliveredBoardSeq, sourceTeammate.LastDeliveredBoardSeq);
            }
        }

        foreach (var msg in source.TeamBoard.Where(m => m.Seq > baseBoardSeq))
        {
            var mergedEntry = AppendBoardMessage(target, msg.Sender, msg.Target, msg.Kind, msg.Content, msg.Wake);
            if (msg.Wake && !string.Equals(msg.Target, "coordinator", StringComparison.OrdinalIgnoreCase))
            {
                var senderTm = target.Teammates.FirstOrDefault(t =>
                    string.Equals(t.Handle, msg.Sender, StringComparison.OrdinalIgnoreCase));
                var targets = string.Equals(msg.Target, "all", StringComparison.OrdinalIgnoreCase)
                    ? target.Teammates.Where(t => !string.Equals(t.Handle, msg.Sender, StringComparison.OrdinalIgnoreCase)).Select(t => t.Handle).ToArray()
                    : [msg.Target];
                foreach (var wakeHandle in targets)
                {
                    WakeTeammateFromBoard(target, updated.DispatchedByTaskId, wakeHandle, mergedEntry.Seq, msg.Content, senderTm?.RelayDepth);
                }
            }
        }

        if (target.Correction is null || source.Correction is null)
            return false;
        if (runtimePolicy is null)
            throw new InvalidDataException("A correction checkpoint is missing its frozen runtime policy.");
        target.Correction = CorrectionTracePolicy.Merge(target.Correction, source.Correction);
        CorrectionTracePolicy.EvaluateCompletion(
            target.Correction,
            runtimePolicy,
            goal,
            target.Correction.Completion.ModifiedArtifacts.Count > 0);
        return true;
    }

    internal static void PropagateExpiryPark(FullDuplexCheckpointV1 target, FullDuplexCheckpointV1 source)
    {
        if (!source.AwaitingApprovalExpiryReview) return;
        target.AwaitingApprovalExpiryReview = true;
        target.Phase = "awaiting_user";
    }

    private async Task<RuntimeAgentInstance?> AssignWorkerAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        RuntimeAgentInstance worker;
        try
        {
            worker = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerAssignmentException ex)
        {
            await ApplyTaskResultAsync(run, configuration, runId, checkpoint,
                FailedTaskResult(task, task.WorkerAgentId, WorkerAssignmentInvalidCategory, SafeError(ex)), cancellationToken).ConfigureAwait(false);
            return null;
        }
        var refusal = await AcquireWriteScopeAsync(run, configuration, runId, task, worker.Id, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            await ApplyTaskResultAsync(run, configuration, runId, checkpoint,
                FailedTaskResult(task, task.WorkerAgentId, RunErrorTaxonomy.ResourceConflict, refusal), cancellationToken).ConfigureAwait(false);
            return null;
        }
        await EnrolWorkerAsync(run, configuration, checkpoint, task, worker, cancellationToken).ConfigureAwait(false);
        return worker;
    }

    private async Task<RuntimeAgentInstance> GetOrCreateWorkerAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var graph = configuration.Graph;
        var instances = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);

        var spawnable = ResolvePersistedSpawnable(configuration, task);
        var soloMaster = ResolveSoloMaster(configuration, graph, task);
        WorkerSelection selected;
        if (soloMaster is not null)
        {
            selected = soloMaster;
        }
        else if (spawnable is not null)
        {
            selected = new WorkerSelection(SpawnableDefinition(spawnable, configuration), task.WorkerAssignmentReason ?? "spawnable_whitelist");
        }
        else if (!string.IsNullOrWhiteSpace(task.RequestedAgent))
        {
            (selected, spawnable) = ResolveRequestedWorker(configuration, task, checkpoint.Tasks);
        }
        else
        {
            try
            {
                selected = ResolveOrSelectWorker(configuration, task);
            }
            catch (WorkerUnavailableException)
            {
                await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.CapabilityMissing, run, configuration, checkpoint, cancellationToken,
                    $"No executor covers task '{task.TaskKey}' (tools: {string.Join(", ", task.RequiredTools)}; capabilities: {string.Join(", ", task.RequiredCapabilities)}).",
                    "task", task.TaskId.ToString("N")).ConfigureAwait(false);
                var coverage = GraphSpawnAuthority.FindCoverage(graph, task.RequiredTools, task.RequiredCapabilities);
                if (coverage is null) throw;
                var tierCarriesSpawnAuthority = graph is not null
                    && GraphSpawnAuthority.CarriesSpawnAuthority(graph.Tier);
                if (!tierCarriesSpawnAuthority)
                    throw new WorkerAssignmentException(
                        $"Task '{task.TaskKey}' matches spawnable template '{coverage.Slug}', but the {graph?.Tier ?? "declared-graph"} tier denies spawn (graph_tier_spawn_denied).");
                spawnable = coverage;
                selected = new WorkerSelection(SpawnableDefinition(spawnable, configuration), "spawnable_whitelist");
            }
            catch (InvalidDataException ex)
            {
                throw new WorkerAssignmentException(ex.Message, ex);
            }
        }

        var dispatcherSlug = ResolveDispatcherAgentSlug(checkpoint.Tasks, task);
        if (spawnable is null && soloMaster is null && graph is not null
            && !GraphEdgeAuthority.IsDispatchAllowed(graph, selected.Agent.Id, dispatcherSlug))
        {
            throw new WorkerAssignmentException(
                $"Task '{task.TaskKey}' worker '{selected.Agent.Id}' is not a declared dispatch target of the mode graph (tier {graph.Tier}).");
        }

        var boundTeammate = !string.IsNullOrWhiteSpace(task.TeammateHandle)
            ? checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase))
            : null;
        var isTeammateWakeContinuation = boundTeammate is { RelayDepth: > 0, AgentInstanceId: not null };
        if (!isTeammateWakeContinuation)
        {
            EnsureGraphWorkerBudget(instances, configuration.Spawn.MaxAgentsPerRun, task.TaskKey);
        }

        var lineage = ResolveDispatcherLineage(instances, checkpoint.Tasks, task);
        EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, configuration.Spawn.MaxDepth, task.TaskKey);

        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug))
        {
            task.WorkerAgentSlug = selected.Agent.Id;
            task.WorkerAgentDefinitionId = selected.Agent.AgentDefinitionId;
            task.WorkerAgentVersionId = selected.Agent.AgentVersionId;
            task.WorkerAgentVersionHash = selected.Agent.VersionContentHash;
            task.WorkerAssignmentReason = selected.Reason;
            task.WorkerHandle = !string.IsNullOrWhiteSpace(task.TeammateHandle)
                ? task.TeammateHandle
                : NextWorkerHandle(checkpoint.Tasks, selected.Agent.Id);
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-selected", cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "worker.assigned", $"Task assigned to {task.WorkerHandle}.", new
            {
                task_id = task.TaskId,
                task_key = task.TaskKey,
                title = task.Title,
                handle = task.WorkerHandle,
                teammate_handle = task.TeammateHandle,
                teammate_mode = task.TeammateMode,
                requested_agent = task.RequestedAgent,
                agent_slug = selected.Agent.Id,
                agent_definition_id = selected.Agent.AgentDefinitionId,
                agent_version_id = selected.Agent.AgentVersionId,
                agent_version_hash = selected.Agent.VersionContentHash,
                reason = selected.Reason,
                required_capabilities = task.RequiredCapabilities,
                required_tools = task.RequiredTools
            }, cancellationToken, task.TaskId).ConfigureAwait(false);
        }

        if (task.WorkerAgentId is { } assigned)
        {
            var existing = instances.FirstOrDefault(item => item.Id == assigned);
            if (existing is not null)
            {
                VerifyWorkerInstance(existing, selected.Agent, task);
                if (boundTeammate is not null)
                {
                    boundTeammate.AgentInstanceId = existing.Id;
                    boundTeammate.CurrentTaskId = task.TaskId;
                }
                return existing;
            }
        }

        var worker = instances.FirstOrDefault(item => item.Generated
            && item.TaskId == task.TaskId
            && item.AgentVersionId == selected.Agent.AgentVersionId
            && item.Status is "created" or "running");
        if (worker is null)
        {
            if (selected.Agent.AgentDefinitionId is not { } definitionId
                || selected.Agent.AgentVersionId is not { } versionId
                || string.IsNullOrWhiteSpace(selected.Agent.VersionContentHash))
                throw new WorkerAssignmentException($"Frozen specialist '{selected.Agent.Id}' has no immutable version binding.");

            IReadOnlyList<string> seedTools;
            if (spawnable is not null)
            {
                var required = task.RequiredTools
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var outside = spawnable.ToolCeiling.Contains("*", StringComparer.Ordinal)
                    ? []
                    : required.Where(tool => !spawnable.ToolCeiling.Contains(tool, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (outside.Length > 0)
                    throw new WorkerAssignmentException(
                        $"Task '{task.TaskKey}' requires tools outside the spawnable template '{spawnable.Slug}' ceiling: {string.Join(", ", outside)} (spawn_tool_ceiling_exceeded).");
                seedTools = required.Length > 0 ? required : spawnable.ToolCeiling;
            }
            else
            {
                seedTools = selected.Agent.AllowedTools;
            }

            if (task.Worktree
                && string.IsNullOrWhiteSpace(task.TaskWorktreePath)
                && !seedTools.Contains("*", StringComparer.Ordinal)
                && !seedTools.Contains("git_worktree_create", StringComparer.OrdinalIgnoreCase)
                && configuration.ToolManifest.Any(m => string.Equals(m.Id, "git_worktree_create", StringComparison.OrdinalIgnoreCase)))
            {
                seedTools = [.. seedTools, "git_worktree_create"];
            }

            var seedGrants = ResourceSeed(spawnable?.ResourceGrants ?? selected.Agent.ResourceGrants);
            if (task.Worktree && !seedGrants.Any(g => g.StartsWith("write:", StringComparison.OrdinalIgnoreCase)))
            {
                seedGrants = [.. seedGrants, "write:.tinadec/worktrees"];
            }

            worker = await _instances.CreateRootAsync(new RuntimeAgentSeed(
                Guid.Parse(run.SessionId),
                runId,
                selected.Agent.Id,
                selected.Agent.Layer,
                selected.Agent.Role,
                "chat",
                selected.Agent.Capabilities,
                seedTools,
                seedGrants,
                configuration.Context.DefaultTokenBudget,
                TaskId: task.TaskId,
                AgentDefinitionId: definitionId,
                AgentVersionId: versionId,
                VersionContentHash: selected.Agent.VersionContentHash,
                AllowedDispatchTargets: spawnable?.AllowedDispatchTargets ?? selected.Agent.AllowedDispatchTargets,
                ParentInstanceId: lineage.InstanceId,
                GenerationDepth: lineage.Depth ?? 0), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.created",
                spawnable is null ? "Graph-tier execution worker created." : $"Spawnable worker '{spawnable.Slug}' created from the frozen whitelist.",
                new
                {
                    agent_instance_id = worker.Id,
                    agent_slug = selected.Agent.Id,
                    agent_definition_id = worker.AgentDefinitionId,
                    agent_version_id = worker.AgentVersionId,
                    layer = worker.Layer,
                    role = worker.Role,
                    author_instance_id = plannerId
                }, cancellationToken).ConfigureAwait(false);
        }
        VerifyWorkerInstance(worker, selected.Agent, task);

        if (boundTeammate is not null)
        {
            boundTeammate.AgentInstanceId = worker.Id;
            boundTeammate.CurrentTaskId = task.TaskId;
        }
        if (task.WorkerAgentId != worker.Id)
        {
            task.WorkerAgentId = worker.Id;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-assigned", cancellationToken).ConfigureAwait(false);
        }
        return worker;
    }

    private static string? ResolveDispatcherAgentSlug(IReadOnlyList<DurableTaskNode> tasks, DurableTaskNode task)
    {
        if (task.DispatchedByTaskId is not { } parentId) return null;
        return tasks.FirstOrDefault(item => item.TaskId == parentId)?.WorkerAgentSlug;
    }

    internal static void EnsureGraphWorkerBudget(IReadOnlyList<RuntimeAgentInstance> instances, int maxAgentsPerRun, string taskKey)
    {
        if (instances.Count >= maxAgentsPerRun)
            throw new WorkerAssignmentException(
                $"Graph-tier worker budget exhausted for task '{taskKey}': the run already carries {instances.Count} instances (ceiling {maxAgentsPerRun}).");
    }

    internal static (bool Dispatched, Guid? InstanceId, int? Depth) ResolveDispatcherLineage(
        IReadOnlyList<RuntimeAgentInstance> instances,
        IReadOnlyList<DurableTaskNode> tasks,
        DurableTaskNode task)
    {
        if (task.DispatchedByTaskId is not { } dispatchedBy) return (false, null, 0);
        var dispatcher = tasks.FirstOrDefault(item => item.TaskId == dispatchedBy);
        if (dispatcher?.WorkerAgentId is not { } dispatcherInstanceId) return (true, null, null);
        var dispatcherInstance = instances.FirstOrDefault(item => item.Id == dispatcherInstanceId);
        if (dispatcher is { IsCoordinatorTask: true } || string.Equals(dispatcher.TaskKey, "solo", StringComparison.Ordinal))
        {
            // Direct dispatch from the root conversation/coordinator task sits at depth 1
            // when MaxDepth >= 1, or depth 0 when MaxDepth == 0 (single-hop coordinator -> worker).
            return (true, dispatcherInstanceId, dispatcherInstance?.GenerationDepth ?? 0);
        }
        return (true, dispatcherInstanceId, dispatcherInstance?.GenerationDepth + 1);
    }

    internal static void EnsureGraphWorkerDepth(bool dispatched, int? workerDepth, int maxDepth, string taskKey)
    {
        if (!dispatched) return;
        if (workerDepth is null)
            throw new WorkerAssignmentException(
                $"Task '{taskKey}' is a dispatched sub-task whose dispatcher worker could not be resolved, so its spawn depth is unverifiable (max_depth = {maxDepth}). Refusing to create it at depth 0.");
        if (workerDepth.Value > maxDepth)
            throw new WorkerAssignmentException(
                $"Task '{taskKey}' would exceed the spawn depth ceiling (max_depth = {maxDepth}): it would be created at depth {workerDepth.Value}.");
    }

    private static IReadOnlyList<string> ResourceSeed(IReadOnlyList<FrozenResourceGrant> grants) =>
        grants.Count == 0
            ? []
            : grants.Select(grant => $"{grant.Level}:{grant.PathPrefix}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static FrozenSpawnableTemplate? ResolvePersistedSpawnable(FrozenRunConfigurationV1 configuration, DurableTaskNode task)
    {
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug)) return null;
        var template = configuration.Graph?.SpawnableTemplates.FirstOrDefault(item =>
            string.Equals(item.Slug, task.WorkerAgentSlug, StringComparison.OrdinalIgnoreCase));
        if (template is null) return null;
        if ((task.WorkerAgentDefinitionId is { } persistedDefinition && persistedDefinition != template.AgentDefinitionId)
            || (task.WorkerAgentVersionId is { } persistedVersion && persistedVersion != template.AgentVersionId)
            || (task.WorkerAgentVersionHash is { } persistedHash && !string.Equals(persistedHash, template.VersionHash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Persisted worker assignment for task '{task.TaskKey}' does not match the frozen spawnable template.");
        return template;
    }

    private static RuntimeAgentDefinition SpawnableDefinition(FrozenSpawnableTemplate template, FrozenRunConfigurationV1 configuration)
    {
        var conversation = RequiredConversationAgent(configuration);
        return new RuntimeAgentDefinition(
            template.Slug,
            "execution",
            template.Role,
            "on_demand",
            template.Capabilities,
            DirectUserOutput: false,
            ContextAccess: "read")
        {
            AgentDefinitionId = template.AgentDefinitionId,
            AgentVersionId = template.AgentVersionId,
            VersionContentHash = template.VersionHash,
            AllowedTools = template.ToolCeiling,
            ResourceGrants = template.ResourceGrants,
            Description = template.Description,
            SystemPrompt = template.SystemPrompt ?? string.Empty,
            AllowedDispatchTargets = template.AllowedDispatchTargets,
            ModelPlan = conversation.ModelPlan,
            Enabled = true
        };
    }

    internal static (WorkerSelection Selection, FrozenSpawnableTemplate? Spawnable) ResolveRequestedWorker(
        FrozenRunConfigurationV1 configuration,
        DurableTaskNode task,
        IReadOnlyList<DurableTaskNode>? allTasks = null,
        string? dispatcherSlug = null,
        int dispatcherDepth = 0)
    {
        var requested = task.RequestedAgent!.Trim();
        var effectiveDispatcherSlug = dispatcherSlug ?? (allTasks is not null ? ResolveDispatcherAgentSlug(allTasks, task) : null);
        var isRootDispatcher = string.IsNullOrWhiteSpace(effectiveDispatcherSlug)
            || string.Equals(effectiveDispatcherSlug, configuration.Graph?.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase);

        if (isRootDispatcher
            && ConversationDispatchTargets(configuration) is { } allowed
            && !allowed.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            throw new WorkerAssignmentException(
                $"Task '{task.TaskKey}' names executor '{requested}', which this mode does not allow its coordinator to dispatch to. {DescribeDispatchChoices(DispatchRosterOf(configuration))}");
        }
        if (!isRootDispatcher)
        {
            var subAllowed = DispatchRosterForWorker(configuration, effectiveDispatcherSlug!, currentDepth: dispatcherDepth);
            if (!subAllowed.Any(t => string.Equals(t.Id, requested, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WorkerAssignmentException(
                    $"Task '{task.TaskKey}' names executor '{requested}', which dispatcher '{effectiveDispatcherSlug}' is not permitted to dispatch to. {DescribeDispatchChoices(subAllowed)}");
            }
        }

        var agent = configuration.ExecutionAgents.FirstOrDefault(candidate =>
            candidate.Enabled && string.Equals(candidate.Id, requested, StringComparison.OrdinalIgnoreCase));
        if (agent is not null)
        {
            ValidateFrozenAgent(agent, "worker");
            if (!string.IsNullOrWhiteSpace(task.WorkerAgentSlug)
                && (!string.Equals(task.WorkerAgentSlug, agent.Id, StringComparison.Ordinal)
                    || task.WorkerAgentDefinitionId != agent.AgentDefinitionId
                    || task.WorkerAgentVersionId != agent.AgentVersionId
                    || !string.Equals(task.WorkerAgentVersionHash, agent.VersionContentHash, StringComparison.OrdinalIgnoreCase)))
                throw new WorkerAssignmentException($"Persisted worker assignment for task '{task.TaskKey}' does not match its requested executor '{agent.Id}'.");
            return (new WorkerSelection(agent, task.WorkerAssignmentReason ?? "coordinator_assigned"), null);
        }

        var graph = configuration.Graph;
        var template = graph?.SpawnableTemplates.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, requested, StringComparison.OrdinalIgnoreCase));
        if (template is not null)
        {
            if (!GraphSpawnAuthority.CarriesSpawnAuthority(graph!.Tier))
                throw new WorkerAssignmentException(
                    $"Task '{task.TaskKey}' names spawnable template '{template.Slug}', but the {graph.Tier} tier denies spawn (graph_tier_spawn_denied).");
            return (new WorkerSelection(SpawnableDefinition(template, configuration), task.WorkerAssignmentReason ?? "coordinator_assigned"), template);
        }
        throw new WorkerAssignmentException(
            $"Task '{task.TaskKey}' names executor '{requested}', which this run cannot dispatch to. {DescribeDispatchChoices(DispatchRosterOf(configuration))}");
    }

    internal static string NextWorkerHandle(IEnumerable<DurableTaskNode> tasks, string agentId)
    {
        var used = tasks.Count(item => !string.IsNullOrWhiteSpace(item.WorkerHandle)
            && string.Equals(item.WorkerAgentSlug, agentId, StringComparison.OrdinalIgnoreCase));
        return $"{agentId}#{used + 1}";
    }

    internal static WorkerSelection ResolveOrSelectWorker(FrozenRunConfigurationV1 configuration, DurableTaskNode task)
    {
        var selected = SelectWorker(configuration, task);
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug)) return selected;
        if (!string.Equals(task.WorkerAgentSlug, selected.Agent.Id, StringComparison.Ordinal)
            || task.WorkerAgentDefinitionId != selected.Agent.AgentDefinitionId
            || task.WorkerAgentVersionId != selected.Agent.AgentVersionId
            || !string.Equals(task.WorkerAgentVersionHash, selected.Agent.VersionContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Persisted worker assignment for task '{task.TaskKey}' does not match the frozen deterministic selection.");
        }
        return selected with { Reason = task.WorkerAssignmentReason ?? selected.Reason };
    }

    internal static WorkerSelection SelectWorker(FrozenRunConfigurationV1 configuration, DurableTaskNode task)
    {
        var requiredTools = NormalizeValues(task.RequiredTools);
        var requiredCapabilities = NormalizeValues(task.RequiredCapabilities);
        var manifestTools = configuration.ToolManifest.Select(tool => tool.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingManifestTool = requiredTools.FirstOrDefault(tool => !manifestTools.Contains(tool));
        if (missingManifestTool is not null)
            throw new InvalidDataException($"Task '{task.TaskKey}' requires tool '{missingManifestTool}', which is not in the frozen run manifest.");

        var hasGeneralWorker = configuration.ExecutionAgents.Any(agent =>
            agent.Enabled && string.Equals(agent.Id, "worker.general", StringComparison.Ordinal));
        var unclassifiedTask = requiredTools.Count == 0 && requiredCapabilities.Count == 0;
        var candidates = configuration.ExecutionAgents
            .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
            .Select(agent =>
            {
                ValidateFrozenAgent(agent, "worker");
                var tools = ExpandFrozenTools(agent.AllowedTools, manifestTools);
                var capabilities = agent.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isGeneral = string.Equals(agent.Id, "worker.general", StringComparison.Ordinal);
                var coversTools = requiredTools.All(tools.Contains);
                var coversCapabilities = requiredCapabilities.All(capabilities.Contains);
                var eligible = coversTools && coversCapabilities;
                if (unclassifiedTask) eligible = eligible && (isGeneral || !hasGeneralWorker);
                return new WorkerCandidate(agent, tools, capabilities, isGeneral, eligible);
            })
            .Where(candidate => candidate.Eligible)
            .OrderBy(candidate => candidate.IsGeneral)
            .ThenBy(candidate => unclassifiedTask
                ? -candidate.Tools.Count
                : Math.Max(0, candidate.Tools.Count - requiredTools.Count))
            .ThenBy(candidate => unclassifiedTask
                ? -candidate.Capabilities.Count
                : Math.Max(0, candidate.Capabilities.Count - requiredCapabilities.Count))
            .ThenBy(candidate => candidate.Agent.RosterOrder)
            .ThenBy(candidate => candidate.Agent.Id, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            var capabilityText = requiredCapabilities.Count == 0 ? "none" : string.Join(", ", requiredCapabilities);
            var toolText = requiredTools.Count == 0 ? "none" : string.Join(", ", requiredTools);
            throw new WorkerUnavailableException($"No frozen execution specialist can satisfy task '{task.TaskKey}' (capabilities: {capabilityText}; tools: {toolText}).");
        }

        var winner = candidates[0];
        var reason = winner.IsGeneral
            ? unclassifiedTask ? "general_default" : "general_fallback"
            : unclassifiedTask ? "general_unavailable_fallback"
            : requiredCapabilities.Count == 0 ? "specialist_tool_match" : "specialist_capability_and_tool_match";
        return new WorkerSelection(winner.Agent, reason);
    }

    private static HashSet<string> ExpandFrozenTools(
        IReadOnlyList<string> allowedTools,
        IReadOnlySet<string> manifestTools)
    {
        if (allowedTools.Any(tool => string.Equals(tool, "*", StringComparison.OrdinalIgnoreCase)))
            return manifestTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowedTools.Where(manifestTools.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> NormalizeValues(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static RuntimeAgentDefinition GetAssignedWorkerDefinition(
        FrozenRunConfigurationV1 configuration,
        DurableTaskNode task)
    {
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug))
            throw new InvalidDataException($"Task '{task.TaskKey}' has no persisted worker assignment.");
        var definition = configuration.ExecutionAgents.SingleOrDefault(agent =>
            string.Equals(agent.Id, task.WorkerAgentSlug, StringComparison.Ordinal)
            && agent.AgentDefinitionId == task.WorkerAgentDefinitionId
            && agent.AgentVersionId == task.WorkerAgentVersionId
            && string.Equals(agent.VersionContentHash, task.WorkerAgentVersionHash, StringComparison.OrdinalIgnoreCase));
        if (definition is not null) return definition;
        var master = configuration.OperationAgents.SingleOrDefault(agent =>
            string.Equals(agent.Id, task.WorkerAgentSlug, StringComparison.Ordinal)
            && agent.AgentDefinitionId == task.WorkerAgentDefinitionId
            && agent.AgentVersionId == task.WorkerAgentVersionId
            && string.Equals(agent.VersionContentHash, task.WorkerAgentVersionHash, StringComparison.OrdinalIgnoreCase));
        if (master is not null) return master;
        var template = configuration.Graph?.SpawnableTemplates.SingleOrDefault(item =>
            string.Equals(item.Slug, task.WorkerAgentSlug, StringComparison.OrdinalIgnoreCase)
            && item.AgentDefinitionId == task.WorkerAgentDefinitionId
            && item.AgentVersionId == task.WorkerAgentVersionId
            && string.Equals(item.VersionHash, task.WorkerAgentVersionHash, StringComparison.OrdinalIgnoreCase));
        return template is not null
            ? SpawnableDefinition(template, configuration)
            : throw new InvalidDataException($"Task '{task.TaskKey}' worker assignment is not present in the frozen roster.");
    }

    private static void VerifyWorkerInstance(
        RuntimeAgentInstance instance,
        RuntimeAgentDefinition definition,
        DurableTaskNode task)
    {
        if (instance.AgentDefinitionId != definition.AgentDefinitionId
            || instance.AgentVersionId != definition.AgentVersionId
            || !string.Equals(instance.AgentVersionContentHash, definition.VersionContentHash, StringComparison.OrdinalIgnoreCase)
            || instance.TaskId != task.TaskId)
            throw new WorkerAssignmentException($"Worker instance '{instance.Id}' does not match task '{task.TaskKey}' frozen assignment.");
    }

    private async Task<WorkerModelTurn> GetWorkerModelTurnAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        IReadOnlyList<WorkerToolDescriptor> tools,
        CancellationToken cancellationToken,
        string? closeoutPrompt = null)
    {
        var workerDefinition = GetAssignedWorkerDefinition(configuration, task);
        var (taskWorktreePath, taskWorktreeBranch) = ResolveTaskWorktree(checkpoint, task);
        var promptConfiguration = SpacePromptConfiguration(configuration, checkpoint, taskWorktreePath);
        var context = await BuildContextAsync(
            run,
            promptConfiguration,
            workerDefinition,
            WithSpaceContext(
                checkpoint,
                $"Task: {task.Title}\nDescription: {task.Description}\nSuccess criteria: {string.Join("; ", task.SuccessCriteria)}"
                + (task.WriteScope is { Count: > 0 } scope
                    ? $"\nWrite scope (reserved for you; change files only inside it — changes elsewhere are reported to the reviewer): {string.Join("; ", scope)}"
                    : string.Empty),
                taskWorktreePath,
                taskWorktreeBranch),
            checkpoint.UserGoal,
            cancellationToken).ConfigureAwait(false);

        var assembly = await AssemblePromptAsync(promptConfiguration, workerDefinition, context, cancellationToken).ConfigureAwait(false);
        var agent = new AgentDefinition
        {
            Id = worker.Id,
            Name = workerDefinition.Id,
            Layer = worker.Layer,
            AgentType = workerDefinition.Role,
            ModelRoutePurpose = "chat",
            AllowedTools = worker.AllowedTools,
            Enabled = true
        };

        var hasTeamSpawn = worker.AllowedTools.Any(CoreVirtualToolPolicy.IsTeamSpawn);
        var hasTaskDispatch = worker.AllowedTools.Any(CoreVirtualToolPolicy.IsTaskDispatch);
        var dispatchSection = hasTeamSpawn || hasTaskDispatch
            ? "\n\n" + BuildDispatchRosterSection(
                DispatchRosterForWorker(configuration, workerDefinition.Id, worker.GenerationDepth),
                usesTeamSpawn: hasTeamSpawn)
            : string.Empty;

        var modelFactory = CreateModelFactory(
            configuration,
            checkpoint,
            workerDefinition,
            worker.Id,
            worker.ParentInstanceId,
            AnswerStreaming.WorkLayer,
            task: task,
            run: run);

        var probeResolution = await modelFactory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        var isNonMcpHarness = ChatProtocols.Normalize(probeResolution.Protocol) is ChatProtocols.HeadlessCli or ChatProtocols.Tui;
        var boardSection = FormatBoardContextForAgent(checkpoint, task, isNonMcpHarness);
        if (!string.IsNullOrWhiteSpace(task.TeammateHandle))
        {
            var tm = checkpoint.Teammates.FirstOrDefault(t =>
                string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase));
            if (tm is not null)
            {
                tm.LastDeliveredBoardSeq = checkpoint.NextTeamBoardSeq;
            }
        }

        var fullResults = await LoadToolResultsKeptOnExecutionAsync(task, cancellationToken).ConfigureAwait(false);
        var effectiveInstructions = assembly.Instructions + dispatchSection + boardSection + WorkerPatchProtocol;
        return await new ExecutionAgent(modelFactory, _logger).GetNextTurnAsync(
            CreateRunContext(run, checkpoint),
            agent,
            ToPlannedTask(task),
            task.ToolTurns,
            tools,
            closeoutPrompt is null
                ? effectiveInstructions
                : closeoutPrompt + "\n\n" + effectiveInstructions,
            configuration.Context.RecentMessageLimit,
            cancellationToken,
            task.Interruptions,
            _services.GetService(typeof(IMessageAttachmentStore)) as IMessageAttachmentStore,
            fullResults).ConfigureAwait(false);
    }

    private IAgentChatClientFactory CreateModelFactory(
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RuntimeAgentDefinition definition,
        Guid? instanceId,
        Guid? parentInstanceId,
        AnswerStreaming streaming = AnswerStreaming.None,
        bool observeOutput = true,
        DurableTaskNode? task = null,
        bool interruptible = true,
        RunState? run = null)
    {
        var resolver = _modelResolver ?? throw new InvalidOperationException("The agent model resolver is not registered.");
        var basePlan = definition.ModelPlan ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no model plan.");
        var effectivePlan = BuildEffectiveModelPlan(configuration, basePlan, task);
        var definitionId = definition.AgentDefinitionId ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no definition id.");
        var versionId = definition.AgentVersionId ?? throw new InvalidDataException($"Frozen agent '{definition.Id}' has no version id.");
        var modeVersionId = configuration.Bindings.Single(binding => binding.ConfigurationKind == "agent_mode_version").ConfigurationVersionId;

        IAgentModelResolver effectiveResolver = resolver;
        if (task is not null)
        {
            effectiveResolver = new TeammateScopedModelResolver(
                resolver,
                this,
                run,
                configuration,
                checkpoint,
                task);
        }

        return new ModelInvocationChatFactory(effectiveResolver, _chatClients, effectivePlan, new ModelInvocationContext(
            checkpoint.SessionId, checkpoint.RunId, checkpoint.TurnId, instanceId, parentInstanceId,
            definitionId, versionId, modeVersionId, effectivePlan.StrategySource), observeOutput ? async (frame, ct) =>
            {
                var state = await _lifecycle.GetRunStateAsync(checkpoint.RunId.ToString(), ct).ConfigureAwait(false);
                var epoch = LeaseEpochFor(checkpoint.RunId);
                if (IsTerminal(state.Status) || state.Status == RunStatus.Paused
                    || epoch is null || state.LeaseOwner != epoch.OwnerId || state.RecoveryCount != epoch.RecoveryCount)
                    return;
                if (frame.Kind != "delta" || frame.Channel == "reasoning" || task is not null)
                {
                    var boundTm = task?.TeammateHandle is { } tmHandle
                        ? checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, tmHandle, StringComparison.OrdinalIgnoreCase))
                        : null;
                    await AppendEventAsync(checkpoint.RunId, $"model.output.{frame.Kind}",
                        "Model output update.", new
                        {
                            run_id = checkpoint.RunId,
                            turn_id = checkpoint.TurnId,
                            response_id = frame.ResponseId,
                            agent_name = definition.Id,
                            instance_id = instanceId,
                            task_id = task?.TaskId,
                            handle = task?.TeammateHandle ?? task?.WorkerHandle,
                            runtime_badge = boundTm?.RuntimeBadge,
                            channel = frame.Channel,
                            delta = frame.Delta
                        }, ct).ConfigureAwait(false);
                }
                var draft = streaming == AnswerStreaming.AnswerLayer
                    ? _answerDrafts.GetOrAdd(checkpoint.RunId, _ => new AnswerDraft())
                    : null;
                var delta = frame.Kind == "delta" && frame.Channel == "text"
                    ? draft?.Append(frame.Delta ?? string.Empty)
                    : null;
                if (streaming == AnswerStreaming.AnswerLayer && (frame.Kind is "started" or "failed" or "interrupted" || frame.Channel == "text"))
                {
                    await PublishRunStreamAsync(checkpoint.RunId.ToString(), new DurableRunStreamAppend(
                        checkpoint.TurnId, frame.Kind == "interrupted" ? "answer.failed" : $"answer.{frame.Kind}", Delta: delta ?? frame.Delta), ct).ConfigureAwait(false);
                    if (frame.Kind == "delta" && frame.Channel == "text") draft?.MarkPublished();
                }
            } : null,
            interruptible ? Interrupts : null);
    }

    private static FrozenModelPlan BuildEffectiveModelPlan(
        FrozenRunConfigurationV1 configuration,
        FrozenModelPlan basePlan,
        DurableTaskNode? task)
    {
        if (task is null
            || (task.ProviderInstanceOverride is null
                && string.IsNullOrWhiteSpace(task.ChannelOverride)
                && string.IsNullOrWhiteSpace(task.ModelIdOverride)))
        {
            return basePlan;
        }

        var mappedProtocol = MapChannelOverrideToProtocol(task.ChannelOverride);
        var candidates = basePlan.Candidates.ToList();

        if (task.ProviderInstanceOverride is { } targetProviderId)
        {
            var matching = candidates.Where(c => c.ProviderInstanceId == targetProviderId).ToList();
            if (matching.Count == 0)
            {
                var poolMatch = configuration.OperationAgents.Concat(configuration.ExecutionAgents)
                    .Where(a => a.ModelPlan is not null)
                    .SelectMany(a => a.ModelPlan!.Candidates)
                    .FirstOrDefault(c => c.ProviderInstanceId == targetProviderId);
                if (poolMatch is not null)
                {
                    matching.Add(poolMatch);
                }
            }
            if (matching.Count > 0)
            {
                var rest = candidates.Where(c => c.ProviderInstanceId != targetProviderId).ToList();
                candidates = [.. matching, .. rest];
            }
        }

        var adjusted = candidates
            .Select((c, idx) => c with
            {
                Position = idx,
                Model = !string.IsNullOrWhiteSpace(task.ModelIdOverride) && (idx == 0 || c.ProviderInstanceId == task.ProviderInstanceOverride)
                    ? task.ModelIdOverride
                    : c.Model,
                Protocol = !string.IsNullOrWhiteSpace(mappedProtocol) && (idx == 0 || c.ProviderInstanceId == task.ProviderInstanceOverride)
                    ? mappedProtocol!
                    : c.Protocol
            })
            .ToArray();

        return basePlan with
        {
            StrategySource = "team_spawn_override",
            Candidates = adjusted,
            InheritFromParent = task.ProviderInstanceOverride is null && basePlan.InheritFromParent
        };
    }

    private static string? MapChannelOverrideToProtocol(string? channel) =>
        channel?.Trim().ToLowerInvariant() switch
        {
            "acp" => ChatProtocols.Acp,
            "headless" or "headless-cli" or "cli" => ChatProtocols.HeadlessCli,
            "tui" => ChatProtocols.Tui,
            "http" or "opencode-serve" => ChatProtocols.OpencodeServe,
            _ => null
        };

    private ChatResolution EnrichTeammateResolution(
        RunState? run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        ChatResolution resolution)
    {
        var effectiveHandle = task.TeammateHandle ?? task.WorkerHandle ?? task.TaskKey;
        var (taskWorktreePath, _) = ResolveTaskWorktree(checkpoint, task);
        var effectiveWorktree = taskWorktreePath ?? checkpoint.SpaceWorktreePath;
        var teammate = !string.IsNullOrWhiteSpace(task.TeammateHandle)
            ? checkpoint.Teammates.FirstOrDefault(t => string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase))
            : null;
        var effectiveWriteScope = task.WriteScope ?? teammate?.WriteScope;

        string? mcpEndpoint = null;
        string? mcpToken = null;
        if (_teamBoardMcpBridge is not null && !task.IsCoordinatorTask)
        {
            mcpEndpoint = ResolveMcpLoopbackEndpoint();
            var leaseKey = (checkpoint.RunId, effectiveHandle.ToLowerInvariant());
            if (!_runTeammateMcpLeases.TryGetValue(leaseKey, out var lease)
                || _teamBoardMcpBridge.ValidateToken(lease.Token) is null)
            {
                var tenantId = run is not null && Guid.TryParse(run.TenantId, out var tid) ? tid : Guid.Empty;
                Guid? workspaceId = run is not null && Guid.TryParse(run.WorkspaceId, out var wid) ? wid : null;
                var principalId = run is not null && Guid.TryParse(run.InitiatedByPrincipalId, out var pid) ? pid : Guid.Empty;
                lease = _teamBoardMcpBridge.IssueToken(
                    tenantId,
                    workspaceId,
                    checkpoint.SessionId,
                    checkpoint.RunId,
                    effectiveHandle,
                    projectId: configuration.Workspace?.ProjectId,
                    principalId: principalId);
                _runTeammateMcpLeases[leaseKey] = lease;
            }
            mcpToken = lease.Token;
        }

        var enriched = resolution with
        {
            RunId = checkpoint.RunId.ToString(),
            TeammateHandle = effectiveHandle,
            WorkingDirectoryOverride = effectiveWorktree,
            WriteScope = effectiveWriteScope,
            McpLoopbackEndpoint = mcpEndpoint,
            McpScopedBearerToken = mcpToken,
            Model = !string.IsNullOrWhiteSpace(task.ModelIdOverride) ? task.ModelIdOverride : resolution.Model,
            ModelId = !string.IsNullOrWhiteSpace(task.ModelIdOverride) ? task.ModelIdOverride : resolution.ModelId,
        };

        if (teammate is not null && enriched.IsAvailable)
        {
            teammate.RuntimeBadge = enriched.RuntimeBadge ?? FallbackRuntimeBadge(enriched);
            teammate.ProviderInstanceId ??= enriched.ProviderInstanceId;
            teammate.ModelId ??= enriched.Model ?? enriched.ModelId;
            teammate.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return enriched;
    }

    private static string FallbackRuntimeBadge(ChatResolution resolution)
    {
        var protocol = ChatProtocols.Normalize(resolution.Protocol);
        var label = resolution.Model ?? resolution.ModelId ?? protocol;
        return protocol switch
        {
            ChatProtocols.Acp => $"ACP · {label}",
            ChatProtocols.HeadlessCli => $"CLI · {label}",
            ChatProtocols.Tui => $"TUI · {label}",
            ChatProtocols.OpencodeServe => $"HTTP · {label}",
            _ => $"API · {label}"
        };
    }

    private string ResolveMcpLoopbackEndpoint()
    {
        var explicitMcp = Environment.GetEnvironmentVariable("TINADEC_MCP_URL");
        if (!string.IsNullOrWhiteSpace(explicitMcp))
            return explicitMcp.Trim();

        var config = _services.GetService(typeof(IConfiguration)) as IConfiguration;
        var rawUrls = Environment.GetEnvironmentVariable("TINADEC_CORE_URL")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? config?["Tinadec:LoopbackBaseUrl"]
            ?? config?["urls"]
            ?? config?["ASPNETCORE_URLS"];

        if (!string.IsNullOrWhiteSpace(rawUrls))
        {
            var first = rawUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                var normalized = first
                    .Replace("://0.0.0.0:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal)
                    .TrimEnd('/');
                if (normalized.EndsWith("/api/v1/mcp/tinadec", StringComparison.OrdinalIgnoreCase))
                    return normalized;
                return normalized + "/api/v1/mcp/tinadec";
            }
        }

        return "http://127.0.0.1:48720/api/v1/mcp/tinadec";
    }

    private async Task SettleTeammateTurnAsync(
        RunState run,
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        TaskExecutionResult execution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.TeammateHandle)) return;

        var teammate = checkpoint.Teammates.FirstOrDefault(t =>
            string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase));
        if (teammate is null) return;

        var (worktreePath, worktreeBranch) = ResolveTaskWorktree(checkpoint, task);
        if (!string.IsNullOrWhiteSpace(worktreePath)) teammate.WorktreePath = worktreePath;
        if (!string.IsNullOrWhiteSpace(worktreeBranch)) teammate.BranchName = worktreeBranch;

        var rawSummary = task.ResultSummary ?? execution.Result.Summary ?? string.Empty;
        var cleanedSummary = ExtractNonMcpBoardMessages(checkpoint, teammate, task.DispatchedByTaskId, rawSummary);
        teammate.LastOutcomeSummary = BoundText(cleanedSummary, MaxTeammateOutcomeSummaryChars);
        teammate.TranscriptSummary = AppendBoundedTranscriptSummary(
            teammate.TranscriptSummary,
            task.Attempt,
            teammate.RelayDepth,
            execution.Status,
            teammate.LastOutcomeSummary);

        teammate.Status = string.Equals(teammate.Mode, "teammate", StringComparison.OrdinalIgnoreCase)
            ? "waiting"
            : "terminated";
        teammate.CurrentTaskId = null;
        teammate.UpdatedAt = DateTimeOffset.UtcNow;

        var alreadyPostedThisTurn = task.ToolTurns.Any(turn =>
            CoreVirtualToolPolicy.IsTeamMessage(turn.ToolId) && turn.ToolSuccess == true);
        if (!alreadyPostedThisTurn && !string.IsNullOrWhiteSpace(teammate.LastOutcomeSummary))
        {
            AppendBoardMessage(
                checkpoint,
                teammate.Handle,
                "coordinator",
                execution.Status == "completed" ? "handoff" : "update",
                $"[{execution.Status}] {teammate.LastOutcomeSummary}",
                wake: false);
        }

        await AppendEventAsync(runId, "team.teammate_settled",
            $"Teammate @{teammate.Handle} ({teammate.AgentSlug}) settled into '{teammate.Status}'.",
            new
            {
                run_id = runId,
                task_id = task.TaskId,
                handle = teammate.Handle,
                agent_slug = teammate.AgentSlug,
                mode = teammate.Mode,
                status = teammate.Status,
                relay_depth = teammate.RelayDepth,
                worktree_path = teammate.WorktreePath,
                branch_name = teammate.BranchName,
                runtime_badge = teammate.RuntimeBadge,
                last_outcome = teammate.LastOutcomeSummary
            }, cancellationToken, task.TaskId).ConfigureAwait(false);
    }

    private static string AppendBoundedTranscriptSummary(
        string? existing,
        int attempt,
        int relayDepth,
        string status,
        string outcomeSummary)
    {
        var entry = $"[turn #{Math.Max(1, attempt)} / wake #{relayDepth} -> {status}] {outcomeSummary}".Trim();
        var combined = string.IsNullOrWhiteSpace(existing)
            ? entry
            : existing.Trim() + "\n" + entry;
        if (combined.Length <= MaxTeammateTranscriptSummaryChars)
            return combined;
        return "…" + combined[(combined.Length - MaxTeammateTranscriptSummaryChars + 1)..];
    }

    /// <summary>
    /// Bridges <c>BOARD_MESSAGE: @&lt;target&gt; | &lt;content&gt;</c> lines emitted by HeadlessCli / Tui
    /// teammates (which cannot issue live mid-turn HTTP MCP calls) back onto the shared run board.
    /// </summary>
    private static string ExtractNonMcpBoardMessages(
        FullDuplexCheckpointV1 checkpoint,
        DurableTeammateState sender,
        Guid? dispatcherTaskId,
        string text)
    {
        const string marker = "BOARD_MESSAGE:";
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return text;

        var kept = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var body = trimmed[marker.Length..].Trim();
                var sep = body.IndexOf('|', StringComparison.Ordinal);
                var target = "coordinator";
                var msgContent = body;
                if (sep > 0)
                {
                    target = body[..sep].Trim().TrimStart('@');
                    if (string.IsNullOrWhiteSpace(target)) target = "coordinator";
                    msgContent = body[(sep + 1)..].Trim();
                }
                if (!string.IsNullOrWhiteSpace(msgContent))
                {
                    var entry = AppendBoardMessage(checkpoint, sender.Handle, target, "update", msgContent, wake: true);
                    if (!string.Equals(target, "coordinator", StringComparison.OrdinalIgnoreCase))
                    {
                        var wakeTargets = string.Equals(target, "all", StringComparison.OrdinalIgnoreCase)
                            ? checkpoint.Teammates.Where(t => !string.Equals(t.Handle, sender.Handle, StringComparison.OrdinalIgnoreCase)).Select(t => t.Handle).ToArray()
                            : [target];
                        foreach (var h in wakeTargets)
                        {
                            WakeTeammateFromBoard(checkpoint, dispatcherTaskId, h, entry.Seq, msgContent, sender.RelayDepth);
                        }
                    }
                }
                continue;
            }
            kept.Add(line);
        }
        var cleaned = string.Join("\n", kept).Trim();
        return cleaned.Length == 0 ? text.Trim() : cleaned;
    }

    internal static bool WakeTeammateFromBoard(
        FullDuplexCheckpointV1 checkpoint,
        Guid? dispatcherTaskId,
        string handle,
        long wakeSeq,
        string reasonPrompt,
        int? senderRelayDepth = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return false;
        var cleanHandle = handle.Trim().TrimStart('@');
        var teammate = checkpoint.Teammates.FirstOrDefault(t =>
            string.Equals(t.Handle, cleanHandle, StringComparison.OrdinalIgnoreCase));
        if (teammate is null) return false;
        if (!string.Equals(teammate.Mode, "teammate", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(teammate.Status, "waiting", StringComparison.OrdinalIgnoreCase)) return false;

        var nextDepth = (senderRelayDepth ?? teammate.RelayDepth) + 1;
        if (nextDepth > MaxTeammateRelayDepth)
        {
            AppendBoardMessage(
                checkpoint,
                "system",
                "coordinator",
                "update",
                $"[relay-cap] Refused waking @{teammate.Handle}: relay depth {nextDepth} exceeds MaxTeammateRelayDepth ({MaxTeammateRelayDepth}).",
                wake: false);
            return false;
        }

        var baseKey = NormalizeTaskKey(null, $"{teammate.Handle}-wake-{nextDepth}");
        var taskKey = baseKey;
        if (checkpoint.Tasks.Any(n => string.Equals(n.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)))
        {
            taskKey = $"{baseKey}-{checkpoint.Tasks.Count + 1}";
        }

        var parentTaskId = dispatcherTaskId
            ?? checkpoint.Tasks.FirstOrDefault(t => t.IsCoordinatorTask)?.TaskId
            ?? checkpoint.Tasks.FirstOrDefault(t => t.DispatchedByTaskId is null)?.TaskId;

        var wakeDescription = BuildTeammateWakePrompt(checkpoint, teammate, reasonPrompt);
        var wakeTask = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = taskKey,
            Title = $"@{teammate.Handle} continuation (wake #{nextDepth})",
            Description = wakeDescription,
            RequestedAgent = teammate.AgentSlug,
            TeammateHandle = teammate.Handle,
            TeammateMode = "teammate",
            WorkerHandle = teammate.Handle,
            DispatchedByTaskId = parentTaskId,
            WriteScope = teammate.WriteScope?.ToList(),
            Worktree = !string.IsNullOrWhiteSpace(teammate.WorktreePath),
            TaskWorktreePath = teammate.WorktreePath,
            TaskWorktreeBranch = teammate.BranchName,
            ProviderInstanceOverride = teammate.ProviderInstanceId,
            ChannelOverride = teammate.ChannelOverride,
            ModelIdOverride = teammate.ModelId,
            SuccessCriteria = [],
            Priority = 2,
            Risk = "medium",
            Status = "pending"
        };

        teammate.Status = "working";
        teammate.RelayDepth = nextDepth;
        teammate.LastWakeSeq = wakeSeq;
        teammate.CurrentTaskId = wakeTask.TaskId;
        teammate.UpdatedAt = DateTimeOffset.UtcNow;

        checkpoint.Tasks.Add(wakeTask);
        return true;
    }

    private static string BuildTeammateWakePrompt(
        FullDuplexCheckpointV1 checkpoint,
        DurableTeammateState teammate,
        string reasonPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are persistent teammate @{teammate.Handle} ({teammate.AgentSlug}), woken on the shared board (wake #{teammate.RelayDepth + 1}/{MaxTeammateRelayDepth}).");
        if (!string.IsNullOrWhiteSpace(teammate.WorktreePath))
        {
            sb.AppendLine($"Your isolated Git worktree from earlier turns is preserved at: {teammate.WorktreePath}"
                + (string.IsNullOrWhiteSpace(teammate.BranchName) ? string.Empty : $" (branch: {teammate.BranchName})"));
        }
        if (teammate.WriteScope is { Count: > 0 })
        {
            sb.AppendLine($"Authorized write_scope: {string.Join(", ", teammate.WriteScope)}");
        }
        if (!string.IsNullOrWhiteSpace(teammate.TranscriptSummary))
        {
            sb.AppendLine("\nYour bounded prior turn summary:");
            sb.AppendLine(teammate.TranscriptSummary);
        }
        var unread = checkpoint.TeamBoard
            .Where(m => m.Seq > teammate.LastDeliveredBoardSeq)
            .TakeLast(16)
            .ToArray();
        if (unread.Length > 0)
        {
            sb.AppendLine("\nRecent board messages since your last turn:");
            foreach (var msg in unread)
            {
                sb.AppendLine($"- [#{msg.Seq} {msg.Sender} -> @{msg.Target} ({msg.Kind})]: {BoundText(msg.Content, 600)}");
            }
        }
        sb.AppendLine("\nNew wake instruction / feedback to address now:");
        sb.AppendLine(reasonPrompt.Trim());
        return sb.ToString();
    }

    private sealed record WorkerCandidate(
        RuntimeAgentDefinition Agent,
        HashSet<string> Tools,
        HashSet<string> Capabilities,
        bool IsGeneral,
        bool Eligible);

    internal sealed record WorkerSelection(RuntimeAgentDefinition Agent, string Reason);

    private sealed class TeammateScopedModelResolver(
        IAgentModelResolver inner,
        FullDuplexRunEngine engine,
        RunState? run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task) : IAgentModelResolver
    {
        public Task<ModelResolutionPreviewDto> PreviewAsync(ModelResolutionPreviewRequestDto request, CancellationToken cancellationToken = default) =>
            inner.PreviewAsync(request, cancellationToken);

        public Task<FrozenModelPlan> FreezeAsync(AgentModelFreezeRequest request, CancellationToken cancellationToken = default) =>
            inner.FreezeAsync(request, cancellationToken);

        public async Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(
            FrozenModelPlan plan,
            Guid? parentInstanceId,
            CancellationToken cancellationToken = default)
        {
            var resolved = await inner.ResolveInvocationCandidatesAsync(plan, parentInstanceId, cancellationToken).ConfigureAwait(false);
            if (resolved.Count == 0) return resolved;
            return resolved
                .Select(candidate => engine.EnrichTeammateResolution(run, configuration, checkpoint, task, candidate))
                .ToArray();
        }

        public Task<Guid> StartInvocationAsync(ModelInvocationStart request, CancellationToken cancellationToken = default) =>
            inner.StartInvocationAsync(request, cancellationToken);

        public Task CompleteInvocationAsync(
            Guid invocationId,
            string status,
            ModelUsage? usage = null,
            string? errorCategory = null,
            string? safeErrorMessage = null,
            CancellationToken cancellationToken = default) =>
            inner.CompleteInvocationAsync(invocationId, status, usage, errorCategory, safeErrorMessage, cancellationToken);
    }
}
