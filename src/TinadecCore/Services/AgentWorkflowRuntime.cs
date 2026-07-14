using System.Diagnostics;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Tracing;

namespace TinadecCore.Services;

public sealed class AgentWorkflowRuntime(IToolRegistry tools) : IAgentWorkflowRuntime
{
    public const string RuntimeName = "tinadec-core-workflow";

    public AgentWorkflowPlanDto Compile(OrchestrationSnapshotDto snapshot)
    {
        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.AgentWorkflowCompile);

        if (snapshot.Run is null) return new AgentWorkflowPlanDto("", RuntimeName, []);

        var nodeMap = snapshot.Nodes.ToDictionary(node => node.Id);
        var steps = snapshot.Assignments
            .Select(assignment =>
            {
                nodeMap.TryGetValue(assignment.TaskNodeId, out var node);
                var toolIds = ResolveToolIds(assignment, node);
                return new AgentWorkflowStepDto(
                    $"workflow_step_{assignment.Id}",
                    assignment.RunId,
                    assignment.TaskNodeId,
                    assignment.AgentId,
                    assignment.AgentType,
                    RuntimeName,
                    assignment.PermissionMode,
                    toolIds,
                    "compiled");
            })
            .ToArray();

        return new AgentWorkflowPlanDto(snapshot.Run.Id, RuntimeName, steps);
    }

    private IReadOnlyList<string> ResolveToolIds(AgentAssignmentDto assignment, TaskNodeDto? node)
    {
        var available = tools.ListTools("programming").Select(tool => tool.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requested = new List<string>();

        // Search-oriented agents: code-explorer, search-specialist, file-finder
        if (assignment.AgentType is "code-explorer" or "search-specialist" or "file-finder")
        {
            requested.Add("search_files");
            requested.Add("glob_search");
            requested.Add("grep_content");
        }

        // Test / multimodal agent needs sandbox
        if (assignment.AgentType is "test-multimodal" or "testing-agent") requested.Add("sandbox_exec");

        // Git manager needs Git tooling plus read-only context for handoff notes.
        if (assignment.AgentType is "git-manager")
        {
            requested.Add("git_worktree_manager");
            requested.AddRange([
                "git_stage", "git_unstage", "git_commit",
                "git_status", "git_push_readiness", "git_diff", "git_log_list", "git_log_detail", "git_file_history",
                "git_branch_list", "git_worktree_list", "git_ref_list", "git_remote_list", "git_blame", "git_file_at_revision",
                "git_conflict_preview"
            ]);
            requested.Add("sandbox_exec");
            requested.Add("review_format");
            requested.Add("read_file");
            requested.Add("grep_content");
        }

        // Code writer needs apply_patch and sandbox
        if (assignment.AgentType is "code-writer")
        {
            requested.Add("sandbox_exec");
            requested.Add("apply_patch");
        }

        // Review-related agents
        if (assignment.AgentType is "review-executor" || node?.RequiredCapabilities.Contains("review.format") == true)
            requested.Add("review_format");

        // Agents that need file/workspace access
        if (assignment.AgentType is "code-explorer" or "context-compressor" or "file-finder")
        {
            requested.Add("read_file");
            requested.Add("list_directory");
        }

        if (assignment.AllowedTools.Any(tool => tool.Contains("write", StringComparison.OrdinalIgnoreCase)))
            requested.Add("apply_patch");

        // Auto-assign read-only tools to any agent type that needs workspace access
        if (requested.Count > 0)
        {
            requested.Add("read_file");
            requested.Add("list_directory");
        }

        return requested
            .Where(available.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
