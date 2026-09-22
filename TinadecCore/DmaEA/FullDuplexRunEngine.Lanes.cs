using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.DmaEA;

/// <summary>
/// Lane orchestration split out of <see cref="FullDuplexRunEngine"/> (same class,
/// partial file). Lane machinery is currently unreachable at runtime — every mode
/// freezes a declared graph, so RunFreezeGate rejects lanes_enabled at admission
/// (graph_tier_lanes_unsupported) — while its contract surface (lane_key columns,
/// approvals, projections, UI) stays live. This file is a pure organization split:
/// no behavior, contract, event-name or serialization change.
/// </summary>
internal sealed partial class FullDuplexRunEngine : BackgroundService, IFullDuplexRunEngine
{
    /// <summary>
    /// One multi-lane tick: every non-terminal lane advances at most one step.
    /// Worker concurrency is budgeted globally across lanes, and a lane blocked
    /// on another lane's unfinished work parks as "waiting" instead of failing
    /// the run as an invalid graph. All lanes stay inside the single run lease.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> ExecuteLaneTickAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        SyncLanes(checkpoint);
        // An escalated lane keeps the run parked on awaiting_user; do not
        // overwrite it with "executing" at tick start.
        if (!checkpoint.Lanes.Any(lane => lane.Escalated))
        {
            await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
        }

