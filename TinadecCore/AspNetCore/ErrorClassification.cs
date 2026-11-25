namespace TinadecCore.AspNetCore;

/// <summary>
/// The product-wide error contract carried by every problem response.
///
/// A status code and a message tell a reader that something failed; they do not say whether
/// retrying helps, whether the person can fix it, or which control to touch. Every failure the
/// API reports is therefore classified on two axes — who can resolve it, and whether repeating
/// the request could succeed — and may name the recovery actions a client can offer.
///
/// Categories are a closed set so a client can branch on them without tracking individual codes:
/// <list type="bullet">
/// <item><c>user_action_required</c> — a person can resolve it (edit configuration, pick a
/// folder, sign in). Retrying the identical request will fail the same way.</item>
/// <item><c>retryable</c> — the same request may succeed later (a lease is held, a peer is
/// busy, a transient conflict).</item>
/// <item><c>environment_unavailable</c> — a dependency this machine needs is missing or
/// unreachable (a registered folder was deleted, a tool is not installed, the backend is
/// down). A person usually resolves it outside the failing request.</item>
/// <item><c>internal</c> — an unexpected defect. Always reportable, never user-fixable.</item>
/// </list>
///
/// Actions are stable identifiers, not sentences: the client owns the wording and the
/// behaviour, so a localized UI never depends on a server-side translation.
/// </summary>
public static class ErrorClassification
{
    public const string UserActionRequired = "user_action_required";
    public const string Retryable = "retryable";
    public const string EnvironmentUnavailable = "environment_unavailable";
    public const string Internal = "internal";

    /// <summary>Stable recovery hints a client may offer; unknown kinds must be ignored.</summary>
    public static class ActionKind
    {
        public const string Retry = "retry";
        public const string Reload = "reload";
        public const string OpenSettings = "open_settings";
        public const string OpenStorageSettings = "open_storage_settings";
        public const string OpenToolSettings = "open_tool_settings";
        public const string UnregisterWorkspace = "unregister_workspace";
        public const string ChooseFolder = "choose_folder";
    }

    public sealed record Classification(string Category, bool Retryable, IReadOnlyList<string> Actions);

    /// <summary>
    /// Classifies a refusal by its stable code, falling back to the HTTP status when the code is
    /// not yet modelled. An unknown 4xx is user-action-shaped (the request was refused) and an
    /// unknown 5xx is internal, which is the honest default in both directions.
    /// </summary>
    public static Classification Classify(string? code, int status)
    {
        var value = code ?? string.Empty;
        return value switch
        {
            // A registered workspace whose folder is gone cannot be repaired by retrying it, but
            // it must never be a dead end: unregister is the escape hatch, and the settings page
            // is where storage roots are chosen.
            "storage_scope_unavailable" => new(EnvironmentUnavailable, false,
                [ActionKind.UnregisterWorkspace, ActionKind.Retry, ActionKind.OpenStorageSettings]),

            "storage_scope_not_found" => new(UserActionRequired, false, [ActionKind.Reload, ActionKind.OpenStorageSettings]),
            "storage_access_denied" => new(EnvironmentUnavailable, false, [ActionKind.OpenStorageSettings]),
            "storage_io_error" or "storage_timeout" => new(EnvironmentUnavailable, true, [ActionKind.Retry, ActionKind.OpenStorageSettings]),
            "storage_database_error" => new(Internal, true, [ActionKind.Retry, ActionKind.OpenStorageSettings]),
            "storage_database_conflict" => new(Retryable, true, [ActionKind.Reload]),
            "workspace_authorization_required" => new(UserActionRequired, false, [ActionKind.OpenStorageSettings]),
            "workspace_root_not_registered" or "workspace_root_required" or "workspace_root_unavailable"
                => new(UserActionRequired, false, [ActionKind.ChooseFolder]),

            // Configuration is the TOML the person edits; a bad file is theirs to fix.
            "configuration_invalid" or "configuration_missing" or "configuration_link_rejected"
                or "configuration_scope_mismatch" or "configuration_projection_invalid"
                or "configuration_source_reference" or "configuration_unique_filter_unsupported"
                or "preferences_invalid"
                => new(UserActionRequired, false, [ActionKind.OpenSettings, ActionKind.Reload]),
            // A concurrent editor won the write; re-reading and re-applying is the fix.
            "configuration_conflict" or "configuration_changed_during_admission" or "preferences_conflict"
                => new(Retryable, true, [ActionKind.Reload]),
            "configuration_restart_required" => new(UserActionRequired, false, [ActionKind.OpenSettings]),

            "model_not_configured" or "tool_runtime_not_configured" or "tool_provider_not_configured"
                or "agent_mode_not_configured" => new(UserActionRequired, false, [ActionKind.OpenSettings]),

            // Installed but unusable right now: the person may fix the host, and a retry can help
            // once it is back.
            "tool_provider_unavailable" or "capability_unavailable" or "run_coordinator_unavailable"
                or "validator_unavailable" or "target_configuration_unavailable"
                => new(EnvironmentUnavailable, true, [ActionKind.Retry, ActionKind.OpenToolSettings]),

            "forbidden" or "unauthorized" or "host_authorization_required" or "host_identity_unavailable"
                or "host_control_unbound" => new(UserActionRequired, false, []),

            "rate_limited" => new(Retryable, true, [ActionKind.Retry]),
            "context_conflict" or "session_settings_conflict" or "workspace_conflict"
                or "queue_owner_changed" => new(Retryable, true, [ActionKind.Reload]),

            "active_run_conflict" or "run_terminal" or "mode_unavailable" or "invalid_lifecycle_transition"
                or "active_run_limit" => new(UserActionRequired, false, [ActionKind.Reload]),

            "internal_error" => new(Internal, true, [ActionKind.Retry]),
            _ => status >= 500
                ? new(Internal, true, [ActionKind.Retry])
                : new(UserActionRequired, false, [])
        };
    }
}
