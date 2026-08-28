using System.Text.Json;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Runtime;

namespace TinadecCore.AspNetCore.Endpoints;

public static class ControlPlaneEndpoints
{
    public static IEndpointRouteBuilder MapControlPlaneEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/model-providers", (ControlPlaneService service, CancellationToken ct) => service.ListProviders(ct));
        app.MapPost("/api/v1/model-providers", async (HttpRequest request, ControlPlaneService service, CancellationToken ct) => await service.SaveProvider(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), null, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapPut("/api/v1/model-providers/{id:guid}", async (Guid id, HttpRequest request, ControlPlaneService service, CancellationToken ct) => await service.SaveProvider(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), id, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapDelete("/api/v1/model-providers/{id:guid}", (Guid id, HttpRequest request, ControlPlaneService service, CancellationToken ct) => service.DeleteProvider(id, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapPost("/api/v1/model-providers/{id:guid}/models/refresh", (Guid id, ControlPlaneService service, CancellationToken ct) => service.RefreshProviderModels(id, ct));
        app.MapGet("/api/v1/model-providers/cli/discover", (ControlPlaneService service, CancellationToken ct) => service.DiscoverCliRuntimes(ct));
        app.MapPost("/api/v1/model-providers/cli/connect", async (HttpRequest request, ControlPlaneService service, CancellationToken ct) => await service.ConnectCliRuntime(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), ct));
        app.MapGet("/api/v1/model-provider-templates", () => Results.Ok(new object[]
        {
            new { provider_family = "openai-compatible", driver = "openai", protocol = "openai-chat", display_name = "OpenAI-compatible", connection_kind = "api-key", credential_kind = "api-key", summary = "OpenAI-compatible HTTP model provider (chat completions)", contributor_description = "Core built-in template", default_base_url = "https://api.openai.com/v1", default_model = "gpt-4o-mini", default_timeout_seconds = 60, capabilities = new { supports_streaming = true, supports_tools = true, supports_json_mode = true, supports_system_prompt = true, requires_workspace = false, credential_kind = "api-key", health_status = "unknown" } },
            new { provider_family = "openai-compatible", driver = "openai-responses", protocol = "openai-responses", display_name = "OpenAI Responses", connection_kind = "api-key", credential_kind = "api-key", summary = "OpenAI Responses API model provider", contributor_description = "Core built-in template", default_base_url = "https://api.openai.com/v1", default_model = "gpt-4o-mini", default_timeout_seconds = 60, capabilities = new { supports_streaming = true, supports_tools = true, supports_json_mode = true, supports_system_prompt = true, requires_workspace = false, credential_kind = "api-key", health_status = "unknown" } },
            new { provider_family = "anthropic", driver = "anthropic", protocol = "anthropic-messages", display_name = "Anthropic Claude", connection_kind = "api-key", credential_kind = "api-key", summary = "Anthropic Messages API model provider", contributor_description = "Core built-in template", default_base_url = "https://api.anthropic.com/v1", default_model = "claude-sonnet-4-6", default_timeout_seconds = 60, capabilities = new { supports_streaming = true, supports_tools = true, supports_json_mode = false, supports_system_prompt = true, requires_workspace = false, credential_kind = "api-key", health_status = "unknown" } }
        }));
        app.MapGet("/api/v1/model-routes", (ControlPlaneService service, CancellationToken ct) => service.ListRoutes(ct));
        app.MapPut("/api/v1/model-routes/{purpose}", (string purpose, ModelRouteWriteRequestDto request, HttpRequest httpRequest, ControlPlaneService service, CancellationToken ct) => service.SaveRoute(purpose, request, httpRequest.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapGet("/api/v1/model-settings", () => Results.Ok(new { base_url = "", model = "", has_api_key = false, revision = 0L, updated_at = DateTimeOffset.UtcNow }));
        app.MapPut("/api/v1/model-settings", () => Results.Json(new { code = "capability_unavailable", message = "Use model-providers for persisted provider configuration." }, statusCode: 501));

        app.MapGet("/api/v1/prompt-fragments", (ControlPlaneService service, CancellationToken ct) => service.ListPrompts(ct));
        app.MapPost("/api/v1/prompt-fragments", async (HttpRequest request, ControlPlaneService service, CancellationToken ct) => await service.SavePrompt(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), null, request.Headers.IfMatch.FirstOrDefault(), ct));
        app.MapPut("/api/v1/prompt-fragments/{id:guid}", async (Guid id, HttpRequest request, ControlPlaneService service, CancellationToken ct) => await service.SavePrompt(await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct), id, request.Headers.IfMatch.FirstOrDefault(), ct));
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

        app.MapGet("/api/v1/approvals", (string? status, string? session_id, string? run_id, ControlPlaneService service, CancellationToken ct) => service.ListApprovals(status, session_id, run_id, ct));
        app.MapGet("/api/v1/approvals/{id:guid}", (Guid id, ControlPlaneService service, CancellationToken ct) => service.GetApproval(id, ct));
        app.MapPost("/api/v1/approvals/{id:guid}/decision", (Guid id, ApprovalDecisionRequestDto input, ControlPlaneService service, CancellationToken ct) => service.DecideApproval(id, input, ct));
        return app;
    }
}
