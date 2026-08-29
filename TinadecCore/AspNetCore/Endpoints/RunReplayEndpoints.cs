using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.DmaEA;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Read-only replay surface: rebuilds a durable run timeline from the event
/// journal for supervision replay and candidate evaluation. Never re-executes.
/// </summary>
public static class RunReplayEndpoints
{
    public static IEndpointRouteBuilder MapRunReplayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/runs/{runId}/replay", async (string runId, IRunReplayService replay, CancellationToken ct) =>
        {
            var document = await replay.BuildReplayAsync(runId, ct);
            return document is null
                ? Results.NotFound(new { code = "NOT_FOUND", message = "Run replay was not found." })
                : Results.Ok(new
                {
                    run_id = document.RunId,
                    status = document.Status,
                    tasks = document.Tasks.Select(task => new
                    {
                        task_key = task.TaskKey,
                        status = task.Status,
                        summary = task.Summary,
                        evidence = task.Evidence
                    }),
                    supervision_rounds = document.SupervisionRounds.Select(round => new
                    {
                        revision_round = round.RevisionRound,
                        decision = round.Decision,
                        reasons = round.Reasons
                    }),
                    milestones = document.Milestones.Select(milestone => new
                    {
                        event_type = milestone.EventType,
                        timestamp = milestone.Timestamp
                    })
                });
        });
        return app;
    }
}
