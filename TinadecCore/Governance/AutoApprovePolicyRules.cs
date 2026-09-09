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
