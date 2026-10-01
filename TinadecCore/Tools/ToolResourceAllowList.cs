namespace TinadecCore.Tools;

/// <summary>
/// Named decision step for resource-grant evaluation (WS-4 envelope + WS-8 path
/// prefix enforcement).
///
/// Grants are frozen per instance as level-prefixed, forward-slash,
/// workspace-relative prefixes (<c>read:&lt;prefix&gt;</c> /
/// <c>write:&lt;prefix&gt;</c>; an empty prefix means the whole workspace). Write
/// implies read. An empty grant list is the fail-closed "no workspace
/// authorization" state — never "unrestricted" — and the historical coarse
/// tokens (<c>workspace</c>/<c>project</c>) are not grants at all any more.
///
/// Enforcement split:
/// • here (cheap pre-check at scope resolution + PDP resource_access boundary):
///   the grant LIST, its path PREFIXES and the read/write LEVEL decide the
///   workspace target.
/// • the tool process independently refuses any path outside its workspace root.
/// A tool without a single target path (shell, mcp_*, git_*) is decided by level
/// only; that fallback never widens a path-scoped grant, because such a tool has
/// no path to narrow in the first place.
/// </summary>
internal static class ToolResourceAllowList
{
    /// <summary>
    /// Boolean projection for the cheap pre-check at scope resolution: a
    /// non-empty grant list authorizes the workspace root. Levels and prefixes
    /// are enforced per claim by the PDP resource_access boundary.
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<string> grants) =>
        Evaluate(grants, relativePath: null, mutating: false).Allowed;

    /// <summary>
    /// Decide one workspace target. <paramref name="relativePath"/> is the
    /// workspace-relative target normalized by <see cref="ToolResourcePathRegistry"/>;
    /// null means the tool has no single path and only the level is checked.
    /// </summary>
    public static ResourceAllowDecision Evaluate(IReadOnlyList<string> grants, string? relativePath, bool mutating)
    {
        // An empty envelope is the fail-closed "no workspace authorization"
        // state. There is no grant to upgrade from, so it never asks.
        if (grants.Count == 0) return ResourceAllowDecision.Refused(ResourceAllowBasis.NoGrant);

        if (string.IsNullOrEmpty(relativePath))
        {
            var sawGrant = false;
            foreach (var grant in grants)
            {
                if (!TryParseGrant(grant, out var write, out _)) continue;
                sawGrant = true;
                if (mutating && !write) continue;
                return ResourceAllowDecision.Granted(grant);
            }

            return ResourceAllowDecision.LevelNotGranted(mutating, sawGrant);
        }

        // A target that cannot be expressed inside the workspace sits outside the
        // envelope whatever level was granted, so it never upgrades.
        var normalizedTarget = ToolResourcePathRegistry.NormalizeRelativePath(relativePath);
        if (normalizedTarget is null) return ResourceAllowDecision.Refused(ResourceAllowBasis.PathDenied);

        var sawLevelMatch = false;
        var sawAnyGrant = false;
        foreach (var grant in grants)
        {
            if (!TryParseGrant(grant, out var write, out var prefix)) continue;
            sawAnyGrant = true;
            if (mutating && !write) continue;
            sawLevelMatch = true;
            if (CoversPrefix(prefix, normalizedTarget)) return ResourceAllowDecision.Granted(grant);
        }

        // Distinguish "the level was never granted" from "the level was granted
        // but no prefix covers this target": both refuse, but only the first may
        // upgrade to an approval. A granted level whose prefix does not cover the
        // target is an envelope escape, not a missing grant.
        return sawLevelMatch
            ? ResourceAllowDecision.Refused(ResourceAllowBasis.PathDenied)
            : ResourceAllowDecision.LevelNotGranted(mutating, sawAnyGrant);
    }

    /// <summary>Parse "read:/write:" level prefix. Unknown forms are not grants.</summary>
    private static bool TryParseGrant(string grant, out bool write, out string prefix)
    {
        write = false;
        prefix = string.Empty;
        if (string.IsNullOrWhiteSpace(grant)) return false;
        var trimmed = grant.Trim();
        if (trimmed.StartsWith("read:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = trimmed[5..];
        }
        else if (trimmed.StartsWith("write:", StringComparison.OrdinalIgnoreCase))
        {
            write = true;
            prefix = trimmed[6..];
        }
        else
        {
            return false;
        }

        prefix = prefix.Replace('\\', '/').Trim('/');
        return true;
    }

    /// <summary>An empty prefix covers the whole workspace; otherwise match on a path-segment boundary.</summary>
    private static bool CoversPrefix(string prefix, string path)
    {
        if (prefix.Length == 0) return true;
        if (string.Equals(prefix, path, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Length > prefix.Length
            && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && path[prefix.Length] == '/';
    }
}

/// <summary>
/// Operator- and model-facing explanation of a resource denial. It is surfaced as
/// the tool execution's safe error message (and, from there, to the model), so it
/// names the missing level, the frozen grants and the workspace root instead of
/// leaving a bare "denied" that a caller cannot act on.
/// </summary>
internal static class ResourceDenialExplanation
{
    public static string Describe(
        ResourceAllowDecision decision,
        IReadOnlyList<string> grants,
        string? toolId,
        string? workspaceRoot,
        string? relativePath)
    {
        var tool = string.IsNullOrWhiteSpace(toolId) ? "the tool" : $"'{toolId}'";
        var workspace = string.IsNullOrWhiteSpace(workspaceRoot)
            ? "the run workspace"
            : $"the workspace rooted at '{workspaceRoot}'";
        var frozen = grants.Count == 0 ? "(none)" : string.Join(", ", grants.Select(grant => $"'{grant}'"));
        var target = string.IsNullOrWhiteSpace(relativePath) ? "the workspace" : $"'{relativePath}'";

        return decision.Basis switch
        {
            ResourceAllowBasis.NoGrant =>
                $"{tool} was denied: this agent instance holds no workspace resource grant, so it cannot access {workspace}. "
                + "No grants were frozen for the instance; workspace read access must be granted at admission.",
            ResourceAllowBasis.LevelDenied when grants.Any(grant => grant.StartsWith("read:", StringComparison.OrdinalIgnoreCase)) =>
                $"{tool} was denied: it changes {workspace} but the instance holds read-level grants only ({frozen}). "
                + "A write-level grant (write:<prefix>) is required, and the write still needs its approval.",
            ResourceAllowBasis.LevelDenied =>
                $"{tool} was denied: no frozen grant covers the requested level for {workspace}. Frozen grants: {frozen}.",
            ResourceAllowBasis.PathDenied =>
                $"{tool} was denied: it targets {target}, which no frozen prefix grant covers. Frozen grants: {frozen}.",
            _ => $"{tool} was denied by the resource boundary for {workspace}. Frozen grants: {frozen}."
        };
    }

    /// <summary>
    /// Operator/model-facing explanation of an upgrade-to-approval decision. It rides
    /// on the permission request so the approval prompt states WHY it is being asked
    /// (which level is missing, which grants were frozen) instead of a bare
    /// "approval required" that hides whether the envelope can ever authorize it.
    /// </summary>
    public static string DescribeUpgrade(
        IReadOnlyList<string> grants,
        string? toolId,
        string? workspaceRoot,
        string? relativePath)
    {
        var tool = string.IsNullOrWhiteSpace(toolId) ? "the tool" : $"'{toolId}'";
        var workspace = string.IsNullOrWhiteSpace(workspaceRoot)
            ? "the run workspace"
            : $"the workspace rooted at '{workspaceRoot}'";
        var frozen = grants.Count == 0 ? "(none)" : string.Join(", ", grants.Select(grant => $"'{grant}'"));
        var target = string.IsNullOrWhiteSpace(relativePath) ? "the workspace" : $"'{relativePath}'";

        return $"{tool} needs approval: it changes {target} in {workspace}, but the instance holds no write-level grant "
            + $"(frozen grants: {frozen}). Approving issues a one-use write authorization for this call.";
    }
}

internal enum ResourceAllowBasis
{
    Granted,
    NoGrant,
    LevelDenied,
    PathDenied
}

/// <summary>
/// What the resource envelope decided for one tool claim. <see cref="Allowed"/>
/// and <see cref="RequiresApproval"/> both clear the decision point; they differ in
/// whether the call may proceed on its own or must reach the approval gate first.
/// Only <see cref="Denied"/> stops the call here.
/// </summary>
internal enum ResourceAllowDisposition
{
    Allowed,
    RequiresApproval,
    Denied
}

internal sealed record ResourceAllowDecision(ResourceAllowDisposition Disposition, ResourceAllowBasis Basis, string? MatchedGrant)
{
    /// <summary>The claim may act without a human decision.</summary>
    public bool Allowed => Disposition == ResourceAllowDisposition.Allowed;

    /// <summary>
    /// The claim may only act behind an approval decision. A MUTATING claim against
    /// an envelope that does hold parseable grants upgrades instead of refusing: the
    /// envelope never implied write access, but the approval gate is exactly the
    /// mechanism that can grant it once with a decision behind it. Refusing here is
    /// what made "write a file" structurally unreachable for every instance whose
    /// envelope defaulted to read-only — the request never reached the gate that
    /// could have authorized it.
    /// </summary>
    public bool RequiresApproval => Disposition == ResourceAllowDisposition.RequiresApproval;

    internal static ResourceAllowDecision Granted(string grant) =>
        new(ResourceAllowDisposition.Allowed, ResourceAllowBasis.Granted, grant);

    /// <summary>Stops the call at the decision point: no envelope at all, or a target outside it.</summary>
    internal static ResourceAllowDecision Refused(ResourceAllowBasis basis) =>
        new(ResourceAllowDisposition.Denied, basis, null);

    /// <summary>
    /// The requested level was never granted. A mutating claim over an envelope that
    /// holds at least one parseable grant upgrades to an approval; everything else
    /// (a read claim, or an envelope whose grants are all unrecognized) refuses.
    /// </summary>
    internal static ResourceAllowDecision LevelNotGranted(bool mutating, bool hasParseableGrant) =>
        mutating && hasParseableGrant
            ? new(ResourceAllowDisposition.RequiresApproval, ResourceAllowBasis.LevelDenied, null)
            : new(ResourceAllowDisposition.Denied, ResourceAllowBasis.LevelDenied, null);
}
