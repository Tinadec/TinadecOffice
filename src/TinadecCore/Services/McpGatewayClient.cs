using System.Net.Http.Json;
using System.Text.Json;
using Tinadec.Contracts.Models;

namespace TinadecCore.Services;

/// <summary>
/// Core 调用 Gateway MCP 协调端点的客户端。
/// Gateway URL 由 TINADEC_GATEWAY_URL 环境变量配置（默认 http://127.0.0.1:48730）。
/// </summary>
public sealed class McpGatewayClient(HttpClient httpClient)
{
    public async Task<McpConnectResultDto?> ConnectAsync(string serverId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(
            $"api/v1/mcp/servers/{Uri.EscapeDataString(serverId)}/connect",
            content: null,
            cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<McpConnectResultDto>(TinadecJson.Options, cancellationToken);
    }

    public async Task<McpDisconnectResultDto?> DisconnectAsync(string serverId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(
            $"api/v1/mcp/servers/{Uri.EscapeDataString(serverId)}/disconnect",
            content: null,
            cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<McpDisconnectResultDto>(TinadecJson.Options, cancellationToken);
    }

    public async Task<McpRuntimeStatusDto?> GetStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/mcp/servers/{Uri.EscapeDataString(serverId)}/status",
            cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<McpRuntimeStatusDto>(TinadecJson.Options, cancellationToken);
    }

    public async Task<McpToolCallResultDto?> CallToolAsync(
        string serverId,
        string toolName,
        JsonElement? arguments,
        CancellationToken cancellationToken = default)
    {
        var body = new { arguments };
        using var response = await httpClient.PostAsJsonAsync(
            $"api/v1/mcp/servers/{Uri.EscapeDataString(serverId)}/tools/{Uri.EscapeDataString(toolName)}/call",
            body,
            TinadecJson.Options,
            cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<McpToolCallResultDto>(TinadecJson.Options, cancellationToken);
    }
}
