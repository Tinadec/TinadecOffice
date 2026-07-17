using System.Text.Json;
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

        app.MapGet("/api/v1/sessions", async (string? projectId, string? project_id, ProjectSessionStore store, CancellationToken ct) =>
        {
            var selected = projectId ?? project_id;
            if (selected is not null && !Guid.TryParse(selected, out var parsed)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
            return Results.Ok((await store.ListSessionsAsync(selected is null ? null : Guid.Parse(selected), ct).ConfigureAwait(false)).Select(ToSession));
        });

        app.MapPost("/api/v1/sessions", async (CreateSessionRequest request, ProjectSessionStore store, CancellationToken ct) =>
        {
            if (!Guid.TryParse(request.ProjectId, out var projectId)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
            try
            {
                var session = await store.CreateSessionAsync(projectId, request.Title, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/sessions/{session.Id}", ToSession(session));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "PROJECT_NOT_FOUND" }); }
        });

        app.MapPatch("/api/v1/sessions/{sessionId}", async (string sessionId, UpdateSessionRequest request, ProjectSessionStore store, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try
            {
                var session = await store.UpdateTitleAsync(id, request.Title, ct).ConfigureAwait(false);
                return session is null ? Results.NotFound(new { code = "SESSION_NOT_FOUND" }) : Results.Ok(ToSession(session));
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
            if (selected is not null && !Guid.TryParse(selected, out var parsed)) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            var events = await lifecycle.ReplayEventsAsync(selected is null ? null : Guid.Parse(selected), afterSeq ?? after_seq ?? 0, ct).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            foreach (var item in events)
            {
                await context.Response.WriteAsync($"event: {item.EventType}\ndata: {JsonSerializer.Serialize(item)}\n\n", ct).ConfigureAwait(false);
            }
            await context.Response.WriteAsync("event: end\ndata: {}\n\n", ct).ConfigureAwait(false);
        });

        return app;
    }

    private static object ToProject(ProjectRecord project) => new { id = project.Id, name = project.Name, path = project.RootPath, kind = project.Kind, created_at = project.CreatedAt, updated_at = project.UpdatedAt, archived = project.Archived };
    private static object ToSession(SessionRecord session) => new { id = session.Id, project_id = session.ProjectId, title = session.Title, status = session.Status, mode = session.Mode, summary = session.Summary, history_revision = session.HistoryRevision, created_at = session.CreatedAt, updated_at = session.UpdatedAt, archived = session.Archived };
    private static object ToMessage(StoredMessage message) => new { id = message.Id, session_id = message.SessionId, run_id = message.RunId, role = message.Role, content = message.Content, created_at = message.CreatedAt };
    private static object ToRun(RunRecord run) => new { id = run.Id, session_id = run.SessionId, trigger_message_id = run.TriggerMessageId, status = run.Status, summary = run.Summary, task_revision = run.TaskRevision, latest_event_sequence = run.LastEventSequence, latest_event_at = run.LastEventAt, created_at = run.CreatedAt, updated_at = run.UpdatedAt, completed_at = run.CompletedAt };

}
