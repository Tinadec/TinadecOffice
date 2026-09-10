namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Single source of truth for Core-owned virtual tools: tools that never reach a
/// TinadecTools child process and are executed by Core itself. The projectless
/// (free-conversation) contract needs the same identity in Lifecycle (approval
/// admission), Tools (dispatch/manifest freezing), and the HTTP layer, so the
/// literal lives here instead of being re-typed per module.
/// </summary>
public static class CoreVirtualToolPolicy
{
    public const string CreateWorkspaceToolId = "create_workspace";

    /// <summary>
    /// A nullable project id cannot travel on the wire, so a projectless call
    /// carries <see cref="Guid.Empty"/> as its project sentinel.
    /// </summary>
    public static readonly Guid ProjectlessProjectId = Guid.Empty;

    public static bool IsCreateWorkspace(string? toolId) =>
        string.Equals(toolId, CreateWorkspaceToolId, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the caller declared no project (the <see cref="Guid.Empty"/> sentinel).</summary>
    public static bool IsProjectlessScope(Guid projectId) => projectId == ProjectlessProjectId;

    /// <summary>
    /// True for the only tool that may run inside a projectless scope: the
    /// Core-owned workspace-creation virtual tool. Every provider-backed tool
    /// requires a real project root and must fail closed here.
    /// </summary>
    public static bool IsProjectlessCreateWorkspace(Guid projectId, string? toolId) =>
        IsProjectlessScope(projectId) && IsCreateWorkspace(toolId);
}
