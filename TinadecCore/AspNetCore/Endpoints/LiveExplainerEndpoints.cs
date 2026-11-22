using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA.LiveExplainer;
using TinadecCore.Models;
using TinadecCore.Runtime;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Governance-layer Live Digest endpoints:
/// - <c>GET/PUT /api/v1/live-digest/settings</c> (3-tier mode <c>off | auto | always</c> + <c>live_explainer</c> route slot binding)
/// - <c>GET /api/v1/runs/{runId}/live-digest</c> (durable snapshot &amp; post-run replay)
/// - <c>GET /api/v1/runs/{runId}/live-digest/stream</c> (real-time unbuffered SSE stream)
/// </summary>
public static class LiveExplainerEndpoints
{
    public static IEndpointRouteBuilder MapLiveExplainerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/live-digest/settings", GetSettings)
            .Produces<LiveDigestSettingsDto>(StatusCodes.Status200OK);

        app.MapPut("/api/v1/live-digest/settings", PutSettings)
            .Produces<LiveDigestSettingsDto>(StatusCodes.Status200OK);

        app.MapGet("/api/v1/runs/{runId}/live-digest", GetRunLiveDigest)
            .Produces<LiveDigestSnapshotDto>(StatusCodes.Status200OK);

        app.MapGet("/api/v1/runs/{runId}/live-digest/stream", StreamRunLiveDigest);

