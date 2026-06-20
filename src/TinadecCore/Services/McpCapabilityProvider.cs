using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

/// <summary>
/// MCP 工具能力提供者：动态查询 DB 中所有 connected MCP server 的 tools，
/// 注册进 ToolRegistryService 使模型可调用。
///
/// 借鉴 codex ToolInfo 双名设计：
/// - 协议路由名：{server_id}:{tool_name}（内部）
/// - 模型可见名：mcp__{server_name}__{tool_name}（借鉴 vscode 命名风格）
/// </summary>
public sealed class McpCapabilityProvider : ICapabilityProvider
{
    private readonly CoreStore _coreStore;

    public McpCapabilityProvider(CoreStore coreStore)
    {
        _coreStore = coreStore;
    }

    public string Id => "mcp";

    public IReadOnlyList<ToolDescriptorDto> ListCapabilities()
    {
        var tools = new List<ToolDescriptorDto>();
        var servers = _coreStore.ListMcpServers();

        foreach (var server in servers)
        {
            if (!string.Equals(server.Status, "connected", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var toolName in server.Tools)
            {
                // 模型可见名：mcp__{server_name}__{tool_name}
                var toolId = $"mcp__{SanitizeForId(server.Name)}__{toolName}";
                // ExecuteEndpoint 指向 Gateway 的 tools/call 端点
                var endpoint = $"/api/v1/mcp/servers/{Uri.EscapeDataString(server.Id)}/tools/{Uri.EscapeDataString(toolName)}/call";

                tools.Add(new ToolDescriptorDto(
                    Id: toolId,
                    DisplayName: $"{server.Name}: {toolName}",
                    Domain: "programming",
                    Source: "mcp",
                    Risk: "read-only",
                    RequiresApproval: false,
                    ExecuteEndpoint: endpoint,
                    Capabilities: ["mcp", server.Name, toolName]));
            }
        }

        return tools;
    }

    private static string SanitizeForId(string value)
    {
        // 将 server name 转换为合法的 id 片段（只保留字母数字和下划线）
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] != '_')
            {
                chars[i] = '_';
            }
        }
        return new string(chars).Trim('_');
    }
}
