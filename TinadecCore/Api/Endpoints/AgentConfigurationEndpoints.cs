using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Api.Endpoints;

public static class AgentConfigurationEndpoints
{
    public static WebApplication MapAgentConfigurationEndpoints(this WebApplication app)
    {
        // Agents
        app.MapGet("/api/v1/agents", ListAgents);
        app.MapPost("/api/v1/agents", CreateAgent);
        app.MapGet("/api/v1/agents/{id:guid}", GetAgent);
        app.MapPut("/api/v1/agents/{id:guid}/draft", UpdateAgentDraft);
        app.MapPost("/api/v1/agents/{id:guid}/publish", PublishAgent);
        app.MapPost("/api/v1/agents/{id:guid}/archive", ArchiveAgent);
        app.MapGet("/api/v1/agents/{id:guid}/versions", ListAgentVersions);
        app.MapGet("/api/v1/agents/{id:guid}/versions/{versionId:guid}", GetAgentVersion);

        // Agent Modes
        app.MapGet("/api/v1/agent-modes", ListModes);
        app.MapPost("/api/v1/agent-modes", CreateMode);
        app.MapGet("/api/v1/agent-modes/{id:guid}", GetMode);
        app.MapPut("/api/v1/agent-modes/{id:guid}/draft", UpdateModeDraft);
        app.MapPost("/api/v1/agent-modes/{id:guid}/publish", PublishMode);
        app.MapPost("/api/v1/agent-modes/{id:guid}/archive", ArchiveMode);
        app.MapGet("/api/v1/agent-modes/{id:guid}/versions", ListModeVersions);
        app.MapGet("/api/v1/agent-modes/{id:guid}/versions/{versionId:guid}", GetModeVersion);

        // Prompt Pipelines
        app.MapGet("/api/v1/prompt-pipelines", ListPipelines);
        app.MapPost("/api/v1/prompt-pipelines", CreatePipeline);
        app.MapGet("/api/v1/prompt-pipelines/{id:guid}", GetPipeline);
        app.MapPut("/api/v1/prompt-pipelines/{id:guid}/draft", UpdatePipelineDraft);
        app.MapPost("/api/v1/prompt-pipelines/{id:guid}/publish", PublishPipeline);
        app.MapPost("/api/v1/prompt-pipelines/{id:guid}/archive", ArchivePipeline);
        app.MapGet("/api/v1/prompt-pipelines/{id:guid}/versions", ListPipelineVersions);
        app.MapGet("/api/v1/prompt-pipelines/{id:guid}/versions/{versionId:guid}", GetPipelineVersion);

        // Candidates & Instances (read-only thin wrappers)
        app.MapGet("/api/v1/agent-candidates", ListCandidates);
        app.MapPost("/api/v1/agent-candidates/{id:guid}/promote", PromoteCandidate);
        app.MapPost("/api/v1/agent-candidates/{id:guid}/reject", RejectCandidate);
        app.MapGet("/api/v1/agent-runtime-instances", ListInstances);

        return app;
    }

