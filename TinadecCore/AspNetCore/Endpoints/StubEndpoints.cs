using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Runtime;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Extension methods that register all stub Core endpoints
/// needed by the Gateway proxy and Desktop frontend.
/// GET endpoints return 200 with empty/default collections.
/// Write endpoints return 501 Not Implemented.
/// </summary>
public static class StubEndpoints
{
    public static IEndpointRouteBuilder MapStubEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapReadinessStubs();
        app.MapProjectSessionStubs();
        app.MapToolStubs();
        app.MapMarketExtensionStubs();
        app.MapMcpAcpStubs();
        app.MapDebugStubs();
        return app;
    }

    // ──────────────────────────────────────────────────────────
    // Readiness / Doctor
    // ──────────────────────────────────────────────────────────
    // Deprecated compat shims: these three endpoints used to be stubs/hardcoded.
    // They are now thin adapters over the unified readiness receipt
    // (GET /api/v1/readiness) and keep their legacy response shapes so older
    // Desktop/Gateway consumers keep working. New consumers must use the receipt.
    private static void MapReadinessStubs(this IEndpointRouteBuilder app)
    {
        // [Deprecated] Use GET /api/v1/readiness (unified receipt) instead.
        app.MapGet("/api/v1/doctor", async (TinadecCore.Runtime.ReadinessService readiness, CancellationToken ct) =>
        {
            var receipt = await readiness.GetReceiptAsync(ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos",
                agent_core_version = "0.1.0",
                overall = receipt.Status,
                checks = receipt.Items.Select(item => new
                {
                    id = item.Id,
                    status = item.Status,
                    reason = item.Reason,
                    action = item.Action
                })
            });
        });

        // [Deprecated] Use GET /api/v1/readiness + POST /api/v1/model-probe instead.
        // Legacy shape: two-state ready|warning projection of the model layers.
        app.MapGet("/api/v1/model-readiness", async (TinadecCore.Runtime.ReadinessService readiness, CancellationToken ct) =>
        {
            var receipt = await readiness.GetReceiptAsync(ct).ConfigureAwait(false);
            var items = receipt.Items.ToDictionary(item => item.Id, item => item);
            ReadinessItem Item(string id) => items.TryGetValue(id, out var value)
                ? value
                : new ReadinessItem { Id = id, Status = "unavailable", Reason = "receipt item missing" };

            var modelItems = new[] { Item("model_provider"), Item("model_secret"), Item("model_route"), Item("model_probe") };
            // Mirror the receipt rule: model_probe "unavailable" (probe not executed
            // yet) must not degrade the legacy projection to "warning".
            var legacyStatus = modelItems.Any(item => item.Status == "blocked") ? "warning"
                : modelItems.Where(item => !(item.Id == "model_probe" && item.Status == "unavailable"))
                    .All(item => item.Status == "ready") ? "ready"
                : "warning";
            var reasons = modelItems
                .Where(item => item.Status is not "ready")
                .Select(item => item.Reason ?? item.Status)
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct()
                .ToArray();
            var provider = Item("model_provider");
            var providerReady = provider.Status == "ready";
            var route = Item("model_route");
            var routeReady = route.Status == "ready";
            var probe = Item("model_probe");
            var providerInstanceId = provider.Data is { } data
                && data.GetType().GetProperty("provider_instance_id")?.GetValue(data) is Guid id && id != Guid.Empty
                ? (Guid?)id : null;
            var providerDisplayName = provider.Data?.GetType().GetProperty("provider_display_name")?.GetValue(provider.Data) as string;
            var model = provider.Data?.GetType().GetProperty("model")?.GetValue(provider.Data) as string;

            return Results.Ok(new
            {
                status = legacyStatus,
                generated_at = DateTimeOffset.UtcNow,
                receipt_id = Guid.NewGuid().ToString("N"),
                provider_count = providerReady ? 1 : 0,
                ready_provider_count = providerReady ? 1 : 0,
                warning_provider_count = 0,
                blocked_provider_count = providerReady ? 0 : 1,
                route_count = routeReady ? 1 : 0,
                ready_route_count = routeReady ? 1 : 0,
                warning_route_count = 0,
                blocked_route_count = routeReady ? 0 : 1,
                providers = Array.Empty<object>(),
                routes = new[]
                {
                    new
                    {
                        purpose = "chat",
                        provider_instance_id = providerInstanceId,
                        provider_display_name = providerDisplayName,
                        model,
                        status = routeReady ? "ready" : "blocked",
                        summary = routeReady ? route.Reason ?? string.Empty : reasons.FirstOrDefault() ?? route.Reason ?? string.Empty,
                        evidence = reasons
                    }
                },
                design_notes = reasons.Length > 0
                    ? reasons
                    : probe.Status == "unavailable"
                        ? new[] { probe.Reason ?? "Model probe has not run yet." }
                        : Array.Empty<string>()
            });
        });

        // [Deprecated] Use GET /api/v1/readiness (agent_pack/default_mode/model_* items) instead.
        // Legacy shape kept; numeric counters are no longer faked beyond the provider count.
        app.MapGet("/api/v1/model-catalog-readiness", async (TinadecCore.Runtime.ReadinessService readiness, CancellationToken ct) =>
        {
            var receipt = await readiness.GetReceiptAsync(ct).ConfigureAwait(false);
            var items = receipt.Items.ToDictionary(item => item.Id, item => item);
            var providerReady = items.TryGetValue("model_provider", out var provider) && provider.Status == "ready";
            var overallBlocked = receipt.Status == "blocked";
            return Results.Ok(new
            {
                status = receipt.Status == "ready" ? "ready" : "warning",
                generated_at = DateTimeOffset.UtcNow,
                receipt_id = Guid.NewGuid().ToString("N"),
                template_count = 0,
                ready_template_count = 0,
                warning_template_count = 0,
                blocked_template_count = 0,
                runtime_module_count = 0,
                configured_provider_count = providerReady ? 1 : 0,
                advisory_probe_template_count = 0,
                templates = Array.Empty<object>(),
                design_notes = new[]
                {
                    overallBlocked
                        ? "Readiness is blocked — see GET /api/v1/readiness items for reasons and actions."
                        : "Deprecated shim: authoritative per-layer state lives in GET /api/v1/readiness."
                }
            });
        });

        app.MapGet("/api/v1/tool-layer-readiness", async (IToolRegistry registry, IAgentRuntimeConfiguration runtime, CancellationToken ct) =>
        {
            IReadOnlyList<ToolManifestEntryDto> tools = [];
            string[] notes = [];
            try
            {
                tools = await registry.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                notes = [$"TinadecTools manifest unavailable: {ex.Message}"];
            }
            var offered = tools.Select(tool => tool.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scopes = runtime.Current.Agents.Values.Select(agent =>
            {
                var unresolved = agent.AllowedTools
                    .Where(value => value != "*" && !offered.Contains(value))
                    .ToArray();
                return new
                {
                    id = agent.Id,
                    layer = agent.Layer,
                    role = agent.Role,
                    capabilities = agent.Capabilities,
                    allowed_tools = agent.AllowedTools,
                    wildcard = agent.AllowedTools.Any(value => value == "*"),
                    unresolved_tools = unresolved,
                    status = unresolved.Length == 0 ? "ready" : "warning"
                };
            }).ToArray();
            var execution = scopes.Where(scope => scope.layer == "execution").ToArray();
            var blocked = scopes.Count(scope => scope.layer == "operation" && scope.allowed_tools.Count != 0);
            if (blocked != 0)
            {
                notes = notes.Append($"{blocked} operation-layer agent(s) declare tools; the governance layer is denied every tool invocation by policy.").ToArray();
            }
            return Results.Ok(new
            {
                status = tools.Count == 0 ? "warning" : "ready",
                generated_at = DateTimeOffset.UtcNow,
                runtime = "tinadec-core-maf-0.1.0",
                receipt_id = Guid.NewGuid().ToString("N"),
                tool_count = tools.Count,
                ready_tool_count = tools.Count,
                warning_tool_count = 0,
                blocked_tool_count = 0,
                execution_agent_count = execution.Length,
                ready_agent_count = execution.Count(scope => scope.status == "ready"),
                warning_agent_count = execution.Count(scope => scope.status == "warning"),
                blocked_agent_count = 0,
                approval_gated_tool_count = tools.Count(tool => tool.RequiresApproval),
                human_checkpoint_tool_count = tools.Count(tool => tool.ConfirmationFields.Count != 0),
                future_tool_count = 0,
                unresolved_scope_count = scopes.Sum(scope => scope.unresolved_tools.Length),
                tools = tools.Select(tool => new
                {
                    id = tool.Id,
                    description = tool.Description,
                    risk = tool.Risk,
                    mutates_workspace = tool.MutatesWorkspace,
                    requires_approval = tool.RequiresApproval,
                    retry_safety = tool.RetrySafety,
                    confirmation_fields = tool.ConfirmationFields,
                    status = "ready"
                }).ToArray(),
                agent_scopes = scopes,
                design_notes = notes.Append("agent_scopes reflects the resolved runtime profile baseline; the session's formal roster scope is derived from its frozen mode version.").ToArray()
            });
        });
    }

    // ──────────────────────────────────────────────────────────
    // Projects / Sessions
    // ──────────────────────────────────────────────────────────
    private static void MapProjectSessionStubs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/sessions/{sessionId}/context-packs", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/sessions/{sessionId}/supervision-findings", () => Results.Ok(Array.Empty<object>()));

    }

    // ──────────────────────────────────────────────────────────
    // Tools
    // ──────────────────────────────────────────────────────────
    private static void MapToolStubs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tools", async (IToolRegistry registry, CancellationToken ct) =>
        {
            try { return Results.Ok(await registry.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false)); }
            catch (Exception ex) { return Results.Json(new { code = "TOOL_REGISTRY_UNAVAILABLE", message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });

        app.MapGet("/api/v1/tools/search", async (string? query, string? q, IToolRegistry registry, CancellationToken ct) =>
        {
            try { return Results.Ok(await registry.SearchToolsAsync(query ?? q ?? string.Empty, cancellationToken: ct).ConfigureAwait(false)); }
            catch (Exception ex) { return Results.Json(new { code = "TOOL_REGISTRY_UNAVAILABLE", message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });

        // `POST /api/v1/tools/shell` was a 501 stub before the terminal work landed.
        // Agent shell execution is now a governed tool: prepare via
        // `POST /api/v1/runs/{runId}/tools/shell/execute` (approval gating applies),
        // and drive the session via `GET/POST /api/v1/terminals/*`.

        app.MapPost("/api/v1/runs/{runId}/tools/{toolId}/execute", async (string runId, string toolId, ToolDispatchRequestDto? input, IToolDispatcher dispatcher, CancellationToken ct) =>
        {
            if (!Guid.TryParse(runId, out _)) return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "run_id must be a valid Guid." });
            if (input is null) return Results.BadRequest(new { code = "INVALID_TOOL_EXECUTION", message = "task_id, agent_instance_id, and params are required." });
            var result = await dispatcher.PrepareAsync(new ToolDispatchRequestDto
            {
                RunId = runId,
                TaskId = input.TaskId,
                AgentInstanceId = input.AgentInstanceId,
                ToolId = toolId,
                ToolCallKey = input.ToolCallKey,
                Params = input.Params
            }, ct).ConfigureAwait(false);
            var status = result.Status is ToolDispatchStatus.AwaitingApproval or ToolDispatchStatus.AwaitingDelegate or ToolDispatchStatus.AwaitingUser
                ? StatusCodes.Status202Accepted : StatusCodes.Status200OK;
            return Results.Json(result, statusCode: status);
        });

        app.MapPost("/api/v1/tool-executions/{id:guid}/recovery-decision", async (Guid id, ToolExecutionRecoveryDecisionRequestDto input, IToolExecutionCoordinator executions, ILifecycleManager lifecycle, IFullDuplexRunEngine engine, CancellationToken ct) =>
        {
            try
            {
                var result = await executions.ApplyRecoveryDecisionAsync(id, input.Decision, ct).ConfigureAwait(false);
                if (result.Status == "not_found") return Results.NotFound(new { code = "TOOL_EXECUTION_NOT_FOUND" });
                if (result.Status is "not_recoverable" or "run_not_active") return Results.Conflict(new { code = result.Status, message = result.Message });
                var active = result.ReplacementExecution ?? result.Execution;
                if (active is not null)
                {
                    await lifecycle.AppendEventAsync(active.RunId, "tool.execution.recovery_decided", new
                    {
                        execution_id = id,
                        replacement_execution_id = result.ReplacementExecution?.Id,
                        decision = input.Decision,
                        approval_id = result.ApprovalId
                    }, $"Tool recovery decision: {input.Decision}.", result.Status == "failed" ? "warning" : "info", active.TaskId, approvalId: result.ApprovalId, toolId: active.ToolId, cancellationToken: ct).ConfigureAwait(false);
                    await engine.EnqueueAsync(active.RunId, ct).ConfigureAwait(false);
                }
                return Results.Json(result, statusCode: result.Status == ToolDispatchStatus.AwaitingApproval ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_RECOVERY_DECISION", message = ex.Message }); }
        });
    }

    // ──────────────────────────────────────────────────────────
    // Approvals
    // ──────────────────────────────────────────────────────────
    private static void MapApprovalStubs(this IEndpointRouteBuilder app)
    {
        // Approval routes are mapped by ControlPlaneEndpoints.
    }

    // ──────────────────────────────────────────────────────────
    // Model Center (required by Gateway BFF composition)
    // ──────────────────────────────────────────────────────────
    private static void MapModelStubs(this IEndpointRouteBuilder app)
    {
        // These three are REQUIRED by the Gateway modelAgentCenter BFF.
        app.MapGet("/api/v1/model-provider-templates", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/model-providers", () => Results.Ok(Array.Empty<object>()));
        // Model provider, route, and settings routes are mapped by ControlPlaneEndpoints.
    }

    // ──────────────────────────────────────────────────────────
    // Prompt Fragments
    // ──────────────────────────────────────────────────────────
    private static void MapPromptStubs(this IEndpointRouteBuilder app)
    {
        // Prompt fragment routes are mapped by ControlPlaneEndpoints.
    }

    // ──────────────────────────────────────────────────────────
    // Agents
    // ──────────────────────────────────────────────────────────
    private static void MapAgentStubs(this IEndpointRouteBuilder app)
    {
        // Required by Gateway agent-center BFF
        // Agent routes are mapped by ControlPlaneEndpoints.
        // Agent evolution routes are mapped by EvolutionEndpoints.
    }

    // ──────────────────────────────────────────────────────────
    // Market / Extensions
    // ──────────────────────────────────────────────────────────
    private static void MapMarketExtensionStubs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/market/sources", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/v1/market/sources", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/market/sources/{sourceId}/refresh", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapGet("/api/v1/market/catalog", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/market/catalog/{catalogId}", () => Results.NotFound(new { code = "NOT_FOUND", message = "Catalog item not found." }));

        app.MapPost("/api/v1/extensions/install-preview", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/extensions/install", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapGet("/api/v1/extensions/installed", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/v1/extensions/{extensionId}/enable", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/extensions/{extensionId}/disable", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/extensions/{extensionId}/update", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapDelete("/api/v1/extensions/{extensionId}", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
    }

    // ──────────────────────────────────────────────────────────
    // MCP / ACP
    // ──────────────────────────────────────────────────────────
    private static void MapMcpAcpStubs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mcp/servers", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/mcp/servers/{serverId}/tools", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/v1/mcp/servers/{serverId}/reload", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));

        app.MapGet("/api/v1/acp/adapters", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/v1/acp/adapters/{adapterId}/probe", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
    }

    // ──────────────────────────────────────────────────────────
    // Debug Studio
    // ──────────────────────────────────────────────────────────
    private static void MapDebugStubs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/debug/traces", () => Results.Ok(new { items = Array.Empty<object>(), total = 0, limit = 50, offset = 0 }));
        app.MapGet("/api/v1/debug/traces/{traceId}", () => Results.NotFound(new { code = "NOT_FOUND" }));
        app.MapGet("/api/v1/debug/spans", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/debug/metrics", () => Results.Ok(new { buckets = Array.Empty<object>() }));
        app.MapGet("/api/v1/debug/diagnostics", () => Results.Ok(new { diagnostics = Array.Empty<object>() }));
        app.MapGet("/api/v1/debug/processes", () => Results.Ok(Array.Empty<object>()));

        // Simulation endpoints
        app.MapPost("/api/v1/debug/simulate/message", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/debug/simulate/model-response", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/debug/simulate/tool-result", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/debug/simulate/approval-decision", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/debug/simulate/state-patch", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));

        // Breakpoints
        app.MapGet("/api/v1/debug/breakpoints", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/v1/debug/breakpoints", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapDelete("/api/v1/debug/breakpoints/{id}", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
    }
}
