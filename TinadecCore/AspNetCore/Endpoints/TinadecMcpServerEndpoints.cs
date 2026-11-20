using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tools;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Loopback-only MCP JSON-RPC 2.0 server mounted at <c>POST /api/v1/mcp/tinadec</c>.
/// Enforces an external-network block (remote socket IP and proxy forwarding headers must
/// all resolve to loopback) without intra-host auth friction, exposing the <c>tinadec_*</c>
/// configuration toolkit directly via <see cref="ITinadecConfigToolGateway"/>.
/// </summary>
public static class TinadecMcpServerEndpoints
{
    private const string ServerName = "tinadec-selfconfig-mcp";
    private const string ServerVersion = "1.0.0";
    private const string DefaultProtocolVersion = "2024-11-05";

    private sealed record ResourceDescriptor(string Uri, string Name, string Description, string MimeType);

    private static readonly IReadOnlyList<ResourceDescriptor> StaticResources =
    [
        new("tinadec://agents", "Agents Roster", "List of configured and pack-managed agents (sanitized).", "application/json"),
        new("tinadec://modes", "Agent Modes", "Agent modes and team orchestration topologies summary.", "application/json"),
        new("tinadec://models", "Model Catalog & Profiles", "Enabled model providers, routes, health, and non-secret model_profiles.", "application/json"),
        new("tinadec://prompts", "Prompts & Pipelines", "Prompt fragments and prompt assembly pipelines.", "application/json"),
        new("tinadec://skills", "Skills Catalog", "Workspace, shared, and built-in skills inventory.", "application/json"),
        new("tinadec://mcp", "MCP Servers", "Managed MCP server registrations with environment secrets masked.", "application/json"),
        new("tinadec://catalog", "Authoring Catalog", "Agents and prompt pipelines available when authoring modes.", "application/json"),
    ];

