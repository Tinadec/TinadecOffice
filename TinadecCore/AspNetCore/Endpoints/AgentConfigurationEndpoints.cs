using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;

namespace TinadecCore.AspNetCore.Endpoints;

public static class AgentConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapAgentConfigurationEndpoints(this IEndpointRouteBuilder app)
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

        // Workspace defaults are a separate control-plane object. They are
        // drafted and published independently from the referenced definitions.
        app.MapGet("/api/v1/workspace-defaults", GetWorkspaceDefaults);
        app.MapPut("/api/v1/workspace-defaults/draft", UpdateWorkspaceDefaultsDraft);
        app.MapPost("/api/v1/workspace-defaults/publish", PublishWorkspaceDefaults);
        app.MapPost("/api/v1/workspace-defaults/archive", ArchiveWorkspaceDefaults);

        // Candidate review is mapped only by MemoryReviewEndpoints.
        app.MapGet("/api/v1/agent-runtime-instances", ListInstances);

        return app;
    }

    // ── helpers ──
    static (Guid tenant, Guid workspace, Guid principal) Ctx(ITenantContextAccessor a) => (a.Current.TenantId, a.Current.WorkspaceId, a.Current.PrincipalId);
    static long IfMatch(HttpRequest r) => long.TryParse(r.Headers.IfMatch.FirstOrDefault()?.Trim('\"', 'W', '/', ' '), out var v) ? v : -1;
    static string Slug(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim().ToLowerInvariant().Replace(' ', '-');

    static async Task<IResult> GetWorkspaceDefaults(IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, HttpResponse response, CancellationToken ct)
    {
        var (t, w, _) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var row = await db.WorkspaceDefaults.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == t && x.WorkspaceId == w, ct);
        if (row is null) return Results.NotFound(new { code = "not_found" });
        SetEtag(response, row.Revision);
        return Results.Ok(ToWorkspaceDefaultsDto(row));
    }

    static async Task<IResult> UpdateWorkspaceDefaultsDraft(
        WorkspaceDefaultsRequestDto? input,
        HttpRequest req,
        IDbContextFactory<AgentConfigurationDbContext> f,
        ITenantContextAccessor a,
        CancellationToken ct)
    {
        if (input is null) return Results.BadRequest(new { code = "invalid_request", message = "Workspace defaults are required." });
        var expected = IfMatch(req);
        var (t, w, p) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var row = await db.WorkspaceDefaults.SingleOrDefaultAsync(x => x.TenantId == t && x.WorkspaceId == w, ct);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            if (expected >= 0) return Results.Json(new { code = "conflict", message = "Workspace defaults do not exist." }, statusCode: 412);
            row = new WorkspaceDefaultsRecord
            {
                TenantId = t, WorkspaceId = w, DefaultAgentDefinitionId = input.DefaultAgentDefinitionId,
                DefaultAgentModeId = input.DefaultAgentModeId, DefaultPromptPipelineId = input.DefaultPromptPipelineId,
                DefaultAgentVersionId = input.DefaultAgentVersionId, DefaultModeVersionId = input.DefaultModeVersionId,
                DefaultPromptVersionId = input.DefaultPromptVersionId,
                Status = "draft", Revision = 1, CreatedAt = now, UpdatedAt = now
            };
            db.WorkspaceDefaults.Add(row);
        }
        else
        {
            if (row.Status == "archived") return Results.Conflict(new { code = "archived", message = "Archived workspace defaults cannot be edited." });
            if (expected >= 0 && row.Revision != expected) return Results.Json(new { code = "conflict", message = "Revision conflict." }, statusCode: 412);
            row.DefaultAgentDefinitionId = input.DefaultAgentDefinitionId;
            row.DefaultAgentModeId = input.DefaultAgentModeId;
            row.DefaultPromptPipelineId = input.DefaultPromptPipelineId;
            row.DefaultAgentVersionId = input.DefaultAgentVersionId;
            row.DefaultModeVersionId = input.DefaultModeVersionId;
            row.DefaultPromptVersionId = input.DefaultPromptVersionId;
            row.Status = "draft";
            row.Revision++;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        SetEtag(req.HttpContext.Response, row.Revision);
        return Results.Ok(ToWorkspaceDefaultsDto(row));
    }

    static async Task<IResult> PublishWorkspaceDefaults(
        HttpRequest req,
        IDbContextFactory<AgentConfigurationDbContext> f,
        ITenantContextAccessor a,
        CancellationToken ct)
    {
        var expected = IfMatch(req);
        var (t, w, _) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var row = await db.WorkspaceDefaults.SingleOrDefaultAsync(x => x.TenantId == t && x.WorkspaceId == w, ct);
        if (row is null) return Results.NotFound(new { code = "not_found" });
        if (expected >= 0 && row.Revision != expected) return Results.Json(new { code = "conflict", message = "Revision conflict." }, statusCode: 412);
        if (!string.Equals(row.Status, "draft", StringComparison.OrdinalIgnoreCase)) return Results.Conflict(new { code = "not_draft", message = "Only draft workspace defaults can be published." });
        if (row.DefaultAgentDefinitionId is { } agent && !await db.AgentDefinitions.AnyAsync(x => x.Id == agent && x.TenantId == t && x.WorkspaceId == w && x.Status == "published", ct))
            return Results.BadRequest(new { code = "invalid_request", message = "default_agent_definition_id must reference a published agent." });
        if (row.DefaultAgentModeId is { } mode && !await db.AgentModes.AnyAsync(x => x.Id == mode && x.TenantId == t && x.WorkspaceId == w && x.Status == "published", ct))
            return Results.BadRequest(new { code = "invalid_request", message = "default_agent_mode_id must reference a published mode." });
        if (row.DefaultPromptPipelineId is { } prompt && !await db.PromptPipelines.AnyAsync(x => x.Id == prompt && x.TenantId == t && x.WorkspaceId == w && x.Status == "published", ct))
            return Results.BadRequest(new { code = "invalid_request", message = "default_prompt_pipeline_id must reference a published prompt pipeline." });
        if (row.DefaultAgentDefinitionId is { } agentDefinition)
        {
            row.DefaultAgentVersionId ??= await db.AgentVersions.Where(x => x.AgentDefinitionId == agentDefinition && x.TenantId == t && x.WorkspaceId == w && x.Status == "published").OrderByDescending(x => x.Version).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (row.DefaultAgentVersionId is not { } agentVersion || !await db.AgentVersions.AnyAsync(x => x.Id == agentVersion && x.AgentDefinitionId == agentDefinition && x.TenantId == t && x.WorkspaceId == w && x.Status == "published", ct))
                return Results.BadRequest(new { code = "invalid_request", message = "default_agent_version_id must reference a published version of default_agent_definition_id." });
        }
        else if (row.DefaultAgentVersionId is not null) return Results.BadRequest(new { code = "invalid_request", message = "default_agent_version_id requires default_agent_definition_id." });
        if (row.DefaultAgentModeId is { } modeDefinition)
        {
            row.DefaultModeVersionId ??= await db.ModeVersions.Where(x => x.AgentModeId == modeDefinition && x.TenantId == t && x.WorkspaceId == w && x.Status == "published").OrderByDescending(x => x.Version).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (row.DefaultModeVersionId is not { } modeVersion || !await db.ModeVersions.AnyAsync(x => x.Id == modeVersion && x.AgentModeId == modeDefinition && x.TenantId == t && x.WorkspaceId == w && x.Status == "published", ct))
                return Results.BadRequest(new { code = "invalid_request", message = "default_mode_version_id must reference a published version of default_agent_mode_id." });
        }
        else if (row.DefaultModeVersionId is not null) return Results.BadRequest(new { code = "invalid_request", message = "default_mode_version_id requires default_agent_mode_id." });
        if (row.DefaultPromptPipelineId is { } promptDefinition)
        {
            row.DefaultPromptVersionId ??= await db.PromptVersions.Where(x => x.PromptPipelineId == promptDefinition && x.TenantId == t && x.WorkspaceId == w && x.Status == "published").OrderByDescending(x => x.Version).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (row.DefaultPromptVersionId is not { } promptVersion || !await db.PromptVersions.AnyAsync(x => x.Id == promptVersion && x.PromptPipelineId == promptDefinition && x.TenantId == t && x.WorkspaceId == w && x.Status == "published", ct))
                return Results.BadRequest(new { code = "invalid_request", message = "default_prompt_version_id must reference a published version of default_prompt_pipeline_id." });
        }
        else if (row.DefaultPromptVersionId is not null) return Results.BadRequest(new { code = "invalid_request", message = "default_prompt_version_id requires default_prompt_pipeline_id." });
        row.Status = "active";
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        SetEtag(req.HttpContext.Response, row.Revision);
        return Results.Ok(ToWorkspaceDefaultsDto(row));
    }

    static async Task<IResult> ArchiveWorkspaceDefaults(HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, CancellationToken ct)
    {
        var expected = IfMatch(req);
        var (t, w, _) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var row = await db.WorkspaceDefaults.SingleOrDefaultAsync(x => x.TenantId == t && x.WorkspaceId == w, ct);
        if (row is null) return Results.NotFound(new { code = "not_found" });
        if (expected >= 0 && row.Revision != expected) return Results.Json(new { code = "conflict", message = "Revision conflict." }, statusCode: 412);
        row.Status = "archived";
        row.ArchivedAt = DateTimeOffset.UtcNow;
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        SetEtag(req.HttpContext.Response, row.Revision);
        return Results.Ok(ToWorkspaceDefaultsDto(row));
    }

    static void SetEtag(HttpResponse response, long revision) => response.Headers.ETag = $"\"{revision}\"";

    static object ToWorkspaceDefaultsDto(WorkspaceDefaultsRecord row) => new
    {
        tenant_id = row.TenantId,
        workspace_id = row.WorkspaceId,
        default_agent_definition_id = row.DefaultAgentDefinitionId,
        default_agent_mode_id = row.DefaultAgentModeId,
        default_prompt_pipeline_id = row.DefaultPromptPipelineId,
        default_agent_version_id = row.DefaultAgentVersionId,
        default_mode_version_id = row.DefaultModeVersionId,
        default_prompt_version_id = row.DefaultPromptVersionId,
        status = row.Status,
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt,
        archived_at = row.ArchivedAt
    };

    // ── agents ──
    static async Task<IResult> ListAgents(
        IDbContextFactory<AgentConfigurationDbContext> f,
        IDbContextFactory<LifecycleDbContext> lifecycleFactory,
        IAgentModelResolver modelResolver,
        ITenantContextAccessor a,
        HttpRequest req,
        CancellationToken ct)
    {
        var (t, w, _) = Ctx(a);
        var status = req.Query["status"].ToString();
        var sourceKind = req.Query["source_kind"].ToString();
        var layer = req.Query["layer"].ToString();
        await using var db = await f.CreateDbContextAsync(ct);
        var definitions = await db.AgentDefinitions.AsNoTracking().Where(x => x.TenantId == t && x.WorkspaceId == w).ToListAsync(ct);
        var modes = await db.AgentModes.AsNoTracking().Where(x => x.TenantId == t && x.WorkspaceId == w).ToDictionaryAsync(x => x.Id, ct);
        var nodes = await db.ModeNodes.AsNoTracking().Where(x => x.TenantId == t && x.WorkspaceId == w).ToListAsync(ct);
        var latestModeVersions = (await db.ModeVersions.AsNoTracking().Where(x => x.TenantId == t && x.WorkspaceId == w && x.Status == "published").ToListAsync(ct))
            .GroupBy(x => x.AgentModeId).ToDictionary(x => x.Key, x => x.OrderByDescending(v => v.Version).First());
        var latestAgentVersions = (await db.AgentVersions.AsNoTracking().Where(x => x.TenantId == t && x.WorkspaceId == w && x.Status == "published").ToListAsync(ct))
            .GroupBy(x => x.AgentDefinitionId).ToDictionary(x => x.Key, x => x.OrderByDescending(v => v.Version).First());
        await using var lifecycle = await lifecycleFactory.CreateDbContextAsync(ct);
        var recentInvocations = (await lifecycle.ModelInvocations.AsNoTracking().Where(x => x.TenantId == t && x.WorkspaceId == w && x.Status == "succeeded").ToListAsync(ct))
            .GroupBy(x => x.AgentDefinitionId).ToDictionary(x => x.Key, x => x.OrderByDescending(v => v.CompletedAt).First());

        var result = new List<AgentDirectoryItemDto>();
        foreach (var definition in definitions)
        {
            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(definition.Status, status, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(sourceKind) && !string.Equals(definition.SourceKind, sourceKind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(definition.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
            latestAgentVersions.TryGetValue(definition.Id, out var agentVersion);
            recentInvocations.TryGetValue(definition.Id, out var invocation);
            var usages = nodes.Where(x => x.AgentDefinitionId == definition.Id).Select(node =>
            {
                modes.TryGetValue(node.ModeId, out var mode);
                latestModeVersions.TryGetValue(node.ModeId, out var modeVersion);
                return new AgentModeUsageDto
                {
                    ModeId = node.ModeId, ModeVersionId = modeVersion?.Id, ModeSlug = mode?.Slug ?? "missing-mode",
                    NodeKey = node.NodeKey,
                    ModelStrategyOverride = string.IsNullOrWhiteSpace(node.ModelStrategyOverrideJson) ? null : ModelStrategyJson.Parse(node.ModelStrategyOverrideJson)
                };
            }).ToArray();
            var previews = new Dictionary<string, ModelResolutionPreviewDto>(StringComparer.Ordinal);
            foreach (var usage in usages.Where(x => x.ModeVersionId is not null))
            {
                try
                {
                    previews[$"{usage.ModeSlug}:{usage.NodeKey}"] = await modelResolver.PreviewAsync(new ModelResolutionPreviewRequestDto
                    {
                        AgentDefinitionId = definition.Id, AgentVersionId = agentVersion?.Id,
                        ModeVersionId = usage.ModeVersionId, NodeKey = usage.NodeKey
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or KeyNotFoundException)
                {
                    previews[$"{usage.ModeSlug}:{usage.NodeKey}"] = UnavailablePreview(exception.Message);
                }
            }
            ModelStrategyDto configuredStrategy;
            try { configuredStrategy = ModelStrategyJson.Parse(definition.ModelStrategyJson); }
            catch { configuredStrategy = new ModelStrategyDto { Kind = ModelStrategyKinds.Inherit }; }
            result.Add(new AgentDirectoryItemDto
            {
                Id = definition.Id, Slug = definition.Slug, DisplayName = definition.DisplayName,
                Layer = definition.Layer, Role = definition.Role, SourceKind = definition.SourceKind,
                SourceKey = definition.SourceKey, Managed = definition.Managed, Writable = !definition.Managed && definition.Status != "archived",
                Enabled = definition.Enabled, Status = definition.Status, Revision = definition.Revision, Version = definition.Version,
                CurrentVersionId = agentVersion?.Id, ConfiguredStrategy = configuredStrategy,
                ModeUsages = usages, EffectivePreviews = previews,
                RecentInvocation = invocation is null ? null : ToInvocationDto(invocation), UpdatedAt = definition.UpdatedAt
            });
        }

        var known = definitions.Select(x => x.Id).ToHashSet();
        foreach (var missing in nodes.Where(x => !known.Contains(x.AgentDefinitionId)).GroupBy(x => x.AgentDefinitionId))
        {
            result.Add(new AgentDirectoryItemDto
            {
                Id = missing.Key, Slug = $"missing-{missing.Key:N}", DisplayName = "Missing Agent reference",
                SourceKind = "missing_reference", SourceKey = missing.Key.ToString(), Managed = true, Writable = false,
                Enabled = false, Status = "missing", ConfiguredStrategy = new ModelStrategyDto(),
                ModeUsages = missing.Select(node => new AgentModeUsageDto { ModeId = node.ModeId, ModeSlug = modes.GetValueOrDefault(node.ModeId)?.Slug ?? "missing-mode", NodeKey = node.NodeKey }).ToArray()
            });
        }
        return Results.Ok(result.OrderBy(x => x.Status == "missing" ? 0 : 1).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase));
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
            SystemPrompt = el.TryGetProperty("system_prompt", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null,
            Description = el.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString() : null,
            SourceKind = "custom", SourceKey = Slug(slug, name!), Managed = false,
            Enabled = !el.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False,
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
    static async Task<IResult> UpdateAgentDraft(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct);
        var exp = IfMatch(req);
        var (t, w, p) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var rec = await db.AgentDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == t && x.WorkspaceId == w, ct);
        if (rec is null) return Results.NotFound(new { code = "not_found" });
        if (await packs.FindManagedResourceAsync("agent", id, ct) is { } managed) return ManagedReadOnly(managed);
        if (rec.Status == "archived") return Results.Conflict(new { code = "conflict", message = "Archived agent cannot be edited" });
        if (exp >= 0 && rec.Revision != exp) return Results.Json(new { code = "conflict", message = "Revision conflict" }, statusCode: 412);
        if (el.TryGetProperty("layer", out var l)) { AgentConfigurationService.ValidateLayer(l.GetString()!); rec.Layer = l.GetString()!.Trim().ToLowerInvariant(); }
        if (el.TryGetProperty("display_name", out var n)) rec.DisplayName = n.GetString() ?? rec.DisplayName;
        if (el.TryGetProperty("name", out var n2) && string.IsNullOrWhiteSpace(rec.DisplayName)) rec.DisplayName = n2.GetString() ?? rec.DisplayName;
        if (el.TryGetProperty("role", out var r)) rec.Role = r.GetString() ?? rec.Role;
        if (el.TryGetProperty("capabilities", out var c)) rec.CapabilitiesJson = c.GetRawText();
        if (el.TryGetProperty("model_strategy", out var ms)) { var err = ValidateModelStrategy(ms.GetRawText()); if (err is not null) return Results.BadRequest(new { code = "invalid_request", message = err }); rec.ModelStrategyJson = ms.GetRawText(); }
        if (el.TryGetProperty("tool_scope", out var ts)) rec.ToolScopeJson = ts.GetRawText();
        if (el.TryGetProperty("allowed_tools", out var at2)) rec.ToolScopeJson = at2.GetRawText();
        if (el.TryGetProperty("system_prompt", out var sp)) rec.SystemPrompt = sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
        if (el.TryGetProperty("description", out var de)) rec.Description = de.ValueKind == JsonValueKind.String ? de.GetString() : null;
        if (el.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False) rec.Enabled = en.ValueKind == JsonValueKind.True;
        rec.Revision++; rec.UpdatedAt = DateTimeOffset.UtcNow; rec.UpdatedByPrincipalId = p; rec.Status = "draft";
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Results.Json(new { code = "conflict", message = "Concurrent update" }, statusCode: 412); }
        return Results.Ok(ToAgentDto(rec));
    }
    static async Task<IResult> PublishAgent(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var exp = IfMatch(req);
        var (t, w, p) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var rec = await db.AgentDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == t && x.WorkspaceId == w, ct);
        if (rec is null) return Results.NotFound(new { code = "not_found" });
        if (await packs.FindManagedResourceAsync("agent", id, ct) is { } managed) return ManagedReadOnly(managed);
        if (exp >= 0 && rec.Revision != exp) return Results.Json(new { code = "conflict", message = "Revision conflict" }, statusCode: 412);
        if (rec.Status == "archived") return Results.Conflict(new { code = "conflict", message = "Archived" });
        if (!string.Equals(rec.Status, "draft", StringComparison.OrdinalIgnoreCase)) return Results.Conflict(new { code = "not_draft", message = "Only a draft agent can be published." });
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
    static async Task<IResult> ArchiveAgent(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var exp = IfMatch(req);
        var (t, w, _) = Ctx(a);
        await using var db = await f.CreateDbContextAsync(ct);
        var rec = await db.AgentDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == t && x.WorkspaceId == w, ct);
        if (rec is null) return Results.NotFound(new { code = "not_found" });
        if (await packs.FindManagedResourceAsync("agent", id, ct) is { } managed) return ManagedReadOnly(managed);
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
    static async Task<IResult> ListModes(IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, HttpRequest req, CancellationToken ct)
    {
        var (t,w,_) = Ctx(a);
        var status = req.Query["status"].ToString();
        var hasApplicationFilter = req.Query.ContainsKey("application_mode") || req.Query.ContainsKey("applicationMode");
        var applicationMode = req.Query["application_mode"].ToString();
        if (string.IsNullOrWhiteSpace(applicationMode)) applicationMode = req.Query["applicationMode"].ToString();
        applicationMode = AgentRuntimeConfigurationSnapshot.NormalizeApplicationMode(applicationMode);
        if (hasApplicationFilter && applicationMode is not ("conversation" or "space"))
            return Results.BadRequest(new { code = "UNKNOWN_APPLICATION_MODE", message = $"Application mode '{applicationMode}' is not configured." });

        await using var db = await f.CreateDbContextAsync(ct);
        var query = db.AgentModes.Where(x => x.TenantId == t && x.WorkspaceId == w
            && (string.IsNullOrEmpty(status) || x.Status == status));
        if (hasApplicationFilter)
            query = applicationMode == "conversation"
                ? query.Where(x => x.Slug.StartsWith("conversation."))
                : query.Where(x => !x.Slug.StartsWith("conversation."));
        var list = (await query.ToListAsync(ct)).OrderByDescending(x => x.UpdatedAt).ToList();
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
static async Task<IResult> GetMode(Guid id, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if (string.Equals(rec.Status, "archived", StringComparison.OrdinalIgnoreCase)) return Results.NotFound(new{code="not_found", message="Mode is archived."});
        // Read returns the current topology for any live status: draft edits read the
        // draft projection; published/managed modes read the published projection. The
        // draft-only guard belongs on writes (update/publish), not on this read.
        var isDraft = string.Equals(rec.Status, "draft", StringComparison.OrdinalIgnoreCase);
        var nodeStatus = isDraft ? "draft" : "published";
        var nodes=await db.ModeNodes.Where(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w && x.Status==nodeStatus).ToListAsync(ct);
        var edges=await db.ModeEdges.Where(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w && x.Status==nodeStatus).ToListAsync(ct);
        var layout=await db.CanvasLayouts.FirstOrDefaultAsync(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w && x.Status==nodeStatus, ct);
        var managed = await packs.FindManagedResourceAsync("mode", id, ct) is not null;
        return Results.Ok(new{ id=rec.Id, slug=rec.Slug, display_name=rec.DisplayName, description=rec.Description, status=rec.Status, revision=rec.Revision, version=rec.Version, managed, nodes=nodes.Select(n=>new{ id=n.Id, node_key=n.NodeKey, agent_definition_id=n.AgentDefinitionId, layer=n.Layer, label=n.Label, position= n.PositionJson!=null? JsonSerializer.Deserialize<JsonElement>(n.PositionJson): (JsonElement?)null, config= n.ConfigJson!=null? JsonSerializer.Deserialize<JsonElement>(n.ConfigJson): (JsonElement?)null, model_strategy_override = n.ModelStrategyOverrideJson!=null ? JsonSerializer.Deserialize<JsonElement>(n.ModelStrategyOverrideJson) : (JsonElement?)null}), edges=edges.Select(e=>new{ id=e.Id, edge_key=e.EdgeKey, source_node_key=e.SourceNodeKey, target_node_key=e.TargetNodeKey}), canvas_layout= layout!=null? JsonSerializer.Deserialize<JsonElement>(layout.LayoutJson): (JsonElement?)null, created_at=rec.CreatedAt, updated_at=rec.UpdatedAt});
    }

    static ModelResolutionPreviewDto UnavailablePreview(string reason) => new()
    {
        StrategySource = "unavailable",
        Candidates =
        [
            new ModelResolutionCandidatePreviewDto
            {
                Available = false,
                UnavailableReason = reason
            }
        ]
    };
    static async Task<IResult> UpdateModeDraft(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var el=await JsonSerializer.DeserializeAsync<JsonElement>(req.Body,cancellationToken:ct);
        var exp=IfMatch(req); var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if (await packs.FindManagedResourceAsync("mode", id, ct) is { } managed) return ManagedReadOnly(managed);
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict",message="Revision conflict"},statusCode:412);
        if(!string.Equals(rec.Status, "draft", StringComparison.OrdinalIgnoreCase)) return Results.Conflict(new{code="not_draft",message="Only a draft mode can be published."});
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
                    var modelOverride = n.TryGetProperty("model_strategy_override", out var overrideValue) ? overrideValue.GetRawText() : null;
                    if (modelOverride is not null && ValidateModelStrategy(modelOverride) is { } error) throw new ArgumentException(error);
                    db.ModeNodes.Add(new ModeNodeRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, ModeId=mode.Id, NodeKey=key!, AgentDefinitionId=agentId, Layer=layer, Label=n.TryGetProperty("label",out var lab)?lab.GetString():null, PositionJson=n.TryGetProperty("position",out var pos)?pos.GetRawText(): n.TryGetProperty("position_json",out var pj)?pj.GetRawText():null, ConfigJson=n.TryGetProperty("config",out var cfg)?cfg.GetRawText():null, ModelStrategyOverrideJson=modelOverride, Status="draft", Revision=1, CreatedAt=DateTimeOffset.UtcNow, UpdatedAt=DateTimeOffset.UtcNow});
                } else {
                    if(n.TryGetProperty("agent_definition_id",out var aid3) && Guid.TryParse(aid3.GetString(),out var g3)) existing.AgentDefinitionId=g3;
                    if(n.TryGetProperty("layer",out var l2)) existing.Layer=l2.GetString()??existing.Layer;
                    if(n.TryGetProperty("label",out var lab2)) existing.Label=lab2.GetString();
                    if(n.TryGetProperty("position",out var pos2)) existing.PositionJson=pos2.GetRawText();
                    if(n.TryGetProperty("config",out var cfg2)) existing.ConfigJson=cfg2.GetRawText();
                    if(n.TryGetProperty("model_strategy_override",out var overrideValue)) { var error=ValidateModelStrategy(overrideValue.GetRawText()); if(error is not null) throw new ArgumentException(error); existing.ModelStrategyOverrideJson=overrideValue.ValueKind==JsonValueKind.Null?null:overrideValue.GetRawText(); }
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
    static async Task<IResult> PublishMode(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var exp=IfMatch(req); var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if (await packs.FindManagedResourceAsync("mode", id, ct) is { } managed) return ManagedReadOnly(managed);
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict",message="Revision conflict"},statusCode:412);
        var nodes=await db.ModeNodes.Where(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w).ToListAsync(ct);
        var edges=await db.ModeEdges.Where(x=>x.ModeId==id && x.TenantId==t && x.WorkspaceId==w).ToListAsync(ct);
        // dual-lane validation
        var agentDefinitions = await db.AgentDefinitions.AsNoTracking().Where(x => nodes.Select(n => n.AgentDefinitionId).Contains(x.Id) && x.TenantId == t && x.WorkspaceId == w).ToDictionaryAsync(x => x.Id, ct);
        var opCount=nodes.Count(n=>n.Layer=="operation" && agentDefinitions.TryGetValue(n.AgentDefinitionId, out var definition) && definition.Slug=="meeting");
        var exCount=nodes.Count(n=>n.Layer=="execution");
        if(opCount==0) return Results.BadRequest(new{code="invalid_request",message="Mode must have at least one operation agent (meeting)"});
        if(exCount==0) return Results.BadRequest(new{code="invalid_request",message="Mode must have at least one execution agent"});
        // cross-layer reuse warning
        var byAgent=nodes.GroupBy(n=>n.AgentDefinitionId).Where(g=>g.Select(x=>x.Layer).Distinct().Count()>1).Select(g=>g.Key).ToArray();
        var warnings = new List<object>();
        if(byAgent.Length>0) warnings.Add(new{ code="CROSS_LAYER_REUSE", message="Same agent reused across operation and execution", agent_ids=byAgent });
        // Freeze exact AgentVersion/PromptVersion bindings. Runtime consumes only this snapshot.
        var frozenNodes = new List<object>();
        foreach(var node in nodes.OrderBy(n => n.NodeKey, StringComparer.Ordinal))
        {
            if (!agentDefinitions.TryGetValue(node.AgentDefinitionId, out var agentDef) || agentDef.Status != "published")
                return Results.BadRequest(new{code="invalid_request",message=$"Node {node.NodeKey} must reference a published agent."});
            var agentVersion = await db.AgentVersions.AsNoTracking().Where(x => x.AgentDefinitionId == agentDef.Id && x.Status == "published").OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
            if (agentVersion is null || string.IsNullOrWhiteSpace(agentVersion.ContentHash))
                return Results.BadRequest(new{code="invalid_request",message=$"Node {node.NodeKey} has no published immutable AgentVersion."});
            var agentTools = ParseTools(agentDef.ToolScopeJson);
            var modeTools = ParseTools(node.ConfigJson);
            HashSet<string> effective;
            if (agentTools.Contains("*")) effective = modeTools.Count>0 ? new HashSet<string>(modeTools, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase){ "*"};
            else if (modeTools.Count==0) effective = new HashSet<string>(agentTools, StringComparer.OrdinalIgnoreCase);
            else effective = new HashSet<string>(agentTools.Intersect(modeTools, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (effective.Count==0)
                warnings.Add(new{ code="EMPTY_EFFECTIVE_TOOLS", message=$"Node {node.NodeKey} has no effective tools after intersection", node_key=node.NodeKey, agent_id=node.AgentDefinitionId });
            PromptVersionRecord? promptVersion = null;
            if (agentDef.BasePromptPipelineId is { } promptPipelineId)
                promptVersion = await db.PromptVersions.AsNoTracking().Where(x => x.PromptPipelineId == promptPipelineId && x.Status == "published").OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
            JsonElement config;
            try { config = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(node.ConfigJson) ? "{}" : node.ConfigJson); }
            catch { return Results.BadRequest(new{code="invalid_request",message=$"Node {node.NodeKey} config is invalid JSON."}); }
            JsonElement? modelStrategyOverride = null;
            if (!string.IsNullOrWhiteSpace(node.ModelStrategyOverrideJson))
            {
                var strategyError = ValidateModelStrategy(node.ModelStrategyOverrideJson);
                if (strategyError is not null) return Results.BadRequest(new{code="invalid_request",message=$"Node {node.NodeKey}: {strategyError}"});
                modelStrategyOverride = JsonSerializer.Deserialize<JsonElement>(node.ModelStrategyOverrideJson);
            }
            frozenNodes.Add(new
            {
                node_key = node.NodeKey,
                agent_definition_id = agentDef.Id,
                agent_version_id = agentVersion.Id,
                agent_version_hash = agentVersion.ContentHash,
                layer = node.Layer,
                label = node.Label,
                config,
                model_strategy_override = modelStrategyOverride,
                model_strategy_source = modelStrategyOverride is null ? "agent_version" : "mode_node_override",
                effective_tools = effective.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                prompt_pipeline_id = agentDef.BasePromptPipelineId,
                prompt_version_id = promptVersion?.Id,
                prompt_version_hash = promptVersion?.ContentHash
            });
        }
        string? warning=null;
        if(warnings.Count>0) warning=JsonSerializer.Serialize(warnings);
        var frozenEdges = edges.OrderBy(e => e.EdgeKey, StringComparer.Ordinal).Select(e => new
        {
            edge_key = e.EdgeKey,
            source_node_key = e.SourceNodeKey,
            target_node_key = e.TargetNodeKey,
            condition = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(e.ConditionJson) ? "{}" : e.ConditionJson)
        }).ToArray();
        var layouts = await db.CanvasLayouts.AsNoTracking().Where(x => x.ModeId == id && x.TenantId == t && x.WorkspaceId == w && x.Status == "draft").ToListAsync(ct);
        var layout = layouts.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        var canvasLayout = JsonSerializer.Deserialize<JsonElement>(layout?.LayoutJson ?? "{}");
        var snapshot=JsonSerializer.Serialize(new
        {
            schema = "tinadec.mode_version/v1",
            mode = new { id = rec.Id, slug = rec.Slug, display_name = rec.DisplayName },
            nodes = frozenNodes,
            edges = frozenEdges,
            canvas_layout = canvasLayout
        });
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
        var ver=(await db.ModeVersions.Where(x=>x.AgentModeId==id).MaxAsync(x=>(int?)x.Version, ct)??0)+1;
        db.ModeVersions.Add(new ModeVersionRecord{ Id=Guid.NewGuid(), TenantId=t, WorkspaceId=w, AgentModeId=id, Version=ver, SnapshotJson=snapshot, TopologyHash=hash, WarningJson=warning, Status="published", Revision=1, CreatedAt=DateTimeOffset.UtcNow, CreatedByPrincipalId=p});
        rec.Version=ver; rec.Revision++; rec.UpdatedAt=DateTimeOffset.UtcNow; rec.Status="published";
        await db.SaveChangesAsync(ct);
        return Results.Ok(new{ id=rec.Id, version=ver, revision=rec.Revision, topology_hash=hash, warning= warning!=null? JsonSerializer.Deserialize<JsonElement>(warning): (JsonElement?)null });
    }
    static async Task<IResult> ArchiveMode(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var exp=IfMatch(req); var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.AgentModes.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if (await packs.FindManagedResourceAsync("mode", id, ct) is { } managed) return ManagedReadOnly(managed);
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
        var list=await db.PromptPipelines.Where(x=>x.TenantId==t && x.WorkspaceId==w && (string.IsNullOrEmpty(q)||x.Status==q)).ToListAsync(ct);
        list=list.OrderByDescending(x=>x.UpdatedAt).ToList();
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
    static async Task<IResult> UpdatePipelineDraft(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var el=await JsonSerializer.DeserializeAsync<JsonElement>(req.Body,cancellationToken:ct);
        var exp=IfMatch(req); var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.PromptPipelines.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if (await packs.FindManagedResourceAsync("prompt_pipeline", id, ct) is { } managed) return ManagedReadOnly(managed);
        if(exp>=0 && rec.Revision!=exp) return Results.Json(new{code="conflict"},statusCode:412);
        if(!string.Equals(rec.Status, "draft", StringComparison.OrdinalIgnoreCase)) return Results.Conflict(new{code="not_draft",message="Only a draft prompt pipeline can be published."});
        if(el.TryGetProperty("display_name",out var n)) rec.DisplayName=n.GetString()??rec.DisplayName;
        if(el.TryGetProperty("description",out var d)) rec.Description=d.GetString();
        if(el.TryGetProperty("graph",out var g)) { try{JsonDocument.Parse(g.GetRawText());}catch{return Results.BadRequest(new{code="invalid_request",message="graph invalid"});} var err=ValidatePromptGraph(g.GetRawText()); if(err is not null) return Results.BadRequest(new{code="invalid_request",message=err}); rec.GraphJson=g.GetRawText();}
        if(el.TryGetProperty("graph_json",out var gj)) { try{JsonDocument.Parse(gj.GetRawText());}catch{return Results.BadRequest(new{code="invalid_request",message="graph invalid"});} var err2=ValidatePromptGraph(gj.GetRawText()); if(err2 is not null) return Results.BadRequest(new{code="invalid_request",message=err2}); rec.GraphJson=gj.GetRawText();}
        rec.Revision++; rec.UpdatedAt=DateTimeOffset.UtcNow; rec.UpdatedByPrincipalId=p; rec.Status="draft";
        await db.SaveChangesAsync(ct); return Results.Ok(new{ id=rec.Id, slug=rec.Slug, display_name=rec.DisplayName, graph=JsonSerializer.Deserialize<JsonElement>(rec.GraphJson), status=rec.Status, revision=rec.Revision});
    }
    static async Task<IResult> PublishPipeline(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var exp=IfMatch(req); var (t,w,p)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.PromptPipelines.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if (await packs.FindManagedResourceAsync("prompt_pipeline", id, ct) is { } managed) return ManagedReadOnly(managed);
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
    static async Task<IResult> ArchivePipeline(Guid id, HttpRequest req, IDbContextFactory<AgentConfigurationDbContext> f, ITenantContextAccessor a, IAgentPackService packs, CancellationToken ct)
    {
        var exp=IfMatch(req); var (t,w,_)=Ctx(a); await using var db=await f.CreateDbContextAsync(ct);
        var rec=await db.PromptPipelines.FirstOrDefaultAsync(x=>x.Id==id && x.TenantId==t && x.WorkspaceId==w, ct);
        if(rec is null) return Results.NotFound(new{code="not_found"});
        if (await packs.FindManagedResourceAsync("prompt_pipeline", id, ct) is { } managed) return ManagedReadOnly(managed);
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

    // ── instances ──
    static async Task<IResult> ListInstances(
        HttpRequest req,
        IAgentInstanceService instances,
        IDbContextFactory<AgentConfigurationDbContext> agentFactory,
        IDbContextFactory<LifecycleDbContext> lifecycleFactory,
        ITenantContextAccessor tenant,
        CancellationToken ct)
    {
        Guid? runId = null;
        var runText = req.Query["run_id"].ToString();
        if (!string.IsNullOrWhiteSpace(runText))
        {
            if (!Guid.TryParse(runText, out var parsedRunId) || parsedRunId == Guid.Empty)
                return Results.BadRequest(new { code = "INVALID_RUN_ID", message = "run_id must be a valid Guid." });
            runId = parsedRunId;
        }

        var instancesForRun = runId is { } selectedRun
            ? await instances.ListByRunAsync(selectedRun, ct).ConfigureAwait(false)
            : await instances.ListByRunAsync(Guid.Empty, ct).ConfigureAwait(false);
        if (runId is null)
        {
            // The service's run-scoped method intentionally remains the single
            // lifecycle access point. For the directory view, query all visible
            // instance rows through the service's implementation helper below.
            instancesForRun = await instances.ListAllAsync(ct).ConfigureAwait(false);
        }
        var definitionIds = instancesForRun.Select(x => x.AgentDefinitionId).Distinct().ToArray();
        var scope = tenant.Current;
        await using var agents = await agentFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var definitions = await agents.AgentDefinitions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && definitionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        await using var lifecycle = await lifecycleFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var invocationsQuery = lifecycle.ModelInvocations.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId);
        if (runId is { } selectedRunForInvocations)
            invocationsQuery = invocationsQuery.Where(x => x.RunId == selectedRunForInvocations);
        var invocations = (await invocationsQuery.ToListAsync(ct).ConfigureAwait(false))
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).ToList();
        return Results.Ok(instancesForRun.Select(instance =>
        {
            definitions.TryGetValue(instance.AgentDefinitionId, out var definition);
            var latest = invocations.FirstOrDefault(x => x.AgentInstanceId == instance.Id && x.Status == "succeeded");
            var attempts = latest is null
                ? Array.Empty<ModelInvocationRecord>()
                : invocations.Where(x => x.CallId == latest.CallId).OrderBy(x => x.Attempt).ToArray();
            return new
            {
                id = instance.Id,
                run_id = instance.RunId,
                parent_instance_id = instance.ParentInstanceId,
                task_id = instance.TaskId,
                layer = instance.Layer,
                role = instance.Role,
                generation_depth = instance.GenerationDepth,
                generated = instance.Generated,
                status = instance.Status,
                capabilities = instance.Capabilities,
                allowed_tools = instance.AllowedTools,
                allowed_resources = instance.AllowedResources,
                budget_tokens = instance.BudgetTokens,
                source_definition = definition is null ? null : new
                {
                    id = definition.Id, slug = definition.Slug, display_name = definition.DisplayName,
                    source_kind = definition.SourceKind, source_key = definition.SourceKey, managed = definition.Managed
                },
                frozen_version = new { agent_version_id = instance.AgentVersionId, content_hash = instance.AgentVersionContentHash },
                recent_actual_model = latest is null ? null : new
                {
                    invocation_id = latest.Id, provider_instance_id = latest.ProviderInstanceId,
                    provider_version_id = latest.ProviderVersionId, model = latest.Model, protocol = latest.Protocol,
                    route_id = latest.RouteId, route_version_id = latest.RouteVersionId,
                    mode_version_id = latest.ModeVersionId, strategy_source = latest.StrategySource,
                    fallback_position = latest.FallbackPosition, completed_at = latest.CompletedAt
                },
                fallback_summary = latest is null ? null : new
                {
                    call_id = latest.CallId, attempts = attempts.Length,
                    failed_attempts = attempts.Count(x => x.Status == "failed"),
                    used_fallback = latest.FallbackPosition > 0 || attempts.Length > 1
                },
                links = new
                {
                    agent_definition_id = instance.AgentDefinitionId,
                    mode_version_id = latest?.ModeVersionId
                },
                created_at = instance.CreatedAt,
                updated_at = instance.UpdatedAt
            };
        }));
    }

    static object ToInstanceDto(RuntimeAgentInstance instance) => new
    {
        id = instance.Id,
        run_id = instance.RunId,
        parent_instance_id = instance.ParentInstanceId,
        task_id = instance.TaskId,
        layer = instance.Layer,
        role = instance.Role,
        generation_depth = instance.GenerationDepth,
        generated = instance.Generated,
        status = instance.Status,
        capabilities = instance.Capabilities,
        allowed_tools = instance.AllowedTools,
        allowed_resources = instance.AllowedResources,
        budget_tokens = instance.BudgetTokens,
        agent_definition_id = instance.AgentDefinitionId,
        agent_version_id = instance.AgentVersionId,
        agent_version_content_hash = instance.AgentVersionContentHash,
        created_at = instance.CreatedAt,
        updated_at = instance.UpdatedAt
    };

    static string? ValidateModelStrategy(string json)
    {
        try
        {
            _ = ModelStrategyJson.Parse(json);
            return null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return exception.Message;
        }
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

    static object ToAgentDto(AgentDefinitionRecord r) => new{ id=r.Id, slug=r.Slug, display_name=r.DisplayName, layer=r.Layer, role=r.Role, source_kind=r.SourceKind, source_key=r.SourceKey, managed=r.Managed, writable=!r.Managed && r.Status!="archived", capabilities= r.CapabilitiesJson!=null? JsonSerializer.Deserialize<JsonElement>(r.CapabilitiesJson): (JsonElement?)null, model_strategy= r.ModelStrategyJson!=null? JsonSerializer.Deserialize<JsonElement>(r.ModelStrategyJson): (JsonElement?)null, tool_scope= r.ToolScopeJson!=null? JsonSerializer.Deserialize<JsonElement>(r.ToolScopeJson): (JsonElement?)null, system_prompt=r.SystemPrompt, description=r.Description, enabled=r.Enabled, status=r.Status, revision=r.Revision, version=r.Version, created_at=r.CreatedAt, updated_at=r.UpdatedAt, archived_at=r.ArchivedAt };

    static ModelInvocationDto ToInvocationDto(ModelInvocationRecord value) => new()
    {
        Id=value.Id, CallId=value.CallId, Attempt=value.Attempt, SessionId=value.SessionId, RunId=value.RunId,
        TurnId=value.TurnId, AgentInstanceId=value.AgentInstanceId, AgentDefinitionId=value.AgentDefinitionId,
        AgentVersionId=value.AgentVersionId, ModeVersionId=value.ModeVersionId, StrategySource=value.StrategySource,
        RouteId=value.RouteId, RouteVersionId=value.RouteVersionId, ProviderInstanceId=value.ProviderInstanceId,
        ProviderVersionId=value.ProviderVersionId, Model=value.Model, Protocol=value.Protocol,
        FallbackPosition=value.FallbackPosition, Status=value.Status, ErrorCategory=value.ErrorCategory,
        SafeErrorMessage=value.SafeErrorMessage, InputTokens=value.InputTokens, OutputTokens=value.OutputTokens,
        TotalTokens=value.TotalTokens, StartedAt=value.StartedAt, CompletedAt=value.CompletedAt
    };
    static object ToModeDto(AgentModeRecord r) => new{ id=r.Id, slug=r.Slug, display_name=r.DisplayName, description=r.Description, status=r.Status, revision=r.Revision, version=r.Version, created_at=r.CreatedAt, updated_at=r.UpdatedAt, archived_at=r.ArchivedAt };

    static IResult ManagedReadOnly(AgentPackManagedResource managed) => throw new AgentPackDomainException(
        StatusCodes.Status409Conflict,
        "managed_resource_read_only",
        "Pack-managed resource is read-only",
        $"Pack '{managed.PackId}' manages {managed.ResourceKind} '{managed.ResourceKey}' ({managed.LogicalEntityId}). Clone it before customizing.");
}
