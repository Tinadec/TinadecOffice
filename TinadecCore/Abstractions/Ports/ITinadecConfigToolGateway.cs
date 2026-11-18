using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Executes the Core-owned <c>tinadec_*</c> workbench-configuration tools
/// (<c>tinadec_inspect</c> / <c>tinadec_agent_apply</c> / <c>tinadec_mode_apply</c> /
/// <c>tinadec_mode_publish</c> / <c>tinadec_skill_apply</c> / <c>tinadec_mcp_apply</c>).
///
/// The Tools project must not reference AgentConfiguration or Skills, so the
/// dispatcher knows only this port. The implementation lives in Runtime, where the
/// configuration, skill, MCP and market services are all reachable — one gateway,
/// no duplicated write path, and the tools keep working from inside a run because
/// the caller's <see cref="ToolInvocationScope"/> supplies tenant/workspace/project.
/// </summary>
public interface ITinadecConfigToolGateway
{
    Task<ToolWireResponseDto> ExecuteAsync(
        string toolId,
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken ct);
}
