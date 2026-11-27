using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.DmaEA;

internal sealed partial class FullDuplexRunEngine
{
    private const int MeetingFindingLimit = 12;
    private const int MeetingFindingChars = 600;
    private const int MeetingEvidenceLineLimit = 400;
    private const int MaxRunEvidenceTurns = 12;

    private async Task<FullDuplexCheckpointV1> ReviewAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        await TrySetRunStatusAsync(run.RunId, "reviewing", null, cancellationToken).ConfigureAwait(false);
        var evidenceRefsRequired = ModeRuntimePolicyRegistry.Has(
            configuration.RuntimePolicy, ModeRuntimePolicyRegistry.EvidenceRefsRequiredFeature);
        var plans = checkpoint.Tasks.Select(ToPlannedTask).ToArray();
        var results = checkpoint.Tasks.Select(item => new StepResult
        {
            TaskNodeId = item.TaskId,
            AgentId = item.WorkerAgentId?.ToString() ?? string.Empty,
            Status = item.ResultStatus ?? item.Status,
            Summary = item.ResultSummary ?? string.Empty,
            Evidence = item.Evidence
        }).ToArray();

        await AppendEventAsync(runId, "supervision.requested", "Supervision reviewing execution evidence.", new
        {
            run_id = run.RunId,
            revision_round = checkpoint.SupervisionRound,
            result_count = results.Length
        }, cancellationToken).ConfigureAwait(false);

