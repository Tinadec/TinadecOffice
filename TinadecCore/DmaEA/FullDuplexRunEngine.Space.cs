using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

internal sealed partial class FullDuplexRunEngine
{
    private const string SpaceWorktreeCallId = "core-space-worktree";
    private static readonly string[] SpecStages = ["requirements", "design", "tasks"];

    internal static string WithSpaceContext(FullDuplexCheckpointV1 checkpoint, string taskContext) => taskContext
        + (checkpoint.SpaceWorktreePath is { } root ? $"\nActual execution root for this run: {root}. All tool paths must use this worktree, not the original checkout." : "")
        + (checkpoint.Correction is { } correction ? "\n" + correction.PromptContext() : "")
        + (checkpoint.SpaceSpecDocuments.Count == 0 ? "" : "\nConfirmed specifications (authoritative, do not ask for their confirmation again):\n"
            + string.Join("\n\n", checkpoint.SpaceSpecDocuments.Select(document => $"[{document.Stage}, revision {document.Revision}, hash {document.ContentHash}]\n{document.Content}"))
            + (checkpoint.SpaceSpecDocuments.Count == SpecStages.Length
                ? "\nAll three stages are confirmed. Allocate the approved tasks to the selected workflow without inventing a competing plan or extending their scope." : ""));

    private static FrozenRunConfigurationV1 SpacePromptConfiguration(FrozenRunConfigurationV1 configuration, FullDuplexCheckpointV1 checkpoint) =>
        configuration.SpaceOptions is { Worktree: true } && checkpoint.SpaceWorktreePath is { } root && configuration.Workspace is { } workspace
            ? configuration with { Workspace = workspace with { RootPath = root } } : configuration;

    internal static bool QueueSpaceWorktree(FrozenRunConfigurationV1 configuration, FullDuplexCheckpointV1 checkpoint, DurableTaskNode task)
    {
        if (configuration.SpaceOptions is not { Worktree: true } || checkpoint.SpaceWorktreePath is not null
            || task.ToolTurns.Any(turn => turn.IsSpaceWorktreeProvisioning)) return false;
        var root = configuration.Workspace?.RootPath ?? throw new InvalidDataException("Spatial worktree has no frozen project root.");
        var branch = $"tinadec/run-{checkpoint.RunId:N}";
        var path = Path.Combine(root, ".tinadec", "worktrees", checkpoint.RunId.ToString("N"));
        task.ToolTurns.Insert(0, new WorkerToolTurn
        {
            CallId = SpaceWorktreeCallId, ToolId = "git_worktree_create", IsSpaceWorktreeProvisioning = true,
            ArgumentsJson = JsonSerializer.Serialize(new
            {
                repository_path = root, path, branch,
                confirm_worktree_create = $"Create {path} on {branch} for this task's requested isolation."
            }),
        });
        return true;
    }

    internal static bool SpacePreparationReady(FrozenRunConfigurationV1 configuration, FullDuplexCheckpointV1 checkpoint) =>
        configuration.SpaceOptions is { } options
        && (!options.Worktree || checkpoint.SpaceWorktreePath is not null)
        && (!options.SpecEnabled || checkpoint.SpaceSpecDocuments.Count == SpecStages.Length);

    internal static string? SpaceToolRefusal(FrozenRunConfigurationV1 configuration, FullDuplexCheckpointV1 checkpoint,
        DurableTaskNode task, WorkerToolTurn turn)
    {
        var options = configuration.SpaceOptions;
        if (options is null) return null;
        if (!options.MultiAgent && SpaceCompositionPolicy.IsDispatch(turn.ToolId))
            return "New executor dispatch is disabled for this run. Complete the task yourself.";
        if (turn.IsSpaceWorktreeProvisioning && turn.ToolId == "git_worktree_create" && task.IsSpacePreparation && options.Worktree) return null;
        if (SpaceCompositionPolicy.IsWorktreeManagement(turn.ToolId))
            return options.Worktree ? "Core owns the single worktree for this run. Keep its changes for the user; do not create another worktree or remove the assigned one."
                : "Independent worktree isolation is disabled for this run.";
        if (options.Worktree && checkpoint.SpaceWorktreePath is null)
            return "The requested worktree has not been established. No task tool may fall back to the original workspace.";
        if (CoreVirtualToolPolicy.IsSpecPropose(turn.ToolId))
        {
            if (!options.SpecEnabled) return "Spec review is not enabled.";
            if (!TryParseJsonObject(turn.ArgumentsJson, out var args)
                || !args.TryGetProperty("stage", out var stageValue) || stageValue.ValueKind != JsonValueKind.String
                || !args.TryGetProperty("document", out var docValue) || docValue.ValueKind != JsonValueKind.String)
                return "spec_propose requires stage and the complete document.";
            var stage = stageValue.GetString();
            var document = docValue.GetString() ?? "";
            if (ApprovalEvidenceProjector.SpecificationArguments(stage ?? "", document) is null)
                return "Shorten the document to at most 3000 characters and 3584 serialized review characters so the person can read its entire content before confirming it.";
            var prior = checkpoint.SpaceSpecDocuments.FirstOrDefault(item => item.Stage == stage);
            if (prior is not null && prior.Content == document)
                return turn.ExecutionId == prior.ExecutionId ? null
                    : "This document is already confirmed. Continue with the next stage instead of asking for the same confirmation again.";
            if (checkpoint.SpaceSpecDocuments.Count >= SpecStages.Length || stage != SpecStages[checkpoint.SpaceSpecDocuments.Count])
                return "Propose requirements, design, and tasks in order. Earlier confirmed documents are immutable for this run.";
            if (stage == "tasks" && (SpecPlan(document).Count is 0 or > CoreVirtualToolPolicy.PlanUpdateMaxSteps))
                return "The tasks document needs a Markdown checklist with 1–20 steps (- [ ] ...), so the complete confirmed specification can be the visible execution plan.";
            return null;
        }
        if (options.SpecEnabled && CoreVirtualToolPolicy.IsPlanUpdate(turn.ToolId))
        {
            if (checkpoint.SpaceSpecDocuments.Count < SpecStages.Length)
                return "Use spec_propose for the specification stages. The confirmed tasks document becomes the plan; do not create a second plan.";
            var error = TryParsePlan(turn.ArgumentsJson, out var steps, out _);
            if (error is not null) return error;
            var original = SpecPlan(checkpoint.SpaceSpecDocuments.Single(document => document.Stage == "tasks").Content);
            if (!steps.Select(step => step.Step).SequenceEqual(original.Select(step => step.Step)))
                return "Update the statuses of the confirmed task steps. Changing the specification requires a new user-reviewed task.";
        }
        var mutating = configuration.ToolManifest.FirstOrDefault(item => item.Id.Equals(turn.ToolId, StringComparison.OrdinalIgnoreCase))?.MutatesWorkspace == true;
        var dispatch = CoreVirtualToolPolicy.IsTaskDispatch(turn.ToolId) || turn.ToolId == "tina_chat_execute_intent";
        if (options.SpecEnabled && checkpoint.SpaceSpecDocuments.Count < SpecStages.Length && (mutating || dispatch))
            return "Implementation and delegation wait for the requirements, design, and tasks documents to be confirmed with spec_propose. You can investigate with read-only tools first.";
        if (options.PlanFirst && !options.SpecEnabled && options.WorkflowModeVersionId is null
            && (mutating || dispatch) && task.Plan is not { Count: > 0 })
            return "Plan-first is enabled. Call plan_update with the concrete steps before changing the workspace or delegating, then continue execution. No extra human confirmation is required.";
        return null;
    }

