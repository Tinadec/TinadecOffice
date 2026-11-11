using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tools;

namespace TinadecCore.Runtime;

/// <summary>
/// Executes the <c>tinadec_*</c> workbench-configuration tools against the live Core
/// services. It is the only implementation of that gateway: the HTTP endpoints and
/// these tools share <see cref="IAgentTopologyService"/>, so a graph saved by a model
/// is the same graph saved by the editor, with the same validation and the same
/// publish gate.
///
/// Rules that hold for every action here:
/// <list type="bullet">
/// <item>Scope comes from the run (<see cref="ToolInvocationScope"/>), never from model
/// arguments — a caller cannot name another tenant, workspace or session.</item>
/// <item>Pack-managed resources are read-only and answer <c>built_in_read_only</c>;
/// cloning is how a caller gets an editable copy.</item>
/// <item>Results are sanitized and bounded: no secrets, no environment values, no skill
/// bodies, at most <see cref="MaxRows"/> rows.</item>
/// <item>Writes keep their governance (approval on the tool call; skill and MCP writes
/// also go through the services' own governed paths).</item>
/// </list>
/// </summary>
public sealed class TinadecConfigToolGateway : ITinadecConfigToolGateway
{
    private const int MaxRows = 50;
    private const int MaxText = 240;

    private readonly IAgentTopologyService _topology;
    private readonly IAgentPackService _packs;
    private readonly IDbContextFactory<AgentConfigurationDbContext> _agents;
    private readonly IToolSkillResourceService? _skills;
    private readonly IMcpResourceRegistry? _mcp;
    private readonly IMarketCatalogService? _market;
    private readonly IToolConfigurationResolver? _toolSettings;
    private readonly IToolProvider? _provider;
    private readonly ISessionLocator? _sessions;
    private readonly IConfiguration? _configuration;

    public TinadecConfigToolGateway(
        IAgentTopologyService topology,
        IAgentPackService packs,
        IDbContextFactory<AgentConfigurationDbContext> agents,
        IToolSkillResourceService? skills = null,
        IMcpResourceRegistry? mcp = null,
        IMarketCatalogService? market = null,
        IToolConfigurationResolver? toolSettings = null,
        IToolProvider? provider = null,
        ISessionLocator? sessions = null,
        IConfiguration? configuration = null)
    {
        _topology = topology;
        _packs = packs;
        _agents = agents;
        _skills = skills;
        _mcp = mcp;
        _market = market;
        _toolSettings = toolSettings;
        _provider = provider;
        _sessions = sessions;
        _configuration = configuration;
    }

