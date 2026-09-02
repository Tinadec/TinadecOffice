namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Why an auto-policy evaluation did not release a tool approval.
/// </summary>
public enum ToolApprovalAutoPolicyOutcome
{
    /// <summary>The policy is off, or this call falls outside its scope. The approval keeps waiting for a human.</summary>
    NotEngaged = 0,

    /// <summary>The policy covers this call and budget remains.</summary>
    Approved = 1,

    /// <summary>The policy covers this call but the per-run budget is spent.</summary>
    BudgetExhausted = 2
}

/// <summary>One tool approval submitted to the auto-approve policy.</summary>
/// <param name="LaneKey">Lane the calling task belongs to; null reads as the implicit "main" lane.</param>
/// <param name="AutoPolicyUsesThisRun">
/// Durable auto-policy decisions already minted for this run on the caller's
/// store. Budget accounting belongs to the caller because each release path
/// persists its own decisions; a pure rule over caller-supplied facts keeps
/// this port free of any store dependency.
/// </param>
public sealed record ToolApprovalAutoPolicyContext(
    string ToolId,
    string Risk,
    Guid RunId,
    string? LaneKey = null,
    string? ParametersHash = null,
    Guid? ExecutionId = null,
    int AutoPolicyUsesThisRun = 0);

public sealed record ToolApprovalAutoPolicyVerdict(
    ToolApprovalAutoPolicyOutcome Outcome,
    string? PolicyVersion = null,
    string? ReasonCode = null,
    int BudgetRemaining = 0)
{
    /// <summary>True only for <see cref="ToolApprovalAutoPolicyOutcome.Approved"/>.</summary>
    public bool Approved => Outcome == ToolApprovalAutoPolicyOutcome.Approved;
}

/// <summary>
/// Third authorization path for durable tool approvals, alongside a frozen
/// full-access run and a pre-authorization grant. It lives in
/// <c>Abstractions/Ports</c> so the lifecycle module can consume it without
/// taking a dependency on the governance module: governance owns the policy,
/// lifecycle owns the approval state machine.
/// </summary>
/// <remarks>
/// Implementations must be pure decision makers — they never mutate approval
/// state. The caller stays the single writer, so a policy that misbehaves can
/// only fail to release a call, never release one that the binding checks would
/// have rejected.
/// </remarks>
public interface IToolApprovalAutoPolicy
{
    Task<ToolApprovalAutoPolicyVerdict> EvaluateAsync(
        ToolApprovalAutoPolicyContext context,
        CancellationToken cancellationToken = default);
}
