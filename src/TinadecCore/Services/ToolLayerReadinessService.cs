using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

public sealed class ToolLayerReadinessService(
    CoreStore store,
    IToolRegistry tools)
{
    private static readonly string[] InternalScopePrefixes =
    [
        "approval",
        "event",
        "message",
        "model",
        "policy",
        "skill",
        "task",
        "context",
        "prompt",
        "mcp"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> ScopeToolAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["file.read"] = ["read_file"],
            ["grep"] = ["grep_content"],
            ["glob"] = ["glob_search"],
            ["shell.approved"] = ["sandbox_exec"],
            ["file.write.approved"] = ["apply_patch"],
            ["git.diff"] = ["git_diff"],
            ["git.status"] = ["git_status"],
            ["git.stage"] = ["git_stage"],
            ["git.unstage"] = ["git_unstage"],
            ["git.branch"] = ["git_branch_list"],
            ["git.worktree"] = ["git_worktree_list", "git_worktree_create", "git_worktree_remove"],
            ["git.commit"] = ["git_commit", "git_worktree_manager"],
            ["git.push"] = ["git_push", "git_worktree_manager"],
            ["git.fetch"] = ["git_fetch"],
            ["git.pull"] = ["git_pull"],
            ["git.merge"] = ["git_merge"],
            ["git.rebase"] = ["git_rebase"],
            ["conflict.resolve"] = ["git_conflict_resolve"],
            ["review.format"] = ["review_format"]
        };

    public ToolLayerReadinessReceiptDto Check()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var toolList = tools.ListTools()
            .OrderBy(tool => SourceSortKey(tool.Source))
            .ThenBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var executionAgents = store.ListAgentProfiles()
            .Where(agent => agent.Layer.Equals("execution", StringComparison.OrdinalIgnoreCase))
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var toolIds = toolList.Select(tool => tool.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedCounts = CountAssignedExecutionAgents(executionAgents, toolIds);

        var toolReceipts = toolList
            .Select(tool => BuildTool(tool, assignedCounts.TryGetValue(tool.Id, out var count) ? count : 0))
            .OrderBy(tool => StatusSortKey(tool.Status))
            .ThenBy(tool => SourceSortKey(tool.Source))
            .ThenBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var agentReceipts = executionAgents
            .Select(agent => BuildAgentScope(agent, toolList, toolIds))
            .OrderBy(agent => StatusSortKey(agent.Status))
            .ThenBy(agent => agent.AgentName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var readyToolCount = toolReceipts.Count(tool => Is(tool.Status, "ready"));
        var warningToolCount = toolReceipts.Count(tool => Is(tool.Status, "warning"));
        var blockedToolCount = toolReceipts.Count(tool => Is(tool.Status, "blocked"));
        var readyAgentCount = agentReceipts.Count(agent => Is(agent.Status, "ready"));
        var warningAgentCount = agentReceipts.Count(agent => Is(agent.Status, "warning"));
        var blockedAgentCount = agentReceipts.Count(agent => Is(agent.Status, "blocked"));
        var status = blockedToolCount > 0 || blockedAgentCount > 0
            ? "blocked"
            : warningToolCount > 0 || warningAgentCount > 0
                ? "warning"
                : "ready";

        return new ToolLayerReadinessReceiptDto(
            status,
            generatedAt,
            AgentWorkflowRuntime.RuntimeName,
            $"tool_layer_readiness_{generatedAt:yyyyMMddHHmmssfff}",
            toolReceipts.Length,
            readyToolCount,
            warningToolCount,
            blockedToolCount,
            agentReceipts.Length,
            readyAgentCount,
            warningAgentCount,
            blockedAgentCount,
            toolReceipts.Count(tool => tool.RequiresApproval),
            toolReceipts.Count(tool => tool.RequiresHumanCheckpoint),
            toolReceipts.Count(tool => tool.IsFuture),
            agentReceipts.Sum(agent => agent.UnresolvedScopeCount),
            toolReceipts,
            agentReceipts,
            [
                "Core owns Tool-layer readiness; Gateway and Desktop may display this receipt but must not recompute dispatchability, scope resolution, or approval policy.",
                "Tool providers declare capabilities while Core resolves canonical tool ids, provider layer, and human checkpoint requirements.",
                "Execution agents stay assignment-bound; unresolved scopes are surfaced as readiness evidence instead of silently expanding permissions."
            ]);
    }

    private static ToolLayerToolReadinessDto BuildTool(ToolDescriptorDto tool, int assignedExecutionAgentCount)
    {
        var isFuture = IsFutureTool(tool);
        var requiresHumanCheckpoint = RequiresHumanCheckpoint(tool);
        var status = string.IsNullOrWhiteSpace(tool.ExecuteEndpoint)
            ? "blocked"
            : isFuture
                ? "warning"
                : "ready";
        var summary = status switch
        {
            "blocked" => "Tool is registered but has no execution endpoint.",
            "warning" => "Tool is declared for future integration and should not be treated as an active runtime.",
            _ when requiresHumanCheckpoint =>
                "Tool is dispatchable only after the Core human-checkpoint policy is satisfied.",
            _ => "Tool is dispatchable as a read-only Core-approved capability."
        };

        return new ToolLayerToolReadinessDto(
            tool.Id,
            tool.DisplayName,
            tool.Source,
            SourceLayer(tool.Source),
            tool.Risk,
            status,
            tool.RequiresApproval,
            requiresHumanCheckpoint,
            isFuture,
            assignedExecutionAgentCount,
            summary,
            [
                $"source:{tool.Source}",
                $"provider_layer:{SourceLayer(tool.Source)}",
                $"risk:{tool.Risk}",
                $"requires_approval:{tool.RequiresApproval}",
                $"requires_human_checkpoint:{requiresHumanCheckpoint}",
                $"is_future:{isFuture}",
                $"assigned_execution_agent_count:{assignedExecutionAgentCount}",
                $"execute_endpoint:{tool.ExecuteEndpoint}"
            ]);
    }

    private static ToolLayerAgentScopeReadinessDto BuildAgentScope(
        AgentProfileDto agent,
        IReadOnlyList<ToolDescriptorDto> toolList,
        IReadOnlySet<string> toolIds)
    {
        var resolvedTools = agent.AllowedTools
            .SelectMany(scope => ResolveToolIds(scope, toolIds))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var internalCapabilities = agent.AllowedTools
            .Where(IsInternalScope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unresolved = agent.AllowedTools
            .Where(scope => !IsScopeResolved(scope, toolIds) && !IsInternalScope(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var toolById = toolList.ToDictionary(tool => tool.Id, StringComparer.OrdinalIgnoreCase);
        var approvalGatedToolCount = resolvedTools
            .Count(toolId => toolById.TryGetValue(toolId, out var tool) && RequiresHumanCheckpoint(tool));
        var status = !agent.Enabled
            ? "warning"
            : unresolved.Length > 0
                ? "warning"
                : resolvedTools.Length > 0 || internalCapabilities.Length > 0
                    ? "ready"
                    : "blocked";
        var summary = status switch
        {
            "blocked" => "Execution agent has no dispatchable tools or recognized internal Core capabilities.",
            "warning" when !agent.Enabled =>
                "Execution agent is disabled; its tool scope is visible but not dispatchable.",
            "warning" => "Execution agent has unresolved tool scope entries that Core will not expand implicitly.",
            _ => "Execution agent scope resolves to Core tools or recognized internal capabilities."
        };

        return new ToolLayerAgentScopeReadinessDto(
            agent.Id,
            agent.Name,
            agent.Layer,
            agent.AgentType,
            agent.Enabled,
            status,
            agent.AllowedTools.Count,
            resolvedTools.Length,
            internalCapabilities.Length,
            unresolved.Length,
            approvalGatedToolCount,
            resolvedTools,
            unresolved,
            summary,
            [
                $"agent_type:{agent.AgentType}",
                $"enabled:{agent.Enabled}",
                $"declared_scope_count:{agent.AllowedTools.Count}",
                $"dispatchable_tool_count:{resolvedTools.Length}",
                $"internal_capability_count:{internalCapabilities.Length}",
                $"unresolved_scope_count:{unresolved.Length}",
                $"approval_gated_tool_count:{approvalGatedToolCount}"
            ]);
    }

    private static Dictionary<string, int> CountAssignedExecutionAgents(
        IReadOnlyList<AgentProfileDto> executionAgents,
        IReadOnlySet<string> toolIds)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in executionAgents.Where(agent => agent.Enabled))
        foreach (var toolId in agent.AllowedTools.SelectMany(scope => ResolveToolIds(scope, toolIds))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            result[toolId] = result.TryGetValue(toolId, out var count) ? count + 1 : 1;

        return result;
    }

    private static IReadOnlyList<string> ResolveToolIds(string scope, IReadOnlySet<string> toolIds)
    {
        if (toolIds.Contains(scope)) return [scope];

        if (!ScopeToolAliases.TryGetValue(scope, out var aliases)) return [];

        return aliases.Where(toolIds.Contains).ToArray();
    }

    private static bool IsScopeResolved(string scope, IReadOnlySet<string> toolIds)
    {
        return ResolveToolIds(scope, toolIds).Count > 0;
    }

    private static bool RequiresHumanCheckpoint(ToolDescriptorDto tool)
    {
        return tool.RequiresApproval || !tool.Risk.Equals("read-only", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFutureTool(ToolDescriptorDto tool)
    {
        return tool.Capabilities.Any(capability => capability.EndsWith(".future", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInternalScope(string value)
    {
        var normalized = value.Trim();
        return InternalScopePrefixes.Any(prefix =>
            normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static string SourceLayer(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "core" => "core",
            "code" => "tool-layer",
            "codex-rust" => "native-glue",
            _ => "extension"
        };
    }

    private static int SourceSortKey(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "core" => 0,
            "code" => 1,
            "codex-rust" => 2,
            _ => 3
        };
    }

    private static int StatusSortKey(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "blocked" => 0,
            "warning" => 1,
            "ready" => 2,
            _ => 3
        };
    }

    private static bool Is(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