        // A previous worker may have stopped after PrepareAsync persisted an
        // execution. Resume it before asking a model to produce another call.
        var pending = checkpoint.Tasks.FirstOrDefault(HasLivePendingExecution);
        if (pending is not null)
        {
            var resumed = await ResumePendingToolAsync(run, configuration, checkpoint, pending, cancellationToken).ConfigureAwait(false);
            checkpoint = resumed.Checkpoint;
            if (resumed.Waiting)
            {
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken, LaneKeyOf(pending)).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            if (resumed.Result is not null)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, resumed.Result, cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-resumed", cancellationToken, LaneKeyOf(pending)).ConfigureAwait(false);
            }
        }

        MarkBlockedDescendants(checkpoint.Tasks);
        SyncLanes(checkpoint);
        checkpoint = await ClassifyParkedLanesAsync(runId, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);

        // Per-lane ready sets dispatched under one global worker budget so the
        // active worker peak across lanes never exceeds MaxParallelWorkers.
        // Parked lanes (waiting/gate_review/escalated) never dispatch here: a
        // waiting lane first passes its gate review in the classification pass.
        var budget = Math.Max(1, configuration.Spawn.MaxParallelWorkers);
        var laneOrder = checkpoint.Lanes
            .Where(lane => !lane.Escalated
                && lane.Status is LaneStatus.Pending or LaneStatus.Executing
                && checkpoint.Tasks.Any(task => LaneKeyOf(task) == lane.LaneKey && task.Status is "pending" or "ready" or "running"))
            .Select(lane => lane.LaneKey)
            .OrderBy(key => string.Equals(key, "main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dispatched = new List<(string LaneKey, DurableTaskNode Task)>();
        var progress = true;
        while (dispatched.Count < budget && progress)
        {
            progress = false;
            foreach (var laneKey in laneOrder)
            {
                if (dispatched.Count >= budget) break;
                var ready = checkpoint.Tasks
                    .Where(item => LaneKeyOf(item) == laneKey
                        && item.Status is "pending" or "ready"
                        && item.Dependencies.All(dependency => checkpoint.Tasks.Any(other => other.TaskKey == dependency && other.Status == "completed")))
                    .OrderBy(item => item.Priority).ThenBy(item => item.TaskKey, StringComparer.Ordinal)
                    .FirstOrDefault(item => dispatched.All(pair => pair.Task != item));
                if (ready is null) continue;
                dispatched.Add((laneKey, ready));
                progress = true;
            }
        }

        if (dispatched.Count == 0)
        {
            if (checkpoint.Lanes.Any(lane => lane.Escalated))
            {
                // The parked run must keep saying awaiting_user: the escalation
                // wrote it once, but an approval cascade or a dispatcher resume can
                // legitimately flip the run back to executing afterwards. Re-assert
                // it on every idle tick (idempotent; skipped when the state machine
                // forbids it) so lane state and run state stay self-consistent.
                await TrySetRunStatusAsync(run.RunId, "awaiting_user", "A lane escalation is pending a user decision.", cancellationToken).ConfigureAwait(false);
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lanes-awaiting-user", cancellationToken).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            if (checkpoint.Tasks.All(item => item.Status is "completed" or "failed" or "blocked"))
            {
                checkpoint.Phase = "reviewing";
                return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "execution-complete", cancellationToken).ConfigureAwait(false);
            }
            if (checkpoint.Tasks.Any(HasLivePendingExecution))
            {
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting", cancellationToken).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            // Anything still dispatchable-but-not-parked is genuinely stuck: a
            // crashed "running" dispatch or an in-lane chain planning validation
            // failed to exclude. That stays an invalid graph. Parked lanes
            // (waiting/gate_review) and escalated lanes are expected here.
            var stuck = checkpoint.Tasks.Any(item => item.Status is "pending" or "ready" or "running"
                && IsLaneStuckEligible(checkpoint, LaneKeyOf(item)));
            if (stuck)
            {
                await FailRunAsync(runId, checkpoint, "invalid_task_graph", "No dependency-ready task remains in the persisted graph.", cancellationToken).ConfigureAwait(false);
                return checkpoint;
            }
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lanes-waiting", cancellationToken).ConfigureAwait(false);
            throw new RunAwaitingExternalDecisionException();
        }

        foreach (var (_, task) in dispatched)
        {
            task.Status = "running";
            task.Attempt++;
            task.InputContextRevision = checkpoint.ContextRevision;
            task.Waits = [];
        }
        foreach (var lane in checkpoint.Lanes)
        {
            if (dispatched.Any(pair => pair.LaneKey == lane.LaneKey)) lane.Status = LaneStatus.Executing;
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-dispatched", cancellationToken).ConfigureAwait(false);

        var plannerId = checkpoint.PlannerAgentId ?? throw new InvalidDataException("Planner instance is missing from checkpoint.");
        // A worker that cannot be resolved or created from the frozen
        // configuration fails only its own task; the rest of the tick continues.
        var workers = new Dictionary<Guid, RuntimeAgentInstance>();
        foreach (var (_, task) in dispatched)
        {
            try
            {
                workers[task.TaskId] = await GetOrCreateWorkerAsync(run, configuration, checkpoint, plannerId, task, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkerAssignmentException ex)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint,
                    FailedTaskResult(task, task.WorkerAgentId, WorkerAssignmentInvalidCategory, SafeError(ex)), cancellationToken).ConfigureAwait(false);
            }
        }
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "workers-assigned", cancellationToken).ConfigureAwait(false);

        // The text/tool split keys off the resolved declaration surface, not the
        // planner's required_tools hint (see ExecuteReadyTasksAsync).
        var surfaces = new Dictionary<Guid, IReadOnlyList<WorkerToolDescriptor>>();
        foreach (var (_, task) in dispatched)
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

        var textOnly = dispatched.Where(pair => surfaces.TryGetValue(pair.Task.TaskId, out var tools) && tools.Count == 0).Select(pair => pair.Task).ToList();
        var toolCapable = dispatched.Where(pair => surfaces.TryGetValue(pair.Task.TaskId, out var tools) && tools.Count != 0).Select(pair => pair.Task).ToList();
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
            SyncLanes(checkpoint);
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tasks-stale-after-context", cancellationToken).ConfigureAwait(false);
        }

        foreach (var task in toolCapable)
        {
            var result = await ExecuteToolTaskAsync(run, configuration, checkpoint, plannerId, task, cancellationToken, surfaces[task.TaskId]).ConfigureAwait(false);
            checkpoint = result.Checkpoint;
            if (result.Waiting)
            {
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "tool-awaiting-decision", cancellationToken, LaneKeyOf(task)).ConfigureAwait(false);
                throw new RunAwaitingExternalDecisionException();
            }
            if (result.Result is not null)
            {
                await ApplyTaskResultAsync(run, configuration, runId, checkpoint, result.Result, cancellationToken).ConfigureAwait(false);
            }
        }

        SyncLanes(checkpoint);
        return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lane-tick", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One classification pass over every non-terminal lane. A lane whose
    /// cross-lane waits are unmet parks as "waiting" with the target lane's
    /// facts hash frozen into each wait; a parked lane whose waits now hold
    /// enters "gate_review" and really calls this lane's planner — proceed
    /// lets it dispatch this tick, wait_more re-parks (facts unchanged means
    /// no progress is possible and escalates), and a proceed that contradicts
    /// code facts (stale hash or unsatisfied per-criterion verdicts) is
    /// discarded with gate.review.rejected_stale. The model can only tighten
    /// a gate, never loosen it. A lane whose tasks are all terminal gets its
    /// supervision verdict first so gates can read per-criterion evidence.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> ClassifyParkedLanesAsync(
        Guid runId,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        foreach (var lane in checkpoint.Lanes)
        {
            if (lane.Escalated) continue;
            var laneTasks = checkpoint.Tasks.Where(item => LaneKeyOf(item) == lane.LaneKey).ToList();
            if (laneTasks.Count == 0) continue;
            var terminal = laneTasks.All(item => item.Status is "completed" or "failed" or "blocked");

            if (terminal)
            {
                // 裁决落 lane: a finished lane gets its supervision verdict and
                // per-criterion evidence while other lanes may still run, so a
                // gate waiting on this lane reads real verdicts instead of
                // waiting for the run-level review that cannot start yet.
                if (lane.SupervisionDecision is null
                    && configuration.Supervision.RequiredBeforeFinal
                    && checkpoint.Lanes.Any(other => !string.Equals(other.LaneKey, lane.LaneKey, StringComparison.OrdinalIgnoreCase)
                        && checkpoint.Tasks.Any(task => LaneKeyOf(task) == other.LaneKey && task.Status is "pending" or "ready" or "running")))
                {
                    await SuperviseLaneAsync(runId, run, configuration, checkpoint, lane, laneTasks, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (laneTasks.Any(item => item.Status == "running" || HasLivePendingExecution(item))) continue;

            var unmet = UnmetLaneWaits(checkpoint, lane.LaneKey);
            if (unmet.Count > 0)
            {
                foreach (var task in laneTasks.Where(item => item.Status is "pending" or "ready"))
                {
                    task.Waits = [.. unmet];
                }
                if (!string.Equals(lane.Status, LaneStatus.Waiting, StringComparison.Ordinal))
                {
                    lane.Status = LaneStatus.Waiting;
                    await AppendEventAsync(runId, "orchestration.lane_waiting",
                        $"Lane '{lane.LaneKey}' is waiting on other lanes.",
                        new { lane_key = lane.LaneKey, waits = unmet.Select(wait => new { lane = wait.LaneKey, predicate = wait.Predicate, facts_hash = wait.ObservedFactsHash }) }, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            if (!string.Equals(lane.Status, LaneStatus.Waiting, StringComparison.Ordinal)) continue;

            // Waits hold: the parked lane must pass its gate before dispatching.
            lane.Status = LaneStatus.GateReview;
            var targetLanes = GateTargetLanes(checkpoint, lane.LaneKey);
            var factsHash = ComputeLaneFactsHash(checkpoint, targetLanes);
            await AppendEventAsync(runId, "orchestration.gate_review",
                $"Lane '{lane.LaneKey}' entered gate review.",
                new { lane_key = lane.LaneKey, facts_hash = factsHash, targets = targetLanes }, cancellationToken).ConfigureAwait(false);
            var gatePrompt = BuildGatePrompt(checkpoint, lane, targetLanes, factsHash);
            var gateDecision = await RunGateReviewAsync(run, configuration, checkpoint, lane, gatePrompt, cancellationToken).ConfigureAwait(false);

            if (gateDecision.Decision == "proceed")
            {
                var currentHash = ComputeLaneFactsHash(checkpoint, targetLanes);
                if (!string.Equals(currentHash, factsHash, StringComparison.Ordinal))
                {
                    // Facts moved while the gate call was in flight: re-park so
                    // waits re-freeze against the new facts.
                    await RejectStaleGateAsync(runId, lane, "facts hash changed during gate review", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!GateCodeFactsHold(checkpoint, targetLanes))
                {
                    await RejectStaleGateAsync(runId, lane, "model proceeded against unsatisfied criteria verdicts", cancellationToken).ConfigureAwait(false);
                    await EscalateLaneAsync(runId, run, lane, "Gate proceed rejected: per-criterion verdicts do not hold over code facts.", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                lane.LastGateFactsHash = factsHash;
                lane.Status = LaneStatus.Executing;
                await AppendEventAsync(runId, "orchestration.gate_review.completed",
                    $"Lane '{lane.LaneKey}' gate approved to proceed.",
                    new { lane_key = lane.LaneKey, decision = "proceed", reasons = gateDecision.Reasons }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (gateDecision.Decision == "wait_more"
                && !string.Equals(lane.LastGateFactsHash, factsHash, StringComparison.Ordinal))
            {
                lane.LastGateFactsHash = factsHash;
                lane.Status = LaneStatus.Waiting;
                await AppendEventAsync(runId, "orchestration.gate_review.completed",
                    $"Lane '{lane.LaneKey}' gate asked for more evidence.",
                    new { lane_key = lane.LaneKey, decision = "wait_more", reasons = gateDecision.Reasons }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            // escalate, unparsable output, or wait_more over unchanged facts
            // (nothing can change while parked): freeze only this lane.
            await EscalateLaneAsync(runId, run, lane,
                gateDecision.Reasons.Count > 0 ? string.Join("; ", gateDecision.Reasons) : "Gate review did not approve the lane to proceed.",
                cancellationToken).ConfigureAwait(false);
        }
        return checkpoint;
    }

    private async Task SuperviseLaneAsync(
        Guid runId,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableLane lane,
        List<DurableTaskNode> laneTasks,
        CancellationToken cancellationToken)
    {
        try
        {
            var supervisorDefinition = RequiredAgent(configuration.OperationAgents, "supervisor");
            var supervisorInstance = await EnsureSupervisorAgentAsync(run, configuration, checkpoint, supervisorDefinition, cancellationToken).ConfigureAwait(false);
            checkpoint.SupervisorAgentId = supervisorInstance.Id;
            var context = await BuildContextAsync(run, configuration, supervisorDefinition.Id, checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
            var assembly = await AssemblePromptAsync(configuration, supervisorDefinition, context, cancellationToken).ConfigureAwait(false);
            var supervisor = new SupervisionAgent(CreateModelFactory(configuration, checkpoint, supervisorDefinition,
                supervisorInstance.Id, supervisorInstance.ParentInstanceId), _logger);
            var plans = laneTasks.Select(ToPlannedTask).ToArray();
            var results = laneTasks.Select(item => new StepResult
            {
                TaskNodeId = item.TaskId,
                AgentId = item.WorkerAgentId?.ToString() ?? string.Empty,
                Status = item.ResultStatus ?? item.Status,
                Summary = item.ResultSummary ?? string.Empty,
                Evidence = item.Evidence
            }).ToArray();
            var verdict = await supervisor.ReviewAsync(checkpoint.UserGoal, plans, results, checkpoint.SupervisionRound, assembly.Instructions, cancellationToken).ConfigureAwait(false);
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, supervisor.LastUsage);
            lane.SupervisionRound = checkpoint.SupervisionRound;
            lane.SupervisionDecision = verdict.DecictionString();
            lane.SupervisionReasons = verdict.Reasons.ToList();
            MapCriterionVerdicts(checkpoint, verdict);
            await AppendEventAsync(runId, "supervision.lane_completed",
                $"Lane '{lane.LaneKey}' supervision decision: {lane.SupervisionDecision}.",
                new { lane_key = lane.LaneKey, decision = lane.SupervisionDecision, reasons = lane.SupervisionReasons }, cancellationToken).ConfigureAwait(false);
            if (verdict.Decision == SupervisionDecision.Escalate)
            {
                await EscalateLaneAsync(runId, run, lane, "Lane supervision escalated.", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lane supervision failed; escalating lane.");
            await EscalateLaneAsync(runId, run, lane, "Lane supervision failed: " + ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EscalateLaneAsync(
        Guid runId,
        RunState run,
        DurableLane lane,
        string reason,
        CancellationToken cancellationToken)
    {
        // A repeated escalation of an already-escalated lane is noise: the lane is
        // frozen, the run is already parked on the user, and the review request was
        // emitted with the first escalation.
        if (lane.Escalated) return;
        lane.Escalated = true;
        // The run may already wait on an approval or a user decision from another
        // lane; a rejected status transition must never escalate into a run failure.
        await TrySetRunStatusAsync(run.RunId, "awaiting_user", reason, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "supervision.user_review.requested",
            "A lane escalated and is waiting for a user decision while other lanes continue.",
            new { run_id = run.RunId, lane_key = lane.LaneKey, decision = "escalate", reasons = new[] { reason }, options = new[] { "continue", "correct", "cancel" } }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RejectStaleGateAsync(
        Guid runId,
        DurableLane lane,
        string reason,
        CancellationToken cancellationToken)
    {
        lane.Status = LaneStatus.Waiting;
        await AppendEventAsync(runId, "gate.review.rejected_stale",
            "A gate proceed was discarded because the frozen code facts no longer hold.",
            new { lane_key = lane.LaneKey, reason }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GateDecision> RunGateReviewAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        DurableLane lane,
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var plannerDefinition = RequiredAgent(configuration.ExecutionAgents, "task_planner");
            // A lane that plans through its own instance is also reviewed by its
            // own instance, so its gate sees the lane's context and the audit
            // trail attributes the verdict to the right agent. Lanes without
            // their own instance — including main — fall back to the shared one.
            var plannerInstanceId = checkpoint.LanePlannerIds.TryGetValue(lane.LaneKey, out var lanePlannerId)
                ? lanePlannerId
                : checkpoint.PlannerAgentId ?? throw new InvalidDataException("Planner instance is missing from checkpoint.");
            var factory = CreateModelFactory(configuration, checkpoint, plannerDefinition, plannerInstanceId, null);
            var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
            if (!resolution.IsAvailable)
            {
                return new GateDecision("escalate", [resolution.Error ?? "Chat route is unavailable."]);
            }
            var chatClient = await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false);
            using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
                chatClient,
                "operation.task_planner",
                "task_planner",
                "Reviews cross-lane gate evidence and may only tighten, never loosen, the gate.",
                new ChatOptions
                {
                    Instructions = "你是任务规划智能体，正在执行门控评审（gate review）。对照给出的代码事实与逐条验收裁决，判断本 lane 是否可以开始执行。只能收紧、不能放宽：任何一条验收裁决不满足或事实哈希不符都必须拒绝放行。仅输出 JSON 对象：{\"decision\":\"proceed|wait_more|escalate\",\"reasons\":[...]}，不要输出其他文字。"
                });
            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, Maf18RuntimeAdapter.NormalizeUsage(response.Usage));
            return ParseGateDecision(response.Text)
                ?? new GateDecision("escalate", ["Gate review response was unparsable; manual confirmation is required."]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GateDecision("escalate", ["Gate review failed: " + ex.Message]);
        }
    }

    private static string BuildGatePrompt(
        FullDuplexCheckpointV1 checkpoint,
        DurableLane lane,
        IReadOnlyCollection<string> targetLanes,
        string factsHash)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Lane: {lane.LaneKey}");
        builder.AppendLine($"门控评审：lane '{lane.LaneKey}' 的等待谓词已满足，请裁决是否放行开始执行。");
        foreach (var key in targetLanes)
        {
            builder.AppendLine($"等待目标 lane '{key}' 逐任务事实:");
            foreach (var task in checkpoint.Tasks
                .Where(item => string.Equals(LaneKeyOf(item), key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.TaskKey, StringComparer.Ordinal))
            {
                builder.AppendLine($"- [{task.TaskKey}] {task.Title} | criteria: {string.Join("; ", task.SuccessCriteria)} | status: {task.ResultStatus ?? task.Status} | result: {task.ResultSummary ?? "-"} | evidence: {string.Join(" / ", task.Evidence)}");
                var verdicts = task.CriteriaVerdicts
                    .OrderBy(verdict => verdict.Round)
                    .Select(verdict => $"{{\"criterion\":\"{verdict.Criterion}\",\"satisfied\":{verdict.Satisfied.ToString().ToLowerInvariant()},\"evidence\":\"{verdict.Evidence ?? string.Empty}\"}}");
                builder.AppendLine($"  verdicts: [{string.Join(", ", verdicts)}]");
            }
        }
        builder.AppendLine($"ObservedFactsHash: {factsHash}");
        builder.AppendLine("仅输出 JSON：{\"decision\":\"proceed|wait_more|escalate\",\"reasons\":[...]}");
        return builder.ToString();
    }

    private static GateDecision? ParseGateDecision(string? text)
    {
        // Tolerate reasoning prose/fences wrapped around the gate verdict object.
        foreach (var candidate in ModelOutputText.ExtractJsonCandidates(text, array: false))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<GateDecisionBody>(candidate, JsonOptions);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.Decision)) continue;
                var decision = parsed.Decision.Trim().ToLowerInvariant() switch
                {
                    "proceed" => "proceed",
                    "wait_more" => "wait_more",
                    _ => "escalate"
                };
                return new GateDecision(decision, parsed.Reasons ?? []);
            }
            catch (JsonException)
            {
                // Not the gate object; try the next balanced candidate.
            }
        }
        return null;
    }

    private sealed record GateDecision(string Decision, IReadOnlyList<string> Reasons);

    private sealed class GateDecisionBody
    {
        [System.Text.Json.Serialization.JsonPropertyName("decision")]
        public string? Decision { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("reasons")]
        public string[]? Reasons { get; set; }
    }

    /// <summary>
    /// The structured waits a lane must still honor: derived from unmet
    /// cross-lane dependencies (predicate lane_done, hash frozen now) plus any
    /// explicit wait already recorded on its tasks whose predicate does not
    /// yet hold.
    /// </summary>
    private static List<LaneWait> UnmetLaneWaits(FullDuplexCheckpointV1 checkpoint, string laneKey)
    {
        var laneTasks = checkpoint.Tasks.Where(item => LaneKeyOf(item) == laneKey).ToList();
        var unmet = new List<LaneWait>();
        void Add(LaneWait wait)
        {
            if (EvaluateLaneWait(checkpoint, wait)) return;
            if (!unmet.Any(existing => string.Equals(existing.LaneKey, wait.LaneKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Predicate, wait.Predicate, StringComparison.Ordinal)))
            {
                unmet.Add(wait);
            }
        }

        var dependencies = laneTasks.Where(item => item.Status is "pending" or "ready")
            .SelectMany(item => item.Dependencies)
            .Where(dependency => !checkpoint.Tasks.Any(other => other.TaskKey == dependency && other.Status == "completed"))
            .Select(dependency => checkpoint.Tasks.FirstOrDefault(other => other.TaskKey == dependency))
            .OfType<DurableTaskNode>()
            .Select(other => LaneKeyOf(other))
            .Where(key => !string.Equals(key, laneKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var key in dependencies)
        {
            Add(new LaneWait(key, LaneWait.LaneDone, [], ComputeLaneFactsHash(checkpoint, [key])));
        }
        foreach (var wait in laneTasks.SelectMany(item => item.Waits))
        {
            Add(wait);
        }
        return unmet;
    }

    /// <summary>Eval only trusts code facts, never model self-reports.</summary>
    private static bool EvaluateLaneWait(FullDuplexCheckpointV1 checkpoint, LaneWait wait)
    {
        var targetTasks = checkpoint.Tasks
            .Where(item => string.Equals(LaneKeyOf(item), wait.LaneKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var targetLane = checkpoint.Lanes.FirstOrDefault(item => string.Equals(item.LaneKey, wait.LaneKey, StringComparison.OrdinalIgnoreCase));
        var holds = wait.Predicate switch
        {
            LaneWait.LaneSupervisionPass => targetLane?.SupervisionDecision == "pass",
            _ => targetTasks.Count > 0
                && targetTasks.All(item => item.Status is "completed" or "failed" or "blocked")
                && targetTasks.All(item => item.Status == "completed")
        };
        if (!holds) return false;
        return wait.RequiredCriteria.Count == 0 || CriterionVerdictsHold(targetTasks, wait.RequiredCriteria);
    }

    /// <summary>
    /// Hard gate validation over code facts: every target lane fully completed
    /// and every latest-round criterion verdict satisfied with evidence.
    /// </summary>
    internal static bool GateCodeFactsHold(FullDuplexCheckpointV1 checkpoint, IReadOnlyCollection<string> targetLaneKeys)
    {
        foreach (var key in targetLaneKeys)
        {
            var tasks = checkpoint.Tasks
                .Where(item => string.Equals(LaneKeyOf(item), key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tasks.Count == 0) continue;
            if (tasks.Any(item => item.Status is not ("completed" or "failed" or "blocked"))) return false;
            if (tasks.Any(item => item.Status != "completed")) return false;
            if (!CriterionVerdictsHold(tasks, null)) return false;
        }
        return true;
    }

    private static bool CriterionVerdictsHold(List<DurableTaskNode> tasks, IReadOnlyList<string>? requiredCriteria)
    {
        var latest = tasks.SelectMany(item => item.CriteriaVerdicts.Select(verdict => (TaskKey: item.TaskKey, Verdict: verdict)))
            .GroupBy(pair => (pair.TaskKey, pair.Verdict.Criterion))
            .ToDictionary(group => group.Key, group => group.OrderBy(pair => pair.Verdict.Round).Last().Verdict);
        if (requiredCriteria is null)
        {
            foreach (var task in tasks)
            {
                foreach (var criterion in task.SuccessCriteria)
                {
                    if (!latest.TryGetValue((task.TaskKey, criterion), out var verdict)
                        || !verdict.Satisfied
                        || string.IsNullOrWhiteSpace(verdict.Evidence))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        foreach (var criterion in requiredCriteria)
        {
            var matches = latest.Values
                .Where(verdict => string.Equals(verdict.Criterion, criterion, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0
                || matches.Any(verdict => !verdict.Satisfied || string.IsNullOrWhiteSpace(verdict.Evidence)))
            {
                return false;
            }
        }
        return true;
    }

    private static List<string> GateTargetLanes(FullDuplexCheckpointV1 checkpoint, string laneKey) =>
        checkpoint.Tasks
            .Where(item => string.Equals(LaneKeyOf(item), laneKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Waits)
            .Select(wait => wait.LaneKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// SHA-256 over the checkpoint revision plus each target lane's supervision
    /// decision and task status vector — the observable-facts identity a gate
    /// is frozen against.
    /// </summary>
    private static string ComputeLaneFactsHash(FullDuplexCheckpointV1 checkpoint, IReadOnlyCollection<string> targetLaneKeys)
    {
        var payload = string.Join(";", targetLaneKeys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                var lane = checkpoint.Lanes.FirstOrDefault(item => string.Equals(item.LaneKey, key, StringComparison.OrdinalIgnoreCase));
                var tasks = checkpoint.Tasks
                    .Where(item => LaneKeyOf(item) == key)
                    .OrderBy(item => item.TaskKey, StringComparer.Ordinal)
                    .Select(item => $"{item.TaskKey}:{item.Status}:{item.ResultStatus ?? "-"}");
                return $"{key}|supervision={lane?.SupervisionDecision ?? "-"}|{string.Join(",", tasks)}";
            }));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"rev={checkpoint.CheckpointRevision}|{payload}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsLaneStuckEligible(FullDuplexCheckpointV1 checkpoint, string laneKey)
    {
        var lane = checkpoint.Lanes.FirstOrDefault(item => string.Equals(item.LaneKey, laneKey, StringComparison.OrdinalIgnoreCase));
        if (lane is null || lane.Escalated) return true;
        return lane.Status is not (LaneStatus.Waiting or LaneStatus.GateReview or LaneStatus.Completed);
    }

    /// <summary>
    /// The lanes a lane must wait on, or null when the lane is not waiting
    /// material: terminal, still dispatching (running or parked on a tool
    /// decision), without dispatchable tasks, blocked only inside itself, or
    /// blocked on a dependency that no planned task satisfies (a planning error
    /// that stays an invalid graph).
    /// </summary>
    internal static List<string>? CrossLaneWaitsFor(IReadOnlyList<DurableTaskNode> tasks, string laneKey)
    {
        var laneTasks = tasks.Where(item => LaneKeyOf(item) == laneKey).ToList();
        if (laneTasks.Count == 0) return null;
        if (laneTasks.All(item => item.Status is "completed" or "failed" or "blocked")) return null;
        if (laneTasks.Any(item => item.Status == "running" || HasLivePendingExecution(item))) return null;
        var waitingTasks = laneTasks.Where(item => item.Status is "pending" or "ready").ToList();
        if (waitingTasks.Count == 0) return null;

        var waits = waitingTasks
            .SelectMany(item => item.Dependencies)
            .Where(dependency => !tasks.Any(other => other.TaskKey == dependency && other.Status == "completed"))
            .Select(dependency => tasks.FirstOrDefault(other => other.TaskKey == dependency))
            .OfType<DurableTaskNode>()
            .Select(other => LaneKeyOf(other))
            .Where(key => !string.Equals(key, laneKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return waits.Count == 0 ? null : waits;
    }

    private static string LaneKeyOf(DurableTaskNode task) =>
        string.IsNullOrWhiteSpace(task.LaneKey) ? "main" : task.LaneKey.Trim();

    private static void SyncLanes(FullDuplexCheckpointV1 checkpoint)
    {
        foreach (var group in checkpoint.Tasks.GroupBy(item => LaneKeyOf(item), StringComparer.OrdinalIgnoreCase))
        {
            var lane = checkpoint.Lanes.FirstOrDefault(item => string.Equals(item.LaneKey, group.Key, StringComparison.OrdinalIgnoreCase));
            if (lane is null)
            {
                lane = new DurableLane { LaneKey = group.Key, Status = "pending" };
                checkpoint.Lanes.Add(lane);
            }
            if (group.All(item => item.Status == "completed")) lane.Status = LaneStatus.Completed;
        }
    }

    /// <summary>
    /// Lands the supervision verdict's per-criterion verdicts on the tasks they
    /// name (裁决落 lane): a gate later reads these as code facts and can only
    /// tighten, never loosen, its decision.
    /// </summary>
    private static void MapCriterionVerdicts(FullDuplexCheckpointV1 checkpoint, SupervisionVerdict verdict)
    {
        if (verdict.CriterionVerdicts is not { Count: > 0 }) return;
        foreach (var item in verdict.CriterionVerdicts)
        {
            var task = checkpoint.Tasks.FirstOrDefault(node => string.Equals(node.TaskKey, item.TaskKey, StringComparison.OrdinalIgnoreCase));
            task?.CriteriaVerdicts.Add(new CriterionVerdict(item.Criterion, item.Satisfied, item.Evidence, "supervisor", checkpoint.SupervisionRound));
        }
    }

    /// <summary>
    /// Protocol contract handed to the meeting agent so a deferred user request
    /// ("after it finishes, test and commit") can be expressed as one final
    /// LANE_OPEN line. The engine, not the model, decides whether the lane opens.
    /// </summary>
    private const string LaneOpenProtocol =
        "Orchestration directive protocol: when the user asks for work that must start only after the target run reaches a state, "
        + "append exactly one final line:\n"
        + "LANE_OPEN: {\"lane_key\":\"<unique-key>\",\"goal\":\"<what this lane does>\","
        + "\"tasks\":[{\"task_key\":\"<key>\",\"title\":\"<title>\",\"description\":\"\",\"success_criteria\":[\"<criterion>\"],\"dependencies\":[],\"priority\":1,\"risk\":\"low\"}],"
        + "\"waits\":[{\"lane\":\"main\",\"predicate\":\"lane_done\",\"required_criteria\":[]}]}\n"
        + "Rules: task_key must be unique inside the lane; dependencies may reference only this lane's tasks; waits.lane must name an existing lane of the target run; "
        + "any tool_scope entry must already be authorized for the target run; lanes declaring mutating tools are rejected. "
        + "Output the line only when the user actually defers work; otherwise output none.";

    private const string LaneOpenMarker = "LANE_OPEN:";

    /// <summary>
    /// Strips every protocol line from the meeting text and persists the last
    /// one as a pending orchestration directive on the target run. The line
    /// never reaches the user stream; the user-visible confirmation is
    /// generated here, not by the model.
    /// </summary>
    private async Task<string> FinalizeInteractionTextAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        string text,
        CancellationToken cancellationToken)
    {
        var extracted = ExtractLaneOpenLine(text);
        if (extracted is null) return text;
        var (cleanText, laneKey, payload) = extracted.Value;
        var sourceRunId = Guid.Parse(run.RunId);
        if (checkpoint.TargetRunId is not { } targetRunId)
        {
            await AppendEventAsync(sourceRunId, "orchestration.directive.rejected",
                "A protocol line was dropped because this interaction has no target run.",
                new { verb = "LANE_OPEN", code = "target_run_required" }, cancellationToken).ConfigureAwait(false);
            return cleanText;
        }
        // Admission validates against the TARGET run's frozen facts: the lane
        // will execute under the target's frozen manifest and permission mode.
        // The interaction's own source run never freezes a manifest (only
        // new_task turns do), so validating against the source configuration
        // would reject every tool-scoped lane with a widening error even when
        // the target authorizes every declared tool.
        var targetFrozen = await _lifecycle.GetFrozenRunConfigurationAsync(targetRunId.ToString(), cancellationToken).ConfigureAwait(false);
        FrozenRunConfigurationV1? targetConfiguration = null;
        if (targetFrozen is not null)
        {
            try { targetConfiguration = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(targetFrozen.Content, JsonOptions); }
            catch (JsonException) { }
        }
        if (targetConfiguration is null || !string.Equals(targetConfiguration.ContentHash, targetFrozen!.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            await AppendEventAsync(targetRunId, "orchestration.directive.rejected",
                "A meeting-authored orchestration directive was dropped because the target run's frozen configuration could not be verified.",
                new { verb = "LANE_OPEN", code = "target_configuration_unavailable", source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
            return cleanText;
        }
        if (!targetConfiguration.Orchestration.LanesEnabled)
        {
            await AppendEventAsync(targetRunId, "orchestration.directive.rejected",
                "A meeting-authored orchestration directive was dropped because lanes are disabled for the target run.",
                new { verb = "LANE_OPEN", code = "lanes_disabled", source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
            return cleanText;
        }
        if (_directiveValidator is null)
        {
            await AppendEventAsync(targetRunId, "orchestration.directive.rejected",
                "A meeting-authored orchestration directive was dropped because no directive validator is registered.",
                new { verb = "LANE_OPEN", code = "validator_unavailable", source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
            return cleanText;
        }

        // Admission-side validation runs on conservative facts: the persisted
        // checkpoint projection carries no lane list, so duplicates and budgets
        // are enforced authoritatively at consumption against the live checkpoint.
        var knownLanes = new List<string> { "main" };
        var frozenToolIds = targetConfiguration.ToolManifest
            .Select(entry => entry.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        var mutatingToolIds = targetConfiguration.ToolManifest
            .Where(entry => entry.MutatesWorkspace)
            .Select(entry => entry.Id)
            .ToList();
        var rejection = _directiveValidator.Validate(
            new OrchestrationDirectiveCandidate("LANE_OPEN", laneKey, payload),
            new OrchestrationDirectiveRules(
                targetConfiguration.Orchestration.LanesEnabled,
                1,
                targetConfiguration.Orchestration.MaxLanesPerRun,
                targetConfiguration.Orchestration.MaxTasksPerLane,
                frozenToolIds,
                mutatingToolIds,
                knownLanes,
                targetConfiguration.PermissionMode));
        if (rejection is not null)
        {
            await AppendEventAsync(targetRunId, "orchestration.directive.rejected",
                "A meeting-authored orchestration directive failed validation.",
                new { verb = "LANE_OPEN", code = rejection.Code, reason = rejection.Reason, source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
            return cleanText + $"\n\nOrchestration request rejected ({rejection.Code}): {rejection.Reason}";
        }

        var payloadHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var queued = await _lifecycle.EnqueueRunDirectiveAsync(new RunDirectiveWrite(
            targetRunId,
            checkpoint.SessionId,
            null,
            "orchestration",
            payload,
            $"directive:{targetRunId}:{sourceRunId}:{payloadHash}"), cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(sourceRunId, "orchestration.directive.queued",
            "A meeting-authored orchestration directive was queued for the target run.",
            new { directive_id = queued.Id, verb = "LANE_OPEN", lane_key = laneKey, source_run_id = sourceRunId, target_run_id = targetRunId }, cancellationToken).ConfigureAwait(false);
        return cleanText + $"\n\nOrchestration registered: lane '{laneKey}' will open on the target run once its waits are satisfied.";
    }

    private static (string CleanText, string LaneKey, string PayloadJson)? ExtractLaneOpenLine(string text)
    {
        string? laneKey = null, payload = null;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith(LaneOpenMarker, StringComparison.Ordinal)) continue;
            payload = line[LaneOpenMarker.Length..].Trim();
            laneKey = TryReadLaneKey(payload);
            lines[index] = string.Empty;
        }
        if (payload is null) return null;
        return (string.Join('\n', lines).TrimEnd(), laneKey ?? string.Empty, payload);
    }

    private static string? TryReadLaneKey(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("lane_key", out var key)
                && key.ValueKind == JsonValueKind.String)
            {
                return key.GetString()?.Trim();
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// Run-terminal drain of orchestration directives queued while this run held
    /// the session. An orchestration directive arriving at terminal fails closed (a
    /// new lane is meaningless on a finished run); a queued INTERACTION is re-admitted
    /// as its own run by <see cref="ReleaseQueuedInteractionAsync"/>, because the slot
    /// it was waiting for is exactly what just became free. The pending-status filter
    /// makes a replayed finalize idempotent.
    /// </summary>
    private async Task<FullDuplexCheckpointV1> DrainRunDirectivesAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var pending = await _lifecycle.ListPendingRunDirectivesAsync(checkpoint.RunId, cancellationToken).ConfigureAwait(false);
        foreach (var directive in pending)
        {
            // Queued interactions belong exclusively to the re-admission pass below.
            // Letting them fall through to the generic branch marked them "rejected"
            // (lanes are off by default) BEFORE the releasing pass saw them, so one
            // message was recorded as both rejected and executed, and the release's
            // own drain then matched no pending row.
            if (string.Equals(directive.Kind, "queued_interaction", StringComparison.Ordinal)) continue;
            if (string.Equals(directive.Kind, "orchestration", StringComparison.Ordinal))
            {
                await AppendEventAsync(runId, "orchestration.directive.rejected",
                    "An orchestration directive arrived at run terminal and cannot materialize a lane.",
                    new { directive_id = directive.Id, code = "run_terminal", kind = directive.Kind }, cancellationToken).ConfigureAwait(false);
                await _lifecycle.DrainRunDirectivesAsync(checkpoint.RunId, [directive.Id], "rejected", cancellationToken).ConfigureAwait(false);
                checkpoint.DirectiveCursor++;
                continue;
            }
            if (!configuration.Orchestration.LanesEnabled)
            {
                await AppendEventAsync(runId, "orchestration.directive.rejected",
                    "A queued directive was rejected because lanes are disabled for this run.",
                    new { directive_id = directive.Id, kind = directive.Kind, code = "lanes_disabled" }, cancellationToken).ConfigureAwait(false);
                await _lifecycle.DrainRunDirectivesAsync(checkpoint.RunId, [directive.Id], "rejected", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AppendEventAsync(runId, "orchestration.directive.accepted",
                    "A queued directive was accepted at run terminal.",
                    new { directive_id = directive.Id, kind = directive.Kind }, cancellationToken).ConfigureAwait(false);
                await _lifecycle.DrainRunDirectivesAsync(checkpoint.RunId, [directive.Id], "drained", cancellationToken).ConfigureAwait(false);
            }
            checkpoint.DirectiveCursor++;
        }
        // A queued USER MESSAGE is not an orchestration directive. It already returned
        // 201 to the client, and the run that stood in its way has just finished — so
        // the only reason it was queued no longer holds and it must be executed.
        //
        // It used to share the directives' fate: with lanes off (the shipped default)
        // it was marked "rejected" and the user, who had seen the message accepted,
        // simply never got an answer. Nothing about a queued conversation turn depends
        // on lanes.
        checkpoint.DirectiveCursor += await ReleaseTerminalQueuedInteractionsAsync(
            run,
            pending,
            cancellationToken).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "directives-drained", cancellationToken).ConfigureAwait(false);
        }
        return checkpoint;
    }

    private async Task<int> ReleaseTerminalQueuedInteractionsAsync(
        RunState run,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(run.RunId, out var runId)) return 0;
        var pending = await _lifecycle.ListPendingRunDirectivesAsync(runId, cancellationToken).ConfigureAwait(false);
        return await ReleaseTerminalQueuedInteractionsAsync(run, pending, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ReleaseTerminalQueuedInteractionsAsync(
        RunState run,
        IReadOnlyList<RunDirective> pending,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(run.RunId, out var runId)) return 0;
        var released = 0;
        foreach (var directive in pending.Where(item => string.Equals(item.Kind, "queued_interaction", StringComparison.Ordinal)).ToList())
        {
            var status = await ReleaseQueuedInteractionAsync(run, directive, cancellationToken).ConfigureAwait(false);
            // Deferred means the user message is still accepted but no run slot is
            // available yet. Keep it pending so the hosted terminal repair loop can
            // retry; only a successful admission or permanently unreadable payload
            // is terminal for the directive itself.
            if (!string.Equals(status, "deferred", StringComparison.Ordinal))
            {
                await _lifecycle.DrainRunDirectivesAsync(runId, [directive.Id], status, cancellationToken).ConfigureAwait(false);
                released++;
            }
        }
        return released;
    }

    /// <summary>
    /// Re-admits an interaction that was queued behind this run, now that the run has
    /// finished and the slot it was waiting for is free. The payload is the one the
    /// interactions endpoint persisted, so the message the user already received a 201
    /// for becomes the run that actually answers it.
    /// </summary>
    /// <returns>
    /// The drain status to record. <c>executed</c> when a run was admitted;
    /// <c>deferred</c> when it still could not be admitted (the reason is published on
    /// the run rather than swallowed) or when no coordinator is available in this host;
    /// <c>rejected</c> only when the stored payload cannot be read at all, because a
    /// message that cannot be reconstructed can never be executed.
    /// </returns>
    private async Task<string> ReleaseQueuedInteractionAsync(RunState run, RunDirective directive, CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var payload = ParseQueuedInteractionPayload(directive.PayloadJson);

        if (payload is null || string.IsNullOrWhiteSpace(payload.Content))
        {
            await AppendEventAsync(runId, "interaction.queued_unreadable",
                "A queued interaction could not be re-admitted: its stored payload carries no content.",
                new { directive_id = directive.Id, code = "queued_payload_unreadable" }, cancellationToken).ConfigureAwait(false);
            return "rejected";
        }

        // Resolved lazily for the same reason the other optional collaborators are:
        // the coordinator depends on this engine, so a constructor dependency would be
        // a cycle. By drain time the engine singleton exists, so this cannot recurse.
        if (_services.GetService(typeof(IFullDuplexRunCoordinator)) is not IFullDuplexRunCoordinator coordinator)
        {
            await AppendEventAsync(runId, "interaction.queued_deferred",
                "A queued interaction is still pending: this host has no run coordinator to admit it.",
                new { directive_id = directive.Id, code = "run_coordinator_unavailable" }, cancellationToken).ConfigureAwait(false);
            return "deferred";
        }

        try
        {
            var submission = await coordinator.SubmitAsync(new FullDuplexInvocation(
                directive.SessionId,
                payload.Content,
                payload.ClientMessageId,
                string.IsNullOrWhiteSpace(payload.PermissionMode) ? "default" : payload.PermissionMode,
                TargetRunId: null,
                ExpectedContextRevision: null,
                MeetingModelOverride: payload.MeetingModelOverride,
                ModeVersionId: payload.ModeVersionId), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "interaction.queued_executed",
                "A queued interaction was admitted as its own run once this run finished.",
                new { directive_id = directive.Id, released_run_id = submission.RunId, existing = submission.Existing }, cancellationToken).ConfigureAwait(false);
            return "executed";
        }
        catch (RunAdmissionException ex)
        {
            // Still not admissible. Say so where the user can see it instead of
            // dropping the message: the failure is a queueing fact, not a verdict.
            await AppendEventAsync(runId, "interaction.queued_deferred",
                $"A queued interaction is still waiting: {ex.Message}",
                new { directive_id = directive.Id, code = ex.Code }, cancellationToken).ConfigureAwait(false);
            return "deferred";
        }
    }

    internal sealed record QueuedInteractionPayload(
        string? Content,
        string? ClientMessageId,
        string? PermissionMode,
        Guid? ModeVersionId,
        SessionModelOverride? MeetingModelOverride);

    internal static QueuedInteractionPayload? ParseQueuedInteractionPayload(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var content = root.TryGetProperty("content", out var contentValue)
                && contentValue.ValueKind == JsonValueKind.String
                ? contentValue.GetString()
                : null;
            var clientMessageId = root.TryGetProperty("client_message_id", out var clientValue)
                && clientValue.ValueKind == JsonValueKind.String
                ? clientValue.GetString()
                : null;
            var permissionMode = root.TryGetProperty("permission_mode", out var permissionValue)
                && permissionValue.ValueKind == JsonValueKind.String
                ? permissionValue.GetString()
                : null;
            Guid? modeVersionId = null;
            if (root.TryGetProperty("mode_version_id", out var modeVersionValue)
                && modeVersionValue.ValueKind == JsonValueKind.String
                && Guid.TryParse(modeVersionValue.GetString(), out var parsedModeVersionId))
            {
                modeVersionId = parsedModeVersionId;
            }
            SessionModelOverride? meetingModelOverride = null;
            if (root.TryGetProperty("meeting_model_override", out var overrideValue)
                && overrideValue.ValueKind == JsonValueKind.Object
                && overrideValue.TryGetProperty("provider_instance_id", out var providerValue)
                && providerValue.ValueKind == JsonValueKind.String
                && Guid.TryParse(providerValue.GetString(), out var providerInstanceId))
            {
                var model = overrideValue.TryGetProperty("model", out var modelValue)
                    && modelValue.ValueKind == JsonValueKind.String
                    ? modelValue.GetString()
                    : null;
                meetingModelOverride = new SessionModelOverride(providerInstanceId, model);
            }
            return new QueuedInteractionPayload(
                content,
                clientMessageId,
                permissionMode,
                modeVersionId,
                meetingModelOverride);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The M4 orchestration port consumer: the target run's owner drains
    /// pending meeting-authored directives beside the context-patch pass and
    /// materializes accepted lanes into the checkpoint. Every failure is
    /// fail-closed and audited; consumed rows are marked so finalize cannot
    /// re-see them.
    /// </summary>
    private async Task<bool> ApplyPendingOrchestrationDirectivesAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var pending = await _lifecycle.ListPendingRunDirectivesAsync(checkpoint.RunId, cancellationToken).ConfigureAwait(false);
        var applied = false;
        foreach (var directive in pending.Where(item => string.Equals(item.Kind, "orchestration", StringComparison.Ordinal)).ToList())
        {
            var rejection = await ConsumeOrchestrationDirectiveAsync(run, configuration, checkpoint, directive, cancellationToken).ConfigureAwait(false);
            await _lifecycle.DrainRunDirectivesAsync(
                checkpoint.RunId,
                [directive.Id],
                rejection is null ? "consumed" : "rejected",
                cancellationToken).ConfigureAwait(false);
            checkpoint.DirectiveCursor++;
            applied = true;
        }
        return applied;
    }

    private async Task<OrchestrationDirectiveRejection?> ConsumeOrchestrationDirectiveAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RunDirective directive,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        if (_directiveValidator is null)
        {
            await AppendEventAsync(runId, "orchestration.directive.rejected",
                "An orchestration directive was rejected because no directive validator is registered.",
                new { directive_id = directive.Id, code = "validator_unavailable" }, cancellationToken).ConfigureAwait(false);
            return new OrchestrationDirectiveRejection("validator_unavailable", "No directive validator is registered.");
        }

        OrchestrationDirectiveCandidate candidate;
        List<LaneWait> waits;
        List<DurableTaskNode> nodes;
        string? goal = null;
        string? title = null;
        try
        {
            using var document = JsonDocument.Parse(directive.PayloadJson);
            var laneKey = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("lane_key", out var key)
                && key.ValueKind == JsonValueKind.String
                    ? key.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
            candidate = new OrchestrationDirectiveCandidate("LANE_OPEN", laneKey, directive.PayloadJson);
            goal = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("goal", out var goalElement)
                && goalElement.ValueKind == JsonValueKind.String
                    ? goalElement.GetString()?.Trim()
                    : null;
            title = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()?.Trim()
                    : null;
            waits = ReadLaneWaits(document.RootElement);
            // A goal-only payload carries no task graph: the lane's own planning
            // agent materializes it, so there is nothing to validate here.
            var laneTasks = ReadLaneTasks(document.RootElement);
            nodes = laneTasks.Length == 0
                ? []
                : ValidateAndMaterializeGraph(laneTasks, configuration.Orchestration.MaxTasksPerLane);
        }
        catch (Exception ex) when (ex is JsonException or InvalidTaskGraphException)
        {
            await AppendEventAsync(runId, "orchestration.directive.rejected",
                "An orchestration directive failed lane materialization.",
                new { directive_id = directive.Id, code = "invalid_lane_payload", reason = ex.Message }, cancellationToken).ConfigureAwait(false);
            return new OrchestrationDirectiveRejection("invalid_lane_payload", ex.Message);
        }

        var knownLanes = checkpoint.Lanes.Select(lane => lane.LaneKey).ToList();
        knownLanes.Add("main");
        var frozenToolIds = configuration.ToolManifest
            .Select(entry => entry.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        var mutatingToolIds = configuration.ToolManifest
            .Where(entry => entry.MutatesWorkspace)
            .Select(entry => entry.Id)
            .ToList();
        var rejection = _directiveValidator.Validate(candidate, new OrchestrationDirectiveRules(
            configuration.Orchestration.LanesEnabled,
            checkpoint.Lanes.Count,
            configuration.Orchestration.MaxLanesPerRun,
            configuration.Orchestration.MaxTasksPerLane,
            frozenToolIds,
            mutatingToolIds,
            knownLanes,
            configuration.PermissionMode));
        if (rejection is not null)
        {
            await AppendEventAsync(runId, "orchestration.directive.rejected",
                "An orchestration directive failed validation at consumption.",
                new { directive_id = directive.Id, code = rejection.Code, reason = rejection.Reason, lane_key = candidate.LaneKey }, cancellationToken).ConfigureAwait(false);
            return rejection;
        }

        foreach (var node in nodes)
        {
            node.LaneKey = candidate.LaneKey;
            if (waits.Count > 0) node.Waits = [.. waits];
        }
        checkpoint.Tasks.AddRange(nodes);
        SyncLanes(checkpoint);

        // A goal-only directive opens the lane in planning and hands the task
        // graph to the lane's own planning agent: literally a second, parallel
        // task-planning agent running inside the same run lease. The lane passes
        // through the planning phase even though the work happens during this
        // consumption, so the event sequence is the same for scripted and real
        // routing.
        if (nodes.Count == 0)
        {
            var lane = checkpoint.Lanes.FirstOrDefault(item =>
                string.Equals(item.LaneKey, candidate.LaneKey, StringComparison.OrdinalIgnoreCase));
            var laneGoal = goal ?? title ?? $"Plan and complete lane '{candidate.LaneKey}'.";
            if (lane is not null) lane.Status = LaneStatus.Planning;
            await AppendEventAsync(runId, "orchestration.lane_planning",
                "A goal-only lane opened and its own planning agent is deriving the task graph.",
                new { directive_id = directive.Id, lane_key = candidate.LaneKey, goal = laneGoal },
                cancellationToken).ConfigureAwait(false);
            try
            {
                var lanePlanner = await EnsureLanePlannerAsync(run, configuration, checkpoint, candidate.LaneKey, cancellationToken).ConfigureAwait(false);
                var planned = await PlanLaneTasksAsync(run, configuration, checkpoint, candidate.LaneKey,
                    lanePlanner, laneGoal, cancellationToken).ConfigureAwait(false);
                foreach (var node in planned)
                {
                    node.LaneKey = candidate.LaneKey;
                    if (waits.Count > 0) node.Waits = [.. waits];
                }
                checkpoint.Tasks.AddRange(planned);
                SyncLanes(checkpoint);
                await AppendEventAsync(runId, "orchestration.lane_planned",
                    "A lane's own planning agent materialized the lane's task graph.",
                    new
                    {
                        directive_id = directive.Id,
                        lane_key = candidate.LaneKey,
                        planner_instance_id = lanePlanner.Id,
                        task_count = planned.Count,
                        task_keys = planned.Select(node => node.TaskKey).ToArray()
                    }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidTaskGraphException or InvalidOperationException)
            {
                // The lane's own plan failed. Escalating keeps the user's intent
                // durably parked on a review request instead of dropping the
                // directive or leaving a planning lane that never dispatches.
                await AppendEventAsync(runId, "orchestration.lane_planning_failed",
                    "A lane's own planning agent failed to derive the task graph.",
                    new { directive_id = directive.Id, lane_key = candidate.LaneKey, reason = ex.Message },
                    cancellationToken).ConfigureAwait(false);
                if (lane is not null)
                {
                    await EscalateLaneAsync(runId, run, lane,
                        $"Lane planning failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
                }
                return null;
            }
        }

        // A lane consumed mid-tick would otherwise dispatch in the same tick it
        // was created, because the classification pass already ran. If its waits
        // are unmet it must park now, exactly as the classification pass would
        // have; only a parked lane passes its gate before dispatching.
        var consumedLane = checkpoint.Lanes.FirstOrDefault(item =>
            string.Equals(item.LaneKey, candidate.LaneKey, StringComparison.OrdinalIgnoreCase));
        if (consumedLane is not null && !consumedLane.Escalated && consumedLane.Status is LaneStatus.Pending or LaneStatus.Planning)
        {
            var unmetWaits = UnmetLaneWaits(checkpoint, candidate.LaneKey);
            if (unmetWaits.Count > 0)
            {
                consumedLane.Status = LaneStatus.Waiting;
                foreach (var task in checkpoint.Tasks.Where(item =>
                    string.Equals(LaneKeyOf(item), candidate.LaneKey, StringComparison.OrdinalIgnoreCase)
                    && item.Status is "pending" or "ready"))
                {
                    task.Waits = [.. unmetWaits];
                }
                await AppendEventAsync(runId, "orchestration.lane_waiting",
                    $"Lane '{candidate.LaneKey}' is waiting on other lanes.",
                    new { lane_key = candidate.LaneKey, waits = unmetWaits.Select(wait => new { lane = wait.LaneKey, predicate = wait.Predicate, facts_hash = wait.ObservedFactsHash }) },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // A directive consumed while every earlier lane already finished would
        // otherwise land on a finalizing checkpoint and never execute.
        if (checkpoint.Phase is "finalizing" or "done" or "completed")
        {
            checkpoint.Phase = "executing";
            await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
        }
        await AppendEventAsync(runId, "orchestration.lane_opened",
            "A meeting-authored lane was materialized on this run.",
            new
            {
                directive_id = directive.Id,
                lane_key = candidate.LaneKey,
                task_count = checkpoint.Tasks.Where(item => string.Equals(LaneKeyOf(item), candidate.LaneKey, StringComparison.OrdinalIgnoreCase)).Count(),
                task_keys = checkpoint.Tasks
                    .Where(item => string.Equals(LaneKeyOf(item), candidate.LaneKey, StringComparison.OrdinalIgnoreCase))
                    .Select(node => node.TaskKey).ToArray(),
                waits = waits.Select(wait => new { lane = wait.LaneKey, predicate = wait.Predicate }).ToArray()
            }, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Spawns the lane's own planning agent instance. It reuses the frozen
    /// roster authorization chain through CreateRootAsync, so the instance's
    /// allowed tools and budget come from the run's frozen configuration, and
    /// carries the lane key so the audit trail can attribute planning calls.
    /// </summary>
    private async Task<RuntimeAgentInstance> EnsureLanePlannerAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        string laneKey,
        CancellationToken cancellationToken)
    {
        if (checkpoint.LanePlannerIds.TryGetValue(laneKey, out var existing))
        {
            var all = await _instances.ListByRunAsync(Guid.Parse(run.RunId), cancellationToken).ConfigureAwait(false);
            var found = all.FirstOrDefault(item => item.Id == existing);
            if (found is not null) return found;
        }
        var plannerDefinition = RequiredAgent(configuration.ExecutionAgents, "task_planner");
        var instance = await _instances.CreateRootAsync(new RuntimeAgentSeed(
            Guid.Parse(run.SessionId), Guid.Parse(run.RunId), plannerDefinition.Id, plannerDefinition.Layer,
            plannerDefinition.Role, "chat", plannerDefinition.Capabilities, plannerDefinition.AllowedTools,
            ResourceSeed(plannerDefinition.ResourceGrants), configuration.Context.DefaultTokenBudget,
            AgentDefinitionId: plannerDefinition.AgentDefinitionId,
            AgentVersionId: plannerDefinition.AgentVersionId,
            VersionContentHash: plannerDefinition.VersionContentHash,
            LaneKey: laneKey), cancellationToken).ConfigureAwait(false);
        checkpoint.LanePlannerIds[laneKey] = instance.Id;
        await AppendEventAsync(Guid.Parse(run.RunId), "agent.created",
            $"Task planning agent created for lane '{laneKey}'.",
            new { agent_instance_id = instance.Id, layer = instance.Layer, role = instance.Role, lane_key = laneKey },
            cancellationToken).ConfigureAwait(false);
        return instance;
    }

    /// <summary>
    /// Plans a lane's task graph through that lane's own planning instance,
    /// reusing the shared planner's model call, roster, and graph validation.
    /// The task budget is the frozen per-lane limit rather than the run limit.
    /// </summary>
    private async Task<List<DurableTaskNode>> PlanLaneTasksAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        string laneKey,
        RuntimeAgentInstance lanePlanner,
        string laneGoal,
        CancellationToken cancellationToken)
    {
        var plannerDefinition = RequiredAgent(configuration.ExecutionAgents, "task_planner");
        var context = await BuildContextAsync(run, configuration, plannerDefinition.Id, laneGoal, cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(configuration, plannerDefinition, context, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "context.packed",
            $"Lane '{laneKey}' planner context assembled.", new
            {
                lane_key = laneKey,
                evidence_count = context.Evidence.Count,
                estimated_tokens = context.EstimatedTokens,
                token_budget = context.TokenBudget,
                sources = context.Evidence.Select(item => item.Source).ToArray(),
                context_revision = checkpoint.ContextRevision
            }, cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        var laneParseError = string.Empty;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var contextForPlanner = CreateRunContext(run, checkpoint);
                var planner = new PlanningAgent(CreateModelFactory(configuration, checkpoint, plannerDefinition,
                    lanePlanner.Id, null), _logger);
                var instructionsForAttempt = attempt == 0
                    ? PlannerInstructions(checkpoint, assembly.Instructions, laneKey)
                    : PlannerInstructions(checkpoint, assembly.Instructions, laneKey) + BuildPlannerRetryHint(lastError, laneParseError);
                var planned = await planner.PlanAsync(
                    contextForPlanner,
                    BuildFrozenPlannerRoster(configuration),
                    instructionsForAttempt,
                    cancellationToken).ConfigureAwait(false);
                laneParseError = planner.LastParseErrorDetail;
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, planner.LastUsage);
                return ValidateAndMaterializeGraph(planned, configuration.Orchestration.MaxTasksPerLane);
            }
            catch (InvalidTaskGraphException ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidTaskGraphException("The lane planner returned no tasks.");
    }

    private static PlannedTask[] ReadLaneTasks(JsonElement payload)
    {
        // A goal-only directive carries no task graph: the lane's own planning
        // agent materializes it. An empty plan keeps the lane open without tasks.
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("tasks", out var tasksElement)
            || tasksElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var laneKey = payload.TryGetProperty("lane_key", out var lane) && lane.ValueKind == JsonValueKind.String
            ? lane.GetString()
            : null;
        var tasks = new List<PlannedTask>();
        foreach (var task in tasksElement.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Every directive task must be an object.");
            tasks.Add(new PlannedTask
            {
                TaskKey = task.TryGetProperty("task_key", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null,
                LaneKey = laneKey,
                Title = task.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() ?? string.Empty : string.Empty,
                Description = task.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null,
                SuccessCriteria = ReadStringArray(task, "success_criteria"),
                Dependencies = ReadStringArray(task, "dependencies"),
                RequiredCapabilities = ReadStringArray(task, "required_capabilities"),
                RequiredTools = ReadStringArray(task, "tool_scope"),
                Priority = task.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number && priority.TryGetInt32(out var value) ? value : 1,
                Risk = task.TryGetProperty("risk", out var risk) && risk.ValueKind == JsonValueKind.String ? risk.GetString() ?? "medium" : "medium"
            });
        }
        return [.. tasks];
    }

    private static List<LaneWait> ReadLaneWaits(JsonElement payload)
    {
        var waits = new List<LaneWait>();
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("waits", out var waitsElement)
            || waitsElement.ValueKind != JsonValueKind.Array)
        {
            return waits;
        }
        foreach (var wait in waitsElement.EnumerateArray())
        {
            if (wait.ValueKind != JsonValueKind.Object) continue;
            var lane = wait.TryGetProperty("lane", out var laneElement) && laneElement.ValueKind == JsonValueKind.String
                ? laneElement.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(lane)) continue;
            var predicate = wait.TryGetProperty("predicate", out var predicateElement) && predicateElement.ValueKind == JsonValueKind.String
                ? predicateElement.GetString()?.Trim()
                : null;
            var criteria = wait.TryGetProperty("required_criteria", out var criteriaElement) && criteriaElement.ValueKind == JsonValueKind.Array
                ? criteriaElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToList()
                : [];
            waits.Add(new LaneWait(lane, string.IsNullOrWhiteSpace(predicate) ? LaneWait.LaneDone : predicate!, criteria, null));
        }
        return waits;
    }

    private static string PlannerInstructions(FullDuplexCheckpointV1 checkpoint, string baseInstructions, string laneKey = "main")
    {
        // The Lane line lets scripted and real routing tell which lane a
        // planning or gate call belongs to; the initial plan runs on "main".
        baseInstructions += $"\nLane: {laneKey}";
        if (checkpoint.RecommendedCapabilities.Count == 0) return baseInstructions;
        var lines = string.Join("\n", checkpoint.RecommendedCapabilities.Select(item =>
            $"- {item.Skill} (confidence: {item.Confidence}): {item.Reason}"));
        return baseInstructions
            + "\n\nCapability recommendations from the operation layer (advisory; accept or reject as you see fit):\n"
            + lines;
    }
}
