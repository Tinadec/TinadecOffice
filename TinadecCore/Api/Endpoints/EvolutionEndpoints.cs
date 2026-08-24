using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.Api.Endpoints;

/// <summary>
/// Agent evolution surface: a durable alias of the agent-candidate review flow.
/// Generate persists a reviewed proposal from a run-scoped evolution agent;
/// proposals list proposals and reject them. Profile publication is deliberately
/// fail-closed until the staged sanitization/evaluation/review/publish/canary/
/// activation pipeline exists. Model generation happens inside the run
/// (experience curator); this endpoint only persists candidate proposals.
/// </summary>
public static class EvolutionEndpoints
{
    public static WebApplication MapEvolutionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent-evolution/proposals", async (string? status, IAgentInstanceService instances, CancellationToken ct) =>
        {
            try
            {
                var candidates = await instances.ListCandidatesAsync(status, ct);
                return Results.Ok(candidates.Select(ToProposal));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_STATUS", message = ex.Message }); }
        });

        app.MapPost("/api/v1/agent-evolution/generate", async (HttpRequest request, IAgentInstanceService instances, CancellationToken ct) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<GenerateProposalRequest>(request.Body, cancellationToken: ct);
                if (body is null) return Results.BadRequest(new { code = "INVALID_PAYLOAD", message = "Request body is empty." });
                var candidate = await instances.CreateCandidateAsync(new AgentCandidateProposal(
                    body.SourceRunId,
                    body.SourceInstanceId,
                    body.Name,
                    body.Layer,
                    body.AgentType,
                    body.Confidence,
                    body.Proposal,
                    body.ProjectId), ct);
                return Results.Created($"/api/v1/agent-evolution/proposals/{candidate.Id}", ToProposal(candidate));
            }
            catch (JsonException) { return Results.BadRequest(new { code = "INVALID_PAYLOAD", message = "Request body is not valid JSON." }); }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Source agent instance was not found." }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_PROPOSAL", message = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { code = "FORBIDDEN_PROPOSAL", message = ex.Message }, statusCode: 403); }
        });

        // Keep the legacy route as an explicit fail-closed compatibility surface.
        // A candidate cannot become a profile through one review call; the staged
        // evolution pipeline will own publication once it is implemented.
        app.MapPost("/api/v1/agent-evolution/proposals/{candidateId}/promote", (Guid candidateId) =>
            Results.Conflict(new
            {
                code = "candidate_pipeline_required",
                candidate_id = candidateId,
                message = "Candidate promotion is disabled until sanitization, evaluation, review, publish, canary, and activation complete."
            }));

        app.MapPost("/api/v1/agent-evolution/proposals/{candidateId}/reject", async (Guid candidateId, ReviewDecisionRequest? request, IAgentInstanceService instances, CancellationToken ct) =>
            await DecideAsync(instances, candidateId, "rejected", request, ct));

        return app;
    }

    private static async Task<IResult> DecideAsync(IAgentInstanceService instances, Guid candidateId, string decision, ReviewDecisionRequest? request, CancellationToken ct)
    {
        try
        {
            var candidate = await instances.DecideCandidateAsync(candidateId, decision, request?.Reason, ct);
            return Results.Ok(ToProposal(candidate));
        }
        catch (AgentCandidatePipelineRequiredException ex) { return Results.Conflict(new { code = "candidate_pipeline_required", candidate_id = ex.CandidateId, message = ex.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Agent proposal was not found." }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_DECISION", message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { code = "ALREADY_DECIDED", message = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { code = "FORBIDDEN_PROMOTION", message = ex.Message }, statusCode: 403); }
    }

    private static object ToProposal(AgentCandidateRecord candidate) => new
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