    private async Task<FullDuplexCheckpointV1> RecordSpaceToolResultAsync(RunState run, FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint, DurableTaskNode task, WorkerToolTurn turn, CancellationToken cancellationToken)
    {
        if (configuration.SpaceOptions is null) return checkpoint;
        if (turn.IsSpaceWorktreeProvisioning && turn.ToolId == "git_worktree_create")
        {
            if (!TryParseJsonObject(turn.ResultJson ?? "null", out var result) || !result.TryGetProperty("path", out var path)
                || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
                throw new InvalidDataException("Worktree creation returned no execution root; the original workspace will not be used.");
            var targetResolver = _services.GetRequiredService<IToolExecutionTargetResolver>();
            var target = await targetResolver.ResolveAsync(checkpoint.SessionId, checkpoint.RunId, task.TaskId,
                "read_file", configuration.Workspace!.RootPath, cancellationToken).ConfigureAwait(false);
            if (target.IsRejected || target.Target is not { Kind: "worktree" })
                throw new InvalidDataException(target.Error ?? "Worktree creation did not establish its run assignment.");
            checkpoint.SpaceWorktreePath = target.Target.RootPath;
            await AppendEventAsync(checkpoint.RunId, "space.worktree.ready", "The task's independent worktree is ready.",
                new { path = checkpoint.SpaceWorktreePath, run_id = checkpoint.RunId }, cancellationToken,
                idempotencyKey: $"space-worktree:{checkpoint.RunId:N}").ConfigureAwait(false);
        }
        if (CoreVirtualToolPolicy.IsSpecPropose(turn.ToolId) && TryParseJsonObject(turn.ArgumentsJson, out var args))
        {
            var stage = args.GetProperty("stage").GetString()!;
            var content = args.GetProperty("document").GetString()!;
            if (checkpoint.SpaceSpecDocuments.All(item => item.Stage != stage))
            {
                var document = new SpaceSpecDocument(stage, content,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                    checkpoint.SpaceSpecDocuments.Count + 1, turn.ExecutionId!);
                checkpoint.SpaceSpecDocuments.Add(document);
                if (stage == "tasks") task.Plan = SpecPlan(content);
                await AppendEventAsync(checkpoint.RunId, "spec.confirmed", $"The user confirmed the {stage} specification.",
                    new { stage, document.Content, document.ContentHash, document.Revision, execution_id = turn.ExecutionId },
                    cancellationToken, task.TaskId, idempotencyKey: $"spec:{turn.ExecutionId}").ConfigureAwait(false);
                if (stage == "tasks")
                    await AppendEventAsync(checkpoint.RunId, "plan.updated", "The confirmed tasks specification is the execution plan.",
                        new { run_id = checkpoint.RunId, task_id = task.TaskId, task_key = task.TaskKey, source = "spec", steps = task.Plan },
                        cancellationToken, task.TaskId, idempotencyKey: $"spec-plan:{turn.ExecutionId}").ConfigureAwait(false);
            }
        }
        return checkpoint;
    }

    private static List<TaskPlanStep> SpecPlan(string document) => document.Split('\n')
        .Select(line => line.Trim()).Where(line => line.StartsWith("- [ ] ", StringComparison.Ordinal)
            || line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase))
        .Select(line => new TaskPlanStep(BoundText(line[6..].Trim(), 300), "pending"))
        .Where(step => step.Step.Length > 0).ToList();
}

internal sealed record SpaceSpecDocument(string Stage, string Content, string ContentHash, int Revision, string ExecutionId);
