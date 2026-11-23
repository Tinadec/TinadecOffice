using System.Text.Json;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.DmaEA;

internal sealed partial class FullDuplexRunEngine
{
    private async Task<FullDuplexCheckpointV1> PlanAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        await TrySetRunStatusAsync(run.RunId,
            checkpoint.PlanRevision == 0 ? "understanding" : "replanning",
            null, cancellationToken).ConfigureAwait(false);

        var graph = configuration.Graph
            ?? throw new InvalidDataException("Frozen run configuration carries no declared graph (schema v2 freezes one for every mode).");
        var author = await EnsureConversationAuthorAsync(run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        checkpoint.MeetingAgentId = author.Id;
        checkpoint.PlannerAgentId = author.Id;
        await JoinConversationPoolAsync(run, checkpoint, author.Id, cancellationToken).ConfigureAwait(false);
        var plannerDefinition = RequiredConversationAgent(configuration);
        await EnrolConversationAsync(run, configuration, author.Id, cancellationToken).ConfigureAwait(false);
        if (!checkpoint.GraphTierAnnounced)
        {
            var graphRunId = Guid.Parse(run.RunId);
            await AppendEventAsync(graphRunId, "orchestration.mode_tier_decided",
                "Declared-graph tier decided at admission.", new
                {
                    run_id = run.RunId,
                    tier = graph.Tier,
                    conversation_slug = graph.ConversationTemplateSlug,
                    edge_count = graph.Edges.Count
                }, cancellationToken, idempotencyKey: $"run:{run.RunId}:tier").ConfigureAwait(false);
            checkpoint.GraphTierAnnounced = true;
        }

        var prepareSpace = configuration.SpaceOptions is { } spaceOptions
            && (spaceOptions.SpecEnabled || spaceOptions.Worktree) && !checkpoint.SpacePreparationComplete;
        if ((string.Equals(graph.Tier, FrozenGraphTiers.SoloDispatch, StringComparison.Ordinal) || prepareSpace)
            && checkpoint.PlanRevision == 0)
        {
            checkpoint.Tasks = BuildSoloMasterTask(configuration, checkpoint,
                isWorkflowPreparation: prepareSpace && IsNonSoloSpaceWorkflow(configuration));
            checkpoint.Tasks[0].IsSpacePreparation = prepareSpace;
            SyncLanes(checkpoint);
            checkpoint.PlanRevision++;
            checkpoint.Phase = "executing";
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "solo-plan", cancellationToken).ConfigureAwait(false);
            await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
            var soloRunId = Guid.Parse(run.RunId);
            var soloTask = checkpoint.Tasks[0];
            await AppendEventAsync(soloRunId, "task_graph.created",
                "1 solo task planned: the conversation identity executes the goal itself.", new
                {
                    run_id = run.RunId,
                    plan_revision = checkpoint.PlanRevision,
                    task_count = checkpoint.Tasks.Count,
                    task_keys = checkpoint.Tasks.Select(item => item.TaskKey).ToArray(),
                    tier = graph.Tier
                }, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(soloRunId, "worker.assigned",
                $"Solo master '{plannerDefinition.Id}' takes its own task.", new
                {
                    task_id = soloTask.TaskId,
                    task_key = soloTask.TaskKey,
                    agent_slug = plannerDefinition.Id,
                    agent_definition_id = plannerDefinition.AgentDefinitionId,
                    agent_version_id = plannerDefinition.AgentVersionId,
                    agent_version_hash = plannerDefinition.VersionContentHash,
                    reason = soloTask.WorkerAssignmentReason,
                    required_capabilities = soloTask.RequiredCapabilities,
                    required_tools = soloTask.RequiredTools
                }, cancellationToken, soloTask.TaskId).ConfigureAwait(false);
            return checkpoint;
        }

        // Explore-First Coordinator (#33): when running FreeForm and the coordinator holds
        // team_spawn (or explore/read tools), run the coordinator directly in its governed
        // tool loop so it explores read-only first, updates SessionPlan via team_plan, and
        // dynamically orchestrates via team_spawn / team_message / team_wait / team_integrate
        // — NEVER forcing upfront blind JSON DAG guessing! Legacy FreeForm configs with zero
        // team_spawn / explore tools on meeting retain the PlanningAgent fallback below.
        if (ShouldUseExploreFirstCoordinator(configuration, plannerDefinition) && checkpoint.PlanRevision == 0)
        {
            checkpoint.IsExploreFirstCoordinator = true;
            checkpoint.Tasks = BuildExploreFirstCoordinatorTask(configuration, checkpoint);
            SyncLanes(checkpoint);
            checkpoint.PlanRevision++;
            checkpoint.Phase = "executing";
            checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "explore-first-plan", cancellationToken).ConfigureAwait(false);
            await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
            var teamRunId = Guid.Parse(run.RunId);
            var coordTask = checkpoint.Tasks[0];
            await AppendEventAsync(teamRunId, "task_graph.created",
                "Explore-first Team coordinator started: read-only investigation and dynamic team_* orchestration enabled.", new
                {
                    run_id = run.RunId,
                    plan_revision = checkpoint.PlanRevision,
                    task_count = checkpoint.Tasks.Count,
                    task_keys = checkpoint.Tasks.Select(item => item.TaskKey).ToArray(),
                    tier = graph.Tier,
                    explore_first = true
                }, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(teamRunId, "worker.assigned",
                $"Explore-first coordinator '{plannerDefinition.Id}' started.", new
                {
                    task_id = coordTask.TaskId,
                    task_key = coordTask.TaskKey,
                    handle = coordTask.WorkerHandle,
                    agent_slug = plannerDefinition.Id,
                    agent_definition_id = plannerDefinition.AgentDefinitionId,
                    agent_version_id = plannerDefinition.AgentVersionId,
                    agent_version_hash = plannerDefinition.VersionContentHash,
                    reason = coordTask.WorkerAssignmentReason,
                    required_capabilities = coordTask.RequiredCapabilities,
                    required_tools = coordTask.RequiredTools
                }, cancellationToken, coordTask.TaskId).ConfigureAwait(false);
            return checkpoint;
        }

        var context = await BuildContextAsync(run, SpacePromptConfiguration(configuration, checkpoint), plannerDefinition, WithSpaceContext(checkpoint, checkpoint.UserGoal), checkpoint.UserGoal, cancellationToken).ConfigureAwait(false);
        var assembly = await AssemblePromptAsync(SpacePromptConfiguration(configuration, checkpoint), plannerDefinition, context, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "context.packed", "Planner context assembled.", new
        {
            evidence_count = context.Evidence.Count,
            estimated_tokens = context.EstimatedTokens,
            token_budget = context.TokenBudget,
            sources = context.Evidence.Select(item => item.Source).ToArray(),
            source_tokens = BudgetShares(context.Evidence),
            dropped_sources = BudgetShares(context.Dropped),
            context_revision = checkpoint.ContextRevision
        }, cancellationToken).ConfigureAwait(false);

        PlannedTask[] planned = [];
        Exception? lastError = null;
        var plannerHead = string.Empty;
        var plannerParseError = string.Empty;
        var existingPlan = await TryGetSessionPlanAsync(Guid.Parse(run.SessionId), cancellationToken).ConfigureAwait(false);
        var existingPlanInstructions = PlanningAgent.BuildExistingPlanInstructions(existingPlan);
        var basePlannerInstructions = string.IsNullOrWhiteSpace(existingPlanInstructions)
            ? PlannerInstructions(checkpoint, assembly.Instructions)
            : $"{PlannerInstructions(checkpoint, assembly.Instructions)}\n\n{existingPlanInstructions}";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var contextForPlanner = CreateRunContext(run, checkpoint);
                var planner = new PlanningAgent(CreateModelFactory(configuration, checkpoint, plannerDefinition,
                    checkpoint.PlannerAgentId, null, AnswerStreaming.None), _logger);
                var instructionsForAttempt = attempt == 0
                    ? basePlannerInstructions
                    : basePlannerInstructions + BuildPlannerRetryHint(lastError, plannerParseError);
                planned = await planner.PlanAsync(
                    contextForPlanner,
                    BuildFrozenPlannerRoster(configuration),
                    instructionsForAttempt,
                    configuration,
                    cancellationToken).ConfigureAwait(false);
                plannerHead = planner.LastResponseHead;
                plannerParseError = planner.LastParseErrorDetail;
                RefuseSilentPlanningFallbackOnDeclaredEdges(configuration, planned);
                checkpoint.ModelUsage = Maf18RuntimeAdapter.AddUsage(checkpoint.ModelUsage, planner.LastUsage);
                var noWorkToPlan = planner.LastPlanWasParsed && planned.Length == 0 && checkpoint.PlanRevision == 0;
                var materialized = noWorkToPlan
                    ? new List<DurableTaskNode>()
                    : ValidateAndMaterializeGraph(planned, configuration.Spawn.MaxAgentsPerRun, PlannerDispatchRosterOf(configuration));
                checkpoint.Tasks = checkpoint.PlanRevision == 0
                    ? materialized
                    : MergeReplannedGraph(checkpoint.Tasks, materialized);
                SyncLanes(checkpoint);
                if (noWorkToPlan)
                {
                    await AppendEventAsync(Guid.Parse(run.RunId), "task_graph.empty",
                        "The planner produced an empty plan; this goal needs no execution work.",
                        new { run_id = run.RunId, user_goal = checkpoint.UserGoal }, cancellationToken).ConfigureAwait(false);
                }
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
            await AppendEventAsync(Guid.Parse(run.RunId), "run.plan_diagnostic",
                "Planner raw answer captured because the plan was refused.",
                new
                {
                    run_id = run.RunId,
                    reason = lastError.Message,
                    parse_error = plannerParseError,
                    response_head = plannerHead,
                    response_head_length = plannerHead.Length
                }, cancellationToken).ConfigureAwait(false);
            await FailRunAsync(Guid.Parse(run.RunId), checkpoint, "invalid_task_graph", lastError.Message, cancellationToken).ConfigureAwait(false);
            return checkpoint;
        }

