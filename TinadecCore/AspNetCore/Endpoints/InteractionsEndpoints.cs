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
    private const string TinaChatInputLockedCode = "tina_chat_input_locked";
    private const string TinaChatInputLockedDetail = "Use the TinaChat intent execution endpoint for this isolated handoff. New instructions belong in a new intent revision.";

    /// <summary>
    /// Bounded because each id costs the user a stored upload that a message may never
    /// claim, and because the model-facing manifest lists a fixed number of rows. Eight
    /// matches what comparable local-first harnesses cap a message at.
    /// </summary>
    private const int MaxAttachmentsPerMessage = 8;

    public static IEndpointRouteBuilder MapInteractionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions", CreateInteraction);
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions/{interactionId:guid}/reassign", ReassignInteraction);
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions/{interactionId:guid}/cancel", CancelInteraction);
        app.MapGet("/api/v1/sessions/{sessionId:guid}/interactions", ListInteractions);
        return app;
    }

    static async Task<IResult> CreateInteraction(Guid sessionId, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, IDbContextFactory<LifecycleDbContext> lifecycleDbFactory, ITenantContextAccessor tenant, IAgentModelResolver modelResolver, ProjectSessionStore sessions, IConversationStore conversations, IFullDuplexRunCoordinator coordinator, IMessageAttachmentStore attachments, StorageLifecycleService lifecycle, CancellationToken ct)
    {
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        if (el.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { code = "invalid_request", message = "Body must be a JSON object." });
        // Fail closed on retired/unknown keys so a stale client cannot silently
        // admit a run under a mode nobody reads anymore.
        var unknownField = el.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => name is not ("content" or "client_message_id" or "mode_version_id" or "permission_mode" or "dispatch_mode" or "target_run_id" or "expected_context_revision" or "meeting_model_override" or "attachment_ids"));
        if (unknownField is not null) return Results.BadRequest(new { code = "unknown_field", message = $"Field '{unknownField}' is not part of the interaction contract." });
        var content = el.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(content)) return Results.BadRequest(new { code = "invalid_request", message = "content is required" });
        var clientMessageId = el.TryGetProperty("client_message_id", out var cm) ? cm.GetString() : Guid.NewGuid().ToString("N");
        var modeVersionId = el.TryGetProperty("mode_version_id", out var mv) && Guid.TryParse(mv.GetString(), out var g) ? g : (Guid?)null;
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

        // An attachment is claimed by the message this interaction appends, so the ids
        // arrive with the send rather than in a second request that could race it.
        IReadOnlyList<Guid>? attachmentIds = null;
        if (el.TryGetProperty("attachment_ids", out var attachmentElement)
            && attachmentElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (attachmentElement.ValueKind != JsonValueKind.Array)
            {
                return Results.BadRequest(new { code = "attachment_ids_invalid", message = "attachment_ids must be an array of attachment ids." });
            }
            // Steering writes a context patch and appends no user message. Accepting ids
            // there would strand the upload with nothing to name it, so the request is
            // refused where the client can read why.
            if (dispatchMode == "insert")
            {
                return Results.BadRequest(new { code = "attachment_dispatch_unsupported", message = "Steering inserts no user message, so it cannot carry attachments. Send the file as its own message." });
            }
            var ids = new List<Guid>(attachmentElement.GetArrayLength());
            foreach (var item in attachmentElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var parsedId))
                {
                    return Results.BadRequest(new { code = "attachment_ids_invalid", message = "Every attachment id must be a guid string." });
                }
                ids.Add(parsedId);
            }
            if (ids.Count > MaxAttachmentsPerMessage)
            {
                return Results.BadRequest(new { code = "attachment_count_exceeded", message = $"A message carries at most {MaxAttachmentsPerMessage} attachments.", max = MaxAttachmentsPerMessage });
            }
            if (ids.Count > 0)
            {
                // Pre-flight against the session's unbound rows before a run exists. The
                // bind below still re-checks; this is what stops the common mistake — a
                // stale id from a session the user has since left — from starting a run
                // whose message silently carries nothing.
                var unbound = await attachments.ListAsync(sessionId, ct).ConfigureAwait(false);
                var missing = ids.Except(unbound.Where(row => row.MessageId is null).Select(row => row.Id)).ToArray();
                if (missing.Length > 0)
                {
                    return Results.BadRequest(new { code = "attachment_not_found", message = $"No unbound attachment in this session has the id(s): {string.Join(", ", missing)}.", missing });
                }
            }
            attachmentIds = ids;
        }

        // The message is appended first (its id comes from the run admission or the queued
        // branch), so binding is a second step and a crash between the two is possible. It
        // is recoverable by design: the same client_message_id replays onto the same
        // message, and BindToMessageAsync treats rows it already owns as success.
        async Task<IResult?> BindAttachmentsAsync(Guid messageId)
        {
            if (attachmentIds is null) return null;
            try
            {
                await attachments.BindToMessageAsync(sessionId, messageId, attachmentIds, ct).ConfigureAwait(false);
                return null;
            }
            catch (AttachmentBindingException ex)
            {
                return Results.BadRequest(new { code = ex.Code, message = ex.Message });
            }
        }

        // An insertion bypasses coordinator admission, so enforce the communication
        // binding here too, before persisting any context patch or mutable mode.
        var chatInputs = req.HttpContext.RequestServices.GetService<ITinaChatRunInput>();
        var chatInput = chatInputs is null ? null : await chatInputs.GetForSessionAsync(sessionId, ct);
        if (chatInput is not null)
            return TinaChatInputLocked(req);

        // mode_version validation if provided
        if (modeVersionId.HasValue)
        {
            await using var cfg = await cfgFactory.CreateDbContextAsync(ct);
            var mvRec = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x =>
                x.Id == modeVersionId.Value && x.TenantId == session.TenantId
                && x.WorkspaceId == session.WorkspaceId && x.Status == "published", ct);
            if (mvRec is null) return Results.BadRequest(new { code = "invalid_request", message = "mode_version_id must reference a published mode in this workspace" });
        }

        // Mode identity is the published ModeVersion only. Resolution order:
        // explicit mode_version_id > the session's bound mode version > the
        // workspace default. The resolved version is persisted onto the session
        // so the run engine's roster resolver freezes the exact relational mode
        // the Agent Center edits.
        modeVersionId ??= session.ModeVersionId;
        if (modeVersionId is null)
        {
            await using var cfg = await cfgFactory.CreateDbContextAsync(ct);
            modeVersionId = await cfg.WorkspaceDefaults.AsNoTracking()
                .Where(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId
                    && x.Status == "active" && x.ArchivedAt == null)
                .Select(x => x.DefaultModeVersionId)
                .FirstOrDefaultAsync(ct);
        }
        if (modeVersionId is null)
            return Results.Conflict(new { code = "agent_mode_not_configured", message = "A published Agent Mode must be configured before creating an interaction." });

        // A disabled pack fails explicitly: the run must not silently fall back to
        // a different roster than the one the user is looking at. The client is
        // told which pack to re-enable (or to pick another mode).
        await using (var guard = await cfgFactory.CreateDbContextAsync(ct))
        {
            var ownerPack = await (from mode in guard.AgentModes.AsNoTracking()
                                   join version in guard.ModeVersions.AsNoTracking() on mode.Id equals version.AgentModeId
                                   join resource in guard.AgentPackManagedResources.AsNoTracking() on mode.Id equals resource.LogicalEntityId
                                   join installation in guard.AgentPackInstallations.AsNoTracking() on resource.InstallationId equals installation.Id
                                   where version.Id == modeVersionId.Value && version.Status == "published"
                                   select new { installation.PackId, installation.Status }).FirstOrDefaultAsync(ct);
            if (ownerPack is not null && !string.Equals(ownerPack.Status, "active", StringComparison.Ordinal))
                return Results.Conflict(new
                {
                    code = "pack_disabled",
                    message = $"Agent pack '{ownerPack.PackId}' is disabled. Enable it or choose a mode from an enabled pack.",
                    pack_id = ownerPack.PackId
                });
        }

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
        var permissionMode = el.TryGetProperty("permission_mode", out var pm) && !string.IsNullOrWhiteSpace(pm.GetString())
            ? pm.GetString()!.Trim().ToLowerInvariant()
            : "default";
        var invocationOverride = meetingModelOverride is null
            ? null
            : new SessionModelOverride(meetingModelOverride.ProviderInstanceId, meetingModelOverride.Model);
        try
        {
            // Mode identity is frozen from the session's bound/persisted mode
            // version; no legacy application-mode/agent-mode admission surface
            // exists anymore. Unattended permission policies ride on
            // permission_mode alone.
            admission = await coordinator.SubmitAsync(new FullDuplexInvocation(
                sessionId,
                content,
                clientMessageId!,
                permissionMode,
                targetRunId,
                expectedRev,
                invocationOverride,
                modeVersionId), ct);
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
            var replay = await lifecycleDb.RunDirectives
                .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
            if (replay is { RunId: not null })
            {
                var replayRevision = await conversations.GetContextRevisionAsync(sessionId, ct).ConfigureAwait(false);
                var replayCursor = await RunStreamCursorAsync(lifecycleDbFactory, replay.RunId, ct).ConfigureAwait(false);
                if (replay.MessageId is { } replayMessageId)
                {
                    var replayBind = await BindAttachmentsAsync(replayMessageId).ConfigureAwait(false);
                    if (replayBind is not null) return replayBind;
                }
                return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{replay.Id}", new { interaction_id = replay.Id, session_id = sessionId, run_id = replay.RunId, dispatch_mode = dispatchMode, status = "queued", mode_version_id = modeVersionId, meeting_model_override = meetingModelOverride, reason = "replayed queued interaction", client_message_id = clientMessageId, context_revision = replayRevision, stream_cursor = replayCursor, correlation_id = clientMessageId });
            }

            var runs = await lifecycle.ListRunsAsync(sessionId, ct);
            var activeRun = runs.FirstOrDefault(run => run.Status is not ("completed" or "failed" or "cancelled"));
            if (activeRun is null)
            {
                // The limiting run may have crossed terminal between admission and
                // queue persistence. Retry once instead of creating an orphan
                // directive with no owner for the repair loop to discover.
                try
                {
                    admission = await coordinator.SubmitAsync(new FullDuplexInvocation(
                        sessionId,
                        content,
                        clientMessageId!,
                        permissionMode,
                        targetRunId,
                        expectedRev,
                        invocationOverride,
                        modeVersionId), ct).ConfigureAwait(false);
                    admissionStatus = "queued";
                }
                catch (RunAdmissionException retry) when (retry.Code == "ACTIVE_RUN_LIMIT")
                {
                    runs = await lifecycle.ListRunsAsync(sessionId, ct).ConfigureAwait(false);
                    activeRun = runs.FirstOrDefault(run => run.Status is not ("completed" or "failed" or "cancelled"));
                }
            }

            if (admission is null)
            {
                if (activeRun is null)
                {
                    return Results.Conflict(new
                    {
                        code = "queue_owner_changed",
                        message = "The active run changed while the interaction was being queued. Retry the same client_message_id."
                    });
                }

                var message = await conversations.AppendMessageAsync(
                    sessionId,
                    "user",
                    content.Trim(),
                    clientMessageId: clientMessageId,
                    cancellationToken: ct).ConfigureAwait(false);
                var queuedBind = await BindAttachmentsAsync(message.Id).ConfigureAwait(false);
                if (queuedBind is not null) return queuedBind;
                var directive = replay ?? new RunDirectiveRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = session.TenantId,
                    WorkspaceId = session.WorkspaceId,
                    SessionId = sessionId,
                    Kind = "queued_interaction",
                    Status = "pending",
                    IdempotencyKey = idempotencyKey,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                directive.RunId = activeRun.Id;
                directive.MessageId = message.Id;
                directive.PayloadJson = JsonSerializer.Serialize(new
                {
                    content,
                    client_message_id = clientMessageId,
                    dispatch_mode = dispatchMode,
                    mode_version_id = modeVersionId,
                    permission_mode = permissionMode,
                    meeting_model_override = meetingModelOverride is null
                        ? null
                        : new
                        {
                            provider_instance_id = meetingModelOverride.ProviderInstanceId,
                            model = meetingModelOverride.Model
                        }
                });
                directive.UpdatedAt = DateTimeOffset.UtcNow;
                if (replay is null) lifecycleDb.RunDirectives.Add(directive);
                await lifecycleDb.SaveChangesAsync(ct).ConfigureAwait(false);
                await lifecycle.AppendEventAsync(activeRun.Id, "run.queued", new
                {
                    interaction_id = directive.Id,
                    directive_id = directive.Id,
                    message_id = message.Id,
                    content,
                    dispatch_mode = dispatchMode,
                    mode_version_id = modeVersionId,
                    permission_mode = permissionMode,
                    meeting_model_override = meetingModelOverride
                }, "Interaction queued behind the active run", "info", cancellationToken: ct).ConfigureAwait(false);
                var overflowRevision = await conversations.GetContextRevisionAsync(sessionId, ct).ConfigureAwait(false);
                var overflowCursor = await RunStreamCursorAsync(lifecycleDbFactory, activeRun.Id, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{directive.Id}", new { interaction_id = directive.Id, session_id = sessionId, run_id = activeRun.Id, dispatch_mode = dispatchMode, status = "queued", mode_version_id = modeVersionId, meeting_model_override = meetingModelOverride, reason = ex.Message, client_message_id = clientMessageId, context_revision = overflowRevision, stream_cursor = overflowCursor, correlation_id = clientMessageId });
            }
        }
        catch (RunAdmissionException ex) when (ex.Code == "CONTEXT_REVISION_CONFLICT")
        {
            return Results.Conflict(new { code = "context_conflict", message = ex.Message });
        }
        catch (RunAdmissionException ex)
        {
            // Fail-closed admission rejections (graph_tier_lanes_unsupported,
            // run freeze rules, spawn ceilings) surface as structured 409s with
            // the snake_case code instead of a bare 500.
            return Results.Conflict(new { code = ex.Code, message = ex.Message });
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
        {
            var admittedBind = await BindAttachmentsAsync(admission.MessageId).ConfigureAwait(false);
            if (admittedBind is not null) return admittedBind;
        }
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

    private static IResult TinaChatInputLocked(HttpRequest request) => Results.Problem(
        type: $"https://tinadec.dev/errors/{TinaChatInputLockedCode}",
        title: TinaChatInputLockedCode,
        statusCode: StatusCodes.Status403Forbidden,
        detail: TinaChatInputLockedDetail,
        instance: request.Path.Value,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = TinaChatInputLockedCode,
            ["trace_id"] = request.HttpContext.TraceIdentifier
        });

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
