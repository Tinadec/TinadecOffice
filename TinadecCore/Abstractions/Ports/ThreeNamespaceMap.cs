namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Three-namespace reconciliation map for DmaEA graph orchestration.
///
/// The same authority exists in three namespaces that cannot be compared
/// directly — containment only means something after mapping:
/// ① capability strings (template/pack declarations, e.g. agent.create_temporary,
///    task.dispatch) — consumed by capability gates such as the spawn admission
///    check, never matched literally against PDP claims;
/// ② tool ids (the frozen TinadecTools manifest space, e.g. read_file);
/// ③ PDP claims — what leases/approval/authorization actually evaluate:
///    CapabilityClaim("tool.invoke", read|mutate, "tool://&lt;id&gt;").
///
/// agent.create_temporary / agent.spawn are capability declarations gating the
/// spawn chain, NOT PDP claims; the real spawn path is the capability gate plus
/// required_tools, parent-grant subset, and spawn budget/depth checks.
/// </summary>
public static class ThreeNamespaceMap
{
    public const string ToolInvokeCapability = "tool.invoke";

    public const string SpawnTemporaryCapability = "agent.create_temporary";
    public const string SpawnPersistentCapability = "agent.create_persistent";
    /// <summary>Legacy alias accepted by the spawn capability gate.</summary>
    public const string SpawnAliasCapability = "agent.spawn";

    public const string TaskDispatchCapability = "task.dispatch";
    public const string ConverseCapability = "user.respond";
    public const string ConverseCapabilityAlias = "user.converse";

    public const string ToolClaimScheme = "tool://";

    /// <summary>Tool id (+mutates flag) → the PDP claim the dispatcher actually presents.</summary>
    public static CapabilityClaim ToolClaim(string toolId, bool mutatesWorkspace) =>
        new(ToolInvokeCapability, mutatesWorkspace ? "mutate" : "read", $"{ToolClaimScheme}{toolId}");

    /// <summary>True when the claim is the tool.invoke claim shape produced by <see cref="ToolClaim"/>.</summary>
    public static bool IsToolInvokeClaim(CapabilityClaim claim) =>
        string.Equals(claim.Capability, ToolInvokeCapability, StringComparison.OrdinalIgnoreCase)
        && claim.Resource.StartsWith(ToolClaimScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tool id carried by a tool.invoke claim, or null for foreign claims.</summary>
    public static string? ToolIdOfClaim(CapabilityClaim claim) =>
        IsToolInvokeClaim(claim) ? claim.Resource[ToolClaimScheme.Length..] : null;

    /// <summary>
    /// True for capability strings that declare spawn authority (including the
    /// legacy alias). These gate the spawn chain; they are never PDP claim literals.
    /// </summary>
    public static bool IsSpawnCapability(string? capability) =>
        capability is not null
        && (string.Equals(capability, SpawnTemporaryCapability, StringComparison.OrdinalIgnoreCase)
            || string.Equals(capability, SpawnPersistentCapability, StringComparison.OrdinalIgnoreCase)
            || string.Equals(capability, SpawnAliasCapability, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps a set of declared tool ids onto the PDP claims the dispatcher would
    /// present, so envelope ⊆ boundary containment can be evaluated in one
    /// namespace (claim space) instead of comparing across namespaces.
    /// </summary>
    public static IReadOnlyList<CapabilityClaim> ToolClaims(IEnumerable<(string ToolId, bool MutatesWorkspace)> tools) =>
        tools.Select(tool => ToolClaim(tool.ToolId, tool.MutatesWorkspace)).ToArray();
}
