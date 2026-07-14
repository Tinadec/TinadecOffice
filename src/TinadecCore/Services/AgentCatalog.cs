using Tinadec.Contracts.Models;

namespace TinadecCore.Services;

public sealed record AgentProfileSeed(
    string Id,
    string Name,
    string Layer,
    string AgentType,
    string Mode,
    string Description,
    string ModelRoutePurpose,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> Capabilities,
    string? SystemPrompt = null);

public sealed record AgentCandidateSeed(
    string Id,
    string GeneratedByAgentId,
    string Name,
    string Layer,
    string AgentType,
    string Description,
    IReadOnlyList<string> SuggestedTools,
    IReadOnlyList<string> EvaluationNotes);

public static class AgentCatalog
{
    public static IReadOnlyList<AgentModeDto> Modes { get; } =
    [
        new("balanced", "Balanced", "Default two-layer orchestration with one planner lane and two executor lanes.", 2,
            true, true, "balanced"),
        new("plan-first", "Plan First", "Meeting and supervisor agents draft a task graph before execution starts.", 1,
            true, true, "strict"),
        new("parallel", "Parallel", "Allows more execution-layer workers when dependencies and budget allow it.", 4,
            true, true, "performance"),
        new("safe-research", "Safe Research",
            "Read-only exploration with strict approval gates for writes, shell, network, and ACP.", 2, false, true,
            "strict")
    ];

