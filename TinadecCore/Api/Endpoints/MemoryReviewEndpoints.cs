using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.Api.Endpoints;

/// <summary>
/// Human review endpoints for Core-owned memory and agent candidates. Memory
/// candidates can be promoted to immutable memory versions; generated agent
/// candidates are proposals only and remain fail-closed until the staged evolution
/// pipeline exists. Memory review and tool approvals are deliberately separate state
/// machines (never share decision tokens).
/// </summary>
public static class MemoryReviewEndpoints
{
    public static WebApplication MapMemoryReviewEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/memory-candidates", async (string? status, ILongTermMemoryService memory, CancellationToken ct) =>
        {
            try
            {
                var candidates = await memory.ListCandidatesAsync(status, ct);
                return Results.Ok(candidates.Select(ToMemoryCandidate));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_STATUS", message = ex.Message }); }
        });

        app.MapGet("/api/v1/memory-items", async (ILongTermMemoryService memory, CancellationToken ct) =>
            Results.Ok((await memory.ListItemsAsync(ct)).Select(item => new
            {
                id = item.Id,
                scope = item.Scope,
                kind = item.Kind,
                status = item.Status,
                version = item.Version,
                content = item.Content,
                created_at = item.CreatedAt,
                updated_at = item.UpdatedAt,
                revoked_at = item.RevokedAt
            })));

        app.MapPost("/api/v1/memory-candidates/{candidateId}/promote", async (Guid candidateId, ReviewDecisionRequest? request, ILongTermMemoryService memory, CancellationToken ct) =>
            await DecideMemoryAsync(memory, candidateId, "promoted", request, ct));

        app.MapPost("/api/v1/memory-candidates/{candidateId}/reject", async (Guid candidateId, ReviewDecisionRequest? request, ILongTermMemoryService memory, CancellationToken ct) =>
            await DecideMemoryAsync(memory, candidateId, "rejected", request, ct));

        app.MapPost("/api/v1/memory-items/{itemId}/revoke", async (Guid itemId, ReviewDecisionRequest? request, ILongTermMemoryService memory, CancellationToken ct) =>
        {
            try
            {
                var item = await memory.RevokeAsync(itemId, request?.Reason, ct);
                return Results.Ok(new
                {
                    id = item.Id,
                    scope = item.Scope,
                    kind = item.Kind,
                    status = item.Status,
                    version = item.Version,
                    revoked_at = item.RevokedAt
                });
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Memory item was not found." }); }
        });

        app.MapGet("/api/v1/agent-candidates", async (string? status, IAgentInstanceService instances, CancellationToken ct) =>
        {
            try
            {
                var candidates = await instances.ListCandidatesAsync(status, ct);
                return Results.Ok(candidates.Select(ToAgentCandidate));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_STATUS", message = ex.Message }); }
        });

        app.MapPost("/api/v1/agent-candidates/{candidateId}/promote", async (Guid candidateId, ReviewDecisionRequest? request, IAgentInstanceService instances, CancellationToken ct) =>
            await DecideAgentAsync(instances, candidateId, "promoted", request, ct));

        app.MapPost("/api/v1/agent-candidates/{candidateId}/reject", async (Guid candidateId, ReviewDecisionRequest? request, IAgentInstanceService instances, CancellationToken ct) =>
            await DecideAgentAsync(instances, candidateId, "rejected", request, ct));

        return app;
    }

    private static async Task<IResult> DecideMemoryAsync(ILongTermMemoryService memory, Guid candidateId, string decision, ReviewDecisionRequest? request, CancellationToken ct)
    {
        try
        {
            var candidate = await memory.DecideCandidateAsync(candidateId, decision, request?.Reason, ct);
            return Results.Ok(ToMemoryCandidate(candidate));
        }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Memory candidate was not found." }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_DECISION", message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { code = "ALREADY_DECIDED", message = ex.Message }); }
    }

    private static async Task<IResult> DecideAgentAsync(IAgentInstanceService instances, Guid candidateId, string decision, ReviewDecisionRequest? request, CancellationToken ct)
    {
        try
        {
            var candidate = await instances.DecideCandidateAsync(candidateId, decision, request?.Reason, ct);
            return Results.Ok(ToAgentCandidate(candidate));
        }
        catch (AgentCandidatePipelineRequiredException ex) { return Results.Conflict(new { code = "candidate_pipeline_required", candidate_id = ex.CandidateId, message = ex.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Agent candidate was not found." }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_DECISION", message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { code = "ALREADY_DECIDED", message = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { code = "FORBIDDEN_PROMOTION", message = ex.Message }, statusCode: 403); }
    }

    private static object ToMemoryCandidate(TinadecCore.Abstractions.Ports.MemoryCandidate candidate) => new
    {
        id = candidate.Id,
        source_run_id = candidate.SourceRunId,
        generated_by_instance_id = candidate.GeneratedByInstanceId,
        scope = candidate.Scope,
        kind = candidate.Kind,
        status = candidate.Status,
        confidence = candidate.Confidence,
        content = candidate.Content,
        decision_reason = candidate.DecisionReason,
        promoted_memory_item_id = candidate.PromotedMemoryItemId,
        created_at = candidate.CreatedAt,
        updated_at = candidate.UpdatedAt
    };

    private static object ToAgentCandidate(AgentCandidateRecord candidate) => new
    {
        id = candidate.Id,
        source_run_id = candidate.SourceRunId,
        source_instance_id = candidate.SourceInstanceId,
        generated_by_instance_id = candidate.GeneratedByInstanceId,
        name = candidate.Name,
        layer = candidate.Layer,
        agent_type = candidate.AgentType,
        status = candidate.Status,
        confidence = candidate.ConfidenceScore,
        promoted_agent_id = candidate.PromotedAgentId,
        decision_reason = candidate.DecisionReason,
        created_at = candidate.CreatedAt,
        updated_at = candidate.UpdatedAt
    };
}