        SupervisionVerdict verdict;
        var supervisionSkipped = false;
        if (configuration.Supervision.RequiredBeforeFinal)
        {
            var supervisorDefinition = configuration.OperationAgents
                .SingleOrDefault(item => string.Equals(item.Id, "supervisor", StringComparison.Ordinal));
            if (supervisorDefinition is null || !supervisorDefinition.Enabled)
            {
                supervisionSkipped = true;
                verdict = new SupervisionVerdict(SupervisionDecision.Pass,
                    ["The frozen roster carries no supervisor; the review gate is skipped for this mode."], []);
                await AppendEventAsync(runId, "supervision.skipped", "Supervision skipped: no supervisor in the frozen roster.", new
                {
                    run_id = run.RunId,
                    revision_round = checkpoint.SupervisionRound
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ValidateFrozenAgent(supervisorDefinition, "supervisor");
                var supervisorInstance = await EnsureSupervisorAgentAsync(run, configuration, checkpoint, supervisorDefinition, cancellationToken).ConfigureAwait(false);
                checkpoint.SupervisorAgentId = supervisorInstance.Id;
                var context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), supervisorDefinition, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
                var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), supervisorDefinition, context, cancellationToken).ConfigureAwait(false);
                var supervisor = new SupervisionAgent(CreateModelFactory(configuration, checkpoint, supervisorDefinition,
                    supervisorInstance.Id, supervisorInstance.ParentInstanceId, AnswerStreaming.None), _logger);
                verdict = await supervisor.ReviewAsync(checkpoint.UserGoal, plans, results, checkpoint.SupervisionRound, assembly.Instructions, cancellationToken, evidenceRefsRequired).ConfigureAwait(false);
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, supervisor.LastUsage);
            }
        }
        else
        {
            supervisionSkipped = true;
            verdict = new SupervisionVerdict(SupervisionDecision.Pass, [], []);
            await AppendEventAsync(runId, "supervision.skipped", "Supervision skipped: the frozen mode does not require a supervisor.", new
            {
                run_id = run.RunId,
                revision_round = checkpoint.SupervisionRound,
                reason = "not_required"
            }, cancellationToken).ConfigureAwait(false);
        }

        MapCriterionVerdicts(checkpoint, verdict);
        var correctionCompletionReady = CorrectionRuntimePolicy.Has(
                configuration.RuntimePolicy,
                CorrectionRuntimePolicy.CompletionContractFeature)
            && checkpoint.Correction is not null
            && CorrectionTracePolicy.EvaluateCompletion(
                checkpoint.Correction,
                configuration.RuntimePolicy!,
                checkpoint.UserGoal,
                checkpoint.Correction.Completion.ModifiedArtifacts.Count > 0) is null;
        if (correctionCompletionReady)
        {
            verdict = new SupervisionVerdict(
                SupervisionDecision.Pass,
                verdict.Reasons.Append("Host correction completion is ready; task closeout status cannot override verified facts.").ToArray(),
                []);
        }
        else
        {
            verdict = EnforceTaskOutcomeFacts(
                checkpoint.Tasks,
                verdict,
                checkpoint.SupervisionRound,
                configuration.Supervision.MaxRevisionRounds);
            verdict = EnforceCriterionFacts(
                checkpoint.Tasks,
                verdict,
                checkpoint.SupervisionRound,
                configuration.Supervision.MaxRevisionRounds,
                requireSupervisorEvidence: !supervisionSkipped,
                evidenceRefsRequired: evidenceRefsRequired);
        }
        verdict = EnforceRevisionBudget(
            verdict,
            checkpoint.SupervisionRound,
            configuration.Supervision.MaxRevisionRounds);
        checkpoint.SupervisionDecision = supervisionSkipped && verdict.Decision == SupervisionDecision.Pass
            ? "skipped"
            : verdict.DecictionString();
        checkpoint.SupervisionReasons = verdict.Reasons.ToList();

        foreach (var stopped in checkpoint.Tasks.Where(item => item.CloseoutRequested))
        {
            var reason = $"closeout:{stopped.TaskKey}:{stopped.CloseoutCategory}"
                + (stopped.CloseoutHardCeiling ? " (hard_ceiling)" : string.Empty)
                + $" — {stopped.CloseoutReason}";
            if (!checkpoint.SupervisionReasons.Contains(reason, StringComparer.Ordinal)) checkpoint.SupervisionReasons.Add(reason);
        }
        foreach (var lane in checkpoint.Lanes)
        {
            lane.SupervisionRound = checkpoint.SupervisionRound;
            lane.SupervisionDecision = checkpoint.SupervisionDecision;
            lane.SupervisionReasons = checkpoint.SupervisionReasons.ToList();
        }
        await AppendEventAsync(runId, "supervision.completed", $"Supervision decision: {checkpoint.SupervisionDecision}.", new
        {
            decision = checkpoint.SupervisionDecision,
            revision_round = checkpoint.SupervisionRound,
            reasons = checkpoint.SupervisionReasons,
            revise_task_indexes = verdict.ReviseTaskIndexes
        }, cancellationToken).ConfigureAwait(false);

        if (verdict.Decision == SupervisionDecision.Escalate)
        {
            checkpoint.Phase = "awaiting_user";
            checkpoint.MeetingResponse = null;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "supervision-escalated", cancellationToken).ConfigureAwait(false);
            await PublishSupervisionReviewAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Supervision requires user review before this run can finish.", cancellationToken).ConfigureAwait(false);
            return checkpoint;
        }

        if (verdict.Decision == SupervisionDecision.Revise && checkpoint.SupervisionRound < configuration.Supervision.MaxRevisionRounds)
        {
            var reviseIndexes = verdict.ReviseTaskIndexes.Where(index => index >= 0 && index < checkpoint.Tasks.Count).ToArray();
            var replanned = await TryReplanFromSupervisionAsync(run, configuration, checkpoint, verdict, reviseIndexes, cancellationToken).ConfigureAwait(false);
            if (!replanned)
            {
                foreach (var index in reviseIndexes)
                {
                    var node = checkpoint.Tasks[index];
                    node.Status = "pending";
                    node.ResultStatus = null;
                    node.ResultSummary = null;
                    node.Evidence = [];
                }
                await AppendEventAsync(runId, "supervision.replan_fallback", "The supervision replan produced no valid graph; flagged tasks were reset for re-execution.", new
                {
                    run_id = run.RunId,
                    revision_round = checkpoint.SupervisionRound,
                    task_keys = reviseIndexes.Select(index => checkpoint.Tasks[index].TaskKey).ToArray()
                }, cancellationToken).ConfigureAwait(false);
            }
            checkpoint.SupervisionRound++;
            checkpoint.Phase = "executing";
            await TrySetRunStatusAsync(run.RunId, "replanning", null, cancellationToken).ConfigureAwait(false);
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "replanned", cancellationToken).ConfigureAwait(false);
        }

        if (checkpoint.Lanes.Any(lane => lane.Escalated))
        {
            checkpoint.Phase = "awaiting_user";
            await TrySetRunStatusAsync(run.RunId, "awaiting_user", "A lane escalation is pending a user decision.", cancellationToken).ConfigureAwait(false);
            return await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "lane-escalation-parked", cancellationToken).ConfigureAwait(false);
        }

        checkpoint.Phase = "finalizing";
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "reviewed", cancellationToken).ConfigureAwait(false);
        if (correctionCompletionReady && checkpoint.Correction is not null)
            await AppendCorrectionTraceEventAsync(run, checkpoint.Correction, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private static void MapCriterionVerdicts(FullDuplexCheckpointV1 checkpoint, SupervisionVerdict verdict)
    {
        if (verdict.CriterionVerdicts is not { Count: > 0 }) return;
        foreach (var item in verdict.CriterionVerdicts)
        {
            var task = checkpoint.Tasks.FirstOrDefault(node => string.Equals(node.TaskKey, item.TaskKey, StringComparison.OrdinalIgnoreCase));
            task?.CriteriaVerdicts.Add(new CriterionVerdict(item.Criterion, item.Satisfied, item.Evidence, "supervisor", checkpoint.SupervisionRound, item.EvidenceRefs));
        }
    }

    internal static SupervisionVerdict EnforceTaskOutcomeFacts(
        IReadOnlyList<DurableTaskNode> tasks,
        SupervisionVerdict verdict,
        int revisionRound,
        int maxRevisionRounds)
    {
        var incomplete = tasks
            .Select((task, index) => (Task: task, Index: index))
            .Where(item => !string.Equals(item.Task.Status, "completed", StringComparison.Ordinal))
            .ToArray();
        if (incomplete.Length == 0) return verdict;

        var reasons = verdict.Reasons.ToList();
        foreach (var item in incomplete)
        {
            var summary = string.IsNullOrWhiteSpace(item.Task.ResultSummary)
                ? "No completion evidence was recorded."
                : TruncateEvidence(item.Task.ResultSummary);
            var reason = $"task_outcome:{item.Task.TaskKey}:{item.Task.Status} — {summary}";
            if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
        }

        var unresolved = incomplete
            .Where(item => !string.Equals(item.Task.Status, "failed", StringComparison.Ordinal))
            .ToArray();
        if (unresolved.Length == 0)
        {
            if (tasks.Any(item => string.Equals(item.Status, "completed", StringComparison.Ordinal)))
                return verdict with { Reasons = reasons };

            reasons.Add("Every task failed and no completed work remains; the outcome requires user review.");
            return verdict with
            {
                Decision = SupervisionDecision.Escalate,
                Reasons = reasons,
                ReviseTaskIndexes = []
            };
        }

        if (verdict.Decision == SupervisionDecision.Escalate)
        {
            return verdict with { Reasons = reasons };
        }

        if (revisionRound < maxRevisionRounds)
        {
            var reviseIndexes = verdict.ReviseTaskIndexes
                .Concat(unresolved.Select(item => item.Index))
                .Where(index => index >= 0 && index < tasks.Count)
                .Distinct()
                .Order()
                .ToArray();
            return verdict with
            {
                Decision = SupervisionDecision.Revise,
                Reasons = reasons,
                ReviseTaskIndexes = reviseIndexes
            };
        }

        reasons.Add("Automatic revision budget is exhausted; incomplete task outcomes require user review.");
        return verdict with
        {
            Decision = SupervisionDecision.Escalate,
            Reasons = reasons,
            ReviseTaskIndexes = []
        };
    }

    internal static SupervisionReviewBrief BuildSupervisionReviewBrief(
        IReadOnlyList<DurableTaskNode> tasks,
        int revisionRound,
        int maxRevisionRounds,
        bool supervisorRequired,
        bool supervisorAvailable,
        bool evidenceRefsRequired = false)
    {
        var completed = tasks.Count(task => string.Equals(task.Status, "completed", StringComparison.Ordinal));
        var blocked = tasks.Count(task => string.Equals(task.Status, "blocked", StringComparison.Ordinal));
        var failed = tasks.Count(task => string.Equals(task.Status, "failed", StringComparison.Ordinal));
        var pending = Math.Max(0, tasks.Count - completed - blocked - failed);
        var requireSupervisorEvidence = supervisorRequired && supervisorAvailable;
        var unmetCriteria = tasks
            .Where(task => string.Equals(task.Status, "completed", StringComparison.Ordinal))
            .Sum(task => task.SuccessCriteria.Count(criterion =>
                !HasSatisfiedCriterionEvidence(task, criterion, revisionRound, requireSupervisorEvidence, evidenceRefsRequired)));
        return new SupervisionReviewBrief(
            tasks.Count,
            completed,
            blocked,
            failed,
            pending,
            unmetCriteria,
            revisionRound,
            maxRevisionRounds,
            supervisorRequired,
            supervisorAvailable);
    }

    internal static SupervisionVerdict EnforceCriterionFacts(
        IReadOnlyList<DurableTaskNode> tasks,
        SupervisionVerdict verdict,
        int revisionRound,
        int maxRevisionRounds,
        bool requireSupervisorEvidence,
        bool evidenceRefsRequired = false)
    {
        var issues = new List<(int Index, string Reason)>();
        var missingRefs = false;
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            if (!string.Equals(task.Status, "completed", StringComparison.Ordinal)) continue;
            foreach (var criterion in task.SuccessCriteria)
            {
                var latest = LatestCriterionVerdict(task, criterion, revisionRound, requireSupervisorEvidence);
                if (evidenceRefsRequired && latest is { Satisfied: true } && latest.EvidenceRefs is not { Count: > 0 })
                {
                    missingRefs = true;
                    issues.Add((index, $"criterion:{task.TaskKey}:{criterion} — satisfied verdict cites no evidence_refs"));
                    continue;
                }
                if (latest is { Satisfied: true } && !string.IsNullOrWhiteSpace(latest.Evidence)) continue;
                var source = requireSupervisorEvidence ? "supervisor" : "worker";
                var detail = latest is null
                    ? $"no {source} verdict"
                    : latest.Satisfied ? "satisfied verdict has no evidence" : "criterion was not satisfied";
                issues.Add((index, $"criterion:{task.TaskKey}:{criterion} — {detail}"));
            }
        }
        if (issues.Count == 0) return verdict;

        var reasons = verdict.Reasons.ToList();
        foreach (var issue in issues)
        {
            if (!reasons.Contains(issue.Reason, StringComparer.Ordinal)) reasons.Add(issue.Reason);
        }
        if (missingRefs)
        {
            const string refsReason = "A satisfied criterion verdict cites no evidence_refs; evidence_refs_required forbids accepting it and user review is required.";
            if (!reasons.Contains(refsReason, StringComparer.Ordinal)) reasons.Add(refsReason);
            return verdict with
            {
                Decision = SupervisionDecision.Escalate,
                Reasons = reasons,
                ReviseTaskIndexes = []
            };
        }
        if (verdict.Decision == SupervisionDecision.Escalate)
        {
            return verdict with { Reasons = reasons };
        }
        if (revisionRound < maxRevisionRounds)
        {
            return verdict with
            {
                Decision = SupervisionDecision.Revise,
                Reasons = reasons,
                ReviseTaskIndexes = verdict.ReviseTaskIndexes
                    .Concat(issues.Select(issue => issue.Index))
                    .Where(index => index >= 0 && index < tasks.Count)
                    .Distinct()
                    .OrderBy(index => index)
                    .ToArray()
            };
        }
        reasons.Add("Automatic revision budget is exhausted; unresolved success criteria require user review.");
        return verdict with
        {
            Decision = SupervisionDecision.Escalate,
            Reasons = reasons,
            ReviseTaskIndexes = []
        };
    }

    private static CriterionVerdict? LatestCriterionVerdict(
        DurableTaskNode task,
        string criterion,
        int revisionRound,
        bool requireSupervisorEvidence)
    {
        var candidates = task.CriteriaVerdicts
            .Where(item => string.Equals(item.Criterion, criterion, StringComparison.Ordinal));
        candidates = requireSupervisorEvidence
            ? candidates.Where(item => string.Equals(item.ReviewedBy, "supervisor", StringComparison.Ordinal)
                && item.Round == revisionRound)
            : candidates.Where(item => item.ReviewedBy?.StartsWith("worker:", StringComparison.Ordinal) == true);
        return candidates.OrderBy(item => item.Round).LastOrDefault();
    }

    private static bool HasSatisfiedCriterionEvidence(
        DurableTaskNode task,
        string criterion,
        int revisionRound,
        bool requireSupervisorEvidence,
        bool evidenceRefsRequired = false)
    {
        var latest = LatestCriterionVerdict(task, criterion, revisionRound, requireSupervisorEvidence);
        if (latest is not { Satisfied: true } || string.IsNullOrWhiteSpace(latest.Evidence)) return false;
        return !evidenceRefsRequired || latest.EvidenceRefs is { Count: > 0 };
    }

    internal static SupervisionVerdict EnforceRevisionBudget(
        SupervisionVerdict verdict,
        int revisionRound,
        int maxRevisionRounds)
    {
        if (verdict.Decision != SupervisionDecision.Revise || revisionRound < maxRevisionRounds)
        {
            return verdict;
        }
        var reasons = verdict.Reasons.ToList();
        const string exhausted = "Automatic revision budget is exhausted; user review is required.";
        if (!reasons.Contains(exhausted, StringComparer.Ordinal)) reasons.Add(exhausted);
        return verdict with
        {
            Decision = SupervisionDecision.Escalate,
            Reasons = reasons,
            ReviseTaskIndexes = []
        };
    }

    private async Task<bool> TryReplanFromSupervisionAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        SupervisionVerdict verdict,
        int[] reviseIndexes,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Lanes.Any(lane => lane.Escalated)) return false;

        // Explore-First Team (#33): wake flagged persistent teammates on the shared board
        // retaining their isolated worktrees and bounded context rather than running PlanningAgent.
        if (checkpoint.IsExploreFirstCoordinator)
        {
            var feedback = "Supervision requested revision:\n"
                + string.Join("\n", verdict.Reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => $"- {r}"));
            var boardMsg = AppendBoardMessage(checkpoint, "supervisor", "all", "review", feedback, wake: true);
            var coordinatorTask = checkpoint.Tasks.FirstOrDefault(t => t.IsCoordinatorTask);
            var wokenAny = false;

            foreach (var index in reviseIndexes)
            {
                var flagged = checkpoint.Tasks[index];
                if (!string.IsNullOrWhiteSpace(flagged.TeammateHandle)
                    && WakeTeammateFromBoard(checkpoint, coordinatorTask?.TaskId, flagged.TeammateHandle, boardMsg.Seq, feedback))
                {
                    wokenAny = true;
                }
            }

            if (!wokenAny && coordinatorTask is not null)
            {
                coordinatorTask.Status = "pending";
                coordinatorTask.ResultStatus = null;
                coordinatorTask.ResultSummary = null;
                coordinatorTask.CompletedAt = null;
                coordinatorTask.Description = string.IsNullOrWhiteSpace(coordinatorTask.Description)
                    ? feedback
                    : coordinatorTask.Description + "\n\n" + feedback;
                wokenAny = true;
            }

            if (wokenAny)
            {
                checkpoint.PlanRevision++;
                await AppendEventAsync(Guid.Parse(run.RunId), "team.supervision_rework",
                    "Explore-first Team resumed persistent teammate(s) from supervision review feedback.",
                    new
                    {
                        run_id = run.RunId,
                        plan_revision = checkpoint.PlanRevision,
                        supervision_round = checkpoint.SupervisionRound,
                        reasons = verdict.Reasons
                    }, cancellationToken).ConfigureAwait(false);
                return true;
            }
            return false;
        }

        var author = await EnsureConversationAuthorAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        checkpoint.MeetingAgentId = author.Id;
        checkpoint.PlannerAgentId = author.Id;
        var plannerDefinition = RequiredConversationAgent(configuration);

        var context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), plannerDefinition, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), plannerDefinition, context, cancellationToken).ConfigureAwait(false);
        var instructions = SupervisionReplanInstructions(checkpoint, assembly.Instructions, verdict, reviseIndexes);
        var reviseKeys = new HashSet<string>(
            reviseIndexes.Select(index => checkpoint.Tasks[index].TaskKey),
            StringComparer.OrdinalIgnoreCase);

        PlannedTask[] planned = [];
        Exception? lastError = null;
        var replanParseError = string.Empty;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var contextForPlanner = CreateRunContext(run, checkpoint);
                var planner = new PlanningAgent(CreateModelFactory(configuration, checkpoint, plannerDefinition,
                    checkpoint.PlannerAgentId, null, AnswerStreaming.None), _logger);
                var instructionsForAttempt = attempt == 0
                    ? PlannerInstructions(checkpoint, instructions)
                    : PlannerInstructions(checkpoint, instructions) + BuildPlannerRetryHint(lastError, replanParseError);
                planned = await planner.PlanAsync(
                    contextForPlanner,
                    BuildFrozenPlannerRoster(configuration),
                    instructionsForAttempt,
                    configuration,
                    cancellationToken).ConfigureAwait(false);
                replanParseError = planner.LastParseErrorDetail;
                RefuseSilentPlanningFallbackOnDeclaredEdges(configuration, planned);
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, planner.LastUsage);
                checkpoint.Tasks = MergeReplannedGraph(checkpoint.Tasks,
                    ValidateAndMaterializeGraph(planned, configuration.Spawn.MaxAgentsPerRun, PlannerDispatchRosterOf(configuration)));
                foreach (var node in checkpoint.Tasks.Where(node => reviseKeys.Contains(node.TaskKey)))
                {
                    node.Status = "pending";
                    node.ResultStatus = null;
                    node.ResultSummary = null;
                    node.Evidence = [];
                    node.CompletedAt = null;
                }
                SyncLanes(checkpoint);
                lastError = null;
                break;
            }
            catch (InvalidTaskGraphException ex)
            {
                lastError = ex;
            }
        }
        if (lastError is not null)
        {
            _logger.TryLogWarning(lastError, "Supervision replan produced no valid graph for run {RunId}; falling back to task reset.", run.RunId);
            return false;
        }

        checkpoint.PlanRevision++;
        await AppendEventAsync(Guid.Parse(run.RunId), "task_graph.created", $"{checkpoint.Tasks.Count} task(s) replanned from supervision revision.", new
        {
            run_id = run.RunId,
            plan_revision = checkpoint.PlanRevision,
            supervision_round = checkpoint.SupervisionRound,
            replan = true,
            task_count = checkpoint.Tasks.Count,
            task_keys = checkpoint.Tasks.Select(item => item.TaskKey).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        await TrySyncPlannedTasksToSessionPlanAsync(
            checkpoint.SessionId,
            checkpoint.RunId,
            checkpoint.Tasks,
            checkpoint.UserGoal,
            cancellationToken).ConfigureAwait(false);
        await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.TaskGraphCreated, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string SupervisionReplanInstructions(
        FullDuplexCheckpointV1 checkpoint,
        string baseInstructions,
        SupervisionVerdict verdict,
        int[] reviseIndexes)
    {
        var parts = new List<string>
        {
            "Supervision rejected the previous execution and requests a revised plan (this is a replan, not a fresh plan).",
            $"Supervision round: {checkpoint.SupervisionRound}."
        };
        var reasons = verdict.Reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).ToArray();
        if (reasons.Length != 0)
        {
            parts.Add("Supervision reasons:");
            parts.AddRange(reasons.Select(reason => $"- {reason}"));
        }
        if (reviseIndexes.Length != 0)
        {
            parts.Add("Flagged tasks and their last outcomes (repair or replace these; keep their task_key when the work continues):");
            foreach (var index in reviseIndexes)
            {
                var node = checkpoint.Tasks[index];
                parts.Add($"- task_key '{node.TaskKey}' (lane '{node.LaneKey ?? "main"}'): {node.Title}");
                if (!string.IsNullOrWhiteSpace(node.ResultSummary))
                    parts.Add($"  last summary: {Truncate(node.ResultSummary, 400)}");
                var unsatisfied = node.CriteriaVerdicts.Where(item => !item.Satisfied).ToArray();
                foreach (var item in unsatisfied)
                    parts.Add($"  unsatisfied criterion '{item.Criterion}': {Truncate(item.Evidence, 300)}");
                foreach (var evidence in node.Evidence.Where(item => !string.IsNullOrWhiteSpace(item)).Take(3))
                    parts.Add($"  evidence: {Truncate(evidence, 300)}");
            }
        }
        parts.Add("Completed tasks (by task_key) are preserved automatically; do not rename completed work. Keep the graph within the frozen worker roster and tool manifest.");
        return baseInstructions + "\n\n" + string.Join("\n", parts);
    }

    private async Task<RuntimeAgentInstance> EnsureSupervisorAgentAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RuntimeAgentDefinition definition,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var all = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var supervisor = checkpoint.SupervisorAgentId is { } instanceId
            ? all.FirstOrDefault(item => item.Id == instanceId)
            : null;
        supervisor ??= all.FirstOrDefault(item => !item.Generated && item.AgentVersionId == definition.AgentVersionId);
        if (supervisor is not null)
        {
            VerifyRootInstance(supervisor, definition, checkpoint.SupervisorAgentId, "supervisor");
            return supervisor;
        }

        supervisor = await _instances.CreateRootAsync(new RuntimeAgentSeed(
            Guid.Parse(run.SessionId),
            runId,
            definition.Id,
            definition.Layer,
            definition.Role,
            "chat",
            definition.Capabilities,
            definition.AllowedTools,
            ResourceSeed(definition.ResourceGrants),
            configuration.Context.DefaultTokenBudget,
            AgentDefinitionId: definition.AgentDefinitionId,
            AgentVersionId: definition.AgentVersionId,
            VersionContentHash: definition.VersionContentHash,
            AllowedDispatchTargets: definition.AllowedDispatchTargets), cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "agent.created", "Supervision agent created.", new
        {
            agent_instance_id = supervisor.Id,
            agent_slug = definition.Id,
            agent_definition_id = supervisor.AgentDefinitionId,
            agent_version_id = supervisor.AgentVersionId,
            layer = supervisor.Layer,
            role = supervisor.Role
        }, cancellationToken).ConfigureAwait(false);
        return supervisor;
    }

    private Task PublishSupervisionReviewAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken ct,
        IReadOnlyList<string>? options = null)
    {
        var supervisorRequired = configuration.Supervision.RequiredBeforeFinal;
        var supervisorAvailable = configuration.OperationAgents.Any(agent =>
            agent.Enabled && string.Equals(agent.Id, "supervisor", StringComparison.Ordinal));
        var brief = BuildSupervisionReviewBrief(
            checkpoint.Tasks,
            checkpoint.SupervisionRound,
            configuration.Supervision.MaxRevisionRounds,
            supervisorRequired,
            supervisorAvailable,
            ModeRuntimePolicyRegistry.Has(configuration.RuntimePolicy, ModeRuntimePolicyRegistry.EvidenceRefsRequiredFeature));
        return AppendEventAsync(Guid.Parse(run.RunId), "supervision.user_review.requested",
            "Supervision escalated the run and is waiting for a user decision.", new
            {
                run_id = run.RunId,
                decision = "escalate",
                reasons = checkpoint.SupervisionReasons,
                options = options ?? new[] { "continue", "correct", "cancel" },
                brief = new
                {
                    total_tasks = brief.TotalTasks,
                    completed_tasks = brief.CompletedTasks,
                    blocked_tasks = brief.BlockedTasks,
                    failed_tasks = brief.FailedTasks,
                    pending_tasks = brief.PendingTasks,
                    unmet_criteria = brief.UnmetCriteria,
                    revision_round = brief.RevisionRound,
                    max_revision_rounds = brief.MaxRevisionRounds,
                    supervisor_required = brief.SupervisorRequired,
                    supervisor_available = brief.SupervisorAvailable
                }
            }, ct, idempotencyKey: $"run:{run.RunId}:supervision-review:{checkpoint.PlanRevision}:{checkpoint.SupervisionRound}");
    }

    private async Task<FullDuplexCheckpointV1> RespondToInteractionAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
        checkpoint.MeetingResponse = checkpoint.InteractionKind switch
        {
            "status_query" => await BuildStatusResponseAsync(checkpoint, cancellationToken).ConfigureAwait(false),
            _ => await GenerateInteractionResponseAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false)
        };
        checkpoint.SupervisionDecision = "pass";
        checkpoint.Phase = "finalizing";
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "interaction-response", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "meeting.interaction.responded", "A non-execution meeting interaction was completed.", new
        {
            interaction_kind = checkpoint.InteractionKind,
            target_run_id = checkpoint.TargetRunId,
            context_revision = checkpoint.ContextRevision
        }, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private async Task<string> GenerateInteractionResponseAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var meetingDefinition = RequiredConversationAgent(configuration);
        var context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), meetingDefinition, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var factory = CreateModelFactory(configuration, checkpoint, meetingDefinition, checkpoint.MeetingAgentId, null, AnswerStreaming.AnswerLayer);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable)
        {
            throw new MeetingResponseUnavailableException(
                RunErrorTaxonomy.ModelUnavailable,
                resolution.Error ?? "Chat route is unavailable.");
        }
        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), meetingDefinition, context, cancellationToken).ConfigureAwait(false);

        var targetSummary = "No active target run.";
        if (checkpoint.TargetRunId is { } targetRunId)
        {
            var target = await _lifecycle.GetRunStateAsync(targetRunId.ToString(), cancellationToken).ConfigureAwait(false);
            targetSummary = $"Target run {target.RunId} is {target.Status.ToString().ToLowerInvariant()} at context revision {target.ContextRevision}.";
        }
        var instructions = assembly.Instructions
            + "\n\nYou are the meeting agent, the only user-facing agent. The user sent a follow-up message (interaction kind: "
            + checkpoint.InteractionKind
            + ") while another run may be active. Respond directly and honestly in the user's language: acknowledge the message, state its impact on the active task, and keep it short. Do not claim tools ran.";
        var prompt = $"Interaction kind: {checkpoint.InteractionKind}\nUser message:\n{checkpoint.UserGoal}\n\n{targetSummary}";
        var attachmentImages = await BuildAttachmentImageInputAsync(checkpoint, resolution, cancellationToken).ConfigureAwait(false);
        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            "operation.meeting",
            "meeting",
            "Produces the only formal user-facing response from governed evidence.",
            new ChatOptions { Instructions = instructions },
            prompt + attachmentImages.PromptSuffix,
            cancellationToken,
            attachmentImages.Parts).ConfigureAwait(false);
        await MarkAttachmentsDeliveredAsync(checkpoint, attachmentImages, cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, turn.Usage);
        if (!turn.HasAnswer) throw new InvalidOperationException("Meeting agent returned no output.");
        return turn.AnswerText;
    }

    private async Task<AttachmentImageInput> BuildAttachmentImageInputAsync(
        FullDuplexCheckpointV1 checkpoint,
        ChatResolution resolution,
        CancellationToken cancellationToken)
    {
        if (checkpoint.TriggerMessageId == Guid.Empty
            || _services.GetService(typeof(IMessageAttachmentStore)) is not IMessageAttachmentStore attachments)
            return AttachmentImageInput.Empty;
        return await AttachmentImageParts.BuildAsync(
            attachments,
            checkpoint.SessionId,
            checkpoint.TriggerMessageId,
            resolution,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkAttachmentsDeliveredAsync(
        FullDuplexCheckpointV1 checkpoint,
        AttachmentImageInput images,
        CancellationToken cancellationToken)
    {
        if (images.DeliveredAttachmentIds.Count == 0
            || _services.GetService(typeof(IMessageAttachmentStore)) is not IMessageAttachmentStore attachments
            || checkpoint.RunId == Guid.Empty)
            return;
        try
        {
            await attachments.MarkDeliveredAsync(images.DeliveredAttachmentIds, checkpoint.RunId, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not persist attachment delivery receipt for run {RunId}.", checkpoint.RunId);
        }
    }

    private async Task<string> BuildStatusResponseAsync(FullDuplexCheckpointV1 checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.TargetRunId is not { } targetRunId)
        {
            return "I need the active run id to provide its status.";
        }
        var target = await _lifecycle.GetRunStateAsync(targetRunId.ToString(), cancellationToken).ConfigureAwait(false);
        return $"Run {target.RunId} is {target.Status.ToString().ToLowerInvariant()} at context revision {target.ContextRevision}.";
    }

    private async Task<bool> ApplyPendingContextPatchesAsync(
        RunState run,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        if (_services.GetService(typeof(ITinaChatRunInput)) is ITinaChatRunInput chatInputs
            && await chatInputs.GetForSessionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false) is not null)
            return false;
        var patches = await _conversations.ListAppliedContextPatchesAsync(
            checkpoint.SessionId,
            checkpoint.RunId,
            checkpoint.ContextRevision,
            cancellationToken).ConfigureAwait(false);
        if (patches.Count == 0) return false;

        foreach (var patch in patches)
        {
            checkpoint.ContextRevision = patch.AppliedRevision;
            await _lifecycle.AdvanceRunContextRevisionAsync(run.RunId, patch.AppliedRevision, cancellationToken).ConfigureAwait(false);
            var supervisionCorrection = checkpoint.Phase == "awaiting_user"
                && string.Equals(checkpoint.SupervisionDecision, "escalate", StringComparison.OrdinalIgnoreCase);
            if (patch.Kind == "goal_adjustment" || supervisionCorrection)
            {
                if (patch.Kind == "goal_adjustment") checkpoint.UserGoal = patch.Content;
                checkpoint.Phase = "planning";
                checkpoint.SupervisionDecision = null;
                checkpoint.SupervisionReasons = [];
                checkpoint.UserAcceptedIncompleteOutcome = false;
                checkpoint.MeetingResponse = null;
                foreach (var lane in checkpoint.Lanes)
                {
                    lane.Escalated = false;
                    lane.Status = LaneStatus.Pending;
                    lane.SupervisionDecision = null;
                    lane.SupervisionReasons = [];
                    lane.LastGateFactsHash = null;
                }
                await AppendEventAsync(Guid.Parse(run.RunId), "context.goal_adjusted", "The active goal changed and will be replanned.", new
                {
                    patch_id = patch.Id,
                    base_context_revision = patch.BaseRevision,
                    context_revision = patch.AppliedRevision,
                    plan_revision = checkpoint.PlanRevision
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AppendEventAsync(Guid.Parse(run.RunId), "context.supplement_applied", "Supplemental context was applied to the active run.", new
                {
                    patch_id = patch.Id,
                    base_context_revision = patch.BaseRevision,
                    context_revision = patch.AppliedRevision
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        return true;
    }

    private async Task FinalizeAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var correctionCompletionReady = false;

        if (CorrectionRuntimePolicy.Has(configuration.RuntimePolicy, CorrectionRuntimePolicy.CompletionContractFeature)
            && checkpoint.Correction is not null)
        {
            var correctionRevisionBefore = checkpoint.Correction.Revision;
            var unresolved = CorrectionTracePolicy.EvaluateCompletion(
                checkpoint.Correction,
                configuration.RuntimePolicy!,
                checkpoint.UserGoal,
                checkpoint.Correction.Completion.ModifiedArtifacts.Count > 0);
            correctionCompletionReady = unresolved is null;
            if (correctionCompletionReady && checkpoint.Correction.Revision != correctionRevisionBefore)
            {
                checkpoint = await SaveCheckpointAsync(
                    checkpoint,
                    checkpoint.CheckpointRevision,
                    "correction-completion-ready",
                    cancellationToken).ConfigureAwait(false);
                await AppendCorrectionTraceEventAsync(run, checkpoint.Correction, cancellationToken).ConfigureAwait(false);
            }
            if (unresolved is not null)
            {
                checkpoint.Phase = "awaiting_user";
                checkpoint.MeetingResponse = null;
                var userAuthority = checkpoint.Correction.Completion.Requirement == "user";
                checkpoint.Correction.Status = userAuthority ? "awaiting_user" : "blocked";
                if (!userAuthority)
                {
                    checkpoint.SupervisionDecision = "escalate";
                    checkpoint.UserAcceptedIncompleteOutcome = false;
                    checkpoint.SupervisionReasons = checkpoint.SupervisionReasons
                        .Append($"Correction completion contract blocked: {unresolved}")
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                }
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "correction-completion-blocked", cancellationToken).ConfigureAwait(false);
                await AppendCorrectionTraceEventAsync(run, checkpoint.Correction, cancellationToken).ConfigureAwait(false);
                await TrySetRunStatusAsync(run.RunId, "awaiting_user", unresolved, cancellationToken).ConfigureAwait(false);
                if (!userAuthority)
                {
                    await PublishSupervisionReviewAsync(
                        run,
                        configuration,
                        checkpoint,
                        cancellationToken,
                        ["correct", "cancel"]).ConfigureAwait(false);
                }
                return;
            }
        }

        if (!checkpoint.UserAcceptedIncompleteOutcome && !correctionCompletionReady)
        {
            var supervisorRequired = configuration.Supervision.RequiredBeforeFinal
                && configuration.OperationAgents.Any(agent =>
                    agent.Enabled && string.Equals(agent.Id, "supervisor", StringComparison.Ordinal));
            var facts = new SupervisionVerdict(SupervisionDecision.Pass, [], []);
            facts = EnforceTaskOutcomeFacts(
                checkpoint.Tasks,
                facts,
                checkpoint.SupervisionRound,
                checkpoint.SupervisionRound);
            facts = EnforceCriterionFacts(
                checkpoint.Tasks,
                facts,
                checkpoint.SupervisionRound,
                checkpoint.SupervisionRound,
                requireSupervisorEvidence: supervisorRequired);
            var unresolvedDecision = checkpoint.SupervisionDecision is not ("pass" or "skipped");
            if (unresolvedDecision || facts.Decision != SupervisionDecision.Pass)
            {
                checkpoint.SupervisionDecision = "escalate";
                checkpoint.SupervisionReasons = checkpoint.SupervisionReasons
                    .Concat(facts.Reasons)
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (unresolvedDecision)
                {
                    checkpoint.SupervisionReasons.Add("The persisted supervision decision is unresolved; user review is required.");
                }
                checkpoint.Phase = "awaiting_user";
                checkpoint.MeetingResponse = null;
                checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "finalization-facts-rejected", cancellationToken).ConfigureAwait(false);
                if (run.Status != RunStatus.AwaitingUser)
                {
                    await TrySetRunStatusAsync(run.RunId, "awaiting_user", "Completion facts require user review before this run can finish.", cancellationToken).ConfigureAwait(false);
                }
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(checkpoint.MeetingResponse))
        {
            var stateBeforeMeeting = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            if (stateBeforeMeeting.Status == RunStatus.Cancelled)
            {
                await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            await PersistRunEvidenceAsync(runId, run, checkpoint, cancellationToken).ConfigureAwait(false);
            var meetingDefinition = RequiredConversationAgent(configuration);
            var context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), meetingDefinition, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
            try
            {
                checkpoint.MeetingResponse = await GenerateMeetingResponseAsync(run, configuration, checkpoint, meetingDefinition, context, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelInvocationExhaustedException modelFailure) when (CanUseMeetingEvidenceFallback(checkpoint, modelFailure))
            {
                checkpoint.MeetingResponse = BuildMeetingEvidenceFallback(checkpoint, modelFailure.Category);
                await AppendEventAsync(runId, "meeting.response_fallback",
                    "The final meeting model was unavailable; persisted execution evidence was returned directly.",
                    new
                    {
                        run_id = runId,
                        error_category = modelFailure.Category,
                        retry_count = checkpoint.TransientModelRetryCount,
                        task_count = checkpoint.Tasks.Count
                    }, cancellationToken,
                    idempotencyKey: $"run:{runId}:meeting-response-fallback").ConfigureAwait(false);
            }
            catch (MeetingResponseUnavailableException meetingFailure) when (CanUseMeetingEvidenceFallback(checkpoint))
            {
                checkpoint.MeetingResponse = BuildMeetingEvidenceFallback(checkpoint, meetingFailure.Category);
                await AppendEventAsync(runId, "meeting.response_fallback",
                    "The final meeting response was unavailable; persisted execution evidence was returned directly.",
                    new
                    {
                        run_id = runId,
                        error_category = meetingFailure.Category,
                        retry_count = checkpoint.TransientModelRetryCount,
                        task_count = checkpoint.Tasks.Count
                    }, cancellationToken,
                    idempotencyKey: $"run:{runId}:meeting-response-fallback").ConfigureAwait(false);
            }

            var stateAfterMeeting = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            if (stateAfterMeeting.Status == RunStatus.Cancelled)
            {
                checkpoint.MeetingResponse = null;
                await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (IsTerminal(stateAfterMeeting.Status)
                || RecoveryPolicy.IsAwaitingDecision(stateAfterMeeting.Status)
                || stateAfterMeeting.Status == RunStatus.Paused)
            {
                checkpoint.MeetingResponse = null;
                return;
            }
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "meeting-response", cancellationToken).ConfigureAwait(false);
        }

        var meetingResponse = checkpoint.MeetingResponse ?? throw new InvalidDataException("Meeting response is missing from finalization checkpoint.");

        var completionSource = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        if (completionSource.Status == RunStatus.Cancelled)
        {
            await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (IsTerminal(completionSource.Status)
            || RecoveryPolicy.IsAwaitingDecision(completionSource.Status)
            || completionSource.Status == RunStatus.Paused)
        {
            return;
        }
        var expectedCompletionStatus = RunStatusStorageValue(completionSource.Status);
        if (expectedCompletionStatus is not ("executing" or "reviewing")) return;
        var claimedCompletion = await _lifecycle.TryClaimRunCompletionAsync(
            run.RunId,
            expectedCompletionStatus,
            LeaseEpochFor(runId)?.OwnerId,
            LeaseEpochFor(runId)?.RecoveryCount,
            cancellationToken).ConfigureAwait(false);
        var claimedState = await _lifecycle.GetRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        if (!claimedCompletion && claimedState.CompletedAt is null)
        {
            if (claimedState.Status == RunStatus.Cancelled)
            {
                await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        if (claimedState.Status is RunStatus.Cancelled or RunStatus.Failed)
        {
            if (claimedState.Status == RunStatus.Cancelled)
            {
                await FinalizeCancellationCoreAsync(runId, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var answerDraft = _answerDrafts.TryRemove(runId, out var produced) ? produced : null;
        if (answerDraft is { PublishedAnyFrame: false })
        {
            answerDraft.SetWhole(meetingResponse);
            await PublishRunStreamAsync(run.RunId, new DurableRunStreamAppend(
                checkpoint.TurnId,
                "answer.delta",
                Delta: meetingResponse,
                IdempotencyKey: $"run:{run.RunId}:turn:{checkpoint.TurnId}:meeting:answer.delta:0"), cancellationToken).ConfigureAwait(false);
        }

        if (checkpoint.AssistantMessageId is null)
        {
            var assistant = await _conversations.AppendMessageAsync(checkpoint.SessionId, "assistant", meetingResponse,
                runId, checkpoint.TurnId, $"run:{run.RunId}:turn:{checkpoint.TurnId}:assistant:v1", cancellationToken).ConfigureAwait(false);
            checkpoint.AssistantMessageId = assistant.Id;
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "assistant-persisted", cancellationToken).ConfigureAwait(false);
        }

        await PersistRunEvidenceAsync(runId, run, checkpoint, cancellationToken).ConfigureAwait(false);
        if (string.Equals(run.RunKind, SideQuestionService.RunKind, StringComparison.OrdinalIgnoreCase))
        {
            await ArchiveEvidenceAsync(run, null, EvidenceKinds.SideQuestion, "Side question answer", "oracle",
                $"Question:\n{checkpoint.UserGoal}\n\nAnswer:\n{meetingResponse}",
                $"run:{run.RunId}:side-question-answer", cancellationToken).ConfigureAwait(false);
        }

        var revision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        await _conversations.CompleteTurnAsync(checkpoint.TurnId, runId, checkpoint.AssistantMessageId, revision, "completed", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "user.response", "Meeting agent response persisted.", new
        {
            message_id = checkpoint.AssistantMessageId,
            character_count = meetingResponse.Length,
            supervision_decision = checkpoint.SupervisionDecision
        }, cancellationToken).ConfigureAwait(false);
        await PublishRunStreamAsync(run.RunId, new DurableRunStreamAppend(
            checkpoint.TurnId,
            "done",
            checkpoint.AssistantMessageId,
            UsageJson: Maf18RuntimeAdapter.SerializeUsage(checkpoint.ModelUsage),
            FinishReason: "completed",
            IdempotencyKey: $"run:{run.RunId}:turn:{checkpoint.TurnId}:done"), cancellationToken).ConfigureAwait(false);
        await _lifecycle.CompleteRunAsync(run.RunId, cancellationToken).ConfigureAwait(false);

        _autonomousPlanning?.Forget(runId);
        _liveMcpBoardPosts.TryRemove(runId, out _);
        _compactionRecommendedRevisions.TryRemove(runId, out _);
        PruneSettledTerminalGates(runId);
        try { _teamBoardMcpBridge?.RevokeRunTokens(runId); }
        catch (Exception ex) { _logger.TryLogDebug(ex, "Could not revoke team MCP tokens on completion for run {RunId}.", runId); }

        await NotifyCompanionBackflowAsync(
            run,
            configuration,
            checkpoint,
            "completed",
            BoundText(meetingResponse, 400),
            cancellationToken).ConfigureAwait(false);

        checkpoint = await DrainRunDirectivesAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.RunFinalized, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        await _instances.ReleaseRunInstancesAsync(runId, cancellationToken).ConfigureAwait(false);
        await ReleaseRunLeasesAsync(runId, cancellationToken).ConfigureAwait(false);
        if (_services.GetService<IToolExecutionContextLifecycle>() is { } contexts)
            await contexts.ReleaseAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        await SetRunMembersOfflineAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private void PruneSettledTerminalGates(Guid currentRunId)
    {
        if (_terminalFinalizationGates.Count <= 128) return;
        foreach (var pair in _terminalFinalizationGates)
        {
            if (pair.Key != currentRunId && pair.Value.CurrentCount == 1)
            {
                _terminalFinalizationGates.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task<string> GenerateMeetingResponseAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        RuntimeAgentDefinition meetingDefinition,
        ContextPack context,
        CancellationToken cancellationToken)
    {
        var factory = CreateModelFactory(configuration, checkpoint, meetingDefinition, checkpoint.MeetingAgentId, null, AnswerStreaming.AnswerLayer);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), meetingDefinition, context, cancellationToken).ConfigureAwait(false);
        var escalation = checkpoint.SupervisionDecision == "escalate"
            ? "\n\nIMPORTANT: supervision escalated this run. Explain the unresolved decision clearly and ask the user for direction."
            : string.Empty;
        var evidence = string.Join("\n", checkpoint.Tasks.Select(FormatTaskEvidenceForMeeting));
        var findings = await ExplorationFindingsForMeetingAsync(run, configuration, cancellationToken).ConfigureAwait(false);
        var instructions = assembly.Instructions
            + "\n\nYou are the conversation agent responsible for this run's reply. Reply directly and honestly in the user's language. Summarize completed work, evidence, limits, and next action. Do not claim tools ran if evidence does not say so."
            + (findings is null ? string.Empty : " Settled findings cite evidence; uncited hints were withheld and must not be presented as conclusions.")
            + escalation;
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nExecution evidence:\n{evidence}"
            + (findings is null ? string.Empty : $"\n\nSettled exploration findings:\n{findings}");
        var attachmentImages = await BuildAttachmentImageInputAsync(checkpoint, resolution, cancellationToken).ConfigureAwait(false);
        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            "operation.meeting",
            "meeting",
            "Produces the only formal user-facing response from governed evidence.",
            new ChatOptions { Instructions = instructions },
            prompt + attachmentImages.PromptSuffix,
            cancellationToken,
            attachmentImages.Parts).ConfigureAwait(false);
        await MarkAttachmentsDeliveredAsync(checkpoint, attachmentImages, cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, turn.Usage);
        if (!turn.HasAnswer)
        {
            throw new MeetingResponseUnavailableException("empty_model_response", "Meeting agent returned no output.");
        }
        return turn.AnswerText;
    }

    private async Task<string?> ExplorationFindingsForMeetingAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        CancellationToken cancellationToken)
    {
        if (_exploration is null
            || !ModeRuntimePolicyRegistry.Has(configuration.RuntimePolicy, ModeRuntimePolicyRegistry.WorkerSettlementFeature)
            || !Guid.TryParse(run.TenantId, out var tenantId) || !Guid.TryParse(run.WorkspaceId, out var workspaceId)
            || !Guid.TryParse(run.SessionId, out var sessionId) || !Guid.TryParse(run.RunId, out var runId))
            return null;

        IReadOnlyList<ExplorationNode> nodes;
        try
        {
            nodes = await _exploration.QueryAsync(new ExplorationScope(tenantId, workspaceId, sessionId, runId),
                new ExplorationQuery(RunId: runId, Limit: 64), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not read the exploration findings of run {RunId}.", run.RunId);
            return null;
        }

        var cited = nodes
            .Where(node => node.Kind is ExplorationNodeKinds.Fact or ExplorationNodeKinds.Finding)
            .OrderByDescending(node => node.CreatedAt)
            .Take(MeetingFindingLimit)
            .ToArray();
        var withheld = nodes.Count(node => node.Kind == ExplorationNodeKinds.Hint);
        if (cited.Length == 0 && withheld == 0) return null;

        var lines = cited.Select(node =>
        {
            var content = node.Content?.ReplaceLineEndings(" ").Trim();
            if (content is { Length: > MeetingFindingChars }) content = content[..MeetingFindingChars] + "…";
            var refs = node.EvidenceRefs.Count == 0 ? "no refs" : string.Join(", ", node.EvidenceRefs.Take(4));
            return $"- [{node.Kind}] {node.Title}: {content ?? "(no detail)"} (refs: {refs})";
        }).ToList();
        if (withheld > 0)
            lines.Add($"- {withheld} hint(s) had no evidence reference and were withheld from this reply.");
        return string.Join("\n", lines);
    }

    internal static string FormatTaskEvidenceForMeeting(DurableTaskNode item)
    {
        var who = item.TeammateHandle ?? item.WorkerHandle ?? item.WorkerAgentSlug ?? "unassigned";
        var builder = new StringBuilder()
            .Append("- [").Append(item.ResultStatus ?? item.Status).Append("] ")
            .Append(item.Title).Append(" (").Append(who).Append(')');
        var ownBranch = ResolveOwnWorktreeBranch(item);
        if (!string.IsNullOrWhiteSpace(item.TaskWorktreePath)
            || !string.IsNullOrWhiteSpace(ownBranch)
            || item.DispatchedByTaskId is not null)
        {
            var meta = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(item.TaskWorktreePath)) meta.Add($"worktree: {item.TaskWorktreePath}");
            if (!string.IsNullOrWhiteSpace(ownBranch)) meta.Add($"branch: {ownBranch}");
            if (item.DispatchedByTaskId is { } parentId) meta.Add($"dispatched_by: {parentId}");
            builder.Append(" [").Append(string.Join(", ", meta)).Append(']');
        }
        builder.Append(": ").Append(Bound(item.ResultSummary ?? "no result reported"));
        foreach (var entry in item.Evidence)
        {
            if (entry.StartsWith("criterion_evidence:", StringComparison.Ordinal))
            {
                var body = entry["criterion_evidence:".Length..];
                var split = body.IndexOf("||", StringComparison.Ordinal);
                builder.Append("\n    - ✓ ")
                    .Append(split < 0 ? Bound(body) : $"{Bound(body[..split])} — {Bound(body[(split + 2)..])}");
            }
            else if (entry.StartsWith("missing_criterion_evidence:", StringComparison.Ordinal))
            {
                builder.Append("\n    - ✗ no evidence: ").Append(Bound(entry["missing_criterion_evidence:".Length..]));
            }
        }
        return builder.ToString();

        static string Bound(string value)
        {
            var text = value.ReplaceLineEndings(" ").Trim();
            return text.Length <= MeetingEvidenceLineLimit ? text : text[..MeetingEvidenceLineLimit] + "…";
        }
    }

    internal static bool CanUseMeetingEvidenceFallback(FullDuplexCheckpointV1 checkpoint)
    {
        if (checkpoint.Tasks.Count == 0) return false;
        var terminal = checkpoint.Tasks.All(task => task.Status is "completed" or "failed" or "blocked");
        return terminal && (checkpoint.Tasks.All(task => task.Status == "completed")
            || checkpoint.UserAcceptedIncompleteOutcome);
    }

    internal static bool CanUseMeetingEvidenceFallback(
        FullDuplexCheckpointV1 checkpoint,
        ModelInvocationExhaustedException failure) =>
        CanUseMeetingEvidenceFallback(checkpoint)
        && (!IsDurableModelRetryCategory(failure.Category)
            || checkpoint.TransientModelRetryCount >= MaxDurableModelRetries);

    internal static string BuildMeetingEvidenceFallback(FullDuplexCheckpointV1 checkpoint, string errorCategory)
    {
        var chinese = checkpoint.UserGoal.Any(character => character is >= '\u4e00' and <= '\u9fff');
        var allCompleted = checkpoint.Tasks.All(task => task.Status == "completed");
        var lines = new List<string>
        {
            chinese
                ? allCompleted
                    ? "执行任务已经完成，但负责生成最终汇总的模型在重试后仍不可用。下面内容直接来自已持久化的执行证据："
                    : "用户已选择在部分任务未完成的情况下继续，但最终汇总模型不可用。下面内容直接来自已持久化的执行证据："
                : allCompleted
                    ? "The execution tasks completed, but the model responsible for the final summary remained unavailable after retry. The following comes directly from persisted execution evidence:"
                    : "The user chose to continue with incomplete tasks, but the final summary model was unavailable. The following comes directly from persisted execution evidence:"
        };
        foreach (var task in checkpoint.Tasks)
        {
            var summary = string.IsNullOrWhiteSpace(task.ResultSummary)
                ? (chinese ? "已完成，但没有留下摘要。" : "Completed without a stored summary.")
                : TruncateEvidence(task.ResultSummary);
            lines.Add($"- [{task.Status}] {task.Title}: {summary}");
            foreach (var evidence in task.Evidence
                .Where(item => !string.IsNullOrWhiteSpace(item)
                    && !string.Equals(item, task.ResultSummary, StringComparison.Ordinal)
                    && !item.StartsWith("dispatch:", StringComparison.Ordinal))
                .Take(2))
            {
                lines.Add(chinese
                    ? $"  证据：{TruncateEvidence(evidence)}"
                    : $"  Evidence: {TruncateEvidence(evidence)}");
            }
        }
        foreach (var reason in checkpoint.SupervisionReasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).Take(3))
        {
            lines.Add(chinese
                ? $"- 验收说明：{TruncateEvidence(reason)}"
                : $"- Review note: {TruncateEvidence(reason)}");
        }
        lines.Add(chinese
            ? $"说明：这份答复未经过 meeting 模型润色；模型错误类别为 {errorCategory}。"
            : $"Note: this response was not rewritten by the meeting model; model error category: {errorCategory}.");
        return string.Join("\n", lines);
    }

    private async Task PersistRunEvidenceAsync(Guid runId, RunState run, FullDuplexCheckpointV1 checkpoint, CancellationToken cancellationToken)
    {
        var turns = checkpoint.Tasks.SelectMany(task => task.ToolTurns).ToList();
        var taskEvidence = checkpoint.Tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ResultSummary) || task.Evidence.Count > 0)
            .TakeLast(MaxRunEvidenceTurns)
            .Select(task => TruncateEvidence(
                $"task {task.TaskKey} {task.Status}: {task.ResultSummary ?? string.Empty}; "
                + string.Join(" | ", task.Evidence
                    .Where(item => !string.IsNullOrWhiteSpace(item)
                        && !string.Equals(item, task.ResultSummary, StringComparison.Ordinal))
                    .Take(2))))
            .ToList();
        var toolEvidence = turns
            .TakeLast(MaxRunEvidenceTurns)
            .Select(turn =>
            {
                var status = string.IsNullOrWhiteSpace(turn.DispatchStatus) ? "completed" : turn.DispatchStatus;
                var category = string.IsNullOrWhiteSpace(turn.ErrorCategory) ? string.Empty : $" ({turn.ErrorCategory})";
                return TruncateEvidence($"{turn.ToolId} {status}{category}: {turn.ResultJson ?? string.Empty}");
            })
            .ToList();
        if (taskEvidence.Count == 0 && toolEvidence.Count == 0) return;

        var sections = new List<string>();
        if (taskEvidence.Count > 0)
        {
            sections.Add($"task_evidence — {checkpoint.Tasks.Count} task(s), showing {taskEvidence.Count}\n"
                + string.Join('\n', taskEvidence));
        }
        if (toolEvidence.Count > 0)
        {
            sections.Add($"tool_evidence — {turns.Count} tool round(s), showing the last {toolEvidence.Count}\n"
                + string.Join('\n', toolEvidence));
        }
        var content = string.Join("\n\n", sections);
        await _conversations.AppendMessageAsync(checkpoint.SessionId, "tool_evidence", content,
            runId, checkpoint.TurnId, $"run:{run.RunId}:turn:{checkpoint.TurnId}:tool-evidence:v1", cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "run.evidence_recorded",
            "The run's tool activity was persisted into the conversation history.",
            new
            {
                task_count = checkpoint.Tasks.Count,
                task_evidence_recorded = taskEvidence.Count,
                turn_count = turns.Count,
                tool_evidence_recorded = toolEvidence.Count
            }, cancellationToken,
            idempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:evidence-recorded").ConfigureAwait(false);
    }

    private Task FinalizeCancellationCoreAsync(
        Guid runId,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        _ = configuration;
        _ = checkpoint;
        return FinalizeCancelledRunAsync(runId, cancellationToken);
    }

    private async Task PersistCancellationTerminalAsync(
        Guid runId,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var revision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        await _conversations.CompleteTurnAsync(checkpoint.TurnId, runId, null, revision, "cancelled", cancellationToken).ConfigureAwait(false);
        await PublishRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            checkpoint.TurnId,
            "done",
            FinishReason: "cancelled",
            IdempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:cancelled"), cancellationToken).ConfigureAwait(false);
        try
        {
            await AppendEventAsync(runId, "task.cancelled", "The run was cancelled.", new { run_id = runId }, cancellationToken,
                idempotencyKey: $"run:{runId}:event:task.cancelled").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not append the cancellation audit event for run {RunId}.", runId);
        }
    }

    private async Task PersistFailureTerminalAsync(
        Guid runId,
        RunState state,
        FullDuplexCheckpointV1 checkpoint,
        string category,
        string message,
        CancellationToken cancellationToken)
    {
        var revision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        await _conversations.CompleteTurnAsync(checkpoint.TurnId, runId, null, revision, "failed", cancellationToken).ConfigureAwait(false);
        await PublishRunStreamAsync(runId.ToString(), new DurableRunStreamAppend(
            checkpoint.TurnId,
            "error",
            ErrorCategory: category,
            SafeErrorMessage: message,
            IdempotencyKey: $"run:{runId}:turn:{checkpoint.TurnId}:error:{category}"), cancellationToken).ConfigureAwait(false);
        try
        {
            await AppendEventAsync(runId, "run.failed", message, new { run_id = runId, error_category = category }, cancellationToken,
                idempotencyKey: $"run:{runId}:event:run.failed").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogWarning(ex, "Could not append the failure audit event for run {RunId}.", runId);
        }

        await NotifyCompanionBackflowAsync(
            state,
            configuration: null,
            checkpoint,
            "failed",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    // Queued User Interactions Drain & Re-admission

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
        var queue = pending.Where(item => string.Equals(item.Kind, "queued_interaction", StringComparison.Ordinal)).ToList();
        for (var index = 0; index < queue.Count; index++)
        {
            var directive = queue[index];
            var outcome = await ReleaseQueuedInteractionAsync(run, directive, cancellationToken).ConfigureAwait(false);
            switch (outcome.Status)
            {
                case "executed":
                    await _lifecycle.DrainRunDirectivesAsync(runId, [directive.Id], "executed", cancellationToken).ConfigureAwait(false);
                    released++;
                    await MoveQueueAsync(run, queue.Skip(index + 1).ToArray(), outcome.OwnerRunId!.Value, cancellationToken).ConfigureAwait(false);
                    return released;
                case "busy":
                    await MoveQueueAsync(run, queue.Skip(index).ToArray(), outcome.OwnerRunId!.Value, cancellationToken).ConfigureAwait(false);
                    return released;
                case "rejected":
                    await _lifecycle.DrainRunDirectivesAsync(runId, [directive.Id], "rejected", cancellationToken).ConfigureAwait(false);
                    released++;
                    continue;
                default:
                    return released;
            }
        }
        return released;
    }

    private async Task MoveQueueAsync(RunState from, IReadOnlyList<RunDirective> rest, Guid ownerRunId, CancellationToken cancellationToken)
    {
        if (rest.Count == 0) return;
        var fromRunId = Guid.Parse(from.RunId);
        var moved = await _lifecycle.RequeueRunDirectivesAsync(fromRunId, rest.Select(item => item.Id).ToArray(), ownerRunId, cancellationToken).ConfigureAwait(false);
        if (moved == 0) return;
        await AppendEventAsync(fromRunId, "interaction.queue_moved",
            $"{moved} queued message(s) now wait behind run {ownerRunId:N}.",
            new { behind_run_id = ownerRunId, directive_ids = rest.Select(item => item.Id).ToArray(), moved }, cancellationToken).ConfigureAwait(false);
        foreach (var directive in rest)
        {
            var payload = ParseQueuedInteractionPayload(directive.PayloadJson);
            await AppendEventAsync(ownerRunId, "run.queued", "Interaction queued behind the active run", new
            {
                interaction_id = directive.Id,
                directive_id = directive.Id,
                content = payload?.Content,
                moved_from_run_id = fromRunId
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record QueuedRelease(string Status, Guid? OwnerRunId = null);

    private async Task<QueuedRelease> ReleaseQueuedInteractionAsync(RunState run, RunDirective directive, CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var payload = ParseQueuedInteractionPayload(directive.PayloadJson);

        if (payload is null || string.IsNullOrWhiteSpace(payload.Content))
        {
            await AppendEventAsync(runId, "interaction.queued_unreadable",
                "A queued interaction could not be re-admitted: its stored payload carries no content.",
                new { directive_id = directive.Id, code = "queued_payload_unreadable" }, cancellationToken).ConfigureAwait(false);
            return new QueuedRelease("rejected");
        }

        if (_services.GetService(typeof(IFullDuplexRunCoordinator)) is not IFullDuplexRunCoordinator coordinator)
        {
            await AppendEventAsync(runId, "interaction.queued_deferred",
                "A queued interaction is still pending: this host has no run coordinator to admit it.",
                new { directive_id = directive.Id, code = "run_coordinator_unavailable" }, cancellationToken).ConfigureAwait(false);
            return new QueuedRelease("deferred");
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
                ModeVersionId: payload.ModeVersionId,
                QueueBehindActiveRun: true,
                SpaceOptions: payload.SpaceOptions,
                SessionSettingsCaptured: true,
                InterfaceLocale: payload.InterfaceLocale), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "interaction.queued_executed",
                "A queued interaction was admitted as its own run once this run finished.",
                new { directive_id = directive.Id, released_run_id = submission.RunId, existing = submission.Existing }, cancellationToken).ConfigureAwait(false);
            return new QueuedRelease("executed", submission.RunId);
        }
        catch (RunAdmissionException ex) when (ex.Code is "SESSION_BUSY" or "ACTIVE_RUN_LIMIT")
        {
            if (await NewestActiveRunAsync(directive.SessionId, runId, cancellationToken).ConfigureAwait(false) is { } owner)
                return new QueuedRelease("busy", owner);
            await AppendEventAsync(runId, "interaction.queued_deferred",
                $"A queued interaction is still waiting: {ex.Message}",
                new { directive_id = directive.Id, code = ex.Code }, cancellationToken).ConfigureAwait(false);
            return new QueuedRelease("deferred");
        }
        catch (RunAdmissionException ex)
        {
            await AppendEventAsync(runId, "interaction.queued_deferred",
                $"A queued interaction is still waiting: {ex.Message}",
                new { directive_id = directive.Id, code = ex.Code }, cancellationToken).ConfigureAwait(false);
            return new QueuedRelease("deferred");
        }
    }

    private async Task<Guid?> NewestActiveRunAsync(Guid sessionId, Guid except, CancellationToken cancellationToken)
    {
        var active = await _lifecycle.ListActiveRunsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return active.Where(item => Guid.TryParse(item.RunId, out var id) && id != except)
            .OrderByDescending(item => item.StartedAt)
            .Select(item => (Guid?)Guid.Parse(item.RunId))
            .FirstOrDefault();
    }

    internal sealed record QueuedInteractionPayload(
        string? Content,
        string? ClientMessageId,
        string? PermissionMode,
        Guid? ModeVersionId,
        SessionModelOverride? MeetingModelOverride,
        SpaceRunOptions? SpaceOptions,
        string? InterfaceLocale = null);

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
            var interfaceLocale = root.TryGetProperty("interface_locale", out var localeValue)
                && localeValue.ValueKind == JsonValueKind.String
                ? localeValue.GetString()
                : null;
            SpaceRunOptions? spaceOptions = null;
            if (root.TryGetProperty("space_options", out var optionsValue) && optionsValue.ValueKind != JsonValueKind.Null)
            {
                spaceOptions = SpaceRunOptionsJson.Parse(optionsValue);
            }
            return new QueuedInteractionPayload(
                content,
                clientMessageId,
                permissionMode,
                modeVersionId,
                meetingModelOverride,
                spaceOptions,
                interfaceLocale);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Operational Roles & Phase 11.5 Closure #1 (Auto-Evolve Memory + Async Compaction)

    private async Task EvaluateAndDispatchOperationsAsync(
        OperationalTriggerPoint point,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken,
        string? summary = null,
        string? subjectKind = null,
        string? subjectId = null)
    {
        await RaiseStandingAsync(run, configuration, OperationalTriggerEvaluator.TopicOf(point),
            summary ?? DefaultSummary(point, checkpoint), subjectKind, subjectId, cancellationToken).ConfigureAwait(false);
        if (_triggerEvaluator is null || !configuration.Triggers.Enabled) return;
        IReadOnlyList<OperationalTriggerMatch> matches;
        try
        {
            matches = _triggerEvaluator.Evaluate(point, configuration);
        }
        catch (Exception ex)
        {
            _logger.TryLogWarning(ex, "Operational trigger evaluation failed for run {RunId}.", run.RunId);
            return;
        }
        if (matches.Count == 0) return;

        foreach (var match in matches)
        {
            if (OperationalRoles.BuiltInKind(match.Agent) is null) continue;
            try
            {
                await DispatchOperationalRoleAsync(match, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.TryLogWarning(ex, "Operational role '{Agent}' dispatch failed for run {RunId}.", match.Agent.Id, run.RunId);
                try
                {
                    await AppendEventAsync(Guid.Parse(run.RunId), "operation.dispatch.failed",
                        $"Operational role '{match.Agent.Id}' failed: {SafeError(ex)}", new
                        {
                            run_id = run.RunId,
                            agent_slug = match.Agent.Id,
                            trigger_point = point.ToString(),
                            trigger_name = match.TriggerName
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception eventEx)
                {
                    _logger.TryLogDebug(eventEx, "Could not record operational dispatch failure for run {RunId}.", run.RunId);
                }
            }
        }
    }

    private Task DispatchOperationalRoleAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken) =>
        OperationalRoleKey(match.Agent) switch
        {
            "context_compressor" => DispatchContextCompressionAsync(match, run, configuration, checkpoint, cancellationToken),
            "skill_recommender" => DispatchSkillRecommendationAsync(match, run, configuration, checkpoint, cancellationToken),
            "evolution" => DispatchEvolutionAsync(match, run, configuration, checkpoint, cancellationToken),
            "git_steward" => DispatchGitStewardAsync(match, run, configuration, checkpoint, cancellationToken),
            _ => Task.CompletedTask
        };

    internal static string OperationalRoleKey(RuntimeAgentDefinition agent) =>
        (agent.Role?.Trim().ToLowerInvariant(), agent.Id?.Trim().ToLowerInvariant()) switch
        {
            ("context_maintenance", _) or (_, "context_compressor") => "context_compressor",
            ("capability_advisor", _) or (_, "skill_recommender") => "skill_recommender",
            ("experience_curator", _) or (_, "evolution") => "evolution",
            ("git_steward", _) or (_, "git_steward") => "git_steward",
            _ => string.Empty
        };

    private async Task DispatchContextCompressionAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var effectiveStrategy = ResolveEffectiveContextStrategy(configuration);
        if (string.Equals(effectiveStrategy, "direct", StringComparison.OrdinalIgnoreCase))
        {
            await AppendEventAsync(runId, "context.compaction.skipped", "Compression skipped: resolved context strategy is 'direct'.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                reason = "direct_strategy"
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (checkpoint.Tasks.Any(task => task.Status is "running" or "waiting" || !string.IsNullOrWhiteSpace(task.PendingToolExecutionId)))
        {
            await AppendEventAsync(runId, "context.compaction.skipped", "Compression skipped: a task is still executing.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                reason = "active_tasks"
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), match.Agent, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var recommendedByPack = context.Metadata.TryGetValue("compaction_recommended", out var recVal)
            && string.Equals(recVal, "true", StringComparison.OrdinalIgnoreCase);
        if (!recommendedByPack && context.EstimatedTokens < configuration.Triggers.ContextTokenThreshold)
        {
            await AppendEventAsync(runId, "context.compaction.skipped", "Compression skipped: context is below the token threshold.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                reason = "below_threshold",
                estimated_tokens = context.EstimatedTokens,
                threshold = configuration.Triggers.ContextTokenThreshold
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null, AnswerStreaming.None, observeOutput: false, interruptible: false);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), match.Agent, context, cancellationToken).ConfigureAwait(false);
        var evidence = string.Join("\n", context.Evidence.Select(item => $"[{item.Source}] {item.Content}"));
        var instructions = assembly.Instructions
            + "\n\nYou are the context compression agent. Compress the session context into a structured summary with these sections: 当前目标 / 关键约束 / 已完成事项 / 待处理事项 / 重要结论 / 风险点. Preserve the current goal and constraints exactly, keep approval conclusions, and never invent new facts.";
        var coveredThrough = HistoryLastSequence(context);
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nSession context evidence:\n{evidence}";
        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            $"operation.{match.Agent.Id}",
            match.Agent.Id,
            "Compresses session context without altering constraints.",
            new ChatOptions { Instructions = instructions },
            prompt,
            cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, turn.Usage);
        if (!turn.HasAnswer)
        {
            await AppendEventAsync(runId, "context.compaction.skipped", "Compression skipped: model returned no summary.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                reason = "empty_summary"
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var summary = turn.AnswerText;
        var baseRevision = await _conversations.GetContextRevisionAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
        var result = await _conversations.ApplyContextPatchAsync(new ContextPatchRequest(
            checkpoint.SessionId,
            baseRevision,
            summary,
            $"Context summary by {match.Agent.Id}",
            runId,
            Kind: "compaction",
            CoversThroughSequence: coveredThrough), cancellationToken).ConfigureAwait(false);
        await ArchiveEvidenceAsync(run, null, EvidenceKinds.Summary, $"Context summary at revision {baseRevision}", match.Agent.Id,
            summary, $"summary:{result.PatchId}", cancellationToken).ConfigureAwait(false);
        if (string.Equals(result.Status, "applied", StringComparison.OrdinalIgnoreCase))
        {
            await AppendEventAsync(runId, "context.compacted", "Context compression applied as a session patch.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                patch_id = result.PatchId,
                base_context_revision = baseRevision,
                context_revision = result.AppliedRevision
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AppendEventAsync(runId, "context.compacted.stale", "Context compression lost the revision race and was kept for audit.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString(),
                patch_id = result.PatchId,
                base_context_revision = baseRevision,
                context_revision = result.CurrentRevision
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunBackgroundStructuralCompactionAsync(
        RunState run,
        FrozenRunConfigurationV1 config,
        RuntimeAgentDefinition compressor,
        Guid sessionId,
        Guid runId,
        ContextPack pack,
        string goal)
    {
        var checkpoint = await TryReadCheckpointAsync(runId).ConfigureAwait(false);
        if (checkpoint is null) return;
        if (checkpoint.Tasks.Any(t => !string.IsNullOrWhiteSpace(t.PendingToolExecutionId))) return;

        var factory = CreateModelFactory(config, checkpoint, compressor, null, null, AnswerStreaming.None, observeOutput: false, interruptible: false);
        var resolution = await factory.ResolveChatAsync("chat", CancellationToken.None).ConfigureAwait(false);
        if (!resolution.IsAvailable) return;

        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(config, checkpoint), compressor, pack, CancellationToken.None).ConfigureAwait(false);
        var evidence = string.Join("\n", pack.Evidence.Select(item => $"[{item.Source}] {item.Content}"));
        var instructions = assembly.Instructions
            + "\n\nYou are the context compression agent. Compress the session context into a structured summary with these sections: 当前目标 / 关键约束 / 已完成事项 / 待处理事项 / 重要结论 / 风险点. Preserve the current goal and constraints exactly, keep approval conclusions, and never invent new facts.";
        var coveredThrough = HistoryLastSequence(pack);
        var prompt = $"Current user goal:\n{goal}\n\nSession context evidence:\n{evidence}";

        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            await factory.CreateAsync(resolution, CancellationToken.None).ConfigureAwait(false),
            $"operation.{compressor.Id}",
            compressor.Id,
            "Background structural context compaction.",
            new ChatOptions { Instructions = instructions },
            prompt,
            CancellationToken.None).ConfigureAwait(false);
        if (!turn.HasAnswer) return;

        var summary = turn.AnswerText;
        var baseRevision = await _conversations.GetContextRevisionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var result = await _conversations.ApplyContextPatchAsync(new ContextPatchRequest(
            sessionId,
            baseRevision,
            summary,
            $"Async structural compaction by {compressor.Id}",
            runId,
            Kind: "compaction",
            CoversThroughSequence: coveredThrough), CancellationToken.None).ConfigureAwait(false);
        await ArchiveEvidenceAsync(run, null, EvidenceKinds.Summary, $"Context summary at revision {baseRevision}", compressor.Id,
            summary, $"summary:{result.PatchId}", CancellationToken.None).ConfigureAwait(false);
        if (string.Equals(result.Status, "applied", StringComparison.OrdinalIgnoreCase))
        {
            await AppendEventAsync(runId, "context.compacted", "Background structural context compaction applied.", new
            {
                run_id = runId,
                agent_slug = compressor.Id,
                trigger_point = "CompactionRecommended",
                patch_id = result.PatchId,
                base_context_revision = baseRevision,
                context_revision = result.AppliedRevision
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static long? HistoryLastSequence(ContextPack context) =>
        SequenceOf(context, "session_history", "last_sequence")
        ?? SequenceOf(context, "session_compaction", "covers_through_sequence");

    private static long? SequenceOf(ContextPack context, string source, string key)
    {
        var evidence = context.Evidence.FirstOrDefault(item => item.Source == source);
        return evidence is not null
            && evidence.Metadata.TryGetValue(key, out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : null;
    }

    private async Task DispatchSkillRecommendationAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), match.Agent, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null, AnswerStreaming.None, observeOutput: false, interruptible: false);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), match.Agent, context, cancellationToken).ConfigureAwait(false);
        var taskSummary = checkpoint.Tasks.Count == 0
            ? "(no task graph yet)"
            : string.Join("\n", checkpoint.Tasks.Select(item =>
                $"- {item.TaskKey}: capabilities=[{string.Join(", ", item.RequiredCapabilities)}] tools=[{string.Join(", ", item.RequiredTools)}]"));
        var evidence = string.Join("\n", context.Evidence.Select(item => $"[{item.Source}] {item.Content}"));
        var instructions = assembly.Instructions
            + "\n\nYou are the capability advisor. Recommend execution capabilities for the current task graph. Respond with strict JSON only: {\"recommendations\":[{\"skill\":\"...\",\"reason\":\"...\",\"confidence\":\"high|medium|low\"}]}. Advise only; never execute, approve, or address the user.";
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nTask graph:\n{taskSummary}\n\nSession context evidence:\n{evidence}";
        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            $"operation.{match.Agent.Id}",
            match.Agent.Id,
            "Advises the planner on capable execution specialists without deciding.",
            new ChatOptions { Instructions = instructions },
            prompt,
            cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, turn.Usage);

        var recommendations = ParseRecommendations(turn.RawText);
        if (recommendations.Count == 0)
        {
            await AppendEventAsync(runId, "capability.recommendation.empty", "Capability advisor produced no usable recommendations.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                trigger_point = match.Point.ToString()
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var added = 0;
        foreach (var recommendation in recommendations)
        {
            if (checkpoint.RecommendedCapabilities.Count >= 10) break;
            if (checkpoint.RecommendedCapabilities.Any(existing => string.Equals(existing.Skill, recommendation.Skill, StringComparison.OrdinalIgnoreCase))) continue;
            checkpoint.RecommendedCapabilities.Add(recommendation);
            added++;
        }
        if (added > 0)
        {
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "capability-recommended", cancellationToken).ConfigureAwait(false);
        }
        await AppendEventAsync(runId, "capability.recommended", $"Capability advisor recommended {recommendations.Count} skill(s).", new
        {
            run_id = run.RunId,
            agent_slug = match.Agent.Id,
            trigger_point = match.Point.ToString(),
            added,
            recommendations = recommendations.Select(item => new { skill = item.Skill, reason = item.Reason, confidence = item.Confidence }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<RecommendedCapability> ParseRecommendations(string? text)
    {
        foreach (var candidate in ModelOutputText.ExtractJsonCandidates(text, array: false))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("recommendations", out var array)
                    || array.ValueKind != JsonValueKind.Array) continue;
                var result = new List<RecommendedCapability>();
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var skill = item.TryGetProperty("skill", out var skillNode) && skillNode.ValueKind == JsonValueKind.String
                        ? skillNode.GetString()?.Trim()
                        : null;
                    if (string.IsNullOrWhiteSpace(skill)) continue;
                    var reason = item.TryGetProperty("reason", out var reasonNode) && reasonNode.ValueKind == JsonValueKind.String
                        ? reasonNode.GetString() ?? string.Empty
                        : string.Empty;
                    var confidence = item.TryGetProperty("confidence", out var confidenceNode) && confidenceNode.ValueKind == JsonValueKind.String
                        ? confidenceNode.GetString() ?? "medium"
                        : "medium";
                    result.Add(new RecommendedCapability(skill, reason, confidence));
                }
                return result;
            }
            catch (JsonException)
            {
            }
        }
        return [];
    }

    internal async Task DispatchEvolutionAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var effectiveStrategy = ResolveEffectiveContextStrategy(configuration);
        var effectiveEvolutionMode = ResolveEffectiveEvolutionMode(configuration);
        var candidateOnly = string.Equals(effectiveStrategy, "direct", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectiveEvolutionMode, "candidate_only", StringComparison.OrdinalIgnoreCase);

        var runId = Guid.Parse(run.RunId);
        var hasMemoryCuratorTarget = candidateOnly
            ? _longTermMemory is not null
            : (_contextMemory is not null || _longTermMemory is not null);

        ContextPack? context = null;
        if (hasMemoryCuratorTarget)
        {
            context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), match.Agent, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        }

        // P2 Track 3: Gather bounded P1 diagnostics + Top-3 Winnowing overlap evidence,
        // feed the multi-run degradation & prompt-dedup proposal queue (conservative pending posture),
        // and build character-capped "Context & Trajectory Evidence" for experience_curator.
        var observation = await GatherRunContextEvolutionObservationAsync(
            run,
            runId,
            configuration,
            checkpoint,
            context,
            effectiveStrategy,
            effectiveEvolutionMode,
            cancellationToken).ConfigureAwait(false);

        var proposalService = ContextPolicyProposalServiceRegistry.Resolve(_services);
        if (proposalService is not null)
        {
            try
            {
                var queuedProposals = await proposalService.RecordRunAndGenerateProposalsAsync(observation, cancellationToken).ConfigureAwait(false);
                foreach (var queued in queuedProposals)
                {
                    await AppendEventAsync(runId, "context.evolution.proposal_queued", "Context self-evolution proposal queued for review.", new
                    {
                        run_id = run.RunId,
                        proposal_id = queued.Id,
                        kind = queued.Kind,
                        status = queued.Status,
                        target_scope = queued.TargetScope,
                        remediation_code = queued.RemediationCode,
                        evidence_run_ids = queued.EvidenceRunIds
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.TryLogDebug(ex, "Context policy proposal evaluation skipped for run {RunId}.", run.RunId);
            }
        }

        if (!hasMemoryCuratorTarget || context is null)
        {
            _logger.LogDebug("Memory curation target is not registered; skipping experience curator model turn for run {RunId}.", run.RunId);
            return;
        }

        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null, AnswerStreaming.None, observeOutput: false, interruptible: false);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), match.Agent, context, cancellationToken).ConfigureAwait(false);
        var evidence = string.Join("\n", checkpoint.Tasks.Select(item =>
            $"- [{item.ResultStatus ?? item.Status}] {item.ResultSummary}"));
        var contextTrajectoryEvidence = proposalService is not null
            ? proposalService.BuildBoundedCuratorEvidence(observation, 2000)
            : FormatFallbackCuratorEvidence(observation, 2000);
        var instructions = assembly.Instructions
            + "\n\nYou are the experience curator. Review the finished run, execution outcomes, and Context & Trajectory Evidence to extract reusable experience. "
            + "When repeated tool failures or governance redlines occurred (e.g. SAME_ARGS_RETRY, FS_STALE_VERSION, WORKTREE_GUARD, TIMEOUT), extract concrete failure_pattern or supervision_rule items with explicit applicability and expiry_condition. "
            + "Respond with strict JSON only: "
            + "{\"memory_candidates\":[{\"scope\":\"workspace\",\"kind\":\"success_pattern|failure_pattern|fact|preference|decision|task_template|supervision_rule\",\"content\":\"...\",\"confidence\":0.8,\"applicability\":\"...\",\"expiry_condition\":\"...\"}],"
            + "\"agent_candidates\":[{\"name\":\"...\",\"layer\":\"execution\",\"agent_type\":\"task_executor\",\"confidence\":0.7,\"proposal\":{\"slug\":\"...\",\"display_name\":\"...\",\"layer\":\"execution\",\"role\":\"task_executor\",\"system_prompt\":\"...\",\"description\":\"...\",\"capabilities\":[]}}]}. "
            + "Only propose items backed by concrete evidence from this run; an empty object means nothing was worth keeping.";
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nExecution evidence:\n{evidence}\n\nSupervision decision: {checkpoint.SupervisionDecision ?? "none"}\n\nContext & Trajectory Evidence:\n{contextTrajectoryEvidence}\n\nSession context evidence:\n{string.Join("\n", context.Evidence.Select(item => $"[{item.Source}] {item.Content}"))}";
        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            $"operation.{match.Agent.Id}",
            match.Agent.Id,
            "Curates reusable experience from finished runs.",
            new ChatOptions { Instructions = instructions },
            prompt,
            cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, turn.Usage);

        var (memoryCandidates, agentCandidates) = ParseCurationOutput(turn.RawText);
        memoryCandidates = memoryCandidates
            .Where(item => configuration.Memory.AllowedScopes.Contains(item.Scope, StringComparer.OrdinalIgnoreCase)
                && configuration.Memory.AllowedKinds.Contains(item.Kind, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (memoryCandidates.Count == 0 && agentCandidates.Count == 0) return;

        var curator = await EnsureEvolutionAgentAsync(run, configuration, match.Agent, cancellationToken).ConfigureAwait(false);
        var refreshedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (scope, kind, content, confidence, applicability, expiryCondition) in memoryCandidates)
        {
            var effectiveApplicability = ResolveCuratedApplicability(kind, applicability, configuration);
            var effectiveExpiryCondition = ResolveCuratedExpiryCondition(kind, expiryCondition);

            // Phase 11.5 Closure #1 + P2: when candidateOnly == false ("auto_evolve") and
            // IContextMemoryService is available, persist curated facts/failure_pattern/supervision_rule
            // directly into active memory with run/session provenance and refresh projected Markdown.
            if (!candidateOnly && _contextMemory is not null)
            {
                var mappedScope = MapAutoEvolveMemoryScope(scope, configuration.Workspace);
                var upserted = await _contextMemory.UpsertItemAsync(new UpsertMemoryItemRequest
                {
                    Scope = mappedScope,
                    Kind = kind,
                    Content = content,
                    ProjectId = configuration.Workspace?.ProjectId,
                    WorkspaceRoot = configuration.Workspace?.RootPath,
                    SourceSessionId = checkpoint.SessionId,
                    SourceRunId = runId,
                    Applicability = effectiveApplicability,
                    ExpiryCondition = effectiveExpiryCondition,
                    Reason = $"Auto-evolved by {match.Agent.Id} from run {runId:N} (confidence {confidence:0.00})."
                }, cancellationToken).ConfigureAwait(false);
                refreshedScopes.Add(mappedScope);

                await AppendEventAsync(runId, "memory.auto_evolved", "Experience curator persisted an active memory item.", new
                {
                    run_id = run.RunId,
                    agent_slug = match.Agent.Id,
                    item_id = upserted.Id,
                    scope = upserted.Scope,
                    kind = upserted.Kind,
                    applicability = upserted.Applicability,
                    expiry_condition = upserted.ExpiryCondition,
                    version = upserted.CurrentVersion,
                    confidence
                }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (_longTermMemory is not null)
            {
                var candidate = await _longTermMemory.CreateCandidateAsync(new MemoryCandidateProposal(
                    runId,
                    curator.Id,
                    scope,
                    kind,
                    content,
                    confidence,
                    Applicability: effectiveApplicability,
                    ExpiryCondition: effectiveExpiryCondition), cancellationToken).ConfigureAwait(false);
                await AppendEventAsync(runId, "memory.candidate_created", "Experience curator proposed a memory candidate.", new
                {
                    run_id = run.RunId,
                    agent_slug = match.Agent.Id,
                    candidate_id = candidate.Id,
                    scope = candidate.Scope,
                    kind = candidate.Kind,
                    applicability = effectiveApplicability,
                    expiry_condition = effectiveExpiryCondition,
                    confidence = candidate.Confidence
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_contextMemory is not null && refreshedScopes.Count > 0)
        {
            foreach (var scopeToRefresh in refreshedScopes)
            {
                try
                {
                    await _contextMemory.GetDocumentAsync(
                        scopeToRefresh,
                        configuration.Workspace?.ProjectId,
                        configuration.Workspace?.RootPath,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.TryLogDebug(ex, "Could not refresh memory Markdown projection for scope '{Scope}' after auto-evolve.", scopeToRefresh);
                }
            }
        }

        foreach (var (name, layer, agentType, confidence, proposal) in agentCandidates)
        {
            var candidate = await _instances.CreateCandidateAsync(new AgentCandidateProposal(
                runId,
                curator.Id,
                name,
                layer,
                agentType,
                confidence,
                proposal), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(runId, "agent.candidate_proposed", "Experience curator proposed an agent candidate.", new
            {
                run_id = run.RunId,
                agent_slug = match.Agent.Id,
                candidate_id = candidate.Id,
                name = candidate.Name,
                layer = candidate.Layer,
                agent_type = candidate.AgentType,
                confidence = candidate.ConfidenceScore
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RunContextEvolutionObservation> GatherRunContextEvolutionObservationAsync(
        RunState run,
        Guid runId,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        ContextPack? context,
        string effectiveStrategy,
        string effectiveEvolutionMode,
        CancellationToken cancellationToken)
    {
        var tenantGuid = Guid.TryParse(run.TenantId, out var tId) ? tId : (Guid?)null;
        var workspaceGuid = Guid.TryParse(run.WorkspaceId, out var wId) ? wId : (Guid?)null;
        var modeId = TryParseModeIdFromRuntimeProfile(configuration.RuntimeProfileId);

        RunContextDiagnosticsDto? diagnostics = null;
        if (_services.GetService(typeof(IRunContextDiagnosticsService)) is IRunContextDiagnosticsService diagnosticsService)
        {
            try
            {
                diagnostics = await diagnosticsService.DiagnoseRunAsync(
                    runId,
                    tenantGuid,
                    workspaceGuid,
                    configuration.Context.DefaultTokenBudget,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.TryLogDebug(ex, "RunContextDiagnosticsService failed during closeout evolution for run {RunId}.", run.RunId);
            }
        }

        var overlapPairs = new List<ContextOverlapPairEvidenceInput>();
        if (_services.GetService(typeof(IContextOverlapAuditor)) is IContextOverlapAuditor overlapAuditor)
        {
            try
            {
                var scope = new ContextAuditExecutionScope(
                    tenantGuid ?? Guid.Empty,
                    workspaceGuid ?? Guid.Empty,
                    checkpoint.SessionId,
                    configuration.Workspace?.ProjectId,
                    configuration.Workspace?.RootPath);
                var audit = await overlapAuditor.AuditAsync(new ContextOverlapAuditRequestDto
                {
                    RunId = runId,
                    ProjectId = configuration.Workspace?.ProjectId,
                    ModeId = modeId?.ToString("D") ?? configuration.ModeSlug,
                    WorkspaceRoot = configuration.Workspace?.RootPath,
                    MaxExcerptLength = 160,
                    MaxMatchesPerPair = 3
                }, scope, cancellationToken).ConfigureAwait(false);

                var pathBySourceId = audit.Sources.ToDictionary(s => s.Id, s => s.Path, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in audit.Pairs.OrderByDescending(p => p.EstimatedDuplicateTokens).Take(5))
                {
                    var topMatch = pair.Matches.OrderByDescending(m => m.EstimatedDuplicateTokens).FirstOrDefault();
                    pathBySourceId.TryGetValue(pair.SourceAId, out var pathA);
                    pathBySourceId.TryGetValue(pair.SourceBId, out var pathB);
                    overlapPairs.Add(new ContextOverlapPairEvidenceInput(
                        SourceAId: pair.SourceAId,
                        SourceALabel: pair.SourceALabel,
                        SourceAKind: pair.SourceAKind,
                        SourceAPath: pathA,
                        SourceAStartLine: topMatch?.SourceALines.Start ?? 1,
                        SourceAEndLine: topMatch?.SourceALines.End ?? 1,
                        SourceBId: pair.SourceBId,
                        SourceBLabel: pair.SourceBLabel,
                        SourceBKind: pair.SourceBKind,
                        SourceBPath: pathB,
                        SourceBStartLine: topMatch?.SourceBLines.Start ?? 1,
                        SourceBEndLine: topMatch?.SourceBLines.End ?? 1,
                        OverlapChars: pair.SharedChars,
                        EstimatedWastedTokens: pair.EstimatedDuplicateTokens,
                        RemediationCode: pair.RemediationCode,
                        Excerpt: topMatch?.Excerpt ?? string.Empty,
                        SuggestedRemediation: pair.RemediationSummary));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.TryLogDebug(ex, "ContextOverlapAuditor failed during closeout evolution for run {RunId}.", run.RunId);
            }
        }

        var (fallbackSignals, fallbackRetryStreak) = ExtractCheckpointTrajectorySignals(checkpoint);
        var fallbackPeakOccupancy = context is { TokenBudget: > 0 }
            ? Math.Round(Math.Clamp((double)context.EstimatedTokens / context.TokenBudget * 100.0, 0.0, 100.0), 2)
            : 0.0;
        var fallbackDroppedSources = context?.Dropped
            .Select(d => d.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var fallbackDroppedTokens = context?.Dropped.Sum(d => Math.Max(0, d.EstimatedTokens)) ?? 0;

        var mergedSignals = new Dictionary<string, int>(fallbackSignals, StringComparer.OrdinalIgnoreCase);
        if (diagnostics is not null)
        {
            foreach (var (sigKey, sigCount) in diagnostics.Governance.SignalCounts)
            {
                if (!mergedSignals.TryGetValue(sigKey, out var cur) || sigCount > cur)
                {
                    mergedSignals[sigKey] = sigCount;
                }
            }
        }

        var mergedDropped = diagnostics is not null && diagnostics.Compaction.CriticalDroppedSources.Count > 0
            ? diagnostics.Compaction.CriticalDroppedSources
                .Concat(fallbackDroppedSources)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : fallbackDroppedSources;

        return new RunContextEvolutionObservation
        {
            RunId = run.RunId,
            SessionId = checkpoint.SessionId,
            ProjectId = configuration.Workspace?.ProjectId is { } pid && pid != Guid.Empty ? pid : null,
            ModeId = modeId,
            ModeSlug = configuration.ModeSlug,
            ModeName = configuration.ModeDisplayName ?? configuration.ModeSlug,
            PeakOccupancyPct = diagnostics is not null && diagnostics.WindowBuckets.PeakOccupancyPct > 0
                ? diagnostics.WindowBuckets.PeakOccupancyPct
                : fallbackPeakOccupancy,
            DegradationDetected = diagnostics?.WindowBuckets.DegradationDetected ?? false,
            DegradationReasons = diagnostics?.WindowBuckets.DegradationReasonCodes ?? [],
            HighOccupancyErrorRate = diagnostics?.WindowBuckets.HighOccupancyErrorRate ?? 0.0,
            LowOccupancyErrorRate = diagnostics?.WindowBuckets.LowOccupancyErrorRate ?? 0.0,
            HighOccupancyAvgLatencyMs = diagnostics?.WindowBuckets.HighOccupancyAvgLatencyMs ?? 0.0,
            LowOccupancyAvgLatencyMs = diagnostics?.WindowBuckets.LowOccupancyAvgLatencyMs ?? 0.0,
            DroppedSources = mergedDropped,
            DroppedTokens = diagnostics is not null && diagnostics.Compaction.TotalDroppedTokensAcrossPacks > 0
                ? diagnostics.Compaction.TotalDroppedTokensAcrossPacks
                : fallbackDroppedTokens,
            RedlineCounts = mergedSignals,
            MaxSameArgsRetryStreak = Math.Max(diagnostics?.Governance.MaxSameArgsRetryStreak ?? 0, fallbackRetryStreak),
            EffectiveStrategy = effectiveStrategy,
            EffectiveCompactionTriggerRatio = ResolveEffectiveCompactionRatio(configuration),
            EffectiveRecentMessageLimit = ResolveEffectiveRecentMessageLimit(configuration),
            EffectiveRecallFidelity = ResolveEffectiveRecallPolicy(configuration),
            EffectiveAllowCrossSessionRecall = ResolveEffectiveAllowCrossSessionRecall(configuration),
            EffectiveEvolutionMode = effectiveEvolutionMode,
            EffectiveCoreMarkdownMaxTokens = ResolveEffectiveCoreMarkdownMaxTokens(configuration),
            OverlapPairs = overlapPairs
        };
    }

    internal static (Dictionary<string, int> SignalCounts, int MaxSameArgsStreak) ExtractCheckpointTrajectorySignals(
        FullDuplexCheckpointV1? checkpoint)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var maxStreak = 0;
        if (checkpoint is null) return (counts, 0);

        void Increment(string code) => counts[code] = counts.TryGetValue(code, out var c) ? c + 1 : 1;

        foreach (var task in checkpoint.Tasks)
        {
            string? prevFingerprint = null;
            var streak = 0;
            foreach (var turn in task.ToolTurns)
            {
                var fp = $"{turn.ToolId.Trim().ToLowerInvariant()}|{NormalizeJsonArgsKey(turn.ArgumentsJson)}";
                if (string.Equals(fp, prevFingerprint, StringComparison.Ordinal))
                {
                    streak++;
                    if (streak >= 2)
                    {
                        Increment(UnifiedGovernanceSignalCodes.SameArgsRetry);
                        if (streak > maxStreak) maxStreak = streak;
                    }
                }
                else
                {
                    prevFingerprint = fp;
                    streak = 1;
                }

                var err = $"{turn.ErrorCategory} {turn.DispatchStatus}".ToLowerInvariant();
                if (err.Contains("rejected", StringComparison.Ordinal) || err.Contains("approval_denied", StringComparison.Ordinal))
                    Increment(UnifiedGovernanceSignalCodes.ApprovalRejected);
                if (err.Contains("not_observed", StringComparison.Ordinal) || err.Contains("read_before_write", StringComparison.Ordinal))
                    Increment(UnifiedGovernanceSignalCodes.FsNotObserved);
                if (err.Contains("stale", StringComparison.Ordinal))
                    Increment(UnifiedGovernanceSignalCodes.FsStaleVersion);
                if (err.Contains("worktree", StringComparison.Ordinal))
                    Increment(UnifiedGovernanceSignalCodes.WorktreeGuard);
                if (err.Contains("write_scope", StringComparison.Ordinal) || err.Contains("scope_locked", StringComparison.Ordinal))
                    Increment(UnifiedGovernanceSignalCodes.WriteScopeLocked);
                if (err.Contains("timeout", StringComparison.Ordinal) || err.Contains("timed_out", StringComparison.Ordinal))
                    Increment(UnifiedGovernanceSignalCodes.Timeout);
                if (err.Contains("loop", StringComparison.Ordinal))
                    Increment(UnifiedGovernanceSignalCodes.LoopDetected);
            }
        }

        return (counts, maxStreak);
    }

    private static string NormalizeJsonArgsKey(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return "{}";
        return string.Concat(rawJson.Where(ch => !char.IsWhiteSpace(ch)));
    }

    private static Guid? TryParseModeIdFromRuntimeProfile(string? runtimeProfileId)
    {
        if (string.IsNullOrWhiteSpace(runtimeProfileId)) return null;
        var parts = runtimeProfileId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && string.Equals(parts[0], "mode", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[1], out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }
        return null;
    }

    private static string? ResolveCuratedApplicability(string kind, string? applicability, FrozenRunConfigurationV1 configuration)
    {
        if (!string.IsNullOrWhiteSpace(applicability)) return applicability;
        if (!string.Equals(kind, "failure_pattern", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, "supervision_rule", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return !string.IsNullOrWhiteSpace(configuration.ModeSlug)
            ? $"mode:{configuration.ModeSlug}"
            : "workspace";
    }

    private static string? ResolveCuratedExpiryCondition(string kind, string? expiryCondition)
    {
        if (!string.IsNullOrWhiteSpace(expiryCondition)) return expiryCondition;
        if (!string.Equals(kind, "failure_pattern", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, "supervision_rule", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return "until underlying tool contract or workspace workflow changes";
    }

    private static string FormatFallbackCuratorEvidence(RunContextEvolutionObservation observation, int maxChars)
    {
        var dropped = observation.DroppedSources.Count > 0 ? string.Join(", ", observation.DroppedSources.Take(6)) : "none";
        var redlines = observation.RedlineCounts.Count > 0
            ? string.Join(", ", observation.RedlineCounts.Select(kv => $"{kv.Key}={kv.Value}"))
            : "none";
        var overlaps = observation.OverlapPairs.Count > 0
            ? string.Join("; ", observation.OverlapPairs.Take(3).Select(p => $"{p.SourceALabel}<->{p.SourceBLabel} [L{p.SourceAStartLine}-L{p.SourceAEndLine} <-> L{p.SourceBStartLine}-L{p.SourceBEndLine}] ~{p.EstimatedWastedTokens}t ({p.RemediationCode})"))
            : "none";
        var raw = $"- Peak occupancy: {observation.PeakOccupancyPct:0.0}% | Degradation: {(observation.DegradationDetected ? "YES" : "no")}\n"
            + $"- Dropped sources ({observation.DroppedTokens}t): {dropped}\n"
            + $"- Redline signals (max retry streak={observation.MaxSameArgsRetryStreak}): {redlines}\n"
            + $"- Top overlaps: {overlaps}";
        return raw.Length <= maxChars ? raw : raw[..maxChars];
    }

    private static string MapAutoEvolveMemoryScope(string rawScope, FrozenWorkspaceBinding? workspace)
    {
        var normalized = rawScope.Trim().ToLowerInvariant();
        return normalized switch
        {
            "user" or "global" or "principal" => "principal",
            "agent" => "agent",
            "project" => "project",
            "workspace" => workspace is not null && (workspace.ProjectId != Guid.Empty || !string.IsNullOrWhiteSpace(workspace.RootPath))
                ? "project"
                : "workspace",
            _ => workspace is not null ? "project" : "workspace"
        };
    }

    private async Task<RuntimeAgentInstance> EnsureEvolutionAgentAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        RuntimeAgentDefinition definition,
        CancellationToken cancellationToken)
    {
        var runId = Guid.Parse(run.RunId);
        var all = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var curator = all.FirstOrDefault(item => !item.Generated && item.AgentVersionId == definition.AgentVersionId);
        if (curator is not null)
        {
            VerifyRootInstance(curator, definition, null, "experience curator");
            return curator;
        }

        curator = await _instances.CreateRootAsync(new RuntimeAgentSeed(
            Guid.Parse(run.SessionId),
            runId,
            definition.Id,
            definition.Layer,
            definition.Role,
            "chat",
            definition.Capabilities,
            definition.AllowedTools,
            ResourceSeed(definition.ResourceGrants),
            configuration.Context.DefaultTokenBudget,
            AgentDefinitionId: definition.AgentDefinitionId,
            AgentVersionId: definition.AgentVersionId,
            VersionContentHash: definition.VersionContentHash,
            AllowedDispatchTargets: definition.AllowedDispatchTargets), cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "agent.created", "Experience curator created.", new
        {
            agent_instance_id = curator.Id,
            agent_slug = definition.Id,
            layer = curator.Layer,
            role = curator.Role
        }, cancellationToken).ConfigureAwait(false);
        return curator;
    }

    private async Task DispatchGitStewardAsync(
        OperationalTriggerMatch match,
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var touchedGit = checkpoint.Tasks.Any(task =>
            task.RequiredTools.Any(tool => tool.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
            || task.ToolTurns.Any(turn => turn.ToolId.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
            || string.Equals(task.WorkerAgentSlug, "worker.git", StringComparison.Ordinal));
        if (!touchedGit) return;

        var runId = Guid.Parse(run.RunId);
        var context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), match.Agent, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var factory = CreateModelFactory(configuration, checkpoint, match.Agent, null, null, AnswerStreaming.None, observeOutput: false, interruptible: false);
        var resolution = await factory.ResolveChatAsync("chat", cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Error ?? "Chat route is unavailable.");
        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), match.Agent, context, cancellationToken).ConfigureAwait(false);
        var gitTasks = checkpoint.Tasks
            .Where(task => task.RequiredTools.Any(tool => tool.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
                || task.ToolTurns.Any(turn => turn.ToolId.StartsWith("git_", StringComparison.OrdinalIgnoreCase)))
            .Select(task => $"- [{task.ResultStatus ?? task.Status}] {task.TaskKey}: tools=[{string.Join(", ", task.RequiredTools.Concat(task.ToolTurns.Select(turn => turn.ToolId)).Distinct(StringComparer.OrdinalIgnoreCase))}]")
            .ToList();
        var instructions = assembly.Instructions
            + "\n\nYou are the git steward. Review the change scope and commit boundaries this run touched. Respond with a short suggestion covering: change scope, suggested commit boundary, and review risks. Advise only; never execute git operations or bypass approvals.";
        var prompt = $"Current user goal:\n{checkpoint.UserGoal}\n\nGit-touching tasks:\n{string.Join("\n", gitTasks)}";
        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            await factory.CreateAsync(resolution, cancellationToken).ConfigureAwait(false),
            $"operation.{match.Agent.Id}",
            match.Agent.Id,
            "Reviews git change scope and commit boundaries without executing git.",
            new ChatOptions { Instructions = instructions },
            prompt,
            cancellationToken).ConfigureAwait(false);
        checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, turn.Usage);
        var suggestion = turn.AnswerOr("No git steward suggestion produced.");
        await AppendEventAsync(runId, "git.steward.reviewed", "Git steward reviewed the run's change scope.", new
        {
            run_id = run.RunId,
            agent_slug = match.Agent.Id,
            trigger_point = match.Point.ToString(),
            suggestion
        }, cancellationToken).ConfigureAwait(false);
    }

    private static (List<(string Scope, string Kind, string Content, double Confidence, string? Applicability, string? ExpiryCondition)> MemoryCandidates,
        List<(string Name, string Layer, string AgentType, double Confidence, JsonElement Proposal)> AgentCandidates) ParseCurationOutput(string? text)
    {
        var memoryCandidates = new List<(string, string, string, double, string?, string?)>();
        var agentCandidates = new List<(string, string, string, double, JsonElement)>();
        foreach (var candidate in ModelOutputText.ExtractJsonCandidates(text, array: false))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(candidate);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (root.TryGetProperty("memory_candidates", out var memoryNode) && memoryNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in memoryNode.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var scope = StringProperty(item, "scope");
                        var kind = StringProperty(item, "kind");
                        var content = StringProperty(item, "content");
                        if (scope is null || kind is null || content is null) continue;
                        memoryCandidates.Add((scope, kind, content, ConfidenceProperty(item), StringProperty(item, "applicability"), StringProperty(item, "expiry_condition")));
                    }
                }

                if (root.TryGetProperty("agent_candidates", out var agentNode) && agentNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in agentNode.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var name = StringProperty(item, "name");
                        var layer = StringProperty(item, "layer");
                        var agentType = StringProperty(item, "agent_type");
                        if (name is null || layer is null || agentType is null) continue;
                        if (!item.TryGetProperty("proposal", out var proposalNode) || proposalNode.ValueKind != JsonValueKind.Object) continue;
                        agentCandidates.Add((name, layer, agentType, ConfidenceProperty(item), proposalNode.Clone()));
                    }
                }
            }

            if (memoryCandidates.Count > 0 || agentCandidates.Count > 0) break;
        }
        return (memoryCandidates, agentCandidates);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;

    private static double ConfidenceProperty(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("confidence", out var property)
        && property.TryGetDouble(out var value)
            ? Math.Clamp(value, 0, 1)
            : 0.5;

    // Task #37: Companion Work Session Backflow Hook (zero polling, zero module coupling)

    private async Task NotifyCompanionBackflowAsync(
        RunState run,
        FrozenRunConfigurationV1? configuration,
        FullDuplexCheckpointV1 checkpoint,
        string status,
        string? summary,
        CancellationToken cancellationToken,
        WorkerToolTurn? approvalTurn = null)
    {
        if (_companionBackflow is null) return;
        try
        {
            var tenantId = Guid.TryParse(run.TenantId, out var tid) ? tid : Guid.Empty;
            Guid? workspaceId = Guid.TryParse(run.WorkspaceId, out var wid) ? wid : null;
            Guid? projectId = configuration?.Workspace?.ProjectId;
            if (projectId is null && _services.GetService(typeof(ISessionLocator)) is ISessionLocator sessionLocator)
            {
                var sessionRef = await sessionLocator.FindAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
                projectId = sessionRef?.ProjectId;
                if (tenantId == Guid.Empty && sessionRef is not null) tenantId = sessionRef.TenantId;
                workspaceId ??= sessionRef?.WorkspaceId;
            }

            var sessionPlan = await TryGetSessionPlanAsync(checkpoint.SessionId, cancellationToken).ConfigureAwait(false);
            var sessionTitle = !string.IsNullOrWhiteSpace(sessionPlan?.Title) && sessionPlan.Title != "Session Plan"
                ? sessionPlan.Title
                : FirstSentenceOrBound(checkpoint.UserGoal, 100);
            var planSummary = BuildCompanionPlanSummary(checkpoint, sessionPlan);

            List<CompanionPendingApprovalItem>? approvals = null;
            if (string.Equals(status, "awaiting_approval", StringComparison.OrdinalIgnoreCase))
            {
                approvals = [];
                foreach (var node in checkpoint.Tasks)
                {
                    foreach (var turn in node.ToolTurns)
                    {
                        if (!string.IsNullOrWhiteSpace(turn.ResultJson)) continue;
                        var approvalIdStr = turn.ApprovalId ?? node.PendingToolApprovalId;
                        if (!Guid.TryParse(approvalIdStr, out var approvalGuid)) continue;
                        var risk = configuration?.ToolManifest
                            .FirstOrDefault(m => string.Equals(m.Id, turn.ToolId, StringComparison.OrdinalIgnoreCase))?.Risk ?? "medium";
                        var itemSummary = turn == approvalTurn && !string.IsNullOrWhiteSpace(summary)
                            ? summary
                            : $"Tool '{turn.ToolId}' requested by {node.TeammateHandle ?? node.WorkerHandle ?? node.TaskKey}";
                        if (approvals.All(a => a.ApprovalId != approvalGuid))
                        {
                            approvals.Add(new CompanionPendingApprovalItem(
                                approvalGuid,
                                turn.ToolId,
                                BoundText(itemSummary, 240),
                                risk,
                                DateTimeOffset.UtcNow));
                        }
                    }
                }
            }

            await _companionBackflow.NotifySessionTransitionAsync(new CompanionSessionBackflowEvent(
                tenantId,
                workspaceId,
                projectId,
                checkpoint.SessionId,
                sessionTitle,
                checkpoint.RunId,
                status,
                configuration?.ModeDisplayName ?? configuration?.ModeSlug,
                summary is null ? null : BoundText(summary, 500),
                planSummary,
                approvals), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.TryLogDebug(ex, "Companion session backflow notification skipped for run {RunId} ({Status}).", checkpoint.RunId, status);
        }
    }

    private static string? BuildCompanionPlanSummary(FullDuplexCheckpointV1 checkpoint, SessionPlanDto? sessionPlan)
    {
        if (sessionPlan is not null)
        {
            var allSteps = sessionPlan.Phases.SelectMany(p => p.Steps).ToArray();
            if (allSteps.Length > 0)
            {
                var done = allSteps.Count(s => s.Status is SessionPlanStatuses.StepCompleted or SessionPlanStatuses.StepSkipped);
                var current = allSteps.FirstOrDefault(s => s.Status == SessionPlanStatuses.StepInProgress)
                    ?? allSteps.FirstOrDefault(s => s.Status == SessionPlanStatuses.StepPending);
                return current is null
                    ? $"{done}/{allSteps.Length} steps completed"
                    : $"{done}/{allSteps.Length} steps · {BoundText(current.Title, 100)}";
            }
        }

        var coordinatorSteps = checkpoint.Tasks.FirstOrDefault(t => t.Plan is { Count: > 0 })?.Plan;
        if (coordinatorSteps is { Count: > 0 })
        {
            var done = coordinatorSteps.Count(s => s.Status == "completed");
            var current = coordinatorSteps.FirstOrDefault(s => s.Status == "in_progress")
                ?? coordinatorSteps.FirstOrDefault(s => s.Status == "pending");
            return current is null
                ? $"{done}/{coordinatorSteps.Count} steps completed"
                : $"{done}/{coordinatorSteps.Count} steps · {BoundText(current.Step, 100)}";
        }

        if (checkpoint.Tasks.Count > 0)
        {
            var done = checkpoint.Tasks.Count(t => t.Status == "completed");
            return $"{done}/{checkpoint.Tasks.Count} tasks completed";
        }
        return null;
    }
}
