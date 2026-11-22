using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>Deterministic, narrowing-only composition of published resources.</summary>
internal static class SpaceCompositionPolicy
{
    internal static readonly HashSet<string> PreparationReadTools = new(StringComparer.OrdinalIgnoreCase)
        { "read_file", "ls", "stat", "file_search", "git_diff", "git_status", "git_log", "git_blame", "git_file_at_revision" };
    internal static void Validate(SpaceRunOptions? options, FormalModeRoster roster, FrozenWorkspaceBinding? workspace)
    {
        if (options is null) return;
        if (options.WorkflowModeVersionId == Guid.Empty)
            throw new RunAdmissionException("space_options_invalid", "workflow_mode_version_id must be a published version id or null.");
        if (options.WorkflowModeVersionId is not null)
        {
            if (!roster.HasDeclaredEdges || roster.Execution.Count == 0)
                throw new RunAdmissionException("space_workflow_invalid", "The selected workflow must declare execution nodes and dispatch edges.");
            if (!options.MultiAgent)
                throw new RunAdmissionException("space_options_conflict", "This workflow requires executor dispatch. Enable multi-agent collaboration or turn the workflow off.");
        }
        var root = roster.Operation.FirstOrDefault(item => item.Id == roster.ConversationTemplateSlug);
        if (root is null) throw new RunAdmissionException("space_base_unavailable", "The selected resources have no conversation identity.");
        var rootExecutesDirectly = !options.MultiAgent
            && options.WorkflowModeVersionId is null
            && root.AllowedTools.Any(tool => !CoreVirtualToolPolicy.IsTinadecConfig(tool) && !CoreVirtualToolPolicy.IsTeamTool(tool));
        if (options.PlanFirst && rootExecutesDirectly
            && !root.AllowedTools.Contains(CoreVirtualToolPolicy.PlanUpdateToolId, StringComparer.OrdinalIgnoreCase)
            && !root.AllowedTools.Contains(CoreVirtualToolPolicy.TeamPlanToolId, StringComparer.OrdinalIgnoreCase))
            throw new RunAdmissionException("space_options_unavailable", "This published baseline does not offer the plan_update or team_plan capability.");
        if (options.Worktree && workspace is not { IsGitRepository: true })
            throw new RunAdmissionException("space_worktree_unavailable", "An independent worktree requires a Git project.");
    }

    internal static RuntimeAgentDefinition[] Narrow(IEnumerable<RuntimeAgentDefinition> agents, SpaceRunOptions options) =>
        agents.Select(agent => agent with
        {
            AllowedTools = agent.AllowedTools.Where(tool => options.MultiAgent || !IsDispatch(tool))
                .Where(tool => options.Worktree || !IsWorktreeManagement(tool))
                .Concat(options.SpecEnabled && agent.DirectUserOutput ? [CoreVirtualToolPolicy.SpecProposeToolId] : Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Capabilities = agent.Capabilities.Where(capability => options.MultiAgent || !IsSpawn(capability)).ToArray(),
            AllowedDispatchTargets = options.MultiAgent ? agent.AllowedDispatchTargets : [],
        }).ToArray();

    internal static FrozenGraph Narrow(FrozenGraph graph, IReadOnlyList<RuntimeAgentDefinition> operation, SpaceRunOptions options) =>
        graph with
        {
            Tier = !options.MultiAgent
                ? FrozenGraphTiers.SoloDispatch
                : options.WorkflowModeVersionId is not null
                    ? (operation.First(agent => agent.Id == graph.ConversationTemplateSlug).Capabilities.Any(IsSpawn)
                        ? FrozenGraphTiers.SelfDispatch : FrozenGraphTiers.Deterministic)
                    : graph.Tier,
            SpawnableTemplates = options.MultiAgent ? graph.SpawnableTemplates.Select(template => template with
            {
                ToolCeiling = template.ToolCeiling.Where(tool => options.Worktree || !IsWorktreeManagement(tool)).ToArray()
            }).ToArray() : [],
        };

    internal static bool IsDispatch(string tool) =>
        CoreVirtualToolPolicy.IsTaskDispatch(tool)
        || CoreVirtualToolPolicy.IsTaskWait(tool)
        || CoreVirtualToolPolicy.IsTeamSpawn(tool)
        || CoreVirtualToolPolicy.IsTeamWait(tool)
        || CoreVirtualToolPolicy.IsTeamIntegrate(tool)
        || tool.Equals("tina_chat_execute_intent", StringComparison.OrdinalIgnoreCase);
    internal static bool IsSpawn(string capability) => capability is "agent.create_temporary" or "agent.spawn" or "agent.create_persistent";
    internal static bool IsWorktreeManagement(string tool) => tool is "git_worktree_create" or "git_worktree_remove";

    internal static string Instructions(SpaceRunOptions options) =>
        "\n\n[Spatial task options — frozen at submission]\n"
        + (options.MultiAgent ? "You may dispatch suitable independent work within your frozen roster.\n" : "Do the work yourself. New executor dispatch and spawning are disabled; governance and approvals remain active.\n")
        + (options.PlanFirst && !options.SpecEnabled && options.WorkflowModeVersionId is null ? "Before changing files, running a mutating tool, or delegating work, call plan_update with a concrete plan, then continue executing. Read-only investigation and simple answers need no artificial plan. Keep the plan updated. This is NOT read-only Plan mode and adds no approval pause.\n" : "")
        + (options.SpecEnabled ? "Spec development is enabled: investigate, then call spec_propose with the complete requirements document (user stories and testable acceptance criteria), wait for its human confirmation, propose design (modules, interfaces, alternatives, verification), and finally tasks (ordered implementation checklist). Each confirmation is durable and belongs to that exact document. Only after all three are approved may you implement. These documents are the plan; do not create a second competing plan. Save approved documents under docs/specs/ when a workspace is available.\n" : "")
        + (options.BulletinBoard ? "Organization collaboration is available within your existing role permissions.\n" : "The bulletin board is disabled for this run. Do not read or post board messages; direct governance and approval channels remain available.\n")
        + (options.Worktree ? "Core establishes one independent worktree for this run before execution. Use its execution root. Keep changes for user inspection; do not merge or remove it automatically.\n"
            : "Independent worktree isolation is off. Work in the current project; do not automatically create or switch to a worktree.\n");
}
