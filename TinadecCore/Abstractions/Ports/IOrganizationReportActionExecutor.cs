namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// A governance report action is a proposal until this port executes it. The
/// communication module owns report membership and revision checks; the runtime
/// owns the actual operation and its domain authorization.
/// </summary>
public sealed record OrganizationReportActionRequest(
    TenantContext Scope,
    Guid SessionId,
    Guid OrganizationId,
    Guid ReportId,
    Guid ActorId,
    string Verb,
    string? ArgumentsJson,
    string? SubjectKind,
    string? SubjectId,
    string IdempotencyKey);

/// <summary>Outcome of one governance action attempt.</summary>
public sealed record OrganizationReportActionResult(
    bool Completed,
    string Status,
    string Code,
    string Message,
    Guid? TargetRunId = null,
    string? TargetStatus = null);

/// <summary>
/// Runs an action proposed by an organization report. Implementations must be
/// idempotent for the supplied key and must fail closed when the target is outside
/// the report's session or tenant boundary.
/// </summary>
public interface IOrganizationReportActionExecutor
{
    Task<OrganizationReportActionResult> ExecuteAsync(
        OrganizationReportActionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Runtime-neutral run control used by the governance action executor.</summary>
public sealed record GovernanceRunControlResult(
    bool Accepted,
    string Status,
    string Action,
    string Code = "",
    string Message = "",
    Guid? RunId = null);

public interface IGovernanceRunController
{
    Task<GovernanceRunControlResult> ControlAsync(
        TenantContext scope,
        Guid sessionId,
        Guid runId,
        string action,
        string clientControlId,
        CancellationToken cancellationToken = default);
}
