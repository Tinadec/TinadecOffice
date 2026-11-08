using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Live companion feed. One durable journal (the activity rows, numbered per tenant) is replayed
/// from the caller's cursor and then followed, so a reconnect resumes exactly where the previous
/// subscription stopped. The three event names are a projection of the row kind, not a second
/// stream: run transitions and settings changes are activity rows too.
/// </summary>
public static class CompanionEventEndpoints
{
    private const string ActivityEvent = "companion.activity";
    private const string RunEvent = "companion.run";
    private const string SettingsEvent = "companion.settings";
    private const string SettingsActivityKind = "settings_changed";

    private static readonly TimeSpan FollowPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
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
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            // The initial heartbeat publishes the resumed cursor before any event exists, so a
            // client that reconnects into an idle feed still learns where it stands.
            await WriteHeartbeatAsync(context, cursor, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            var lastWrite = DateTimeOffset.UtcNow;

            while (!cancellationToken.IsCancellationRequested)
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var rows = await db.Activities.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.Seq > cursor)
                    .OrderBy(x => x.Seq)
                    .Take(200)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                foreach (var row in rows)
                {
                    await WriteEventAsync(context, row, cancellationToken).ConfigureAwait(false);
                    cursor = row.Seq;
                }

                var now = DateTimeOffset.UtcNow;
                var wrote = rows.Count > 0;
                if (wrote || now - lastWrite >= HeartbeatInterval)
                {
                    await WriteHeartbeatAsync(context, cursor, cancellationToken).ConfigureAwait(false);
                    lastWrite = now;
                    wrote = true;
                }
                if (wrote) await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(FollowPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The subscriber disconnected. The journal is durable, so nothing is lost.
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
