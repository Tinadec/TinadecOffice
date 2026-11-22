using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// HTTP surface for Phase 11 Four-Layer Memory &amp; Agentic Context Management:
/// TOML policy inspection/update, bidirectional Markdown documents, structured
/// item ledger + 1-click revert, and conscious conversation history recall.
/// </summary>
public static class ContextMemoryEndpoints
{
    public static IEndpointRouteBuilder MapContextMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/context-memory/policy", async (IContextMemoryService service, CancellationToken ct) =>
            Results.Ok(await service.GetPolicyAsync(ct).ConfigureAwait(false)))
            .Produces<ContextMemoryPolicyDto>();

        app.MapPut("/api/v1/context-memory/policy", async (
            UpdateContextMemoryPolicyRequest request,
            HttpRequest httpRequest,
            IContextMemoryService service,
            CancellationToken ct) =>
        {
            try
            {
                var ifMatch = httpRequest.Headers.IfMatch.ToString();
                var effective = string.IsNullOrWhiteSpace(request.ExpectedContentHash)
                    && string.IsNullOrWhiteSpace(request.ExpectedHash)
                    && !string.IsNullOrWhiteSpace(ifMatch)
                    ? new UpdateContextMemoryPolicyRequest
                    {
                        Strategy = request.Strategy,
                        RecallFidelity = request.RecallFidelity,
                        CompactionTriggerRatio = request.CompactionTriggerRatio,
                        RecentMessageLimit = request.RecentMessageLimit,
                        DefaultTokenBudget = request.DefaultTokenBudget,
                        AllowCrossSessionRecall = request.AllowCrossSessionRecall,
                        OffloadLargeToolPayloadBytes = request.OffloadLargeToolPayloadBytes,
                        EvolutionMode = request.EvolutionMode,
                        CandidateOnly = request.CandidateOnly,
                        RetrievalLimit = request.RetrievalLimit,
                        CoreMarkdownMaxTokens = request.CoreMarkdownMaxTokens,
                        ProjectMarkdownProjection = request.ProjectMarkdownProjection,
                        ProjectMemoryPath = request.ProjectMemoryPath,
                        CompanionCanWriteProjectMemory = request.CompanionCanWriteProjectMemory,
                        ExpectedContentHash = ifMatch.Trim('"')
                    }
                    : request;
                var updated = await service.UpdatePolicyAsync(effective, ct).ConfigureAwait(false);
                return Results.Ok(updated);
            }
            catch (ConfigurationDocumentException ex)
            {
                return ex.Code == "configuration_conflict"
                    ? Results.Conflict(new { code = "POLICY_CONFLICT", message = ex.Message, diagnostics = ex.Diagnostics })
                    : Results.BadRequest(new { code = "INVALID_POLICY", message = ex.Message, diagnostics = ex.Diagnostics });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { code = "INVALID_POLICY", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { code = "POLICY_CONFLICT", message = ex.Message });
            }
        })
        .Produces<ContextMemoryPolicyDto>()
        .ProducesProblem(400)
        .ProducesProblem(409);

        app.MapGet("/api/v1/memories/documents/{scope}", async (
            string scope,
            Guid? project_id,
            string? workspace_root,
            Guid? companion_id,
            HttpResponse httpResponse,
            IContextMemoryService service,
            CancellationToken ct) =>
        {
            try
            {
                var doc = await service.GetDocumentAsync(scope, project_id, workspace_root, companion_id, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(doc.ContentHash))
                {
                    httpResponse.Headers.ETag = '"' + doc.ContentHash + '"';
                }
                return Results.Ok(doc);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "INVALID_SCOPE", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { code = "INVALID_PATH", message = ex.Message });
            }
        })
        .Produces<MemoryDocumentDto>()
        .ProducesProblem(400);

        app.MapPut("/api/v1/memories/documents/{scope}", async (
            string scope,
            Guid? project_id,
            string? workspace_root,
            Guid? companion_id,
            UpdateMemoryDocumentRequest request,
            HttpRequest httpRequest,
            HttpResponse httpResponse,
            IContextMemoryService service,
            CancellationToken ct) =>
        {
            try
            {
                var ifMatch = httpRequest.Headers.IfMatch.ToString();
                var effective = new UpdateMemoryDocumentRequest
                {
                    Markdown = request.Markdown,
                    ProjectId = request.ProjectId ?? project_id,
                    WorkspaceRoot = request.WorkspaceRoot ?? workspace_root,
                    CompanionId = request.CompanionId ?? companion_id,
                    ExpectedContentHash = request.ExpectedContentHash ?? request.ExpectedHash ?? (string.IsNullOrWhiteSpace(ifMatch) ? null : ifMatch.Trim('"')),
                    ExpectedHash = request.ExpectedHash,
                    SourceSessionId = request.SourceSessionId,
                    SourceMessageSeq = request.SourceMessageSeq,
                    Reason = request.Reason,
                    IsCompanionWriter = request.IsCompanionWriter
                };
                var doc = await service.UpdateDocumentAsync(scope, effective, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(doc.ContentHash))
                {
                    httpResponse.Headers.ETag = '"' + doc.ContentHash + '"';
                }
                return Results.Ok(doc);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { code = "COMPANION_PROJECT_WRITE_FORBIDDEN", message = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "INVALID_DOCUMENT", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { code = "DOCUMENT_CONFLICT", message = ex.Message });
            }
        })
        .Produces<MemoryDocumentDto>()
        .ProducesProblem(400)
        .ProducesProblem(403)
        .ProducesProblem(409);

        app.MapGet("/api/v1/memories/items", async (
            string? scope,
            Guid? project_id,
            Guid? companion_id,
            string? status,
            string? kind,
            string? query,
            string? q,
            bool? include_versions,
            bool? include_history,
            int? limit,
            IContextMemoryService service,
            CancellationToken ct) =>
        {
            try
            {
                var list = await service.ListItemsAsync(
                    scope,
                    project_id,
                    companion_id,
                    status,
                    kind,
                    query ?? q,
                    include_versions ?? include_history ?? true,
                    limit ?? 50,
                    ct).ConfigureAwait(false);
                return Results.Ok(list);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "INVALID_QUERY", message = ex.Message });
            }
        })
        .Produces<MemoryItemListResultDto>()
        .ProducesProblem(400);

        app.MapPost("/api/v1/memories/items", async (
            UpsertMemoryItemRequest request,
            IContextMemoryService service,
            CancellationToken ct) =>
        {
            try
            {
                var item = await service.UpsertItemAsync(request, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/memories/items/{item.Id}", item);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { code = "COMPANION_PROJECT_WRITE_FORBIDDEN", message = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "NOT_FOUND", message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "INVALID_MEMORY_ITEM", message = ex.Message });
            }
        })
        .Produces<MemoryItemLedgerDto>(StatusCodes.Status201Created)
        .ProducesProblem(400)
        .ProducesProblem(403)
        .ProducesProblem(404);

        app.MapDelete("/api/v1/memories/items/{id:guid}", async (
            Guid id,
            string? reason,
            IContextMemoryService service,
            CancellationToken ct) =>
        {
            try
            {
                var item = await service.RevokeItemAsync(id, reason, ct).ConfigureAwait(false);
                return Results.Ok(item);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "NOT_FOUND", message = ex.Message });
            }
        })
        .Produces<MemoryItemLedgerDto>()
        .ProducesProblem(404);

        app.MapPost("/api/v1/memories/items/{id:guid}/revert", async (
            Guid id,
            HttpRequest httpRequest,
            IContextMemoryService service,
            CancellationToken ct) =>
        {
            try
            {
                RevertMemoryItemRequest? body = null;
                if (httpRequest.HasJsonContentType() && httpRequest.ContentLength is not 0)
                {
                    try { body = await httpRequest.ReadFromJsonAsync<RevertMemoryItemRequest>(ct).ConfigureAwait(false); }
                    catch (System.Text.Json.JsonException) { body = null; }
                }
                var item = await service.RevertItemAsync(id, body, ct).ConfigureAwait(false);
                return Results.Ok(item);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "NOT_FOUND", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { code = "REVERT_CONFLICT", message = ex.Message });
            }
        })
        .Produces<MemoryItemLedgerDto>()
        .ProducesProblem(404)
        .ProducesProblem(409);

        app.MapPost("/api/v1/sessions/{sessionId:guid}/recall", async (
            Guid sessionId,
            ConversationRecallRequest request,
            IContextMemoryService service,
            CancellationToken ct) =>
        {
            try
            {
                var result = await service.RecallConversationHistoryAsync(sessionId, request, cancellationToken: ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "SESSION_NOT_FOUND", message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "INVALID_RECALL_REQUEST", message = ex.Message });
            }
        })
        .Produces<ConversationRecallResultDto>()
        .ProducesProblem(400)
        .ProducesProblem(404);

        return app;
    }
}