    // ── helpers ──
    static (Guid tenant, Guid workspace, Guid principal) Ctx(ITenantContextAccessor a) => (a.Current.TenantId, a.Current.WorkspaceId, a.Current.PrincipalId);
    static long IfMatch(HttpRequest r) => long.TryParse(r.Headers.IfMatch.FirstOrDefault()?.Trim('\"', 'W', '/', ' '), out var v) ? v : -1;
    static IResult ETag(long rev) => Results.Ok(); // placeholder
    static string Slug(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim().ToLowerInvariant().Replace(' ', '-');

    // ── agents ──
    static async Task<IResult> ListAgents(IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, HttpRequest req, CancellationToken ct)
    {
        var (t, w, _) = Ctx(a);
        var q = req.Query["status"].ToString();
        await using var db = await f.CreateDbContextAsync(ct);
        var list = await db.AgentDefinitions.Where(x => x.TenantId == t && x.WorkspaceId == w && (string.IsNullOrEmpty(q) || x.Status == q)).OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        return Results.Ok(list.Select(ToAgentDto));
    }
    static async Task<IResult> CreateAgent(HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        var slug = el.TryGetProperty("slug", out var s) ? s.GetString() : null;
        var name = el.TryGetProperty("display_name", out var n) ? n.GetString() : el.TryGetProperty("name", out var n2) ? n2.GetString() : null;
        var layer = el.TryGetProperty("layer", out var l) ? l.GetString() : "operation";
        AgentConfigurationService.ValidateLayer(layer!);
        if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { code = "invalid_request", message = "display_name is required" });
        // model_strategy validation
        if (el.TryGetProperty("model_strategy", out var msEl))
        {
            var err = ValidateModelStrategy(msEl.GetRawText());
            if (err is not null) return Results.BadRequest(new { code = "invalid_request", message = err });
        }
        var (t, w, p) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var rec = new AgentDefinitionRecord
        {
            Id = Guid.NewGuid(), TenantId = t, WorkspaceId = w, Slug = Slug(slug, name!), DisplayName = name!, Layer = layer!.Trim().ToLowerInvariant(),
            Role = el.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "",
            CapabilitiesJson = el.TryGetProperty("capabilities", out var c) ? c.GetRawText() : "[]",
            ModelStrategyJson = el.TryGetProperty("model_strategy", out var ms) ? ms.GetRawText() : el.TryGetProperty("model_strategy_json", out var ms2) ? ms2.GetRawText() : null,
            ToolScopeJson = el.TryGetProperty("tool_scope", out var ts) ? ts.GetRawText() : el.TryGetProperty("allowed_tools", out var at) ? at.GetRawText() : "[]",
            Status = "draft", Revision = 1, Version = 0, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, CreatedByPrincipalId = p, UpdatedByPrincipalId = p
        };
        // single draft invariant via filtered index; catch duplicate
        try { db.AgentDefinitions.Add(rec); await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Results.Conflict(new { code = "conflict", message = "Draft with same slug already exists" }); }
        return Results.Created($"/api/v1/agents/{rec.Id}", ToAgentDto(rec));
    }
    static async Task<IResult> GetAgent(Guid id, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t, w, _) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var rec = await db.AgentDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == t && x.WorkspaceId == w, ct);
        return rec is null ? Results.NotFound(new { code = "not_found", message = "Agent not found" }) : Results.Ok(ToAgentDto(rec));
    }
    static async Task<IResult> UpdateAgentDraft(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        var exp = IfMatch(req);
        var (t, w, p) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var rec = await db.AgentDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == t && x.WorkspaceId == w, ct);
        if (rec is null) return Results.NotFound(new { code = "not_found" });
        if (rec.Status == "archived") return Results.Conflict(new { code = "conflict", message = "Archived agent cannot be edited" });
        if (exp >= 0 && rec.Revision != exp) return Results.Json(new { code = "conflict", message = "Revision conflict" }, statusCode: 412);
        if (el.TryGetProperty("layer", out var l)) { AgentConfigurationService.ValidateLayer(l.GetString()!); rec.Layer = l.GetString()!.Trim().ToLowerInvariant(); }
        if (el.TryGetProperty("display_name", out var n)) rec.DisplayName = n.GetString() ?? rec.DisplayName;
        if (el.TryGetProperty("name", out var n2) && string.IsNullOrWhiteSpace(rec.DisplayName)) rec.DisplayName = n2.GetString() ?? rec.DisplayName;
        if (el.TryGetProperty("role", out var r)) rec.Role = r.GetString() ?? rec.Role;
        if (el.TryGetProperty("capabilities", out var c)) rec.CapabilitiesJson = c.GetRawText();
        if (el.TryGetProperty("model_strategy", out var ms)) { var err = ValidateModelStrategy(ms.GetRawText()); if (err is not null) return Results.BadRequest(new { code = "invalid_request", message = err }); rec.ModelStrategyJson = ms.GetRawText(); }
        if (el.TryGetProperty("tool_scope", out var ts)) rec.ToolScopeJson = ts.GetRawText();
        if (el.TryGetProperty("allowed_tools", out var at)) rec.ToolScopeJson = at.GetRawText();
        rec.Revision++; rec.UpdatedAt = DateTimeOffset.UtcNow; rec.UpdatedByPrincipalId = p; rec.Status = "draft";
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Results.Json(new { code = "conflict", message = "Concurrent update" }, statusCode: 412); }
        return Results.Ok(ToAgentDto(rec));
    }
    static async Task<IResult> PublishAgent(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var exp = IfMatch(req);
        var (t, w, p) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var rec = await db.AgentDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == t && x.WorkspaceId == w, ct);
        if (rec is null) return Results.NotFound(new { code = "not_found" });
        if (exp >= 0 && rec.Revision != exp) return Results.Json(new { code = "conflict", message = "Revision conflict" }, statusCode: 412);
        if (rec.Status == "archived") return Results.Conflict(new { code = "conflict", message = "Archived" });
        // minimal validation
        AgentConfigurationService.ValidateLayer(rec.Layer);
        if (string.IsNullOrWhiteSpace(rec.DisplayName)) return Results.BadRequest(new { code = "invalid_request", message = "display_name required" });
        if (rec.ModelStrategyJson is not null)
        {
            var err = ValidateModelStrategy(rec.ModelStrategyJson);
            if (err is not null) return Results.BadRequest(new { code = "invalid_request", message = err });
        }
        var ver = (await db.AgentVersions.Where(x => x.AgentDefinitionId == id).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
        var snap = JsonSerializer.Serialize(ToAgentDto(rec), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var vrec = new AgentVersionRecord { Id = Guid.NewGuid(), TenantId = t, WorkspaceId = w, AgentDefinitionId = id, Version = ver, Layer = rec.Layer, Role = rec.Role, SnapshotJson = snap, ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snap))).ToLowerInvariant(), ContentLength = snap.Length, Status = "published", Revision = 1, CreatedAt = DateTimeOffset.UtcNow, CreatedByPrincipalId = p };
        db.AgentVersions.Add(vrec);
        rec.Version = ver; rec.Revision++; rec.UpdatedAt = DateTimeOffset.UtcNow; rec.Status = "published";
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = rec.Id, version = ver, revision = rec.Revision, snapshot = ToAgentDto(rec) });
    }
    static async Task<IResult> ArchiveAgent(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var exp = IfMatch(req);
        var (t, w, _) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var rec = await db.AgentDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == t && x.WorkspaceId == w, ct);
        if (rec is null) return Results.NotFound(new { code = "not_found" });
        if (exp >= 0 && rec.Revision != exp) return Results.Json(new { code = "conflict", message = "Revision conflict" }, statusCode: 412);
        // block if referenced by published mode
        var referenced = await db.ModeNodes.AnyAsync(x => x.AgentDefinitionId == id && x.TenantId == t && x.WorkspaceId == w, ct);
        if (referenced) return Results.Conflict(new { code = "conflict", message = "Agent is referenced by a mode; publish a new mode version first" });
        rec.Status = "archived"; rec.ArchivedAt = DateTimeOffset.UtcNow; rec.Revision++; rec.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToAgentDto(rec));
    }
    static async Task<IResult> ListAgentVersions(Guid id, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t, w, _) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var list = await db.AgentVersions.Where(x => x.AgentDefinitionId == id && x.TenantId == t && x.WorkspaceId == w).OrderBy(x => x.Version).ToListAsync(ct);
        return Results.Ok(list.Select(v => new { id = v.Id, agent_definition_id = v.AgentDefinitionId, version = v.Version, snapshot = JsonSerializer.Deserialize<JsonElement>(v.SnapshotJson), content_hash = v.ContentHash, created_at = v.CreatedAt }));
    }
    static async Task<IResult> GetAgentVersion(Guid id, Guid versionId, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t,w,_) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var v = await db.AgentVersions.FirstOrDefaultAsync(x => x.Id == versionId && x.AgentDefinitionId==id && x.TenantId==t && x.WorkspaceId==w, ct);
        return v is null ? Results.NotFound(new { code="not_found"}) : Results.Ok(new { id=v.Id, agent_definition_id=v.AgentDefinitionId, version=v.Version, snapshot=JsonSerializer.Deserialize<JsonElement>(v.SnapshotJson), content_hash=v.ContentHash, created_at=v.CreatedAt});
    }

    // ── modes ──
    static async Task<IResult> ListModes(IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, HttpRequest req, TinadecCore.DmaEA.IAgentRuntimeConfiguration toml, CancellationToken ct)
    {
        // TOML compatibility: if application_mode query present, return TOML-driven modes (old FullDuplex test expects this)
        if (req.Query.ContainsKey("application_mode") || req.Query.ContainsKey("applicationMode"))
        {
            var appMode = req.Query["application_mode"].ToString();
            if (string.IsNullOrWhiteSpace(appMode)) appMode = req.Query["applicationMode"].ToString();
            var snapshot = toml.Current;
            var appId = TinadecCore.DmaEA.AgentRuntimeConfigurationSnapshot.NormalizeApplicationMode(appMode);
            if (!snapshot.ApplicationModes.TryGetValue(appId, out var mode))
                return Results.BadRequest(new { code = "UNKNOWN_APPLICATION_MODE", message = $"Application mode '{appId}' is not configured." });
            return Results.Ok(mode.AllowedAgentModes.Select(id => new
            {
                id,
                display_name = id switch { "plan" => "Plan", "spec" => "Spec", "ask" => "Ask", "vibe" => "Vibe", "auto" => "Auto", "agent" => "Agent", _ => id },
                summary = $"Agent mode '{id}' in application mode '{appId}'",
                application_mode = appId,
                is_default = string.Equals(id, mode.DefaultAgentMode, StringComparison.OrdinalIgnoreCase),
                max_parallel_executors = snapshot.Spawn.MaxParallelWorkers,
                worktree_isolation = false,
                approval_required = true,
                budget_policy = "bounded",
                runtime_profile_id = mode.Bindings.TryGetValue(id, out var profileId) ? profileId : null,
                operation_agents = mode.Bindings.TryGetValue(id, out var pid) && snapshot.Profiles.TryGetValue(pid, out var profile) ? profile.OperationAgents : (IReadOnlyList<string>)Array.Empty<string>(),
                execution_agents = mode.Bindings.TryGetValue(id, out var pid2) && snapshot.Profiles.TryGetValue(pid2, out var profile2) ? profile2.ExecutionAgents : (IReadOnlyList<string>)Array.Empty<string>(),
                activation_policy = mode.Bindings.TryGetValue(id, out var pid3) && snapshot.Profiles.TryGetValue(pid3, out var profile3) ? profile3.ActivationPolicy : null
            }));
        }
        var (t,w,_) = Ctx(a); var q=req.Query["status"].ToString();
        await using var db = await f.CreateDbContextAsync(ct);
        var list = await db.AgentModes.Where(x=>x.TenantId==t && x.WorkspaceId==w && (string.IsNullOrEmpty(q)||x.Status==q)).OrderByDescending(x=>x.UpdatedAt).ToListAsync(ct);
        // If DB empty, fall back to TOML for backward compat (so old tests see TOML modes)
        if (list.Count == 0 && string.IsNullOrEmpty(q))
        {
            var snapshot = toml.Current;
            var appId = TinadecCore.DmaEA.AgentRuntimeConfigurationSnapshot.NormalizeApplicationMode(null);
            if (snapshot.ApplicationModes.TryGetValue(appId, out var mode))
                return Results.Ok(mode.AllowedAgentModes.Select(id => new { id, display_name = id, summary = $"Agent mode '{id}'", application_mode = appId, is_default = string.Equals(id, mode.DefaultAgentMode, StringComparison.OrdinalIgnoreCase), max_parallel_executors = snapshot.Spawn.MaxParallelWorkers, worktree_isolation = false, approval_required = true, budget_policy = "bounded" }));
        }
        return Results.Ok(list.Select(ToModeDto));
    }
    static async Task<IResult> CreateMode(HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        var slug = el.TryGetProperty("slug", out var s) ? s.GetString() : null;
        var name = el.TryGetProperty("display_name", out var n) ? n.GetString() : el.TryGetProperty("name", out var n2) ? n2.GetString() : "mode";
        var (t,w,p)=Ctx(a);
        await using var db= await f.CreateDbContextAsync(ct);
        var rec = new AgentModeRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, Slug=Slug(slug,name!), DisplayName=name!, Description= el.TryGetProperty("description",out var d)? d.GetString():null, Status="draft", Revision=1, Version=0, CreatedAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow, CreatedByPrincipalId=p, UpdatedByPrincipalId=p};
        db.AgentModes.Add(rec); try{await db.SaveChangesAsync(ct);}catch(DbUpdateException){return Results.Conflict(new{code="conflict",message="Draft slug exists"});}
        // optional inline nodes/edges
        await UpsertModeTopology(db, rec, el, t, w, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/agent-modes/{rec.Id}", ToModeDto(rec));
    }
    static async Task<IResult> GetMode(Guid id, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        var nodes=await db.ModeNodes.Where(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w).ToListAsync(ct);
        var edges=await db.ModeEdges.Where(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w).ToListAsync(ct);
        var layout=await db.CanvasLayouts.FirstOrDefaultAsync(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w, ct);
        return Results.Ok(new{ id=rec.Id, slug=rec.Slug, display_name=rec.DisplayName, description=rec.Description, status=rec.Status, revision=rec.Revision, version=rec.Version, nodes=nodes.Select(n=>new{ id=n.Id, node_key=n.NodeKey, agent_definition_id=n.AgentDefinitionId, layer=n.Layer, label=n.Label, position= n.PositionJson!=null? JsonSerializer.Deserialize<JsonElement>(n.PositionJson): (JsonElement?)null, config= n.ConfigJson!=null? JsonSerializer.Deserialize<JsonElement>(n.ConfigJson): (JsonElement?)null}), edges=edges.Select(e=>new{ id=e.Id, edge_key=e.EdgeKey, source_node_key=e.SourceNodeKey, target_node_key=e.TargetNodeKey}), canvas_layout= layout!=null? JsonSerializer.Deserialize<JsonElement>(layout.LayoutJson): (JsonElement?)null, created_at=rec.CreatedAt, updated_at=rec.UpdatedAt});
    }
    static async Task<IResult> UpdateModeDraft(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var el=await JsonSerializer.DeserializeAsync<JsonElement>(req.Body,cancellationToken:ct);
        var exp=IfMatch(req); var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict",message="Revision conflict"},statusCode:412);
        if(el.TryGetProperty("display_name",out var n)) rec.DisplayName=n.GetString()??rec.DisplayName;
        if(el.TryGetProperty("description",out var d)) rec.Description=d.GetString();
        rec.Revision++; rec.UpdatedAt=DateTimeOffset.UtcNow; rec.UpdatedByPrincipalId=p; rec.Status="draft";
        await UpsertModeTopology(db, rec, el, t, w, ct);
        try{await db.SaveChangesAsync(ct);}catch(DbUpdateConcurrencyException){return Results.Json(new{code="conflict",message="Concurrent"},statusCode:412);}
        return Results.Ok(ToModeDto(rec));
    }
    static async Task UpsertModeTopology(AgentConfigurationDbContext db, AgentModeRecord mode, JsonElement el, Guid t, Guid w, CancellationToken ct)
    {
        if(el.TryGetProperty("nodes", out var nodes) && nodes.ValueKind==JsonValueKind.Array)
        {
            foreach(var n in nodes.EnumerateArray())
            {
                var key=n.TryGetProperty("node_key", out var k)? k.GetString(): n.TryGetProperty("id",out var k2)? k2.GetString(): Guid.NewGuid().ToString("N");
                var agentId=n.TryGetProperty("agent_definition_id",out var aid)? Guid.TryParse(aid.GetString(), out var g)? g: Guid.Empty : Guid.Empty;
                if(agentId==Guid.Empty && n.TryGetProperty("agent_id",out var aid2)) Guid.TryParse(aid2.GetString(), out agentId);
                var layer=n.TryGetProperty("layer",out var l)? l.GetString()??"operation": "operation";
                if(layer!="operation" && layer!="execution") throw new ArgumentException("Mode node layer must be operation or execution");
                var existing=await db.ModeNodes.FirstOrDefaultAsync(x=>x.ModeId==mode.Id && x.NodeKey==key && x.TenantId==t && x.WorkspaceId==w, ct);
                if(existing is null){
                    db.ModeNodes.Add(new ModeNodeRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, ModeId=mode.Id, NodeKey=key!, AgentDefinitionId=agentId, Layer=layer, Label=n.TryGetProperty("label",out var lab)?lab.GetString():null, PositionJson=n.TryGetProperty("position",out var pos)?pos.GetRawText(): n.TryGetProperty("position_json",out var pj)?pj.GetRawText():null, ConfigJson=n.TryGetProperty("config",out var cfg)?cfg.GetRawText():null, Status="draft", Revision=1, CreatedAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow});
                } else {
                    if(n.TryGetProperty("agent_definition_id",out var aid3) && Guid.TryParse(aid3.GetString(),out var g3)) existing.AgentDefinitionId=g3;
                    if(n.TryGetProperty("layer",out var l2)) existing.Layer=l2.GetString()??existing.Layer;
                    if(n.TryGetProperty("label",out var lab2)) existing.Label=lab2.GetString();
                    if(n.TryGetProperty("position",out var pos2)) existing.PositionJson=pos2.GetRawText();
                    if(n.TryGetProperty("config",out var cfg2)) existing.ConfigJson=cfg2.GetRawText();
                    existing.UpdatedAt=DateTimeOffset.UtcNow; existing.Revision++;
                }
            }
        }
        if(el.TryGetProperty("edges", out var edges) && edges.ValueKind==JsonValueKind.Array)
        {
            foreach(var e in edges.EnumerateArray())
            {
                var key=e.TryGetProperty("edge_key",out var k)? k.GetString(): Guid.NewGuid().ToString("N");
                var src=e.TryGetProperty("source_node_key",out var s)? s.GetString(): e.TryGetProperty("source",out var s2)? s2.GetString(): null;
                var tgt=e.TryGetProperty("target_node_key",out var t2)? t2.GetString(): e.TryGetProperty("target",out var t3)? t3.GetString(): null;
                if(string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt)) continue;
                var existing=await db.ModeEdges.FirstOrDefaultAsync(x=>x.ModeId==mode.Id && x.EdgeKey==key && x.TenantId==t && x.WorkspaceId==w, ct);
                if(existing is null) db.ModeEdges.Add(new ModeEdgeRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, ModeId=mode.Id, EdgeKey=key!, SourceNodeKey=src!, TargetNodeKey=tgt!, ConditionJson=e.TryGetProperty("condition",out var cond)?cond.GetRawText():null, Status="draft", Revision=1, CreatedAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow});
                else { existing.SourceNodeKey=src!; existing.TargetNodeKey=tgt!; existing.UpdatedAt=DateTimeOffset.UtcNow; existing.Revision++; }
            }
        }
        if(el.TryGetProperty("canvas_layout", out var layout) || el.TryGetProperty("layout", out layout))
        {
            var existing=await db.CanvasLayouts.FirstOrDefaultAsync(x=>x.ModeId==mode.Id && x.TenantId==t && x.WorkspaceId==w, ct);
            var json=layout.GetRawText();
            if(existing is null) db.CanvasLayouts.Add(new CanvasLayoutRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, ModeId=mode.Id, LayoutJson=json, Status="draft", Revision=1, CreatedAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow});
            else { existing.LayoutJson=json; existing.UpdatedAt=DateTimeOffset.UtcNow; existing.Revision++; }
        }
    }
    static async Task<IResult> PublishMode(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var exp=IfMatch(req); var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict",message="Revision conflict"},statusCode:412);
        var nodes=await db.ModeNodes.Where(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w).ToListAsync(ct);
        var edges=await db.ModeEdges.Where(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w).ToListAsync(ct);
        // dual-lane validation
        var opCount=nodes.Count(n=>n.Layer=="operation");
        var exCount=nodes.Count(n=>n.Layer=="execution");
        if(opCount==0) return Results.BadRequest(new{code="invalid_request",message="Mode must have at least one operation agent (meeting)"});
        if(exCount==0) return Results.BadRequest(new{code="invalid_request",message="Mode must have at least one execution agent"});
        // cross-layer reuse warning
        var byAgent=nodes.GroupBy(n=>n.AgentDefinitionId).Where(g=>g.Select(x=>x.Layer).Distinct().Count()>1).Select(g=>g.Key).ToArray();
        var warnings = new List<object>();
        if(byAgent.Length>0) warnings.Add(new{ code="CROSS_LAYER_REUSE", message="Same agent reused across operation and execution", agent_ids=byAgent });
        // tool effective intersection per node
        foreach(var node in nodes)
        {
            var agentDef = await db.AgentDefinitions.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==node.AgentDefinitionId && x.TenantId==t && x.WorkspaceId==w, ct);
            if(agentDef is null) continue;
            var agentTools = ParseTools(agentDef.ToolScopeJson);
            var modeTools = ParseTools(node.ConfigJson);
            // if agent has "*", effective = modeTools or * (allow all)
            HashSet<string> effective;
            if (agentTools.Contains("*")) effective = modeTools.Count>0 ? new HashSet<string>(modeTools, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase){ "*"};
            else if (modeTools.Count==0) effective = new HashSet<string>(agentTools, StringComparer.OrdinalIgnoreCase);
            else effective = new HashSet<string>(agentTools.Intersect(modeTools, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (effective.Count==0)
                warnings.Add(new{ code="EMPTY_EFFECTIVE_TOOLS", message=$"Node {node.NodeKey} has no effective tools after intersection", node_key=node.NodeKey, agent_id=node.AgentDefinitionId });
        }
        string? warning=null;
        if(warnings.Count>0) warning=JsonSerializer.Serialize(warnings);
        // freeze snapshot
        var snapshot=JsonSerializer.Serialize(new{ nodes=nodes.Select(n=>new{ n.NodeKey, n.AgentDefinitionId, n.Layer}), edges=edges.Select(e=>new{e.EdgeKey, e.SourceNodeKey, e.TargetNodeKey})});
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
        var ver=(await db.ModeVersions.Where(x=>x.AgentModeId==id).MaxAsync(x=>(int?)x.Version, ct)??0)+1;
        db.ModeVersions.Add(new ModeVersionRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, AgentModeId=id, Version=ver, SnapshotJson=snapshot, TopologyHash=hash, WarningJson=warning, Status="published", Revision=1, CreatedAt=DateTimeOffset.UtcNow, CreatedByPrincipalId=p});
        rec.Version=ver; rec.Revision++; rec.UpdatedAt=DateTimeOffset.UtcNow; rec.Status="published";
        await db.SaveChangesAsync(ct);
        return Results.Ok(new{ id=rec.Id, version=ver, revision=rec.Revision, topology_hash=hash, warning= warning!=null? JsonSerializer.Deserialize<JsonElement>(warning): (JsonElement?)null });
    }
    static async Task<IResult> ArchiveMode(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var exp=IfMatch(req); var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict"},statusCode:412);
        rec.Status="archived"; rec.ArchivedAt=DateTimeOffset.UtcNow; rec.Revision++; rec.UpdatedAt=DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return Results.Ok(ToModeDto(rec));
    }
    static async Task<IResult> ListModeVersions(Guid id, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var list=await db.ModeVersions.Where(x=>x.AgentModeId==id && x.TenantId==t && x.WorkspaceId==w).OrderBy(x=>x.Version).ToListAsync(ct);
        return Results.Ok(list.Select(v=>new{ id=v.Id, agent_mode_id=v.AgentModeId, version=v.Version, snapshot=v.SnapshotJson!=null? JsonSerializer.Deserialize<JsonElement>(v.SnapshotJson): (JsonElement?)null, topology_hash=v.TopologyHash, warning=v.WarningJson!=null? JsonSerializer.Deserialize<JsonElement>(v.WarningJson): (JsonElement?)null, created_at=v.CreatedAt}));
    }
    static async Task<IResult> GetModeVersion(Guid id, Guid versionId, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var v=await db.ModeVersions.FirstOrDefaultAsync(x=>x.Id==versionId && x.AgentModeId==id && x.TenantId==t && x.WorkspaceId==w, ct);
        return v is null? Results.NotFound(new{code="not_found"}): Results.Ok(new{ id=v.Id, agent_mode_id=v.AgentModeId, version=v.Version, snapshot=v.SnapshotJson!=null? JsonSerializer.Deserialize<JsonElement>(v.SnapshotJson): (JsonElement?)null, topology_hash=v.TopologyHash, warning=v.WarningJson!=null? JsonSerializer.Deserialize<JsonElement>(v.WarningJson): (JsonElement?)null, created_at=v.CreatedAt});
    }

    // ── pipelines ──
    static async Task<IResult> ListPipelines(IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, HttpRequest req, CancellationToken ct)
    {
        var (t,w,_)=Ctx(a); var q=req.Query["status"].ToString();
        await using var db=await f.CreateDbContextAsync(ct);
        var list=await db.PromptPipelines.Where(x=>x.TenantId==t && x.WorkspaceId==w && (string.IsNullOrEmpty(q)||x.Status==q)).OrderByDescending(x=>x.UpdatedAt).ToListAsync(ct);
        return Results.Ok(list.Select(p=>new{ id=p.Id, slug=p.Slug, display_name=p.DisplayName, description=p.Description, graph= JsonSerializer.Deserialize<JsonElement>(p.GraphJson), status=p.Status, revision=p.Revision, version=p.Version, created_at=p.CreatedAt, updated_at=p.UpdatedAt}));
    }
    static async Task<IResult> CreatePipeline(HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var el=await JsonSerializer.DeserializeAsync<JsonElement>(req.Body,cancellationToken:ct);
        var slug=el.TryGetProperty("slug",out var s)? s.GetString():null;
        var name=el.TryGetProperty("display_name",out var n)? n.GetString(): el.TryGetProperty("name",out var n2)? n2.GetString(): "pipeline";
        var graph=el.TryGetProperty("graph",out var g)? g.GetRawText(): el.TryGetProperty("graph_json",out var gj)? gj.GetRawText(): "{}";
        // basic graph validation
        try{ JsonDocument.Parse(graph);}catch{return Results.BadRequest(new{code="invalid_request",message="graph must be valid JSON"});}
        var gerr = ValidatePromptGraph(graph);
        if (gerr is not null) return Results.BadRequest(new{ code="invalid_request", message=gerr });
        var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=new PromptPipelineRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, Slug=Slug(slug,name!), DisplayName=name!, Description=el.TryGetProperty("description",out var d)? d.GetString():null, GraphJson=graph, Status="draft", Revision=1, Version=0, CreatedAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow, CreatedByPrincipalId=p, UpdatedByPrincipalId=p};
        db.PromptPipelines.Add(rec); try{await db.SaveChangesAsync(ct);}catch(DbUpdateException){return Results.Conflict(new{code="conflict",message="Draft slug exists"});}
        return Results.Created($"/api/v1/prompt-pipelines/{rec.Id}", new{ id=rec.Id, slug=rec.Slug, display_name=rec.DisplayName, graph=JsonSerializer.Deserialize<JsonElement>(graph), status=rec.Status, revision=rec.Revision});
    }
    static async Task<IResult> GetPipeline(Guid id, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.PromptPipelines.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        return rec is null? Results.NotFound(new{code="not_found"}): Results.Ok(new{ id=rec.Id, slug=rec.Slug, display_name=rec.DisplayName, description=rec.Description, graph=JsonSerializer.Deserialize<JsonElement>(rec.GraphJson), status=rec.Status, revision=rec.Revision, version=rec.Version, created_at=rec.CreatedAt, updated_at=rec.UpdatedAt});
    }
    static async Task<IResult> UpdatePipelineDraft(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var el=await JsonSerializer.DeserializeAsync<JsonElement>(req.Body,cancellationToken:ct);
        var exp=IfMatch(req); var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.PromptPipelines.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict"},statusCode:412);
        if(el.TryGetProperty("display_name",out var n)) rec.DisplayName=n.GetString()??rec.DisplayName;
        if(el.TryGetProperty("description",out var d)) rec.Description=d.GetString();
        if(el.TryGetProperty("graph",out var g)) { try{JsonDocument.Parse(g.GetRawText());}catch{return Results.BadRequest(new{code="invalid_request",message="graph invalid"});} var err=ValidatePromptGraph(g.GetRawText()); if(err is not null) return Results.BadRequest(new{code="invalid_request",message=err}); rec.GraphJson=g.GetRawText();}
        if(el.TryGetProperty("graph_json",out var gj)) { try{JsonDocument.Parse(gj.GetRawText());}catch{return Results.BadRequest(new{code="invalid_request",message="graph invalid"});} var err2=ValidatePromptGraph(gj.GetRawText()); if(err2 is not null) return Results.BadRequest(new{code="invalid_request",message=err2}); rec.GraphJson=gj.GetRawText();}
        rec.Revision++; rec.UpdatedAt=DateTimeOffset.UtcNow; rec.UpdatedByPrincipalId=p; rec.Status="draft";
        await db.SaveChangesAsync(ct); return Results.Ok(new{ id=rec.Id, slug=rec.Slug, display_name=rec.DisplayName, graph=JsonSerializer.Deserialize<JsonElement>(rec.GraphJson), status=rec.Status, revision=rec.Revision});
    }
    static async Task<IResult> PublishPipeline(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var exp=IfMatch(req); var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.PromptPipelines.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict"},statusCode:412);
        var gerr = ValidatePromptGraph(rec.GraphJson);
        if (gerr is not null) return Results.BadRequest(new{ code="invalid_request", message=gerr });
        // ponytail: strong publish validation – must contain at least one template AND one assemble, whitelist + cycle already checked
        try{
            using var d=JsonDocument.Parse(rec.GraphJson);
            var root=d.RootElement;
            var hasAssemble=false; var hasTemplate=false;
            if(root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind==JsonValueKind.Array){
                foreach(var n in nodes.EnumerateArray()){
                    var type = n.TryGetProperty("type", out var tt) ? tt.GetString() : n.TryGetProperty("kind", out var kk) ? kk.GetString(): null;
                    if(string.Equals(type,"assemble",StringComparison.OrdinalIgnoreCase)) hasAssemble=true;
                    if(string.Equals(type,"template",StringComparison.OrdinalIgnoreCase)) hasTemplate=true;
                }
            }
            if(!hasAssemble || !hasTemplate) return Results.BadRequest(new{ code="invalid_request", message="pipeline must contain at least one template and one assemble node"});
        }catch{}
        var ver=(await db.PromptVersions.Where(x=>x.PromptPipelineId==id).MaxAsync(x=>(int?)x.Version, ct)??0)+1;
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rec.GraphJson))).ToLowerInvariant();
        db.PromptVersions.Add(new PromptVersionRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, PromptPipelineId=id, Version=ver, GraphJson=rec.GraphJson, ContentHash=hash, ContentLength=rec.GraphJson.Length, Status="published", Revision=1, CreatedAt=DateTimeOffset.UtcNow, CreatedByPrincipalId=p});
        rec.Version=ver; rec.Revision++; rec.UpdatedAt=DateTimeOffset.UtcNow; rec.Status="published";
        await db.SaveChangesAsync(ct); return Results.Ok(new{ id=rec.Id, version=ver, revision=rec.Revision, content_hash=hash});
    }
    static async Task<IResult> ArchivePipeline(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var exp=IfMatch(req); var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.PromptPipelines.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict"},statusCode:412);
        rec.Status="archived"; rec.ArchivedAt=DateTimeOffset.UtcNow; rec.Revision++; rec.UpdatedAt=DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return Results.Ok(new{ id=rec.Id, status=rec.Status, revision=rec.Revision});
    }
    static async Task<IResult> ListPipelineVersions(Guid id, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var list=await db.PromptVersions.Where(x=>x.PromptPipelineId==id && x.TenantId==t && x.WorkspaceId==w).OrderBy(x=>x.Version).ToListAsync(ct);
        return Results.Ok(list.Select(v=>new{ id=v.Id, prompt_pipeline_id=v.PromptPipelineId, version=v.Version, graph=JsonSerializer.Deserialize<JsonElement>(v.GraphJson), content_hash=v.ContentHash, created_at=v.CreatedAt}));
    }
    static async Task<IResult> GetPipelineVersion(Guid id, Guid versionId, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var v=await db.PromptVersions.FirstOrDefaultAsync(x=>x.Id==versionId && x.PromptPipelineId==id && x.TenantId==t && x.WorkspaceId==w, ct);
        return v is null? Results.NotFound(new{code="not_found"}): Results.Ok(new{ id=v.Id, prompt_pipeline_id=v.PromptPipelineId, version=v.Version, graph=JsonSerializer.Deserialize<JsonElement>(v.GraphJson), content_hash=v.ContentHash, created_at=v.CreatedAt});
    }

    // ── candidates & instances ──
    static async Task<IResult> ListCandidates(IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        // thin wrapper: currently empty until evolution generates; return empty list shape compatible with Desktop
        var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        // check if we have a candidates table? reuse Memory candidates for now as fallback
        return Results.Ok(Array.Empty<object>());
    }
    static async Task<IResult> PromoteCandidate(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        // stub: create a draft agent from candidate, optionally add to mode draft
        var (t,w,p)=Ctx(a); var el=await JsonSerializer.DeserializeAsync<JsonElement>(req.Body,cancellationToken:ct);
        var targetModeDraft = el.TryGetProperty("target_mode_draft_id", out var m) && Guid.TryParse(m.GetString(), out var gm) ? gm : (Guid?)null;
        await using var db=await f.CreateDbContextAsync(ct);
        // create draft agent
        var rec=new AgentDefinitionRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, Slug="candidate-"+id.ToString("N")[..8], DisplayName="Promoted "+id.ToString()[..8], Layer="operation", Role="promoted", Status="draft", Revision=1, Version=0, CreatedAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow, CreatedByPrincipalId=p, UpdatedByPrincipalId=p};
        db.AgentDefinitions.Add(rec);
        if(targetModeDraft.HasValue){
            var mode=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==targetModeDraft.Value && x.TenantId==t && x.WorkspaceId==w, ct);
            if(mode!=null){
                db.ModeNodes.Add(new ModeNodeRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, ModeId=mode.Id, NodeKey="node-"+rec.Id.ToString("N")[..6], AgentDefinitionId=rec.Id, Layer=rec.Layer, Status="draft", Revision=1, CreatedAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow});
                mode.UpdatedAt=DateTimeOffset.UtcNow; mode.Revision++;
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new{ candidate_id=id, promoted_agent_id=rec.Id, target_mode_draft_id=targetModeDraft, message="Candidate promoted to draft; republish mode to activate"});
    }
    static async Task<IResult> RejectCandidate(Guid id, CancellationToken ct) => Results.Ok(new{ candidate_id=id, status="rejected"});
    static async Task<IResult> ListInstances(HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        // proxy to lifecycle agent_instances via query run_id; for now return empty
        return Results.Ok(Array.Empty<object>());
    }

    static string? ValidateModelStrategy(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? kind = null;
            if (root.TryGetProperty("kind", out var k)) kind = k.GetString()?.Trim().ToLowerInvariant();
            else if (root.TryGetProperty("selection_kind", out var sk)) kind = sk.GetString()?.Trim().ToLowerInvariant();
            if (kind is not ("inherit" or "fixed" or "parent_select"))
                return "model_strategy.kind must be inherit|fixed|parent_select";
            if (kind == "fixed")
            {
                if (!root.TryGetProperty("provider_instance_id", out var pid) || string.IsNullOrWhiteSpace(pid.GetString()))
                    return "fixed model_strategy requires provider_instance_id";
                if (!root.TryGetProperty("model", out var m) && !root.TryGetProperty("model_id", out m) || string.IsNullOrWhiteSpace(m.GetString()))
                    return "fixed model_strategy requires model";
            }
            return null;
        }
        catch { return "model_strategy must be valid JSON"; }
    }

    static HashSet<string> ParseTools(string? json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray()) if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())) set.Add(el.GetString()!.Trim());
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("allowed_tools", out var at) && at.ValueKind == JsonValueKind.Array) foreach (var el in at.EnumerateArray()) if (el.ValueKind == JsonValueKind.String) set.Add(el.GetString()!.Trim());
                if (root.TryGetProperty("tool_enabled", out var te) && te.ValueKind == JsonValueKind.Array) foreach (var el in te.EnumerateArray()) if (el.ValueKind == JsonValueKind.String) set.Add(el.GetString()!.Trim());
                if (root.TryGetProperty("tools", out var ts) && ts.ValueKind == JsonValueKind.Array) foreach (var el in ts.EnumerateArray()) if (el.ValueKind == JsonValueKind.String) set.Add(el.GetString()!.Trim());
                // if object itself is like {"*"} handled above array case
            }
        }
        catch { }
        return set;
    }

    static string? ValidatePromptGraph(string graphJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(graphJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "graph must be object";
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "template", "variable", "condition", "stage", "stage_select", "read_only_retrieval", "retrieval", "memory", "budget", "assemble", "context_trim" };
            var nodes = new List<JsonElement>();
            if (root.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Array) nodes.AddRange(n.EnumerateArray());
            else if (root.TryGetProperty("prompt_nodes", out var pn) && pn.ValueKind == JsonValueKind.Array) nodes.AddRange(pn.EnumerateArray());
            else nodes = new List<JsonElement>(); // empty is allowed for draft but publish requires at least one
            foreach (var node in nodes)
            {
                if (!node.TryGetProperty("type", out var t) && !node.TryGetProperty("kind", out t))
                    return "each node must have type/kind";
                var type = t.GetString()?.Trim().ToLowerInvariant();
                if (!allowed.Contains(type!)) return $"unknown node type '{type}'";
            }
            // check assemble or template exists for publish
            // cycle detection if edges present
            if (root.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
            {
                var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var nodesById = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in nodes)
                {
                    var id = node.TryGetProperty("id", out var idEl) ? idEl.GetString() : node.TryGetProperty("node_key", out var nk) ? nk.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id)) nodesById.Add(id!);
                }
                foreach (var e in edges.EnumerateArray())
                {
                    var src = e.TryGetProperty("source", out var s) ? s.GetString() : e.TryGetProperty("source_node_key", out var sn) ? sn.GetString() : null;
                    var tgt = e.TryGetProperty("target", out var t) ? t.GetString() : e.TryGetProperty("target_node_key", out var tn) ? tn.GetString() : null;
                    if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt)) continue;
                    if (!adj.TryGetValue(src!, out var list)) adj[src!] = list = new List<string>();
                    list.Add(tgt!);
                }
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool HasCycle(string cur)
                {
                    if (stack.Contains(cur)) return true;
                    if (visited.Contains(cur)) return false;
                    visited.Add(cur); stack.Add(cur);
                    if (adj.TryGetValue(cur, out var nxt)) foreach (var nb in nxt) if (HasCycle(nb)) return true;
                    stack.Remove(cur); return false;
                }
                foreach (var nid in nodesById) if (HasCycle(nid)) return "graph contains cycle";
            }
            return null;
        }
        catch (Exception ex) { return "graph invalid: " + ex.Message; }
    }

    static object ToAgentDto(AgentDefinitionRecord r) => new{ id=r.Id, slug=r.Slug, display_name=r.DisplayName, layer=r.Layer, role=r.Role, capabilities= r.CapabilitiesJson!=null? JsonSerializer.Deserialize<JsonElement>(r.CapabilitiesJson): (JsonElement?)null, model_strategy= r.ModelStrategyJson!=null? JsonSerializer.Deserialize<JsonElement>(r.ModelStrategyJson): (JsonElement?)null, tool_scope= r.ToolScopeJson!=null? JsonSerializer.Deserialize<JsonElement>(r.ToolScopeJson): (JsonElement?)null, status=r.Status, revision=r.Revision, version=r.Version, created_at=r.CreatedAt, updated_at=r.UpdatedAt, archived_at=r.ArchivedAt };
    static object ToModeDto(AgentModeRecord r) => new{ id=r.Id, slug=r.Slug, display_name=r.DisplayName, description=r.Description, status=r.Status, revision=r.Revision, version=r.Version, created_at=r.CreatedAt, updated_at=r.UpdatedAt, archived_at=r.ArchivedAt };
}