        return app;
    }

    private static async Task<IResult> GetSettings(
        HttpResponse response,
        LiveExplainerStore store,
        IDbContextFactory<ModelControlDbContext> modelDbFactory,
        IChatResolver chatResolver,
        ITenantContextAccessor tenantAccessor,
        CancellationToken ct)
    {
        var dto = await BuildSettingsDtoAsync(store, modelDbFactory, chatResolver, tenantAccessor.Current, ct).ConfigureAwait(false);
        response.Headers.ETag = $"\"{dto.Revision}\"";
        return Results.Json(dto, LiveDigestJson.Options);
    }

    private static async Task<IResult> PutSettings(
        UpdateLiveDigestSettingsRequestDto? request,
        HttpResponse response,
        LiveExplainerStore store,
        ControlPlaneService controlPlane,
        IDbContextFactory<ModelControlDbContext> modelDbFactory,
        IChatResolver chatResolver,
        ITenantContextAccessor tenantAccessor,
        CancellationToken ct)
    {
        request ??= new UpdateLiveDigestSettingsRequestDto();
        var scope = tenantAccessor.Current;

        var (currentMode, _, _) = await store.GetSettingsAsync(scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        var targetMode = currentMode;
        if (!string.IsNullOrWhiteSpace(request.Mode))
        {
            if (!LiveExplainerModes.TryNormalize(request.Mode, out targetMode))
            {
                return Results.BadRequest(new
                {
                    code = "INVALID_LIVE_DIGEST_MODE",
                    message = "mode must be one of: off, auto, always."
                });
            }
        }

        if (request.ClearModelBinding)
        {
            await ClearExplicitRouteAsync(modelDbFactory, scope, ct).ConfigureAwait(false);
        }
        else if (request.ProviderInstanceId is { } providerId && providerId != Guid.Empty)
        {
            var saveRouteResult = await controlPlane.SaveRoute(
                LiveExplainerModelSlot.Purpose,
                new ModelRouteWriteRequestDto
                {
                    Candidates =
                    [
                        new ModelRouteCandidateDto
                        {
                            ProviderInstanceId = providerId,
                            Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
                            Position = 0
                        }
                    ]
                },
                ifMatch: null,
                ct).ConfigureAwait(false);

            if (saveRouteResult is IStatusCodeHttpResult { StatusCode: >= 400 })
            {
                return saveRouteResult;
            }
        }

        await store.SaveModeAsync(targetMode, ct).ConfigureAwait(false);
        var dto = await BuildSettingsDtoAsync(store, modelDbFactory, chatResolver, scope, ct).ConfigureAwait(false);
        response.Headers.ETag = $"\"{dto.Revision}\"";
        return Results.Json(dto, LiveDigestJson.Options);
    }

    private static async Task<IResult> GetRunLiveDigest(
        string runId,
        LiveExplainerService explainer,
        CancellationToken ct)
    {
        if (!Guid.TryParse(runId, out var runGuid) || runGuid == Guid.Empty)
        {
            return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "Run id must be a valid UUID." });
        }

        try
        {
            var snapshot = await explainer.GetSnapshotAsync(runGuid, ct).ConfigureAwait(false);
            return Results.Json(snapshot, LiveDigestJson.Options);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { code = "NOT_FOUND", message = "Run was not found." });
        }
    }

    private static async Task StreamRunLiveDigest(
        HttpContext context,
        string runId,
        long? after_seq,
        LiveExplainerService explainer,
        CancellationToken ct)
    {
        if (!Guid.TryParse(runId, out var runGuid) || runGuid == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "INVALID_RUN_ID", message = "Run id must be a valid UUID." }, ct).ConfigureAwait(false);
            return;
        }

        var cursor = after_seq ?? 0;
        if (context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && long.TryParse(lastEventId.ToString(), out var headerCursor))
        {
            cursor = Math.Max(cursor, headerCursor);
        }

        if (cursor < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "INVALID_STREAM_CURSOR", message = "after_seq must be non-negative." }, ct).ConfigureAwait(false);
            return;
        }

        // Verify existence before committing SSE headers so unknown runs return a clean 404 JSON body.
        try
        {
            await explainer.GetSnapshotAsync(runGuid, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { code = "NOT_FOUND", message = "Run was not found." }, ct).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await foreach (var frame in explainer.FollowAsync(runGuid, cursor, context.RequestAborted).ConfigureAwait(false))
            {
                if (string.Equals(frame.EventKind, "heartbeat", StringComparison.Ordinal))
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                    continue;
                }

                var json = JsonSerializer.Serialize(frame.Payload, LiveDigestJson.Options);
                await context.Response.WriteAsync($"id: {frame.Seq}\nevent: {frame.EventKind}\ndata: {json}\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Normal client disconnect.
        }
    }

    private static async Task<LiveDigestSettingsDto> BuildSettingsDtoAsync(
        LiveExplainerStore store,
        IDbContextFactory<ModelControlDbContext> modelDbFactory,
        IChatResolver chatResolver,
        TenantContext scope,
        CancellationToken ct)
    {
        var (mode, revision, updatedAt) = await store.GetSettingsAsync(scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);

        Guid? explicitProviderId = null;
        string? explicitModel = null;
        await using (var db = await modelDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var route = await db.Routes.AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.Purpose == LiveExplainerModelSlot.Purpose
                        && x.TenantId == scope.TenantId
                        && x.WorkspaceId == scope.WorkspaceId
                        && x.DeletedAt == null,
                    ct)
                .ConfigureAwait(false);
            if (route is not null)
            {
                var candidate = await db.RouteCandidates.AsNoTracking()
                    .Where(x => x.RouteVersionId == route.CurrentVersionId)
                    .OrderBy(x => x.Position)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (candidate is not null)
                {
                    explicitProviderId = candidate.ProviderInstanceId;
                    explicitModel = candidate.Model;
                }
                if (route.UpdatedAt > updatedAt) updatedAt = route.UpdatedAt;
            }
        }

        var resolution = await chatResolver.ResolveChatAsync(LiveExplainerModelSlot.Purpose, ct).ConfigureAwait(false);
        return new LiveDigestSettingsDto
        {
            Mode = mode,
            RoutePurpose = LiveExplainerModelSlot.Purpose,
            ProviderInstanceId = explicitProviderId,
            Model = explicitModel,
            ResolvedProviderInstanceId = resolution.IsAvailable ? resolution.ProviderInstanceId : null,
            ResolvedModel = resolution.IsAvailable ? (resolution.Model ?? resolution.ModelId) : null,
            ResolvedSource = resolution.IsAvailable ? (resolution.StrategySource ?? "explicit_route") : "unconfigured",
            Revision = revision,
            UpdatedAt = updatedAt
        };
    }

    private static async Task ClearExplicitRouteAsync(
        IDbContextFactory<ModelControlDbContext> modelDbFactory,
        TenantContext scope,
        CancellationToken ct)
    {
        await using var db = await modelDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var route = await db.Routes
            .SingleOrDefaultAsync(
                x => x.Purpose == LiveExplainerModelSlot.Purpose
                    && x.TenantId == scope.TenantId
                    && x.WorkspaceId == scope.WorkspaceId
                    && x.DeletedAt == null,
                ct)
            .ConfigureAwait(false);
        if (route is null) return;

        route.DeletedAt = DateTimeOffset.UtcNow;
        route.Revision++;
        route.UpdatedByPrincipalId = scope.PrincipalId;
        route.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
