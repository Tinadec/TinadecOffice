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
/// </summary>
let isOverIterationLimit (iteration: int) (maxIterations: int) : bool =
    iteration >= maxIterations

/// <summary>
/// Checks if the token budget is exhausted.
/// </summary>
let isTokenBudgetExhausted (tokensUsed: int) (tokenBudget: int) : bool =
    tokenBudget > 0 && tokensUsed >= tokenBudget

/// <summary>
/// Checks if the tool call count exceeds the maximum.
/// </summary>
let isToolCallLimitExceeded (toolCallCount: int) (maxToolCalls: int) : bool =
    toolCallCount >= maxToolCalls

/// <summary>
/// Checks if there are too many consecutive errors.
/// </summary>
let hasTooManyConsecutiveErrors (consecutiveErrors: int) (maxConsecutiveErrors: int) : bool =
    consecutiveErrors >= maxConsecutiveErrors
