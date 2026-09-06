using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;

namespace TinadecCore.AspNetCore.Endpoints;

public static class InteractionsEndpoints
{
    public static IEndpointRouteBuilder MapInteractionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions", CreateInteraction);
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions/{interactionId:guid}/reassign", ReassignInteraction);
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions/{interactionId:guid}/cancel", CancelInteraction);
        app.MapGet("/api/v1/sessions/{sessionId:guid}/interactions", ListInteractions);
        return app;
    }

    static async Task<IResult> CreateInteraction(Guid sessionId, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, IDbContextFactory<LifecycleDbContext> lifecycleDbFactory, ITenantContextAccessor tenant, IAgentModelResolver modelResolver, ProjectSessionStore sessions, IConversationStore conversations, IFullDuplexRunCoordinator coordinator, StorageLifecycleService lifecycle, CancellationToken ct)
    {
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        var content = el.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(content)) return Results.BadRequest(new { code = "invalid_request", message = "content is required" });
        var clientMessageId = el.TryGetProperty("client_message_id", out var cm) ? cm.GetString() : Guid.NewGuid().ToString("N");
        var modeVersionId = el.TryGetProperty("mode_version_id", out var mv) && Guid.TryParse(mv.GetString(), out var g) ? g : (Guid?)null;
        var agentMode = el.TryGetProperty("agent_mode", out var am) ? am.GetString()?.Trim().ToLowerInvariant() : null;
        if (agentMode is not (null or "plan" or "spec" or "ask" or "vibe" or "auto" or "agent"))
            return Results.BadRequest(new { code = "invalid_request", message = "agent_mode must be one of plan|spec|ask|vibe|auto|agent" });
        var dispatchMode = el.TryGetProperty("dispatch_mode", out var dm) ? dm.GetString()?.Trim().ToLowerInvariant() : "queued";
        if (dispatchMode is not ("queued" or "insert" or "parallel")) return Results.BadRequest(new { code = "invalid_request", message = "dispatch_mode must be queued|insert|parallel" });
        Guid? targetRunId = null;
        if (el.TryGetProperty("target_run_id", out var tr) && Guid.TryParse(tr.GetString(), out var tg)) targetRunId = tg;
        if (dispatchMode == "insert" && targetRunId is null) return Results.BadRequest(new { code = "invalid_request", message = "insert requires target_run_id" });
        MeetingModelOverrideDto? meetingModelOverride = null;
        if (el.TryGetProperty("meeting_model_override", out var overrideElement)
            && overrideElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            try { meetingModelOverride = overrideElement.Deserialize<MeetingModelOverrideDto>(); }
            catch (JsonException) { return Results.BadRequest(new { code = "invalid_model_override", message = "meeting_model_override must be an object." }); }
            if (meetingModelOverride is null || meetingModelOverride.ProviderInstanceId == Guid.Empty)
                return Results.BadRequest(new { code = "invalid_model_override", message = "meeting_model_override.provider_instance_id is required." });
            try
            {
                await modelResolver.PreviewAsync(new ModelResolutionPreviewRequestDto { MeetingModelOverride = meetingModelOverride }, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { code = "invalid_model_override", message = exception.Message });
            }
        }
        var expectedRev = el.TryGetProperty("expected_context_revision", out var er) && er.TryGetInt64(out var rv) ? rv : (long?)null;

        // session existence + session's default mode handling
        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null) return Results.NotFound(new { code = "not_found", message = "Session not found" });

        // mode_version validation if provided
        if (modeVersionId.HasValue)
        {
            await using var cfg = await cfgFactory.CreateDbContextAsync(ct);
            var mvRec = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x =>
                x.Id == modeVersionId.Value && x.TenantId == session.TenantId
                && x.WorkspaceId == session.WorkspaceId && x.Status == "published", ct);
            if (mvRec is null) return Results.BadRequest(new { code = "invalid_request", message = "mode_version_id must reference a published mode in this workspace" });
        }

        // Composer agent_mode selects a published conversation mode for this workspace.
        // Resolution order: explicit mode_version_id > agent_mode slug (conversation.*) >
        // the session's existing mode (workspace default applied at session creation).
        // The resolved version is persisted onto the session so the run engine's roster
        // resolver freezes the exact relational mode the Agent Center edits.
        if (modeVersionId is null && agentMode is not null)
        {
            await using var cfg = await cfgFactory.CreateDbContextAsync(ct);
            var slug = $"conversation.{agentMode}";
            var candidateModes = await cfg.AgentModes.AsNoTracking().Where(
                x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId && x.Slug == slug && x.Status == "published").ToListAsync(ct);
            Guid? sourceInstallationId = null;
            if (session.ModeVersionId is { } currentModeVersionId)
            {
                var currentModeId = await cfg.ModeVersions.AsNoTracking()
                    .Where(version => version.Id == currentModeVersionId && version.TenantId == session.TenantId && version.WorkspaceId == session.WorkspaceId)
                    .Select(version => (Guid?)version.AgentModeId)
                    .SingleOrDefaultAsync(ct);
                if (currentModeId is { } logicalModeId)
                    sourceInstallationId = await cfg.AgentPackManagedResources.AsNoTracking()
                        .Where(resource => resource.ResourceKind == "mode" && resource.LogicalEntityId == logicalModeId)
                        .Select(resource => (Guid?)resource.InstallationId)
                        .SingleOrDefaultAsync(ct);
            }
            AgentModeRecord? mode = null;
            if (sourceInstallationId is { } installationId)
            {
                var managedModeId = await cfg.AgentPackManagedResources.AsNoTracking()
                    .Where(resource => resource.InstallationId == installationId
                        && resource.ResourceKind == "mode"
                        && candidateModes.Select(candidate => candidate.Id).Contains(resource.LogicalEntityId))
                    .Select(resource => (Guid?)resource.LogicalEntityId)
                    .SingleOrDefaultAsync(ct);
                mode = candidateModes.SingleOrDefault(candidate => candidate.Id == managedModeId);
            }
            mode ??= candidateModes
                .Where(candidate => !cfg.AgentPackManagedResources.AsNoTracking().Any(resource => resource.ResourceKind == "mode" && resource.LogicalEntityId == candidate.Id))
                .OrderByDescending(candidate => candidate.UpdatedAt)
                .FirstOrDefault();
            if (mode is null) return Results.BadRequest(new { code = "invalid_request", message = $"agent_mode '{agentMode}' has no published mode for this workspace" });
            var version = await cfg.ModeVersions.AsNoTracking()
                .Where(x => x.AgentModeId == mode.Id && x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId && x.Status == "published")
                .OrderByDescending(x => x.Version)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (version is null) return Results.BadRequest(new { code = "invalid_request", message = $"agent_mode '{agentMode}' has no published version" });
            modeVersionId = version;
        }

        modeVersionId ??= session.ModeVersionId;
        if (modeVersionId is null)
            return Results.Conflict(new { code = "agent_mode_not_configured", message = "A published Agent Mode must be configured before creating an interaction." });

        if (dispatchMode == "insert" && meetingModelOverride is not null)
            return Results.Conflict(new { code = "model_override_frozen", message = "A model override cannot be changed when inserting into an already frozen run." });

        // Persist the chosen mode_version onto the session so the run engine's roster resolver
        // reads the same relational mode the center edits — not a per-call event hint.
        if (modeVersionId.HasValue)
        {
            try
            {
                await sessions.UpdateSessionModeAsync(sessionId, modeVersionId, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Results.Json(new { code = "mode_binding_failed", message = $"Failed to bind mode version to session: {ex.Message}" }, statusCode: 503);
            }
        }

        // dispatch to existing full-duplex engine via coordinator
        // For insert: enqueue steering patch on target run (with context_revision conflict detection)
        if (dispatchMode == "insert" && targetRunId.HasValue)
        {
            // try context patch for conflict detection
            var baseRev = expectedRev ?? await conversations.GetContextRevisionAsync(sessionId, ct).ConfigureAwait(false);
            var patch = await conversations.ApplyContextPatchAsync(new ContextPatchRequest(sessionId, baseRev, content, $"Steering for {targetRunId}", targetRunId, Kind: "supplement"), ct).ConfigureAwait(false);
            if (patch.Status == "stale")
            {
                await lifecycle.AppendEventAsync(targetRunId.Value, "context.conflict", new { interaction_id = Guid.NewGuid(), target_run_id = targetRunId.Value, base_revision = baseRev, current_revision = patch.CurrentRevision, content }, "Context conflict detected", "warning", cancellationToken: ct);
                await lifecycle.AppendRunStreamAsync(targetRunId.Value, new DurableRunStreamAppend(Guid.NewGuid(), "context_conflict", null, IdempotencyKey: $"run:{targetRunId}:conflict:{Guid.NewGuid():N}", FinishReason: "stale"), ct);
                return Results.Conflict(new { code = "context_conflict", message = $"Context revision conflict: base {baseRev} vs current {patch.CurrentRevision}", base_revision = baseRev, current_revision = patch.CurrentRevision });
            }
            await lifecycle.AppendEventAsync(targetRunId.Value, "interaction.steering", new { interaction_id = Guid.NewGuid(), target_run_id = targetRunId.Value, content, dispatch_mode = dispatchMode }, $"Steering injected into run {targetRunId}", "info", cancellationToken: ct);
            await lifecycle.AppendRunStreamAsync(targetRunId.Value, new DurableRunStreamAppend(Guid.NewGuid(), "steering", null, IdempotencyKey: $"run:{targetRunId}:steering:{Guid.NewGuid():N}"), ct);
            var engine = (IFullDuplexRunEngine)req.HttpContext.RequestServices.GetRequiredService(typeof(IFullDuplexRunEngine));
            await engine.EnqueueAsync(targetRunId.Value, ct);
            var steeringCursor = await RunStreamCursorAsync(lifecycleDbFactory, targetRunId, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{targetRunId.Value}", new { interaction_id = Guid.NewGuid(), session_id = sessionId, run_id = targetRunId.Value, dispatch_mode = dispatchMode, status = "steering_injected", content, client_message_id = clientMessageId, context_revision = patch.CurrentRevision, stream_cursor = steeringCursor, correlation_id = clientMessageId });
        }

        // queued / parallel: normal admission via coordinator
        RunSubmission? admission = null;
        string admissionStatus = "queued";
        try
        {
            // Composer agent modes belong to the conversation application: selecting one routes
            // the runtime through the matching TOML conversation.* profile (policy + binding),
            // while the roster freezes from the resolved relational mode version. No selection
            // keeps the legacy space/full_duplex admission behavior. Explicit
            // application_mode/permission_mode fields preserve the retired invoke-stream's
            // admission surface (e.g. unattended permission policies).
            var applicationMode = el.TryGetProperty("application_mode", out var am2) && !string.IsNullOrWhiteSpace(am2.GetString())
                ? am2.GetString()!.Trim().ToLowerInvariant()
                : agentMode is null ? "space" : "conversation";
            var permissionMode = el.TryGetProperty("permission_mode", out var pm) && !string.IsNullOrWhiteSpace(pm.GetString())
                ? pm.GetString()!.Trim().ToLowerInvariant()
                : "default";
            var invocationOverride = meetingModelOverride is null
                ? null
                : new SessionModelOverride(meetingModelOverride.ProviderInstanceId, meetingModelOverride.Model);
        admission = await coordinator.SubmitAsync(new FullDuplexInvocation(sessionId, content, clientMessageId!, applicationMode, agentMode ?? "agent", permissionMode, targetRunId, expectedRev, invocationOverride), ct);
            admissionStatus = dispatchMode == "parallel" ? "assigned" : "queued";
        }
        catch (RunAdmissionException ex) when (ex.Code == "ACTIVE_RUN_LIMIT" && dispatchMode == "queued")
        {
            // Meeting busy: the interaction must survive the wait. Persist a
            // directive row + the user message + a run.queued event on the
            // active run so the M2 run-terminal hook can drain it; the old
            // branch fabricated a transient id and lost all three.
            var idempotencyKey = $"session:{sessionId}:queued:{clientMessageId}";
            await using var lifecycleDb = await lifecycleDbFactory.CreateDbContextAsync(ct);
            var replay = await lifecycleDb.RunDirectives.AsNoTracking()
                .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
            if (replay is not null)
            {
                var replayRevision = await conversations.GetContextRevisionAsync(sessionId, ct).ConfigureAwait(false);
                var replayCursor = await RunStreamCursorAsync(lifecycleDbFactory, replay.RunId, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{replay.Id}", new { interaction_id = replay.Id, session_id = sessionId, run_id = replay.RunId, dispatch_mode = dispatchMode, status = "queued", mode_version_id = modeVersionId, meeting_model_override = meetingModelOverride, reason = "replayed queued interaction", client_message_id = clientMessageId, context_revision = replayRevision, stream_cursor = replayCursor, correlation_id = clientMessageId });
            }
            var queuedId = Guid.NewGuid();
            var runs = await lifecycle.ListRunsAsync(sessionId, ct);
            var activeRun = runs.FirstOrDefault(run => run.Status is not ("completed" or "failed" or "cancelled"));
            var message = await conversations.AppendMessageAsync(sessionId, "user", content.Trim(), clientMessageId: $"queued:{clientMessageId}", cancellationToken: ct);
            lifecycleDb.RunDirectives.Add(new RunDirectiveRecord
            {
                Id = queuedId,
                TenantId = session.TenantId,
                WorkspaceId = session.WorkspaceId,
                SessionId = sessionId,
                RunId = activeRun?.Id,
                MessageId = message.Id,
                Kind = "queued_interaction",
                Status = "pending",
                PayloadJson = JsonSerializer.Serialize(new { content, client_message_id = clientMessageId, dispatch_mode = dispatchMode, mode_version_id = modeVersionId, agent_mode = agentMode }),
                IdempotencyKey = idempotencyKey,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await lifecycleDb.SaveChangesAsync(ct);
            if (activeRun is { } target)
            {
                await lifecycle.AppendEventAsync(target.Id, "run.queued", new { interaction_id = queuedId, directive_id = queuedId, message_id = message.Id, content, dispatch_mode = dispatchMode }, "Interaction queued behind the active run", "info", cancellationToken: ct);
            }
            var overflowRevision = await conversations.GetContextRevisionAsync(sessionId, ct).ConfigureAwait(false);
            var overflowCursor = await RunStreamCursorAsync(lifecycleDbFactory, activeRun?.Id, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{queuedId}", new { interaction_id = queuedId, session_id = sessionId, run_id = activeRun?.Id, dispatch_mode = dispatchMode, status = "queued", mode_version_id = modeVersionId, meeting_model_override = meetingModelOverride, reason = ex.Message, client_message_id = clientMessageId, context_revision = overflowRevision, stream_cursor = overflowCursor, correlation_id = clientMessageId });
        }
        catch (RunAdmissionException ex) when (ex.Code == "CONTEXT_REVISION_CONFLICT")
        {
            return Results.Conflict(new { code = "context_conflict", message = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            // 会话绑定/请求指定的 mode version 不可用（未发布、缺失、跨工作区、快照
            // 损坏）时，roster 冻结在建 run 之前抛出。给用户结构化 409 与可操作的
            // 提示，而不是裸 500 internal_error。
            return Results.Conflict(new
            {
                code = "mode_unavailable",
                message = "当前会话绑定的对话模式不可用，请在输入框左下角重新选择模式后重试。",
                detail = ex.Message
            });
        }
        if (admission is null) return Results.Conflict(new { code = "conflict", message = "Admission failed" });
        // if mode_version provided, store it as frozen binding hint (best-effort)
        if (modeVersionId.HasValue)
        {
            try { await lifecycle.AppendEventAsync(admission.RunId, "interaction.created", new { interaction_id = admission.TurnId, mode_version_id = modeVersionId.Value, dispatch_mode = dispatchMode, meeting_model_override = meetingModelOverride }, $"Interaction {dispatchMode} with mode_version {modeVersionId}", "info", cancellationToken: ct); } catch { }
            try { await lifecycle.AppendRunStreamAsync(admission.RunId, new DurableRunStreamAppend(admission.TurnId, dispatchMode == "parallel" ? "assigned" : "queued", admission.MessageId, IdempotencyKey: $"run:{admission.RunId}:turn:{admission.TurnId}:{dispatchMode}"), ct); } catch { }
        }
        var status = admissionStatus;
        var admissionCursor = await RunStreamCursorAsync(lifecycleDbFactory, admission.RunId, ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{admission.TurnId}", new { interaction_id = admission.TurnId, session_id = sessionId, run_id = admission.RunId, turn_id = admission.TurnId, dispatch_mode = dispatchMode, status, mode_version_id = modeVersionId, meeting_model_override = meetingModelOverride, client_message_id = clientMessageId, context_revision = admission.ContextRevision, stream_cursor = admissionCursor, correlation_id = clientMessageId });
    }

    /// <summary>
    /// The receipt cursor is the last assigned seq of the run's durable stream:
    /// following from it continues the live stream without duplication; a client
    /// that wants the full history opens the stream without a cursor.
    /// </summary>
    static async Task<long> RunStreamCursorAsync(IDbContextFactory<LifecycleDbContext> factory, Guid? runId, CancellationToken ct)
    {
        if (runId is null) return 0;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var next = await db.RunStreamCursors.AsNoTracking()
            .Where(x => x.RunId == runId.Value)
            .Select(x => (long?)x.NextSequence)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return Math.Max(0, (next ?? 0) - 1);
    }

    static async Task<IResult> ReassignInteraction(Guid sessionId, Guid interactionId, HttpRequest req, StorageLifecycleService lifecycle, ITenantContextAccessor tenant, CancellationToken ct)
    {
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        var newMode = el.TryGetProperty("dispatch_mode", out var dm) ? dm.GetString()?.Trim().ToLowerInvariant() : null;
        if (newMode is not ("queued" or "insert" or "parallel")) return Results.BadRequest(new { code = "invalid_request", message = "dispatch_mode must be queued|insert|parallel" });
        Guid? targetRunId = el.TryGetProperty("target_run_id", out var tr) && Guid.TryParse(tr.GetString(), out var g) ? g : null;
        if (newMode == "insert" && targetRunId is null) return Results.BadRequest(new { code = "invalid_request", message = "insert requires target_run_id" });
        // find run by interactionId (turnId)
        var runs = await lifecycle.ListRunsAsync(sessionId, ct);
        var run = runs.FirstOrDefault(r => r.TurnId == interactionId);
        if (run is null) return Results.NotFound(new { code = "not_found", message = "Interaction not found" });
        await lifecycle.AppendEventAsync(run.Id, "interaction.reassigned", new { interaction_id = interactionId, new_dispatch_mode = newMode, target_run_id = targetRunId }, $"Interaction reassigned to {newMode}", "info", cancellationToken: ct);
        return Results.Ok(new { interaction_id = interactionId, run_id = run.Id, dispatch_mode = newMode, status = "reassigned", target_run_id = targetRunId });
    }

    static async Task<IResult> CancelInteraction(Guid sessionId, Guid interactionId, StorageLifecycleService lifecycle, IFullDuplexRunCoordinator coordinator, CancellationToken ct)
    {
        var runs = await lifecycle.ListRunsAsync(sessionId, ct);
        var run = runs.FirstOrDefault(r => r.TurnId == interactionId);
        if (run is null) return Results.NotFound(new { code = "not_found" });
        var res = await coordinator.ControlAsync(run.Id, new RunControlCommand("cancel", null, null), ct);
        return Results.Ok(new { interaction_id = interactionId, run_id = run.Id, status = res.Status, action = res.Action });
    }

    static async Task<IResult> ListInteractions(Guid sessionId, StorageLifecycleService lifecycle, CancellationToken ct)
    {
        var runs = await lifecycle.ListRunsAsync(sessionId, ct);
        return Results.Ok(runs.Select(r => new { interaction_id = r.TurnId, run_id = r.Id, session_id = r.SessionId, status = r.Status, created_at = r.CreatedAt, updated_at = r.UpdatedAt }));
    }

}
