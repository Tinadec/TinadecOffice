using TinadecCore.Abstractions.Ports;

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
    public const string PolicyVersion = ToolApprovalPolicy.PolicyVersion;

    public bool AutoApproveEnabled { get; set; }

    /// <summary>Requests above this risk rank are never auto-approved.</summary>
    public string AutoApproveRiskMax { get; set; } = "medium";

    /// <summary>Automatic approvals per run before the request escalates to a human.</summary>
    public int AutoApproveMaxPerRun { get; set; } = 5;

    /// <summary>
    /// Release READ-level tool claims in an <c>ask</c> run without a human decision.
    /// A read cannot change the workspace, and the resource envelope plus the tool
    /// process's own root check already bound which paths it may touch, so a click
    /// adds no authority — it only adds latency. Gating reads was what stalled a real
    /// run for 22 minutes on a directory listing and what made an exploratory loop
    /// impractical. Mutating claims are untouched by this switch, and a tool on the
    /// human-only list is never released.
    /// </summary>
    public bool ReleaseReadOnlyInAskMode { get; set; } = true;

    /// <summary>Tool ids that the policy refuses to auto-approve. Any tool id ending in
    /// "_delete" or starting with "delete_" is additionally refused regardless of this list.
    /// "shell" is the registered id of the command tool ("command_run" predates it and stays
    /// listed); a shell call can drive any write the sandbox allows, so it never auto-approves.
    /// "mcp_invoke" stays here because it can change an external system. "web_fetch" is
    /// deliberately absent: it cannot change the workspace, its address guard rejects
    /// private/loopback targets, and gating every fetch was the 22-minute read stall in
    /// another shape. A scope that wants a click can add it back through
    /// <c>runtime.toml [approval] human_only_tools</c>. Browser act tools are listed: they
    /// change pages the user is watching, and a raised auto-approve ceiling must not turn
    /// them into an unattended click surface.</summary>
    public string[] HumanOnlyTools { get; set; } =
    [
        "git_push", "command_run", "shell", "git_worktree_remove", "mcp_invoke",
        "browser_navigate", "browser_click", "browser_type", "browser_press", "browser_scroll",
        "browser_history", "browser_tab_open", "browser_tab_select", "browser_tab_close", "browser_batch",
    ];

    /// <summary>
    /// The highest risk a delegated approval gate (<c>delegate-*</c> permission modes) may decide.
    /// Above it — and for every human-only tool — the call waits for the person, whatever mode the
    /// run was started in. The default keeps the project rule that high-risk operations get a human
    /// checkpoint. Configuration may narrow the low/medium ceiling; elevated/high/critical and
    /// unknown risks always require a person, including when a shell opt-in rule exists.
    /// </summary>
    public string DelegatedApprovalRiskMax { get; set; } = "medium";

    public bool IsHumanOnlyTool(string toolId) =>
        HumanOnlyTools.Contains(toolId, StringComparer.OrdinalIgnoreCase)
        || toolId.EndsWith("_delete", StringComparison.OrdinalIgnoreCase)
        || toolId.StartsWith("delete_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback shape for embedded hosts that register no runtime document. Product
    /// hosts take <see cref="ToolApprovalPolicy"/> from the scope's runtime.toml.
    /// </summary>
    public ToolApprovalPolicy ToPolicy() => new()
    {
        Enabled = AutoApproveEnabled,
        RiskMax = AutoApproveRiskMax,
        MaxPerRun = AutoApproveMaxPerRun,
        ReleaseReadOnlyInAskMode = ReleaseReadOnlyInAskMode,
        DelegatedRiskMax = DelegatedApprovalRiskMax,
        HumanOnlyTools = HumanOnlyTools
    };
}
