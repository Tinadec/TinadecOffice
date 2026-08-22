namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Durable user-originated tool transport. User actions intentionally do not
/// fabricate a run, task, or agent instance, but use the same Core governance
/// state machines and provider boundary as autonomous dispatch.
/// </summary>
public interface IUserToolActionService
{
    Task<UserToolActionResult> CreateAsync(UserToolActionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserToolActionResult>> ListAsync(string? status = null, CancellationToken cancellationToken = default);
    Task<UserToolActionResult?> GetAsync(Guid actionId, CancellationToken cancellationToken = default);
    Task<UserToolActionResult> ResumeAsync(Guid actionId, CancellationToken cancellationToken = default);
    Task<UserToolActionResult> OverrideSnapshotAsync(Guid actionId, string reason, CancellationToken cancellationToken = default);
}

public sealed record UserToolActionRequest(
    Guid ProjectId,
    string ToolId,
    string ParametersJson,
    string? IdempotencyKey = null);

public sealed record UserToolActionResult(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid ProjectId,
    Guid PrincipalId,
    string ToolId,
    string Status,
    string Risk,
    bool MutatesWorkspace,
    bool RequiresApproval,
    Guid? PermissionRequestId,
    Guid? AuthorizationDecisionId,
    Guid? ActionApprovalId,
    Guid? SnapshotId,
    string? SnapshotHash,
    string? ResultJson,
    string? ErrorCategory,
    string? Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public static class UserToolActionStatuses
{
    public const string SnapshotRequired = "snapshot_required";
    public const string AwaitingDelegate = "awaiting_delegate";
    public const string AwaitingUser = "awaiting_user";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Blocked = "blocked";
    public const string OutcomeUnknown = "outcome_unknown";
}
