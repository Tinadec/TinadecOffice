using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Live companion SSE feed. Replays durable journal rows from <c>after_seq</c> / <c>Last-Event-ID</c>
/// and then streams both durable activity wakes and real-time <see cref="ICompanionStreamBroker"/>
/// frames (<c>companion.delta</c>, <c>companion.tool_call</c>, <c>companion.proposal</c>,
/// <c>companion.stage_cue</c>, <c>companion.work_session</c>) via <see cref="System.Threading.Channels"/>
/// with zero database polling.
/// </summary>
public static class CompanionEventEndpoints
{
    private const string ActivityEvent = "companion.activity";
    private const string RunEvent = "companion.run";
    private const string SettingsEvent = "companion.settings";
    private const string SettingsActivityKind = "settings_changed";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IEndpointRouteBuilder MapCompanionEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/companion-events", StreamAsync).WithTags("Companions");
        return app;
    }

    private static async Task StreamAsync(
        HttpContext context,
        IDbContextFactory<CompanionDbContext> dbFactory,
        ITenantContextAccessor tenant,
        ICompanionStreamBroker broker,
        long? afterSeq,
        long? after_seq,
        CancellationToken cancellationToken)
    {
        var cursor = afterSeq ?? after_seq ?? 0;
        if (context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && long.TryParse(lastEventId.ToString(), out var headerCursor))
            cursor = Math.Max(cursor, headerCursor);
        if (cursor < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "invalid_event_cursor" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tenantId = tenant.Current.TenantId;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = broker.Subscribe(tenantId);
        try
        {
            await WriteHeartbeatAsync(context, cursor, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

            cursor = await DrainDurableActivitiesAsync(context, dbFactory, tenantId, cursor, cancellationToken)
                .ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var signal = await subscription.ReadOrTimeoutAsync(HeartbeatInterval, cancellationToken)
                    .ConfigureAwait(false);

                if (signal is null)
                {
                    await WriteHeartbeatAsync(context, cursor, cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (signal.IsActivityWake)
                {
                    cursor = await DrainDurableActivitiesAsync(context, dbFactory, tenantId, cursor, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (!string.IsNullOrEmpty(signal.EventName) && signal.DataJson is not null)
                {
                    await context.Response.WriteAsync(
                        $"event: {signal.EventName}\ndata: {signal.DataJson}\n\n",
                        cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Subscriber disconnected cleanly.
        }
    }

    private static async Task<long> DrainDurableActivitiesAsync(
        HttpContext context,
        IDbContextFactory<CompanionDbContext> dbFactory,
        Guid tenantId,
        long cursor,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.Activities.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Seq > cursor)
                .OrderBy(x => x.Seq)
                .Take(200)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (rows.Count == 0) return cursor;

            foreach (var row in rows)
            {
                await WriteEventAsync(context, row, cancellationToken).ConfigureAwait(false);
                cursor = row.Seq;
            }
            await WriteHeartbeatAsync(context, cursor, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (rows.Count < 200) return cursor;
        }
    }

    private static async Task WriteEventAsync(HttpContext context, CompanionActivityRecord row, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            seq = row.Seq,
            id = row.Id,
            kind = row.Kind,
            companion_id = row.CompanionId,
            conversation_id = row.ConversationId,
            summary = row.Summary,
            detail = row.Detail,
            payload = ParsePayload(row.PayloadJson),
            created_at = row.CreatedAt
        }, SseJson);
        await context.Response.WriteAsync($"id: {row.Seq}\nevent: {EventName(row.Kind)}\ndata: {data}\n\n", cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task WriteHeartbeatAsync(HttpContext context, long cursor, CancellationToken cancellationToken) =>
        context.Response.WriteAsync($"event: heartbeat\ndata: {{\"after_seq\":{cursor}}}\n\n", cancellationToken);

    private static string EventName(string kind) => kind switch
    {
        SettingsActivityKind => SettingsEvent,
        "run_started" or "run_completed" or "run_failed" => RunEvent,
        _ => ActivityEvent
    };

    private static JsonElement? ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
