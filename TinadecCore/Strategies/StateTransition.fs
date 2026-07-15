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
    | "pending", "running"
    | "running", "completed"
    | "running", "failed"
    | "running", "cancelled"
    | "pending", "cancelled"
    | "failed", "pending"
    | "cancelled", "pending" ->
        (true, "")
    | _ ->
        (false, $"Transition from '{currentState}' to '{targetState}' is not allowed")
