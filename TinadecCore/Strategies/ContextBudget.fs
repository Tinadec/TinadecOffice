module TinadecCore.Strategies.ContextBudget

open System
open System.Collections.Generic
open TinadecCore.Abstractions.Ports

/// <summary>
/// Allocates token budget across evidence items using proportional weighting.
/// Pure function; no side effects.
/// </summary>
let allocateBudget (totalBudget: int) (evidence: IReadOnlyList<ContextEvidence>) : int[] =
    if isNull evidence || evidence.Count = 0 then
        Array.empty
    else
        let totalEstimated =
            evidence
            |> Seq.sumBy (fun e -> max e.EstimatedTokens 1)

        evidence
        |> Seq.map (fun e ->
            let weight = float (max e.EstimatedTokens 1) / float totalEstimated
            int (float totalBudget * weight))
        |> Seq.toArray

/// <summary>
/// Checks whether the total estimated tokens exceed the budget.
/// </summary>
let isOverBudget (budget: int) (estimatedTokens: int) : bool =
    estimatedTokens > budget

/// <summary>
/// Calculates the utilization ratio (0.0 to 1.0+).
/// </summary>
let utilizationRatio (budget: int) (used: int) : float =
    if budget <= 0 then 1.0 else float used / float budget
