namespace TinadecCore.Abstractions;

/// <summary>
/// Single source of truth for durable run error_category values (plan §3.3 item 5).
/// Producers must use these constants instead of ad-hoc string literals; problem
/// details, events, and Desktop presentation key off this taxonomy. The categories
/// that the ad-hoc literals never covered (config / model / protocol) are
/// first-class here so runtime failures can be classified instead of collapsing
/// into "runtime".
/// </summary>
public static class RunErrorTaxonomy
{
    // ── Configuration / model / protocol (previously missing categories) ──
    /// <summary>Provider, route, or workspace misconfiguration discovered at run time.</summary>
    public const string Config = "config";
    /// <summary>Model invocation failed after the route resolved successfully.</summary>
    public const string Model = "model";
    /// <summary>Contract violation between Core and an adapter (unexpected payload/stream shape).</summary>
    public const string Protocol = "protocol";

    // ── Runtime / orchestration ──
    public const string Runtime = "runtime";
    public const string WorkerUnavailable = "worker_unavailable";
    public const string Cancelled = "run_cancelled";

    // ── Tools ──
    public const string ToolError = "tool_error";
    public const string ToolTimeout = "timeout";
    public const string ToolProcessExit = "process_exit";
    public const string ToolPrepareFailed = "tool_prepare_failed";
    public const string ToolManifestChanged = "manifest_changed";
    public const string ToolBlocked = "blocked";
    public const string ToolAlreadyRunning = "already_running";
    /// <summary>The provider transport failed before the call was delivered (handshake/startup failure); the call was never sent.</summary>
    public const string ToolRuntimeUnavailable = "tool_runtime_unavailable";

    // ── Approval ──
    public const string ApprovalMissing = "approval_missing";
    public const string ApprovalExpired = "approval_expired";
    public const string ApprovalBindingMismatch = "approval_binding_mismatch";
    public const string NotApproved = "not_approved";
    public const string ApprovalConsumedWithoutOutcome = "approval_consumed_without_outcome";

    // ── Recovery ──
    public const string RecoveryFailed = "recovery_failed";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string SnapshotFailed = "snapshot_failed";
    public const string SnapshotOverride = "snapshot_override";
    public const string RecoveryMarkedFailed = "recovery_marked_failed";

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        Config, Model, Protocol, Runtime, WorkerUnavailable, Cancelled,
        ToolError, ToolTimeout, ToolProcessExit, ToolPrepareFailed, ToolManifestChanged, ToolBlocked, ToolAlreadyRunning, ToolRuntimeUnavailable,
        ApprovalMissing, ApprovalExpired, ApprovalBindingMismatch, NotApproved, ApprovalConsumedWithoutOutcome,
        RecoveryFailed, OutcomeUnknown, SnapshotFailed, SnapshotOverride, RecoveryMarkedFailed
    };

    /// <summary>All well-known categories (vocabulary of record).</summary>
    public static IReadOnlyCollection<string> KnownCategories => Known;

    public static bool IsKnown(string? category) => !string.IsNullOrWhiteSpace(category) && Known.Contains(category);
}
