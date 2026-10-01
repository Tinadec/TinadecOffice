using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

public static class WorkspaceSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/projects/{projectId:guid}/snapshots", async (
            Guid projectId,
            WorkspaceSnapshotCreateRequestDto? input,
            IWorkspaceSnapshotService snapshots,
            CancellationToken ct) =>
        {
            if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "A snapshot request body is required." });
            try
            {
                var value = await snapshots.CreateAsync(new WorkspaceSnapshotCreateRequest(
                    projectId,
                    input.IdempotencyKey,
                    input.ExpectedWorkspaceHash,
                    input.IncludeHidden,
                    input.MaxFiles,
                    input.MaxBytes), ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/workspace-snapshots/{value.Id}", ToSnapshot(value));
            }
            catch (WorkspaceSnapshotConflictException ex)
            {
                return Results.Conflict(new { code = "workspace_conflict", message = ex.Message, conflicts = ex.Conflicts });
            }
        });

        app.MapGet("/api/v1/projects/{projectId:guid}/snapshots", async (
            Guid projectId,
            IWorkspaceSnapshotService snapshots,
            CancellationToken ct) => Results.Ok((await snapshots.ListAsync(projectId, ct).ConfigureAwait(false)).Select(ToSnapshot)));

        app.MapGet("/api/v1/workspace-snapshots/{snapshotId:guid}", async (
            Guid snapshotId,
            IWorkspaceSnapshotService snapshots,
            CancellationToken ct) =>
        {
            var value = await snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false);
            return value is null ? Results.NotFound(new { code = "snapshot_not_found" }) : Results.Ok(ToSnapshot(value));
        });

        app.MapPost("/api/v1/workspace-snapshots/{snapshotId:guid}/restore", async (
            Guid snapshotId,
            WorkspaceRestoreRequestDto? input,
            IWorkspaceSnapshotService snapshots,
            CancellationToken ct) =>
        {
            try
            {
                var value = await snapshots.RestoreAsync(snapshotId, new WorkspaceRestoreRequest(
                    input?.IdempotencyKey,
                    input?.ExpectedWorkspaceHash,
                    input?.AllowConflicts ?? false), ct).ConfigureAwait(false);
                return Results.Ok(ToRestore(value));
            }
            catch (WorkspaceSnapshotConflictException ex)
            {
                return Results.Conflict(new { code = "workspace_conflict", message = ex.Message, conflicts = ex.Conflicts });
            }
        });

        // Per-file review of one snapshot. These read the manifest against the workspace as it
        // stands now, so a row answers "did this write land, and is it still there" — which the
        // workspace-wide restore endpoint cannot, because it only knows the whole tree.
        app.MapGet("/api/v1/workspace-snapshots/{snapshotId:guid}/files", async (
            Guid snapshotId,
            IWorkspaceSnapshotService snapshots,
            CancellationToken ct) =>
        {
            try
            {
                var changes = await snapshots.ListFileChangesAsync(snapshotId, ct).ConfigureAwait(false);
                return Results.Ok(changes.Select(ToFileChange));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "snapshot_not_found", message = ex.Message });
            }
        });

        app.MapGet("/api/v1/workspace-snapshots/{snapshotId:guid}/files/diff", async (
            Guid snapshotId,
            string path,
            IWorkspaceSnapshotService snapshots,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { code = "invalid_request", message = "A file path query value is required." });
            try
            {
                var diff = await snapshots.GetFileDiffAsync(snapshotId, path, ct).ConfigureAwait(false);
                return diff is null
                    ? Results.NotFound(new { code = "file_not_found", message = $"Neither the snapshot nor the workspace holds '{path}'." })
                    : Results.Ok(ToDiff(diff));
            }
            catch (WorkspaceSnapshotPathException ex)
            {
                return Results.BadRequest(new { code = "path_outside_workspace", message = ex.Message, path = ex.Path });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "snapshot_not_found", message = ex.Message });
            }
        });

        app.MapPost("/api/v1/workspace-snapshots/{snapshotId:guid}/files/restore", async (
            Guid snapshotId,
            WorkspaceFileRestoreRequestDto? input,
            IWorkspaceSnapshotService snapshots,
            CancellationToken ct) =>
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Path))
                return Results.BadRequest(new { code = "invalid_request", message = "A file path is required." });
            try
            {
                var restored = await snapshots.RestoreFileAsync(snapshotId,
                    new WorkspaceFileRestoreRequest(input.Path, input.ExpectedSha256), ct).ConfigureAwait(false);
                return Results.Ok(ToFileChange(restored));
            }
            catch (WorkspaceSnapshotPathException ex)
            {
                return Results.BadRequest(new { code = "path_outside_workspace", message = ex.Message, path = ex.Path });
            }
            catch (WorkspaceSnapshotConflictException ex)
            {
                return Results.Conflict(new { code = "workspace_conflict", message = ex.Message, conflicts = ex.Conflicts });
            }
            catch (WorkspaceSnapshotFileNotCapturedException ex)
            {
                return Results.Conflict(new { code = "file_content_not_captured", message = ex.Message, path = ex.Path });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_request", message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { code = "snapshot_not_found", message = ex.Message });
            }
        });

        app.MapGet("/api/v1/debug/snapshot/{sessionId:guid}", async (
            Guid sessionId,
            ISessionLocator sessions,
            IWorkspaceSnapshotService snapshots,
            CancellationToken ct) =>
        {
            var session = await sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null) return Results.NotFound(new { code = "session_not_found" });
            var projection = await snapshots.GetSessionProjectionAsync(sessionId, ct).ConfigureAwait(false);
            if (projection is null) return Results.NotFound(new { code = "session_not_found" });
            var values = session.ProjectId is { } projId
                ? await snapshots.ListAsync(projId, ct).ConfigureAwait(false)
                : Array.Empty<WorkspaceSnapshot>();
            return Results.Ok(new
            {
                session_id = sessionId,
                project_id = session.ProjectId,
                tenant_id = projection.TenantId,
                workspace_id = projection.WorkspaceId,
                kind = projection.Kind,
                source = projection.Source,
                schema_version = projection.SchemaVersion,
                revision = projection.Revision,
                captured_at = projection.CapturedAt,
                latest = values.FirstOrDefault() is { } latest ? ToSnapshot(latest) : null,
                snapshots = values.Select(ToSnapshot).ToArray(),
                runs = projection.Runs.Select(ToRunMetadata).ToArray(),
                tasks = projection.Tasks.Select(ToTaskMetadata).ToArray(),
                events = projection.Events.Select(ToEventMetadata).ToArray()
            });
        });

        return app;
    }

    private static object ToSnapshot(WorkspaceSnapshot value) => new
    {
        id = value.Id,
        tenant_id = value.TenantId,
        workspace_id = value.WorkspaceId,
        project_id = value.ProjectId,
        kind = value.Kind,
        status = value.Status,
        is_git = value.IsGit,
        workspace_hash = value.WorkspaceHash,
        content_reference = value.ContentReference,
        content_hash = value.ContentHash,
        content_length = value.ContentLength,
        file_count = value.FileCount,
        base_snapshot_id = value.BaseSnapshotId,
        created_at = value.CreatedAt
    };

    private static object ToRestore(WorkspaceRestoreResult value) => new
    {
        status = value.Status,
        snapshot_id = value.SnapshotId,
        workspace_hash = value.WorkspaceHash,
        conflicts = value.Conflicts,
        applied_file_count = value.AppliedFileCount,
        restored_at = value.RestoredAt
    };

    private static object ToFileChange(WorkspaceFileChange value) => new
    {
        path = value.Path,
        status = value.Status,
        before_sha256 = value.BeforeSha256,
        after_sha256 = value.AfterSha256,
        before_length = value.BeforeLength,
        after_length = value.AfterLength,
        restorable = value.Restorable
    };

    private static object ToDiff(WorkspaceFileDiff value) => new
    {
        path = value.Path,
        status = value.Status,
        restorable = value.Restorable,
        before = ToSide(value.Before),
        after = ToSide(value.After)
    };

    private static object ToSide(WorkspaceFileSide value) => new
    {
        present = value.Present,
        length = value.Length,
        sha256 = value.Sha256,
        binary = value.Binary,
        truncated = value.Truncated,
        text = value.Text
    };

    private static object ToRunMetadata(WorkspaceRunMetadata value) => new
    {
        id = value.Id,
        session_id = value.SessionId,
        trigger_message_id = value.TriggerMessageId,
        initiated_by_principal_id = value.InitiatedByPrincipalId,
        status = value.Status,
        summary = value.Summary,
        task_revision = value.TaskRevision,
        latest_event_sequence = value.LastEventSequence,
        created_at = value.CreatedAt,
        updated_at = value.UpdatedAt,
        completed_at = value.CompletedAt
    };

    private static object ToTaskMetadata(WorkspaceTaskMetadata value) => new
    {
        run_id = value.RunId,
        index = value.Index,
        data = value.Value
    };

    private static object ToEventMetadata(WorkspaceEventMetadata value) => new
    {
        event_id = value.EventId,
        run_id = value.RunId,
        session_id = value.SessionId,
        sequence = value.Sequence,
        event_type = value.EventType,
        severity = value.Severity,
        task_id = value.TaskId,
        approval_id = value.ApprovalId,
        tool_id = value.ToolId,
        summary = value.Summary,
        schema_version = value.SchemaVersion,
        payload_hash = value.PayloadHash,
        timestamp = value.Timestamp
    };
}
