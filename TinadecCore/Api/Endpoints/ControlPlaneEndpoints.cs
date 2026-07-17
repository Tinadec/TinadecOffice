using System.Text.Json;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Endpoints;

public static class ControlPlaneEndpoints
{
    public static WebApplication MapControlPlaneEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/model-providers", (ControlPlaneService service, CancellationToken ct) => service.ListProviders(ct));
        app.MapPost("/api/v1/model-providers", async (HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.SaveProvider(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), null, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapPut("/api/v1/model-providers/{id:guid}", async (Guid id, HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.SaveProvider(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), id, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapDelete("/api/v1/model-providers/{id:guid}", (Guid id, HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.DeleteProvider(id, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapGet("/api/v1/model-provider-templates", () => Results.Ok(new[] { new { provider_family = "openai-compatible", driver = "openai", display_name = "OpenAI-compatible", connection_kind = "api-key", credential_kind = "api-key", summary = "OpenAI-compatible HTTP model provider", contributor_description = "Core built-in template", default_base_url = "https://api.openai.com/v1", default_model = "gpt-4o-mini", default_timeout_seconds = 60, capabilities = new { supports_streaming = true, supports_tools = true, supports_json_mode = true, supports_system_prompt = true, requires_workspace = false, credential_kind = "api-key", health_status = "unknown" } } }));
        app.MapGet("/api/v1/model-routes", (ControlPlaneService service, CancellationToken ct) => service.ListRoutes(ct));
        app.MapPut("/api/v1/model-routes/{purpose}", async (string purpose, HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.SaveRoute(purpose, await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapGet("/api/v1/model-settings", () => Results.Ok(new { base_url = "", model = "", has_api_key = false, revision = 0L, updated_at = DateTimeOffset.UtcNow }));
        app.MapPut("/api/v1/model-settings", () => Results.Json(new { code = "capability_unavailable", message = "Use model-providers for persisted provider configuration." }, statusCode: 501));

        app.MapGet("/api/v1/prompt-fragments", (ControlPlaneService service, CancellationToken ct) => service.ListPrompts(ct));
        app.MapPost("/api/v1/prompt-fragments", async (HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.SavePrompt(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), null, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapPut("/api/v1/prompt-fragments/{id:guid}", async (Guid id, HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.SavePrompt(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), id, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapDelete("/api/v1/prompt-fragments/{id:guid}", (Guid id, ControlPlaneService service, CancellationToken ct) => service.DeletePrompt(id, ct));
        app.MapPost("/api/v1/prompt-fragments/{id:guid}/clone", () => Results.Json(new { code = "capability_unavailable", message = "Clone requires an explicit new fragment identity." }, statusCode: 501));
        app.MapGet("/api/v1/prompt-fragments/{id:guid}/versions", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/v1/prompt-fragments/{id:guid}/versions", () => Results.Json(new { code = "capability_unavailable", message = "Use PUT to create an immutable prompt version." }, statusCode: 501));
        app.MapPost("/api/v1/prompt-fragments/{id:guid}/rollback", () => Results.Json(new { code = "capability_unavailable", message = "Rollback endpoint is not enabled in this runtime." }, statusCode: 501));
        app.MapGet("/api/v1/prompt-fragments/{id:guid}/effectiveness", (Guid id) => Results.Ok(new { fragment_id = id, active_version = 0, total_invocations = 0, positive_signals = 0, negative_signals = 0, effectiveness_score = 0d, last_evaluated_at = DateTimeOffset.UtcNow, versions = Array.Empty<object>() }));
        app.MapGet("/api/v1/prompt-fragments/effectiveness", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/v1/prompt-fragments/{id:guid}/signals", () => Results.Json(new { code = "capability_unavailable", message = "Prompt signal recording requires an active run." }, statusCode: 501));
        app.MapPost("/api/v1/prompt-fragments/{id:guid}/compare", () => Results.Json(new { code = "capability_unavailable", message = "Prompt comparison requires version telemetry." }, statusCode: 501));
        app.MapPost("/api/v1/prompt-context/preview", () => Results.Json(new { code = "capability_unavailable", message = "Prompt context preview requires the active context assembler runtime." }, statusCode: 501));

        app.MapGet("/api/v1/agents", (ControlPlaneService service, CancellationToken ct) => service.ListAgents(ct));
        app.MapPut("/api/v1/agents/{id:guid}", async (Guid id, HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.SaveAgent(id, await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapPut("/api/v1/agents/{id:guid}/mode", () => Results.Json(new { code = "capability_unavailable", message = "Agent mode changes require a versioned profile update." }, statusCode: 501));
        app.MapGet("/api/v1/agent-modes", () => Results.Ok(new[] { new { id = "default", display_name = "Default", summary = "Standard agent mode", max_parallel_executors = 1, worktree_isolation = false, approval_required = true, budget_policy = "bounded" } }));
        app.MapGet("/api/v1/agent-candidates", () => Results.Ok(Array.Empty<object>()));

        app.MapGet("/api/v1/approvals", (string? status, ControlPlaneService service, CancellationToken ct) => service.ListApprovals(status, ct));
        app.MapPost("/api/v1/approvals", async (HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.CreateApproval(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), ct));
        app.MapPost("/api/v1/approvals/{id:guid}/decision", async (Guid id, HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.DecideApproval(id, await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), ct));
        return app;
    }
}
