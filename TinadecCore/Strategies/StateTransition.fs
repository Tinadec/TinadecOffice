module TinadecCore.Strategies.StateTransition

open System

/// <summary>
/// Validates whether a state transition is allowed.
/// Returns (isValid, reason).
/// Pure function; no side effects.
/// </summary>
let validateTransition (currentState: string) (targetState: string) : bool * string =
    match currentState, targetState with
    | _, _ when String.IsNullOrEmpty(currentState) || String.IsNullOrEmpty(targetState) ->
        (false, "State cannot be null or empty")
    | a, b when a = b ->
        (false, "Target state is the same as current state")
    | "planning", "understanding"
    | "planning", "executing"
    | "understanding", "executing"
    | "understanding", "replanning"
    | "understanding", "awaiting_approval"
    | "executing", "replanning"
    | "executing", "awaiting_approval"
    | "executing", "reviewing"
    | "executing", "completed"
    | "reviewing", "executing"
    | "reviewing", "completed"
    | "replanning", "executing"
    | "awaiting_approval", "executing"
    | "awaiting_approval", "replanning"
    | "paused", "executing"
    // Bulk storage compat: some raw rows still store "pending"/"running" alias.
    | "planning", "pending"
    | "pending", "planning"
    | "pending", "running"
    | "running", "completed"
    | "running", "failed"
    | "running", "cancelled"
    | "pending", "cancelled"
    | "failed", "pending"
    | "cancelled", "pending"
    | _, "paused"
    | _, "failed"
    | _, "cancelled" ->
        (true, "")
    | _ ->
        (false, $"Transition from '{currentState}' to '{targetState}' is not allowed")
