namespace TinadecCore.Lifecycle;

/// <summary>
/// Durable approval windows. The decision window bounds how long a pending
/// approval waits for a human — expiring it parks the execution instead of
/// failing it, so an unanswered prompt never destroys the durable call. The
/// execution window bounds how long an approved approval stays startable;
/// after it lapses the approval hard-fails so a stale decision can never
/// replay a mutating call.
/// </summary>
public sealed class TinadecApprovalOptions
{
    public const string SectionName = "TinadecApproval";

    public int DecisionWindowMinutes { get; set; } = 30;
    public int ExecutionWindowMinutes { get; set; } = 30;
}