    public static IEndpointRouteBuilder MapTinadecMcpServerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/mcp/tinadec", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        ITinadecConfigToolGateway gateway,
        ITenantContextAccessor tenantAccessor,
        CancellationToken ct)
    {
        if (!IsLoopbackOnlyRequest(context))
        {
            return Results.Json(
                new
                {
                    type = "https://tinadec.local/errors/external_network_forbidden",
                    title = "External network access forbidden",
                    status = StatusCodes.Status403Forbidden,
                    detail = "The Tinadec self-configuration MCP endpoint only accepts loopback requests.",
                    code = "external_network_forbidden",
                },
                statusCode: StatusCodes.Status403Forbidden,
                contentType: "application/problem+json");
        }

        JsonDocument requestDoc;
        try
        {
            requestDoc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return JsonRpcError(null, -32700, "Parse error", ex.Message);
        }

        using (requestDoc)
        {
            var root = requestDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return JsonRpcError(null, -32600, "Invalid Request", "JSON-RPC payload must be a JSON object.");

            JsonElement? requestId = root.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Undefined
                ? idProp.Clone()
                : null;

            var jsonrpc = root.TryGetProperty("jsonrpc", out var verProp) && verProp.ValueKind == JsonValueKind.String
                ? verProp.GetString()
                : null;
            var method = root.TryGetProperty("method", out var methodProp) && methodProp.ValueKind == JsonValueKind.String
                ? methodProp.GetString()
                : null;
            if (!string.Equals(jsonrpc, "2.0", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(method))
                return JsonRpcError(requestId, -32600, "Invalid Request", "Expected jsonrpc='2.0' and non-empty string 'method'.");

            JsonElement? @params = root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind is JsonValueKind.Object
                ? paramsProp.Clone()
                : null;

            switch (method)
            {
                case "initialize":
                {
                    var requestedVersion = @params is { } p
                        && p.TryGetProperty("protocolVersion", out var pv)
                        && pv.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(pv.GetString())
                        ? pv.GetString()!
                        : DefaultProtocolVersion;
                    var negotiated = requestedVersion is "2024-11-05" or "2025-03-26"
                        ? requestedVersion
                        : DefaultProtocolVersion;

                    return JsonRpcOk(requestId, new
                    {
                        protocolVersion = negotiated,
                        capabilities = new
                        {
                            tools = new { listChanged = false },
                            resources = new { subscribe = false, listChanged = false },
                        },
                        serverInfo = new
                        {
                            name = ServerName,
                            version = ServerVersion,
                        },
                        instructions = "Loopback MCP server exposing Tinadec tinadec_* self-configuration tools and read-only configuration resources.",
                    });
                }

                case "notifications/initialized":
                case "initialized":
                    return requestId is null
                        ? Results.StatusCode(StatusCodes.Status202Accepted)
                        : JsonRpcOk(requestId, new { });

                case "tools/list":
                {
                    var tools = CoreTinadecConfigTools.AllManifestEntries()
                        .Select(entry => new
                        {
                            name = entry.Id,
                            description = entry.Description,
                            inputSchema = entry.InputSchema,
                        })
                        .ToArray();
                    return JsonRpcOk(requestId, new { tools });
                }

                case "tools/call":
                {
                    if (@params is null
                        || !@params.Value.TryGetProperty("name", out var nameProp)
                        || nameProp.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(nameProp.GetString()))
                    {
                        return JsonRpcError(requestId, -32602, "Invalid params", "tools/call requires a non-empty 'name' parameter.");
                    }

                    var toolName = nameProp.GetString()!.Trim();
                    if (!CoreVirtualToolPolicy.IsTinadecConfig(toolName))
                        return JsonRpcError(requestId, -32602, "Unknown tool", $"No tinadec configuration tool named '{toolName}'.");

                    var argsElement = @params.Value.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object
                        ? argsProp.Clone()
                        : EmptyObject();

                    var scope = BuildHostScope(tenantAccessor, argsElement);
                    var wire = new ToolWireRequestDto
                    {
                        ToolId = toolName,
                        SessionId = string.Empty,
                        ToolCallId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Approved = true,
                        Params = argsElement,
                    };

                    var result = await gateway.ExecuteAsync(toolName, scope, wire, ct).ConfigureAwait(false);
                    var text = result.Result is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } r
                        ? JsonSerializer.Serialize(r)
                        : JsonSerializer.Serialize(new { error = result.Error ?? "Tool execution failed." });

                    if (result.Result is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } structured)
                    {
                        return JsonRpcOk(requestId, new
                        {
                            content = new object[] { new { type = "text", text } },
                            structuredContent = structured,
                            isError = !result.IsSuccess,
                        });
                    }

                    return JsonRpcOk(requestId, new
                    {
                        content = new object[] { new { type = "text", text } },
                        isError = !result.IsSuccess,
                    });
                }

                case "resources/list":
                {
                    var resources = StaticResources
                        .Select(r => new
                        {
                            uri = r.Uri,
                            name = r.Name,
                            description = r.Description,
                            mimeType = r.MimeType,
                        })
                        .ToArray();
                    return JsonRpcOk(requestId, new { resources });
                }

                case "resources/read":
                {
                    if (@params is null
                        || !@params.Value.TryGetProperty("uri", out var uriProp)
                        || uriProp.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(uriProp.GetString()))
                    {
                        return JsonRpcError(requestId, -32602, "Invalid params", "resources/read requires a non-empty 'uri' parameter.");
                    }

                    var uri = uriProp.GetString()!.Trim();
                    var inspectArgs = ResolveInspectArgumentsForUri(uri);
                    if (inspectArgs is null)
                        return JsonRpcError(requestId, -32002, "Resource not found", $"Unsupported resource URI '{uri}'.");

                    var scope = BuildHostScope(tenantAccessor, inspectArgs.Value);
                    var wire = new ToolWireRequestDto
                    {
                        ToolId = CoreVirtualToolPolicy.TinadecInspectToolId,
                        SessionId = string.Empty,
                        ToolCallId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Approved = true,
                        Params = inspectArgs,
                    };
                    var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, scope, wire, ct).ConfigureAwait(false);
                    if (!result.IsSuccess)
                        return JsonRpcError(requestId, -32002, result.Error ?? "Resource read failed", result.Result);

                    var text = result.Result is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } r
                        ? JsonSerializer.Serialize(r)
                        : "{}";
                    return JsonRpcOk(requestId, new
                    {
                        contents = new object[]
                        {
                            new
                            {
                                uri,
                                mimeType = "application/json",
                                text,
                            },
                        },
                    });
                }

                default:
                    return JsonRpcError(requestId, -32601, "Method not found", $"Unsupported MCP method '{method}'.");
            }
        }
    }

    internal static bool IsLoopbackOnlyRequest(HttpContext context)
    {
        // On TCP sockets (Kestrel) RemoteIpAddress is always populated; null only happens
        // for in-memory TestServer or local IPC transports (Unix domain sockets / pipes).
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is not null && !IsLoopbackIp(remoteIp))
            return false;

        if (!AllHeaderIpsLoopback(context.Request.Headers["X-Forwarded-For"]))
            return false;

        if (!AllHeaderIpsLoopback(context.Request.Headers["X-Real-IP"]))
            return false;

        return true;
    }

    private static bool AllHeaderIpsLoopback(Microsoft.Extensions.Primitives.StringValues headerValues)
    {
        foreach (var raw in headerValues)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            foreach (var segment in raw.Split(','))
            {
                var trimmed = segment.Trim();
                if (!TryParseHostIp(trimmed, out var ip) || !IsLoopbackIp(ip))
                    return false;
            }
        }
        return true;
    }

    private static bool TryParseHostIp(string value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var direct))
        {
            address = direct;
            return true;
        }
        if (IPEndPoint.TryParse(value, out var endpoint))
        {
            address = endpoint.Address;
            return true;
        }
        address = IPAddress.None;
        return false;
    }

    private static bool IsLoopbackIp(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(normalized);
    }

    private static ToolInvocationScope BuildHostScope(ITenantContextAccessor tenantAccessor, JsonElement args)
    {
        var current = tenantAccessor.Current;
        Guid? projectId = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("project_id", out var projectProp)
            && projectProp.ValueKind == JsonValueKind.String
            && Guid.TryParse(projectProp.GetString(), out var parsedProject))
        {
            projectId = CoreVirtualToolPolicy.FromWireSentinel(parsedProject);
        }

        return new ToolInvocationScope(
            TenantId: current.TenantId,
            WorkspaceId: current.WorkspaceId,
            PrincipalId: current.PrincipalId,
            ProjectId: CoreVirtualToolPolicy.ToWireSentinel(projectId),
            SessionId: Guid.Empty,
            RunId: Guid.Empty,
            TaskId: Guid.Empty,
            AgentInstanceId: Guid.Empty,
            WorkspaceRoot: string.Empty,
            AllowedTools: CoreVirtualToolPolicy.TinadecConfigToolIds,
            AllowedResources: [],
            FrozenConfigurationHash: string.Empty,
            DefaultTimeoutSeconds: 60,
            WorkerRetryLimit: 0,
            SerializeWorkspaceWrites: false);
    }

    private static JsonElement? ResolveInspectArgumentsForUri(string uri) => uri switch
    {
        "tinadec://agents" => JsonSerializer.SerializeToElement(new { target = "agents", limit = 50 }),
        "tinadec://modes" => JsonSerializer.SerializeToElement(new { target = "modes", limit = 50 }),
        "tinadec://models" => JsonSerializer.SerializeToElement(new { target = "models", limit = 50 }),
        "tinadec://prompts" => JsonSerializer.SerializeToElement(new { target = "prompts", limit = 50 }),
        "tinadec://skills" => JsonSerializer.SerializeToElement(new { target = "skills", limit = 50 }),
        "tinadec://mcp" => JsonSerializer.SerializeToElement(new { target = "mcp", limit = 50 }),
        "tinadec://catalog" => JsonSerializer.SerializeToElement(new { target = "catalog", limit = 50 }),
        _ when uri.StartsWith("tinadec://agents/", StringComparison.OrdinalIgnoreCase) =>
            uri["tinadec://agents/".Length..] is { Length: > 0 } id
                ? JsonSerializer.SerializeToElement(new { target = "agent", agent_id = Uri.UnescapeDataString(id) })
                : null,
        _ when uri.StartsWith("tinadec://modes/", StringComparison.OrdinalIgnoreCase) =>
            uri["tinadec://modes/".Length..] is { Length: > 0 } id
                ? JsonSerializer.SerializeToElement(new { target = "mode", mode_id = Uri.UnescapeDataString(id) })
                : null,
        _ when uri.StartsWith("tinadec://prompt-pipelines/", StringComparison.OrdinalIgnoreCase) =>
            uri["tinadec://prompt-pipelines/".Length..] is { Length: > 0 } id
                ? JsonSerializer.SerializeToElement(new { target = "prompt_pipeline", pipeline_id = Uri.UnescapeDataString(id) })
                : null,
        _ => null,
    };

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    private static IResult JsonRpcOk(JsonElement? id, object result) =>
        Results.Json(new
        {
            jsonrpc = "2.0",
            id,
            result,
        });

    private static IResult JsonRpcError(JsonElement? id, int code, string message, object? data = null) =>
        Results.Json(new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message,
                data,
            },
        });
}
