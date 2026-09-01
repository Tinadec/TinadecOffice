namespace TinadecCore.Governance;

/// <summary>
/// Auto-approve policy: when enabled, a non-agent non-human decision path
/// approves pending permission requests inside a hard ceiling instead of
/// denying them as unauthorized approvers. Disabled by default — the
/// "must have exactly one binding-checked approval" invariant is unchanged.
/// </summary>
public sealed class AutoApproveOptions
{
    public const string SectionName = "TinadecApproval";

    /// <summary>Bumped whenever the policy semantics change; written into every auto decision and event.</summary>
    public const string PolicyVersion = "auto-approve-v1";

    public bool AutoApproveEnabled { get; set; }

    /// <summary>Requests above this risk rank are never auto-approved.</summary>
    public string AutoApproveRiskMax { get; set; } = "medium";

    /// <summary>Automatic approvals per run before the request escalates to a human.</summary>
    public int AutoApproveMaxPerRun { get; set; } = 5;

    /// <summary>Tool ids that the policy refuses to auto-approve. Any tool id ending in
    /// "_delete" or starting with "delete_" is additionally refused regardless of this list.</summary>
    public string[] HumanOnlyTools { get; set; } = ["git_push", "command_run", "git_worktree_remove", "mcp_invoke"];

    public bool IsHumanOnlyTool(string toolId) =>
        HumanOnlyTools.Contains(toolId, StringComparer.OrdinalIgnoreCase)
        || toolId.EndsWith("_delete", StringComparison.OrdinalIgnoreCase)
        || toolId.StartsWith("delete_", StringComparison.OrdinalIgnoreCase);
}
