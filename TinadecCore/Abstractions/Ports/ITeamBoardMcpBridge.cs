using System.Text.Json;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Scoped bearer token lease binding an external CLI/ACP/OpenCode teammate process to a single active
/// run's shared board tools on the loopback MCP endpoint (<c>POST /api/v1/mcp/tinadec</c>).
/// </summary>
public sealed record ScopedTeamMcpLease(
    string Token,
    Guid TenantId,
    Guid? WorkspaceId,
    Guid SessionId,
    Guid RunId,
    string TeammateHandle,
    IReadOnlyList<string> AllowedTools,
    DateTimeOffset ExpiresAt)
{
    public Guid? ProjectId { get; init; }
    public Guid PrincipalId { get; init; }
}

/// <summary>
/// Context passed to a run-board handler when an external MCP teammate invokes <c>team_message</c> or <c>team_plan</c>.
/// </summary>
public sealed record TeamBoardToolCallContext(
    Guid TenantId,
    Guid? WorkspaceId,
    Guid SessionId,
    Guid RunId,
    string TeammateHandle,
    string ToolName,
    JsonElement Arguments);

/// <summary>
/// Structured outcome returned by a run-board action handler.
/// </summary>
public sealed record TeamBoardToolCallResult(
    bool Success,
    JsonElement? Result = null,
    string? Error = null);

/// <summary>
/// Optional DI port implemented by the run engine (<c>DmaEA</c>) so live <c>team_message</c> and
/// <c>team_plan</c> calls arriving over the loopback MCP server mutate the live run board and wake
/// teammates without cross-module coupling.
/// </summary>
public interface ITeamRunBoardActionSink
{
    Task<TeamBoardToolCallResult> ExecuteBoardActionAsync(
        TeamBoardToolCallContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Issues, validates, revokes, and dispatches per-teammate scoped MCP tokens (#36).
/// Scoped tokens only expose shared-board &amp; read-only recall tools — never <c>tinadec_*</c> self-config tools.
/// </summary>
public interface ITeamBoardMcpBridge
{
    public static readonly IReadOnlyList<string> DefaultAllowedTools =
    [
        CoreVirtualToolPolicy.TeamMessageToolId,
        CoreVirtualToolPolicy.TeamPlanToolId,
        TeamTaskBoardProtocol.TeamTasksToolId,
        CoreVirtualToolPolicy.RecallEvidenceToolId,
        CoreVirtualToolPolicy.RecallHistoryToolId,
        CoreVirtualToolPolicy.GraphViewToolId,
    ];

    ScopedTeamMcpLease IssueToken(
        Guid tenantId,
        Guid? workspaceId,
        Guid sessionId,
        Guid runId,
        string teammateHandle,
        IReadOnlyList<string>? allowedTools = null,
        TimeSpan? ttl = null,
        Guid? projectId = null,
        Guid principalId = default);

    int RevokeRunTokens(Guid runId);

    ScopedTeamMcpLease? ValidateToken(string? token);

    IReadOnlyList<ToolManifestEntryDto> ListScopedTools(ScopedTeamMcpLease lease);

    Task<ToolWireResponseDto> ExecuteScopedToolAsync(
        ScopedTeamMcpLease lease,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a live per-run callback for <c>team_message</c> / <c>team_plan</c> invocations from
    /// external CLI/ACP teammates. Disposing the returned token unregisters the handler.
    /// </summary>
    IDisposable RegisterRunBoardHandler(
        Guid runId,
        Func<TeamBoardToolCallContext, CancellationToken, Task<TeamBoardToolCallResult>> handler);
}
