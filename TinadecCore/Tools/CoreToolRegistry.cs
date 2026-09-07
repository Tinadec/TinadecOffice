using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Manifest-backed tool registry. The default workspace root's process is the
/// catalog source; all lookups fall back to it so callers never need to know
/// which workspace a tool call will run in.
/// </summary>
public sealed class CoreToolRegistry : IToolRegistry
{
    private readonly IToolProvider _provider;

    public CoreToolRegistry(IToolProvider provider) => _provider = provider;

    public async Task<IReadOnlyList<ToolManifestEntryDto>> ListToolsAsync(string? workspaceRoot = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifest = await _provider.GetManifestAsync(workspaceRoot ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return manifest.Tools;
        }
        catch (InvalidOperationException)
        {
            // 无工作区根且未配置 TinadecTools:DefaultWorkspaceRoot（本地模式默认如此）时，
            // ResolveRoot 会抛 InvalidOperationException。清单展示/就绪探测路径按预期
            // fail-closed 降级为空清单（tool_count=0），而不是冒泡成 /api/v1/tools 的 503
            // 让前端弹「No workspace root...」错误。工具「执行/派发」路径直接调用
            // _provider.GetManifestAsync（不经此方法），仍保持抛错 fail-closed。
            return [];
        }
    }

    public async Task<IReadOnlyList<ToolManifestEntryDto>> SearchToolsAsync(string query, string? workspaceRoot = null, CancellationToken cancellationToken = default)
    {
        var tools = await ListToolsAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return tools;

        return tools.Where(tool =>
                terms.Any(term =>
                    tool.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || tool.Description.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task<ToolManifestEntryDto?> FindToolAsync(string toolId, string? workspaceRoot = null, CancellationToken cancellationToken = default)
    {
        var tools = await ListToolsAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);
        return tools.FirstOrDefault(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
    }
}
