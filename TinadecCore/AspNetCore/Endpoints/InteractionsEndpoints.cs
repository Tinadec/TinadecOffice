using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Contracts.Events;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

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
    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static IEndpointRouteBuilder MapInteractionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions", CreateInteraction)
            .Produces<InteractionCreatedResponseDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions/{interactionId:guid}/reassign", ReassignInteraction)
            .Produces<InteractionMutationResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions/{interactionId:guid}/cancel", CancelInteraction)
            .Produces<InteractionMutationResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
        app.MapGet("/api/v1/sessions/{sessionId:guid}/interactions", ListInteractions)
            .Produces<IReadOnlyList<InteractionSummaryResponseDto>>(StatusCodes.Status200OK);
        app.MapGet("/api/v1/sessions/{sessionId:guid}/ingress/{clientRequestId}", GetIngressStatus)
            .Produces<PeerIngressStatusDto>(StatusCodes.Status200OK);
        return app;
    }

    static async Task<IResult> CreateInteraction(Guid sessionId, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, GlobalAgentConfiguration globalConfig, IDbContextFactory<LifecycleDbContext> lifecycleDbFactory, ITenantContextAccessor tenant, IAgentModelResolver modelResolver, ProjectSessionStore sessions, IConversationStore conversations, IFullDuplexRunCoordinator coordinator, IMessageAttachmentStore attachments, StorageLifecycleService lifecycle, ILifecycleManager lifecycleManager, CancellationToken ct)
    {
        if (PeerDeviceEndpoints.IsObserverPeerRequest(req.HttpContext))
        {
            return PeerDeviceEndpoints.Problem(
                req.HttpContext,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Observer peers cannot submit session interactions.");
        }

        JsonElement el;
        try
        {
            el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { code = "invalid_request", message = "Body must be valid JSON." });
        }

        if (el.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { code = "invalid_request", message = "Body must be a JSON object." });

        var unknownField = el.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => name is not (
                "content" or "client_message_id" or "client_request_id" or "mode_version_id" or "permission_mode"
                or "dispatch_mode" or "target_run_id" or "expected_context_revision" or "expected_settings_revision"
                or "meeting_model_override" or "clear_meeting_model_override" or "space_options" or "attachment_ids"
                or "staged_attachment_ids" or "interrupt" or "interface_locale"));
        if (unknownField is not null)
            return Results.BadRequest(new { code = "unknown_field", message = $"Field '{unknownField}' is not part of the interaction contract." });

        string? bodyClientRequestId = null;
        if (el.TryGetProperty("client_request_id", out var crEl) && crEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (crEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { code = "invalid_request", message = "client_request_id must be a string." });
            bodyClientRequestId = crEl.GetString()?.Trim();
        }

        var dispatchMode = el.TryGetProperty("dispatch_mode", out var dmPreview) ? dmPreview.GetString()?.Trim().ToLowerInvariant() : "queued";
        if (dispatchMode is not ("queued" or "insert" or "parallel"))
            return Results.BadRequest(new { code = "invalid_request", message = "dispatch_mode must be queued|insert|parallel" });

        var ingressQueue = req.HttpContext.RequestServices.GetService<IPeerIngressQueueService>();
        // Out-of-band steering ('insert') bypasses peer_ingress_queue per spec §4.1.
        if (dispatchMode == "insert" || ingressQueue is null)
        {
            return await CreateInteractionCoreAsync(
                sessionId, el, req, cfgFactory, globalConfig, lifecycleDbFactory, tenant, modelResolver,
                sessions, conversations, coordinator, attachments, lifecycle, lifecycleManager, ct).ConfigureAwait(false);
        }

        var headerClientRequestId = req.Headers["x-tinadec-client-request-id"].FirstOrDefault()?.Trim();
        var bodyClientMessageId = el.TryGetProperty("client_message_id", out var cmEl) && cmEl.ValueKind == JsonValueKind.String
            ? cmEl.GetString()?.Trim()
            : null;
        var effectiveClientRequestId = !string.IsNullOrWhiteSpace(bodyClientRequestId)
            ? bodyClientRequestId!
            : !string.IsNullOrWhiteSpace(headerClientRequestId)
                ? headerClientRequestId!
                : !string.IsNullOrWhiteSpace(bodyClientMessageId)
                    ? bodyClientMessageId!
                    : Guid.NewGuid().ToString("N");

        var rawControllerId = req.Headers[PeerDeviceEndpoints.PeerControllerIdHeader].FirstOrDefault()?.Trim();
        var originDeviceId = string.IsNullOrWhiteSpace(rawControllerId)
            || string.Equals(rawControllerId, "local", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : rawControllerId;
        var rawControllerName = req.Headers[PeerDeviceEndpoints.PeerControllerNameHeader].FirstOrDefault()?.Trim();
        var originDisplayName = !string.IsNullOrWhiteSpace(rawControllerName)
            ? rawControllerName
            : originDeviceId.Length == 0
                ? (Environment.MachineName?.Trim() is { Length: > 0 } machine ? machine : "local")
                : originDeviceId;

        var outcome = await ingressQueue.ExecuteSerializedAsync(
            new PeerIngressSubmitCommand(
                SessionId: sessionId.ToString(),
                ClientRequestId: effectiveClientRequestId,
                OriginDeviceId: originDeviceId,
                OriginDisplayName: originDisplayName,
                Method: "session.send",
                PayloadJson: el.GetRawText()),
            async (ticket, innerCt) =>
            {
                var innerResult = await CreateInteractionCoreAsync(
                    sessionId, el, req, cfgFactory, globalConfig, lifecycleDbFactory, tenant, modelResolver,
                    sessions, conversations, coordinator, attachments, lifecycle, lifecycleManager, innerCt,
                    overrideClientMessageId: effectiveClientRequestId).ConfigureAwait(false);

                return await CaptureIngressWorkResultAsync(
                    req.HttpContext.RequestServices,
                    innerResult,
                    ticket,
                    effectiveClientRequestId).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(outcome.Location))
        {
            req.HttpContext.Response.Headers.Location = outcome.Location;
        }

        if (outcome.IdempotentReplay)
        {
            var replayBody = RewriteReplayResponseJson(outcome);
            return Results.Content(replayBody, "application/json; charset=utf-8", Encoding.UTF8, outcome.HttpStatusCode);
        }

        var responseBody = outcome.ResponseJson ?? "{}";
        var contentType = outcome.HttpStatusCode >= 400 && responseBody.Contains("\"type\":\"https://tinadec.dev/errors/", StringComparison.Ordinal)
            ? "application/problem+json"
            : "application/json; charset=utf-8";
        return Results.Content(responseBody, contentType, Encoding.UTF8, outcome.HttpStatusCode);
    }

    private static async Task<IResult> CreateInteractionCoreAsync(
        Guid sessionId,
        JsonElement el,
        HttpRequest req,
        IDbContextFactory<AgentConfigurationDbContext> cfgFactory,
        GlobalAgentConfiguration globalConfig,
        IDbContextFactory<LifecycleDbContext> lifecycleDbFactory,
        ITenantContextAccessor tenant,
        IAgentModelResolver modelResolver,
        ProjectSessionStore sessions,
        IConversationStore conversations,
        IFullDuplexRunCoordinator coordinator,
        IMessageAttachmentStore attachments,
        StorageLifecycleService lifecycle,
        ILifecycleManager lifecycleManager,
        CancellationToken ct,
        string? overrideClientMessageId = null)
    {
        var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        // Not "content is required" here: a message that carries a file does say something, it
        // just does not say it in words. The refusal belongs below, where the attachment ids have
        // been parsed, so the two together decide whether this turn says anything at all.
        var hasText = !string.IsNullOrWhiteSpace(content);
        var clientMessageId = el.TryGetProperty("client_message_id", out var cm) && cm.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cm.GetString())
            ? cm.GetString()!
            : (overrideClientMessageId ?? Guid.NewGuid().ToString("N"));
        var modeVersionId = el.TryGetProperty("mode_version_id", out var mv) && Guid.TryParse(mv.GetString(), out var g) ? g : (Guid?)null;
        var dispatchMode = el.TryGetProperty("dispatch_mode", out var dm) ? dm.GetString()?.Trim().ToLowerInvariant() : "queued";
        if (dispatchMode is not ("queued" or "insert" or "parallel")) return Results.BadRequest(new { code = "invalid_request", message = "dispatch_mode must be queued|insert|parallel" });
        var interfaceLocale = el.TryGetProperty("interface_locale", out var localeElement) && localeElement.ValueKind == JsonValueKind.String
            ? localeElement.GetString()
            : null;
        Guid? targetRunId = null;
        if (el.TryGetProperty("target_run_id", out var tr) && Guid.TryParse(tr.GetString(), out var tg)) targetRunId = tg;
        if (dispatchMode == "insert" && targetRunId is null) return Results.BadRequest(new { code = "invalid_request", message = "insert requires target_run_id" });
        // Hard insert (todo D2): the steering also cuts off what the run is doing now instead of
        // waiting for its next boundary. Only an insert steers a run, so only an insert can.
        var interrupt = false;
        if (el.TryGetProperty("interrupt", out var interruptElement) && interruptElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (interruptElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Results.BadRequest(new { code = "invalid_request", message = "interrupt must be a boolean." });
            interrupt = interruptElement.GetBoolean();
            if (interrupt && dispatchMode != "insert")
                return Results.BadRequest(new { code = "invalid_request", message = "interrupt applies to dispatch_mode insert only." });
        }
        MeetingModelOverrideDto? meetingModelOverride = null;
        if (el.TryGetProperty("meeting_model_override", out var overrideElement)
            && overrideElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            try { meetingModelOverride = overrideElement.Deserialize<MeetingModelOverrideDto>(SettingsJson); }
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
        if (el.TryGetProperty("expected_settings_revision", out var settingsRevisionElement))
        {
            if (settingsRevisionElement.ValueKind != JsonValueKind.Number || !settingsRevisionElement.TryGetInt64(out var settingsRevision) || settingsRevision < 0)
                return Results.BadRequest(new { code = "invalid_request", message = "expected_settings_revision must be a nonnegative integer." });
            if (settingsRevision != session.SettingsRevision)
                return Results.Conflict(new { code = "session_settings_conflict", message = "Session settings changed. Refresh before sending this message.", settings_revision = session.SettingsRevision });
        }
        var hasExplicitModelOverride = meetingModelOverride is not null;
        var clearModelOverride = false;
        if (el.TryGetProperty("clear_meeting_model_override", out var clearOverride))
        {
            if (clearOverride.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Results.BadRequest(new { code = "invalid_request", message = "clear_meeting_model_override must be a boolean." });
            clearModelOverride = clearOverride.GetBoolean();
        }
        if (clearModelOverride && hasExplicitModelOverride)
            return Results.BadRequest(new { code = "invalid_model_override", message = "Cannot set and clear meeting_model_override together." });
        if (!clearModelOverride && meetingModelOverride is null && session.MeetingModelOverride is { } sessionModel)
        {
            // The session's saved choice may name a provider deleted since, or one this storage
            // scope never had. It is then not carried into the run (nor echoed back): the run
            // uses the default route. The saved choice stays on the session for the UI to show.
            var saved = new MeetingModelOverrideDto { ProviderInstanceId = sessionModel.ProviderInstanceId, Model = sessionModel.Model };
            try
            {
                await modelResolver.PreviewAsync(new ModelResolutionPreviewRequestDto { MeetingModelOverride = saved }, ct).ConfigureAwait(false);
                meetingModelOverride = saved;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException) { }
        }
        var permissionMode = session.PermissionMode;
        if (el.TryGetProperty("permission_mode", out var pm))
        {
            if (pm.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { code = "invalid_request", message = "permission_mode must be a string." });
            permissionMode = pm.GetString()!.Trim().ToLowerInvariant();
        }
        var spaceOptions = session.SpaceOptions;
        var hasSpaceOptionsProperty = el.TryGetProperty("space_options", out var optionsElement);
        var hasExplicitSpaceOptions = hasSpaceOptionsProperty && optionsElement.ValueKind != JsonValueKind.Null;
        if (hasExplicitSpaceOptions)
        {
            try { spaceOptions = SpaceRunOptionsJson.Parse(optionsElement); }
            catch (JsonException) { return Results.BadRequest(new { code = "invalid_space_options", message = "space_options must contain supported spatial settings." }); }
        }
        else if (hasSpaceOptionsProperty && optionsElement.ValueKind == JsonValueKind.Null)
        {
            spaceOptions = session.ViewMode == "space" ? new SpaceRunOptions() : null;
        }
        try
        {
            ProjectSessionStore.ValidateSessionSettings(session.ViewMode, permissionMode, spaceOptions);
            await StorageEndpoints.ValidateWorkflowAsync(spaceOptions, session.TenantId, session.WorkspaceId, cfgFactory, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "invalid_session_settings", message = ex.Message }); }
        if (dispatchMode == "insert" && hasExplicitSpaceOptions)
            return Results.Conflict(new { code = "space_options_frozen", message = "Spatial options cannot change an already admitted run. They apply to the next task." });

        // An attachment is claimed by the message this interaction appends, so the ids
        // arrive with the send rather than in a second request that could race it.
        var stageService = req.HttpContext.RequestServices.GetService<IPeerAttachmentStageService>();
        var workspaceZeroCopyRefs = new List<PeerResolvedStagedAttachment>();
        var stagedAttachmentIdsEcho = new List<string>();
        var collectedAttachmentIds = new List<Guid>();

        if (el.TryGetProperty("staged_attachment_ids", out var stagedElement)
            && stagedElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (stagedElement.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { code = "staged_attachment_ids_invalid", message = "staged_attachment_ids must be an array of strings." });
            if (dispatchMode == "insert")
                return Results.BadRequest(new { code = "attachment_dispatch_unsupported", message = "Steering inserts no user message, so it cannot carry attachments. Send the file as its own message." });

            foreach (var item in stagedElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    return Results.BadRequest(new { code = "staged_attachment_ids_invalid", message = "Every staged attachment id must be a non-empty string." });

                var rawStageOrAttId = item.GetString()!.Trim();
                stagedAttachmentIdsEcho.Add(rawStageOrAttId);

                if (stageService is not null)
                {
                    try
                    {
                        var resolvedStage = await stageService.ResolveForMessageAsync(sessionId, rawStageOrAttId, ct).ConfigureAwait(false);
                        if (resolvedStage is not null)
                        {
                            if (resolvedStage.ZeroCopy)
                                workspaceZeroCopyRefs.Add(resolvedStage);
                            else if (resolvedStage.AttachmentId is { } attGuid)
                                collectedAttachmentIds.Add(attGuid);
                            continue;
                        }
                    }
                    catch (PeerException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
                    {
                        return Results.NotFound(new { code = ex.Code, message = ex.Message });
                    }
                    catch (PeerException ex)
                    {
                        return Results.BadRequest(new { code = ex.Code, message = ex.Message });
                    }
                }

                if (Guid.TryParse(rawStageOrAttId, out var fallbackGuid))
                {
                    collectedAttachmentIds.Add(fallbackGuid);
                }
                else
                {
                    return Results.BadRequest(new { code = "attachment_not_found", message = $"No staged attachment found for id '{rawStageOrAttId}'." });
                }
            }
        }

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
            foreach (var item in attachmentElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    return Results.BadRequest(new { code = "attachment_ids_invalid", message = "Every attachment id must be a guid or stage_id string." });
                }

                var rawId = item.GetString()!.Trim();
                if (stageService is not null)
                {
                    try
                    {
                        var resolvedFromStage = await stageService.ResolveForMessageAsync(sessionId, rawId, ct).ConfigureAwait(false);
                        if (resolvedFromStage is not null)
                        {
                            stagedAttachmentIdsEcho.Add(resolvedFromStage.StageId);
                            if (resolvedFromStage.ZeroCopy)
                                workspaceZeroCopyRefs.Add(resolvedFromStage);
                            else if (resolvedFromStage.AttachmentId is { } stageAttGuid)
                                collectedAttachmentIds.Add(stageAttGuid);
                            continue;
                        }
                    }
                    catch (PeerException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
                    {
                        return Results.NotFound(new { code = ex.Code, message = ex.Message });
                    }
                    catch (PeerException ex)
                    {
                        return Results.BadRequest(new { code = ex.Code, message = ex.Message });
                    }
                }

                if (!Guid.TryParse(rawId, out var parsedId))
                {
                    return Results.BadRequest(new { code = "attachment_ids_invalid", message = "Every attachment id must be a guid string." });
                }
                collectedAttachmentIds.Add(parsedId);
            }
        }

        var uniqueAttachmentIds = collectedAttachmentIds.Distinct().ToList();
        workspaceZeroCopyRefs = workspaceZeroCopyRefs
            .GroupBy(x => x.StageId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (uniqueAttachmentIds.Count + workspaceZeroCopyRefs.Count > MaxAttachmentsPerMessage)
        {
            return Results.BadRequest(new { code = "attachment_count_exceeded", message = $"A message carries at most {MaxAttachmentsPerMessage} attachments.", max = MaxAttachmentsPerMessage });
        }

        if (uniqueAttachmentIds.Count > 0)
        {
            var unbound = await attachments.ListAsync(sessionId, ct).ConfigureAwait(false);
            var replayTarget = string.IsNullOrWhiteSpace(clientMessageId)
                ? null
                : await conversations.FindMessageByClientMessageIdAsync(sessionId, clientMessageId, ct).ConfigureAwait(false);
            var presentable = unbound
                .Where(row => row.MessageId is null || (replayTarget is not null && row.MessageId == replayTarget.Id))
                .Select(row => row.Id);
            var missing = uniqueAttachmentIds.Except(presentable).ToArray();
            if (missing.Length > 0)
            {
                return Results.BadRequest(new { code = "attachment_not_found", message = $"No unbound attachment in this session has the id(s): {string.Join(", ", missing)}.", missing });
            }
            attachmentIds = uniqueAttachmentIds;
        }

        if (!hasText && attachmentIds is not { Count: > 0 } && workspaceZeroCopyRefs.Count == 0)
        {
            // Same machine code the empty-content contract has always answered with; only the
            // message grew, because "content is required" was a lie by omission - attaching the
            // file is the other way to say something.
            return Results.BadRequest(new { code = "invalid_request", message = "content is required unless the message carries an attachment." });
        }

        if (workspaceZeroCopyRefs.Count > 0)
        {
            var refLines = workspaceZeroCopyRefs
                .Where(r => !string.IsNullOrWhiteSpace(r.SourcePath) && !content.Contains(r.SourcePath!, StringComparison.Ordinal))
                .Select(r => $"[Workspace file reference: {r.FileName} ({r.SourcePath})]")
                .ToArray();
            if (refLines.Length > 0)
            {
                content = hasText
                    ? $"{content}\n\n{string.Join("\n", refLines)}"
                    : string.Join("\n", refLines);
                hasText = !string.IsNullOrWhiteSpace(content);
            }
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

        // A file with no words is not a request, so this turn appends to the transcript and
        // starts nothing. Run admission keeps its own "message content is required" rule
        // untouched - what changed is the refusal to pretend that uploading a file is a
        // question. The next typed turn sees the file: the context builder lists attachments
        // bound to user messages inside its window, so the bytes reach the model through the
        // evidence section plus, when needed, `read_attachment` by the id printed there.
        if (!hasText)
        {
            var fileOnlyMessage = await conversations.AppendMessageAsync(
                sessionId, "user", string.Empty, clientMessageId: clientMessageId, cancellationToken: ct).ConfigureAwait(false);
            var fileOnlyBind = await BindAttachmentsAsync(fileOnlyMessage.Id).ConfigureAwait(false);
            if (fileOnlyBind is not null) return fileOnlyBind;
            return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{fileOnlyMessage.Id}", new
            {
                interaction_id = fileOnlyMessage.Id,
                session_id = sessionId,
                message_id = fileOnlyMessage.Id,
                // No run_id key at all: this host drops nulls on write, so "there is no run"
                // travels as an absent field - which is what the client already tests with
                // `if (resp.run_id)`. Writing `run_id = null` here would be a comment posing
                // as data.
                status = "message_only",
                attachment_ids = attachmentIds,
                client_message_id = clientMessageId,
                context_revision = await conversations.GetContextRevisionAsync(sessionId, ct).ConfigureAwait(false),
                correlation_id = clientMessageId
            });
        }

        // mode_version validation if provided
        if (modeVersionId.HasValue)
        {
            var published = await AgentConfigurationLookup.HasPublishedModeAsync(cfgFactory, globalConfig,
                session.TenantId, session.WorkspaceId, modeVersionId.Value, ct).ConfigureAwait(false);
            if (!published) return Results.BadRequest(new { code = "invalid_request", message = "mode_version_id must reference a published mode in this workspace" });
        }

        // Mode identity is the published ModeVersion only. Resolution order:
        // explicit mode_version_id > the session's bound mode version > the
        // workspace default. This per-message selection is passed to admission;
        // only the session settings endpoint changes defaults for later messages.
        modeVersionId ??= session.ModeVersionId;
        if (modeVersionId is null)
        {
            modeVersionId = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
                cfgFactory, globalConfig, session.TenantId, session.WorkspaceId, ct).ConfigureAwait(false);
        }
        if (modeVersionId is null)
            return Results.Conflict(new { code = "agent_mode_not_configured", message = "A published Agent Mode must be configured before creating an interaction." });

        // A disabled pack fails explicitly: the run must not silently fall back to
        // a different roster than the one the user is looking at. The client is
        // told which pack to re-enable (or to pick another mode).
        var ownerPack = await AgentConfigurationLookup.FindPackAsync(cfgFactory, globalConfig,
            session.TenantId, session.WorkspaceId, modeVersionId.Value, ct).ConfigureAwait(false);
        if (ownerPack is { } owner && !string.Equals(owner.Status, "active", StringComparison.Ordinal))
        {
            return Results.Conflict(new
            {
                code = "pack_disabled",
                message = $"Agent pack '{owner.PackId}' is disabled. Enable it or choose a mode from an enabled pack.",
                pack_id = owner.PackId
            });
        }

        if (dispatchMode == "insert" && (hasExplicitModelOverride || clearModelOverride))
            return Results.Conflict(new { code = "model_override_frozen", message = "A model override cannot be changed when inserting into an already frozen run." });

        // A session that has said nothing yet takes the conversation identity of the mode its
        // first message is sent in. Revision 0 is the cheap pre-check (every append bumps it);
        // the store re-checks for messages before it writes.
        TinadecCore.Runtime.ConversationIdentityResolver.ConversationIdentity? resolvedIdentity = null;
        if (modeVersionId.HasValue)
        {
            await using var cfg = await cfgFactory.CreateDbContextAsync(ct);
            var snapshotJson = await cfg.ModeVersions.AsNoTracking()
                .Where(x => x.Id == modeVersionId.Value && x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId)
                .Select(x => x.SnapshotJson)
                .FirstOrDefaultAsync(ct);
            var definitions = await cfg.AgentDefinitions.AsNoTracking()
                .Where(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId)
                .Select(x => new TinadecCore.Runtime.ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson))
                .ToListAsync(ct);
            resolvedIdentity = TinadecCore.Runtime.ConversationIdentityResolver.Resolve(snapshotJson, definitions);
        }
        var identityDiffers = resolvedIdentity is not null
            && !string.Equals(resolvedIdentity.TemplateSlug, session.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase);
        if (identityDiffers && await sessions.GetContextRevisionAsync(sessionId, ct).ConfigureAwait(false) == 0)
        {
            await sessions.AdoptConversationIdentityIfEmptyAsync(sessionId, resolvedIdentity!.NodeKey, resolvedIdentity.TemplateSlug, ct).ConfigureAwait(false);
        }
        // Todo E6 (one conversation identity): a session frozen on a legacy identity (an agent no
        // mode still points at) may move to the requested mode's identity while nothing is running —
        // an active run freezes its roster against the identity it started with, so under those the
        // freeze gate's 409 stands on purpose.
        else if (identityDiffers && (await lifecycleManager.ListActiveRunsAsync(sessionId, ct).ConfigureAwait(false)).Count == 0)
        {
            await sessions.MigrateConversationIdentityAsync(sessionId, resolvedIdentity!.NodeKey, resolvedIdentity.TemplateSlug, ct).ConfigureAwait(false);
        }

        // An interaction owns its submitted snapshot only. Session defaults are
        // changed by PATCH /sessions, so promoting an older queued message cannot
        // roll a subsequently selected mode back. Admission freezes this explicit
        // mode version after the conversation identity checks above.

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
            await lifecycle.AppendEventAsync(targetRunId.Value, "interaction.steering", new { interaction_id = Guid.NewGuid(), target_run_id = targetRunId.Value, content, dispatch_mode = dispatchMode, interrupt }, $"Steering injected into run {targetRunId}", "info", cancellationToken: ct);
            await lifecycle.AppendRunStreamAsync(targetRunId.Value, new DurableRunStreamAppend(Guid.NewGuid(), "steering", null, IdempotencyKey: $"run:{targetRunId}:steering:{Guid.NewGuid():N}"), ct);
            // The steering is durable above, so whatever the run redoes after the cut reads it. The
            // signal reaches the run's calls on this host; elsewhere the next model call reads it anyway.
            var interruptedModelCalls = 0;
            if (interrupt && req.HttpContext.RequestServices.GetService<IRunInterrupts>() is { } interrupts)
            {
                interruptedModelCalls = interrupts.Request(targetRunId.Value, new RunInterruptRequest(patch.PatchId, content, DateTimeOffset.UtcNow));
                await lifecycle.AppendEventAsync(targetRunId.Value, "interaction.interrupt_requested",
                    new { target_run_id = targetRunId.Value, patch_id = patch.PatchId, interrupted_model_calls = interruptedModelCalls },
                    "The user interrupted the run to steer it.", "info", cancellationToken: ct).ConfigureAwait(false);
            }
            // A supervision escalation is a durable user-review gate: the engine
            // re-parks on every tick while the checkpoint phase is awaiting_user, so
            // merely enqueueing a steering patch leaves it parked and the correction is
            // never read. Supplying correction text IS the user decision to replan, so
            // move the run back to executing first (the engine then applies the patch
            // and returns the checkpoint to planning).
            var parkedRun = await lifecycle.FindRunAsync(targetRunId.Value, ct).ConfigureAwait(false);
            if (parkedRun is not null && parkedRun.Status == "awaiting_user")
            {
                await lifecycle.SetRunStatusAsync(
                    targetRunId.Value,
                    "executing",
                    "User correction accepted; resuming after supervision review.",
                    ct).ConfigureAwait(false);
            }
            var engine = (IFullDuplexRunEngine)req.HttpContext.RequestServices.GetRequiredService(typeof(IFullDuplexRunEngine));
            await engine.EnqueueAsync(targetRunId.Value, ct);
            var steeringCursor = await RunStreamCursorAsync(lifecycleDbFactory, targetRunId, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{targetRunId.Value}", new { interaction_id = Guid.NewGuid(), session_id = sessionId, run_id = targetRunId.Value, dispatch_mode = dispatchMode, status = "steering_injected", content, client_message_id = clientMessageId, context_revision = patch.CurrentRevision, stream_cursor = steeringCursor, correlation_id = clientMessageId, interrupt, interrupted_model_calls = interruptedModelCalls });
        }

        // queued / parallel: normal admission via coordinator
        RunSubmission? admission = null;
        string admissionStatus = "queued";
        var invocationOverride = meetingModelOverride is null
            ? null
            : new SessionModelOverride(meetingModelOverride.ProviderInstanceId, meetingModelOverride.Model);
        try
        {
            // Mode identity is frozen from the session's bound/persisted mode
            // version; no legacy application-mode/agent-mode admission surface
            // exists anymore. Unattended permission policies ride on
            // permission_mode alone.
            // queued = after what the session is doing now (todo D1); parallel = alongside it,
            // bounded by the active-run limit. The coordinator decides with the run ledger in hand.
            admission = await coordinator.SubmitAsync(new FullDuplexInvocation(
                sessionId,
                content,
                clientMessageId!,
                permissionMode,
                targetRunId,
                expectedRev,
                invocationOverride,
                modeVersionId,
                QueueBehindActiveRun: dispatchMode == "queued", SpaceOptions: spaceOptions, SessionSettingsCaptured: true,
                InterfaceLocale: interfaceLocale), ct);
            admissionStatus = dispatchMode == "parallel" ? "assigned" : "queued";
        }
        catch (RunAdmissionException ex) when (ex.Code is "ACTIVE_RUN_LIMIT" or "SESSION_BUSY" && dispatchMode == "queued")
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
            var activeRun = await QueueOwnerAsync(lifecycleDb, runs, ct).ConfigureAwait(false);
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
                        modeVersionId,
                        QueueBehindActiveRun: true, SpaceOptions: spaceOptions, SessionSettingsCaptured: true,
                        InterfaceLocale: interfaceLocale), ct).ConfigureAwait(false);
                    admissionStatus = "queued";
                }
                catch (RunAdmissionException retry) when (retry.Code is "ACTIVE_RUN_LIMIT" or "SESSION_BUSY")
                {
                    runs = await lifecycle.ListRunsAsync(sessionId, ct).ConfigureAwait(false);
                    activeRun = await QueueOwnerAsync(lifecycleDb, runs, ct).ConfigureAwait(false);
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
                var queuedPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["content"] = content,
                    ["client_message_id"] = clientMessageId,
                    ["dispatch_mode"] = dispatchMode,
                    ["mode_version_id"] = modeVersionId,
                    ["permission_mode"] = permissionMode,
                    ["space_options"] = spaceOptions,
                    ["meeting_model_override"] = meetingModelOverride is null
                        ? null
                        : new
                        {
                            provider_instance_id = meetingModelOverride.ProviderInstanceId,
                            model = meetingModelOverride.Model
                        }
                };
                if (interfaceLocale is not null) queuedPayload["interface_locale"] = interfaceLocale;
                directive.PayloadJson = JsonSerializer.Serialize(queuedPayload, SettingsJson);
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
        catch (RunAdmissionException ex) when (ex.Code == "conversation_identity_locked_mismatch")
        {
            // The gate's own message names template slugs; the person reading it needs to know
            // what happened and what to do instead. The raw text travels as `diagnostic`, not
            // `detail`: the Gateway's problem mapping reads detail before message, so a raw
            // detail would replace the sentence meant for the user.
            return Results.Conflict(new
            {
                code = ex.Code,
                message = "所选模式由另一位智能体负责和你对话，已经开始对话的会话不能中途更换它。请新建会话后再选择这个模式。",
                diagnostic = ex.Message
            });
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
                // Not `detail`: the Gateway would surface it in place of the message above.
                diagnostic = ex.Message
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
        return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{admission.TurnId}", new
        {
            interaction_id = admission.TurnId,
            session_id = sessionId,
            run_id = admission.RunId,
            turn_id = admission.TurnId,
            message_id = admission.MessageId,
            dispatch_mode = dispatchMode,
            status,
            mode_version_id = modeVersionId,
            meeting_model_override = meetingModelOverride,
            attachment_ids = attachmentIds,
            staged_attachment_ids = stagedAttachmentIdsEcho.Count > 0 ? stagedAttachmentIdsEcho.Distinct().ToArray() : null,
            workspace_references = workspaceZeroCopyRefs.Count > 0
                ? workspaceZeroCopyRefs.Select(r => new { stage_id = r.StageId, file_name = r.FileName, source_path = r.SourcePath, total_bytes = r.TotalBytes }).ToArray()
                : null,
            client_message_id = clientMessageId,
            context_revision = admission.ContextRevision,
            stream_cursor = admissionCursor,
            correlation_id = clientMessageId
        });
    }

    static async Task<IResult> GetIngressStatus(
        Guid sessionId,
        string clientRequestId,
        long? after_seq,
        HttpContext http,
        StorageLifecycleService lifecycle,
        IDbContextFactory<LifecycleDbContext> lifecycleDbFactory,
        CancellationToken ct)
    {
        var ingressQueue = http.RequestServices.GetService<IPeerIngressQueueService>();
        if (ingressQueue is null || string.IsNullOrWhiteSpace(clientRequestId))
        {
            return Results.Ok(new PeerIngressStatusDto
            {
                Found = false,
                SessionId = sessionId.ToString(),
                ClientRequestId = clientRequestId ?? string.Empty
            });
        }

        var status = await ingressQueue.GetIngressStatusAsync(sessionId.ToString(), clientRequestId.Trim(), ct).ConfigureAwait(false);
        if (!status.Found)
        {
            return Results.Ok(status);
        }

        long? liveMaxSeq = status.LastEventSeq;
        string? streamCursor = null;
        IReadOnlyList<object>? deltaEvents = null;

        Guid? parsedRunId = Guid.TryParse(status.RunId, out var rGuid) ? rGuid : null;
        if (parsedRunId is { } runGuid)
        {
            var cursorVal = await RunStreamCursorAsync(lifecycleDbFactory, runGuid, ct).ConfigureAwait(false);
            streamCursor = cursorVal.ToString(CultureInfo.InvariantCulture);
            liveMaxSeq = Math.Max(liveMaxSeq ?? 0L, cursorVal);
        }

        var sessionEvents = await lifecycle.FollowEventsAsync(sessionId, new Dictionary<Guid, long>(), ct).ConfigureAwait(false);
        var matchingEvents = parsedRunId is { } targetRun
            ? sessionEvents.Where(e => string.Equals(e.RunId, targetRun.ToString(), StringComparison.OrdinalIgnoreCase)).ToList()
            : sessionEvents.ToList();

        foreach (var evt in matchingEvents)
        {
            if (TryExtractEventSeq(evt) is { } seq && seq > (liveMaxSeq ?? 0L))
            {
                liveMaxSeq = seq;
            }
        }

        if (after_seq is { } afterThreshold)
        {
            var filtered = new List<object>();
            foreach (var evt in matchingEvents)
            {
                var seq = TryExtractEventSeq(evt);
                if (seq is null || seq.Value > afterThreshold)
                {
                    filtered.Add(evt);
                }
            }
            deltaEvents = filtered;
        }

        return Results.Ok(new PeerIngressStatusDto
        {
            Found = true,
            SessionId = status.SessionId,
            ClientRequestId = status.ClientRequestId,
            EnqueuedSeq = status.EnqueuedSeq,
            Stage = status.Stage,
            OriginDeviceId = status.OriginDeviceId,
            OriginDisplayName = status.OriginDisplayName,
            RunId = status.RunId,
            TurnId = status.TurnId,
            MessageId = status.MessageId,
            LastEventSeq = liveMaxSeq,
            StreamCursor = streamCursor,
            RejectErrorCode = status.RejectErrorCode,
            AfterSeq = after_seq,
            Events = deltaEvents
        });
    }

    private static long? TryExtractEventSeq(EventEnvelope item)
    {
        if (!item.Payload.TryGetValue("sequence", out var value) || value is null) return null;
        return value switch
        {
            long s => s,
            int i => i,
            JsonElement { ValueKind: JsonValueKind.Number } num when num.TryGetInt64(out var parsed) => parsed,
            string str when long.TryParse(str, out var parsedStr) => parsedStr,
            _ => null
        };
    }

    private static async Task<PeerIngressWorkResult> CaptureIngressWorkResultAsync(
        IServiceProvider services,
        IResult innerResult,
        PeerIngressTicketContext ticket,
        string clientRequestId)
    {
        var captureContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        await using var bodyStream = new MemoryStream();
        captureContext.Response.Body = bodyStream;

        await innerResult.ExecuteAsync(captureContext).ConfigureAwait(false);
        var statusCode = captureContext.Response.StatusCode;
        var location = captureContext.Response.Headers.Location.FirstOrDefault();
        var rawJson = Encoding.UTF8.GetString(bodyStream.ToArray());

        if (statusCode is >= 200 and < 300)
        {
            string? runId = null;
            string? turnId = null;
            string? messageId = null;
            long? lastEventSeq = null;
            var enrichedJson = rawJson;

            try
            {
                if (JsonNode.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson) is JsonObject obj)
                {
                    runId = obj["run_id"]?.GetValue<string>();
                    turnId = obj["turn_id"]?.GetValue<string>() ?? (runId is not null ? obj["interaction_id"]?.GetValue<string>() : null);
                    messageId = obj["message_id"]?.GetValue<string>();
                    if (obj["stream_cursor"] is JsonValue cursorVal)
                    {
                        if (cursorVal.TryGetValue<long>(out var cursorLong))
                            lastEventSeq = cursorLong;
                        else if (cursorVal.TryGetValue<string>(out var cursorStr) && long.TryParse(cursorStr, out var parsedCursor))
                            lastEventSeq = parsedCursor;
                    }

                    obj["client_request_id"] = clientRequestId;
                    obj["enqueued_seq"] = ticket.EnqueuedSeq;
                    obj["stage"] = "processed";
                    obj["idempotent_replay"] = false;
                    enrichedJson = obj.ToJsonString();
                }
            }
            catch (JsonException)
            {
                // Preserve original response body if non-object.
            }

            return new PeerIngressWorkResult(
                Accepted: true,
                HttpStatusCode: statusCode,
                RunId: runId,
                TurnId: turnId,
                MessageId: messageId,
                LastEventSeq: lastEventSeq,
                RejectErrorCode: null,
                ResponseJson: enrichedJson,
                Location: location);
        }
        else
        {
            var rejectCode = "rejected";
            try
            {
                using var errDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
                if (errDoc.RootElement.ValueKind == JsonValueKind.Object
                    && errDoc.RootElement.TryGetProperty("code", out var codeEl)
                    && codeEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(codeEl.GetString()))
                {
                    rejectCode = codeEl.GetString()!;
                }
            }
            catch (JsonException)
            {
            }

            return new PeerIngressWorkResult(
                Accepted: false,
                HttpStatusCode: statusCode,
                RejectErrorCode: rejectCode,
                ResponseJson: rawJson,
                Location: location);
        }
    }

    private static string RewriteReplayResponseJson(PeerIngressExecutionOutcome outcome)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(outcome.ResponseJson)
                && JsonNode.Parse(outcome.ResponseJson) is JsonObject obj)
            {
                obj["client_request_id"] = outcome.ClientRequestId;
                obj["enqueued_seq"] = outcome.EnqueuedSeq;
                obj["stage"] = outcome.Stage;
                obj["idempotent_replay"] = true;
                return obj.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        var fallback = new JsonObject
        {
            ["session_id"] = outcome.SessionId,
            ["client_request_id"] = outcome.ClientRequestId,
            ["enqueued_seq"] = outcome.EnqueuedSeq,
            ["stage"] = outcome.Stage,
            ["idempotent_replay"] = true
        };
        if (outcome.RunId is not null) fallback["run_id"] = outcome.RunId;
        if (outcome.TurnId is not null)
        {
            fallback["turn_id"] = outcome.TurnId;
            fallback["interaction_id"] = outcome.TurnId;
        }
        if (outcome.MessageId is not null) fallback["message_id"] = outcome.MessageId;
        if (outcome.LastEventSeq is { } seq) fallback["last_event_seq"] = seq;
        return fallback.ToJsonString();
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
    /// <summary>
    /// Which unfinished run a queued message waits behind. The session's queue has one owner at a
    /// time, so a run that already holds pending queued messages keeps receiving them (a new message
    /// never jumps ahead of older ones held by another run); otherwise the newest unfinished run.
    /// </summary>
    private static async Task<RunRecord?> QueueOwnerAsync(LifecycleDbContext db, IReadOnlyList<RunRecord> runs, CancellationToken ct)
    {
        var active = runs.Where(run => run.Status is not ("completed" or "failed" or "cancelled")).ToList();
        if (active.Count <= 1) return active.FirstOrDefault();
        var ids = active.Select(run => (Guid?)run.Id).ToArray();
        var owners = await db.RunDirectives.AsNoTracking()
            .Where(x => x.Kind == "queued_interaction" && x.Status == "pending" && ids.Contains(x.RunId))
            .Select(x => x.RunId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        // ListRunsAsync is newest first.
        return active.FirstOrDefault(run => owners.Contains(run.Id)) ?? active[0];
    }

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

    static async Task<IResult> CancelInteraction(Guid sessionId, Guid interactionId, StorageLifecycleService lifecycle, IFullDuplexRunCoordinator coordinator,
        IDbContextFactory<LifecycleDbContext> lifecycleDbFactory, CancellationToken ct)
    {
        var runs = await lifecycle.ListRunsAsync(sessionId, ct);
        var run = runs.FirstOrDefault(r => r.TurnId == interactionId);
        if (run is null)
        {
            // Not a run yet: a message still waiting in the session's queue (todo D1). Cancelling it
            // takes it out of the queue; its text stays in the conversation as what the user said.
            await using var db = await lifecycleDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var queued = await db.RunDirectives.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == interactionId && x.SessionId == sessionId && x.Kind == "queued_interaction", ct).ConfigureAwait(false);
            if (queued is null) return Results.NotFound(new { code = "not_found" });
            var cancelled = await db.RunDirectives
                .Where(x => x.Id == interactionId && x.Status == "pending")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.DrainedAt, now).SetProperty(x => x.UpdatedAt, now), ct)
                .ConfigureAwait(false);
            if (cancelled == 1 && queued.RunId is { } owner)
                await lifecycle.AppendEventAsync(owner, "interaction.queue_cancelled", new { interaction_id = interactionId, directive_id = interactionId },
                    "A queued message was taken out of the queue.", "info", cancellationToken: ct).ConfigureAwait(false);
            return cancelled == 1
                ? Results.Ok(new { interaction_id = interactionId, run_id = queued.RunId, status = "cancelled", action = "dequeued" })
                : Results.Conflict(new { code = "conflict", message = "The message already left the queue: it is running or was decided." });
        }
        var res = await coordinator.ControlAsync(run.Id, new RunControlCommand("cancel", null, null), ct);
        return Results.Ok(new { interaction_id = interactionId, run_id = run.Id, status = res.Status, action = res.Action });
    }

    static async Task<IResult> ListInteractions(Guid sessionId, StorageLifecycleService lifecycle, CancellationToken ct)
    {
        var runs = await lifecycle.ListRunsAsync(sessionId, ct);
        return Results.Ok(runs.Select(r => new { interaction_id = r.TurnId, run_id = r.Id, session_id = r.SessionId, status = r.Status, created_at = r.CreatedAt, updated_at = r.UpdatedAt }));
    }

}
