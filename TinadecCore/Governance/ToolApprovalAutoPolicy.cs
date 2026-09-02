using Microsoft.Extensions.Options;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Governance;

/// <summary>
/// Governance-side implementation of the auto-approve policy for durable tool
/// approvals. It is a pure decision rule over caller-supplied facts and the
/// frozen options — it never mutates approval state and never touches a store,
/// so the lifecycle coordinator stays the sole writer and a policy that
/// misbehaves can only fail to release a call, never release one that the
/// binding checks would have rejected.
/// </summary>
/// <remarks>
/// The per-run budget is counted from the caller's durable decisions because
/// the tool-approval path persists its own decision rows. The permission-request
/// path in <see cref="GovernanceService"/> keeps its own counter over its own
/// authorization decisions; the two budgets are deliberately separate rather
/// than joined across stores, and both escalate instead of deny when spent.
/// </remarks>
public sealed class ToolApprovalAutoPolicy : IToolApprovalAutoPolicy
{
    private readonly IOptions<AutoApproveOptions> _options;

    public ToolApprovalAutoPolicy(IOptions<AutoApproveOptions> options) => _options = options;

    public Task<ToolApprovalAutoPolicyVerdict> EvaluateAsync(
        ToolApprovalAutoPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (!AutoApprovePolicyRules.Engages(_options.Value, approveIntent: true, context.ToolId, context.Risk))
        {
            return Task.FromResult(new ToolApprovalAutoPolicyVerdict(ToolApprovalAutoPolicyOutcome.NotEngaged));
        }

        var remaining = _options.Value.AutoApproveMaxPerRun - context.AutoPolicyUsesThisRun;
        return Task.FromResult(remaining > 0
            ? new ToolApprovalAutoPolicyVerdict(
                ToolApprovalAutoPolicyOutcome.Approved,
                AutoApproveOptions.PolicyVersion,
                "auto_policy_approved",
                remaining - 1)
            : new ToolApprovalAutoPolicyVerdict(
                ToolApprovalAutoPolicyOutcome.BudgetExhausted,
                AutoApproveOptions.PolicyVersion,
                "auto_policy_budget_exhausted"));
    }
}
