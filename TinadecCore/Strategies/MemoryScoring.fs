module TinadecCore.Strategies.MemoryScoring

open System
open System.Collections.Generic
open TinadecCore.Abstractions.Ports

/// <summary>
/// Computes a simple relevance score for memory entries based on keyword overlap.
/// Pure function; no side effects.
/// </summary>
let scoreEntries (query: string) (entries: IReadOnlyList<MemoryEntry>) : (string * float)[] =
    if String.IsNullOrEmpty(query) || isNull entries then
        Array.empty
    else
        let queryTerms =
            query.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.ToLowerInvariant())
            |> Set.ofArray

        entries
        |> Seq.map (fun e ->
            let contentTerms =
                e.Content.ToLowerInvariant()
                    .Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                |> Set.ofArray

            let overlap = Set.intersect queryTerms contentTerms |> Set.count
            let score =
                if queryTerms.IsEmpty then 0.0
                else float overlap / float queryTerms.Count
            (e.Id, score))
        |> Seq.toArray

/// <summary>
/// Filters and sorts entries by score, returning at most maxResults.
/// </summary>
let topEntries (maxResults: int) (scored: (string * float)[]) : (string * float)[] =
    if isNull scored || scored.Length = 0 then
        Array.empty
    else
        scored
        |> Seq.filter (fun (_, s) -> s > 0.0)
        |> Seq.sortByDescending (fun (_, s) -> s)
        |> Seq.truncate (max 1 maxResults)
        |> Seq.toArray