    public async Task<ToolWireResponseDto> ExecuteAsync(
        string toolId,
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken ct)
    {
        try
        {
            if (CoreVirtualToolPolicy.IsTinadecInspect(toolId)) return await InspectAsync(scope, wire, ct).ConfigureAwait(false);
            if (string.Equals(toolId, CoreVirtualToolPolicy.TinadecAgentApplyToolId, StringComparison.OrdinalIgnoreCase)) return await AgentApplyAsync(scope, wire, ct).ConfigureAwait(false);
            if (string.Equals(toolId, CoreVirtualToolPolicy.TinadecModeApplyToolId, StringComparison.OrdinalIgnoreCase)) return await ModeApplyAsync(scope, wire, ct).ConfigureAwait(false);
            if (string.Equals(toolId, CoreVirtualToolPolicy.TinadecModePublishToolId, StringComparison.OrdinalIgnoreCase)) return await ModePublishAsync(scope, wire, ct).ConfigureAwait(false);
            if (string.Equals(toolId, CoreVirtualToolPolicy.TinadecSkillApplyToolId, StringComparison.OrdinalIgnoreCase)) return await SkillApplyAsync(scope, wire, ct).ConfigureAwait(false);
            if (string.Equals(toolId, CoreVirtualToolPolicy.TinadecMcpApplyToolId, StringComparison.OrdinalIgnoreCase)) return await McpApplyAsync(scope, wire, ct).ConfigureAwait(false);
            return Fail(wire, "unknown_tool", $"No workbench configuration tool named '{toolId}'.");
        }
        catch (ToolSettingsException exception)
        {
            return Fail(wire, exception.Code, exception.Message);
        }
        catch (MarketCatalogException exception)
        {
            return Fail(wire, exception.Code, exception.Message);
        }
        catch (DbUpdateException exception)
        {
            return Fail(wire, "conflict", exception.InnerException?.Message ?? exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Fail(wire, "invalid_request", exception.Message);
        }
    }

    // ── tinadec_inspect ──

    private async Task<ToolWireResponseDto> InspectAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        if (parameters.ValueKind != JsonValueKind.Object)
            return Fail(wire, "invalid_request", "tinadec_inspect needs an object with 'target'.");
        var target = (Text(parameters, "target") ?? string.Empty).Trim().ToLowerInvariant();
        var query = Text(parameters, "query");
        var limit = Math.Clamp(ReadInt(parameters, "limit", 20), 1, MaxRows);
        switch (target)
        {
            case "agents":
                return Ok(wire, await InspectAgentsAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "catalog":
                return Ok(wire, await InspectCatalogAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "modes":
                return Ok(wire, await InspectModesAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "mode":
            {
                if ((GuidOf(parameters, "mode_id") ?? GuidOf(parameters, "id")) is not { } modeId)
                    return Fail(wire, "invalid_request", "target=mode needs mode_id.");
                var result = await _topology.GetAsync(modeId, ct).ConfigureAwait(false);
                return result.Ok ? Ok(wire, new { ok = true, mode = result.Mode }) : TopologyFailure(wire, result);
            }
            case "skills":
                return Ok(wire, await InspectSkillsAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "mcp":
                return Ok(wire, await InspectMcpAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "market":
                return Ok(wire, await InspectMarketAsync(query, limit, ct).ConfigureAwait(false));
            default:
                return Fail(wire, "invalid_request", "target must be one of agents, modes, mode, catalog, skills, mcp, market.");
        }
    }

    private async Task<object> InspectAgentsAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.AgentDefinitions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(ct).ConfigureAwait(false);
        var ownership = await PackOwnershipAsync(db, scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        var filtered = rows.Where(x => Matches(query, x.Slug, x.DisplayName, x.Description))
            .OrderBy(x => x.Layer, StringComparer.Ordinal).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            target = "agents",
            total = filtered.Length,
            truncated = filtered.Length > limit,
            items = filtered.Take(limit).Select(x =>
            {
                ownership.TryGetValue(x.Id, out var owner);
                var builtIn = owner.PackId is not null;
                return new
                {
                    id = x.Id,
                    slug = x.Slug,
                    display_name = x.DisplayName,
                    layer = x.Layer,
                    role = x.Role,
                    status = x.Status,
                    enabled = x.Enabled,
                    source_kind = x.SourceKind,
                    version = x.Version,
                    revision = x.Revision,
                    pack_id = owner.PackId,
                    built_in = builtIn,
                    writable = !builtIn && !string.Equals(x.Status, "archived", StringComparison.Ordinal),
                    capabilities = ParseStringArray(x.CapabilitiesJson),
                    tool_count = ParseStringArray(x.ToolScopeJson).Count,
                    description = Truncate(x.Description, MaxText),
                };
            }).ToArray(),
        };
    }

    private async Task<object> InspectCatalogAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var agents = await db.AgentDefinitions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status != "archived")
            .ToListAsync(ct).ConfigureAwait(false);
        var pipelines = await db.PromptPipelines.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(ct).ConfigureAwait(false);
        var matchedAgents = agents.Where(x => Matches(query, x.Slug, x.DisplayName)).ToArray();
        var matchedPipelines = pipelines.Where(x => Matches(query, x.Slug, x.DisplayName)).ToArray();
        return new
        {
            target = "catalog",
            agents = matchedAgents.Take(limit).Select(x => new
            {
                id = x.Id,
                slug = x.Slug,
                display_name = x.DisplayName,
                layer = x.Layer,
                status = x.Status,
                capabilities = ParseStringArray(x.CapabilitiesJson),
                tool_ids = ParseStringArray(x.ToolScopeJson).Take(MaxRows).ToArray(),
            }).ToArray(),
            agents_truncated = matchedAgents.Length > limit,
            prompt_pipelines = matchedPipelines.Take(limit).Select(x => new
            {
                id = x.Id,
                slug = x.Slug,
                display_name = x.DisplayName,
                status = x.Status,
                version = x.Version,
            }).ToArray(),
            prompt_pipelines_truncated = matchedPipelines.Length > limit,
            note = "A mode node needs an agent id from 'agents'; edges connect node keys.",
        };
    }

    private async Task<object> InspectModesAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.AgentModes.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(ct).ConfigureAwait(false);
        var ownership = await PackOwnershipAsync(db, scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        var counts = await db.ModeNodes.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .GroupBy(x => x.ModeId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        var filtered = rows.Where(x => Matches(query, x.Slug, x.DisplayName, x.Description))
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            target = "modes",
            total = filtered.Length,
            truncated = filtered.Length > limit,
            items = filtered.Take(limit).Select(x =>
            {
                ownership.TryGetValue(x.Id, out var owner);
                var builtIn = owner.PackId is not null;
                return new
                {
                    id = x.Id,
                    slug = x.Slug,
                    display_name = x.DisplayName,
                    description = Truncate(x.Description, MaxText),
                    status = x.Status,
                    revision = x.Revision,
                    version = x.Version,
                    node_count = counts.GetValueOrDefault(x.Id),
                    pack_id = owner.PackId,
                    built_in = builtIn,
                    writable = !builtIn && string.Equals(x.Status, "draft", StringComparison.Ordinal),
                };
            }).ToArray(),
        };
    }

    private async Task<object> InspectSkillsAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        if (_skills is null)
            return new { target = "skills", available = false, total = 0, items = Array.Empty<object>(), note = "No skill catalog is registered in this host." };
        var catalog = await _skills.ListAsync(ProjectOf(scope), includeBuiltins: true, ct).ConfigureAwait(false);
        var filtered = catalog.Skills.Where(x => Matches(query, x.Name, x.Description))
            .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        return new
        {
            target = "skills",
            available = true,
            total = filtered.Length,
            truncated = filtered.Length > limit,
            items = filtered.Take(limit).Select(x => new
            {
                resource_id = x.ResourceId,
                name = x.Name,
                description = Truncate(x.Description, MaxText),
                scope = x.Scope,
                source = x.SourceKind,
                enabled = x.Enabled,
                valid = x.Valid,
                read_only = x.ReadOnly,
                rank = x.Rank,
                shadowed_by = x.ShadowedBy,
                when_to_use = Truncate(x.WhenToUse, MaxText),
                reason_code = x.ReasonCode,
            }).ToArray(),
            diagnostics = catalog.Diagnostics.Take(10).Select(value => Truncate(value, MaxText)).ToArray(),
        };
    }

    private async Task<object> InspectMcpAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        if (_mcp is null)
            return new { target = "mcp", available = false, total = 0, items = Array.Empty<object>(), note = "No MCP registry is registered in this host." };
        var resources = await _mcp.ListAsync(ProjectOf(scope), ct).ConfigureAwait(false);
        var filtered = resources.Where(x => Matches(query, x.Id, x.Name, x.Command))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            target = "mcp",
            available = true,
            total = filtered.Length,
            truncated = filtered.Length > limit,
            items = filtered.Take(limit).Select(x => new
            {
                resource_id = x.ResourceId,
                id = x.Id,
                name = x.Name,
                enabled = x.Enabled,
                command = x.Command,
                args = x.Args.Take(MaxRows).ToArray(),
                // Names only: values are secret references and never leave Core.
                env_keys = (x.Env?.Keys ?? Enumerable.Empty<string>()).OrderBy(key => key, StringComparer.Ordinal).Take(MaxRows).ToArray(),
                cwd = x.Cwd,
                project_id = x.ProjectId,
                revision = x.Revision,
                import_source = x.ImportSource,
            }).ToArray(),
        };
    }

    private async Task<object> InspectMarketAsync(string? query, int limit, CancellationToken ct)
    {
        if (_market is null)
            return new { target = "market", available = false, total = 0, items = Array.Empty<object>(), note = "No market catalog is registered in this host." };
        var sources = await _market.ListSourcesAsync(ct).ConfigureAwait(false);
        var page = await _market.ListCatalogAsync(new MarketCatalogQuery(Search: query, Limit: limit), ct).ConfigureAwait(false);
        return new
        {
            target = "market",
            available = true,
            sources = sources.Take(MaxRows).Select(x => new { id = x.Id, name = x.Name, kind = x.Kind, enabled = x.Enabled }).ToArray(),
            total = page.TotalAvailable,
            truncated = page.HasMore,
            items = page.Items.Take(limit).Select(x => new
            {
                catalog_id = x.CatalogId,
                extension_id = x.ExtensionId,
                kind = x.Kind,
                display_name = x.DisplayName,
                version = x.Version,
                description = Truncate(x.Description, MaxText),
                source_name = x.SourceName,
                transports = x.Transports,
                installable = x.Installable,
                install_blocker_code = x.InstallBlockerCode,
                homepage = x.Homepage,
            }).ToArray(),
        };
    }

    // ── tinadec_agent_apply ──

    private async Task<ToolWireResponseDto> AgentApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case "create":
            {
                var name = Text(payload, "display_name") ?? Text(payload, "name");
                if (string.IsNullOrWhiteSpace(name))
                    return Fail(wire, "invalid_request", "action=create needs payload.display_name.");
                var layer = (Text(payload, "layer") ?? "operation").Trim().ToLowerInvariant();
                if (ValidateLayer(layer) is { } layerError) return Fail(wire, "invalid_layer", layerError);
                if (ModelStrategyError(payload) is { } strategyError) return Fail(wire, "invalid_model_strategy", strategyError);
                var slug = await UniqueDraftSlugAsync(db, Text(payload, "slug") ?? name, scope, ct).ConfigureAwait(false);
                var record = new AgentDefinitionRecord
                {
                    Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                    Slug = slug, DisplayName = name.Trim(), Layer = layer,
                    Role = Text(payload, "role") ?? string.Empty,
                    CapabilitiesJson = Raw(payload, "capabilities") ?? "[]",
                    ModelStrategyJson = Raw(payload, "model_strategy"),
                    ToolScopeJson = Raw(payload, "tool_scope") ?? Raw(payload, "allowed_tools") ?? "[]",
                    SystemPrompt = Text(payload, "system_prompt"),
                    Description = Text(payload, "description"),
                    SourceKind = "custom", SourceKey = slug, Managed = false,
                    Enabled = ReadBool(payload, "enabled", true),
                    Status = "draft", Revision = 1, Version = 0, CreatedAt = now, UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.AgentDefinitions.Add(record);
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateException) { return Fail(wire, "duplicate_slug", "A draft agent already uses this slug."); }
                return Ok(wire, new { ok = true, action, agent = DescribeAgent(record, builtIn: false), note = "Saved as a draft; publish it before a new run can use it." });
            }
            case "clone":
            {
                if ((GuidOf(parameters, "agent_id") ?? GuidOf(parameters, "source_id")) is not { } sourceId)
                    return Fail(wire, "invalid_request", "action=clone needs agent_id (the agent to copy).");
                var source = await db.AgentDefinitions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == sourceId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct).ConfigureAwait(false);
                if (source is null) return Fail(wire, "not_found", "Agent not found.");
                if (string.Equals(source.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived agent cannot be cloned.");
                var name = Text(payload, "display_name") ?? $"{source.DisplayName} (copy)";
                var slug = await UniqueDraftSlugAsync(db, Text(payload, "slug") ?? $"{source.Slug}-copy", scope, ct).ConfigureAwait(false);
                var copy = new AgentDefinitionRecord
                {
                    Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                    Slug = slug, DisplayName = name.Trim(), Layer = source.Layer,
                    Role = source.Role, CapabilitiesJson = source.CapabilitiesJson,
                    BasePromptPipelineId = source.BasePromptPipelineId,
                    ModelStrategyJson = source.ModelStrategyJson, ToolScopeJson = source.ToolScopeJson,
                    SystemPrompt = source.SystemPrompt, Description = source.Description,
                    I18nJson = null, SourceKind = "custom", SourceKey = slug, Managed = false,
                    Enabled = source.Enabled, Status = "draft", Revision = 1, Version = 0,
                    CreatedAt = now, UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.AgentDefinitions.Add(copy);
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateException) { return Fail(wire, "duplicate_slug", "A draft agent already uses this slug."); }
                return Ok(wire, new { ok = true, action, agent = DescribeAgent(copy, builtIn: false), source_agent_id = source.Id, note = "A custom draft copy; the source is unchanged." });
            }
            case "update":
            {
                if (GuidOf(parameters, "agent_id") is not { } agentId)
                    return Fail(wire, "invalid_request", "action=update needs agent_id.");
                var record = await db.AgentDefinitions
                    .FirstOrDefaultAsync(x => x.Id == agentId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct).ConfigureAwait(false);
                if (record is null) return Fail(wire, "not_found", "Agent not found.");
                if (await _packs.FindManagedResourceAsync("agent", agentId, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages agent '{managed.ResourceKey}'. Clone it and update the copy.");
                if (string.Equals(record.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived agent cannot be edited.");
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("layer", out var layerElement))
                {
                    var layer = (layerElement.GetString() ?? "operation").Trim().ToLowerInvariant();
                    if (ValidateLayer(layer) is { } layerError) return Fail(wire, "invalid_layer", layerError);
                    record.Layer = layer;
                }
                if (ModelStrategyError(payload) is { } strategyError) return Fail(wire, "invalid_model_strategy", strategyError);
                if (Text(payload, "display_name") is { } displayName && !string.IsNullOrWhiteSpace(displayName)) record.DisplayName = displayName;
                if (Text(payload, "role") is { } role) record.Role = role;
                if (Raw(payload, "capabilities") is { } capabilities) record.CapabilitiesJson = capabilities;
                if (Raw(payload, "model_strategy") is { } modelStrategy) record.ModelStrategyJson = modelStrategy;
                if ((Raw(payload, "tool_scope") ?? Raw(payload, "allowed_tools")) is { } toolScope) record.ToolScopeJson = toolScope;
                if (Text(payload, "system_prompt") is { } systemPrompt) record.SystemPrompt = systemPrompt;
                if (Text(payload, "description") is { } description) record.Description = description;
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    record.Enabled = enabled.ValueKind == JsonValueKind.True;
                record.Status = "draft";
                record.Revision++;
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateConcurrencyException) { return Fail(wire, "conflict", "The agent changed since it was read."); }
                return Ok(wire, new { ok = true, action, agent = DescribeAgent(record, builtIn: false), note = "Saved as a draft; publish it before a new run can use it." });
            }
            default:
                return Fail(wire, "invalid_request", "action must be clone, create or update.");
        }
    }

    // ── tinadec_mode_apply / tinadec_mode_publish ──

    private async Task<ToolWireResponseDto> ModeApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        var expectedRevision = ReadInt(parameters, "expected_revision", -1);
        var modeId = GuidOf(parameters, "mode_id");
        AgentTopologyResult result;
        switch (action)
        {
            case "create":
                result = await _topology.CreateDraftAsync(payload.ValueKind == JsonValueKind.Object ? payload : parameters, ct).ConfigureAwait(false);
                break;
            case "clone":
                if (modeId is null) return Fail(wire, "invalid_request", "action=clone needs mode_id (the mode to copy).");
                result = await _topology.CloneDraftAsync(modeId.Value, payload, ct).ConfigureAwait(false);
                break;
            case "upsert":
                if (modeId is null) return Fail(wire, "invalid_request", "action=upsert needs mode_id.");
                result = await _topology.UpsertDraftAsync(modeId.Value, payload.ValueKind == JsonValueKind.Object ? payload : parameters, expectedRevision, ct).ConfigureAwait(false);
                break;
            case "delete":
                if (modeId is null) return Fail(wire, "invalid_request", "action=delete needs mode_id.");
                result = await _topology.DeleteDraftAsync(modeId.Value, ct).ConfigureAwait(false);
                break;
            default:
                return Fail(wire, "invalid_request", "action must be clone, create, upsert or delete.");
        }
        if (!result.Ok) return TopologyFailure(wire, result);
        return Ok(wire, new
        {
            ok = true,
            action,
            mode = result.Mode,
            mode_id = result.ModeId,
            revision = result.Revision,
            version = result.Version,
            note = action == "delete" ? null : "Saved as a draft; call tinadec_mode_publish when the graph is right.",
        });
    }

    private async Task<ToolWireResponseDto> ModePublishAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        if (GuidOf(parameters, "mode_id") is not { } modeId)
            return Fail(wire, "invalid_request", "tinadec_mode_publish needs mode_id.");
        var result = await _topology.PublishAsync(modeId, ReadInt(parameters, "expected_revision", -1), ct).ConfigureAwait(false);
        if (!result.Ok) return TopologyFailure(wire, result);
        return Ok(wire, new
        {
            ok = true,
            mode_id = result.ModeId,
            version = result.Version,
            revision = result.Revision,
            topology_hash = result.TopologyHash,
            warnings = result.Warnings,
            note = "Published. Runs that were already admitted keep their frozen version; new runs use this one.",
        });
    }

    // ── tinadec_skill_apply ──

    private async Task<ToolWireResponseDto> SkillApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        if (_skills is null) return Fail(wire, "unavailable", "No skill catalog is registered in this host.");
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var input = ChildObject(parameters, "input");
        var projectId = ProjectOf(scope, parameters, input);
        var resourceId = GuidOf(parameters, "resource_id") ?? GuidOf(input, "resource_id");
        switch (action)
        {
            case "install":
            case "import":
            {
                var requestedScope = (Text(input, "scope") ?? "shared").Trim().ToLowerInvariant();
                if (requestedScope is not ("shared" or "project"))
                    return Fail(wire, "invalid_scope", "scope must be shared or project.");
                var targetProject = projectId;
                if (requestedScope == "project" && targetProject is null)
                    return Fail(wire, "project_required", "A project-scoped skill needs project_id (or a run inside a project).");
                var name = Text(input, "name");
                var content = Text(input, "content");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
                    return Fail(wire, "invalid_request", "install needs input.name and input.content (the SKILL.md body).");
                var write = new ToolSkillWriteDto
                {
                    Scope = requestedScope,
                    ProjectId = targetProject,
                    Name = name,
                    Content = content,
                    Enabled = ReadNullableBool(input, "enabled"),
                    Files = ReadFileMap(input),
                    ReplaceFiles = ReadBool(input, "replace_files", true),
                    Source = Text(input, "source") ?? "manual",
                    Version = Text(input, "version"),
                    Commit = Text(input, "commit"),
                };
                var saved = await _skills.RequestSaveAsync(null, targetProject, write, 0, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, action, skill = DescribeSkill(saved), note = GovernedSkillNote(saved) });
            }
            case "enable":
            case "disable":
            {
                if (resourceId is null) return Fail(wire, "invalid_request", $"action={action} needs resource_id.");
                var existing = await _skills.GetAsync(resourceId.Value, projectId, ct).ConfigureAwait(false);
                if (existing is null) return Fail(wire, "skill_not_found", "Skill resource not found.");
                if (existing.ReadOnly)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly, "Built-in and compatibility skills are read-only. Copy the skill into the project to edit it.");
                var revision = ReadInt(input, "expected_revision", ReadInt(parameters, "expected_revision", (int)Math.Min(existing.Revision, int.MaxValue)));
                // A project skill's governed write re-checks the reviewed file hash; a flag
                // flip is still a write of that exact file, so hand the current hash back.
                var write = new ToolSkillWriteDto
                {
                    Scope = existing.Scope,
                    ProjectId = existing.ProjectId,
                    Enabled = action == "enable",
                    ExpectedFileHash = existing.FileHash,
                };
                var saved = await _skills.RequestSaveAsync(resourceId, projectId, write, revision, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, action, skill = DescribeSkill(saved), note = GovernedSkillNote(saved) });
            }
            case "remove":
            {
                if (resourceId is null) return Fail(wire, "invalid_request", "action=remove needs resource_id.");
                var existing = await _skills.GetAsync(resourceId.Value, projectId, ct).ConfigureAwait(false);
                if (existing is null) return Fail(wire, "skill_not_found", "Skill resource not found.");
                if (existing.ReadOnly)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly, "Built-in and compatibility skills are read-only and cannot be removed.");
                var revision = ReadInt(input, "expected_revision", ReadInt(parameters, "expected_revision", (int)Math.Min(existing.Revision, int.MaxValue)));
                var removed = await _skills.RequestDeleteAsync(resourceId.Value, projectId, revision, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, action, skill = DescribeSkill(removed), note = GovernedSkillNote(removed) });
            }
            default:
                return Fail(wire, "invalid_request", "action must be install, import, enable, disable or remove.");
        }
    }

    // ── tinadec_mcp_apply ──

    private async Task<ToolWireResponseDto> McpApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        if (_mcp is null) return Fail(wire, "unavailable", "No MCP registry is registered in this host.");
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var input = ChildObject(parameters, "input");
        var resourceId = GuidOf(parameters, "resource_id") ?? GuidOf(input, "resource_id");
        var expected = ReadInt(input, "expected_revision", ReadInt(parameters, "expected_revision", int.MinValue));
        switch (action)
        {
            case "upsert":
            {
                var projectId = ProjectOf(scope, parameters, input);
                var serverId = Text(input, "id") ?? Text(parameters, "id");
                var command = Text(input, "command") ?? Text(parameters, "command");
                if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(command))
                    return Fail(wire, "invalid_request", "upsert needs input.id and input.command.");
                var existing = resourceId is { } id ? await _mcp.GetAsync(id, ct).ConfigureAwait(false) : null;
                if (resourceId is not null && existing is null) return Fail(wire, "mcp_resource_not_found", "MCP resource not found.");
                var write = new McpResourceWriteDto
                {
                    ProjectId = projectId,
                    Id = serverId,
                    Name = Text(input, "name") ?? serverId,
                    Enabled = ReadBool(input, "enabled", existing?.Enabled ?? true),
                    Command = command,
                    Args = ReadStringArray(input, "args"),
                    // Masked values mean "keep the stored secret"; the registry performs that swap.
                    Env = ReadEnvMap(input),
                    Cwd = Text(input, "cwd"),
                };
                var revision = expected != int.MinValue ? expected : existing?.Revision ?? 0;
                var saved = await _mcp.SaveAsync(resourceId, write, revision, ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    action,
                    server = DescribeMcp(saved),
                    note = "Registered in Core; a run only sees it once it has the mcp_* tools in its frozen scope.",
                });
            }
            case "test":
            {
                if (resourceId is null) return Fail(wire, "invalid_request", "action=test needs resource_id.");
                return await TestMcpAsync(scope, wire, resourceId.Value, ct).ConfigureAwait(false);
            }
            case "remove":
            {
                if (resourceId is null) return Fail(wire, "invalid_request", "action=remove needs resource_id.");
                var existing = await _mcp.GetAsync(resourceId.Value, ct).ConfigureAwait(false);
                if (existing is null) return Fail(wire, "mcp_resource_not_found", "MCP resource not found.");
                var revision = expected != int.MinValue ? expected : existing.Revision;
                await _mcp.DeleteAsync(resourceId.Value, revision, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, action, resource_id = resourceId });
            }
            default:
                return Fail(wire, "invalid_request", "action must be upsert, test or remove.");
        }
    }

    /// <summary>
    /// Connection status only — the same probe the settings page uses: expire the cached
    /// listing by asking the provider to list this resource once. No tool schemas, no
    /// environment values and no server output reach the model.
    /// </summary>
    private async Task<ToolWireResponseDto> TestMcpAsync(ToolInvocationScope scope, ToolWireRequestDto wire, Guid resourceId, CancellationToken ct)
    {
        var resource = await _mcp!.GetAsync(resourceId, ct).ConfigureAwait(false);
        if (resource is null) return Fail(wire, "mcp_resource_not_found", "MCP resource not found.");
        if (!resource.Enabled)
            return Ok(wire, new { ok = true, resource_id = resourceId, status = "disabled", reason = "Enable this resource before testing it." });
        if (_provider is null || _toolSettings is null)
            return Ok(wire, new { ok = true, resource_id = resourceId, status = "unavailable", reason = "This host cannot start MCP servers." });
        try
        {
            var resolved = await _toolSettings.ResolveAsync(resource.ProjectId, null, ct).ConfigureAwait(false);
            var selected = new ToolExecutionContextDto
            {
                Settings = ToolSettingsSchema.Merge(resolved.Settings, JsonSerializer.SerializeToElement(new { mcp = new { enabled = true } })),
                SettingsHash = resolved.SettingsHash,
                McpServers = await _mcp.ResolveAsync(resource.ProjectId, [resourceId], ct).ConfigureAwait(false),
                ReadRoots = [],
                McpImportError = resolved.McpImportError,
            };
            var context = await _toolSettings.MaterializeForCallAsync(selected, ["mcp_list"], ct).ConfigureAwait(false);
            string root;
            if (resource.ProjectId is { } projectId && _sessions is not null)
                root = (await _sessions.FindProjectAsync(projectId, ct).ConfigureAwait(false))?.RootPath ?? scope.WorkspaceRoot;
            else
                root = _configuration?["TinadecTools:DefaultWorkspaceRoot"] ?? scope.WorkspaceRoot;
            var response = await _provider.CallAsync(root, new ToolWireRequestDto
            {
                ToolId = "mcp_list",
                SessionId = "mcp-resource-test",
                Approved = false,
                Params = JsonSerializer.SerializeToElement(new { include_schema = false }),
                ExecutionContext = context,
            }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!response.IsSuccess)
                return Ok(wire, new { ok = true, resource_id = resourceId, status = "error", reason = response.Error });
            var row = response.Result is { ValueKind: JsonValueKind.Object } payload
                && payload.TryGetProperty("servers", out var listed) && listed.ValueKind == JsonValueKind.Array
                    ? listed.EnumerateArray().FirstOrDefault(server => server.TryGetProperty("id", out var id) && id.GetString() == resource.Id)
                    : default;
            if (row.ValueKind == JsonValueKind.Undefined)
                return Ok(wire, new { ok = true, resource_id = resourceId, status = "not_reported", reason = "The provider did not report this resource." });
            return Ok(wire, new
            {
                ok = true,
                resource_id = resourceId,
                status = row.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
                reason = row.TryGetProperty("error", out var error) ? error.GetString() : null,
                tool_count = row.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array ? tools.GetArrayLength() : 0,
            });
        }
        catch (Exception exception) when (exception is IOException or TimeoutException || exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Ok(wire, new { ok = true, resource_id = resourceId, status = "unavailable", reason = exception.Message });
        }
    }

    // ── projections ──

    private static object DescribeAgent(AgentDefinitionRecord agent, bool builtIn) => new
    {
        id = agent.Id,
        slug = agent.Slug,
        display_name = agent.DisplayName,
        layer = agent.Layer,
        role = agent.Role,
        status = agent.Status,
        enabled = agent.Enabled,
        version = agent.Version,
        revision = agent.Revision,
        built_in = builtIn,
        writable = !builtIn && !string.Equals(agent.Status, "archived", StringComparison.Ordinal),
        capabilities = ParseStringArray(agent.CapabilitiesJson),
        tool_ids = ParseStringArray(agent.ToolScopeJson).Take(MaxRows).ToArray(),
    };

    private static object DescribeSkill(ToolSkillResourceDto skill) => new
    {
        resource_id = skill.ResourceId,
        name = skill.Name,
        scope = skill.Scope,
        source = skill.SourceKind,
        enabled = skill.Enabled,
        valid = skill.Valid,
        revision = skill.Revision,
        read_only = skill.ReadOnly,
        reason_code = skill.ReasonCode,
        action_status = skill.ActionStatus,
        user_action_id = skill.UserActionId,
    };

    private static string? GovernedSkillNote(ToolSkillResourceDto skill) => skill.ActionStatus is null
        ? null
        : "Queued as a governed action: it lands only after a person approves it.";

    private static object DescribeMcp(McpResourceDto server) => new
    {
        resource_id = server.ResourceId,
        id = server.Id,
        name = server.Name,
        enabled = server.Enabled,
        command = server.Command,
        args = server.Args.Take(MaxRows).ToArray(),
        env_keys = (server.Env?.Keys ?? Enumerable.Empty<string>()).OrderBy(key => key, StringComparer.Ordinal).Take(MaxRows).ToArray(),
        cwd = server.Cwd,
        project_id = server.ProjectId,
        revision = server.Revision,
    };

    private async Task<Dictionary<Guid, (string PackId, string Status)>> PackOwnershipAsync(
        AgentConfigurationDbContext db,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken ct)
    {
        var rows = await (from resource in db.AgentPackManagedResources.AsNoTracking()
                          join installation in db.AgentPackInstallations.AsNoTracking() on resource.InstallationId equals installation.Id
                          where resource.TenantId == tenantId && resource.WorkspaceId == workspaceId
                          select new { resource.LogicalEntityId, installation.PackId, installation.Status })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.GroupBy(row => row.LogicalEntityId)
            .ToDictionary(group => group.Key, group => (group.First().PackId, group.First().Status));
    }

    // ── helpers ──

    private static ToolWireResponseDto Ok(ToolWireRequestDto wire, object payload) => new()
    {
        CallId = wire.ToolCallId,
        IsSuccess = true,
        Result = JsonSerializer.SerializeToElement(payload),
    };

    private static ToolWireResponseDto Fail(ToolWireRequestDto wire, string code, string message, object? diagnostics = null) => new()
    {
        CallId = wire.ToolCallId,
        IsSuccess = false,
        Error = $"{code}: {message}",
        Result = JsonSerializer.SerializeToElement(new { ok = false, code, message, diagnostics }),
    };

    private static ToolWireResponseDto TopologyFailure(ToolWireRequestDto wire, AgentTopologyResult result) =>
        Fail(wire, result.Code ?? AgentTopologyCodes.InvalidRequest, result.Message ?? "The topology operation was refused.",
            result.Diagnostics.Count == 0 ? null : result.Diagnostics.Select(diagnostic => new { code = diagnostic.Code, message = diagnostic.Message }).ToArray());

    private static Guid? ProjectOf(ToolInvocationScope scope) => CoreVirtualToolPolicy.FromWireSentinel(scope.ProjectId);

    private static Guid? ProjectOf(ToolInvocationScope scope, JsonElement parameters, JsonElement input)
    {
        var raw = Text(parameters, "project_id") ?? Text(input, "project_id");
        if (raw is not null) return Guid.TryParse(raw, out var projectId) && projectId != Guid.Empty ? projectId : null;
        return ProjectOf(scope);
    }

    private static async Task<string> UniqueDraftSlugAsync(
        AgentConfigurationDbContext db,
        string candidate,
        ToolInvocationScope scope,
        CancellationToken ct)
    {
        var slug = AgentTopologyService.Slugify(candidate);
        if (string.IsNullOrWhiteSpace(slug)) slug = "agent";
        var taken = (await db.AgentDefinitions.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "draft")
                .Select(x => x.Slug).ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(slug)) return slug;
        for (var index = 2; index < 500; index++)
        {
            var next = $"{slug}-{index}";
            if (!taken.Contains(next)) return next;
        }
        return $"{slug}-{Guid.NewGuid():N}";
    }

    private static string? ValidateLayer(string layer)
    {
        try { AgentConfigurationService.ValidateLayer(layer); return null; }
        catch (ArgumentException exception) { return exception.Message; }
    }

    private static string? ModelStrategyError(JsonElement payload) =>
        Raw(payload, "model_strategy") is { } json ? AgentTopologyService.ValidateModelStrategy(json) : null;

    private static bool Matches(string? query, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var needle = query.Trim();
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…";

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Raw(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetRawText()
            : null;

    private static JsonElement ChildObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static Guid? GuidOf(JsonElement element, string name) =>
        Text(element, name) is { } raw && Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : null;

    private static int ReadInt(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static bool ReadBool(JsonElement element, string name, bool fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.ValueKind == JsonValueKind.True : fallback;

    private static bool? ReadNullableBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.ValueKind == JsonValueKind.True : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
            : [];

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            if (element.ValueKind == JsonValueKind.Array)
                return element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "allowed_tools", "tool_enabled", "tools" })
                    if (element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                        return list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            }
            return [];
        }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyDictionary<string, string>? ReadFileMap(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.EnumerateObject())
            if (file.Value.ValueKind == JsonValueKind.String) map[file.Name] = file.Value.GetString()!;
        return map.Count == 0 ? null : map;
    }

    private static IReadOnlyDictionary<string, string?>? ReadEnvMap(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in env.EnumerateObject())
            map[pair.Name] = pair.Value.ValueKind switch
            {
                JsonValueKind.String => pair.Value.GetString(),
                JsonValueKind.Null => null,
                _ => throw new ToolSettingsException("invalid_mcp_environment", "Environment values must be strings or null."),
            };
        return map.Count == 0 ? null : map;
    }
}
