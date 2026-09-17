namespace TinadecCore.DmaEA;

/// <summary>
/// Pure spawn authority for declared-graph tiers: the conversation identity may
/// only spawn workers from the frozen spawnable-template whitelist, and only in
/// the tiers that carry spawn authority at all (self_dispatch / free_form).
/// deterministic is topology-welded — any spawn intent is denied and surfaces as
/// graph_tier_spawn_denied at the dispatch site. Deliberately static pure
/// functions so the negative cases are unit-tested directly.
/// </summary>
public static class GraphSpawnAuthority
{
    public static bool IsSpawnAllowed(FrozenGraph? graph, string? targetSlug)
    {
        if (graph is null || string.IsNullOrWhiteSpace(targetSlug)) return false;
        if (!CarriesSpawnAuthority(graph.Tier)) return false;
        return graph.SpawnableTemplates.Any(template =>
            string.Equals(template.Slug, targetSlug, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Picks the spawnable template covering a task the frozen roster cannot
    /// satisfy, in a tier that carries spawn authority. A wildcard tool ceiling
    /// covers any requirement; otherwise the template's frozen ceiling must cover
    /// every required tool and its capabilities every required capability.
    /// Closest fit wins (fewest extra tools, then slug order) so the dispatch
    /// stays deterministic.
    /// </summary>
    public static FrozenSpawnableTemplate? SelectSpawnable(
        FrozenGraph? graph,
        IReadOnlyList<string> requiredTools,
        IReadOnlyList<string> requiredCapabilities)
    {
        if (graph is null || !CarriesSpawnAuthority(graph.Tier)) return null;
        return FindCoverage(graph, requiredTools, requiredCapabilities);
    }

    /// <summary>
    /// Tier-blind coverage lookup: which spawnable template COULD build this
    /// task, regardless of whether the tier allows spawning at all. The dispatch
    /// site uses the distinction to tell "nothing could ever cover this"
    /// (worker_unavailable) apart from "a template fits but the tier forbids
    /// spawn" (graph_tier_spawn_denied).
    /// </summary>
    public static FrozenSpawnableTemplate? FindCoverage(
        FrozenGraph? graph,
        IReadOnlyList<string> requiredTools,
        IReadOnlyList<string> requiredCapabilities)
    {
        if (graph is null || graph.SpawnableTemplates.Count == 0) return null;
        var tools = requiredTools
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capabilities = requiredCapabilities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covering = graph.SpawnableTemplates
            .Where(template => Covers(template.ToolCeiling, tools) && Covers(template.Capabilities, capabilities))
            .ToArray();
        if (covering.Length == 0) return null;

        // A task that DECLARED requirements is already covered; among the templates
        // that cover it, the narrowest ceiling is the least-privilege choice.
        if (tools.Count > 0 || capabilities.Count > 0)
        {
            return covering
                .OrderBy(template => template.ToolCeiling.Contains("*", StringComparer.Ordinal) ? int.MaxValue : template.ToolCeiling.Count)
                .ThenBy(template => template.Slug, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        // An open-ended task declares nothing to narrow against. "Fewest tools" then
        // picks the NARROWEST template, which is how a goal that needed to write a
        // file was handed to the read-only search worker — verified in a real run
        // whose worker then reported it had no way to do the job while the run closed
        // as completed. Breadth is the only signal left, and it is the honest one:
        // the widest ceiling is the template that can actually attempt an open goal.
        // Deterministic: widest first, then slug order.
        return covering
            .OrderByDescending(template => template.ToolCeiling.Contains("*", StringComparer.Ordinal))
            .ThenByDescending(template => template.ToolCeiling.Count)
            .ThenByDescending(template => template.Capabilities.Count)
            .ThenBy(template => template.Slug, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Tiers whose conversation identity may build workers from the frozen spawnable
    /// whitelist. solo_dispatch keeps spawn authority even though the master executes
    /// itself: "更积极地派子智能体" is the point of that tier, so it must be able to
    /// hand work off, not only do it.
    /// </summary>
    internal static bool CarriesSpawnAuthority(string tier) =>
        string.Equals(tier, FrozenGraphTiers.SelfDispatch, StringComparison.Ordinal)
        || string.Equals(tier, FrozenGraphTiers.FreeForm, StringComparison.Ordinal)
        || string.Equals(tier, FrozenGraphTiers.SoloDispatch, StringComparison.Ordinal);

    private static bool Covers(IReadOnlyList<string> ceiling, HashSet<string> required) =>
        ceiling.Contains("*", StringComparer.Ordinal) || required.All(required0 => ceiling.Contains(required0, StringComparer.OrdinalIgnoreCase));
}
