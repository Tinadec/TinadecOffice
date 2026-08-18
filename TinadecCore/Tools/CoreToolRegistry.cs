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
    private readonly IToolProcessManager _processes;

    public CoreToolRegistry(IToolProcessManager processes) => _processes = processes;

    public async Task<IReadOnlyList<ToolManifestEntryDto>> ListToolsAsync(string? workspaceRoot = null, CancellationToken cancellationToken = default)
    {
        var manifest = await _processes.GetManifestAsync(workspaceRoot ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return manifest.Tools;
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