    /// <summary>
    /// Dual-layer agent architecture:
    ///   Layer 1 - Planning active agents: self-driven orchestration, policy, context, and supervision.
    ///   Layer 2 - Execution passive agents: assignment-driven workers with bounded tools and evidence.
    /// </summary>
    public static IReadOnlyList<AgentProfileSeed> Profiles { get; } =
    [
        new(
            "agent_meeting",
            "Meeting Agent",
            "planning",
            "meeting",
            "plan-first",
            "Talks with the user, parses intent, creates task graphs, and marks success criteria and approval points.",
            "planner",
            ["skill", "model.chat"],
            ["task_graph.create", "success_criteria.define", "approval_points.mark", "intent.parse"]),
        new(
            "agent_context_compressor",
            "Context Compressor Agent",
            "planning",
            "context-compressor",
            "balanced",
            "Maintains reversible context packs, token budgets, evidence maps, and long-session summaries.",
            "context",
            ["message.read", "event.read"],
            ["context.compact", "context.pattern.detect", "evidence.map", "summary.expand", "token_budget.guard"]),
        new(
            "agent_prompt_context_engineer",
            "Prompt Context Engineer Agent",
            "planning",
            "prompt-context-engineer",
            "balanced",
            "Plans prompt fragments, token budget, context packs, and evidence emphasis for Meeting Agent turns.",
            "context",
            ["prompt_context_resolve", "message.read", "event.read", "model.chat"],
            ["prompt.context.plan", "prompt.fragment.select", "token_budget.guard", "context_pack.rank"]),
        new(
            "agent_evolver",
            "Evolution Agent",
            "planning",
            "evolver",
            "safe-research",
            "Observes repeated workflows and proposes candidate skills, MCP manifests, prompts, or executor specs outside the hot path.",
            "evolution",
            ["event.read", "skill.read"],
            [
                "evolution.observe", "pattern.learn", "candidate.generate", "evaluation.plan", "skill.propose",
                "mcp.propose", "executor.propose"
            ]),
        new(
            "agent_tool_assistant",
            "Tool Assistant Agent",
            "planning",
            "tool-assistant",
            "balanced",
            "Selects toolkits, model routes, MCP servers, ACP adapters, and permission envelopes for task nodes.",
            "tooling",
            ["skill", "mcp.list", "model.route"],
            ["toolkit.resolve", "model_route.resolve", "policy_scope.assign", "tool.recommend"]),
        new(
            "agent_supervisor",
            "Supervisor Agent",
            "planning",
            "supervisor",
            "safe-research",
            "Guards safety, cost, quality, cancellation, and approval thresholds across execution-layer output.",
            "supervisor",
            ["approval", "event.read", "policy"],
            ["authorize", "abort_run", "raise_alert", "budget.guard", "quality.review"]),
        new(
            "agent_skill_learner",
            "Skill Learner Agent",
            "planning",
            "skill-learner",
            "balanced",
            "Learns reusable skill and tool-template patterns from history and feedback under Core supervision.",
            "evolution",
            ["event.read", "skill.read", "model.chat"],
            ["skill.learn", "tool.template.create", "knowledge.register", "feedback.analyze"]),

        new(
            "executor_task_planner",
            "Task Planner Executor",
            "execution",
            "task-planner",
            "balanced",
            "Receives task nodes and produces deterministic step plans and structured execution guidance.",
            "executor",
            ["task.step", "event.write"],
            ["step.run", "step.result", "plan.decompose"]),
        new(
            "executor_test_multimodal",
            "Test Multimodal Executor",
            "execution",
            "test-multimodal",
            "balanced",
            "Runs approved validation and classifies failures with text and multimodal evidence.",
            "test",
            ["shell.approved", "event.write", "model.multimodal"],
            ["test.run", "failure.classify", "visual.diff", "evidence.capture"]),
        new(
            "executor_code_explorer",
            "Code Explorer Executor",
            "execution",
            "code-explorer",
            "safe-research",
            "Read-only worker that locates files, symbols, references, and code evidence.",
            "search",
            ["file.read", "grep", "glob"],
            ["code.search", "symbol.locate", "evidence.collect", "ref.trace"]),
        new(
            "executor_search_specialist",
            "Search Specialist Executor",
            "execution",
            "search-specialist",
            "safe-research",
            "Read-only worker that searches docs, events, extension metadata, and workspace context before execution.",
            "search",
            ["file.read", "grep", "glob", "event.read"],
            ["search.query", "evidence.collect", "web.search", "doc.retrieve"]),
        new(
            "executor_file_finder",
            "File Finder Executor",
            "execution",
            "file-finder",
            "safe-research",
            "Read-only worker that resolves target files through glob, fuzzy name, and path fragment searches.",
            "search",
            ["file.read", "glob"],
            ["file.glob", "file.locate", "path.resolve"]),
        new(
            "executor_git_manager",
            "Git Manager Subagent",
            "execution",
            "git-manager",
            "balanced",
            "Manages approved Git workflows, prepares push plans, explains diffs and branch state, and records user-facing Git handoff notes.",
            "git",
            [
                "git_worktree_manager", "git_stage", "git_unstage", "git_commit", "git_checkout", "git_branch_create", "git_branch_delete", "git_branch_rename", "git_worktree_create", "git_worktree_remove", "git_fetch", "git_push", "git_pull", "git_merge", "git_rebase", "git_conflict_resolve", "git_status", "git_push_readiness", "git_diff", "git_log_list", "git_log_detail",
                "git_file_history", "git_branch_list", "git_worktree_list", "git_ref_list", "git_remote_list", "git_blame",
                "git_file_at_revision", "git_conflict_preview", "review_format", "read_file", "grep_content", "event.write"
            ],
            [
                "git.status", "git.diff", "git.stage", "git.unstage", "git.branch", "git.worktree", "git.commit",
                "git.push", "git.merge", "git.rebase", "conflict.resolve", "handoff.explain"
            ],
            "You are TinadecOffice's Git Manager Subagent. Work only from assigned execution tasks. Explain repository state, branch intent, diff impact, and push readiness clearly. Never push, rewrite history, create commits, or mutate Git state unless Core has supplied an explicit approval-bound tool invocation."),
        new(
            "executor_code_writer",
            "Code Writer Executor",
            "execution",
            "code-writer",
            "balanced",
            "Applies approved code edits and patches while preserving task specs, local style, and evidence trails.",
            "executor",
            ["shell.approved", "event.write", "file.write.approved"],
            ["code.write", "patch.apply", "code.refactor", "style.enforce"]),
        new(
            "executor_designer",
            "Designer Executor",
            "execution",
            "designer",
            "balanced",
            "Produces UI, layout, style, and design-token plans for design-oriented task nodes.",
            "executor",
            ["model.chat", "model.multimodal", "event.write"],
            ["design.generate", "ui.component.create", "style.compose", "layout.plan", "design_token.emit"])
    ];

    public static IReadOnlyList<AgentCandidateSeed> Candidates { get; } =
    [
        new(
            "cand_evolution_review_agent",
            "agent_evolver",
            "Evolution Review Executor",
            "execution",
            "review-executor",
            "Candidate generated from repeated review workflows. It should stay read-only until golden-repo evaluation passes.",
            ["file.read", "grep", "git.diff"],
            ["Schema valid", "Read-only by default", "Needs golden-repo evaluation before enablement"]),
        new(
            "cand_mcp_packager_agent",
            "agent_evolver",
            "MCP Packager Agent",
            "planning",
            "tool-packager",
            "Candidate that turns repeated external tool setup flows into declarative MCP extension manifests.",
            ["event.read", "market.preview"],
            ["Requires extension signing workflow", "Must stay out of hot-path execution"])
    ];
}
