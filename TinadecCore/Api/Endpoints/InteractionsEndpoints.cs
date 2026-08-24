using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;

namespace TinadecCore.Api.Endpoints;

public static class InteractionsEndpoints
{
    public static WebApplication MapInteractionsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions", CreateInteraction);
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions/{interactionId:guid}/reassign", ReassignInteraction);
        app.MapPost("/api/v1/sessions/{sessionId:guid}/interactions/{interactionId:guid}/cancel", CancelInteraction);
        app.MapGet("/api/v1/sessions/{sessionId:guid}/interactions", ListInteractions);
        return app;
    }

    static async Task<IResult> CreateInteraction(Guid sessionId, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, ITenantContextAccessor tenant, ProjectSessionStore sessions, IConversationStore conversations, IFullDuplexRunCoordinator coordinator, StorageLifecycleService lifecycle, CancellationToken ct)
    {
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        var content = el.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(content)) return Results.BadRequest(new { code = "invalid_request", message = "content is required" });
        var clientMessageId = el.TryGetProperty("client_message_id", out var cm) ? cm.GetString() : Guid.NewGuid().ToString("N");
        var modeVersionId = el.TryGetProperty("mode_version_id", out var mv) && Guid.TryParse(mv.GetString(), out var g) ? g : (Guid?)null;
        var dispatchMode = el.TryGetProperty("dispatch_mode", out var dm) ? dm.GetString()?.Trim().ToLowerInvariant() : "queued";
        if (dispatchMode is not ("queued" or "insert" or "parallel")) return Results.BadRequest(new { code = "invalid_request", message = "dispatch_mode must be queued|insert|parallel" });
        Guid? targetRunId = null;
        if (el.TryGetProperty("target_run_id", out var tr) && Guid.TryParse(tr.GetString(), out var tg)) targetRunId = tg;
        if (dispatchMode == "insert" && targetRunId is null) return Results.BadRequest(new { code = "invalid_request", message = "insert requires target_run_id" });
        var meetingModel = el.TryGetProperty("meeting_model", out var mm) ? mm.GetString() : null;
        var expectedRev = el.TryGetProperty("expected_context_revision", out var er) && er.TryGetInt64(out var rv) ? rv : (long?)null;

        // session existence + session's default mode handling
        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null) return Results.NotFound(new { code = "not_found", message = "Session not found" });

        // mode_version validation if provided
        if (modeVersionId.HasValue)
        {
            await using var cfg = await cfgFactory.CreateDbContextAsync(ct);
            var mvRec = await cfg.ModeVersions.FirstOrDefaultAsync(x => x.Id == modeVersionId.Value, ct);
            if (mvRec is null) return Results.BadRequest(new { code = "invalid_request", message = "mode_version_id not found" });
        }

        // model strategy resolution (inherit/fixed/parent_select) with 2 retries
        string? resolvedMeetingModel = meetingModel;
        string? modelSelectionLog = null;
        if (modeVersionId.HasValue)
        {
            try { (resolvedMeetingModel, modelSelectionLog) = await ResolveMeetingModelAsync(modeVersionId.Value, meetingModel, cfgFactory, req.HttpContext.RequestServices, ct).ConfigureAwait(false); } catch (Exception ex) { modelSelectionLog = ex.Message; }
        }

        // Persist the chosen mode_version onto the session so the run engine's roster resolver
        // reads the same relational mode the center edits — not a per-call event hint.
        if (modeVersionId.HasValue)
        {
            try { await sessions.UpdateSessionModeAsync(sessionId, modeVersionId.Value, resolvedMeetingModel, null, ct).ConfigureAwait(false); } catch { }
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
            await lifecycle.AppendEventAsync(targetRunId.Value, "interaction.steering", new { interaction_id = Guid.NewGuid(), target_run_id = targetRunId.Value, content, meeting_model = resolvedMeetingModel, dispatch_mode = dispatchMode }, $"Steering injected into run {targetRunId}", "info", cancellationToken: ct);
            await lifecycle.AppendRunStreamAsync(targetRunId.Value, new DurableRunStreamAppend(Guid.NewGuid(), "steering", null, IdempotencyKey: $"run:{targetRunId}:steering:{Guid.NewGuid():N}"), ct);
            var engine = (IFullDuplexRunEngine)req.HttpContext.RequestServices.GetRequiredService(typeof(IFullDuplexRunEngine));
            await engine.EnqueueAsync(targetRunId.Value, ct);
            return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{targetRunId.Value}", new { interaction_id = Guid.NewGuid(), session_id = sessionId, run_id = targetRunId.Value, dispatch_mode = dispatchMode, status = "steering_injected", content, resolved_meeting_model = resolvedMeetingModel });
        }

        // queued / parallel: normal admission via coordinator
        RunSubmission? admission = null;
        string admissionStatus = "queued";
        try
        {
            admission = await coordinator.SubmitAsync(new FullDuplexInvocation(sessionId, content, clientMessageId!, "space", "agent", "default", targetRunId, expectedRev), ct);
            admissionStatus = dispatchMode == "parallel" ? "assigned" : "queued";
        }
        catch (RunAdmissionException ex) when (ex.Code == "ACTIVE_RUN_LIMIT" && dispatchMode == "queued")
        {
            // meeting busy -> keep queued without run
            var queuedId = Guid.NewGuid();
            // ponytail: no run yet, keep as transient queued interaction without durable event
            return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{queuedId}", new { interaction_id = queuedId, session_id = sessionId, run_id = (Guid?)null, dispatch_mode = dispatchMode, status = "queued", mode_version_id = modeVersionId, meeting_model = resolvedMeetingModel, reason = ex.Message });
        }
        catch (RunAdmissionException ex) when (ex.Code == "CONTEXT_REVISION_CONFLICT")
        {
            return Results.Conflict(new { code = "context_conflict", message = ex.Message });
        }
        if (admission is null) return Results.Conflict(new { code = "conflict", message = "Admission failed" });
        // if mode_version provided, store it as frozen binding hint (best-effort)
        if (modeVersionId.HasValue)
        {
            try { await lifecycle.AppendEventAsync(admission.RunId, "interaction.created", new { interaction_id = admission.TurnId, mode_version_id = modeVersionId.Value, dispatch_mode = dispatchMode, meeting_model = resolvedMeetingModel, model_selection_log = modelSelectionLog }, $"Interaction {dispatchMode} with mode_version {modeVersionId}", "info", cancellationToken: ct); } catch { }
            try { await lifecycle.AppendRunStreamAsync(admission.RunId, new DurableRunStreamAppend(admission.TurnId, dispatchMode == "parallel" ? "assigned" : "queued", admission.MessageId, IdempotencyKey: $"run:{admission.RunId}:turn:{admission.TurnId}:{dispatchMode}"), ct); } catch { }
            if (modelSelectionLog is not null)
                try { await lifecycle.AppendRunStreamAsync(admission.RunId, new DurableRunStreamAppend(admission.TurnId, "model_selection", admission.MessageId, IdempotencyKey: $"run:{admission.RunId}:model_sel:{admission.TurnId}"), ct); } catch { }
        }
        var status = admissionStatus;
        return Results.Created($"/api/v1/sessions/{sessionId}/interactions/{admission.TurnId}", new { interaction_id = admission.TurnId, session_id = sessionId, run_id = admission.RunId, turn_id = admission.TurnId, dispatch_mode = dispatchMode, status, mode_version_id = modeVersionId, meeting_model = resolvedMeetingModel, model_selection_log = modelSelectionLog });
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

    static async Task<(string? resolved, string? log)> ResolveMeetingModelAsync(Guid modeVersionId, string? requested, IDbContextFactory<AgentConfigurationDbContext> cfgFactory, IServiceProvider sp, CancellationToken ct)
    {
        await using var cfg = await cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var mv = await cfg.ModeVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == modeVersionId, ct).ConfigureAwait(false);
        if (mv is null || string.IsNullOrWhiteSpace(mv.SnapshotJson)) return (requested, null);
        // find meeting node (operation)
        JsonElement snap;
        try { snap = JsonDocument.Parse(mv.SnapshotJson).RootElement; } catch { return (requested, null); }
        Guid? meetingAgentId = null;
        if (snap.TryGetProperty("nodes", out var nodes) && nodes.ValueKind==JsonValueKind.Array)
        {
            foreach(var n in nodes.EnumerateArray()){
                var layer = n.TryGetProperty("Layer", out var l) ? l.GetString() : n.TryGetProperty("layer", out var ll) ? ll.GetString(): null;
                if(string.Equals(layer,"operation",StringComparison.OrdinalIgnoreCase)){
                    if(n.TryGetProperty("AgentDefinitionId", out var aid) && Guid.TryParse(aid.GetString(), out var g)) meetingAgentId=g;
                    else if(n.TryGetProperty("agentDefinitionId", out var aid2) && Guid.TryParse(aid2.GetString(), out var g2)) meetingAgentId=g2;
                    break;
                }
            }
        }
        if(meetingAgentId is null) return (requested, null);
        var agent = await cfg.AgentDefinitions.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==meetingAgentId.Value, ct).ConfigureAwait(false);
        if(agent is null || string.IsNullOrWhiteSpace(agent.ModelStrategyJson)) return (requested, null);
        JsonElement strat;
        try{ strat = JsonDocument.Parse(agent.ModelStrategyJson).RootElement; } catch{ return (requested, null);}
        var kind = strat.TryGetProperty("kind", out var k) ? k.GetString()?.Trim().ToLowerInvariant() : strat.TryGetProperty("selection_kind", out var sk) ? sk.GetString()?.Trim().ToLowerInvariant(): "inherit";
        if(kind=="fixed"){
            var prov = strat.TryGetProperty("provider_instance_id", out var p) ? p.GetString() : null;
            var model = strat.TryGetProperty("model", out var m) ? m.GetString() : strat.TryGetProperty("model_id", out var mi) ? mi.GetString(): null;
            // fixed ignores requested
            return (model, $"fixed:{prov}:{model}");
        }
        if(kind=="parent_select"){
            // select from workspace enabled models with up to 2 retries
            try{
                var modelFactory = sp.GetService(typeof(IDbContextFactory<TinadecCore.Models.ModelControlDbContext>)) as IDbContextFactory<TinadecCore.Models.ModelControlDbContext>;
                if(modelFactory is not null){
                    await using var mdb = await modelFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                    var candidates = await mdb.Providers.AsNoTracking().Where(p=>p.Enabled && p.DeletedAt==null).ToListAsync(ct).ConfigureAwait(false);
                    // simple: pick first with retry
                    for(int attempt=0; attempt<3; attempt++){
                        var cand = candidates.ElementAtOrDefault(attempt);
                        if(cand is null) break;
                        // check if provider has model (via versions)
                        var ver = await mdb.ProviderVersions.AsNoTracking().Where(v=>v.ProviderId==cand.Id).OrderByDescending(v=>v.Version).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                        if(ver is not null) return (ver.ContentReference, $"parent_select attempt {attempt+1}:{cand.Id}");
                    }
                    return (requested, "parent_select: no enabled provider, fallback to requested");
                }
            }catch(Exception ex){ return (requested, $"parent_select failed:{ex.Message}"); }
            return (requested, "parent_select: no provider factory");
        }
        // inherit
        return (requested, "inherit");
    }
}
