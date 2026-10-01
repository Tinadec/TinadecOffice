namespace TinadecCore.Governance;

/// <summary>
/// The auto-approve decision rule, shared by the permission-request path in
/// <see cref="GovernanceService"/> and the tool-approval path in
/// <see cref="ToolApprovalAutoPolicy"/>. Both must reach the same verdict for
/// the same call, so the rule exists exactly once.
/// </summary>
internal static class AutoApprovePolicyRules
{
    /// <summary>
    /// Risk ordering used by the auto-approve policy. Rankings are compared
    /// against a configured ceiling, so only the relative order matters; every
    /// value the policy does not explicitly recognize sorts above every ceiling
    /// and can never be auto-approved. The ordering is the shared vocabulary:
    /// low &lt; medium &lt; elevated &lt; high &lt; critical.
    /// </summary>
    /// <remarks>
    /// <c>elevated</c> is part of the shared vocabulary but is still never
    /// auto-approved at any ceiling (M8 invariant): <see cref="Engages"/> refuses
    /// it explicitly instead of relying on it sorting above the ceiling.
    /// </remarks>
    public static int RiskRank(string? risk) => risk?.Trim().ToLowerInvariant() switch
    {
        "low" => 0,
        "medium" => 1,
        "elevated" => 2,
        "high" => 3,
        "critical" => 4,
        _ => int.MaxValue
    };

    /// <summary>
    /// True when the policy may decide this call at all. A false result means
    /// the policy abstains and the request follows its normal path — it is
    /// never a denial.
    /// </summary>
    public static bool Engages(AutoApproveOptions options, bool approveIntent, string? toolId, string? risk) =>
        options.AutoApproveEnabled
        && approveIntent
        && !(toolId is not null && options.IsHumanOnlyTool(toolId))
        && !string.Equals(risk?.Trim(), "elevated", StringComparison.OrdinalIgnoreCase)
        && RiskRank(risk) <= RiskRank(options.AutoApproveRiskMax);
}

/// <summary>
/// Which calls a delegated approval gate may decide at all. One rule, read by the PDP (which releases a
/// delegable mutating claim to the approval layer instead of parking it for the person) and by the
/// gate service (which refuses to decide anything else even if it reached the approval layer another
/// way), so the two can never disagree about what stays with the human.
/// </summary>
public static class DelegatedApprovalRules
{
    public static bool Delegable(AutoApproveOptions options, string? toolId, string? risk, bool optedIn = false) =>
        !string.IsNullOrWhiteSpace(toolId)
        && (!options.IsHumanOnlyTool(toolId)
            || (optedIn && Abstractions.Ports.DelegatedToolOptInRules.IsEligibleTool(toolId)))
        // Core's own virtual tools with an approval (create_workspace) keep the person's click: their
        // approval gate is their whole safety net.
        && !Abstractions.Ports.CoreVirtualToolPolicy.IsCoreVirtual(toolId)
        // An opt-in moves a low/medium shell call to the judge, never the risk ceiling.
        // Unknown ceilings must not sort above unknown risks and accidentally admit them.
        && AutoApprovePolicyRules.RiskRank(risk) <= 1
        && AutoApprovePolicyRules.RiskRank(options.DelegatedApprovalRiskMax) != int.MaxValue
        && AutoApprovePolicyRules.RiskRank(risk) <= AutoApprovePolicyRules.RiskRank(options.DelegatedApprovalRiskMax);
}
