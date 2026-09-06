module TinadecCore.Strategies.StateTransition

open System
open TinadecCore.Abstractions

/// <summary>
/// Validates whether a run status transition is allowed.
/// Returns (isValid, reason). Pure function; no side effects.
///
/// Delegates to the shared RunStatusMachine in TinadecCore.Abstractions (plan §3.3
/// item 3): one transition table for the C# lifecycle services and the F# strategy
/// layer. The legacy local table (terminal revival via failed/cancelled → pending,
/// pending/running aliases, missing awaiting_* rules, wildcard → paused) is retired.
/// </summary>
let validateTransition (currentState: string) (targetState: string) : bool * string =
    if String.IsNullOrEmpty(currentState) || String.IsNullOrEmpty(targetState) then
        (false, "State cannot be null or empty")
    elif currentState.Equals(targetState, StringComparison.OrdinalIgnoreCase) then
        (false, "Target state is the same as current state")
    elif RunStatusMachine.CanTransition(currentState, targetState) then
        (true, "")
    else
        (false, $"Transition from '{currentState}' to '{targetState}' is not allowed")
