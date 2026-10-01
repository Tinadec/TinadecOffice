using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace TinadecTools.Tools.Mcp;

internal sealed class McpClientPool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients = new(StringComparer.OrdinalIgnoreCase);

    public async Task<McpClient> GetOrCreateAsync(McpServerConfig config, CancellationToken cancellationToken = default)
    {
        var lazy = _clients.GetOrAdd(config.Id, _ => new Lazy<Task<McpClient>>(() => CreateAsync(config, cancellationToken)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _clients.TryRemove(config.Id, out _);
            throw;
        }
    }

    public async Task<IReadOnlyList<McpToolSummary>> ListToolsAsync(McpServerConfig config, bool includeSchema, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateAsync(config, cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.Select(tool => ToSummary(tool, includeSchema)).ToArray();
    }

    public async Task<JsonElement> InvokeAsync(McpServerConfig config, string toolName, JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var client = await GetOrCreateAsync(config, cancellationToken).ConfigureAwait(false);
        var dictionary = McpJsonArguments.ToDictionary(arguments);
        var result = await client.CallToolAsync(toolName, dictionary, cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, McpJsonContext.Default.CallToolResult);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _clients.Values)
        {
            if (!lazy.IsValueCreated)
                continue;

            try
            {
                var client = await lazy.Value.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown during process exit.
            }
        }

        _clients.Clear();
    }

    private static Task<McpClient> CreateAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = string.IsNullOrWhiteSpace(config.Name) ? config.Id : config.Name,
            Command = config.Command,
            Arguments = config.Args.ToArray(),
            WorkingDirectory = config.Cwd,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = config.Env
        });

        return McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    private static McpToolSummary ToSummary(McpClientTool tool, bool includeSchema)
    {
        return new McpToolSummary
        {
            Id = tool.Name,
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = includeSchema ? tool.JsonSchema.Clone() : null
        };
    }
}
