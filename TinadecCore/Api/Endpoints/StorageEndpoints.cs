using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Contracts.Events;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;

namespace TinadecCore.Api.Endpoints;

public static class StorageEndpoints
{
    public static WebApplication MapStorageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/projects", async (ProjectSessionStore store, CancellationToken ct) =>
            Results.Ok((await store.ListProjectsAsync(ct).ConfigureAwait(false)).Select(ToProject)));

        app.MapPost("/api/v1/projects", async (CreateProjectRequest request, ProjectSessionStore store, CancellationToken ct) =>
        {
            try
            {
                var project = await store.CreateProjectAsync(request.Name, request.Path, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/projects/{project.Id}", ToProject(project));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_PROJECT", message = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { code = "DUPLICATE_PROJECT_ROOT", message = ex.Message }); }
        });

        app.MapGet("/api/v1/sessions", async (string? projectId, string? project_id, ProjectSessionStore store, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, CancellationToken ct) =>
        {
            var selected = projectId ?? project_id;
            if (selected is not null && !Guid.TryParse(selected, out var parsed)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
            var sessions = await store.ListSessionsAsync(selected is null ? null : Guid.Parse(selected), ct).ConfigureAwait(false);
            var enriched = new List<object>(sessions.Count);
            foreach (var s in sessions) enriched.Add(await ToSessionEnrichedAsync(s, cfgFactory, ct).ConfigureAwait(false));
            return Results.Ok(enriched);
        });

        app.MapPost("/api/v1/sessions", async (CreateSessionRequest request, ProjectSessionStore store, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, CancellationToken ct) =>
        {
            if (!Guid.TryParse(request.ProjectId, out var projectId)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
            try
            {
                var session = await store.CreateSessionAsync(projectId, request.Title, ct).ConfigureAwait(false);
                if (request.ModeVersionId.HasValue || !string.IsNullOrWhiteSpace(request.MeetingModel))
                {
                    session = await store.UpdateSessionModeAsync(session.Id, request.ModeVersionId, request.MeetingModel, request.MeetingProviderId, ct).ConfigureAwait(false) ?? session;
                }
                else
                {
                    // default to workspace default mode if any
                    try
                    {
                        await using var cfg = await cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                        var wsDefault = await cfg.WorkspaceDefaults.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId, ct).ConfigureAwait(false);
                        if (wsDefault?.DefaultAgentModeId is { } mid)
                        {
                            var latest = await cfg.ModeVersions.Where(x => x.AgentModeId == mid).OrderByDescending(x => x.Version).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                            if (latest.HasValue) session = await store.UpdateSessionModeAsync(session.Id, latest, null, null, ct).ConfigureAwait(false) ?? session;
                        }
                    }
                    catch { }
                }
                return Results.Created($"/api/v1/sessions/{session.Id}", await ToSessionEnrichedAsync(session, cfgFactory, ct).ConfigureAwait(false));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "PROJECT_NOT_FOUND" }); }
        });

        app.MapPatch("/api/v1/sessions/{sessionId}", async (string sessionId, UpdateSessionRequest request, ProjectSessionStore store, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try
            {
                SessionRecord? session = null;
                if (!string.IsNullOrWhiteSpace(request.Title))
                    session = await store.UpdateTitleAsync(id, request.Title!, ct).ConfigureAwait(false);
                if (request.ModeVersionId.HasValue || request.MeetingModel is not null || request.MeetingProviderId is not null)
                {
                    session = await store.UpdateSessionModeAsync(id, request.ModeVersionId, request.MeetingModel, request.MeetingProviderId, ct).ConfigureAwait(false) ?? session;
                    if (session is null) return Results.NotFound(new { code = "SESSION_NOT_FOUND" });
                }
                else if (session is null)
                {
                    // no mode fields and no title change: fetch the session directly
                    session = await store.GetSessionAsync(id, ct).ConfigureAwait(false);
                    if (session is null) return Results.NotFound(new { code = "SESSION_NOT_FOUND" });
                }
                return Results.Ok(await ToSessionEnrichedAsync(session, cfgFactory, ct).ConfigureAwait(false));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_SESSION", message = ex.Message }); }
        });

        app.MapGet("/api/v1/sessions/{sessionId}/messages", async (string sessionId, ProjectSessionStore store, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try { return Results.Ok((await store.ListMessagesAsync(id, ct).ConfigureAwait(false)).Select(ToMessage)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "SESSION_NOT_FOUND" }); }
        });

        app.MapPost("/api/v1/sessions/{sessionId}/messages", async (string sessionId, CreateMessageRequest request, ProjectSessionStore store, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try { return Results.Created($"/api/v1/sessions/{id}/messages", ToMessage(await store.AddMessageAsync(id, request.Content, "user", null, ct).ConfigureAwait(false))); }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "SESSION_NOT_FOUND" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_MESSAGE", message = ex.Message }); }
        });

        app.MapGet("/api/v1/sessions/{sessionId}/runs", async (string sessionId, StorageLifecycleService lifecycle, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            return Results.Ok((await lifecycle.ListRunsAsync(id, ct).ConfigureAwait(false)).Select(ToRun));
        });

        app.MapGet("/api/v1/events", async (HttpContext context, string? sessionId, string? session_id, long? afterSeq, long? after_seq, StorageLifecycleService lifecycle, CancellationToken ct) =>
        {
            var selected = sessionId ?? session_id;
            Guid? selectedSessionId = null;
            if (selected is not null)
            {
                if (!Guid.TryParse(selected, out var parsed)) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                selectedSessionId = parsed;
            }
            var cursor = afterSeq ?? after_seq ?? 0;
            if (context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
                && long.TryParse(lastEventId.ToString(), out var headerCursor))
            {
                cursor = Math.Max(cursor, headerCursor);
            }
            if (cursor < 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { code = "INVALID_EVENT_CURSOR" }, ct).ConfigureAwait(false);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            try
            {
                // Event sequences are allocated per run, so the client-facing
                // Last-Event-ID cursor cannot be used as a database key once a
                // new run restarts at sequence one. The initial replay honors
                // the cursor; follow cycles poll a timestamp watermark and
                // de-duplicate by per-run sequence instead of rescanning the
                // whole journal on every tick.
                var lastSequenceByRun = new Dictionary<string, long>(StringComparer.Ordinal);
                var unsequencedSeenIds = new HashSet<string>(StringComparer.Ordinal);
                var lastHeartbeat = DateTimeOffset.UtcNow;
                var initialReplay = true;
                var followSince = DateTimeOffset.MinValue;

                while (!ct.IsCancellationRequested)
                {
                    var events = initialReplay
                        ? await lifecycle.ReplayEventsAsync(selectedSessionId, 0, ct).ConfigureAwait(false)
                        : await lifecycle.FollowEventsAsync(selectedSessionId, followSince, ct).ConfigureAwait(false);
                    var wrote = false;
                    foreach (var item in events)
                    {
                        if (item.Timestamp > followSince) followSince = item.Timestamp - EventFollowOverlap;
                        var sequence = GetEventSequence(item);
                        var runKey = item.RunId ?? string.Empty;
                        if (sequence is not null)
                        {
                            if (lastSequenceByRun.TryGetValue(runKey, out var seen) && sequence.Value <= seen) continue;
                            lastSequenceByRun[runKey] = sequence.Value;
                            if (initialReplay && sequence.Value <= cursor) continue;
                            if (sequence.Value > cursor) cursor = sequence.Value;
                        }
                        else if (!unsequencedSeenIds.Add(item.EventId)) continue;
                        await WriteEventAsync(context, item, sequence, ct).ConfigureAwait(false);
                        wrote = true;
                    }

                    var now = DateTimeOffset.UtcNow;
                    if (initialReplay || now - lastHeartbeat >= EventHeartbeatInterval)
                    {
                        await WriteHeartbeatAsync(context, cursor, ct).ConfigureAwait(false);
                        lastHeartbeat = now;
                        wrote = true;
                    }
                    initialReplay = false;
                    if (wrote) await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    await Task.Delay(EventFollowPollInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The subscriber disconnected; durable replay remains available on reconnect.
            }
        });

        return app;
    }

    private static object ToProject(ProjectRecord project) => new { id = project.Id, name = project.Name, path = project.RootPath, kind = project.Kind, created_at = project.CreatedAt, updated_at = project.UpdatedAt, archived = project.Archived };
    private static object ToSession(SessionRecord session) => new { id = session.Id, project_id = session.ProjectId, title = session.Title, status = session.Status, mode = session.Mode, mode_version_id = session.ModeVersionId, meeting_model = session.MeetingModel, meeting_provider_id = session.MeetingProviderId, summary = session.Summary, history_revision = session.HistoryRevision, created_at = session.CreatedAt, updated_at = session.UpdatedAt, archived = session.Archived };

    private static async Task<object> ToSessionEnrichedAsync(SessionRecord session, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, CancellationToken ct)
    {
        bool hasUpdate = false;
        Guid? latestModeVersionId = null;
        try
        {
            await using var cfg = await cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var wsDefault = await cfg.WorkspaceDefaults.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId, ct).ConfigureAwait(false);
            if (wsDefault?.DefaultAgentModeId is { } mid)
            {
                latestModeVersionId = await cfg.ModeVersions.Where(x => x.AgentModeId == mid).OrderByDescending(x => x.Version).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (latestModeVersionId.HasValue && session.ModeVersionId.HasValue)
                    hasUpdate = latestModeVersionId.Value != session.ModeVersionId.Value;
                else if (latestModeVersionId.HasValue && !session.ModeVersionId.HasValue)
                    hasUpdate = true;
            }
        }
        catch { }
        return new { id = session.Id, project_id = session.ProjectId, title = session.Title, status = session.Status, mode = session.Mode, mode_version_id = session.ModeVersionId, meeting_model = session.MeetingModel, meeting_provider_id = session.MeetingProviderId, has_update = hasUpdate, latest_mode_version_id = latestModeVersionId, summary = session.Summary, history_revision = session.HistoryRevision, created_at = session.CreatedAt, updated_at = session.UpdatedAt, archived = session.Archived };
    }
    private static object ToMessage(StoredMessage message) => new { id = message.Id, session_id = message.SessionId, run_id = message.RunId, role = message.Role, content = message.Content, created_at = message.CreatedAt };
    private static object ToRun(RunRecord run) => new { id = run.Id, session_id = run.SessionId, trigger_message_id = run.TriggerMessageId, status = run.Status, summary = run.Summary, task_revision = run.TaskRevision, latest_event_sequence = run.LastEventSequence, latest_event_at = run.LastEventAt, created_at = run.CreatedAt, updated_at = run.UpdatedAt, completed_at = run.CompletedAt };

    private static readonly TimeSpan EventFollowPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan EventFollowOverlap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EventHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static async Task WriteEventAsync(HttpContext context, EventEnvelope item, long? sequence, CancellationToken cancellationToken)
    {
        if (sequence is not null) await context.Response.WriteAsync($"id: {sequence.Value}\n", cancellationToken).ConfigureAwait(false);
        await context.Response.WriteAsync($"event: {item.EventType}\ndata: {JsonSerializer.Serialize(item, SseJsonOptions)}\n\n", cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteHeartbeatAsync(HttpContext context, long cursor, CancellationToken cancellationToken) =>
        context.Response.WriteAsync($"event: heartbeat\ndata: {{\"after_seq\":{cursor}}}\n\n", cancellationToken);

    private static long? GetEventSequence(EventEnvelope item)
    {
        if (!item.Payload.TryGetValue("sequence", out var value) || value is null) return null;
        return value switch
        {
            long sequence => sequence,
            int sequence => sequence,
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var sequence) => sequence,
            string text when long.TryParse(text, out var sequence) => sequence,
            _ => null
        };
    }

}
