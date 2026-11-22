using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TinadecCore.DmaEA;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Session-scoped structured plan CRUD, phase/step PATCH updates, and explicit
/// approval gate endpoints. Backed by <see cref="SessionPlanService"/>.
/// </summary>
public static class SessionPlanEndpoints
{
    public static IEndpointRouteBuilder MapSessionPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sessions/{sessionId:guid}/plan")
            .WithTags("SessionPlan");

        group.MapGet(string.Empty, async (HttpContext context, Guid sessionId, SessionPlanService plans, CancellationToken ct) =>
        {
            try
            {
                var dto = await plans.GetAsync(sessionId, ct).ConfigureAwait(false);
                context.Response.Headers.ETag = $"\"{dto.Revision}\"";
                return Results.Ok(dto);
            }
            catch (SessionPlanException ex)
            {
                return ErrorResult(ex);
            }
        })
        .Produces<SessionPlanDto>(StatusCodes.Status200OK);

        group.MapPut(string.Empty, async (HttpContext context, Guid sessionId, SessionPlanPutRequestDto request, SessionPlanService plans, CancellationToken ct) =>
        {
            if (!TryParseIfMatch(context.Request, out var ifMatchRevision))
            {
                return Results.BadRequest(new { code = "INVALID_IF_MATCH", message = "If-Match header must be a numeric plan revision." });
            }

            try
            {
                var dto = await plans.PutAsync(
                    sessionId,
                    request,
                    ifMatchRevision ?? request.ExpectedRevision,
                    runId: null,
                    ct).ConfigureAwait(false);
                context.Response.Headers.ETag = $"\"{dto.Revision}\"";
                return Results.Ok(dto);
            }
            catch (SessionPlanException ex)
            {
                return ErrorResult(ex);
            }
        })
        .Produces<SessionPlanDto>(StatusCodes.Status200OK);

        group.MapPatch(string.Empty, async (HttpContext context, Guid sessionId, SessionPlanPatchRequestDto request, SessionPlanService plans, CancellationToken ct) =>
        {
            if (!TryParseIfMatch(context.Request, out var ifMatchRevision))
            {
                return Results.BadRequest(new { code = "INVALID_IF_MATCH", message = "If-Match header must be a numeric plan revision." });
            }

            try
            {
                var dto = await plans.PatchAsync(
                    sessionId,
                    request,
                    ifMatchRevision ?? request.ExpectedRevision,
                    runId: null,
                    ct).ConfigureAwait(false);
                context.Response.Headers.ETag = $"\"{dto.Revision}\"";
                return Results.Ok(dto);
            }
            catch (SessionPlanException ex)
            {
                return ErrorResult(ex);
            }
        })
        .Produces<SessionPlanDto>(StatusCodes.Status200OK);

        group.MapPost("/approve", async (HttpContext context, Guid sessionId, SessionPlanService plans, CancellationToken ct) =>
        {
            if (!TryParseIfMatch(context.Request, out var ifMatchRevision))
            {
                return Results.BadRequest(new { code = "INVALID_IF_MATCH", message = "If-Match header must be a numeric plan revision." });
            }

            SessionPlanApproveRequestDto? body = null;
            if (context.Request.ContentLength is > 0 || !string.IsNullOrWhiteSpace(context.Request.ContentType))
            {
                try
                {
                    if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
                    using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                    var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        body = JsonSerializer.Deserialize<SessionPlanApproveRequestDto>(raw);
                    }
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { code = "INVALID_JSON", message = "Request body is not valid JSON." });
                }
            }

            try
            {
                var dto = await plans.ApproveAsync(
                    sessionId,
                    ifMatchRevision ?? body?.ExpectedRevision,
                    ct).ConfigureAwait(false);
                context.Response.Headers.ETag = $"\"{dto.Revision}\"";
                return Results.Ok(dto);
            }
            catch (SessionPlanException ex)
            {
                return ErrorResult(ex);
            }
        })
        .Produces<SessionPlanDto>(StatusCodes.Status200OK);

        return app;
    }

    private static IResult ErrorResult(SessionPlanException ex) =>
        Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: ex.StatusCode);

    private static bool TryParseIfMatch(HttpRequest request, out long? revision)
    {
        revision = null;
        if (!request.Headers.TryGetValue("If-Match", out var values)) return true;
        var raw = values.ToString().Trim();
        if (string.IsNullOrEmpty(raw) || raw == "*") return true;
        if (raw.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) raw = raw[2..].Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') raw = raw[1..^1].Trim();
        if (long.TryParse(raw, out var parsed) && parsed >= 0)
        {
            revision = parsed;
            return true;
        }
        return false;
    }
}
