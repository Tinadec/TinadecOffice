using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// The human-facing MCP inventory. Servers are configured in a file the Tool Provider reads
/// (never in Core), so both routes here are reads through the provider's <c>mcp_list</c> tool
/// — the same control-channel pattern the terminal panel uses for <c>#terminal</c>.
///
/// The inventory is the only honest source for "is it connected", because connecting is the
/// provider's act, not Core's: a stored row saying <c>enabled</c> would describe an intention,
/// while <c>mcp_list</c> reports whether a client actually got a tool list back.
///
/// Deliberate asymmetry with <see cref="TerminalEndpoints"/>: a failed provider read here
/// answers 200 with <c>source = "tool_provider_unavailable"</c> and the provider's reason,
/// while a terminal write answers 503. A write caller needs a retryable failure; a read caller
/// needs the answer to carry its own provenance, so that "nothing is configured" and "we could
/// not look" cannot arrive as the same payload.
/// </summary>
public static class McpEndpoints
{
    private const string McpInventoryToolId = "mcp_list";

    /// <summary>
    /// Listing tools means handshaking every configured stdio server, so this is bounded but
    /// not aggressive: an unreachable server is reported per row by the provider rather than
    /// being allowed to hold the whole read open.
    /// </summary>
    private static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mcp/servers", async (
            IToolProvider provider,
            IConfiguration configuration,
            CancellationToken ct) =>
            await ReadInventoryAsync(provider, configuration, includeSchema: false, ct).ConfigureAwait(false))
            .Produces<McpInventoryDto>(StatusCodes.Status200OK);

        app.MapGet("/api/v1/mcp/servers/{serverId}/tools", async (
            string serverId,
            IToolProvider provider,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var inventory = await ReadInventoryAsync(provider, configuration, includeSchema: true, ct)
                .ConfigureAwait(false);

            if (inventory.Source != McpReadSource.Provider)
            {
                // The read itself failed: report that, and never imply the server is unknown.
                return Results.Ok(new McpServerToolsDto
                {
                    Source = inventory.Source,
                    Reason = inventory.Reason,
                    WorkspaceRoot = inventory.WorkspaceRoot,
                    ConfigPath = inventory.ConfigPath,
                    ServerId = serverId,
                });
            }

            // Only a completed read is entitled to call a server absent. Every server the
            // provider could not reach still appears, with its error, so an outage never
            // becomes a 404 that reads as "you deleted it".
            var match = inventory.Servers.FirstOrDefault(
                server => string.Equals(server.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return Results.NotFound(new { code = "mcp_server_not_found", message = $"No MCP server named '{serverId}' is configured." });

            return Results.Ok(new McpServerToolsDto
            {
                Source = inventory.Source,
                WorkspaceRoot = inventory.WorkspaceRoot,
                ConfigPath = inventory.ConfigPath,
                ServerId = serverId,
                Server = match,
            });
        })
            .Produces<McpServerToolsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<McpInventoryDto> ReadInventoryAsync(
        IToolProvider provider,
        IConfiguration configuration,
        bool includeSchema,
        CancellationToken cancellationToken)
    {
        var workspaceRoot = configuration["TinadecTools:DefaultWorkspaceRoot"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            workspaceRoot = Directory.GetCurrentDirectory();

        JsonElement payload;
        try
        {
            var response = await provider.CallAsync(
                workspaceRoot,
                new ToolWireRequestDto
                {
                    ToolId = McpInventoryToolId,
                    // No run owns an inventory read; the provider only echoes this back on
                    // wire events, and a listing call produces none.
                    SessionId = "mcp-inventory",
                    Approved = false,
                    Params = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["include_schema"] = includeSchema
                    })
                },
                InventoryTimeout,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return Unavailable(workspaceRoot, response.Error ?? "The tool provider rejected the inventory read.");

            if (response.Result is not { } result || result.ValueKind != JsonValueKind.Object)
                return Unavailable(workspaceRoot, "mcp_list returned no object payload.");

            payload = result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable(workspaceRoot, ex.Message);
        }

        if (!payload.TryGetProperty("servers", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return Unavailable(workspaceRoot, "mcp_list returned no servers array.");

        var servers = new List<McpServerDto>();
        var dropped = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var id = ReadText(row, "id");
            if (string.IsNullOrEmpty(id))
            {
                // A row without an id cannot be named, clicked, or looked up again.
                dropped++;
                continue;
            }

            servers.Add(new McpServerDto
            {
                Id = id,
                Name = ReadText(row, "name") ?? id,
                Status = ReadText(row, "status") ?? McpServerStatus.Unknown,
                Error = ReadText(row, "error"),
                Tools = ReadTools(row),
            });
        }

        var inventory = new McpInventoryDto
        {
            Source = McpReadSource.Provider,
            WorkspaceRoot = workspaceRoot,
            ConfigPath = ReadText(payload, "config_path"),
            DroppedRows = dropped == 0 ? null : dropped,
            Servers = servers,
        };
        return inventory;
    }

    private static IReadOnlyList<McpToolDto> ReadTools(JsonElement row)
    {
        if (!row.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return [];

        var listed = new List<McpToolDto>();
        foreach (var tool in tools.EnumerateArray())
        {
            var id = ReadText(tool, "id") ?? ReadText(tool, "name");
            if (string.IsNullOrEmpty(id))
                continue;

            listed.Add(new McpToolDto
            {
                Id = id,
                Name = ReadText(tool, "name") ?? id,
                Description = ReadText(tool, "description"),
                InputSchema = tool.TryGetProperty("input_schema", out var schema)
                    && schema.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                    ? schema.Clone()
                    : null,
            });
        }

        return listed;
    }

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static McpInventoryDto Unavailable(string workspaceRoot, string reason) => new()
    {
        Source = McpReadSource.Unavailable,
        Reason = reason,
        WorkspaceRoot = workspaceRoot,
    };
}
