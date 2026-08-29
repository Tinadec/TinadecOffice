using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Agent evolution surface: a durable alias of the agent-candidate review flow.
/// Generate persists a reviewed proposal from a run-scoped evolution agent;
/// proposals list proposals and reject them. Promotion is review-driven: the
/// endpoint sanitizes the immutable proposal, publishes an immutable agent
/// version through the AgentConfiguration boundary, and records the decision.
/// Canary and activation remain future pipeline stages. Model generation
/// happens inside the run (experience curator); this endpoint only persists
/// and reviews candidate proposals.
/// </summary>
public static class EvolutionEndpoints
{
    public static IEndpointRouteBuilder MapEvolutionEndpoints(this IEndpointRouteBuilder app)
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

        // Promotion is a human review decision: sanitize the immutable proposal,
        // publish an immutable agent version, and record the decision. Canary and
        // activation remain future pipeline stages.
        app.MapPost("/api/v1/agent-evolution/proposals/{candidateId}/promote", async (Guid candidateId, ReviewDecisionRequest? request, IAgentInstanceService instances, TinadecCore.AgentConfiguration.IAgentConfigurationService configurations, CancellationToken ct) =>
            await AgentCandidatePromotion.PromoteAsync(instances, configurations, candidateId, request?.Reason, ct));

        // Evaluation evidence for human review: the proposal plus the replayed
        // source run (task outcomes, supervision rounds, milestones).
        app.MapGet("/api/v1/agent-evolution/proposals/{candidateId}/evaluation", async (Guid candidateId, IAgentInstanceService instances, IRunReplayService replay, CancellationToken ct) =>
        {
            try
            {
                var (candidate, proposal) = await instances.GetCandidateWithProposalAsync(candidateId, ct);
                var sourceReplay = await replay.BuildReplayAsync(candidate.SourceRunId.ToString(), ct);
                return Results.Ok(new
                {
                    candidate_id = candidate.Id,
                    name = candidate.Name,
                    layer = candidate.Layer,
                    agent_type = candidate.AgentType,
                    status = candidate.Status,
                    confidence = candidate.ConfidenceScore,
                    proposal,
                    source_run_replay = sourceReplay is null ? null : new
                    {
                        run_id = sourceReplay.RunId,
                        status = sourceReplay.Status,
                        tasks = sourceReplay.Tasks.Select(task => new
                        {
                            task_key = task.TaskKey,
                            status = task.Status,
                            summary = task.Summary,
                            evidence = task.Evidence
                        }),
                        supervision_rounds = sourceReplay.SupervisionRounds.Select(round => new
                        {
                            revision_round = round.RevisionRound,
                            decision = round.Decision,
                            reasons = round.Reasons
                        }),
                        milestones = sourceReplay.Milestones.Select(milestone => new
                        {
                            event_type = milestone.EventType,
                            timestamp = milestone.Timestamp
                        })
                    }
                });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { code = "NOT_FOUND", candidate_id = candidateId, message = "Agent candidate was not found." });
            }
        });

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
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "NOT_FOUND", message = "Agent proposal was not found." }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_DECISION", message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { code = "ALREADY_DECIDED", message = ex.Message }); }
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
