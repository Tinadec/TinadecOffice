namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The facts a governance-layer role can subscribe to in its relationship file (<c>subscriptions</c>).
///
/// This list is the engine's vocabulary and is deliberately short. Wiring a role to any of these is
/// the mode author's free choice — the topology is theirs — but every topic here has an emission site
/// in the run engine, so a subscription can never name a fact nobody ever raises. Adding a topic means
/// adding an emission site in the same change; a topic without one would be a promise the engine does
/// not keep, and the role subscribed to it would simply never wake.
///
/// The first four names are the full-duplex trigger vocabulary the TOML baseline already uses
/// (<c>compress_on_task_closed</c>, <c>recommend_on_task_created</c>, …): one vocabulary for declared
/// and derived wiring, not two.
/// </summary>
public static class GovernanceTopics
{
    /// <summary>A task graph was materialized (first planning or a replan).</summary>
    public const string TaskCreated = "task_created";

    /// <summary>A task reached a terminal result (completed, failed or blocked).</summary>
    public const string TaskClosed = "task_closed";

    /// <summary>The run finalized.</summary>
    public const string RunClosed = "run_closed";

    /// <summary>No executor could take a task's required capabilities or tools.</summary>
    public const string CapabilityMissing = "capability_missing";

    /// <summary>A tool call was refused because another run holds the resource it would touch.</summary>
    public const string LeaseConflict = "lease_conflict";

    /// <summary>A tool call parked on an approval gate.</summary>
    public const string ApprovalRequested = "approval_requested";

    public static readonly IReadOnlyList<string> All =
    [
        TaskCreated, TaskClosed, RunClosed, CapabilityMissing, LeaseConflict, ApprovalRequested
    ];

    public static bool IsKnown(string? topic) =>
        !string.IsNullOrWhiteSpace(topic) && All.Contains(topic.Trim(), StringComparer.OrdinalIgnoreCase);
}
