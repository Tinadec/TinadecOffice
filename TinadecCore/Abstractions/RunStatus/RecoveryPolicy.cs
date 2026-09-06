namespace TinadecCore.Abstractions;

/// <summary>
/// Single source of truth for recovery semantics (plan §4.3 item 5). One policy
/// home for the four former rule sets: startup orphan recovery, the engine lease
/// scan, user-tool-action recovery, and decision-time approval park expiry.
/// Every recovery path must protect the awaiting_* decision states, must not
/// touch terminal states, and must respect the admission grace window so a run
/// that is still freezing its configuration is never "recovered" mid-admission.
/// </summary>
public static class RecoveryPolicy
{
    /// <summary>
    /// Runs parked on an external decision (human or delegate agent). They are
    /// durable waiting points, not orphaned work: a host restart must leave them
    /// untouched and resume them only after an explicit authorization fact.
    /// </summary>
    public static readonly IReadOnlySet<string> AwaitingDecisionStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "awaiting_approval", "awaiting_delegate", "awaiting_user"
        };

    /// <summary>
    /// A run younger than this is still being admitted (configuration freeze +
    /// tool manifest handshake); recovery scans must skip it.
    /// </summary>
    public const int AdmissionGraceSeconds = 30;

    /// <summary>Terminal and awaiting states are protected from recovery actions.</summary>
    public static bool IsProtected(string status) =>
        AwaitingDecisionStatuses.Contains(status) || RunStatusMachine.IsTerminal(status);

    /// <summary>Enum-shaped twin of <see cref="IsProtected(string)"/> for typed run records.</summary>
    public static bool IsProtected(Ports.RunStatus status) =>
        IsAwaitingDecision(status)
        || status is Ports.RunStatus.Completed or Ports.RunStatus.Failed or Ports.RunStatus.Cancelled;

    public static bool IsAwaitingDecision(string status) => AwaitingDecisionStatuses.Contains(status);

    /// <summary>Enum-shaped twin of <see cref="IsAwaitingDecision(string)"/>.</summary>
    public static bool IsAwaitingDecision(Ports.RunStatus status) =>
        status is Ports.RunStatus.AwaitingApproval or Ports.RunStatus.AwaitingDelegate or Ports.RunStatus.AwaitingUser;

    public static bool IsAdmissionGraceElapsed(DateTimeOffset createdAt, DateTimeOffset now) =>
        createdAt <= now.AddSeconds(-AdmissionGraceSeconds);
}
