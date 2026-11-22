using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.DmaEA;

internal sealed partial class FullDuplexRunEngine
{
    /// <summary>A result longer than this is kept on its execution row; the checkpoint keeps a head.</summary>
    internal const int InlineToolResultChars = 16 * 1024;
    private const int ToolResultHeadChars = 2 * 1024;

    /// <summary>Dispatch status of a call a hard insert kept from starting (todo D2).</summary>
    internal const string NotRunAfterInterrupt = "not_run";

    /// <summary>Interruptions kept per task; the newest are the ones the worker must act on.</summary>
    private const int MaxTaskInterruptions = 5;

    /// <summary>Per-line cap, so one long result cannot dominate the digest.</summary>
    private const int MaxRunEvidenceLineLength = 240;

    private async Task<TaskExecutionResult> ExecuteTextTaskAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken)
    {
        try
        {
            var worker = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
            var model = await GetWorkerModelTurnAsync(run, configuration, checkpoint, task, worker, [], cancellationToken).ConfigureAwait(false);
            if (!model.IsAvailable || model.Error is not null)
            {
                var failed = new StepResult
                {
                    TaskNodeId = task.TaskId,
                    AgentId = worker.Id.ToString("N"),
                    Status = "failed",
                    Summary = model.Error ?? "Worker model is unavailable.",
                    Evidence = []
                };
                return new TaskExecutionResult(task.TaskId, worker.Id, "failed", failed, model.Usage);
            }
            if (model.Calls.Count != 0)
            {
                var failed = new StepResult
                {
                    TaskNodeId = task.TaskId,
                    AgentId = worker.Id.ToString("N"),
                    Status = "failed",
                    Summary = "The model requested a tool that was not advertised to this worker.",
                    Evidence = []
                };
                return new TaskExecutionResult(task.TaskId, worker.Id, "failed", failed, model.Usage);
            }
            var result = string.IsNullOrWhiteSpace(model.Text)
                ? new StepResult
                {
                    TaskNodeId = task.TaskId,
                    AgentId = worker.Id.ToString("N"),
                    Status = "failed",
                    Summary = "Execution returned no output.",
                    Evidence = [DispatchEvidence(task)]
                }
                : BuildWorkerStepResult(task, worker.Id, model.Text);
            return new TaskExecutionResult(task.TaskId, worker.Id, result.Status, result, model.Usage);
        }
        catch (Exception ex) when (ex is WorkerAssignmentException or InvalidDataException)
        {
            return FailedTaskResult(task, task.WorkerAgentId, WorkerAssignmentInvalidCategory, SafeError(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new StepResult { TaskNodeId = task.TaskId, Status = "failed", Summary = SafeError(ex), Evidence = [] };
            return new TaskExecutionResult(task.TaskId, null, "failed", failed);
        }
    }

    private Task<ToolTaskExecutionResult> ResumePendingToolAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        CancellationToken cancellationToken) =>
        ExecuteToolTaskAsync(run, configuration, checkpoint, checkpoint.PlannerAgentId ?? Guid.Empty, task, cancellationToken);

    private async Task<ToolTaskExecutionResult> ExecuteToolTaskAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        Guid plannerId,
        DurableTaskNode task,
        CancellationToken cancellationToken,
        IReadOnlyList<WorkerToolDescriptor>? resolvedTools = null)
    {
        RuntimeAgentInstance worker;
        try
        {
            worker = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerAssignmentException ex)
        {
            return FailedToolTask(checkpoint, task, null, WorkerAssignmentInvalidCategory, SafeError(ex));
        }

        var dispatcher = _services.GetRequiredService<IToolDispatcher>();
        var executionCoordinator = _services.GetRequiredService<IToolExecutionCoordinator>();
        var roundLimit = configuration.Tools.ResolveTaskRoundLimit(task.Category, task.Risk);
        IReadOnlyList<WorkerToolDescriptor> descriptors;
        if (resolvedTools is not null)
        {
            descriptors = resolvedTools;
        }
        else
        {
            try
            {
                descriptors = await GetWorkerToolsAsync(run, configuration, task, worker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FailedToolTask(checkpoint, task, worker, ToolManifestUnavailableCategory, SafeError(ex));
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (task.IsSpacePreparation)
            {
                if (task.ToolTurns.FirstOrDefault(turn => turn.IsSpaceWorktreeProvisioning && turn.ResultJson is not null) is { } failedWorktree
                    && (failedWorktree.ToolSuccess == false || failedWorktree.DispatchStatus != ToolDispatchStatus.Completed))
                {
                    return FailedToolTask(checkpoint, task, worker, RunErrorTaxonomy.ToolError,
                        "The requested worktree could not be prepared; task execution stopped.", failedWorktree.ToolId);
                }
                if (QueueSpaceWorktree(configuration, checkpoint, task))
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "space-worktree-requested", cancellationToken).ConfigureAwait(false);
                checkpoint.SpacePreparationComplete = SpacePreparationReady(configuration, checkpoint);
                if (checkpoint.SpacePreparationComplete && configuration.SpaceOptions?.WorkflowModeVersionId is not null)
                {
                    var prepared = BuildWorkerStepResult(task, worker.Id,
                        "Spatial preparation completed: confirmed specifications and execution isolation are ready.\nTASK_OUTCOME: completed");
                    return new ToolTaskExecutionResult(checkpoint, false, new TaskExecutionResult(task.TaskId, worker.Id, "completed", prepared));
                }
            }
            else if (task.Worktree && task.TaskWorktreePath is null)
            {
                if (task.ToolTurns.FirstOrDefault(turn => turn.IsSpaceWorktreeProvisioning && turn.ResultJson is not null) is { } failedTaskWorktree
                    && (failedTaskWorktree.ToolSuccess == false || failedTaskWorktree.DispatchStatus != ToolDispatchStatus.Completed))
                {
                    return FailedToolTask(checkpoint, task, worker, RunErrorTaxonomy.ToolError,
                        "The requested task-isolated worktree could not be prepared; task execution stopped.", failedTaskWorktree.ToolId);
                }
                if (QueueTaskWorktree(configuration, checkpoint, task))
                {
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "task-worktree-requested", cancellationToken).ConfigureAwait(false);
                }
            }

            if (Interrupts?.TakePending(Guid.Parse(run.RunId)) is { } steering)
            {
                await ObserveAutonomousEventAsync(run, configuration, GovernanceTopics.UserSteer, steering.Content).ConfigureAwait(false);
                checkpoint = await SkipUnstartedToolCallsAsync(run, checkpoint, task, steering, cancellationToken).ConfigureAwait(false);
            }

            var pendingTurn = task.ToolTurns.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.ResultJson));
            if (pendingTurn is not null && SpaceToolRefusal(configuration, checkpoint, task, pendingTurn) is { } refusal)
            {
                checkpoint = await FeedToolFailureBackAsync(run, task, pendingTurn, RunErrorTaxonomy.InvalidToolArguments,
                    refusal, checkpoint, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 5 Orthogonal Team Primitives (#33)
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsTeamSpawn(pendingTurn.ToolId))
            {
                checkpoint = await HandleTeamSpawnAsync(run, configuration, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsTeamMessage(pendingTurn.ToolId))
            {
                checkpoint = await HandleTeamMessageAsync(run, configuration, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsTeamWait(pendingTurn.ToolId))
            {
                var teamWait = await HandleTeamWaitAsync(run, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                checkpoint = teamWait.Checkpoint;
                if (teamWait.Parked) return new ToolTaskExecutionResult(checkpoint, Waiting: false, Result: null);
                continue;
            }
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsTeamPlan(pendingTurn.ToolId))
            {
                checkpoint = await HandleTeamPlanAsync(run, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsTeamIntegrate(pendingTurn.ToolId))
            {
                checkpoint = await HandleTeamIntegrateAsync(run, configuration, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Legacy Core-owned virtual tools (preserved for backward compatibility)
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsTaskWait(pendingTurn.ToolId))
            {
                var wait = await HandleTaskWaitAsync(run, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                checkpoint = wait.Checkpoint;
                if (wait.Parked) return new ToolTaskExecutionResult(checkpoint, Waiting: false, Result: null);
                continue;
            }
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsPlanUpdate(pendingTurn.ToolId))
            {
                checkpoint = await HandlePlanUpdateAsync(run, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsPlanRead(pendingTurn.ToolId))
            {
                checkpoint = await HandlePlanReadAsync(run, checkpoint, task, pendingTurn, descriptors, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (pendingTurn is not null && CoreVirtualToolPolicy.IsCorrectionTrace(pendingTurn.ToolId))
            {
                var correction = await HandleCorrectionTraceAsync(run, configuration, checkpoint, pendingTurn, cancellationToken).ConfigureAwait(false);
                checkpoint = correction.Checkpoint;
                if (correction.Parked) return new ToolTaskExecutionResult(checkpoint, Waiting: false, Result: null);
                continue;
            }

            if (pendingTurn is not null)
            {
                ToolDispatchResultDto dispatch;
                if (string.IsNullOrWhiteSpace(pendingTurn.ExecutionId))
                {
                    if (!TryParseJsonObject(pendingTurn.ArgumentsJson, out var parameters))
                    {
                        checkpoint = await FeedToolFailureBackAsync(run, task, pendingTurn,
                            RunErrorTaxonomy.InvalidToolArguments, "The worker returned invalid tool arguments.",
                            checkpoint, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var toolCallKey = $"run:{run.RunId}:task:{task.TaskKey}:attempt:{task.Attempt}:round:{task.ToolRounds}:call:{pendingTurn.CallId}";
                    dispatch = await dispatcher.PrepareAsync(new ToolDispatchRequestDto
                    {
                        RunId = run.RunId,
                        TaskId = task.TaskId.ToString(),
                        AgentInstanceId = worker.Id.ToString(),
                        ToolId = pendingTurn.ToolId,
                        ToolCallKey = toolCallKey,
                        LeaseUses = 1,
                        LaneKey = LaneKeyOf(task),
                        Params = parameters
                    }, cancellationToken).ConfigureAwait(false);
                    pendingTurn.ExecutionId = dispatch.ExecutionId;
                    pendingTurn.ApprovalId = dispatch.ApprovalId;
                    pendingTurn.DispatchStatus = dispatch.Status;
                    task.PendingToolExecutionId = dispatch.ExecutionId;
                    task.PendingToolApprovalId = dispatch.ApprovalId;
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-prepared", cancellationToken).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(dispatch.ExecutionId))
                    {
                        checkpoint = await FeedToolFailureBackAsync(run, task, pendingTurn,
                            dispatch.ErrorCategory ?? RunErrorTaxonomy.ToolPrepareFailed,
                            dispatch.Message ?? "The tool call could not be prepared.",
                            checkpoint, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                if (dispatcher is not ILeaseFencedToolDispatcher fencedDispatcher)
                {
                    throw new InvalidOperationException(
                        "The configured tool dispatcher does not support run-lease fenced execution.");
                }
                var executionAuthority = await RequireToolExecutionAuthorityAsync(
                    Guid.Parse(run.RunId),
                    cancellationToken).ConfigureAwait(false);
                dispatch = await fencedDispatcher.ResumeAsync(
                    pendingTurn.ExecutionId!,
                    executionAuthority,
                    cancellationToken).ConfigureAwait(false);
                if (string.Equals(dispatch.ErrorCategory, RunErrorTaxonomy.RunLeaseLost, StringComparison.Ordinal))
                {
                    throw new RunCheckpointConflictException(
                        run.RunId,
                        checkpoint.CheckpointRevision,
                        checkpoint.CheckpointRevision);
                }

                pendingTurn.DispatchStatus = dispatch.Status;
                if (dispatch.Status == ToolDispatchStatus.AwaitingApproval && dispatch.ParkExpired)
                {
                    SyncLanes(checkpoint);
                    ClearPendingToolExecution(task, RunErrorTaxonomy.ApprovalExpired);
                    task.Status = "failed";
                    task.ResultStatus = "failed";
                    task.ResultSummary = "Approval expired while the run was unattended.";
                    task.CompletedAt = DateTimeOffset.UtcNow;
                    checkpoint.Phase = "awaiting_user";
                    checkpoint.AwaitingApprovalExpiryReview = true;
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-park-expired", cancellationToken).ConfigureAwait(false);

                    var expiryReason = $"Approval for tool '{pendingTurn.ToolId}' expired its decision window while the run was unattended.";
                    await TrySetRunStatusAsync(run.RunId, "awaiting_user", expiryReason, cancellationToken).ConfigureAwait(false);
                    await AppendEventAsync(Guid.Parse(run.RunId), "worker.failed",
                        "The tool task failed after its approval expired unattended.",
                        new { run_id = run.RunId, task_id = task.TaskId, task_key = task.TaskKey, error_category = RunErrorTaxonomy.ApprovalExpired },
                        cancellationToken).ConfigureAwait(false);
                    await AppendEventAsync(Guid.Parse(run.RunId), "supervision.user_review.requested",
                        "The run is waiting for a user decision after an approval expiry.",
                        new { run_id = run.RunId, lane_key = "main", decision = "escalate", reasons = new[] { expiryReason }, options = new[] { "continue", "correct", "cancel" } },
                        cancellationToken).ConfigureAwait(false);
                    return new ToolTaskExecutionResult(checkpoint, Waiting: true, Result: null);
                }

                if (dispatch.Status is ToolDispatchStatus.AwaitingApproval or ToolDispatchStatus.AwaitingResume
                    or ToolDispatchStatus.AwaitingDelegate or ToolDispatchStatus.AwaitingUser)
                {
                    if (dispatch.Status is ToolDispatchStatus.AwaitingApproval or ToolDispatchStatus.AwaitingDelegate or ToolDispatchStatus.AwaitingUser)
                    {
                        var runStatus = dispatch.Status switch
                        {
                            ToolDispatchStatus.AwaitingDelegate => "awaiting_delegate",
                            ToolDispatchStatus.AwaitingUser => "awaiting_user",
                            _ => "awaiting_approval"
                        };
                        await TrySetRunStatusAsync(run.RunId, runStatus, dispatch.Message, cancellationToken).ConfigureAwait(false);
                        if (dispatch.Status == ToolDispatchStatus.AwaitingApproval)
                        {
                            await NotifyCompanionBackflowAsync(
                                run,
                                configuration,
                                checkpoint,
                                "awaiting_approval",
                                dispatch.Message ?? $"Tool '{pendingTurn.ToolId}' requires approval.",
                                cancellationToken,
                                pendingTurn).ConfigureAwait(false);
                        }
                    }
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting", cancellationToken).ConfigureAwait(false);
                    return new ToolTaskExecutionResult(checkpoint, Waiting: true, Result: null);
                }

                if (dispatch.Status != ToolDispatchStatus.Completed)
                {
                    var category = dispatch.ErrorCategory ?? dispatch.Status;
                    var summary = dispatch.Message ?? $"Tool '{pendingTurn.ToolId}' returned {dispatch.Status}.";
                    pendingTurn.DispatchStatus = dispatch.Status;
                    pendingTurn.ErrorCategory = category;
                    pendingTurn.ResultJson = ToolCallFailureJson(pendingTurn.ToolId, category, summary, dispatch);
                    ClearPendingToolExecution(task);
                    checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-result", cancellationToken).ConfigureAwait(false);
                    await AppendEventAsync(Guid.Parse(run.RunId), "worker.tool_failed",
                        $"Tool '{pendingTurn.ToolId}' failed ({category}); the failure was returned to the worker as a tool result.",
                        new
                        {
                            run_id = run.RunId,
                            task_id = task.TaskId,
                            task_key = task.TaskKey,
                            lane_key = LaneKeyOf(task),
                            tool_id = pendingTurn.ToolId,
                            call_id = pendingTurn.CallId,
                            execution_id = pendingTurn.ExecutionId,
                            dispatch_status = dispatch.Status,
                            error_category = category,
                            message = summary,
                            consecutive_errors = TrailingConsecutiveErrors(task),
                            returned_to_worker = true
                        }, cancellationToken, task.TaskId, idempotencyKey: $"tool-failure:{task.TaskId}:{pendingTurn.CallId}").ConfigureAwait(false);
                    continue;
                }

                var snapshot = await executionCoordinator.FindAsync(
                    Guid.Parse(pendingTurn.ExecutionId!), cancellationToken).ConfigureAwait(false);
                var resultJson = dispatch.Result?.GetRawText();
                if (string.IsNullOrWhiteSpace(resultJson)) resultJson = snapshot?.ResultJson ?? "null";
                if (resultJson.Length > InlineToolResultChars && !pendingTurn.IsSpaceWorktreeProvisioning
                    && string.Equals(snapshot?.ResultJson, resultJson, StringComparison.Ordinal))
                {
                    pendingTurn.ResultJson = ToolResultHead(resultJson, pendingTurn.ExecutionId!);
                    pendingTurn.ResultOnExecution = true;
                }
                else
                {
                    pendingTurn.ResultJson = resultJson;
                }

                pendingTurn.DispatchStatus = ToolDispatchStatus.Completed;
                pendingTurn.ToolSuccess = snapshot?.ToolSuccess;
                if (pendingTurn.ToolSuccess != false)
                {
                    checkpoint = await RecordSpaceToolResultAsync(run, configuration, checkpoint, task, pendingTurn, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(task.TeammateHandle) && !string.IsNullOrWhiteSpace(task.TaskWorktreePath))
                    {
                        var tm = checkpoint.Teammates.FirstOrDefault(t =>
                            string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase));
                        if (tm is not null)
                        {
                            tm.WorktreePath = task.TaskWorktreePath;
                            tm.BranchName = task.TaskWorktreeBranch ?? tm.BranchName;
                            tm.UpdatedAt = DateTimeOffset.UtcNow;
                        }
                    }
                    if (string.Equals(pendingTurn.ToolId, "browser_handoff", StringComparison.OrdinalIgnoreCase))
                    {
                        await NotifyCompanionBackflowAsync(
                            run,
                            configuration,
                            checkpoint,
                            "browser_handoff",
                            "Browser task space handed off for human control.",
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                if (pendingTurn.ToolSuccess == false)
                {
                    pendingTurn.ErrorCategory = RunErrorTaxonomy.ToolError;
                }

                if (checkpoint.Correction is not null
                    && CorrectionRuntimePolicy.IsStructuredInvestigation(configuration.RuntimePolicy))
                {
                    var frozenTool = configuration.ToolManifest.FirstOrDefault(item =>
                        string.Equals(item.Id, pendingTurn.ToolId, StringComparison.OrdinalIgnoreCase));
                    CorrectionTracePolicy.RecordToolFact(
                        checkpoint.Correction,
                        pendingTurn.ToolId,
                        pendingTurn.ArgumentsJson,
                        new ToolDispatchResultDto
                        {
                            Status = pendingTurn.ToolSuccess == false ? "failed" : "completed",
                            ExecutionId = pendingTurn.ExecutionId,
                            Result = dispatch.Result,
                        },
                        frozenTool?.MutatesWorkspace ?? WorkspaceToolCatalog.MutatingMembers([pendingTurn.ToolId]).Count > 0,
                        task.WorkerHandle,
                        snapshot?.WorkspaceSnapshotId);
                    CorrectionTracePolicy.EvaluateCompletion(
                        checkpoint.Correction,
                        configuration.RuntimePolicy!,
                        checkpoint.UserGoal,
                        checkpoint.Correction.Completion.ModifiedArtifacts.Count > 0);
                }

                task.PendingToolExecutionId = null;
                task.PendingToolApprovalId = null;
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-result", cancellationToken).ConfigureAwait(false);
                if (checkpoint.Correction is not null)
                    await AppendCorrectionTraceEventAsync(run, checkpoint.Correction, cancellationToken).ConfigureAwait(false);
                continue;
            }

            WorkerModelTurn model;
            try
            {
                IReadOnlyList<WorkerToolDescriptor> declaredTools = descriptors;
                string? closeoutPrompt = null;
                if (task.CloseoutRequested)
                {
                    declaredTools = [];
                    closeoutPrompt = CloseoutPrompts.Build(new CloseoutPrompts.CloseoutContext(
                        task.CloseoutCategory ?? RunErrorTaxonomy.Runtime,
                        task.CloseoutReason ?? "The worker budget was exhausted before the task could finish.",
                        task.CloseoutHardCeiling,
                        task.ToolRounds,
                        roundLimit,
                        configuration.Tools.ResolveTaskRoundLimitSource(task.Category, task.Risk),
                        task.ToolTurns.Count,
                        configuration.Tools.MaxToolCalls,
                        task.TokensUsed,
                        configuration.Context.EffectiveTaskTokenBudget,
                        Maf18RuntimeAdapter.BudgetTokens(checkpoint.ModelUsage),
                        configuration.Context.RunTokenBudget,
                        task.ToolTurns
                            .TakeLast(3)
                            .Select(turn => turn.ToolId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToArray()));
                }
                model = await GetWorkerModelTurnAsync(run, configuration, checkpoint, task, worker, declaredTools, cancellationToken, closeoutPrompt).ConfigureAwait(false);
            }
            catch (RunInterruptedException interrupted) when (!cancellationToken.IsCancellationRequested)
            {
                Interrupts?.TakePending(Guid.Parse(run.RunId));
                checkpoint = await RecordWorkerInterruptionAsync(run, checkpoint, task, interrupted.Request, interrupted.PartialText, [], cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (InvalidDataException ex)
            {
                return FailedToolTask(checkpoint, task, worker, WorkerAssignmentInvalidCategory, SafeError(ex));
            }

            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, model.Usage);
            AccumulateTaskTokens(task, model.Usage);
            if (!model.IsAvailable || model.Error is not null)
            {
                return FailedToolTask(checkpoint, task, worker, RunErrorTaxonomy.ModelUnavailable, model.Error ?? "Worker model is unavailable.");
            }
            if (task.CloseoutRequested)
            {
                return CompleteCloseout(checkpoint, task, worker, model);
            }

            task.ToolRounds++;
            if (model.Calls.Count == 0 && string.IsNullOrWhiteSpace(model.Text))
            {
                task.ConsecutiveEmptyResponses++;
            }
            else
            {
                task.ConsecutiveEmptyResponses = 0;
            }

            await EmitToolRoundEventAsync(run, checkpoint, task, configuration, roundLimit, model, cancellationToken).ConfigureAwait(false);

            if (roundLimit > 0 && task.ToolRounds > roundLimit)
            {
                RequestCloseout(task, RunErrorTaxonomy.ToolRoundLimit,
                    $"The worker exhausted its tool-round budget ({DescribeToolBudget(configuration, task, roundLimit)}).");
            }

            if (!task.CloseoutRequested && _loopGuard is { } guard)
            {
                var decision = await guard.EvaluateAsync(
                    run.SessionId.ToString(),
                    run.RunId.ToString(),
                    new LoopGuardContext
                    {
                        Iteration = task.ToolRounds - 1,
                        MaxIterations = roundLimit,
                        TokensUsed = task.TokensUsed,
                        TokenBudget = configuration.Context.EffectiveTaskTokenBudget,
                        ToolCallCount = task.ToolTurns.Count,
                        MaxToolCalls = configuration.Tools.MaxToolCalls,
                        RecentToolCallFingerprints = TrailingFingerprints(task, FingerprintTail),
                        ConsecutiveErrors = TrailingConsecutiveErrors(task)
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!decision.ShouldContinue)
                {
                    RequestCloseout(task, decision.Category ?? RunErrorTaxonomy.ToolLoopDetected,
                        decision.Reason ?? "The loop guard stopped further tool rounds for this task.",
                        decision.HardCeiling);
                }
            }

            if (!task.CloseoutRequested)
            {
                var runTokens = Maf18RuntimeAdapter.BudgetTokens(checkpoint.ModelUsage);
                if (configuration.Context.RunTokenBudget > 0 && runTokens >= configuration.Context.RunTokenBudget)
                {
                    await ObserveAutonomousEventAsync(run, configuration, GovernanceTopics.BudgetTick,
                        $"The run exhausted its token budget ({runTokens}/{configuration.Context.RunTokenBudget}).").ConfigureAwait(false);
                    RequestCloseout(task, RunErrorTaxonomy.RunTokenBudgetExhausted,
                        $"The run exhausted its token budget ({runTokens}/{configuration.Context.RunTokenBudget}).");
                }
            }

            if (!task.CloseoutRequested && task.ConsecutiveEmptyResponses >= MaxConsecutiveEmptyResponses)
            {
                RequestCloseout(task, RunErrorTaxonomy.EmptyResponseLimit,
                    $"The worker returned {task.ConsecutiveEmptyResponses} consecutive empty responses.");
            }

            if (task.CloseoutRequested)
            {
                await EmitCloseoutRequestedAsync(run, checkpoint, task, configuration, roundLimit, model, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-closeout", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (model.Calls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(model.Text))
                {
                    var result = BuildWorkerStepResult(task, worker.Id, model.Text);
                    return new ToolTaskExecutionResult(checkpoint, Waiting: false,
                        new TaskExecutionResult(task.TaskId, worker.Id, result.Status, result));
                }
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-empty-response", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var existingCallIds = task.ToolTurns.Select(item => item.CallId).ToHashSet(StringComparer.Ordinal);
            foreach (var call in model.Calls)
            {
                if (!existingCallIds.Add(call.CallId))
                {
                    return FailedToolTask(checkpoint, task, worker, RunErrorTaxonomy.DuplicateToolCall, $"The worker reused tool call id '{call.CallId}'.", call.ToolId);
                }
                task.ToolTurns.Add(new WorkerToolTurn
                {
                    CallId = call.CallId,
                    ToolId = call.ToolId,
                    ArgumentsJson = call.ArgumentsJson,
                    AssistantText = task.ToolTurns.Count == 0 ? model.Text : null
                });
            }

            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-calls", cancellationToken).ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<IReadOnlyList<WorkerToolDescriptor>> GetWorkerToolsAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        CancellationToken cancellationToken)
    {
        var catalog = _services.GetRequiredService<IFrozenToolManifestCatalog>();
        var authorized = await catalog.ListAuthorizedAsync(
            Guid.Parse(run.RunId), task.TaskId, worker.Id, cancellationToken).ConfigureAwait(false);
        if (configuration.SpaceOptions is not null)
            authorized = authorized.Where(entry => !SpaceCompositionPolicy.IsWorktreeManagement(entry.Id)).ToArray();

        if (task.RequiredTools.Count == 0)
        {
            return authorized
                .Select(entry => new WorkerToolDescriptor(entry.Id, entry.Description, entry.InputSchema))
                .ToArray();
        }

        var byId = authorized.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var entries = new List<WorkerToolDescriptor>(task.RequiredTools.Count);
        foreach (var required in task.RequiredTools)
        {
            if (!byId.TryGetValue(required, out var entry))
                throw new InvalidDataException($"Task '{task.TaskKey}' requires tool '{required}', which is not authorized for the assigned worker instance.");
            entries.Add(new WorkerToolDescriptor(entry.Id, entry.Description, entry.InputSchema));
        }
        return entries;
    }

    internal static string ToolResultHead(string result, string executionId)
    {
        var length = Math.Min(ToolResultHeadChars, result.Length);
        if (length < result.Length && char.IsHighSurrogate(result[length - 1])) length--;
        return string.Concat(result.AsSpan(0, length),
            $"\n…[{result.Length} characters in total; the full result is kept with tool execution {executionId}]");
    }

    private async Task<IReadOnlyDictionary<string, string>?> LoadToolResultsKeptOnExecutionAsync(
        DurableTaskNode task, CancellationToken cancellationToken)
    {
        Dictionary<string, string>? results = null;
        foreach (var turn in task.ToolTurns)
        {
            if (!turn.ResultOnExecution || !Guid.TryParse(turn.ExecutionId, out var executionId)) continue;
            var execution = await _services.GetRequiredService<IToolExecutionCoordinator>()
                .FindAsync(executionId, cancellationToken).ConfigureAwait(false);
            if (execution?.ResultJson is { Length: > 0 } full)
                (results ??= new Dictionary<string, string>(StringComparer.Ordinal))[turn.CallId] = full;
            else
                _logger.TryLogWarning("Tool execution {ExecutionId} no longer holds its result; the worker reads the checkpoint's head.", executionId);
        }
        return results;
    }

    private async Task ApplyTaskResultAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        TaskExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var task = checkpoint.Tasks.FirstOrDefault(item => item.TaskId == execution.TaskId)
            ?? throw new InvalidDataException($"Task '{execution.TaskId}' is missing from the checkpoint.");
        task.Status = execution.Status;
        task.ResultStatus = execution.Result.Status;
        task.ResultSummary = execution.Result.Summary;
        task.Evidence = execution.Result.Evidence.ToList();
        foreach (var verdict in execution.Result.CriterionVerdicts)
        {
            task.CriteriaVerdicts.Add(new CriterionVerdict(
                verdict.Criterion,
                verdict.Satisfied,
                verdict.Evidence,
                $"worker:{execution.WorkerAgentId?.ToString("N") ?? "unknown"}",
                checkpoint.SupervisionRound,
                verdict.EvidenceRefs));
        }
        task.CompletedAt = execution.Status is "completed" or "failed" or "blocked" ? DateTimeOffset.UtcNow : null;
        if (execution.WorkerAgentId is { } workerId) task.WorkerAgentId = workerId;

        if (execution.Status is "completed" or "failed" or "blocked")
        {
            if (_resourceLeases is not null)
            {
                await _resourceLeases.ReleaseTaskAsync(runId, task.TaskId, cancellationToken).ConfigureAwait(false);
            }

            await SettleTeammateTurnAsync(run, runId, checkpoint, task, execution, cancellationToken).ConfigureAwait(false);

            var persistentWaitingTeammate = !string.IsNullOrWhiteSpace(task.TeammateHandle)
                && checkpoint.Teammates.Any(t =>
                    string.Equals(t.Handle, task.TeammateHandle, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Status, "waiting", StringComparison.OrdinalIgnoreCase));
            if (!persistentWaitingTeammate && task.WorkerAgentId is { } finishedWorker)
            {
                await SetMemberOfflineAsync(finishedWorker, cancellationToken).ConfigureAwait(false);
            }

            var archivedEvidenceId = await ArchiveEvidenceAsync(
                run,
                task.TaskId,
                EvidenceKinds.TaskResult,
                task.Title,
                task.TeammateHandle ?? task.WorkerHandle ?? task.WorkerAgentSlug,
                TaskEvidenceText(task, ModeRuntimePolicyRegistry.Has(configuration.RuntimePolicy, ModeRuntimePolicyRegistry.EvidenceRefsRequiredFeature)),
                $"task:{task.TaskId:N}:{task.Attempt}",
                cancellationToken).ConfigureAwait(false);
            task.PendingToolExecutionId = null;
            task.PendingToolApprovalId = null;
            await SettleWorkerAsync(run, configuration, runId, task, execution, archivedEvidenceId, cancellationToken).ConfigureAwait(false);

            var (wtPath, wtBranch) = ResolveTaskWorktree(checkpoint, task);
            await TrySyncTaskTransitionToSessionPlanAsync(
                checkpoint.SessionId,
                runId,
                task,
                task.ResultSummary,
                task.Evidence,
                wtPath,
                wtBranch,
                cancellationToken).ConfigureAwait(false);
        }

        await _lifecycle.UpdateTaskSnapshotAsync(runId, new
        {
            id = task.TaskId,
            task_key = task.TaskKey,
            run_id = runId,
            title = task.Title,
            description = task.Description,
            status = task.Status,
            priority = task.Priority,
            risk = task.Risk,
            success_criteria = task.SuccessCriteria,
            dependencies = task.Dependencies,
            required_capabilities = task.RequiredCapabilities,
            required_tools = task.RequiredTools,
            worker_agent_id = task.WorkerAgentId,
            result_summary = task.ResultSummary,
            evidence = task.Evidence,
            criteria_verdicts = task.CriteriaVerdicts,
            updated_at = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);

        var eventType = execution.Status switch
        {
            "completed" => "worker.completed",
            "blocked" => "worker.blocked",
            _ => "worker.failed"
        };
        await AppendEventAsync(runId, eventType, execution.Result.Summary, new
        {
            task_id = task.TaskId,
            task_key = task.TaskKey,
            handle = task.TeammateHandle ?? task.WorkerHandle,
            agent_instance_id = execution.WorkerAgentId,
            status = execution.Result.Status,
            summary = execution.Result.Summary,
            evidence = execution.Result.Evidence,
            criteria_verdicts = execution.Result.CriterionVerdicts
        }, cancellationToken, task.TaskId).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(execution.Result.ProposedPatchContent))
        {
            var patchOutcome = await _conversations.ApplyContextPatchAsync(new ContextPatchRequest(
                Guid.Parse(run.SessionId),
                task.InputContextRevision,
                execution.Result.ProposedPatchContent,
                string.IsNullOrWhiteSpace(execution.Result.ProposedPatchSummary)
                    ? $"Worker patch for task '{task.TaskKey}'."
                    : execution.Result.ProposedPatchSummary,
                checkpoint.RunId,
                execution.WorkerAgentId,
                Kind: "supplement"), cancellationToken).ConfigureAwait(false);
            var patchApplied = string.Equals(patchOutcome.Status, "applied", StringComparison.OrdinalIgnoreCase);
            await AppendEventAsync(runId, patchApplied ? "context.patch.accepted" : "context.patch.stale",
                patchApplied
                    ? $"Worker patch for task '{task.TaskKey}' applied."
                    : $"Worker patch for task '{task.TaskKey}' was based on stale context and kept for audit.", new
                {
                    task_id = task.TaskId,
                    task_key = task.TaskKey,
                    source = "worker",
                    agent_instance_id = execution.WorkerAgentId,
                    patch_id = patchOutcome.PatchId,
                    base_context_revision = task.InputContextRevision,
                    context_revision = patchApplied ? patchOutcome.AppliedRevision : null
                }, cancellationToken, task.TaskId).ConfigureAwait(false);
        }

        if (execution.Status == "completed")
        {
            await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.TaskCompleted, run, configuration, checkpoint, cancellationToken,
                $"Task '{task.TaskKey}' ({task.TeammateHandle ?? task.WorkerHandle ?? task.WorkerAgentSlug}) completed: {Clip(task.ResultSummary, 240)}",
                "task", task.TaskId.ToString("N")).ConfigureAwait(false);
        }
        else if (execution.Status is "failed" or "blocked")
        {
            await RaiseStandingAsync(run, configuration, GovernanceTopics.TaskClosed,
                $"Task '{task.TaskKey}' ({task.TeammateHandle ?? task.WorkerHandle ?? task.WorkerAgentSlug}) ended {execution.Status}: {Clip(task.ResultSummary, 240)}",
                "task", task.TaskId.ToString("N"), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SettleWorkerAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        Guid runId,
        DurableTaskNode task,
        TaskExecutionResult execution,
        Guid? archivedEvidenceId,
        CancellationToken cancellationToken)
    {
        if (_workerSettlement is null
            || !ModeRuntimePolicyRegistry.Has(configuration.RuntimePolicy, ModeRuntimePolicyRegistry.WorkerSettlementFeature))
            return;

        var request = new WorkerSettlementRequest(
            task.TaskId,
            task.Title,
            task.Status,
            task.ResultSummary,
            task.TeammateHandle ?? task.WorkerHandle,
            task.WorkerAgentSlug,
            execution.WorkerAgentId ?? task.WorkerAgentId,
            task.Attempt,
            task.ToolTurns.Select(turn => turn.ExecutionId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToArray(),
            archivedEvidenceId);

        WorkerSettlementResult settled;
        try
        {
            settled = await _workerSettlement.SettleAsync(run, configuration, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not settle task {TaskId} of run {RunId}.", task.TaskId, run.RunId);
            return;
        }
        if (!settled.Applied) return;

        var summary = settled.NodeKind is { } kind
            ? $"Task '{task.TaskKey}' settled as a {kind} with {settled.EvidenceRefs.Count} evidence ref(s); {settled.IntentsUpdated} plan intent(s) closed."
            : $"Task '{task.TaskKey}' settled; {settled.IntentsUpdated} plan intent(s) closed.";
        await AppendEventAsync(runId, "worker.settled", summary, new
        {
            task_id = task.TaskId,
            task_key = task.TaskKey,
            agent_instance_id = request.WorkerAgentId,
            status = request.Status,
            node_kind = settled.NodeKind,
            evidence_refs = settled.EvidenceRefs,
            intents_updated = settled.IntentsUpdated
        }, cancellationToken, task.TaskId,
            idempotencyKey: $"worker-settled:{task.TaskId:N}:{task.Attempt}").ConfigureAwait(false);

        if (!await ObserveAutonomousEventAsync(run, configuration, GovernanceTopics.WorkerSettled, summary).ConfigureAwait(false))
        {
            await RaiseStandingAsync(run, configuration, GovernanceTopics.WorkerSettled, summary,
                "task", task.TaskId.ToString("N"), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ToolTaskExecutionResult FailedToolTask(
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RuntimeAgentInstance? worker,
        string category,
        string message,
        string? toolId = null)
    {
        ClearPendingToolExecution(task, category);
        return new(checkpoint, Waiting: false,
            FailedTaskResult(task, worker?.Id ?? task.WorkerAgentId, category, message, toolId));
    }

    private static TaskExecutionResult FailedTaskResult(DurableTaskNode task, Guid? workerId, string category, string message, string? toolId = null) =>
        new(task.TaskId, workerId, "failed", new StepResult
        {
            TaskNodeId = task.TaskId,
            AgentId = workerId?.ToString("N") ?? string.Empty,
            Status = "failed",
            Summary = message,
            Evidence = toolId is null
                ? [$"error_category:{category}", DispatchEvidence(task)]
                : [$"error_category:{category}", $"tool:{toolId}", DispatchEvidence(task)]
        });

    private static string DispatchEvidence(DurableTaskNode task)
    {
        var slug = string.IsNullOrWhiteSpace(task.WorkerAgentSlug) ? "unassigned" : task.WorkerAgentSlug;
        var reason = string.IsNullOrWhiteSpace(task.WorkerAssignmentReason) ? "unknown" : task.WorkerAssignmentReason;
        return $"dispatch:{slug}:{reason}";
    }

    private static string ToolCallFailureJson(string toolId, string category, string message, ToolDispatchResultDto? dispatch = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["tool_id"] = toolId,
            ["error_category"] = category,
            ["message"] = message,
        };
        if (dispatch?.Outcome is { Length: > 0 } outcome) body["outcome"] = outcome;
        if (dispatch?.Retryable is { } retryable) body["retryable"] = retryable;
        if (!string.IsNullOrWhiteSpace(dispatch?.PolicyRef)) body["policy_ref"] = dispatch!.PolicyRef;
        if (!string.IsNullOrWhiteSpace(dispatch?.AgentGuidance)) body["agent_guidance"] = dispatch!.AgentGuidance;
        return JsonSerializer.Serialize(body);
    }

    private async Task<FullDuplexCheckpointV1> FeedToolFailureBackAsync(
        RunState run,
        DurableTaskNode task,
        WorkerToolTurn turn,
        string category,
        string message,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        turn.DispatchStatus ??= category;
        turn.ErrorCategory ??= category;
        turn.ResultJson = ToolCallFailureJson(turn.ToolId, category, message);
        ClearPendingToolExecution(task);
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-failure", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "worker.tool_failed",
            $"Tool '{turn.ToolId}' failed ({category}); the failure was returned to the worker as a tool result.",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                lane_key = LaneKeyOf(task),
                tool_id = turn.ToolId,
                call_id = turn.CallId,
                dispatch_status = turn.DispatchStatus,
                error_category = category,
                message,
                consecutive_errors = TrailingConsecutiveErrors(task),
                returned_to_worker = true
            }, cancellationToken, task.TaskId, idempotencyKey: $"tool-failure:{task.TaskId}:{turn.CallId}").ConfigureAwait(false);
        return checkpoint;
    }

    private async Task<FullDuplexCheckpointV1> SkipUnstartedToolCallsAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RunInterruptRequest steering,
        CancellationToken cancellationToken)
    {
        var unstarted = task.ToolTurns
            .Where(turn => string.IsNullOrWhiteSpace(turn.ResultJson) && string.IsNullOrWhiteSpace(turn.ExecutionId))
            .ToList();
        if (unstarted.Count == 0) return checkpoint;
        foreach (var turn in unstarted)
        {
            turn.DispatchStatus = NotRunAfterInterrupt;
            turn.ResultJson = JsonSerializer.Serialize(new
            {
                tool_id = turn.ToolId,
                status = NotRunAfterInterrupt,
                reason = "interrupted",
                message = "Not run: the user sent new instructions before this call started. Read them and decide again whether it is still needed."
            });
        }
        return await RecordWorkerInterruptionAsync(run, checkpoint, task, steering, null,
            unstarted.Select(turn => turn.ToolId).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FullDuplexCheckpointV1> RecordWorkerInterruptionAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RunInterruptRequest steering,
        string? partialText,
        IReadOnlyList<string> skippedTools,
        CancellationToken cancellationToken)
    {
        var partial = string.IsNullOrWhiteSpace(partialText) ? null : Clip(partialText.Trim(), 2000);
        var interruptions = task.Interruptions ??= [];
        interruptions.Add(new WorkerInterruption(task.ToolTurns.Count, Clip(steering.Content.Trim(), 2000), partial, skippedTools.Count, DateTimeOffset.UtcNow));
        if (interruptions.Count > MaxTaskInterruptions) interruptions.RemoveRange(0, interruptions.Count - MaxTaskInterruptions);
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "worker-interrupted", cancellationToken).ConfigureAwait(false);
        var runId = Guid.Parse(run.RunId);
        await AppendEventAsync(runId, "worker.interrupted",
            $"The user interrupted {task.TeammateHandle ?? task.WorkerHandle ?? task.WorkerAgentSlug ?? task.TaskKey}; it continues with the new instructions.",
            new
            {
                run_id = runId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                handle = task.TeammateHandle ?? task.WorkerHandle,
                lane_key = LaneKeyOf(task),
                patch_id = steering.PatchId,
                during = skippedTools.Count > 0 ? "tool_calls" : "model_call",
                skipped_tools = skippedTools,
                partial_characters = partial?.Length ?? 0
            }, cancellationToken, task.TaskId,
            idempotencyKey: $"worker-interrupted:{task.TaskId}:{steering.PatchId:N}:{task.ToolTurns.Count}").ConfigureAwait(false);
        return checkpoint;
    }

    private async Task AppendRunInterruptedAsync(Guid runId, RunInterruptedException interrupted, CancellationToken cancellationToken)
    {
        try
        {
            await AppendEventAsync(runId, "run.interrupted",
                "The user interrupted the run; the step in progress is redone with the new instructions.",
                new
                {
                    run_id = runId,
                    patch_id = interrupted.Request.PatchId,
                    redone = "step",
                    partial_characters = interrupted.PartialText.Length
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not append the interruption event for run {RunId}.", runId);
        }
    }

    private static void ClearPendingToolExecution(DurableTaskNode task, string? category = null)
    {
        task.PendingToolExecutionId = null;
        task.PendingToolApprovalId = null;
        if (category is null) return;
        foreach (var turn in task.ToolTurns)
        {
            if (string.IsNullOrWhiteSpace(turn.ResultJson)) turn.ErrorCategory ??= category;
        }
    }

    private static void RequestCloseout(DurableTaskNode task, string category, string reason, bool hardCeiling = false)
    {
        if (task.CloseoutRequested) return;
        task.CloseoutRequested = true;
        task.CloseoutCategory = category;
        task.CloseoutReason = reason;
        task.CloseoutHardCeiling = hardCeiling;
    }

    private static ToolTaskExecutionResult CompleteCloseout(
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        RuntimeAgentInstance worker,
        WorkerModelTurn model)
    {
        var category = task.CloseoutCategory ?? RunErrorTaxonomy.Runtime;
        var reason = task.CloseoutReason ?? "The worker budget was exhausted before the task could finish.";
        var text = model.Text?.Trim();
        var outcome = string.IsNullOrWhiteSpace(text)
            ? new WorkerOutcomeProtocol.Parsed(
                "blocked",
                $"Stopped before completion ({category}): {reason}",
                Criteria: [],
                Explicit: false)
            : WorkerOutcomeProtocol.Parse(text, requireExplicit: true);
        var evaluation = EvaluateWorkerOutcome(task, outcome);
        var status = task.CloseoutHardCeiling && evaluation.Status == "completed"
            ? "blocked"
            : evaluation.Status;
        var summary = outcome.Summary;
        if (evaluation.MissingCriteria.Count > 0)
        {
            summary += "\nMissing criterion evidence: " + string.Join("; ", evaluation.MissingCriteria);
        }
        if (outcome.Status == "completed" && evaluation.UnresolvedToolFailure)
        {
            summary += "\nCompletion rejected: the latest explicit tool outcome reports success=false and no later successful tool outcome resolved it.";
        }
        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary)) evidence.Add(summary);
        evidence.Add(DispatchEvidence(task));
        evidence.Add($"worker_outcome:{status}:{OutcomeBasis(outcome)}");
        evidence.AddRange(outcome.Criteria.Select(item =>
            $"criterion_evidence:{item.Criterion}||{item.Evidence}"));
        evidence.AddRange(evaluation.MissingCriteria.Select(item =>
            $"missing_criterion_evidence:{item}"));
        if (evaluation.UnresolvedToolFailure) evidence.Add("tool_outcome_fact:unresolved_failure");
        evidence.Add($"closeout:{category}");
        evidence.Add($"closeout_reason:{reason}");
        if (task.CloseoutHardCeiling) evidence.Add("hard_ceiling:true");
        var result = new StepResult
        {
            TaskNodeId = task.TaskId,
            AgentId = worker.Id.ToString("N"),
            Status = status,
            Summary = summary,
            Evidence = evidence,
            CriterionVerdicts = evaluation.Verdicts
        };
        return new ToolTaskExecutionResult(checkpoint, Waiting: false,
            new TaskExecutionResult(task.TaskId, worker.Id, status, result, model.Usage));
    }

    private static void AccumulateTaskTokens(DurableTaskNode task, ModelUsage? usage)
    {
        var tokens = Maf18RuntimeAdapter.BudgetTokens(usage);
        if (tokens <= 0) return;
        task.TokensUsed = (int)Math.Min(int.MaxValue, task.TokensUsed + tokens);
    }

    private static int TrailingConsecutiveErrors(DurableTaskNode task)
    {
        var count = 0;
        for (var index = task.ToolTurns.Count - 1; index >= 0; index--)
        {
            if (string.IsNullOrWhiteSpace(task.ToolTurns[index].ErrorCategory)) break;
            count++;
        }
        return count;
    }

    private static IReadOnlyList<string> TrailingFingerprints(DurableTaskNode task, int max)
    {
        var tail = new List<string>(max);
        for (var index = task.ToolTurns.Count - 1; index >= 0 && tail.Count < max; index--)
        {
            var turn = task.ToolTurns[index];
            if (string.IsNullOrWhiteSpace(turn.ResultJson) || turn.DispatchStatus == NotRunAfterInterrupt) continue;
            tail.Add($"{turn.ToolId}:{turn.ArgumentsJson}");
        }
        tail.Reverse();
        return tail;
    }

    private static string DescribeToolBudget(FrozenRunConfigurationV1 configuration, DurableTaskNode task, int roundLimit)
    {
        var source = configuration.Tools.ResolveTaskRoundLimitSource(task.Category, task.Risk);
        var limit = roundLimit > 0
            ? $"{roundLimit} (source: {source})"
            : $"unlimited (source: {source}; max_tool_calls fuse: {configuration.Tools.MaxToolCalls})";
        var lastTools = task.ToolTurns
            .TakeLast(3)
            .Select(turn => turn.ToolId)
            .Where(id => !string.IsNullOrWhiteSpace(id));
        return $"effective round limit {limit}, category {task.Category ?? "(none)"}, risk {task.Risk}, "
            + $"rounds used {task.ToolRounds}, calls issued {task.ToolTurns.Count}, "
            + $"last tools [{string.Join(", ", lastTools)}]";
    }

    private async Task EmitToolRoundEventAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        FrozenRunConfigurationV1 configuration,
        int roundLimit,
        WorkerModelTurn model,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = $"worker-tool-round:{run.RunId}:{task.TaskId:N}:{task.ToolRounds}";
        await AppendEventAsync(Guid.Parse(run.RunId), "worker.tool_round",
            $"Worker round {task.ToolRounds}: {model.Calls.Count} call(s) this round, {task.ToolTurns.Count + model.Calls.Count} in total.",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                lane_key = LaneKeyOf(task),
                round = task.ToolRounds,
                effective_round_limit = roundLimit,
                round_limit_source = configuration.Tools.ResolveTaskRoundLimitSource(task.Category, task.Risk),
                task_category = task.Category,
                calls_this_round = model.Calls.Count,
                calls_total = task.ToolTurns.Count + model.Calls.Count,
                task_tokens_used = task.TokensUsed,
                task_token_budget = configuration.Context.EffectiveTaskTokenBudget,
                run_tokens_used = Maf18RuntimeAdapter.BudgetTokens(checkpoint.ModelUsage),
                run_token_budget = configuration.Context.RunTokenBudget
            }, cancellationToken, taskId: task.TaskId, idempotencyKey: idempotencyKey).ConfigureAwait(false);
    }

    private async Task EmitCloseoutRequestedAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task,
        FrozenRunConfigurationV1 configuration,
        int roundLimit,
        WorkerModelTurn model,
        CancellationToken cancellationToken)
    {
        await AppendEventAsync(Guid.Parse(run.RunId), "worker.budget_exhausted",
            task.CloseoutReason ?? "The worker stopped tool use and is wrapping up.",
            new
            {
                run_id = run.RunId,
                task_id = task.TaskId,
                task_key = task.TaskKey,
                lane_key = LaneKeyOf(task),
                category = task.CloseoutCategory,
                reason = task.CloseoutReason,
                hard_ceiling = task.CloseoutHardCeiling,
                effective_round_limit = roundLimit,
                round_limit_source = configuration.Tools.ResolveTaskRoundLimitSource(task.Category, task.Risk),
                task_category = task.Category,
                rounds_used = task.ToolRounds,
                calls_issued = task.ToolTurns.Count,
                dropped_calls = model.Calls.Count,
                task_tokens_used = task.TokensUsed,
                task_token_budget = configuration.Context.EffectiveTaskTokenBudget,
                run_tokens_used = Maf18RuntimeAdapter.BudgetTokens(checkpoint.ModelUsage),
                run_token_budget = configuration.Context.RunTokenBudget,
                max_tool_calls = configuration.Tools.MaxToolCalls
            }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseJsonObject(string json, out JsonElement parameters)
    {
        parameters = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) return false;
            parameters = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string[] ReadStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return arrayElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static string BoundText(string value, int limit)
    {
        var text = value.Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }

    private static string Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "(no summary)" : value.Length <= max ? value : value[..max] + "…";

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string TruncateEvidence(string value)
    {
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxRunEvidenceLineLength ? collapsed : collapsed[..MaxRunEvidenceLineLength] + "…";
    }

    private static StepResult BuildWorkerStepResult(DurableTaskNode task, Guid workerId, string text)
    {
        var requireExplicit = !task.IsCoordinatorTask && string.IsNullOrWhiteSpace(task.TeammateMode);
        var outcome = WorkerOutcomeProtocol.Parse(text, requireExplicit: requireExplicit);
        var evaluation = EvaluateWorkerOutcome(task, outcome);
        var summary = ExtractContextPatch(outcome.Summary, out var patchSummary, out var patchContent);
        if (evaluation.MissingCriteria.Count > 0)
        {
            summary += "\nMissing criterion evidence: " + string.Join("; ", evaluation.MissingCriteria);
        }
        if (outcome.Status == "completed" && evaluation.UnresolvedToolFailure)
        {
            summary += "\nCompletion rejected: the latest explicit tool outcome reports success=false and no later successful tool outcome resolved it.";
        }
        var taskId = task.TaskId;
        var evidence = new List<string>
        {
            summary,
            DispatchEvidence(task),
            $"worker_outcome:{evaluation.Status}:{OutcomeBasis(outcome)}"
        };
        evidence.AddRange(outcome.Criteria.Select(item =>
            $"criterion_evidence:{item.Criterion}||{item.Evidence}"));
        evidence.AddRange(evaluation.MissingCriteria.Select(item =>
            $"missing_criterion_evidence:{item}"));
        if (evaluation.UnresolvedToolFailure) evidence.Add("tool_outcome_fact:unresolved_failure");
        return new StepResult
        {
            TaskNodeId = taskId,
            AgentId = workerId.ToString("N"),
            Status = evaluation.Status,
            Summary = summary,
            Evidence = evidence,
            CriterionVerdicts = evaluation.Verdicts,
            ProposedPatchSummary = patchSummary,
            ProposedPatchContent = patchContent
        };
    }

    private static string OutcomeBasis(WorkerOutcomeProtocol.Parsed outcome) =>
        outcome.ContradictedCompletion ? "explicit_contradiction"
            : outcome.Explicit ? "explicit"
            : outcome.MissingRequiredMarker ? "missing_marker"
            : "legacy_inferred";

    private static WorkerOutcomeEvaluation EvaluateWorkerOutcome(
        DurableTaskNode task,
        WorkerOutcomeProtocol.Parsed outcome)
    {
        var criteria = outcome.Criteria
            .Where(item => !string.IsNullOrWhiteSpace(item.Criterion) && !string.IsNullOrWhiteSpace(item.Evidence))
            .GroupBy(item => item.Criterion.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Evidence.Trim(), StringComparer.Ordinal);
        var verdicts = task.SuccessCriteria
            .Select(criterion =>
            {
                var normalized = criterion.Trim();
                var found = criteria.TryGetValue(normalized, out var evidence);
                return new LaneCriterionVerdict(task.TaskKey, criterion, found, found ? evidence : null);
            })
            .ToArray();
        var missing = verdicts
            .Where(item => !item.Satisfied || string.IsNullOrWhiteSpace(item.Evidence))
            .Select(item => item.Criterion)
            .ToArray();
        var unresolvedToolFailure = LatestEmbeddedToolOutcome(task) == false;
        var status = EnforceLatestToolOutcomeFact(task,
            outcome.Status == "completed" && missing.Length > 0 ? "blocked" : outcome.Status);
        return new WorkerOutcomeEvaluation(status, verdicts, missing, unresolvedToolFailure);
    }

    internal static string EnforceLatestToolOutcomeFact(DurableTaskNode task, string proposedStatus) =>
        string.Equals(proposedStatus, "completed", StringComparison.Ordinal)
        && LatestEmbeddedToolOutcome(task) == false
            ? "failed"
            : proposedStatus;

    internal static bool? LatestEmbeddedToolOutcome(DurableTaskNode task)
    {
        for (var index = task.ToolTurns.Count - 1; index >= 0; index--)
        {
            if (task.ToolTurns[index].ToolSuccess is { } success) return success;
        }
        return null;
    }

    private sealed record WorkerOutcomeEvaluation(
        string Status,
        IReadOnlyList<LaneCriterionVerdict> Verdicts,
        IReadOnlyList<string> MissingCriteria,
        bool UnresolvedToolFailure);

    private static string ExtractContextPatch(string text, out string? patchSummary, out string? patchContent)
    {
        patchSummary = null;
        patchContent = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (patchContent is null && trimmed.StartsWith(ContextPatchMarker, StringComparison.OrdinalIgnoreCase))
            {
                var payload = trimmed[ContextPatchMarker.Length..].Trim();
                var separator = payload.IndexOf("||", StringComparison.Ordinal);
                if (separator <= 0 || separator >= payload.Length - 2) continue;
                var summary = payload[..separator].Trim();
                var content = payload[(separator + 2)..].Trim();
                if (summary.Length == 0 || content.Length == 0) continue;
                patchSummary = summary;
                patchContent = content;
                continue;
            }
            kept.Add(line);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1])) kept.RemoveAt(kept.Count - 1);
        var cleaned = string.Join("\n", kept).Trim();
        return cleaned.Length == 0 ? text.Trim() : cleaned;
    }
}
