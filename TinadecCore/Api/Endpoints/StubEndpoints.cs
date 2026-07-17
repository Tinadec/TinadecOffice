using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace TinadecCore.Api.Endpoints;

/// <summary>
/// Extension methods that register all stub Core endpoints
/// needed by the Gateway proxy and Desktop frontend.
/// GET endpoints return 200 with empty/default collections.
/// Write endpoints return 501 Not Implemented.
/// </summary>
public static class StubEndpoints
{
    public static WebApplication MapStubEndpoints(this WebApplication app)
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
    private static void MapReadinessStubs(this WebApplication app)
    {
        app.MapGet("/api/v1/doctor", () => Results.Ok(new
        {
            platform = "windows",
            agent_core_version = "0.1.0",
            checks = Array.Empty<object>()
        }));

        app.MapGet("/api/v1/model-readiness", () => Results.Ok(new
        {
            status = "warning",
            generated_at = DateTimeOffset.UtcNow,
            receipt_id = Guid.NewGuid().ToString("N"),
            provider_count = 0,
            ready_provider_count = 0,
            warning_provider_count = 0,
            blocked_provider_count = 0,
            route_count = 0,
            ready_route_count = 0,
            warning_route_count = 0,
            blocked_route_count = 0,
            providers = Array.Empty<object>(),
            routes = Array.Empty<object>(),
            design_notes = new[] { "No model providers configured — skeleton mode." }
        }));

        app.MapGet("/api/v1/model-catalog-readiness", () => Results.Ok(new
        {
            status = "warning",
            generated_at = DateTimeOffset.UtcNow,
            receipt_id = Guid.NewGuid().ToString("N"),
            template_count = 0,
            ready_template_count = 0,
            warning_template_count = 0,
            blocked_template_count = 0,
            runtime_module_count = 0,
            configured_provider_count = 0,
            advisory_probe_template_count = 0,
            templates = Array.Empty<object>(),
            design_notes = new[] { "No provider templates configured — skeleton mode." }
        }));

        app.MapGet("/api/v1/tool-layer-readiness", () => Results.Ok(new
        {
            status = "warning",
            generated_at = DateTimeOffset.UtcNow,
            runtime = "tinadec-core-maf-0.1.0",
            receipt_id = Guid.NewGuid().ToString("N"),
            tool_count = 0,
            ready_tool_count = 0,
            warning_tool_count = 0,
            blocked_tool_count = 0,
            execution_agent_count = 0,
            ready_agent_count = 0,
            warning_agent_count = 0,
            blocked_agent_count = 0,
            approval_gated_tool_count = 0,
            human_checkpoint_tool_count = 0,
            future_tool_count = 0,
            unresolved_scope_count = 0,
            tools = Array.Empty<object>(),
            agent_scopes = Array.Empty<object>(),
            design_notes = new[] { "No tools registered — skeleton mode." }
        }));
    }

    // ──────────────────────────────────────────────────────────
    // Projects / Sessions
    // ──────────────────────────────────────────────────────────
    private static void MapProjectSessionStubs(this WebApplication app)
    {
        app.MapPost("/api/v1/sessions/{sessionId}/invoke-stream", () => Results.Json(new { code = "NOT_IMPLEMENTED", message = "Invoke-stream is not implemented in skeleton mode." }, statusCode: 501));

        app.MapGet("/api/v1/sessions/{sessionId}/orchestration", () => Results.Ok(new
        {
            run = (object?)null,
            graph = (object?)null,
            nodes = Array.Empty<object>(),
            assignments = Array.Empty<object>(),
            step_results = Array.Empty<object>(),
            context_packs = Array.Empty<object>(),
            supervision_findings = Array.Empty<object>()
        }));

        app.MapGet("/api/v1/sessions/{sessionId}/tool-executions", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/sessions/{sessionId}/task-nodes", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/sessions/{sessionId}/context-packs", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/sessions/{sessionId}/supervision-findings", () => Results.Ok(Array.Empty<object>()));

    }

    // ──────────────────────────────────────────────────────────
    // Tools
    // ──────────────────────────────────────────────────────────
    private static void MapToolStubs(this WebApplication app)
    {
        app.MapGet("/api/v1/tools", () => Results.Ok(Array.Empty<object>()));

        app.MapGet("/api/v1/tools/search", () => Results.Ok(Array.Empty<object>()));

        app.MapPost("/api/v1/tools/shell", () => Results.Json(new { code = "NOT_IMPLEMENTED", message = "Shell tool execution is not implemented in skeleton mode." }, statusCode: 501));

        app.MapPost("/api/v1/runs/{runId}/tools/{toolId}/execute", () => Results.Json(new { code = "NOT_IMPLEMENTED", message = "Tool execution is not implemented in skeleton mode." }, statusCode: 501));
    }

    // ──────────────────────────────────────────────────────────
    // Approvals
    // ──────────────────────────────────────────────────────────
    private static void MapApprovalStubs(this WebApplication app)
    {
        // Approval routes are mapped by ControlPlaneEndpoints.
    }

    // ──────────────────────────────────────────────────────────
    // Model Center (required by Gateway BFF composition)
    // ──────────────────────────────────────────────────────────
    private static void MapModelStubs(this WebApplication app)
    {
        // These three are REQUIRED by the Gateway modelAgentCenter BFF.
        app.MapGet("/api/v1/model-provider-templates", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/model-providers", () => Results.Ok(Array.Empty<object>()));
        // Model provider, route, and settings routes are mapped by ControlPlaneEndpoints.
    }

    // ──────────────────────────────────────────────────────────
    // Prompt Fragments
    // ──────────────────────────────────────────────────────────
    private static void MapPromptStubs(this WebApplication app)
    {
        // Prompt fragment routes are mapped by ControlPlaneEndpoints.
    }

    // ──────────────────────────────────────────────────────────
    // Agents
    // ──────────────────────────────────────────────────────────
    private static void MapAgentStubs(this WebApplication app)
    {
        // Required by Gateway agent-center BFF
        // Agent routes are mapped by ControlPlaneEndpoints.

        // Agent evolution
        app.MapGet("/api/v1/agent-evolution/proposals", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/v1/agent-evolution/generate", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/agent-evolution/proposals/{candidateId}/promote", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
        app.MapPost("/api/v1/agent-evolution/proposals/{candidateId}/reject", () => Results.Json(new { code = "NOT_IMPLEMENTED" }, statusCode: 501));
    }

    // ──────────────────────────────────────────────────────────
    // Market / Extensions
    // ──────────────────────────────────────────────────────────
    private static void MapMarketExtensionStubs(this WebApplication app)
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
    private static void MapMcpAcpStubs(this WebApplication app)
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
    private static void MapDebugStubs(this WebApplication app)
    {
        app.MapGet("/api/v1/debug/traces", () => Results.Ok(new { items = Array.Empty<object>(), total = 0, limit = 50, offset = 0 }));
        app.MapGet("/api/v1/debug/traces/{traceId}", () => Results.NotFound(new { code = "NOT_FOUND" }));
        app.MapGet("/api/v1/debug/spans", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/v1/debug/metrics", () => Results.Ok(new { buckets = Array.Empty<object>() }));
        app.MapGet("/api/v1/debug/snapshot/{sessionId}", () => Results.Ok(new
        {
            session_id = "",
            runs = Array.Empty<object>(),
            tasks = Array.Empty<object>(),
            events = Array.Empty<object>()
        }));
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
