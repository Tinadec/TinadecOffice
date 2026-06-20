using System.Net.Http.Json;
using System.Text.Json;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;

namespace TinadecCore.Services;

/// <summary>
/// MCP 工具调用适配器：将模型的工具调用请求转发到 Gateway 的 MCP tools/call 端点。
///
/// tool.ExecuteEndpoint 格式：/api/v1/mcp/servers/{serverId}/tools/{toolName}/call
/// 请求 body：{ arguments: {...} }
/// 响应 body：{ ok, result, error, message }
/// </summary>
public sealed class McpToolInvocationAdapter(HttpClient httpClient) : IToolInvocationAdapter
{
    public string Id => "mcp";

    public bool CanInvoke(ToolDescriptorDto tool)
    {
        return string.Equals(tool.Source, Id, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CodeToolExecuteResultDto> InvokeAsync(
        ToolDescriptorDto tool,
        CodeToolExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tool.ExecuteEndpoint))
        {
            return Failed(tool.Id, "MCP tool has no execute endpoint.");
        }

        // 将 CodeToolExecuteRequest.Arguments 转换为 MCP 的 { arguments: {...} }
        var mcpBody = new { arguments = request.Arguments ?? new Dictionary<string, object?>() };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                tool.ExecuteEndpoint,
                mcpBody,
                TinadecJson.Options,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                return Failed(tool.Id, $"Gateway returned {response.StatusCode}: {errorText}");
            }

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(TinadecJson.Options, cancellationToken);
            var ok = result.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();

            if (!ok)
            {
                var errorMsg = result.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "Unknown MCP error";
                return Failed(tool.Id, errorMsg ?? "Unknown MCP error");
            }

            // 提取 result 内容
            var resultData = result.TryGetProperty("result", out var resultProp) ? resultProp : default;
            var outputText = resultData.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.Array
                ? string.Join("\n", contentProp.EnumerateArray().Select(item =>
                    item.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : ""))
                : resultData.GetRawText();

            return new CodeToolExecuteResultDto(
                tool.Id,
                "success",
                outputText,
                ["mcp.tool.invoked"],
                new Dictionary<string, object?> { ["mcp_result"] = resultData.GetRawText() },
                false,
                null);
        }
        catch (Exception ex)
        {
            return Failed(tool.Id, ex.Message);
        }
    }

    private static CodeToolExecuteResultDto Failed(string toolId, string message) => new(
        toolId,
        "failed",
        message,
        ["mcp.tool.failed"],
        new Dictionary<string, object?>(),
        false,
        null);
}
