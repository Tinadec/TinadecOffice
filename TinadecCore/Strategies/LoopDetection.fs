module TinadecCore.Strategies.LoopDetection

open System
open System.Collections.Generic

/// <summary>
/// Detects repeated tool call fingerprints.
/// Returns true if the same fingerprint appears 3 or more times consecutively.
/// Pure function; no side effects.
/// </summary>
let detectRepeatCalls (fingerprints: IReadOnlyList<string>) : bool =
    if isNull fingerprints || fingerprints.Count < 3 then
        false
    else
        let last = fingerprints.[fingerprints.Count - 1]
        let count =
            fingerprints
            |> Seq.rev
            |> Seq.takeWhile (fun f -> f = last)
            |> Seq.length
        count >= 3

/// <summary>
/// Checks if the iteration count exceeds the maximum.
/// A non-positive maximum means "no round gate" (unlimited rounds), matching the
/// TOML `max_tool_rounds <= 0` semantics; it must not veto on the first round.
/// </summary>
let isOverIterationLimit (iteration: int) (maxIterations: int) : bool =
    maxIterations > 0 && iteration >= maxIterations

/// <summary>
/// Checks if the token budget is exhausted.
/// </summary>
let isTokenBudgetExhausted (tokensUsed: int) (tokenBudget: int) : bool =
    tokenBudget > 0 && tokensUsed >= tokenBudget

/// <summary>
/// Checks if the tool call count exceeds the maximum.
/// A non-positive maximum disables the check instead of vetoing immediately
/// (the previous hard-coded default made an unsupplied budget always fire).
/// </summary>
let isToolCallLimitExceeded (toolCallCount: int) (maxToolCalls: int) : bool =
    maxToolCalls > 0 && toolCallCount >= maxToolCalls

/// <summary>
/// Checks if there are too many consecutive errors.
/// A non-positive maximum disables the check, matching the other budget checks:
/// zero must mean "not enforced", never "veto immediately".
/// </summary>
let hasTooManyConsecutiveErrors (consecutiveErrors: int) (maxConsecutiveErrors: int) : bool =
    maxConsecutiveErrors > 0 && consecutiveErrors >= maxConsecutiveErrors
