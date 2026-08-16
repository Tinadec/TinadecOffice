using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Endpoints;

/// <summary>
/// DmaEA runtime endpoints: SSE invoke-stream and orchestration projections.
/// The endpoint appends the user message (Desktop never double-writes) and passes
/// the message id as the run trigger. No fake success: without a chat route the
/// invoke-stream returns an error chunk and the run records a run.failed event.
/// </summary>
public static class DmaeaEndpoints
{
    public static WebApplication MapDmaeaEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/sessions/{sessionId}/invoke-stream", async (HttpContext context, string sessionId, InvokeStreamRequest request, IAgentOrchestrator orchestrator, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("{\"code\":\"INVALID_SESSION_ID\"}", ct);
                return;
            }
            if (request is null || string.IsNullOrWhiteSpace(request.Content))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("{\"code\":\"INVALID_MESSAGE\"}", ct);
                return;
            }

            StoredMessage message;
            try
            {
                message = await sessions.AddMessageAsync(sessionGuid, request.Content, "user", runId: null, ct);
            }
            catch (KeyNotFoundException)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("{\"code\":\"NOT_FOUND\",\"message\":\"Session was not found.\"}", ct);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            try
            {
                var result = await orchestrator.OrchestrateAsync(sessionId, request.Content, message.Id.ToString(), ct);
                if (result.Success)
                {
                    await WriteChunkAsync(context, new
                    {
                        run_id = result.RunId,
                        session_id = sessionId,
                        purpose = "dual_layer",
                        provider_instance_id = "",
                        effective_model = (string?)null,
                        kind = "done",
                        delta = (string?)null,
                        tool_call_delta = (object?)null,
                        usage = (object?)null,
                        finish_reason = (string?)null,
                        error_category = (string?)null,
                        is_retryable = false,
                        safe_error_message = (string?)null,
                        fallback_provider_selected = false,
                        error_provider_id = (string?)null
                    }, ct);
                }
                else
                {
                    await WriteChunkAsync(context, new
                    {
                        run_id = result.RunId,
                        session_id = sessionId,
                        purpose = "dual_layer",
                        provider_instance_id = "",
                        effective_model = (string?)null,
                        kind = "error",
                        delta = (string?)null,
                        tool_call_delta = (object?)null,
                        usage = (object?)null,
                        finish_reason = (string?)null,
                        error_category = "runtime",
                        is_retryable = false,
                        safe_error_message = result.Summary,
                        fallback_provider_selected = false,
                        error_provider_id = (string?)null
                    }, ct);
                }
            }
            catch (Exception ex)
            {
                await WriteChunkAsync(context, new
                {
                    run_id = "",
                    session_id = sessionId,
                    purpose = "dual_layer",
                    provider_instance_id = "",
                    effective_model = (string?)null,
                    kind = "error",
                    delta = (string?)null,
                    tool_call_delta = (object?)null,
                    usage = (object?)null,
                    finish_reason = (string?)null,
                    error_category = "runtime",
                    is_retryable = false,
                    safe_error_message = ex.Message,
                    fallback_provider_selected = false,
                    error_provider_id = (string?)null
                }, ct);
            }
        });

        app.MapGet("/api/v1/sessions/{sessionId}/orchestration", async (string sessionId, StorageLifecycleService lifecycle, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            var session = await sessions.FindAsync(sessionGuid, ct);
            if (session is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });
            var runs = await lifecycle.ListRunsAsync(sessionGuid, ct);
            var run = runs.FirstOrDefault();
            if (run is null) return Results.Json(new { run = (object?)null, graph = (object?)null, nodes = Array.Empty<object>(), assignments = Array.Empty<object>(), step_results = Array.Empty<object>(), context_packs = Array.Empty<object>(), supervision_findings = Array.Empty<object>() }, options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
            var events = await lifecycle.ReplayEventsAsync(sessionGuid, 0, ct);
            var nodes = events.Where(e => e.EventType == "task.assigned" || e.EventType == "step.result.created")
                .Select(e => new
                {
                    id = PayloadString(e.Payload, "task_node_id"),
                    graph_id = PayloadString(e.Payload, "graph_id"),
                    run_id = run.Id.ToString(),
                    session_id = sessionId,
                    title = PayloadString(e.Payload, "title") ?? "",
                    description = PayloadString(e.Payload, "description") ?? "",
                    status = e.EventType == "step.result.created" ? PayloadString(e.Payload, "status") ?? "completed" : "assigned",
                    priority = 1,
                    risk = "medium",
                    success_criteria = Array.Empty<string>(),
                    dependencies = Array.Empty<string>(),
                    required_capabilities = Array.Empty<string>(),
                    created_at = e.Timestamp,
                    updated_at = e.Timestamp
                })
                .ToList();
            var stepResults = events.Where(e => e.EventType == "step.result.created")
                .Select(e => new
                {
                    id = PayloadString(e.Payload, "task_node_id"),
                    run_id = run.Id.ToString(),
                    task_node_id = PayloadString(e.Payload, "task_node_id"),
                    agent_id = PayloadString(e.Payload, "agent_id") ?? "",
                    status = PayloadString(e.Payload, "status") ?? "completed",
                    summary = PayloadString(e.Payload, "summary") ?? "",
                    evidence = PayloadArray(e.Payload, "evidence"),
                    created_at = e.Timestamp
                })
                .ToList();
            return Results.Json(new
            {
                run = new
                {
                    id = run.Id.ToString(),
                    session_id = sessionId,
                    user_message_id = run.TriggerMessageId.ToString(),
                    status = run.Status,
                    summary = run.Summary ?? "",
                    created_at = run.CreatedAt,
                    updated_at = run.UpdatedAt
                },
                graph = (object?)null,
                nodes,
                assignments = Array.Empty<object>(),
                step_results = stepResults,
                context_packs = Array.Empty<object>(),
                supervision_findings = Array.Empty<object>()
            }, options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
            });
        });

        app.MapGet("/api/v1/sessions/{sessionId}/tool-executions", async (string sessionId, StorageLifecycleService lifecycle, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            if (await sessions.FindAsync(sessionGuid, ct) is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });
            var events = await lifecycle.ReplayEventsAsync(sessionGuid, 0, ct);
            var items = events.Where(e => e.EventType == "step.result.created" || e.EventType == "task.assigned").Select(e => new
            {
                id = PayloadString(e.Payload, "task_node_id"),
                run_id = PayloadString(e.Payload, "run_id") ?? "",
                session_id = sessionId,
                tool_id = PayloadString(e.Payload, "tool_id") ?? "",
                tool_display_name = "",
                source = "dmaea",
                provider_layer = "execution",
                risk = "medium",
                requires_approval = false,
                status = e.EventType == "step.result.created" ? PayloadString(e.Payload, "status") ?? "completed" : "pending",
                approval_id = (string?)null,
                step_result_id = PayloadString(e.Payload, "task_node_id"),
                summary = PayloadString(e.Payload, "summary") ?? "",
                evidence = PayloadArray(e.Payload, "evidence"),
                requested_at = e.Timestamp,
                updated_at = e.Timestamp,
                duration_ms = 0L,
                requested_seq = e.Payload.TryGetValue("sequence", out var seq) ? (long)(seq is JsonElement je && je.ValueKind == JsonValueKind.Number ? je.GetInt64() : 0) : 0L,
                updated_seq = 0L,
                event_types = new[] { e.EventType },
                checkpoint_summary = ""
            }).ToList();
            return Results.Ok(items);
        });

        app.MapGet("/api/v1/sessions/{sessionId}/task-nodes", async (string sessionId, StorageLifecycleService lifecycle, ProjectSessionStore sessions, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            if (await sessions.FindAsync(sessionGuid, ct) is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." });
            var events = await lifecycle.ReplayEventsAsync(sessionGuid, 0, ct);
            var nodes = events.Where(e => e.EventType == "task.assigned" || e.EventType == "step.result.created").Select(e => new
            {
                id = PayloadString(e.Payload, "task_node_id"),
                graph_id = PayloadString(e.Payload, "graph_id") ?? "",
                run_id = PayloadString(e.Payload, "run_id") ?? "",
                session_id = sessionId,
                title = PayloadString(e.Payload, "title") ?? "",
                description = PayloadString(e.Payload, "description") ?? "",
                status = e.EventType == "step.result.created" ? PayloadString(e.Payload, "status") ?? "completed" : "assigned",
                priority = 1,
                risk = "medium",
                success_criteria = Array.Empty<string>(),
                dependencies = Array.Empty<string>(),
                required_capabilities = Array.Empty<string>(),
                created_at = e.Timestamp,
                updated_at = e.Timestamp
            }).ToList();
            return Results.Ok(nodes);
        });

        return app;
    }

    private static async Task WriteChunkAsync(HttpContext context, object chunk, CancellationToken ct)
    {
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n", ct);
    }

    private static JsonElement PayloadRoot(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue("payload", out var inner) && inner is JsonElement { ValueKind: JsonValueKind.Object } obj) return obj;
        return default;
    }

    private static string? PayloadString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static IReadOnlyList<string> PayloadArray(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        return [];
    }
}
