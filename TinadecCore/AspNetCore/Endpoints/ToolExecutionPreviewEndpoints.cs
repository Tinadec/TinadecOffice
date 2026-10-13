using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Read-only, bounded preview of one tool execution's result for the conversation feed.
/// <para>
/// Scope: <see cref="IToolExecutionCoordinator.FindAsync"/> only returns rows of the
/// caller's tenant and workspace, so an execution outside that scope answers the same
/// 404 as one that does not exist. The response carries the approval evidence projection
/// (never the stored parameters), a preview bounded by <c>max_bytes</c> (clamped to
/// 1–64 KiB, default 16 KiB), and only the stored safe error message.
/// </para>
/// </summary>
public static class ToolExecutionPreviewEndpoints
{
    public static IEndpointRouteBuilder MapToolExecutionPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tool-executions/{executionId:guid}/preview", async (
            Guid executionId,
            HttpContext context,
            IToolExecutionCoordinator executions,
            CancellationToken ct) =>
        {
            int? maxBytes = null;
            var text = context.Request.Query["max_bytes"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!int.TryParse(text, out var parsed))
                    return Problem(context, StatusCodes.Status400BadRequest, "invalid_max_bytes", "max_bytes must be an integer.");
                maxBytes = parsed;
            }

            var execution = await executions.FindAsync(executionId, ct).ConfigureAwait(false);
            if (execution is null)
                return Problem(context, StatusCodes.Status404NotFound, "tool_execution_not_found", "Tool execution was not found.");

            // The preview of a running call would change under the reader; never let a cache keep it.
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(ToolResultPreviewProjector.Project(execution, ToolResultPreviewProjector.ClampMaxBytes(maxBytes)));
        }).WithTags("ToolExecutions")
            .WithSummary("Bounded result preview of one tool execution")
            .Produces<ToolExecutionPreviewDto>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");

        return app;
    }

    private static IResult Problem(HttpContext context, int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["trace_id"] = context.TraceIdentifier });
}
