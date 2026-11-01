namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// User-editable tool approval policy. The source of truth is
/// <c>runtime.toml [approval]</c>; this record is the typed shape both the
/// runtime configuration loader and the governance policy share.
/// </summary>
/// <remarks>
/// The policy only decides whether a call still needs a person. It never
/// replaces the frozen tool manifest, the resource envelope, the approval
/// binding checks, or the durable approval state machine.
/// </remarks>
public sealed record ToolApprovalPolicy
{
    /// <summary>Bumped when the policy semantics change; written into auto decisions and events.</summary>
    public const string PolicyVersion = "tool-approval-v1";

    /// <summary>
    /// Tools that always wait for a person, whatever the risk and permission
    /// mode (a frozen <c>full-access</c> run is the one exception). Delete tools
    /// are refused by suffix as well, whether or not they appear here.
    /// Browser act tools are here because they change pages the user is watching;
    /// the read-only snapshot/wait/tabs-list tools are not.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultHumanOnlyTools =
    [
        "git_push", "command_run", "shell", "git_worktree_remove", "mcp_invoke",
        "browser_navigate", "browser_click", "browser_type", "browser_press", "browser_scroll",
        "browser_history", "browser_tab_open", "browser_tab_select", "browser_tab_close", "browser_batch",
    ];

    /// <summary>
    /// Tools a delegated gate may never decide on its own, even when they are
    /// not human-only. Egress and browser interactions stay with the person in
    /// delegated modes.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDelegationBlockedTools =
    [
        "web_fetch", "mcp_invoke",
        "browser_navigate", "browser_click", "browser_type", "browser_press", "browser_scroll",
        "browser_history", "browser_tab_open", "browser_tab_select", "browser_tab_close", "browser_batch",
    ];

    public static ToolApprovalPolicy Default { get; } = new();

    /// <summary>Master switch for the auto-approve permission mode.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Highest risk rank the auto policy may release without a person.</summary>
    public string RiskMax { get; init; } = "low";

    /// <summary>Auto approvals minted by policy per run before the call parks for a person.</summary>
    public int MaxPerRun { get; init; } = 32;

    /// <summary>
    /// Release read-only claims and read-only approvals in the ask/default
    /// family without a person. Mutating calls stay gated.
    /// </summary>
    public bool ReleaseReadOnlyInAskMode { get; init; } = true;

    /// <summary>Highest risk a delegated approval gate may decide.</summary>
    public string DelegatedRiskMax { get; init; } = "medium";

    public IReadOnlyList<string> HumanOnlyTools { get; init; } = DefaultHumanOnlyTools;

    public IReadOnlyList<string> DelegationBlockedTools { get; init; } = DefaultDelegationBlockedTools;

    public bool IsHumanOnlyTool(string? toolId) =>
        !string.IsNullOrWhiteSpace(toolId)
        && (HumanOnlyTools.Contains(toolId, StringComparer.OrdinalIgnoreCase)
            || toolId.EndsWith("_delete", StringComparison.OrdinalIgnoreCase)
            || toolId.StartsWith("delete_", StringComparison.OrdinalIgnoreCase));

    public bool IsDelegationBlocked(string? toolId) =>
        !string.IsNullOrWhiteSpace(toolId)
        && DelegationBlockedTools.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    public static void Validate(ToolApprovalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsKnownRisk(policy.RiskMax))
        {
            throw new InvalidDataException(
                $"approval.risk_max must be one of low, medium, elevated, high, critical (got '{policy.RiskMax}').");
        }
        if (!IsKnownRisk(policy.DelegatedRiskMax))
        {
            throw new InvalidDataException(
                $"approval.delegated_risk_max must be one of low, medium, elevated, high, critical (got '{policy.DelegatedRiskMax}').");
        }
        if (policy.MaxPerRun is < 0 or > 1000)
        {
            throw new InvalidDataException("approval.max_per_run must be between 0 and 1000.");
        }
        ValidateToolIds(policy.HumanOnlyTools, "approval.human_only_tools");
        ValidateToolIds(policy.DelegationBlockedTools, "approval.delegation_blocked_tools");
    }

    public static int RiskRank(string? risk) => risk?.Trim().ToLowerInvariant() switch
    {
        "low" => 0,
        "medium" => 1,
        "elevated" => 2,
        "high" => 3,
        "critical" => 4,
        _ => int.MaxValue
    };

    private static bool IsKnownRisk(string? risk) => RiskRank(risk) != int.MaxValue;

    private static void ValidateToolIds(IReadOnlyList<string> tools, string key)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count > 100)
        {
            throw new InvalidDataException($"{key} must not contain more than 100 entries.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool) || tool.Length > 128 || tool == "*")
            {
                throw new InvalidDataException($"{key} must contain explicit tool ids, not empty entries or '*'.");
            }
            if (!seen.Add(tool))
            {
                throw new InvalidDataException($"{key} contains the duplicate tool id '{tool}'.");
            }
        }
    }
}