        checkpoint.PlanRevision++;
        checkpoint.Phase = "executing";
        checkpoint = await SaveCheckpointAsync(checkpoint, checkpoint.CheckpointRevision, "planned", cancellationToken).ConfigureAwait(false);
        await TrySetRunStatusAsync(run.RunId, "executing", null, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(Guid.Parse(run.RunId), "task_graph.created", $"{checkpoint.Tasks.Count} task(s) planned.", new
        {
            run_id = run.RunId,
            plan_revision = checkpoint.PlanRevision,
            task_count = checkpoint.Tasks.Count,
            task_keys = checkpoint.Tasks.Select(item => item.TaskKey).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        await TrySyncPlannedTasksToSessionPlanAsync(
            Guid.Parse(run.SessionId),
            Guid.Parse(run.RunId),
            checkpoint.Tasks,
            checkpoint.UserGoal,
            cancellationToken).ConfigureAwait(false);
        await EvaluateAndDispatchOperationsAsync(OperationalTriggerPoint.TaskGraphCreated, run, configuration, checkpoint, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    internal static bool ShouldUseExploreFirstCoordinator(
        FrozenRunConfigurationV1 configuration,
        RuntimeAgentDefinition coordinator)
    {
        if (configuration.Graph is not { Tier: FrozenGraphTiers.FreeForm }) return false;
        return coordinator.AllowedTools.Any(CoreVirtualToolPolicy.IsTeamSpawn);
    }

    private static List<DurableTaskNode> BuildExploreFirstCoordinatorTask(
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint)
    {
        var coordinator = RequiredConversationAgent(configuration);
        var task = new DurableTaskNode
        {
            IsCoordinatorTask = true,
            TaskId = Guid.NewGuid(),
            TaskKey = "coordinator",
            Title = checkpoint.UserGoal,
            Description = null,
            SuccessCriteria = [],
            Dependencies = [],
            RequiredCapabilities = coordinator.Capabilities.ToList(),
            RequiredTools = [],
            Priority = 1,
            Risk = "low",
            Status = "pending",
            WorkerAgentSlug = coordinator.Id,
            WorkerAgentDefinitionId = coordinator.AgentDefinitionId,
            WorkerAgentVersionId = coordinator.AgentVersionId,
            WorkerAgentVersionHash = coordinator.VersionContentHash,
            WorkerAssignmentReason = "explore_first_coordinator",
            WorkerHandle = "coordinator"
        };
        return [task];
    }

    private static List<DurableTaskNode> BuildSoloMasterTask(
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        bool isWorkflowPreparation = false)
    {
        var master = RequiredConversationAgent(configuration);
        var task = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = isWorkflowPreparation ? "space-preparation" : "solo",
            Title = checkpoint.UserGoal,
            Description = null,
            SuccessCriteria = isWorkflowPreparation ? [] : ["The goal is satisfied and the answer reports what what actually happened."],
            Dependencies = [],
            RequiredCapabilities = master.Capabilities.ToList(),
            RequiredTools = [],
            Priority = 1,
            Risk = "medium",
            Status = "pending",
            WorkerAgentSlug = master.Id,
            WorkerAgentDefinitionId = master.AgentDefinitionId,
            WorkerAgentVersionId = master.AgentVersionId,
            WorkerAgentVersionHash = master.VersionContentHash,
            WorkerAssignmentReason = "solo_master"
        };
        return [task];
    }

    private static WorkerSelection? ResolveSoloMaster(
        FrozenRunConfigurationV1 configuration,
        FrozenGraph? graph,
        DurableTaskNode task)
    {
        if (graph is null
            || (graph.Tier != FrozenGraphTiers.SoloDispatch && !task.IsSpacePreparation && !task.IsCoordinatorTask))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(task.WorkerAgentSlug)) return null;
        if (!string.Equals(task.WorkerAgentSlug, graph.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase)) return null;
        var master = configuration.OperationAgents.SingleOrDefault(agent =>
            string.Equals(agent.Id, task.WorkerAgentSlug, StringComparison.Ordinal)
            && agent.AgentDefinitionId == task.WorkerAgentDefinitionId
            && agent.AgentVersionId == task.WorkerAgentVersionId
            && string.Equals(agent.VersionContentHash, task.WorkerAgentVersionHash, StringComparison.OrdinalIgnoreCase));
        return master is null ? null : new WorkerSelection(master, task.WorkerAssignmentReason ?? "solo_master");
    }

    private async Task<RuntimeAgentInstance> EnsureConversationAuthorAsync(
        RunState run,
        FrozenRunConfigurationV1 configuration,
        FullDuplexCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {
        var conversationDefinition = RequiredConversationAgent(configuration);
        var runId = Guid.Parse(run.RunId);
        var all = await _instances.ListByRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var author = checkpoint.MeetingAgentId is { } meetingId ? all.FirstOrDefault(item => item.Id == meetingId) : null;
        author ??= all.FirstOrDefault(item => !item.Generated && item.AgentVersionId == conversationDefinition.AgentVersionId);
        if (author is not null)
        {
            VerifyRootInstance(author, conversationDefinition, checkpoint.MeetingAgentId, "meeting");
            return author;
        }
        author = await _instances.CreateRootAsync(new RuntimeAgentSeed(
            Guid.Parse(run.SessionId),
            runId,
            conversationDefinition.Id,
            conversationDefinition.Layer,
            conversationDefinition.Role,
            "chat",
            conversationDefinition.Capabilities,
            conversationDefinition.AllowedTools,
            ResourceSeed(conversationDefinition.ResourceGrants),
            configuration.Context.DefaultTokenBudget,
            AgentDefinitionId: conversationDefinition.AgentDefinitionId,
            AgentVersionId: conversationDefinition.AgentVersionId,
            VersionContentHash: conversationDefinition.VersionContentHash,
            AllowedDispatchTargets: conversationDefinition.AllowedDispatchTargets,
            DirectUserOutput: true), cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(runId, "agent.created", "Conversation author agent created.", new
        {
            agent_instance_id = author.Id,
            agent_slug = conversationDefinition.Id,
            layer = author.Layer,
            role = author.Role
        }, cancellationToken).ConfigureAwait(false);
        return author;
    }

    private static RuntimeAgentDefinition RequiredConversationAgent(FrozenRunConfigurationV1 configuration)
    {
        var slug = configuration.Graph?.ConversationTemplateSlug ?? "meeting";
        return configuration.OperationAgents.SingleOrDefault(item =>
            string.Equals(item.Id, slug, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"The frozen operation roster has no conversation agent '{slug}'.");
    }

    private static RuntimeAgentDefinition RequiredAgent(
        IReadOnlyList<RuntimeAgentDefinition> agents,
        string slug)
    {
        var agent = agents.SingleOrDefault(item => string.Equals(item.Id, slug, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Frozen profile has no '{slug}' agent.");
        if (!agent.Enabled) throw new InvalidDataException($"Frozen agent '{slug}' is disabled.");
        ValidateFrozenAgent(agent, slug);
        return agent;
    }

    private static void ValidateFrozenAgent(RuntimeAgentDefinition agent, string purpose)
    {
        if (agent.AgentDefinitionId is null || agent.AgentVersionId is null || string.IsNullOrWhiteSpace(agent.VersionContentHash))
            throw new InvalidDataException($"Frozen {purpose} agent '{agent.Id}' has no immutable version binding.");
    }

    private static void VerifyRootInstance(
        RuntimeAgentInstance instance,
        RuntimeAgentDefinition definition,
        Guid? checkpointInstanceId,
        string purpose)
    {
        if (instance.Generated
            || instance.AgentDefinitionId != definition.AgentDefinitionId
            || instance.AgentVersionId != definition.AgentVersionId
            || !string.Equals(instance.AgentVersionContentHash, definition.VersionContentHash, StringComparison.OrdinalIgnoreCase)
            || checkpointInstanceId is { } expectedId && instance.Id != expectedId)
        {
            throw new InvalidDataException($"Persisted {purpose} instance does not match its frozen agent version.");
        }
    }

    private static string PlannerInstructions(FullDuplexCheckpointV1 checkpoint, string baseInstructions, string laneKey = "main")
    {
        baseInstructions += $"\nLane: {laneKey}";
        if (checkpoint.RecommendedCapabilities.Count == 0) return baseInstructions;
        var lines = string.Join("\n", checkpoint.RecommendedCapabilities.Select(item =>
            $"- {item.Skill} (confidence: {item.Confidence}): {item.Reason}"));
        return baseInstructions
            + "\n\nCapability recommendations from the operation layer (advisory; accept or reject as you see fit):\n"
            + lines;
    }

    internal static IReadOnlyList<FrozenDispatchTarget> DispatchRosterOf(FrozenRunConfigurationV1 configuration) =>
        configuration.DispatchRoster ?? ComputeDispatchRoster(configuration);

    internal static IReadOnlyList<FrozenDispatchTarget> PlannerDispatchRosterOf(FrozenRunConfigurationV1 configuration)
    {
        if (configuration.DispatchRoster is { } explicitRoster && configuration.Graph is null)
            return explicitRoster;
        var allowed = ConversationDispatchTargets(configuration);
        var baseRoster = configuration.DispatchRoster ?? ComputeDispatchRoster(configuration);
        return allowed is null
            ? baseRoster
            : baseRoster.Where(target => allowed.Contains(target.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal static IReadOnlyList<FrozenDispatchTarget> DispatchRosterForWorker(
        FrozenRunConfigurationV1 configuration,
        string workerSlug,
        int currentDepth)
    {
        if (currentDepth >= configuration.Spawn.MaxDepth)
            return [];
        if (configuration.Graph is null
            || string.Equals(workerSlug, configuration.Graph.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase))
        {
            return PlannerDispatchRosterOf(configuration);
        }
        return ComputeDispatchRoster(configuration, workerSlug, currentDepth);
    }

    internal static IReadOnlyList<FrozenDispatchTarget> ComputeDispatchRoster(
        FrozenRunConfigurationV1 configuration,
        string? dispatcherSlug = null,
        int currentDepth = 0)
    {
        if (!string.IsNullOrWhiteSpace(dispatcherSlug))
        {
            if (currentDepth >= configuration.Spawn.MaxDepth)
                return [];
            if (string.Equals(dispatcherSlug, configuration.Graph?.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase))
            {
                var convAllowed = ConversationDispatchTargets(configuration);
                return BuildFrozenPlannerRoster(configuration)
                    .Where(agent => convAllowed is null || convAllowed.Contains(agent.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(agent => new FrozenDispatchTarget(agent.Name, agent.Description ?? string.Empty, agent.AllowedTools))
                    .ToArray();
            }
            var workerDef = configuration.ExecutionAgents.FirstOrDefault(a =>
                string.Equals(a.Id, dispatcherSlug, StringComparison.OrdinalIgnoreCase));
            var workerTemplate = configuration.Graph?.SpawnableTemplates.FirstOrDefault(t =>
                string.Equals(t.Slug, dispatcherSlug, StringComparison.OrdinalIgnoreCase));
            var narrowTargets = workerDef?.AllowedDispatchTargets ?? workerTemplate?.AllowedDispatchTargets;
            var allCandidates = AllExecutorDefinitions(configuration);
            return allCandidates
                .Where(candidate =>
                    (narrowTargets is null || narrowTargets.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
                    && GraphEdgeAuthority.IsDispatchAllowed(configuration.Graph, candidate.Name, dispatcherSlug))
                .Select(candidate => new FrozenDispatchTarget(candidate.Name, candidate.Description ?? string.Empty, candidate.AllowedTools))
                .ToArray();
        }

        var allowed = ConversationDispatchTargets(configuration);
        var result = new List<FrozenDispatchTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in BuildFrozenPlannerRoster(configuration))
        {
            if (allowed is not null && !allowed.Contains(agent.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            if (seen.Add(agent.Name))
                result.Add(new FrozenDispatchTarget(agent.Name, agent.Description ?? string.Empty, agent.AllowedTools));
        }
        if (configuration.Spawn.MaxDepth > 0)
        {
            var dispatchingWorkers = configuration.ExecutionAgents
                .Where(a => a.Enabled && a.AllowedTools.Any(t => CoreVirtualToolPolicy.IsTaskDispatch(t) || CoreVirtualToolPolicy.IsTeamSpawn(t)))
                .Select(a => a.Id)
                .Concat((configuration.Graph?.SpawnableTemplates ?? [])
                    .Where(t => t.ToolCeiling.Any(tool => CoreVirtualToolPolicy.IsTaskDispatch(tool) || CoreVirtualToolPolicy.IsTeamSpawn(tool)))
                    .Select(t => t.Slug))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var subDispatcher in dispatchingWorkers)
            {
                foreach (var target in ComputeDispatchRoster(configuration, subDispatcher, currentDepth: 0))
                {
                    if (seen.Add(target.Id))
                        result.Add(target);
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<AgentDefinition> AllExecutorDefinitions(FrozenRunConfigurationV1 configuration)
    {
        var list = new List<AgentDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in configuration.ExecutionAgents
                     .Where(a => a.Enabled && !string.Equals(a.Id, "task_planner", StringComparison.Ordinal))
                     .OrderBy(a => a.RosterOrder)
                     .ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            if (seen.Add(agent.Id))
                list.Add(ToPlannerRosterEntry(agent.AgentDefinitionId ?? Guid.Empty, agent.Id, agent.Role, agent.Capabilities, agent.AllowedTools, agent.Description));
        }
        if (configuration.Graph is { } graph)
        {
            foreach (var template in graph.SpawnableTemplates)
            {
                if (seen.Add(template.Slug))
                    list.Add(ToPlannerRosterEntry(template.AgentDefinitionId, template.Slug, template.Role, template.Capabilities, template.ToolCeiling, template.Description));
            }
        }
        return list;
    }

    private static bool IsNonSoloSpaceWorkflow(FrozenRunConfigurationV1 configuration) =>
        configuration.SpaceOptions is { MultiAgent: true } options
        && (options.WorkflowModeVersionId is not null
            || (configuration.Graph is { } g && !string.Equals(g.Tier, FrozenGraphTiers.SoloDispatch, StringComparison.Ordinal)));

    internal static IReadOnlyList<string>? ConversationDispatchTargets(FrozenRunConfigurationV1 configuration)
    {
        var slug = configuration.Graph?.ConversationTemplateSlug;
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return configuration.OperationAgents
            .FirstOrDefault(agent => string.Equals(agent.Id, slug, StringComparison.OrdinalIgnoreCase))?
            .AllowedDispatchTargets;
    }

    internal static string BuildDispatchRosterSection(IReadOnlyList<FrozenDispatchTarget> roster, bool usesTeamSpawn = false)
    {
        var toolLabel = usesTeamSpawn ? "team_spawn 的 agent_slug 取值" : "task_dispatch 的 agent 取值";
        if (roster.Count == 0)
            return $"## 可派发执行者\n本次运行没有可派发的执行者：{(usesTeamSpawn ? "team_spawn" : "task_dispatch")} 不可用，需要的工作请自己完成或如实告知用户。";
        var lines = roster.Select(target =>
            $"- {target.Id}: {(string.IsNullOrWhiteSpace(target.Description) ? "（未写职责）" : target.Description.Trim())} | 工具: {(target.Tools.Count == 0 ? "无" : string.Join(", ", target.Tools))}");
        return $"## 可派发执行者（{toolLabel}）\n"
            + "按职责选择专长匹配的队友，把其 id 原样填入参数，并提供自足的任务背景、工作区绝对路径、write_scope 边界与可核对验收条件。\n"
            + string.Join("\n", lines);
    }

    internal static IReadOnlyList<AgentDefinition> BuildFrozenPlannerRoster(FrozenRunConfigurationV1 configuration)
    {
        var graph = configuration.Graph;
        if (graph is null)
            return configuration.ExecutionAgents
                .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
                .OrderBy(agent => agent.RosterOrder)
                .ThenBy(agent => agent.Id, StringComparer.Ordinal)
                .Select(agent => ToPlannerRosterEntry(agent.AgentDefinitionId ?? Guid.Empty, agent.Id, agent.Role, agent.Capabilities, agent.AllowedTools, agent.Description))
                .ToArray();

        var slugByNodeKey = graph.Nodes.ToDictionary(node => node.NodeKey, node => node.AgentSlug, StringComparer.Ordinal);
        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddSlug(string? slug)
        {
            if (!string.IsNullOrWhiteSpace(slug) && seen.Add(slug)) slugs.Add(slug);
        }
        foreach (var edge in graph.Edges)
        {
            if (slugByNodeKey.TryGetValue(edge.TargetNodeKey, out var target)
                && GraphEdgeAuthority.IsDispatchAllowed(graph, target))
            {
                AddSlug(target);
            }
        }
        if (GraphSpawnAuthority.CarriesSpawnAuthority(graph.Tier))
        {
            foreach (var template in graph.SpawnableTemplates)
                AddSlug(template.Slug);
            if (string.Equals(graph.Tier, FrozenGraphTiers.FreeForm, StringComparison.Ordinal))
                foreach (var agent in configuration.ExecutionAgents
                    .Where(agent => agent.Enabled && !string.Equals(agent.Id, "task_planner", StringComparison.Ordinal))
                    .OrderBy(agent => agent.RosterOrder)
                    .ThenBy(agent => agent.Id, StringComparer.Ordinal))
                    AddSlug(agent.Id);
        }

        var enabledBySlug = configuration.ExecutionAgents
            .Where(agent => agent.Enabled)
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.First().Id, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var roster = new List<AgentDefinition>();
        foreach (var slug in slugs)
        {
            if (enabledBySlug.TryGetValue(slug, out var agent))
            {
                roster.Add(ToPlannerRosterEntry(agent.AgentDefinitionId ?? Guid.Empty, agent.Id, agent.Role, agent.Capabilities, agent.AllowedTools, agent.Description));
                continue;
            }
            var template = graph.SpawnableTemplates.FirstOrDefault(candidate =>
                string.Equals(candidate.Slug, slug, StringComparison.OrdinalIgnoreCase));
            if (template is not null)
                roster.Add(ToPlannerRosterEntry(template.AgentDefinitionId, template.Slug, template.Role, template.Capabilities, template.ToolCeiling, template.Description));
        }
        return roster;
    }

    private static AgentDefinition ToPlannerRosterEntry(
        Guid definitionId,
        string slug,
        string role,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> allowedTools,
        string? description) => new()
    {
        Id = definitionId,
        Name = slug,
        Description = description,
        Layer = "execution",
        AgentType = role,
        Capabilities = capabilities,
        AllowedTools = allowedTools,
        Enabled = true
    };

    internal static void RefuseSilentPlanningFallbackOnDeclaredEdges(FrozenRunConfigurationV1 configuration, PlannedTask[] planned)
    {
        if (configuration.Graph is { Edges.Count: > 0 } && planned.Any(task => task.IsFallback))
            throw new InvalidTaskGraphException(
                "The planner output could not be parsed as a task array (plan_parse_failed); "
                + "refusing to dispatch the silent fallback goal-task along declared edges.");
    }

    internal static string BuildPlannerRetryHint(Exception? lastError, string parseErrorDetail)
    {
        var detail = !string.IsNullOrWhiteSpace(parseErrorDetail) ? $"（解析错误: {parseErrorDetail}）" : string.Empty;
        var reason = lastError?.Message ?? "unknown";
        return "\n\n【重试纠正】你上一次的输出未能构成合法任务图（" + reason + "）" + detail + "。再次输出时："
            + "只输出一个裸 JSON 任务数组，不要用散文拒绝或解释；"
            + "success_criteria、dependencies、required_capabilities、required_tools 四个字段必须写成字符串数组（例如 \"success_criteria\": [\"判据\"]），绝不能写成单个字符串；"
            + "dependencies 只能引用本次已声明的 task_key，不得成环；"
            + "若目标无需执行任何子任务，直接输出 []。";
    }

    internal static List<DurableTaskNode> ValidateAndMaterializeGraph(
        PlannedTask[] tasks,
        int maxTasks,
        IReadOnlyList<FrozenDispatchTarget>? dispatchRoster = null)
    {
        if (tasks.Length == 0) throw new InvalidTaskGraphException("The planner returned no tasks.");
        if (tasks.Length > maxTasks) throw new InvalidTaskGraphException($"The planner returned {tasks.Length} tasks, above the frozen limit of {maxTasks}.");
        var candidates = new List<(PlannedTask Task, string Key, string Lane)>();
        var seen = new HashSet<(string Lane, string Key)>();
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Title)) throw new InvalidTaskGraphException("Every planned task needs a title.");
            var key = NormalizeTaskKey(task.TaskKey, task.Title);
            var lane = string.IsNullOrWhiteSpace(task.LaneKey) ? "main" : task.LaneKey.Trim();
            if (!seen.Add((lane, key))) throw new InvalidTaskGraphException($"Task key '{key}' is not unique within lane '{lane}'.");
            candidates.Add((task, key, lane));
        }
        var aliases = candidates.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!aliases.ContainsKey(candidate.Task.Title)) aliases[candidate.Task.Title] = candidate.Key;
        }
        var result = candidates.Select(candidate => new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = candidate.Key,
            Title = candidate.Task.Title.Trim(),
            Description = candidate.Task.Description,
            Category = string.IsNullOrWhiteSpace(candidate.Task.Category) ? null : candidate.Task.Category.Trim(),
            SuccessCriteria = candidate.Task.SuccessCriteria.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
            Dependencies = candidate.Task.Dependencies.Select(value => aliases.TryGetValue(value, out var dependency) ? dependency : throw new InvalidTaskGraphException($"Task '{candidate.Key}' references unknown dependency '{value}'.")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiredCapabilities = candidate.Task.RequiredCapabilities.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiredTools = candidate.Task.RequiredTools.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RequestedAgent = ResolveAssignee(candidate.Key, candidate.Task.Assignee, dispatchRoster),
            WriteScope = NormalizeWriteScope(candidate.Task.WriteScope),
            Worktree = candidate.Task.Worktree,
            Priority = candidate.Task.Priority,
            Risk = string.IsNullOrWhiteSpace(candidate.Task.Risk) ? "medium" : candidate.Task.Risk.Trim(),
            Status = "pending",
            LaneKey = candidate.Lane
        }).ToList();
        var byKey = result.GroupBy(item => item.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<(string? Lane, string Key)>();
        var visited = new HashSet<(string? Lane, string Key)>();
        foreach (var node in result) Visit(node);
        return result;

        void Visit(DurableTaskNode node)
        {
            var identity = (node.LaneKey, node.TaskKey);
            if (visited.Contains(identity)) return;
            if (!visiting.Add(identity)) throw new InvalidTaskGraphException("The task dependency graph contains a cycle.");
            foreach (var dependency in node.Dependencies)
            {
                foreach (var target in byKey[dependency]) Visit(target);
            }
            visiting.Remove(identity);
            visited.Add(identity);
        }
    }

    internal static string? ResolveAssignee(string taskKey, string? assignee, IReadOnlyList<FrozenDispatchTarget>? roster)
    {
        if (string.IsNullOrWhiteSpace(assignee)) return null;
        var requested = assignee.Trim();
        if (roster is null) return requested;
        var match = roster.FirstOrDefault(target => string.Equals(target.Id, requested, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Id;
        throw new InvalidTaskGraphException(
            $"Task '{taskKey}' names assignee '{requested}', which is not a dispatchable executor. {DescribeDispatchChoices(roster)}");
    }

    internal static List<string>? NormalizeWriteScope(IEnumerable<string>? entries)
    {
        var scope = (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
        return scope.Count == 0 ? null : scope;
    }

    internal static string DescribeDispatchChoices(IReadOnlyList<FrozenDispatchTarget> roster) =>
        roster.Count == 0
            ? "This run has no dispatchable executor."
            : "Valid ids: " + string.Join("; ", roster.Select(target =>
                string.IsNullOrWhiteSpace(target.Description) ? target.Id : $"{target.Id} ({target.Description})")) + ".";

    internal static List<DurableTaskNode> MergeReplannedGraph(
        IReadOnlyList<DurableTaskNode> existing,
        IReadOnlyList<DurableTaskNode> replacement)
    {
        var completed = existing
            .Where(item => item.Status == "completed")
            .ToDictionary(item => item.TaskKey, StringComparer.OrdinalIgnoreCase);
        return replacement.Select(item =>
        {
            if (!completed.TryGetValue(item.TaskKey, out var prior)) return item;
            return new DurableTaskNode
            {
                TaskId = prior.TaskId,
                TaskKey = item.TaskKey,
                Title = item.Title,
                Description = item.Description,
                Category = item.Category,
                SuccessCriteria = item.SuccessCriteria,
                Dependencies = item.Dependencies,
                RequiredCapabilities = item.RequiredCapabilities,
                RequiredTools = item.RequiredTools,
                Priority = item.Priority,
                Risk = item.Risk,
                Status = prior.Status,
                Attempt = prior.Attempt,
                ToolRounds = prior.ToolRounds,
                ToolTurns = prior.ToolTurns,
                PendingToolExecutionId = prior.PendingToolExecutionId,
                PendingToolApprovalId = prior.PendingToolApprovalId,
                WorkerAgentId = prior.WorkerAgentId,
                WorkerAgentSlug = prior.WorkerAgentSlug,
                WorkerAgentDefinitionId = prior.WorkerAgentDefinitionId,
                WorkerAgentVersionId = prior.WorkerAgentVersionId,
                WorkerAgentVersionHash = prior.WorkerAgentVersionHash,
                WorkerAssignmentReason = prior.WorkerAssignmentReason,
                RequestedAgent = prior.RequestedAgent,
                WorkerHandle = prior.WorkerHandle,
                TeammateHandle = prior.TeammateHandle,
                TeammateMode = prior.TeammateMode,
                DispatchId = prior.DispatchId,
                WriteScope = prior.WriteScope,
                Worktree = prior.Worktree,
                TaskWorktreePath = prior.TaskWorktreePath,
                TaskWorktreeBranch = prior.TaskWorktreeBranch,
                DispatchedByTaskId = prior.DispatchedByTaskId,
                AwaitingTaskIds = prior.AwaitingTaskIds,
                ReportedTaskIds = prior.ReportedTaskIds,
                Plan = prior.Plan,
                InputContextRevision = prior.InputContextRevision,
                ResultStatus = prior.ResultStatus,
                ResultSummary = prior.ResultSummary,
                Evidence = prior.Evidence,
                StaleEvidence = prior.StaleEvidence,
                CompletedAt = prior.CompletedAt,
                LaneKey = prior.LaneKey,
                Waits = prior.Waits,
                CriteriaVerdicts = prior.CriteriaVerdicts
            };
        }).ToList();
    }

    private static void MarkBlockedDescendants(IReadOnlyList<DurableTaskNode> tasks)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var task in tasks.Where(item => item.Status is "pending" or "ready"))
            {
                if (task.Dependencies.Any(dependency => tasks.Any(other => other.TaskKey == dependency && other.Status is "failed" or "blocked")))
                {
                    task.Status = "blocked";
                    task.ResultStatus = "blocked";
                    task.ResultSummary = "A dependency did not complete.";
                    changed = true;
                }
            }
        }
    }

    private static PlannedTask ToPlannedTask(DurableTaskNode node) => new()
    {
        TaskKey = node.TaskKey,
        Title = node.Title,
        Description = node.Description,
        SuccessCriteria = node.SuccessCriteria.ToArray(),
        Dependencies = node.Dependencies.ToArray(),
        RequiredCapabilities = node.RequiredCapabilities.ToArray(),
        RequiredTools = node.RequiredTools.ToArray(),
        Priority = node.Priority,
        Risk = node.Risk
    };

    private static DmaeaRunContext CreateRunContext(RunState run, FullDuplexCheckpointV1 checkpoint) => new()
    {
        RunId = checkpoint.RunId,
        SessionId = checkpoint.SessionId,
        TriggerMessageId = checkpoint.TriggerMessageId,
        TenantId = Guid.TryParse(run.TenantId, out var tenantId) ? tenantId : Guid.Empty,
        WorkspaceId = Guid.TryParse(run.WorkspaceId, out var workspaceId) ? workspaceId : Guid.Empty,
        UserGoal = checkpoint.UserGoal
    };

    private static string NormalizeTaskKey(string? candidate, string title)
    {
        var source = string.IsNullOrWhiteSpace(candidate) ? title : candidate;
        var buffer = new List<char>(source.Length);
        foreach (var value in source.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(value)) buffer.Add(value);
            else if (buffer.Count != 0 && buffer[^1] != '-') buffer.Add('-');
        }
        while (buffer.Count != 0 && buffer[^1] == '-') buffer.RemoveAt(buffer.Count - 1);
        return buffer.Count == 0 ? "task" : new string(buffer.ToArray());
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

    private static object[] BudgetShares(IReadOnlyList<ContextEvidence> items) =>
        [.. items.Select(item => new { source = item.Source, tokens = item.EstimatedTokens })];

    private async Task<PromptAssemblyResult> AssemblePromptAsync(
        FrozenRunConfigurationV1 configuration,
        RuntimeAgentDefinition definition,
        ContextPack context,
        CancellationToken cancellationToken)
    {
        var instructions = definition.SystemPrompt + (configuration.SpaceOptions is { } options ? SpaceCompositionPolicy.Instructions(options) : "");
        return await _promptAssembler.AssembleAsync(new FrozenPromptAssemblyRequest(
            definition.Id,
            context,
            instructions,
            definition.AgentVersionId,
            definition.VersionContentHash,
            definition.PromptPipelineId,
            definition.PromptVersionId,
            definition.PromptVersionContentHash,
            definition.PromptGraphJson,
            IncludeLiveFragments: configuration.FrozenPromptFragments is null,
            ResponseLocale: configuration.InterfaceLocale)
        {
            Workspace = configuration.Workspace,
            RuntimePolicy = configuration.RuntimePolicy,
            AgentDefinitionId = definition.AgentDefinitionId,
            AgentName = definition.Id,
            AgentSlug = definition.Id,
            AgentLayer = definition.Layer,
            ModeName = configuration.ModeDisplayName,
            ModeSlug = configuration.ModeSlug,
            Capabilities = definition.Capabilities,
            HasTools = definition.AllowedTools.Count > 0,
            FrozenFragments = configuration.FrozenPromptFragments
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string ResolveEffectiveContextStrategy(FrozenRunConfigurationV1 config)
    {
        // Priority 1: Session overlay (SpaceOptions)
        if (NormalizeSupportedStrategy(config.SpaceOptions?.ContextStrategy) is { } overlayStrategy)
            return overlayStrategy;

        // Priority 2: Selected mode static defaults (DefaultOptions -> RuntimePolicy.Settings)
        if (NormalizeSupportedStrategy(config.DefaultOptions?.ContextStrategy) is { } modeDefaultStrategy)
            return modeDefaultStrategy;

        if (config.RuntimePolicy?.Settings is { ValueKind: JsonValueKind.Object } settings
            && settings.TryGetProperty("context_strategy", out var prop)
            && prop.ValueKind == JsonValueKind.String
            && NormalizeSupportedStrategy(prop.GetString()) is { } policyStrategy)
        {
            return policyStrategy;
        }

        // Priority 3: Workspace baseline
        return NormalizeSupportedStrategy(config.Context.Strategy) ?? ContextPolicy.DefaultStrategy;
    }

    internal static double ResolveEffectiveCompactionRatio(FrozenRunConfigurationV1 config)
    {
        // Priority 1: Session overlay
        if (config.SpaceOptions?.ContextAdvanced?.CompactionTriggerRatio is { } overlayRatio
            && !double.IsNaN(overlayRatio) && !double.IsInfinity(overlayRatio)
            && overlayRatio is >= 0.40 and <= 0.95)
        {
            return overlayRatio;
        }

        // Priority 2: Selected mode static defaults
        if (config.DefaultOptions?.ContextAdvanced?.CompactionTriggerRatio is { } modeRatio
            && !double.IsNaN(modeRatio) && !double.IsInfinity(modeRatio)
            && modeRatio is >= 0.40 and <= 0.95)
        {
            return modeRatio;
        }

        if (config.RuntimePolicy?.Settings is { ValueKind: JsonValueKind.Object } settings
            && settings.TryGetProperty("compaction_trigger_ratio", out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDouble(out var ratio)
            && !double.IsNaN(ratio) && !double.IsInfinity(ratio)
            && ratio is >= 0.40 and <= 0.95)
        {
            return ratio;
        }

        // Priority 3: Workspace baseline
        return config.Context.CompactionTriggerRatio;
    }

    internal static string ResolveEffectiveRecallPolicy(FrozenRunConfigurationV1 config)
    {
        // Priority 1: Session overlay
        if (NormalizeNonEmptyLower(config.SpaceOptions?.ContextAdvanced?.RecallFidelity) is { } overlayRecall)
            return overlayRecall;

        // Priority 2: Selected mode static defaults
        if (NormalizeNonEmptyLower(config.DefaultOptions?.ContextAdvanced?.RecallFidelity) is { } modeRecall)
            return modeRecall;

        if (config.RuntimePolicy?.Settings is { ValueKind: JsonValueKind.Object } settings)
        {
            if (settings.TryGetProperty("recall_fidelity", out var rf)
                && rf.ValueKind == JsonValueKind.String
                && NormalizeNonEmptyLower(rf.GetString()) is { } fidelity)
            {
                return fidelity;
            }
            if (settings.TryGetProperty("recall_policy", out var rp)
                && rp.ValueKind == JsonValueKind.String
                && NormalizeNonEmptyLower(rp.GetString()) is { } policy)
            {
                return policy;
            }
        }

        // Priority 3: Workspace baseline
        return NormalizeNonEmptyLower(config.Context.RecallFidelity) ?? ContextPolicy.DefaultRecallFidelity;
    }

    internal static int ResolveEffectiveRecentMessageLimit(FrozenRunConfigurationV1 config)
    {
        // Priority 1: Session overlay
        if (config.SpaceOptions?.ContextAdvanced?.RecentMessageLimit is { } overlayLimit
            && overlayLimit is >= ContextPolicy.MinRecentMessageLimit and <= ContextPolicy.MaxRecentMessageLimit)
        {
            return overlayLimit;
        }

        // Priority 2: Selected mode static defaults
        if (config.DefaultOptions?.ContextAdvanced?.RecentMessageLimit is { } modeLimit
            && modeLimit is >= ContextPolicy.MinRecentMessageLimit and <= ContextPolicy.MaxRecentMessageLimit)
        {
            return modeLimit;
        }

        if (config.RuntimePolicy?.Settings is { ValueKind: JsonValueKind.Object } settings
            && settings.TryGetProperty("recent_message_limit", out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var limit)
            && limit is >= ContextPolicy.MinRecentMessageLimit and <= ContextPolicy.MaxRecentMessageLimit)
        {
            return limit;
        }

        // Priority 3: Workspace baseline
        return config.Context.RecentMessageLimit;
    }

    internal static bool ResolveEffectiveAllowCrossSessionRecall(FrozenRunConfigurationV1 config)
    {
        // Priority 1: Session overlay
        if (config.SpaceOptions?.ContextAdvanced?.AllowCrossSessionRecall is { } overlayCross)
            return overlayCross;

        // Priority 2: Selected mode static defaults
        if (config.DefaultOptions?.ContextAdvanced?.AllowCrossSessionRecall is { } modeCross)
            return modeCross;

        if (config.RuntimePolicy?.Settings is { ValueKind: JsonValueKind.Object } settings
            && settings.TryGetProperty("allow_cross_session_recall", out var prop)
            && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return prop.GetBoolean();
        }

        // Priority 3: Workspace baseline
        return config.Context.AllowCrossSessionRecall;
    }

    internal static string ResolveEffectiveEvolutionMode(FrozenRunConfigurationV1 config)
    {
        // Priority 1: Session overlay
        if (NormalizeSupportedEvolutionMode(config.SpaceOptions?.ContextAdvanced?.EvolutionMode) is { } overlayEvolution)
            return overlayEvolution;

        // Priority 2: Selected mode static defaults
        if (NormalizeSupportedEvolutionMode(config.DefaultOptions?.ContextAdvanced?.EvolutionMode) is { } modeEvolution)
            return modeEvolution;

        if (config.RuntimePolicy?.Settings is { ValueKind: JsonValueKind.Object } settings)
        {
            if (settings.TryGetProperty("evolution_mode", out var em)
                && em.ValueKind == JsonValueKind.String
                && NormalizeSupportedEvolutionMode(em.GetString()) is { } evolutionMode)
            {
                return evolutionMode;
            }
            if (settings.TryGetProperty("memory_evolution_mode", out var mem)
                && mem.ValueKind == JsonValueKind.String
                && NormalizeSupportedEvolutionMode(mem.GetString()) is { } memoryEvolutionMode)
            {
                return memoryEvolutionMode;
            }
        }

        // Priority 3: Workspace baseline
        if (config.Memory.CandidateOnly)
            return "candidate_only";
        return NormalizeSupportedEvolutionMode(config.Memory.EvolutionMode) ?? MemoryPolicy.DefaultEvolutionMode;
    }

    internal static int ResolveEffectiveCoreMarkdownMaxTokens(FrozenRunConfigurationV1 config)
    {
        // Priority 1: Session overlay
        if (config.SpaceOptions?.ContextAdvanced?.CoreMarkdownMaxTokens is { } overlayTokens
            && overlayTokens is >= MemoryPolicy.MinCoreMarkdownMaxTokens and <= MemoryPolicy.MaxCoreMarkdownMaxTokens)
        {
            return overlayTokens;
        }

        // Priority 2: Selected mode static defaults
        if (config.DefaultOptions?.ContextAdvanced?.CoreMarkdownMaxTokens is { } modeTokens
            && modeTokens is >= MemoryPolicy.MinCoreMarkdownMaxTokens and <= MemoryPolicy.MaxCoreMarkdownMaxTokens)
        {
            return modeTokens;
        }

        if (config.RuntimePolicy?.Settings is { ValueKind: JsonValueKind.Object } settings
            && settings.TryGetProperty("core_markdown_max_tokens", out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var tokens)
            && tokens is >= MemoryPolicy.MinCoreMarkdownMaxTokens and <= MemoryPolicy.MaxCoreMarkdownMaxTokens)
        {
            return tokens;
        }

        // Priority 3: Workspace baseline
        return config.Memory.CoreMarkdownMaxTokens;
    }

    private static string? NormalizeSupportedStrategy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().ToLowerInvariant();
        return normalized is "direct" or "managed" or "deep_curator" ? normalized : null;
    }

    private static string? NormalizeSupportedEvolutionMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().ToLowerInvariant();
        return normalized is "auto_evolve" or "candidate_only" ? normalized : null;
    }

    private static string? NormalizeNonEmptyLower(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _contextPressureEmittedRevisions = new();

    internal async Task<ContextPack> BuildContextAsync(
        RunState run,
        FrozenRunConfigurationV1 config,
        RuntimeAgentDefinition agentDefinition,
        string taskContext,
        string? goal,
        CancellationToken cancellationToken)
    {
        var agentId = agentDefinition.Id;
        var definitionId = agentDefinition.AgentDefinitionId;
        var toolContext = config.ToolConfiguration is { } tools
            ? definitionId is { } id
                ? tools.AgentContexts.TryGetValue(id, out var selected) ? selected : throw new UnauthorizedAccessException("The Agent definition is absent from the frozen tool configuration.")
                : tools.SharedContext
            : null;
        var effectiveStrategy = ResolveEffectiveContextStrategy(config);
        var pack = await _contextProvider.BuildContextAsync(new ContextBuildRequest(
            run.SessionId,
            run.RunId,
            config.RuntimeProfileId,
            agentId,
            taskContext,
            config.Context.DefaultTokenBudget,
            ResolveEffectiveRecentMessageLimit(config),
            config.Memory.RetrievalLimit,
            config.TinaChatInput)
        {
            Workspace = config.Workspace,
            ToolExecutionContext = toolContext,
            AllowedToolIds = agentDefinition.AllowedTools,
            ContextStrategyOverride = effectiveStrategy,
            CompactionTriggerRatioOverride = ResolveEffectiveCompactionRatio(config),
            RecallPolicyOverride = ResolveEffectiveRecallPolicy(config),
            AllowCrossSessionRecallOverride = ResolveEffectiveAllowCrossSessionRecall(config),
            EvolutionModeOverride = ResolveEffectiveEvolutionMode(config),
            CoreMarkdownMaxTokensOverride = ResolveEffectiveCoreMarkdownMaxTokens(config),
        }, cancellationToken).ConfigureAwait(false);

        // P2 Track 3 (Runtime Bleeding Control): when occupancy >= 70% accompanied by
        // SAME_ARGS_RETRY / tool error streaks OR when critical high-value sources
        // (workspace_instructions, mode_pipeline, reviewed_memory) were dropped by budget,
        // emit CONTEXT_PRESSURE and promote compaction_recommended = "true" early.
        pack = await EvaluateAndApplyRuntimeContextPressureAsync(run, pack, cancellationToken).ConfigureAwait(false);

        // Phase 11.5 Closure #1: honor ContextPack.Metadata["compaction_recommended"] == "true"
        // by scheduling non-blocking structural session compaction (skipping when strategy is "direct").
        if (pack.Metadata.TryGetValue("compaction_recommended", out var recommended)
            && string.Equals(recommended, "true", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedStrategy = pack.Metadata.TryGetValue("context_strategy", out var packStrategy) && !string.IsNullOrWhiteSpace(packStrategy)
                ? packStrategy.Trim()
                : effectiveStrategy;
            if (!string.Equals(resolvedStrategy, "direct", StringComparison.OrdinalIgnoreCase))
            {
                TryScheduleAsyncStructuralCompaction(run, config, pack, goal);
            }
        }

        var withReports = await WithOpenReportsAsync(run, config, agentId, pack, cancellationToken).ConfigureAwait(false);
        var withLedger = await WithPlanLedgerAsync(run, config, agentDefinition, withReports, cancellationToken).ConfigureAwait(false);
        return await WithExplorationDigestAsync(run, config, goal, withLedger, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContextPack> EvaluateAndApplyRuntimeContextPressureAsync(
        RunState run,
        ContextPack pack,
        CancellationToken cancellationToken)
    {
        var occupancyRatio = pack.TokenBudget > 0
            ? (double)pack.EstimatedTokens / pack.TokenBudget
            : 0.0;

        var droppedCritical = pack.Dropped
            .Select(d => d.Source)
            .Where(IsRuntimeCriticalContextSource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (droppedCritical.Count == 0 && occupancyRatio < 0.70)
        {
            return pack;
        }

        var sameArgsStreak = 0;
        var toolErrorStreak = 0;
        Guid? parsedRunId = Guid.TryParse(run.RunId, out var rGuid) ? rGuid : null;

        if (occupancyRatio >= 0.70 && parsedRunId is { } checkRunId)
        {
            try
            {
                var checkpoint = await TryReadCheckpointAsync(checkRunId).ConfigureAwait(false);
                if (checkpoint is not null)
                {
                    (_, sameArgsStreak) = ExtractCheckpointTrajectorySignals(checkpoint);
                    toolErrorStreak = ComputeMaxToolErrorStreak(checkpoint);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        var triggeredByHighOccupancyFaults = occupancyRatio >= 0.70 && (sameArgsStreak >= 2 || toolErrorStreak >= 2);
        var triggeredByCriticalDrops = droppedCritical.Count > 0;
        if (!triggeredByHighOccupancyFaults && !triggeredByCriticalDrops)
        {
            return pack;
        }

        var reason = triggeredByCriticalDrops && triggeredByHighOccupancyFaults
            ? "critical_sources_dropped_and_high_occupancy_retries"
            : triggeredByCriticalDrops
                ? "critical_sources_dropped"
                : sameArgsStreak >= 2
                    ? "high_occupancy_same_args_retry"
                    : "high_occupancy_tool_error_streak";

        var enrichedMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pack.Metadata)
        {
            enrichedMetadata[k] = v;
        }
        enrichedMetadata["compaction_recommended"] = "true";
        enrichedMetadata["context_pressure"] = "true";
        enrichedMetadata["context_pressure_signal"] = UnifiedGovernanceSignalCodes.ContextPressure;
        enrichedMetadata["context_pressure_reason"] = reason;

        if (parsedRunId is { } emitRunId)
        {
            var currentRevision = run.ContextRevision;
            if (!_contextPressureEmittedRevisions.TryGetValue(emitRunId, out var lastEmitted) || lastEmitted < currentRevision || lastEmitted == 0 && !_contextPressureEmittedRevisions.ContainsKey(emitRunId))
            {
                var shouldEmit = !_contextPressureEmittedRevisions.TryGetValue(emitRunId, out var prevRev) || prevRev < currentRevision;
                if (shouldEmit)
                {
                    _contextPressureEmittedRevisions[emitRunId] = currentRevision;
                    try
                    {
                        await AppendEventAsync(emitRunId, "context.pressure", "Runtime context pressure detected; waking early structural compaction.", new
                        {
                            run_id = run.RunId,
                            signal = UnifiedGovernanceSignalCodes.ContextPressure,
                            reason,
                            occupancy_pct = Math.Round(occupancyRatio * 100.0, 2),
                            dropped_critical_sources = droppedCritical,
                            same_args_retry_streak = sameArgsStreak,
                            tool_error_streak = toolErrorStreak,
                            compaction_recommended = true
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                    }
                }
            }
        }

        return new ContextPack
        {
            SessionId = pack.SessionId,
            RunId = pack.RunId,
            TokenBudget = pack.TokenBudget,
            EstimatedTokens = pack.EstimatedTokens,
            Evidence = pack.Evidence,
            Dropped = pack.Dropped,
            Metadata = enrichedMetadata
        };
    }

    internal static bool IsRuntimeCriticalContextSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var s = source.Trim();
        return s.Equals("workspace_instructions", StringComparison.OrdinalIgnoreCase)
            || s.Equals("project_instructions", StringComparison.OrdinalIgnoreCase)
            || s.Equals("mode_pipeline", StringComparison.OrdinalIgnoreCase)
            || s.Equals("reviewed_memory", StringComparison.OrdinalIgnoreCase)
            || s.Equals("project_memory", StringComparison.OrdinalIgnoreCase)
            || s.Contains("workspace_instructions", StringComparison.OrdinalIgnoreCase)
            || s.Contains("mode_pipeline", StringComparison.OrdinalIgnoreCase)
            || s.Contains("reviewed_memory", StringComparison.OrdinalIgnoreCase);
    }

    private static int ComputeMaxToolErrorStreak(FullDuplexCheckpointV1 checkpoint)
    {
        var maxStreak = 0;
        foreach (var task in checkpoint.Tasks)
        {
            var current = 0;
            foreach (var turn in task.ToolTurns)
            {
                var failed = turn.ToolSuccess == false
                    || !string.IsNullOrWhiteSpace(turn.ErrorCategory)
                    || string.Equals(turn.DispatchStatus, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(turn.DispatchStatus, "rejected", StringComparison.OrdinalIgnoreCase);
                if (failed)
                {
                    current++;
                    if (current > maxStreak) maxStreak = current;
                }
                else
                {
                    current = 0;
                }
            }
        }
        return maxStreak;
    }

    private void TryScheduleAsyncStructuralCompaction(
        RunState run,
        FrozenRunConfigurationV1 config,
        ContextPack pack,
        string? goal)
    {
        if (!Guid.TryParse(run.RunId, out var runId) || !Guid.TryParse(run.SessionId, out var sessionId))
            return;
        var currentRevision = run.ContextRevision;
        if (_compactionRecommendedRevisions.TryGetValue(runId, out var lastScheduled) && lastScheduled >= currentRevision)
            return;
        _compactionRecommendedRevisions[runId] = currentRevision;

        var compressor = config.OperationAgents.FirstOrDefault(a =>
            a.Enabled && string.Equals(OperationalRoleKey(a), "context_compressor", StringComparison.Ordinal));
        if (compressor is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunBackgroundStructuralCompactionAsync(run, config, compressor, sessionId, runId, pack, goal ?? string.Empty).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.TryLogDebug(ex, "Background structural context compaction skipped for run {RunId}.", runId);
            }
        }, CancellationToken.None);
    }

    private async Task<ContextPack> WithExplorationDigestAsync(
        RunState run,
        FrozenRunConfigurationV1 config,
        string? goal,
        ContextPack pack,
        CancellationToken cancellationToken)
    {
        if (_contextDigest is not { } digest
            || !ModeRuntimePolicyRegistry.Has(config.RuntimePolicy, ModeRuntimePolicyRegistry.StructuredContextCompactionFeature)
            || config.RuntimePolicy is not { } policy)
            return pack;
        if (!Guid.TryParse(run.TenantId, out var tenantId) || !Guid.TryParse(run.WorkspaceId, out var workspaceId)
            || !Guid.TryParse(run.SessionId, out var sessionId) || !Guid.TryParse(run.RunId, out var runId))
            return pack;
        ContextDigestResult? result;
        try
        {
            result = await digest.CompactAsync(new ContextDigestRequest(
                new ExplorationScope(tenantId, workspaceId, sessionId, runId),
                AutonomousExplorationSettings.From(policy.Settings),
                goal), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not fold the exploration graph of run {RunId}.", run.RunId);
            return pack;
        }
        return result is null ? pack : WithDigestEvidence(pack, result);
    }

    internal static ContextPack WithDigestEvidence(ContextPack pack, ContextDigestResult digest)
    {
        if (string.IsNullOrWhiteSpace(digest.Text)) return pack;
        var evidence = new ContextEvidence
        {
            Source = "exploration-digest",
            Content = digest.Text,
            EstimatedTokens = Math.Max(1, digest.Text.Length / 2)
        };
        return new ContextPack
        {
            SessionId = pack.SessionId,
            RunId = pack.RunId,
            TokenBudget = pack.TokenBudget,
            EstimatedTokens = pack.EstimatedTokens + evidence.EstimatedTokens,
            Evidence = [.. pack.Evidence, evidence],
            Dropped = pack.Dropped,
            Metadata = pack.Metadata
        };
    }

    private async Task<ContextPack> WithPlanLedgerAsync(
        RunState run,
        FrozenRunConfigurationV1 config,
        RuntimeAgentDefinition agentDefinition,
        ContextPack pack,
        CancellationToken cancellationToken)
    {
        if (_planLedger is not { } ledger
            || !ModeRuntimePolicyRegistry.Has(config.RuntimePolicy, ModeRuntimePolicyRegistry.PlanLedgerFeature)
            || !string.Equals(agentDefinition.Layer, "execution", StringComparison.OrdinalIgnoreCase))
            return pack;
        if (!Guid.TryParse(run.TenantId, out var tenantId) || !Guid.TryParse(run.WorkspaceId, out var workspaceId)
            || !Guid.TryParse(run.SessionId, out var sessionId) || !Guid.TryParse(run.RunId, out var runId))
            return pack;
        IReadOnlyList<PlanIntent> intents;
        try
        {
            intents = await ledger.ListAsync(
                new PlanLedgerScope(tenantId, workspaceId, sessionId, runId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not read the plan ledger of run {RunId}.", run.RunId);
            return pack;
        }
        var text = PlanLedgerPrompt.Format(intents);
        if (text is null) return pack;
        var evidence = new ContextEvidence { Source = "plan-ledger", Content = text, EstimatedTokens = Math.Max(1, text.Length / 2) };
        return new ContextPack
        {
            SessionId = pack.SessionId,
            RunId = pack.RunId,
            TokenBudget = pack.TokenBudget,
            EstimatedTokens = pack.EstimatedTokens + evidence.EstimatedTokens,
            Evidence = [.. pack.Evidence, evidence],
            Dropped = pack.Dropped,
            Metadata = pack.Metadata
        };
    }

    private async Task<ContextPack> WithOpenReportsAsync(
        RunState run,
        FrozenRunConfigurationV1 config,
        string agentId,
        ContextPack pack,
        CancellationToken cancellationToken)
    {
        if (Organization is not { } organization || !Guid.TryParse(run.SessionId, out var sessionId)) return pack;
        var conversation = config.OperationAgents.FirstOrDefault(agent => agent.DirectUserOutput);
        if (conversation is null || !string.Equals(conversation.Id, agentId, StringComparison.OrdinalIgnoreCase)) return pack;
        IReadOnlyList<OrganizationReportDigest> reports;
        try
        {
            reports = await organization.ListOpenReportsAsync(sessionId, 8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not read the open reports of session {SessionId}.", sessionId);
            return pack;
        }
        var relevant = reports.Where(report => report.Severity is "warning" or "blocking").Take(5).ToArray();
        if (relevant.Length == 0) return pack;
        var text = "Open governance reports in this session's organization (observations by standing roles; they change nothing by themselves. "
            + "Take them into account, say so to the user when one blocks the work, and do not claim a proposal was carried out unless evidence shows it):\n"
            + string.Join("\n", relevant.Select(report =>
                $"- [{report.Severity}/{report.ReportKind}] {report.AuthorDisplayName}: {Clip(report.Finding, 300)}"
                + (report.SubjectKind is null ? string.Empty : $" (about {report.SubjectKind} {report.SubjectId})")
                + (report.ProposedVerb is null ? string.Empty : $"; proposes {report.ProposedVerb}")));
        var evidence = new ContextEvidence { Source = "organization_reports", Content = text, EstimatedTokens = Math.Max(1, text.Length / 2) };
        return new ContextPack
        {
            SessionId = pack.SessionId,
            RunId = pack.RunId,
            TokenBudget = pack.TokenBudget,
            EstimatedTokens = pack.EstimatedTokens + evidence.EstimatedTokens,
            Evidence = [.. pack.Evidence, evidence],
            Dropped = pack.Dropped,
            Metadata = pack.Metadata
        };
    }
}
