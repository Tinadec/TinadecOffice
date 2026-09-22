using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Human review endpoints for Core-owned memory and agent candidates. Memory
/// candidates can be promoted to immutable memory versions; generated agent
/// candidates are promoted through the sanitization/publish orchestrator, which
/// produces an immutable agent version bound later by an explicit mode edit.
/// Memory review and tool approvals are deliberately separate state machines
/// (never share decision tokens).
///
/// Every list filter is validated here rather than passed through unfilled: a queue
/// that answers "nothing to review" to a misspelled filter teaches the reviewer to
/// trust an empty shelf.
/// </summary>
public static class MemoryReviewEndpoints
{
    public static IEndpointRouteBuilder MapMemoryReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/memory-candidates", async (string? status, string? scope, string? kind, string? run_id, string? project_id, string? limit, ILongTermMemoryService memory, CancellationToken ct) =>
        {
            try
            {
                var candidates = await memory.ListCandidatesAsync(new MemoryCandidateQuery(
                    status,
                    scope,
                    kind,
                    ReviewVocabulary.ParseId(run_id, "run_id", "INVALID_RUN_ID"),
                    ReviewVocabulary.ParseId(project_id, "project_id", "INVALID_PROJECT_ID"),
                    ReviewVocabulary.ParseLimit(limit)), ct);
                return Results.Ok(candidates.Select(ToMemoryCandidate));
            }
            catch (ReviewFilterValueException ex) { return ReviewFilterFailure(ex); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_STATUS", message = ex.Message }); }
        });

        app.MapGet("/api/v1/memory-items", async (string? status, string? scope, string? kind, string? project_id, string? limit, ILongTermMemoryService memory, CancellationToken ct) =>
        {
            try
            {
                var items = await memory.ListItemsAsync(new MemoryItemQuery(
                    status,
                    scope,
                    kind,
                    ReviewVocabulary.ParseId(project_id, "project_id", "INVALID_PROJECT_ID"),
                    ReviewVocabulary.ParseLimit(limit)), ct);
                return Results.Ok(items.Select(item => new
                {
                    id = item.Id,
                    scope = item.Scope,
                    kind = item.Kind,
                    status = item.Status,
                    version = item.Version,
                    content = item.Content,
                    applicability = item.Applicability,
                    expiry_condition = item.ExpiryCondition,
                    created_at = item.CreatedAt,
                    updated_at = item.UpdatedAt,
                    revoked_at = item.RevokedAt
                }));
            }
            catch (ReviewFilterValueException ex) { return ReviewFilterFailure(ex); }
        });

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
                    applicability = item.Applicability,
                    expiry_condition = item.ExpiryCondition,
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
            catch (ReviewFilterValueException ex) { return ReviewFilterFailure(ex); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_STATUS", message = ex.Message }); }
        });

        app.MapPost("/api/v1/agent-candidates/{candidateId}/promote", async (Guid candidateId, ReviewDecisionRequest? request, IAgentInstanceService instances, IAgentConfigurationService configurations, CancellationToken ct) =>
            await AgentCandidatePromotion.PromoteAsync(instances, configurations, candidateId, request?.Reason, ct));

        app.MapPost("/api/v1/agent-candidates/{candidateId}/reject", async (Guid candidateId, ReviewDecisionRequest? request, IAgentInstanceService instances, CancellationToken ct) =>
            await DecideAgentAsync(instances, candidateId, "rejected", request, ct));

        return app;
    }

    /// <summary>
    /// A refused filter answers with the field it got wrong. Shared with the agent
    /// candidate queue, which narrows with the same vocabulary.
    /// </summary>
    internal static IResult ReviewFilterFailure(ReviewFilterValueException ex)
        => Results.BadRequest(new { code = ex.Code, message = ex.Message, field = ex.FieldName });

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
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Agent candidate was not found." }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_DECISION", message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { code = "ALREADY_DECIDED", message = ex.Message }); }
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
        evidence = candidate.Evidence,
        applicability = candidate.Applicability,
        expiry_condition = candidate.ExpiryCondition,
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
