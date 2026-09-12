namespace TinadecCore.Tools;

/// <summary>
/// Named decision step for resource-grant evaluation (S9 Phase 1: refactor-only —
/// behavior identical to the historical boolean predicate, seeds still pass
/// ["workspace"]). Shared by the invocation-scope resolver and the Core-owned
/// virtual tools. The decision records WHY a path was allowed or denied:
/// <see cref="ResourceAllowBasis.UnrestrictedEmptyGrant"/> and
/// <see cref="ResourceAllowBasis.CoarseToken"/> are the two historical allow
/// shortcuts; otherwise the candidate path must equal, or be nested under, a
/// granted path.
///
/// Phase 1 deliberately does NOT change behavior: the six engine seeds still pass
/// ["workspace"], so the runtime resource envelope is still the coarse token.
/// Replacing seeds with per-instance grants (resource envelope takeover) is
/// Phase 2 work, batched with the R5 frozen-hash changes.
/// </summary>
internal static class ToolResourceAllowList
{
    /// <summary>Evaluate a candidate path against a declared resource grant list.</summary>
    public static ResourceAllowDecision Evaluate(IReadOnlyList<string> resources, string path)
    {
        // Historical behaviour: an empty list means no restriction was declared.
        if (resources.Count == 0)
            return new ResourceAllowDecision(true, ResourceAllowBasis.UnrestrictedEmptyGrant, null);

        var normalizedPath = TryNormalize(path);
        foreach (var resource in resources)
        {
            if (string.Equals(resource, "workspace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(resource, "project", StringComparison.OrdinalIgnoreCase))
            {
                return new ResourceAllowDecision(true, ResourceAllowBasis.CoarseToken, resource);
            }
            var root = TryNormalize(resource);
            if (root is null || normalizedPath is null) continue;
            if (string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new ResourceAllowDecision(true, ResourceAllowBasis.PathMatch, resource);
            }
        }

        return new ResourceAllowDecision(false, ResourceAllowBasis.Denied, null);
    }

    /// <summary>Boolean projection kept for the existing call sites.</summary>
    public static bool IsAllowed(IReadOnlyList<string> resources, string path) => Evaluate(resources, path).Allowed;

    private static string? TryNormalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            // A malformed grant/path is simply not an authorization match.
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

internal enum ResourceAllowBasis
{
    UnrestrictedEmptyGrant,
    CoarseToken,
    PathMatch,
    Denied
}

internal sealed record ResourceAllowDecision(bool Allowed, ResourceAllowBasis Basis, string? MatchedGrant);
