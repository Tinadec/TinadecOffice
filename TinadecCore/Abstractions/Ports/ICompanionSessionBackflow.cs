namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Summary of a pending tool approval inside a work session run so Companion surfaces
/// can render actionable inline approval cards without polling or importing DmaEA/Governance.
/// </summary>
public sealed record CompanionPendingApprovalItem(
    Guid ApprovalId,
    string ToolId,
    string? Summary,
    string? RiskLevel,
    DateTimeOffset CreatedAt);

/// <summary>
/// Event-driven transition payload emitted when a work session run reaches a milestone
/// that a commanding Companion (or connected Companion UI) cares about.
/// <paramref name="Status"/> is one of <c>"awaiting_approval"</c>, <c>"completed"</c>,
/// <c>"failed"</c>, or <c>"browser_handoff"</c>.
/// </summary>
public sealed record CompanionSessionBackflowEvent(
    Guid TenantId,
    Guid? WorkspaceId,
    Guid? ProjectId,
    Guid SessionId,
    string SessionTitle,
    Guid RunId,
    string Status,
    string? ModeName,
    string? Summary,
    string? PlanSummary,
    IReadOnlyList<CompanionPendingApprovalItem>? PendingApprovals = null);

/// <summary>
/// Cross-module port allowing the execution engine and work gateway to notify the Companion
/// command hub about session milestones (<c>awaiting_approval</c>, <c>completed</c>, <c>failed</c>)
/// without direct module references between DmaEA and Companion.
/// </summary>
public interface ICompanionSessionBackflow
{
    void TrackCommandedSession(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        Guid sessionId,
        Guid? runId = null);

    Task NotifySessionTransitionAsync(
        CompanionSessionBackflowEvent transition,
        CancellationToken cancellationToken = default);
}
