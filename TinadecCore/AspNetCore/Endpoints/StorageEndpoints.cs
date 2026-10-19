using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Contracts.Events;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Runtime;

namespace TinadecCore.AspNetCore.Endpoints;

public static class StorageEndpoints
{
    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/projects", async (string? lifecycle_status, string? lifecycleStatus, HttpContext http, ProjectSessionStore store, CancellationToken ct) =>
        {
            var selected = lifecycle_status ?? lifecycleStatus ?? TinadecCore.Memory.LifecycleStatuses.Active;
            if (!TinadecCore.Memory.LifecycleStatuses.IsKnown(selected))
                return Results.BadRequest(new { code = "INVALID_LIFECYCLE_STATUS", message = "lifecycle_status must be active, archived, or trashed." });
            if (http.RequestServices.GetService<IStorageScopeRegistry>() is { } registry
                && (!http.Request.Headers.ContainsKey(StorageScopeHttpExtensions.StorageHeader) || http.Request.Headers[StorageScopeHttpExtensions.StorageHeader] == "user"))
            {
                var projects = new List<object>();
                foreach (var scope in registry.List().Where(x => x.ScopeKind == "project"))
                {
                    try
                    {
                        await using var lease = await registry.AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
                        var workspace = (registry as IWorkspaceRegistry)?.ReadWorkspace(scope.StorageId);
                        projects.AddRange((await lease.Services.GetRequiredService<ProjectSessionStore>().ListProjectsAsync(selected, ct).ConfigureAwait(false)).Select(x => ToProject(x, scope.StorageId, workspace, scope)));
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && StorageScopeRowError.CanRepresent(ex))
                    {
                        // A scope that cannot mount is still listed: a workspace the person can see and act on
                        // beats one that silently disappears. The row carries the same classification contract as
                        // an error response, so the client can offer the same recovery actions here and in a toast.
                        var failure = StorageScopeRowError.From(http, ex);
                        if (selected == TinadecCore.Memory.LifecycleStatuses.Active)
                            projects.Add(UnavailableProject(scope, failure));
                    }
                }
                return Results.Ok(projects);
            }
            var storageId = http.RequestServices.GetService<IScopeStorageLocations>()?.StorageId;
            var definition = http.RequestServices.GetService<IWorkspaceDefinitionProvider>()?.Read();
            return Results.Ok((await store.ListProjectsAsync(selected, ct).ConfigureAwait(false)).Select(x => ToProject(x, storageId, definition)));
        });

        app.MapPost("/api/v1/projects", async (CreateProjectRequest request, HttpContext http, ProjectSessionStore store, CancellationToken ct) =>
        {
            try
            {
                if (http.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
                {
                    var scope = await registry.OpenAsync(new(request.Path, request.Name), ct).ConfigureAwait(false);
                    await using var lease = await registry.AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
                    var opened = await lease.Services.GetRequiredService<ProjectSessionStore>().GetProjectAnyStatusAsync(scope.ProjectId!.Value, ct).ConfigureAwait(false);
                    return Results.Created($"/api/v1/projects/{opened!.Id}", ToProject(opened, scope.StorageId, (registry as IWorkspaceRegistry)?.ReadWorkspace(scope.StorageId), scope));
                }
                var project = await store.CreateProjectAsync(request.Name, request.Path, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/projects/{project.Id}", ToProject(project));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_PROJECT", message = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { code = "DUPLICATE_PROJECT_ROOT", message = ex.Message }); }
        });

        app.MapPatch("/api/v1/projects/{projectId}", async (string projectId, UpdateProjectRequest request, HttpContext http, ProjectSessionStore store, CancellationToken ct) =>
        {
            if (!Guid.TryParse(projectId, out var id)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { code = "INVALID_PROJECT", message = "Project name is required." });
            try
            {
                if (http.RequestServices.GetService<IWorkspaceRegistry>() is { } workspaces
                    && http.RequestServices.GetService<IScopeStorageLocations>() is { ProjectRoot: not null } locations)
                {
                    var expected = http.Request.Headers.IfMatch.ToString();
                    if (expected.Length == 0) return Results.Problem(statusCode: 428, title: "precondition_required");
                    var current = workspaces.ReadWorkspace(locations.StorageId);
                    await workspaces.EditWorkspaceAsync(locations.StorageId, new(request.Name!, current.Roots, current.PrimaryRootId, current.Icon, current.Color), expected.Trim('"'), ct).ConfigureAwait(false);
                }
                var project = await store.RenameProjectAsync(id, request.Name!, ct).ConfigureAwait(false);
                if (project is null) return Results.NotFound(new { code = "PROJECT_NOT_FOUND" });
                return Results.Ok(ToProject(project, http.RequestServices.GetService<IScopeStorageLocations>()?.StorageId,
                    http.RequestServices.GetService<IWorkspaceDefinitionProvider>()?.Read()));
            }
            catch (ConfigurationDocumentException ex) { return Results.Json(new { code = ex.Code, message = ex.Message, diagnostics = ex.Diagnostics },
                statusCode: ex.Code == "configuration_conflict" ? 412 : 403); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_PROJECT", message = ex.Message }); }
        });

        MapProjectLifecycleEndpoints(app, "archive", lifecycle => lifecycle.ArchiveProjectAsync);
        MapProjectLifecycleEndpoints(app, "trash", lifecycle => lifecycle.TrashProjectAsync);
        MapProjectLifecycleEndpoints(app, "restore", lifecycle => lifecycle.RestoreProjectAsync);

        app.MapDelete("/api/v1/projects/{projectId}", async (string projectId, TinadecCore.Runtime.ProjectSessionLifecycleService lifecycle, CancellationToken ct) =>
        {
            if (!Guid.TryParse(projectId, out var id)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
            try
            {
                await lifecycle.PurgeProjectAsync(id, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "PROJECT_NOT_FOUND" }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { code = "invalid_lifecycle_transition", message = ex.Message }); }
            catch (TinadecCore.Runtime.ActiveRunConflictException ex) { return Results.Conflict(new { code = "active_run_conflict", message = ex.Message, run_id = ex.RunId }); }
        });

        app.MapGet("/api/v1/sessions", async (string? projectId, string? project_id, string? lifecycle_status, string? lifecycleStatus, HttpContext http, ProjectSessionStore store, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, GlobalAgentConfiguration globalConfig, CancellationToken ct) =>
        {
            var selected = projectId ?? project_id;
            if (selected is not null && !Guid.TryParse(selected, out var parsed)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
            var status = lifecycle_status ?? lifecycleStatus ?? TinadecCore.Memory.LifecycleStatuses.Active;
            if (!TinadecCore.Memory.LifecycleStatuses.IsKnown(status))
                return Results.BadRequest(new { code = "INVALID_LIFECYCLE_STATUS", message = "lifecycle_status must be active, archived, or trashed." });
            if (http.RequestServices.GetService<IStorageScopeRegistry>() is { } registry && !http.Request.Headers.ContainsKey(StorageScopeHttpExtensions.StorageHeader))
            {
                var aggregate = new List<object>();
                foreach (var scope in registry.List().Where(x => selected is null || x.ProjectId == Guid.Parse(selected)))
                {
                    await using var lease = await registry.AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
                    var records = await lease.Services.GetRequiredService<ProjectSessionStore>().ListSessionsAsync(selected is null ? null : Guid.Parse(selected), status, ct).ConfigureAwait(false);
                    var defaults = await LoadSessionModeDefaultsAsync(records,
                        lease.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>(),
                        lease.Services.GetRequiredService<GlobalAgentConfiguration>(), ct).ConfigureAwait(false);
                    foreach (var record in records) aggregate.Add(ToSessionEnriched(record, scope.StorageId, defaults));
                }
                return Results.Ok(aggregate);
            }
            var sessions = await store.ListSessionsAsync(selected is null ? null : Guid.Parse(selected), status, ct).ConfigureAwait(false);
            var sessionDefaults = await LoadSessionModeDefaultsAsync(sessions, cfgFactory, globalConfig, ct).ConfigureAwait(false);
            var enriched = new List<object>(sessions.Count);
            var scopeStorageId = http.RequestServices.GetService<IScopeStorageLocations>()?.StorageId;
            foreach (var s in sessions) enriched.Add(ToSessionEnriched(s, scopeStorageId, sessionDefaults));
            return Results.Ok(enriched);
        });

        app.MapPost("/api/v1/sessions", async (CreateSessionRequest request, ProjectSessionStore store, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, GlobalAgentConfiguration globalConfig, IAgentModelResolver modelResolver, ITenantContextAccessor tenant, CancellationToken ct) =>
        {
            Guid? projectId = null;
            if (!string.IsNullOrWhiteSpace(request.ProjectId))
            {
                if (!Guid.TryParse(request.ProjectId, out var parsedProjectId)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
                projectId = parsedProjectId;
            }
            try
            {
                var spaceOptions = ToSpaceOptions(request.SpaceOptions);
                ProjectSessionStore.ValidateSessionSettings(request.ViewMode ?? "flat", request.PermissionMode, spaceOptions);
                await ValidateWorkflowAsync(spaceOptions, tenant.Current.TenantId, tenant.Current.WorkspaceId, cfgFactory, globalConfig, ct).ConfigureAwait(false);
                Guid? modeVersionId = request.ModeVersionId;
                string? conversationNodeKey = null;
                string? conversationTemplateSlug = null;
                if (modeVersionId is null)
                    modeVersionId = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
                        cfgFactory, globalConfig, tenant.Current.TenantId, tenant.Current.WorkspaceId, ct).ConfigureAwait(false);
                if (modeVersionId is null)
                    return AgentModeNotConfigured("A published default Agent Mode must be configured before creating a session.");
                var modeFactory = await AgentConfigurationLookup.SelectModeFactoryAsync(
                    cfgFactory, globalConfig, tenant.Current.TenantId, tenant.Current.WorkspaceId,
                    modeVersionId.Value, ct).ConfigureAwait(false);
                await using (var cfg = await modeFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
                {
                    var modeVersion = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == modeVersionId
                        && x.TenantId == tenant.Current.TenantId && x.WorkspaceId == tenant.Current.WorkspaceId
                        && x.Status == "published", ct).ConfigureAwait(false);
                    if (modeVersion is null) return Results.BadRequest(new { code = "invalid_mode_version", message = "mode_version_id must reference a published Agent Mode version." });

                    // ConversationIdentity (frozen at creation): resolve the mode node
                    // carrying the conversation role — caller-requested node validated
                    // against the resolution tiers, otherwise the designated node.
                    var definitions = await cfg.AgentDefinitions.AsNoTracking()
                        .Where(x => x.TenantId == tenant.Current.TenantId && x.WorkspaceId == tenant.Current.WorkspaceId)
                        .Select(x => new TinadecCore.Runtime.ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson))
                        .ToListAsync(ct).ConfigureAwait(false);
                    var identity = request.ConversationNodeKey is { } requestedKey
                        ? TinadecCore.Runtime.ConversationIdentityResolver.ResolveRequested(requestedKey, modeVersion.SnapshotJson, definitions)
                        : TinadecCore.Runtime.ConversationIdentityResolver.Resolve(modeVersion.SnapshotJson, definitions);
                    if (identity is null)
                    {
                        return request.ConversationNodeKey is null
                            ? AgentModeNotConfigured("The selected Agent Mode does not declare a conversation node.")
                            : Results.Json(new { code = "conversation_identity_invalid", message = $"conversation_node_key '{request.ConversationNodeKey}' is not a conversation-capable node of the selected mode." }, statusCode: StatusCodes.Status422UnprocessableEntity);
                    }
                    conversationNodeKey = identity.NodeKey;
                    conversationTemplateSlug = identity.TemplateSlug;
                }
                SessionModelOverride? modelOverride = null;
                if (request.MeetingModelOverride is { } requestedOverride)
                {
                    if (requestedOverride.ProviderInstanceId == Guid.Empty)
                        return Results.BadRequest(new { code = "invalid_model_override", message = "meeting_model_override.provider_instance_id is required." });
                    await modelResolver.PreviewAsync(new ModelResolutionPreviewRequestDto { MeetingModelOverride = requestedOverride }, ct).ConfigureAwait(false);
                    modelOverride = new SessionModelOverride(requestedOverride.ProviderInstanceId, requestedOverride.Model);
                }
                var session = await store.CreateSessionAsync(projectId, request.Title, modeVersionId.Value, modelOverride, conversationNodeKey, conversationTemplateSlug, ct,
                    viewMode: request.ViewMode ?? "flat", permissionMode: request.PermissionMode, spaceOptions: spaceOptions).ConfigureAwait(false);
                return Results.Created($"/api/v1/sessions/{session.Id}", await ToSessionEnrichedAsync(session, cfgFactory, globalConfig, ct).ConfigureAwait(false));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "PROJECT_NOT_FOUND" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_SESSION", message = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { code = "invalid_model_override", message = ex.Message }); }
        });

        app.MapPatch("/api/v1/sessions/{sessionId}", async (string sessionId, UpdateSessionRequest request, ProjectSessionStore store, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, GlobalAgentConfiguration globalConfig, IAgentModelResolver modelResolver, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try
            {
                var session = await store.GetSessionAsync(id, ct).ConfigureAwait(false);
                if (session is null) return Results.NotFound(new { code = "SESSION_NOT_FOUND" });
                var spaceOptions = ToSpaceOptions(request.SpaceOptions);
                ProjectSessionStore.ValidateSessionSettings(session.ViewMode ?? "flat", request.PermissionMode, spaceOptions);
                await ValidateWorkflowAsync(spaceOptions, session.TenantId, session.WorkspaceId, cfgFactory, globalConfig, ct).ConfigureAwait(false);
                if (request.ClearMeetingModelOverride && request.MeetingModelOverride is not null)
                    return Results.BadRequest(new { code = "invalid_model_override", message = "Cannot set and clear meeting_model_override together." });
                if (request.ClearModeVersion && request.ModeVersionId is not null)
                    return Results.BadRequest(new { code = "invalid_mode_version", message = "Cannot set and clear mode_version_id together." });
                var requestedMode = request.ModeVersionId;
                if (request.ClearModeVersion)
                {
                    requestedMode = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
                        cfgFactory, globalConfig, session.TenantId, session.WorkspaceId, ct).ConfigureAwait(false);
                    if (requestedMode is null) return AgentModeNotConfigured("No published default mode is configured.");
                }
                if (requestedMode is { } requestedModeVersionId)
                {
                    var published = await AgentConfigurationLookup.HasPublishedModeAsync(
                        cfgFactory, globalConfig, session.TenantId, session.WorkspaceId,
                        requestedModeVersionId, ct).ConfigureAwait(false);
                    if (!published) return Results.BadRequest(new { code = "invalid_mode_version", message = "mode_version_id must reference a published Agent Mode version in this workspace." });
                }
                if (request.MeetingModelOverride is { } requestedOverride)
                {
                    if (requestedOverride.ProviderInstanceId == Guid.Empty)
                        return Results.BadRequest(new { code = "invalid_model_override", message = "meeting_model_override.provider_instance_id is required." });
                    await modelResolver.PreviewAsync(new ModelResolutionPreviewRequestDto { MeetingModelOverride = requestedOverride }, ct).ConfigureAwait(false);
                }
                var modelOverride = request.MeetingModelOverride is null
                    ? null
                    : new SessionModelOverride(request.MeetingModelOverride.ProviderInstanceId, request.MeetingModelOverride.Model);
                session = await store.UpdateSessionModeAsync(id, requestedMode, modelOverride, ct,
                    permissionMode: request.PermissionMode, spaceOptions: spaceOptions,
                    clearMeetingModelOverride: request.ClearMeetingModelOverride, title: request.Title,
                    expectedSettingsRevision: request.ExpectedSettingsRevision).ConfigureAwait(false);
                if (session is null) return Results.NotFound(new { code = "SESSION_NOT_FOUND" });
                return Results.Ok(await ToSessionEnrichedAsync(session, cfgFactory, globalConfig, ct).ConfigureAwait(false));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_SESSION", message = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { code = "invalid_model_override", message = ex.Message }); }
            catch (SessionSettingsConflictException ex) { return Results.Conflict(new { code = "session_settings_conflict", message = ex.Message, settings_revision = ex.CurrentRevision }); }
        });

        if (app.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IStorageScopeRegistry))
            && app.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(ISessionScopeTransferService)))
        {
        app.MapPost("/api/v1/sessions/{sessionId}/migrate", async (string sessionId, MigrateSessionRequest request,
            TinadecCore.Runtime.IStorageScopeRegistry scopes, TinadecCore.Runtime.ISessionScopeTransferService transfers,
            IScopeStorageLocations locations, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            if (locations.StorageId != "user") return Results.Conflict(new { code = "invalid_transfer_source", message = "Free-session transfer must be requested from its user storage scope." });
            try
            {
                var targetStorage = request.TargetStorageId;
                if (string.IsNullOrWhiteSpace(targetStorage) && Guid.TryParse(request.TargetProjectId, out var projectId))
                    targetStorage = scopes.List().FirstOrDefault(scope => scope.ProjectId == projectId)?.StorageId
                        ?? throw new KeyNotFoundException("The target project storage is not registered.");
                if (string.IsNullOrWhiteSpace(targetStorage) && !string.IsNullOrWhiteSpace(request.ProjectPath))
                    targetStorage = (await scopes.OpenAsync(new TinadecCore.Runtime.OpenStorageScopeRequest(request.ProjectPath, request.ProjectName), ct).ConfigureAwait(false)).StorageId;
                if (string.IsNullOrWhiteSpace(targetStorage))
                    return Results.BadRequest(new { code = "INVALID_MIGRATION_REQUEST", message = "target_storage_id, target_project_id, or project_path is required." });
                var receipt = await transfers.RequestAsync(id, targetStorage, ct).ConfigureAwait(false);
                return Results.Accepted("/api/v1/session-transfers/" + receipt.TransferId, ToTransferReceipt(receipt));
            }
            catch (TinadecCore.DmaEA.RunAdmissionException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "RESOURCE_NOT_FOUND" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_REQUEST", message = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { code = "CONFLICT", message = ex.Message }); }
        });

        app.MapGet("/api/v1/session-transfers/{transferId}", (Guid transferId, TinadecCore.Runtime.ISessionScopeTransferService transfers) =>
        {
            try { return Results.Ok(ToTransferReceipt(transfers.Get(transferId))); }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "transfer_not_found" }); }
        });
        }

        MapSessionLifecycleEndpoints(app, "archive", lifecycle => lifecycle.ArchiveSessionAsync);
        MapSessionLifecycleEndpoints(app, "trash", lifecycle => lifecycle.TrashSessionAsync);
        MapSessionLifecycleEndpoints(app, "restore", lifecycle => lifecycle.RestoreSessionAsync);

        app.MapDelete("/api/v1/sessions/{sessionId}", async (string sessionId, TinadecCore.Runtime.ProjectSessionLifecycleService lifecycle, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try
            {
                await lifecycle.PurgeSessionAsync(id, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "SESSION_NOT_FOUND" }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { code = "invalid_lifecycle_transition", message = ex.Message }); }
            catch (TinadecCore.Runtime.ActiveRunConflictException ex) { return Results.Conflict(new { code = "active_run_conflict", message = ex.Message, run_id = ex.RunId }); }
        });

        app.MapGet("/api/v1/sessions/{sessionId}/messages", async (string sessionId, ProjectSessionStore store, IMessageAttachmentStore attachments, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try
            {
                var messages = await store.ListMessagesAsync(id, ct).ConfigureAwait(false);
                // One listing for the whole page, grouped in memory: per-message reads would
                // be one query per row on the route the chat hits every time it opens a
                // session, and the store is already scoped to this tenant and workspace.
                var claimed = (await attachments.ListAsync(id, ct).ConfigureAwait(false))
                    .Where(row => row.MessageId is not null)
                    .GroupBy(row => row.MessageId!.Value)
                    .ToDictionary(group => group.Key, group => group.ToArray());
                return Results.Ok(messages.Select(message => ToMessage(message,
                    claimed.TryGetValue(message.Id, out var rows) ? rows : Array.Empty<StoredAttachment>())).ToArray());
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "SESSION_NOT_FOUND" }); }
        });

        app.MapPost("/api/v1/sessions/{sessionId}/messages", async (string sessionId, CreateMessageRequest request, ProjectSessionStore store, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try { return Results.Created($"/api/v1/sessions/{id}/messages", ToMessage(await store.AddMessageAsync(id, request.Content, "user", null, ct).ConfigureAwait(false))); }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "SESSION_NOT_FOUND" }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_MESSAGE", message = ex.Message }); }
        });

        // "Edit and resend": cut the conversation at one of its own messages so the
        // corrected turn can be sent in its place. Rows stay durable — a run's trigger
        // message, its checkpoint and the context snapshots all reference message ids —
        // they simply stop being history, for the message list and the model alike.
        app.MapPost("/api/v1/sessions/{sessionId}/messages/{messageId}/revert", async (string sessionId, string messageId, TinadecCore.Runtime.ProjectSessionLifecycleService lifecycle, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var session)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            if (!Guid.TryParse(messageId, out var message)) return Results.BadRequest(new { code = "INVALID_MESSAGE_ID" });
            try
            {
                var reverted = await lifecycle.RevertSessionHistoryAsync(session, message, ct).ConfigureAwait(false);
                return Results.Ok(new
                {
                    from_message_id = reverted.FromMessageId,
                    from_sequence = reverted.FromSequence,
                    removed_count = reverted.RemovedCount,
                    history_revision = reverted.HistoryRevision
                });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { code = "revert_target_not_found", message = ex.Message }); }
            catch (TinadecCore.Runtime.ActiveRunConflictException ex) { return Results.Conflict(new { code = "active_run_conflict", message = ex.Message, run_id = ex.RunId }); }
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

    private static void MapProjectLifecycleEndpoints(
        IEndpointRouteBuilder app,
        string action,
        Func<TinadecCore.Runtime.ProjectSessionLifecycleService, Func<Guid, CancellationToken, Task<TinadecCore.Memory.ProjectRecord>>> selector)
    {
        app.MapPost($"/api/v1/projects/{{projectId}}/{action}", async (string projectId, TinadecCore.Runtime.ProjectSessionLifecycleService lifecycle, CancellationToken ct) =>
        {
            if (!Guid.TryParse(projectId, out var id)) return Results.BadRequest(new { code = "INVALID_PROJECT_ID" });
            try
            {
                await selector(lifecycle)(id, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "PROJECT_NOT_FOUND" }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { code = "invalid_lifecycle_transition", message = ex.Message }); }
            catch (TinadecCore.Runtime.ActiveRunConflictException ex) { return Results.Conflict(new { code = "active_run_conflict", message = ex.Message, run_id = ex.RunId }); }
        });
    }

    private static void MapSessionLifecycleEndpoints(
        IEndpointRouteBuilder app,
        string action,
        Func<TinadecCore.Runtime.ProjectSessionLifecycleService, Func<Guid, CancellationToken, Task<TinadecCore.Memory.SessionRecord>>> selector)
    {
        app.MapPost($"/api/v1/sessions/{{sessionId}}/{action}", async (string sessionId, TinadecCore.Runtime.ProjectSessionLifecycleService lifecycle, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var id)) return Results.BadRequest(new { code = "INVALID_SESSION_ID" });
            try
            {
                await selector(lifecycle)(id, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { code = "SESSION_NOT_FOUND" }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { code = "invalid_lifecycle_transition", message = ex.Message }); }
            catch (TinadecCore.Runtime.ActiveRunConflictException ex) { return Results.Conflict(new { code = "active_run_conflict", message = ex.Message, run_id = ex.RunId }); }
        });
    }

    private static IResult AgentModeNotConfigured(string message) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "agent_mode_not_configured", detail: message,
        extensions: new Dictionary<string, object?> { ["code"] = "agent_mode_not_configured", ["message"] = message });

    /// <summary>
    /// Projection for a registered workspace that could not mount. Mirrors ToProject so a client
    /// needs one row shape, and names the reason on the error contract instead of leaking a raw
    /// exception string that has no category and no recovery path.
    /// </summary>
    private static object UnavailableProject(StorageScopeDescriptor scope, StorageScopeRowError error) => new
    {
        id = scope.ProjectId, storage_id = scope.StorageId, name = Path.GetFileName(scope.ProjectRoot), path = scope.ProjectRoot,
        // A mount failure cannot tell us the persisted lifecycle. Never invent an active record.
        kind = "local", lifecycle_status = (string?)null, created_at = DateTimeOffset.UnixEpoch, updated_at = DateTimeOffset.UnixEpoch,
        storage_root = scope.Root, external = scope.External,
        availability = "error", availability_error = error.Message, availability_code = error.Code,
        category = error.Classification.Category, retryable = error.Classification.Retryable, actions = error.Classification.Actions,
        trace_id = error.TraceId, diagnostics = error.Diagnostics,
    };

    private static object ToProject(ProjectRecord project, string? storageId = null, WorkspaceDefinition? workspace = null, StorageScopeDescriptor? scope = null) => new {
        id = project.Id, storage_id = storageId, name = workspace?.Name ?? project.Name, path = workspace?.PrimaryPath ?? project.RootPath,
        roots = workspace?.Roots, primary_root_id = workspace?.PrimaryRootId, icon = workspace?.Icon ?? "folder", color = workspace?.Color ?? "default",
        configuration_hash = workspace?.ContentHash, storage_root = scope?.Root, external = scope?.External, availability = "ready",
        kind = project.Kind, created_at = project.CreatedAt, updated_at = project.UpdatedAt, lifecycle_status = project.LifecycleStatus, trashed_at = project.TrashedAt };
    private static object ToSession(SessionRecord session) => new
    {
        id = session.Id, project_id = session.ProjectId, title = session.Title, status = session.Status,
        mode_version_id = session.ModeVersionId,
        view_mode = session.ViewMode ?? "flat",
        permission_mode = session.PermissionMode ?? "default", space_options = ToSpaceOptionsDto(ProjectSessionStore.ReadSpaceOptions(session)), settings_revision = session.SettingsRevision,
        meeting_model_override = ToMeetingModelOverride(session), summary = session.Summary,
        history_revision = session.HistoryRevision, created_at = session.CreatedAt, updated_at = session.UpdatedAt,
        lifecycle_status = session.LifecycleStatus, trashed_at = session.TrashedAt
    };

    /// <summary>
    /// The workspace default mode is one row per workspace. Reading it inside the
    /// per-session projection created one DbContext and one query per session
    /// (9 sessions in the measured project read the same single row 9 times).
    /// Callers with a batch read it once and pass the map in; the single-session
    /// paths keep the lazy fallback so a lone read still works.
    /// </summary>
    private static async Task<Dictionary<(Guid TenantId, Guid WorkspaceId), Guid?>> LoadSessionModeDefaultsAsync(
        IReadOnlyCollection<SessionRecord> sessions, IDbContextFactory<AgentConfigurationDbContext> cfgFactory,
        GlobalAgentConfiguration globalConfig, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, Guid), Guid?>();
        var tenantIds = sessions.Select(x => x.TenantId).Distinct().ToArray();
        var workspaceIds = sessions.Select(x => x.WorkspaceId).Distinct().ToArray();
        if (tenantIds.Length == 0) return result;
        try
        {
            if (!ReferenceEquals(cfgFactory, globalConfig.Factory))
            {
                await using var user = await globalConfig.Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var globalRows = await (from defaults in user.WorkspaceDefaults.AsNoTracking()
                                        join version in user.ModeVersions.AsNoTracking() on defaults.DefaultModeVersionId equals version.Id
                                        where tenantIds.Contains(defaults.TenantId) && workspaceIds.Contains(defaults.WorkspaceId)
                                            && defaults.Status == "active" && defaults.ArchivedAt == null
                                            && version.Status == "published"
                                        select new { defaults.TenantId, defaults.WorkspaceId, VersionId = (Guid?)version.Id })
                    .ToListAsync(ct).ConfigureAwait(false);
                foreach (var row in globalRows)
                    if (row.VersionId is not null) result[(row.TenantId, row.WorkspaceId)] = row.VersionId;
            }
            await using var cfg = await cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await (from defaults in cfg.WorkspaceDefaults.AsNoTracking()
                              join version in cfg.ModeVersions.AsNoTracking() on defaults.DefaultModeVersionId equals version.Id
                              where tenantIds.Contains(defaults.TenantId) && workspaceIds.Contains(defaults.WorkspaceId)
                                  && defaults.Status == "active" && defaults.ArchivedAt == null
                                  && version.Status == "published"
                              select new { defaults.TenantId, defaults.WorkspaceId, VersionId = (Guid?)version.Id })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
                if (row.VersionId is not null && !result.ContainsKey((row.TenantId, row.WorkspaceId)))
                    result[(row.TenantId, row.WorkspaceId)] = row.VersionId;
        }
        catch { }
        return result;
    }

    private static async Task<object> ToSessionEnrichedAsync(SessionRecord session, IDbContextFactory<AgentConfigurationDbContext> cfgFactory,
        GlobalAgentConfiguration globalConfig, CancellationToken ct, string? storageId = null)
        => ToSessionEnriched(session, storageId, await LoadSessionModeDefaultsAsync([session], cfgFactory, globalConfig, ct).ConfigureAwait(false));

    private static object ToSessionEnriched(
        SessionRecord session,
        string? storageId,
        Dictionary<(Guid TenantId, Guid WorkspaceId), Guid?> defaults)
    {
        bool hasUpdate = false;
        Guid? latestModeVersionId = null;
        if (defaults.TryGetValue((session.TenantId, session.WorkspaceId), out var defaultModeVersionId)
            && defaultModeVersionId is { } resolvedDefault)
        {
            latestModeVersionId = resolvedDefault;
            hasUpdate = session.ModeVersionId != resolvedDefault;
        }
        return new { id = session.Id, storage_id = storageId, project_id = session.ProjectId, title = session.Title, status = session.Status, view_mode = session.ViewMode ?? "flat", mode_version_id = session.ModeVersionId, conversation_node_key = session.ConversationNodeKey, conversation_template_slug = session.ConversationTemplateSlug, meeting_model_override = ToMeetingModelOverride(session), permission_mode = session.PermissionMode ?? "default", space_options = ToSpaceOptionsDto(ProjectSessionStore.ReadSpaceOptions(session)), settings_revision = session.SettingsRevision, has_update = hasUpdate, latest_mode_version_id = latestModeVersionId, summary = session.Summary, history_revision = session.HistoryRevision, created_at = session.CreatedAt, updated_at = session.UpdatedAt, lifecycle_status = session.LifecycleStatus, trashed_at = session.TrashedAt };
    }

    internal static SpaceRunOptions? ToSpaceOptions(SpaceRunOptionsDto? options) => options is null ? null
        : new(options.PlanFirst, options.SpecEnabled, options.MultiAgent, options.WorkflowModeVersionId, options.BulletinBoard, options.Worktree);

    internal static SpaceRunOptionsDto? ToSpaceOptionsDto(SpaceRunOptions? options) => options is null ? null
        : new(options.PlanFirst, options.SpecEnabled, options.MultiAgent, options.WorkflowModeVersionId, options.BulletinBoard, options.Worktree);

    internal static Task ValidateWorkflowAsync(SpaceRunOptions? options, Guid tenantId, Guid workspaceId,
        IDbContextFactory<AgentConfigurationDbContext> cfgFactory, CancellationToken ct) =>
        ValidateWorkflowAsync(options, tenantId, workspaceId, cfgFactory, null, ct);

    internal static async Task ValidateWorkflowAsync(SpaceRunOptions? options, Guid tenantId, Guid workspaceId,
        IDbContextFactory<AgentConfigurationDbContext> cfgFactory, GlobalAgentConfiguration? globalConfig, CancellationToken ct)
    {
        if (options?.WorkflowModeVersionId is not { } versionId) return;
        var published = globalConfig is null
            ? await HasPublishedModeLocallyAsync(cfgFactory, tenantId, workspaceId, versionId, ct).ConfigureAwait(false)
            : await AgentConfigurationLookup.HasPublishedModeAsync(cfgFactory, globalConfig, tenantId, workspaceId, versionId, ct).ConfigureAwait(false);
        if (!published) throw new ArgumentException("workflow_mode_version_id must reference a published mode in this workspace.");
    }

    private static async Task<bool> HasPublishedModeLocallyAsync(IDbContextFactory<AgentConfigurationDbContext> cfgFactory,
        Guid tenantId, Guid workspaceId, Guid versionId, CancellationToken ct)
    {
        await using var cfg = await cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await cfg.ModeVersions.AsNoTracking().AnyAsync(x => x.Id == versionId
            && x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.Status == "published", ct).ConfigureAwait(false);
    }

    private static object ToTransferReceipt(TinadecCore.Runtime.SessionScopeTransferReceipt receipt) => new
    {
        transfer_id = receipt.TransferId, session_id = receipt.SessionId, source_storage_id = receipt.SourceStorageId,
        storage_id = receipt.TargetStorageId, project_id = receipt.ProjectId, status = receipt.Status,
        created_at = receipt.CreatedAt, updated_at = receipt.UpdatedAt, error = receipt.Error
    };

    private static object? ToMeetingModelOverride(SessionRecord session) =>
        session.MeetingModelOverrideProviderInstanceId is { } providerInstanceId
            ? new { provider_instance_id = providerInstanceId, model = session.MeetingModelOverrideModel }
            : null;
    /// <summary>
    /// attachments rides with the message so a reopened transcript shows what the user sent
    /// without a second round-trip per row. content_reference deliberately does not: it is a
    /// path under the data root, and the download route addresses bytes by attachment id.
    /// </summary>
    private static object ToMessage(StoredMessage message, IReadOnlyList<StoredAttachment>? attachments = null) => new
    {
        id = message.Id,
        session_id = message.SessionId,
        run_id = message.RunId,
        role = message.Role,
        content = message.Content,
        created_at = message.CreatedAt,
        attachments = (attachments ?? Array.Empty<StoredAttachment>()).Select(attachment => new
        {
            id = attachment.Id,
            file_name = attachment.FileName,
            media_type = attachment.MediaType,
            content_length = attachment.ContentLength,
            content_hash = attachment.ContentHash,
            created_at = attachment.CreatedAt,
            bound_at = attachment.BoundAt
        }).ToArray()
    };
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
