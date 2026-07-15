module TinadecCore.Strategies.PromptSelection

open System
open System.Collections.Generic

/// <summary>
/// Prompt fragment for selection. C#-compatible record.
/// </summary>
[<CLIMutable>]
type PromptFragment =
    {
        Id: string
        Priority: int
        EstimatedTokens: int
        Enabled: bool
    }

/// <summary>
/// Selects prompt fragments that fit within the token budget, ordered by priority (descending).
/// Pure function; no side effects.
/// </summary>
let selectFragments (tokenBudget: int) (fragments: IReadOnlyList<PromptFragment>) : PromptFragment[] =
    if isNull fragments then
        Array.empty
    else
        fragments
        |> Seq.filter (fun f -> f.Enabled)
        |> Seq.sortByDescending (fun f -> f.Priority)
        |> Seq.fold (fun (acc: PromptFragment list, remaining) (f: PromptFragment) ->
            if remaining >= f.EstimatedTokens then
                (f :: acc, remaining - f.EstimatedTokens)
            else
                (acc, remaining))
            ([], tokenBudget)
        |> fst
        |> List.rev
        |> List.toArray
