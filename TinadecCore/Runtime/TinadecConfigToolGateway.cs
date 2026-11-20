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
    private readonly IPromptFragmentService? _promptFragments;
    private readonly IModelProfileCatalogService? _modelProfiles;
    private readonly IToolSettingsStore? _toolSettingsStore;

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
        IConfiguration? configuration = null,
        IPromptFragmentService? promptFragments = null,
        IModelProfileCatalogService? modelProfiles = null,
        IToolSettingsStore? toolSettingsStore = null)
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
        _promptFragments = promptFragments;
        _modelProfiles = modelProfiles;
        _toolSettingsStore = toolSettingsStore;
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
            if (string.Equals(toolId, CoreVirtualToolPolicy.TinadecPromptApplyToolId, StringComparison.OrdinalIgnoreCase)) return await PromptApplyAsync(scope, wire, ct).ConfigureAwait(false);
            if (string.Equals(toolId, CoreVirtualToolPolicy.TinadecModelProfileApplyToolId, StringComparison.OrdinalIgnoreCase)) return await ModelProfileApplyAsync(scope, wire, ct).ConfigureAwait(false);
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
        catch (PromptFragmentException exception)
        {
            return Fail(wire, exception.Code, exception.Message);
        }
        catch (ModelProfileException exception)
        {
            return Fail(wire, exception.Code, exception.Message);
        }
        catch (WireRefusal refusal)
        {
            return Fail(wire, refusal.Code, refusal.Message, refusal.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return Fail(wire, "invalid_request", exception.Message);
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
            case "agent":
            {
                var reference = Text(parameters, "agent_id") ?? Text(parameters, "id");
                if (string.IsNullOrWhiteSpace(reference))
                    return Fail(wire, "invalid_request", "target=agent needs agent_id (a GUID or a slug).");
                var agent = await InspectAgentDetailAsync(scope, reference, ct).ConfigureAwait(false);
                return agent is null ? NotFound(wire, "agent", reference) : Ok(wire, agent);
            }
            case "catalog":
                return Ok(wire, await InspectCatalogAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "modes":
                return Ok(wire, await InspectModesAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "mode":
            {
                var reference = Text(parameters, "mode_id") ?? Text(parameters, "id");
                if (string.IsNullOrWhiteSpace(reference))
                    return Fail(wire, "invalid_request", "target=mode needs mode_id (a GUID or a slug).");
                var modeId = await ResolveModeIdAsync(scope, reference, ct).ConfigureAwait(false);
                if (modeId is null) return NotFound(wire, "mode", reference);
                var result = await _topology.GetAsync(modeId.Value, ct).ConfigureAwait(false);
                return result.Ok ? Ok(wire, new { ok = true, mode = result.Mode }) : TopologyFailure(wire, result);
            }
            case "models":
                return Ok(wire, await InspectModelsAsync(limit, ct).ConfigureAwait(false));
            case "prompts":
                return Ok(wire, await InspectPromptsAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "prompt_pipeline":
            {
                var reference = Text(parameters, "pipeline_id") ?? Text(parameters, "id");
                if (string.IsNullOrWhiteSpace(reference))
                    return Fail(wire, "invalid_request", "target=prompt_pipeline needs pipeline_id (a GUID or a slug).");
                var pipeline = await InspectPromptPipelineAsync(scope, reference, ct).ConfigureAwait(false);
                return pipeline is null ? NotFound(wire, "prompt_pipeline", reference) : Ok(wire, pipeline);
            }
            case "skills":
                return Ok(wire, await InspectSkillsAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "mcp":
                return Ok(wire, await InspectMcpAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "market":
                return Ok(wire, await InspectMarketAsync(query, limit, ct).ConfigureAwait(false));
            default:
                return Fail(wire, "invalid_request",
                    "target must be one of agents, agent, modes, mode, catalog, models, prompts, prompt_pipeline, skills, mcp, market.");
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

    /// <summary>
    /// The FULL agent definition, not the list projection: a caller that is about to
    /// edit or clone an agent needs the prompt, the model strategy, the whole tool
    /// scope and the prompt-pipeline binding, and needs to know whether the row is a
    /// pack resource and how it came to exist. The list projection deliberately
    /// truncates all of that.
    /// </summary>
    private async Task<object?> InspectAgentDetailAsync(ToolInvocationScope scope, string reference, CancellationToken ct)
    {
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await FindAgentAsync(db, scope, reference, ct).ConfigureAwait(false);
        if (record is null) return null;
        var ownership = await PackOwnershipAsync(db, scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        ownership.TryGetValue(record.Id, out var owner);
        var builtIn = owner.PackId is not null;
        var runtime = await db.AgentRuntimeBindings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentDefinitionId == record.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct)
            .ConfigureAwait(false);
        return new
        {
            target = "agent",
            ok = true,
            agent = new
            {
                id = record.Id,
                slug = record.Slug,
                display_name = record.DisplayName,
                description = record.Description,
                layer = record.Layer,
                role = record.Role,
                status = record.Status,
                enabled = record.Enabled,
                version = record.Version,
                revision = record.Revision,
                source_kind = record.SourceKind,
                source_key = record.SourceKey,
                managed = record.Managed,
                provenance = Element(record.ProvenanceJson),
                pack_id = owner.PackId,
                built_in = builtIn,
                writable = !builtIn && !string.Equals(record.Status, "archived", StringComparison.Ordinal),
                system_prompt = record.SystemPrompt,
                model_strategy = Element(record.ModelStrategyJson),
                tool_scope = Element(record.ToolScopeJson),
                capabilities = Element(record.CapabilitiesJson),
                base_prompt_pipeline_id = record.BasePromptPipelineId,
                // The runtime binding is an override row, not part of the definition; it is
                // what a frozen run prefers over model_strategy, so an editor has to see it.
                runtime_binding = runtime is null ? (object?)null : new
                {
                    mode = runtime.Mode,
                    provider_instance_id = runtime.ProviderInstanceId,
                    model = runtime.Model,
                    route_purpose = runtime.RoutePurpose,
                    tool_scope_override = Element(runtime.ToolScopeOverrideJson),
                    revision = runtime.Revision,
                    updated_at = runtime.UpdatedAt,
                },
                created_at = record.CreatedAt,
                updated_at = record.UpdatedAt,
                archived_at = record.ArchivedAt,
            },
        };
    }

    /// <summary>
    /// Sanitized model catalog plus the per-model profiles an agent designer needs to
    /// choose a model. Everything here comes from the sanitized projection — provider
    /// secrets never cross this boundary, by construction in the service itself.
    /// </summary>
    private async Task<object> InspectModelsAsync(int limit, CancellationToken ct)
    {
        if (_modelProfiles is null)
            return new { target = "models", available = false, items = Array.Empty<object>(), note = "No model catalog is registered in this host." };
        var catalog = await _modelProfiles.GetSanitizedCatalogAsync(includeDisabled: false, ct).ConfigureAwait(false);
        return new
        {
            target = "models",
            available = true,
            provider_count = catalog.Providers.Count,
            truncated = catalog.Providers.Count > limit,
            providers = catalog.Providers.Take(limit).Select(provider => new
            {
                id = provider.Id,
                display_name = provider.DisplayName,
                driver = provider.Driver,
                protocol = provider.Protocol,
                channel = provider.Channel,
                connection_kind = provider.ConnectionKind,
                enabled = provider.Enabled,
                has_api_key = provider.HasApiKey,
                default_model = provider.DefaultModel,
                revision = provider.Revision,
                models = provider.Models.Take(MaxRows).ToArray(),
                capabilities = provider.Capabilities.Take(MaxRows).ToArray(),
                // The profile map is keyed by model id and is the only place a model's
                // specialties/tier live; tinadec_model_profile_apply writes exactly this.
                model_profiles = provider.ModelProfiles,
            }).ToArray(),
            routes = catalog.Routes.Take(MaxRows).Select(route => new
            {
                id = route.Id,
                purpose = route.Purpose,
                version = route.Version,
                revision = route.Revision,
                candidates = route.Candidates.Take(MaxRows).Select(candidate => new
                {
                    position = candidate.Position,
                    provider_instance_id = candidate.ProviderInstanceId,
                    model = candidate.Model,
                }).ToArray(),
            }).ToArray(),
            health = catalog.Health.Take(MaxRows).Select(state => new
            {
                provider_instance_id = state.ProviderInstanceId,
                model = state.Model,
                consecutive_failures = state.ConsecutiveFailures,
                is_cooling = state.IsCooling,
                cooling_until = state.CoolingUntil,
                last_category = state.LastCategory,
                updated_at = state.UpdatedAt,
            }).ToArray(),
        };
    }

    /// <summary>
    /// Prompt fragments (with content) and prompt pipelines (graph included). Fragments
    /// are workspace-scoped through the service's own tenant context; pipelines live in
    /// the agent-configuration store, which is why this reads two sources.
    /// </summary>
    private async Task<object> InspectPromptsAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        IReadOnlyList<PromptFragmentDto> fragments = _promptFragments is null
            ? []
            : await _promptFragments.ListAsync(cancellationToken: ct).ConfigureAwait(false);
        var matchedFragments = fragments.Where(fragment => Matches(query, fragment.Key, fragment.Title, fragment.Category))
            .OrderBy(fragment => fragment.Category, StringComparer.Ordinal)
            .ThenByDescending(fragment => fragment.Priority)
            .ThenBy(fragment => fragment.Key, StringComparer.Ordinal)
            .ToArray();

        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pipelines = await db.PromptPipelines.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(ct).ConfigureAwait(false);
        var ownership = await PackOwnershipAsync(db, scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        var matchedPipelines = pipelines.Where(pipeline => Matches(query, pipeline.Slug, pipeline.DisplayName, pipeline.Description))
            .OrderBy(pipeline => pipeline.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            target = "prompts",
            fragments = new
            {
                total = matchedFragments.Length,
                truncated = matchedFragments.Length > limit,
                items = matchedFragments.Take(limit).Select(fragment => new
                {
                    id = fragment.Id,
                    key = fragment.Key,
                    title = fragment.Title,
                    scope = fragment.Scope,
                    category = fragment.Category,
                    target_agent_id = fragment.TargetAgentId,
                    target_agent_slug = fragment.TargetAgentSlug,
                    priority = fragment.Priority,
                    enabled = fragment.Enabled,
                    is_builtin = fragment.IsBuiltIn,
                    writable = !fragment.IsBuiltIn,
                    revision = fragment.Revision,
                    // Content is a prompt fragment's whole purpose; it is workspace data,
                    // never a secret, and truncating it would make the tool useless.
                    content = fragment.Content,
                    updated_at = fragment.UpdatedAt,
                }).ToArray(),
            },
            pipelines = new
            {
                total = matchedPipelines.Length,
                truncated = matchedPipelines.Length > limit,
                items = matchedPipelines.Take(limit).Select(pipeline =>
                {
                    ownership.TryGetValue(pipeline.Id, out var owner);
                    var builtIn = owner.PackId is not null;
                    return new
                    {
                        id = pipeline.Id,
                        slug = pipeline.Slug,
                        display_name = pipeline.DisplayName,
                        description = pipeline.Description,
                        status = pipeline.Status,
                        version = pipeline.Version,
                        revision = pipeline.Revision,
                        node_count = CountGraphNodes(pipeline.GraphJson),
                        pack_id = owner.PackId,
                        built_in = builtIn,
                        writable = !builtIn && string.Equals(pipeline.Status, "draft", StringComparison.Ordinal),
                        updated_at = pipeline.UpdatedAt,
                    };
                }).ToArray(),
            },
            note = "A pipeline's own graph is returned by target=prompt_pipeline with its pipeline_id.",
        };
    }

    private async Task<object?> InspectPromptPipelineAsync(ToolInvocationScope scope, string reference, CancellationToken ct)
    {
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pipeline = await FindPromptPipelineAsync(db, scope, reference, ct).ConfigureAwait(false);
        if (pipeline is null) return null;
        var ownership = await PackOwnershipAsync(db, scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        ownership.TryGetValue(pipeline.Id, out var owner);
        var builtIn = owner.PackId is not null;
        var latestVersion = await db.PromptVersions.AsNoTracking()
            .Where(x => x.PromptPipelineId == pipeline.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return new
        {
            target = "prompt_pipeline",
            ok = true,
            pipeline = new
            {
                id = pipeline.Id,
                slug = pipeline.Slug,
                display_name = pipeline.DisplayName,
                description = pipeline.Description,
                status = pipeline.Status,
                version = pipeline.Version,
                revision = pipeline.Revision,
                pack_id = owner.PackId,
                built_in = builtIn,
                writable = !builtIn && string.Equals(pipeline.Status, "draft", StringComparison.Ordinal),
                // The full graph: nodes AND edges, exactly as the canvas edits it.
                graph = Element(pipeline.GraphJson),
                latest_published_version = latestVersion is null ? (object?)null : new
                {
                    id = latestVersion.Id,
                    version = latestVersion.Version,
                    content_hash = latestVersion.ContentHash,
                    created_at = latestVersion.CreatedAt,
                },
                created_at = pipeline.CreatedAt,
                updated_at = pipeline.UpdatedAt,
            },
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
                    // Null means "derive from the topology at freeze time"; a value is the
                    // author's explicit declaration and wins over the inference.
                    orchestration_tier = x.OrchestrationTier,
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
        var reference = Text(parameters, "agent_id") ?? Text(parameters, "id");
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case "create":
            {
                var name = Text(payload, "display_name") ?? Text(payload, "name") ?? Text(parameters, "display_name");
                if (string.IsNullOrWhiteSpace(name))
                    return Fail(wire, "invalid_request", "action=create needs payload.display_name.");
                var layer = (Text(payload, "layer") ?? "operation").Trim().ToLowerInvariant();
                if (ValidateLayer(layer) is { } layerError) return Fail(wire, "invalid_layer", layerError);
                if (ModelStrategyError(payload) is { } strategyError) return Fail(wire, "invalid_model_strategy", strategyError);
                if (await ResolvePromptPipelineAsync(db, scope, payload, ct).ConfigureAwait(false) is { } pipelineFailure) return Fail(wire, pipelineFailure.Code, pipelineFailure.Message);
                var modelBinding = await ApplyRuntimeBindingAsync(db, scope, null, payload, ct).ConfigureAwait(false);
                if (modelBinding.Failure is { } modelBindingFailure) return Fail(wire, modelBindingFailure.Code, modelBindingFailure.Message);
                var slug = await UniqueDraftSlugAsync(db, Text(payload, "slug") ?? name, scope, ct).ConfigureAwait(false);
                // An agent runtime creating an agent is not the same event as a person
                // filling in a form. When the caller did not say, the calling run decides:
                // a run means "agent_generated", no run means "custom".
                var sourceKind = DefaultSourceKind(payload, scope);
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
                    BasePromptPipelineId = GuidOf(payload, "base_prompt_pipeline_id"),
                    SourceKind = sourceKind, SourceKey = slug, Managed = false,
                    ProvenanceJson = Provenance(scope, payload, sourceKind),
                    Enabled = ReadBool(payload, "enabled", true),
                    Status = "draft", Revision = 1, Version = 0, CreatedAt = now, UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.AgentDefinitions.Add(record);
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateException) { return Fail(wire, "duplicate_slug", "A draft agent already uses this slug."); }
                if (modelBinding.Binding is not null)
                    await SaveRuntimeBindingAsync(db, scope, record.Id, modelBinding.Binding, now, ct).ConfigureAwait(false);
                var toolSettingsNote = await ApplyToolSettingsAsync(record.Id, payload, ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    action,
                    agent = DescribeAgent(record, builtIn: false),
                    source_kind = record.SourceKind,
                    base_prompt_pipeline_id = record.BasePromptPipelineId,
                    note = "Saved as a draft; call tinadec_agent_apply action=publish before a mode can use it.",
                    tool_settings_note = toolSettingsNote,
                });
            }
            case "clone":
            {
                var sourceId = reference is not null ? await ResolveAgentIdAsync(db, scope, reference, ct).ConfigureAwait(false) : null;
                if (sourceId is null)
                    return Fail(wire, "invalid_request", "action=clone needs agent_id (the agent to copy, GUID or slug).");
                var source = await db.AgentDefinitions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == sourceId.Value && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct).ConfigureAwait(false);
                if (source is null) return Fail(wire, "not_found", "Agent not found.");
                if (string.Equals(source.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived agent cannot be cloned.");
                if (await ResolvePromptPipelineAsync(db, scope, payload, ct).ConfigureAwait(false) is { } pipelineFailure) return Fail(wire, pipelineFailure.Code, pipelineFailure.Message);
                var name = Text(payload, "display_name") ?? $"{source.DisplayName} (copy)";
                var slug = await UniqueDraftSlugAsync(db, Text(payload, "slug") ?? $"{source.Slug}-copy", scope, ct).ConfigureAwait(false);
                var sourceKind = DefaultSourceKind(payload, scope);
                var copy = new AgentDefinitionRecord
                {
                    Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                    Slug = slug, DisplayName = name.Trim(), Layer = source.Layer,
                    Role = source.Role, CapabilitiesJson = source.CapabilitiesJson,
                    // An explicit payload value wins; otherwise the copy inherits the source's
                    // pipeline. A clone that silently lost its prompt would be a trap.
                    BasePromptPipelineId = GuidOf(payload, "base_prompt_pipeline_id") ?? source.BasePromptPipelineId,
                    ModelStrategyJson = Raw(payload, "model_strategy") ?? source.ModelStrategyJson,
                    ToolScopeJson = Raw(payload, "tool_scope") ?? Raw(payload, "allowed_tools") ?? source.ToolScopeJson,
                    SystemPrompt = Text(payload, "system_prompt") ?? source.SystemPrompt,
                    Description = Text(payload, "description") ?? source.Description,
                    I18nJson = null, SourceKind = sourceKind, SourceKey = slug, Managed = false,
                    ProvenanceJson = Provenance(scope, payload, sourceKind, source.Id),
                    Enabled = ReadBool(payload, "enabled", source.Enabled), Status = "draft", Revision = 1, Version = 0,
                    CreatedAt = now, UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.AgentDefinitions.Add(copy);
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateException) { return Fail(wire, "duplicate_slug", "A draft agent already uses this slug."); }
                var cloneBinding = await ApplyRuntimeBindingAsync(db, scope, null, payload, ct).ConfigureAwait(false);
                if (cloneBinding.Failure is { } cloneBindingFailure) return Fail(wire, cloneBindingFailure.Code, cloneBindingFailure.Message);
                if (cloneBinding.Binding is not null)
                    await SaveRuntimeBindingAsync(db, scope, copy.Id, cloneBinding.Binding, now, ct).ConfigureAwait(false);
                var toolSettingsNote = await ApplyToolSettingsAsync(copy.Id, payload, ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    action,
                    agent = DescribeAgent(copy, builtIn: false),
                    source_agent_id = source.Id,
                    source_kind = copy.SourceKind,
                    tool_settings_note = toolSettingsNote,
                    note = "A custom draft copy; the source is unchanged. Publish it before a mode can use it.",
                });
            }
            case "update":
            {
                var agentId = reference is not null ? await ResolveAgentIdAsync(db, scope, reference, ct).ConfigureAwait(false) : null;
                if (agentId is null) return Fail(wire, "invalid_request", "action=update needs agent_id (GUID or slug).");
                var record = await db.AgentDefinitions
                    .FirstOrDefaultAsync(x => x.Id == agentId.Value && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct).ConfigureAwait(false);
                if (record is null) return Fail(wire, "not_found", "Agent not found.");
                if (await _packs.FindManagedResourceAsync("agent", agentId.Value, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages agent '{managed.ResourceKey}'. Clone it and update the copy.");
                if (string.Equals(record.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived agent cannot be edited.");
                var expected = ReadInt(parameters, "expected_revision", ReadInt(payload, "expected_revision", -1));
                if (expected >= 0 && record.Revision != expected)
                    return Fail(wire, "conflict", $"The agent changed since it was read: expected revision {expected}, actual {record.Revision}. Inspect it again.");
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("layer", out var layerElement))
                {
                    var layer = (layerElement.GetString() ?? "operation").Trim().ToLowerInvariant();
                    if (ValidateLayer(layer) is { } layerError) return Fail(wire, "invalid_layer", layerError);
                    record.Layer = layer;
                }
                if (ModelStrategyError(payload) is { } strategyError) return Fail(wire, "invalid_model_strategy", strategyError);
                if (await ResolvePromptPipelineAsync(db, scope, payload, ct).ConfigureAwait(false) is { } pipelineFailure) return Fail(wire, pipelineFailure.Code, pipelineFailure.Message);
                if (Text(payload, "display_name") is { } displayName && !string.IsNullOrWhiteSpace(displayName)) record.DisplayName = displayName;
                if (Text(payload, "role") is { } role) record.Role = role;
                if (Raw(payload, "capabilities") is { } capabilities) record.CapabilitiesJson = capabilities;
                if (Raw(payload, "model_strategy") is { } modelStrategy) record.ModelStrategyJson = modelStrategy;
                if ((Raw(payload, "tool_scope") ?? Raw(payload, "allowed_tools")) is { } toolScope) record.ToolScopeJson = toolScope;
                if (Text(payload, "system_prompt") is { } systemPrompt) record.SystemPrompt = systemPrompt;
                if (Text(payload, "description") is { } description) record.Description = description;
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("base_prompt_pipeline_id", out var pipelineElement))
                    record.BasePromptPipelineId = pipelineElement.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String when Guid.TryParse(pipelineElement.GetString(), out var pipelineId) && pipelineId != Guid.Empty => pipelineId,
                        // Undefined/null clears; anything else is a caller bug, not a silent no-op.
                        JsonValueKind.String => null,
                        _ => record.BasePromptPipelineId,
                    };
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    record.Enabled = enabled.ValueKind == JsonValueKind.True;
                if (Text(payload, "source_kind") is { } sourceKind && !string.IsNullOrWhiteSpace(sourceKind))
                    record.SourceKind = sourceKind.Trim().ToLowerInvariant();
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("provenance", out _))
                    record.ProvenanceJson = Provenance(scope, payload, record.SourceKind) ?? record.ProvenanceJson;
                record.Status = "draft";
                record.Revision++;
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateConcurrencyException) { return Fail(wire, "conflict", "The agent changed since it was read."); }
                var updateBinding = await ApplyRuntimeBindingAsync(db, scope, record.Id, payload, ct).ConfigureAwait(false);
                if (updateBinding.Failure is { } updateBindingFailure) return Fail(wire, updateBindingFailure.Code, updateBindingFailure.Message);
                if (updateBinding.Binding is not null)
                    await SaveRuntimeBindingAsync(db, scope, record.Id, updateBinding.Binding, now, ct).ConfigureAwait(false);
                var toolSettingsNote = await ApplyToolSettingsAsync(record.Id, payload, ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    action,
                    agent = DescribeAgent(record, builtIn: false),
                    base_prompt_pipeline_id = record.BasePromptPipelineId,
                    runtime_binding = updateBinding.Binding is null ? (object?)null : new { mode = updateBinding.Binding.Mode, revision = updateBinding.Binding.Revision },
                    tool_settings_note = toolSettingsNote,
                    note = "Saved as a draft; publish it to make a new runnable version.",
                });
            }
            case "publish":
            {
                var agentId = reference is not null ? await ResolveAgentIdAsync(db, scope, reference, ct).ConfigureAwait(false) : null;
                if (agentId is null) return Fail(wire, "invalid_request", "action=publish needs agent_id (GUID or slug).");
                return await PublishAgentAsync(db, scope, wire, agentId.Value, parameters, payload, ct).ConfigureAwait(false);
            }
            default:
                return Fail(wire, "invalid_request", "action must be clone, create, update or publish.");
        }
    }

    /// <summary>
    /// Publishes a draft agent the same way the editor does: validate, snapshot an
    /// immutable version, then flip the definition to published. This is the missing
    /// half of self-configuration — a mode only accepts published agents, so an agent
    /// an AI created could never reach a runnable mode without it.
    /// </summary>
    private async Task<ToolWireResponseDto> PublishAgentAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        Guid agentId,
        JsonElement parameters,
        JsonElement payload,
        CancellationToken ct)
    {
        var record = await db.AgentDefinitions
            .FirstOrDefaultAsync(x => x.Id == agentId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct).ConfigureAwait(false);
        if (record is null) return Fail(wire, "not_found", "Agent not found.");
        if (await _packs.FindManagedResourceAsync("agent", agentId, ct).ConfigureAwait(false) is { } managed)
            return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                $"Pack '{managed.PackId}' manages agent '{managed.ResourceKey}'. Clone it and publish the copy.");
        var expected = ReadInt(parameters, "expected_revision", ReadInt(payload, "expected_revision", -1));
        if (expected >= 0 && record.Revision != expected)
            return Fail(wire, "conflict", $"The agent changed since it was read: expected revision {expected}, actual {record.Revision}. Inspect it again.");
        if (string.Equals(record.Status, "archived", StringComparison.Ordinal))
            return Fail(wire, "conflict", "An archived agent cannot be published.");
        if (!string.Equals(record.Status, "draft", StringComparison.OrdinalIgnoreCase))
            return Fail(wire, "not_draft", $"This agent is already '{record.Status}'. Edit the draft to create a new version.");
        if (ValidateLayer(record.Layer) is { } layerError) return Fail(wire, "invalid_layer", layerError);
        if (string.IsNullOrWhiteSpace(record.DisplayName))
            return Fail(wire, "invalid_request", "An agent needs a display_name before it can be published.");
        if (record.ModelStrategyJson is { } strategyJson && AgentTopologyService.ValidateModelStrategy(strategyJson) is { } strategyError)
            return Fail(wire, "invalid_model_strategy", strategyError);

        var now = DateTimeOffset.UtcNow;
        var version = (await db.AgentVersions.Where(x => x.AgentDefinitionId == agentId).MaxAsync(x => (int?)x.Version, ct).ConfigureAwait(false) ?? 0) + 1;
        // Same snapshot shape PublishAgent writes, so a version published by a model and a
        // version published by the editor are indistinguishable to a reader.
        var snapshot = JsonSerializer.Serialize(new
        {
            id = record.Id,
            slug = record.Slug,
            display_name = record.DisplayName,
            layer = record.Layer,
            role = record.Role,
            source_kind = record.SourceKind,
            provenance = Element(record.ProvenanceJson),
            capabilities = ParseJson(record.CapabilitiesJson),
            model_strategy = ParseJson(record.ModelStrategyJson),
            tool_scope = ParseJson(record.ToolScopeJson),
            base_prompt_pipeline_id = record.BasePromptPipelineId,
            system_prompt = record.SystemPrompt,
            description = record.Description,
            enabled = record.Enabled,
        });
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
        db.AgentVersions.Add(new AgentVersionRecord
        {
            Id = Guid.NewGuid(), TenantId = record.TenantId, WorkspaceId = record.WorkspaceId,
            AgentDefinitionId = record.Id, Version = version, Layer = record.Layer, Role = record.Role,
            SnapshotJson = snapshot, ContentHash = hash,
            ContentLength = System.Text.Encoding.UTF8.GetByteCount(snapshot),
            Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId,
        });
        record.Version = version;
        record.Status = "published";
        record.Revision++;
        record.UpdatedAt = now;
        record.UpdatedByPrincipalId = scope.PrincipalId;
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { return Fail(wire, "conflict", "The agent changed since it was read."); }
        return Ok(wire, new
        {
            ok = true,
            action = "publish",
            agent = DescribeAgent(record, builtIn: false),
            version,
            revision = record.Revision,
            content_hash = hash,
            note = "Published. A mode can now use this agent; runs already admitted keep their frozen version.",
        });
    }

    // ── tinadec_mode_apply / tinadec_mode_publish ──

    private async Task<ToolWireResponseDto> ModeApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        var body = payload.ValueKind == JsonValueKind.Object ? payload : parameters;
        var expectedRevision = ReadInt(parameters, "expected_revision", -1);
        var reference = Text(parameters, "mode_id") ?? Text(parameters, "id");
        Guid? modeId = null;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            modeId = await ResolveModeIdAsync(scope, reference, ct).ConfigureAwait(false);
            // create must not silently fall back to "make another mode" when the caller
            // named an existing one: that would duplicate a slug instead of reporting it.
            if (modeId is null && action is not "create")
                return NotFound(wire, "mode", reference);
        }
        // The tier is a first-class mode field, not a graph detail: it selects the
        // enforcement path a run takes. IAgentTopologyService validates and persists it
        // (ParseTopology → ApplyTopologyAsync), so this only normalizes what to echo back
        // and refuses a typo before any write happens.
        string? tier = null;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("orchestration_tier", out var tierElement)
            && tierElement.ValueKind is not JsonValueKind.Null)
        {
            if (tierElement.ValueKind != JsonValueKind.String)
                return Fail(wire, "invalid_request", "orchestration_tier must be a string or null.");
            tier = NormalizeTier(tierElement.GetString());
            if (tier is null)
                return Fail(wire, "invalid_orchestration_tier",
                    "orchestration_tier must be one of solo, solo_dispatch, free_form, self_dispatch, deterministic.");
        }
        AgentTopologyResult result;
        switch (action)
        {
            case "create":
                result = await _topology.CreateDraftAsync(body, ct).ConfigureAwait(false);
                break;
            case "clone":
                if (modeId is null) return Fail(wire, "invalid_request", "action=clone needs mode_id (the mode to copy, GUID or slug).");
                result = await _topology.CloneDraftAsync(modeId.Value, payload, ct).ConfigureAwait(false);
                break;
            case "upsert":
                if (modeId is null) return Fail(wire, "invalid_request", "action=upsert needs mode_id (GUID or slug).");
                result = await _topology.UpsertDraftAsync(modeId.Value, body, expectedRevision, ct).ConfigureAwait(false);
                break;
            case "delete":
                if (modeId is null) return Fail(wire, "invalid_request", "action=delete needs mode_id (GUID or slug).");
                result = await _topology.DeleteDraftAsync(modeId.Value, ct).ConfigureAwait(false);
                break;
            default:
                return Fail(wire, "invalid_request", "action must be clone, create, upsert or delete.");
        }
        if (!result.Ok) return TopologyFailure(wire, result);
        // The topology service persists the tier itself (ParseTopology → ApplyTopologyAsync),
        // so this only reports what was asked for and what was stored.
        return Ok(wire, new
        {
            ok = true,
            action,
            mode = result.Mode,
            mode_id = result.ModeId,
            revision = result.Revision,
            version = result.Version,
            orchestration_tier = tier,
            note = action == "delete" ? null : "Saved as a draft; call tinadec_mode_publish when the graph is right.",
        });
    }

    private async Task<ToolWireResponseDto> ModePublishAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        var reference = Text(parameters, "mode_id") ?? Text(parameters, "id");
        if (string.IsNullOrWhiteSpace(reference))
            return Fail(wire, "invalid_request", "tinadec_mode_publish needs mode_id (GUID or slug).");
        var modeId = await ResolveModeIdAsync(scope, reference, ct).ConfigureAwait(false);
        if (modeId is null) return NotFound(wire, "mode", reference);
        var result = await _topology.PublishAsync(modeId.Value, ReadInt(parameters, "expected_revision", -1), ct).ConfigureAwait(false);
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

    // ── tinadec_prompt_apply ──

    /// <summary>
    /// Prompt fragments and prompt pipelines. Fragments go through
    /// <see cref="IPromptFragmentService"/>, which is the one store the assembler reads;
    /// pipelines go through the agent-configuration store that the canvas and
    /// <c>IAgentConfigurationService.PublishAsync</c> already use. There is no second
    /// store here on purpose: a fragment written by a model must be the same row the
    /// assembler snapshots into a run.
    /// </summary>
    private async Task<ToolWireResponseDto> PromptApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        var target = (Text(parameters, "target") ?? string.Empty).Trim().ToLowerInvariant();
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        var expectedRevision = ReadInt(parameters, "expected_revision", -1);
        long? expected = expectedRevision >= 0 ? expectedRevision : null;
        if (target is not ("fragment" or "pipeline"))
            return Fail(wire, "invalid_request", "target must be fragment or pipeline.");
        if (action is not ("create" or "update" or "clone" or "publish" or "rollback"))
            return Fail(wire, "invalid_request", "action must be create, update, clone, publish or rollback.");
        return target == "fragment"
            ? await PromptFragmentApplyAsync(scope, wire, action, parameters, payload, expected, ct).ConfigureAwait(false)
            : await PromptPipelineApplyAsync(scope, wire, action, parameters, payload, expected, ct).ConfigureAwait(false);
    }

    private async Task<ToolWireResponseDto> PromptFragmentApplyAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        string action,
        JsonElement parameters,
        JsonElement payload,
        long? expected,
        CancellationToken ct)
    {
        if (_promptFragments is null)
            return Fail(wire, "unavailable", "No prompt fragment store is registered in this host.");
        var reference = Text(parameters, "fragment_id") ?? Text(parameters, "id");
        Guid? fragmentId = null;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            fragmentId = await ResolveFragmentIdAsync(reference, ct).ConfigureAwait(false);
            if (fragmentId is null && action != "create") return NotFound(wire, "prompt_fragment", reference);
        }
        switch (action)
        {
            case "create":
            {
                var key = Text(payload, "key");
                if (string.IsNullOrWhiteSpace(key))
                    return Fail(wire, "invalid_request", "target=fragment action=create needs payload.key (a stable fragment key, not a GUID).");
                var content = Text(payload, "content");
                if (content is null)
                    return Fail(wire, "invalid_request", "target=fragment action=create needs payload.content.");
                var saved = await _promptFragments.SaveAsync(null, FragmentRequest(payload), null, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = DescribeFragment(saved), note = FragmentNote(saved) });
            }
            case "update":
            {
                if (fragmentId is null)
                    return Fail(wire, "invalid_request", "target=fragment action=update needs fragment_id (GUID, or key when unique).");
                var existing = await _promptFragments.GetAsync(fragmentId.Value, ct).ConfigureAwait(false);
                if (existing is null) return NotFound(wire, "prompt_fragment", fragmentId.ToString());
                if (existing.IsBuiltIn)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly, "Built-in prompt fragments are read-only. Clone one with action=clone and edit the copy.");
                var saved = await _promptFragments.SaveAsync(fragmentId, FragmentRequest(payload), expected, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = DescribeFragment(saved), note = FragmentNote(saved) });
            }
            case "clone":
            {
                if (fragmentId is null) return Fail(wire, "invalid_request", "target=fragment action=clone needs fragment_id (the fragment to copy).");
                var clone = new PromptFragmentCloneRequestDto
                {
                    Key = Text(payload, "new_key") ?? Text(payload, "key"),
                    Title = Text(payload, "title"),
                    Scope = Text(payload, "scope"),
                    TargetAgentId = Text(payload, "target_agent_id"),
                    TargetAgentSlug = Text(payload, "target_agent_slug"),
                };
                var saved = await _promptFragments.CloneAsync(fragmentId.Value, clone, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = DescribeFragment(saved), note = "A new editable fragment; the source is unchanged." });
            }
            case "publish":
            {
                // A fragment has no draft/published split: every save already writes an
                // immutable version. "Publish" therefore means "add a named version", which
                // is the operation a caller actually wants and the one history shows.
                if (fragmentId is null) return Fail(wire, "invalid_request", "target=fragment action=publish needs fragment_id.");
                var existing = await _promptFragments.GetAsync(fragmentId.Value, ct).ConfigureAwait(false);
                if (existing is null) return NotFound(wire, "prompt_fragment", fragmentId.ToString());
                if (existing.IsBuiltIn)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly, "Built-in prompt fragments are read-only.");
                var created = await _promptFragments.CreateVersionAsync(fragmentId.Value, new PromptFragmentVersionCreateRequestDto
                {
                    Content = Text(payload, "content") ?? existing.Content,
                    ChangedFields = ReadStringArray(payload, "changed_fields") is { Count: > 0 } changed ? changed : null,
                    ChangeSummary = Text(payload, "change_summary") ?? "published by tinadec_prompt_apply",
                }, expected, ct).ConfigureAwait(false);
                // Re-read rather than guessing the new revision: the caller's next write uses
                // this number as expected_revision, so a fabricated one would be a guaranteed
                // conflict on the very next call.
                var afterPublish = await _promptFragments.GetAsync(fragmentId.Value, ct).ConfigureAwait(false) ?? existing;
                return Ok(wire, new
                {
                    ok = true,
                    target = "fragment",
                    action,
                    fragment = DescribeFragment(afterPublish),
                    version = new { id = created.Id, version = created.Version, is_active = created.IsActive, created_at = created.CreatedAt },
                    note = "A new immutable fragment version; earlier versions stay readable and rollback-able.",
                });
            }
            case "rollback":
            {
                if (fragmentId is null) return Fail(wire, "invalid_request", "target=fragment action=rollback needs fragment_id.");
                var existing = await _promptFragments.GetAsync(fragmentId.Value, ct).ConfigureAwait(false);
                if (existing is null) return NotFound(wire, "prompt_fragment", fragmentId.ToString());
                if (existing.IsBuiltIn)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly, "Built-in prompt fragments are read-only.");
                var versionRaw = Text(parameters, "version_id") ?? Text(payload, "version_id");
                var targetVersion = ReadInt(payload, "target_version", ReadInt(parameters, "target_version", 0));
                var rollback = new PromptFragmentRollbackRequestDto
                {
                    VersionId = Guid.TryParse(versionRaw, out var versionId) && versionId != Guid.Empty ? versionId : null,
                    TargetVersion = targetVersion > 0 ? targetVersion : null,
                    ChangeSummary = Text(payload, "change_summary"),
                };
                if (rollback.VersionId is null && rollback.TargetVersion is null)
                    return Fail(wire, "invalid_request", "target=fragment action=rollback needs version_id (GUID) or target_version (the version number).");
                var saved = await _promptFragments.RollbackAsync(fragmentId.Value, rollback, expected, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = DescribeFragment(saved), note = FragmentNote(saved) });
            }
            default:
                return Fail(wire, "invalid_request", "Unsupported fragment action.");
        }
    }

    private async Task<ToolWireResponseDto> PromptPipelineApplyAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        string action,
        JsonElement parameters,
        JsonElement payload,
        long? expected,
        CancellationToken ct)
    {
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var reference = Text(parameters, "pipeline_id") ?? Text(parameters, "id");
        PromptPipelineRecord? record = null;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            record = await FindPromptPipelineAsync(db, scope, reference, ct).ConfigureAwait(false);
            if (record is null && action != "create") return NotFound(wire, "prompt_pipeline", reference);
        }
        var now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case "create":
            {
                var name = Text(payload, "display_name") ?? Text(payload, "name");
                if (string.IsNullOrWhiteSpace(name))
                    return Fail(wire, "invalid_request", "target=pipeline action=create needs payload.display_name.");
                // The graph is the pipeline: refuse one that cannot run rather than
                // saving a draft the publish gate will reject later.
                var graphJson = GraphPayload(payload);
                if (graphJson is null)
                    return Fail(wire, "invalid_request", "target=pipeline action=create needs payload.graph (or payload.nodes + payload.edges).");
                if (ValidatePipelineGraph(graphJson) is { } graphError)
                    return Fail(wire, "invalid_request", graphError);
                var slug = await UniquePipelineSlugAsync(db, Text(payload, "slug") ?? name, scope, ct).ConfigureAwait(false);
                var created = new PromptPipelineRecord
                {
                    Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                    Slug = slug, DisplayName = name.Trim(), Description = Text(payload, "description"),
                    GraphJson = graphJson, Status = "draft", Revision = 1, Version = 0,
                    CreatedAt = now, UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.PromptPipelines.Add(created);
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateException) { return Fail(wire, "duplicate_slug", "A draft prompt pipeline already uses this slug."); }
                return Ok(wire, new
                {
                    ok = true,
                    target = "pipeline",
                    action,
                    pipeline = DescribePipeline(created, builtIn: false),
                    note = PipelineNote(created),
                });
            }
            case "update":
            {
                if (record is null) return Fail(wire, "invalid_request", "target=pipeline action=update needs pipeline_id (GUID or slug).");
                if (await _packs.FindManagedResourceAsync("prompt_pipeline", record.Id, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages prompt pipeline '{managed.ResourceKey}'. Clone it with action=clone and edit the copy.");
                if (!string.Equals(record.Status, "draft", StringComparison.OrdinalIgnoreCase))
                    return Fail(wire, "not_draft", $"This pipeline is '{record.Status}'; only a draft can be edited.");
                if (expected is { } expectedRevision && record.Revision != expectedRevision)
                    return Fail(wire, "revision_conflict", $"Prompt pipeline revision conflict: expected {expectedRevision}, actual {record.Revision}.");
                if (Text(payload, "display_name") is { } displayName && !string.IsNullOrWhiteSpace(displayName)) record.DisplayName = displayName;
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("description", out var description))
                    record.Description = description.ValueKind == JsonValueKind.String ? description.GetString() : null;
                if (GraphPayload(payload) is { } graphJson)
                {
                    if (ValidatePipelineGraph(graphJson) is { } graphError)
                        return Fail(wire, "invalid_request", graphError);
                    record.GraphJson = graphJson;
                }
                record.Revision++;
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateConcurrencyException) { return Fail(wire, "revision_conflict", "The pipeline changed since it was read."); }
                return Ok(wire, new { ok = true, target = "pipeline", action, pipeline = DescribePipeline(record, builtIn: false), note = PipelineNote(record) });
            }
            case "clone":
            {
                if (record is null) return Fail(wire, "invalid_request", "target=pipeline action=clone needs pipeline_id (the pipeline to copy).");
                var name = Text(payload, "display_name") ?? $"{record.DisplayName} (copy)";
                var slug = await UniquePipelineSlugAsync(db, Text(payload, "slug") ?? $"{record.Slug}-copy", scope, ct).ConfigureAwait(false);
                var copy = new PromptPipelineRecord
                {
                    Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                    Slug = slug, DisplayName = name.Trim(), Description = record.Description,
                    // Explicit graph wins, so "clone and change one node" is one call.
                    GraphJson = GraphPayload(payload) ?? record.GraphJson,
                    Status = "draft", Revision = 1, Version = 0,
                    CreatedAt = now, UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
                };
                if (ValidatePipelineGraph(copy.GraphJson) is { } cloneGraphError)
                    return Fail(wire, "invalid_request", cloneGraphError);
                db.PromptPipelines.Add(copy);
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateException) { return Fail(wire, "duplicate_slug", "A draft prompt pipeline already uses this slug."); }
                return Ok(wire, new
                {
                    ok = true,
                    target = "pipeline",
                    action,
                    pipeline = DescribePipeline(copy, builtIn: false),
                    source_pipeline_id = record.Id,
                    note = "A new editable draft pipeline; the source is unchanged.",
                });
            }
            case "publish":
            {
                if (record is null) return Fail(wire, "invalid_request", "target=pipeline action=publish needs pipeline_id.");
                if (await _packs.FindManagedResourceAsync("prompt_pipeline", record.Id, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages prompt pipeline '{managed.ResourceKey}'. Clone it before publishing your own version.");
                if (expected is { } expectedRevision && record.Revision != expectedRevision)
                    return Fail(wire, "revision_conflict", $"Prompt pipeline revision conflict: expected {expectedRevision}, actual {record.Revision}.");
                if (!string.Equals(record.Status, "draft", StringComparison.OrdinalIgnoreCase))
                    return Fail(wire, "not_draft", $"This pipeline is already '{record.Status}'. Edit the draft to create a new version.");
                if (GraphPayload(payload) is { } publishedGraph)
                {
                    if (ValidatePipelineGraph(publishedGraph) is { } publishGraphError)
                        return Fail(wire, "invalid_request", publishGraphError);
                    record.GraphJson = publishedGraph;
                }
                // Publish requires a runnable shape: the assembler needs a template to
                // produce text and an assemble node to collect it.
                if (PipelinePublishError(record.GraphJson) is { } publishError)
                    return Fail(wire, "invalid_request", publishError);
                var version = (await db.PromptVersions.Where(x => x.PromptPipelineId == record.Id)
                    .MaxAsync(x => (int?)x.Version, ct).ConfigureAwait(false) ?? 0) + 1;
                var hash = Sha256(record.GraphJson);
                db.PromptVersions.Add(new PromptVersionRecord
                {
                    Id = Guid.NewGuid(), TenantId = record.TenantId, WorkspaceId = record.WorkspaceId,
                    PromptPipelineId = record.Id, Version = version, GraphJson = record.GraphJson,
                    ContentHash = hash, ContentLength = System.Text.Encoding.UTF8.GetByteCount(record.GraphJson),
                    Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId,
                });
                record.Version = version;
                record.Status = "published";
                record.Revision++;
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateConcurrencyException) { return Fail(wire, "revision_conflict", "The pipeline changed since it was read."); }
                return Ok(wire, new
                {
                    ok = true,
                    target = "pipeline",
                    action,
                    pipeline = DescribePipeline(record, builtIn: false),
                    version,
                    revision = record.Revision,
                    content_hash = hash,
                    note = "Published as an immutable version; agents bound to this pipeline keep their frozen version until a run picks this one up.",
                });
            }
            case "rollback":
            {
                if (record is null) return Fail(wire, "invalid_request", "target=pipeline action=rollback needs pipeline_id.");
                if (await _packs.FindManagedResourceAsync("prompt_pipeline", record.Id, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages prompt pipeline '{managed.ResourceKey}'. Clone it first.");
                var versionRaw = Text(parameters, "version_id") ?? Text(payload, "version_id");
                var targetVersion = ReadInt(payload, "target_version", ReadInt(parameters, "target_version", 0));
                PromptVersionRecord? target = null;
                if (Guid.TryParse(versionRaw, out var versionId) && versionId != Guid.Empty)
                    target = await db.PromptVersions.AsNoTracking().FirstOrDefaultAsync(
                        x => x.Id == versionId && x.PromptPipelineId == record.Id, ct).ConfigureAwait(false);
                else if (targetVersion > 0)
                    target = await db.PromptVersions.AsNoTracking().FirstOrDefaultAsync(
                        x => x.PromptPipelineId == record.Id && x.Version == targetVersion, ct).ConfigureAwait(false);
                else
                    return Fail(wire, "invalid_request", "target=pipeline action=rollback needs version_id (GUID) or target_version (the version number).");
                if (target is null) return Fail(wire, "not_found", "The requested prompt pipeline version was not found.");
                // Rollback restores content as a NEW version: history is append-only, so a
                // run that already froze the version being abandoned stays reproducible.
                var nextVersion = (await db.PromptVersions.Where(x => x.PromptPipelineId == record.Id)
                    .MaxAsync(x => (int?)x.Version, ct).ConfigureAwait(false) ?? 0) + 1;
                record.GraphJson = target.GraphJson;
                var hash = Sha256(record.GraphJson);
                db.PromptVersions.Add(new PromptVersionRecord
                {
                    Id = Guid.NewGuid(), TenantId = record.TenantId, WorkspaceId = record.WorkspaceId,
                    PromptPipelineId = record.Id, Version = nextVersion, GraphJson = record.GraphJson,
                    ContentHash = hash, ContentLength = System.Text.Encoding.UTF8.GetByteCount(record.GraphJson),
                    Status = "published", Revision = 1, CreatedAt = now, CreatedByPrincipalId = scope.PrincipalId,
                });
                record.Version = nextVersion;
                record.Status = "published";
                record.Revision++;
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateConcurrencyException) { return Fail(wire, "revision_conflict", "The pipeline changed since it was read."); }
                return Ok(wire, new
                {
                    ok = true,
                    target = "pipeline",
                    action,
                    pipeline = DescribePipeline(record, builtIn: false),
                    rolled_back_from_version = target.Version,
                    version = nextVersion,
                    revision = record.Revision,
                    content_hash = hash,
                    note = $"Restored version {target.Version} as new version {nextVersion}; earlier versions stay readable.",
                });
            }
            default:
                return Fail(wire, "invalid_request", "Unsupported pipeline action.");
        }
    }

    // ── tinadec_model_profile_apply ──

    /// <summary>
    /// Per-model ability profiles: specialties, tier, notes, context window, cost tier.
    /// Deliberately narrow — <see cref="IModelProfileCatalogService"/> cannot write an
    /// API key, and this handler never passes a secret field even if one arrives.
    /// </summary>
    private async Task<ToolWireResponseDto> ModelProfileApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        if (_modelProfiles is null)
            return Fail(wire, "unavailable", "No model catalog is registered in this host.");
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        var expectedRevision = ReadInt(parameters, "expected_revision", -1);
        long? expected = expectedRevision >= 0 ? expectedRevision : null;
        var providerReference = Text(parameters, "provider_id") ?? Text(payload, "provider_id");
        var modelId = Text(parameters, "model_id") ?? Text(payload, "model_id");
        var catalog = await _modelProfiles.GetSanitizedCatalogAsync(includeDisabled: false, ct).ConfigureAwait(false);

        switch (action)
        {
            case "inspect":
            {
                var providers = FilterProviders(catalog, providerReference);
                if (providers.Length == 0) return NotFound(wire, "model_provider", providerReference ?? "(enabled providers)");
                return Ok(wire, new
                {
                    ok = true,
                    action,
                    providers = providers.Select(provider => new
                    {
                        id = provider.Id,
                        display_name = provider.DisplayName,
                        driver = provider.Driver,
                        protocol = provider.Protocol,
                        enabled = provider.Enabled,
                        revision = provider.Revision,
                        default_model = provider.DefaultModel,
                        models = provider.Models,
                        model_profiles = modelId is null
                            ? provider.ModelProfiles
                            : provider.ModelProfiles.Where(pair => string.Equals(pair.Key, modelId, StringComparison.Ordinal))
                                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    }).ToArray(),
                    note = "model_profiles is keyed by model id; only specialties, tier, notes, context_window and cost_tier are writable.",
                });
            }
            case "update":
            {
                if (string.IsNullOrWhiteSpace(modelId))
                    return Fail(wire, "invalid_request", "action=update needs model_id (the model the profile describes).");
                var providers = FilterProviders(catalog, providerReference);
                // A model id is not unique across providers in general, so refusing an
                // ambiguous write beats silently picking the first one.
                if (providers.Length > 1)
                    throw Refuse("ambiguous_provider",
                        $"model_id '{modelId}' matches {providers.Length} enabled providers; pass provider_id to choose one.",
                        providers.Select(candidate => new { id = candidate.Id, display_name = candidate.DisplayName }).ToArray());
                // The empty-payload guard runs BEFORE provider resolution on purpose: an empty
                // payload is the caller's actual mistake, and answering "provider not found"
                // sends it looking for a provider instead of fixing its own request.
                if (!HasProfileChange(payload))
                    return Fail(wire, "invalid_request",
                        "action=update needs at least one of specialties, tier, notes, context_window, cost_tier in payload.");
                if (providers.Length == 0)
                    return NotFound(wire, "model_provider", providerReference ?? "(enabled providers)");
                var provider = providers[0];
                if (!provider.Models.Contains(modelId, StringComparer.Ordinal))
                    return Fail(wire, "unknown_model",
                        $"Model '{modelId}' is not listed on provider '{provider.DisplayName}'. Enabled models: {string.Join(", ", provider.Models.Take(20))}. Call tinadec_inspect target=models for the exact provider and model ids.");
                var existing = provider.ModelProfiles.TryGetValue(modelId, out var current) ? current : null;
                // Merge over the stored profile so a partial update does not blank fields
                // the caller did not mention.
                var merged = new ModelProfileDto
                {
                    Specialties = payload.TryGetProperty("specialties", out var specialties) && specialties.ValueKind == JsonValueKind.Array
                        ? ReadStringArray(payload, "specialties")
                        : existing?.Specialties ?? [],
                    Tier = Text(payload, "tier") ?? existing?.Tier ?? "balanced",
                    Notes = payload.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Null
                        ? null
                        : Text(payload, "notes") ?? existing?.Notes,
                    ContextWindow = payload.TryGetProperty("context_window", out var contextWindow) && contextWindow.ValueKind == JsonValueKind.Null
                        ? null
                        : ReadNullableInt(payload, "context_window") ?? existing?.ContextWindow,
                    CostTier = payload.TryGetProperty("cost_tier", out var costTier) && costTier.ValueKind == JsonValueKind.Null
                        ? null
                        : Text(payload, "cost_tier") ?? existing?.CostTier,
                };
                ModelProfileUpdateResultDto updated;
                try
                {
                    updated = await _modelProfiles.UpdateModelProfileAsync(provider.Id, modelId, merged, expected, ct).ConfigureAwait(false);
                }
                catch (ArgumentException exception)
                {
                    // ModelProfile.Normalize throws ArgumentException naming the bad leaf
                    // (e.g. "model_profiles.tier must be fast|balanced|deep."). Its code is
                    // the documented one, and the message already tells the model what to fix.
                    return Fail(wire, "invalid_model_profile", exception.Message);
                }
                return Ok(wire, new
                {
                    ok = true,
                    action,
                    provider_instance_id = updated.ProviderInstanceId,
                    model = updated.Model,
                    profile = updated.Profile,
                    model_profiles = updated.ModelProfiles,
                    revision = updated.Revision,
                    updated_at = updated.UpdatedAt,
                    note = "Profile metadata only. API keys and secret references are never read or written here.",
                });
            }
            default:
                return Fail(wire, "invalid_request", "action must be inspect or update.");
        }
    }

    private static SanitizedProviderCatalogItemDto[] FilterProviders(SanitizedModelCatalogDto catalog, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return catalog.Providers.ToArray();
        var needle = reference.Trim();
        return catalog.Providers.Where(provider =>
                provider.Id.ToString().Equals(needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(provider.DisplayName, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(provider.DefaultModel, needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static bool HasProfileChange(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && (payload.TryGetProperty("specialties", out _) || payload.TryGetProperty("tier", out _)
            || payload.TryGetProperty("notes", out _) || payload.TryGetProperty("context_window", out _)
            || payload.TryGetProperty("cost_tier", out _));

    /// <summary>
    /// A per-request failure that carries structured diagnostics. Thrown so a deep
    /// helper can refuse without threading a response object through every caller; the
    /// outer catch turns it into the wire failure.
    /// </summary>
    private sealed class WireRefusal(string code, string message, object? diagnostics) : Exception(message)
    {
        public string Code { get; } = code;
        public object? Diagnostics { get; } = diagnostics;
    }

    private static WireRefusal Refuse(string code, string message, object? diagnostics = null) =>
        new(code, message, diagnostics);

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

    // ── reference resolution ──
    //
    // Every tool that names a resource accepts the id shape a model can actually see:
    // a slug from tinadec_inspect, not only a GUID. A GUID lookup that silently treats
    // a slug as "not found" is what makes a model retry with a fabricated id.

    private async Task<Guid?> ResolveAgentIdAsync(AgentConfigurationDbContext db, ToolInvocationScope scope, string reference, CancellationToken ct)
    {
        var agent = await FindAgentAsync(db, scope, reference, ct).ConfigureAwait(false);
        return agent?.Id;
    }

    private static async Task<AgentDefinitionRecord?> FindAgentAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        string reference,
        CancellationToken ct)
    {
        var needle = reference.Trim();
        if (Guid.TryParse(needle, out var id) && id != Guid.Empty)
        {
            var byId = await db.AgentDefinitions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct).ConfigureAwait(false);
            if (byId is not null) return byId;
        }
        // Slugs are unique per workspace per status, and an archived row must not shadow
        // the live one. Two queries rather than an ORDER BY on a CASE expression: the
        // second only runs when an archived duplicate actually exists.
        var live = await db.AgentDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Slug == needle && x.Status != "archived", ct).ConfigureAwait(false);
        return live ?? await db.AgentDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == needle, ct)
            .ConfigureAwait(false);
    }

    private async Task<Guid?> ResolveModeIdAsync(ToolInvocationScope scope, string reference, CancellationToken ct)
    {
        var needle = reference.Trim();
        if (Guid.TryParse(needle, out var id) && id != Guid.Empty) return id;
        // Slug resolution has to read the table: IAgentTopologyService exposes no
        // lookup-by-slug, and adding one to a file this worker does not own would be a
        // cross-slice edit. Reported to core-topology; they may fold it into the service.
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var live = await db.AgentModes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.Slug == needle && x.Status != "archived", ct).ConfigureAwait(false);
        if (live is not null) return live.Id;
        var archived = await db.AgentModes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == needle, ct)
            .ConfigureAwait(false);
        return archived?.Id;
    }

    private static async Task<PromptPipelineRecord?> FindPromptPipelineAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        string reference,
        CancellationToken ct)
    {
        var needle = reference.Trim();
        if (Guid.TryParse(needle, out var id) && id != Guid.Empty)
        {
            var byId = await db.PromptPipelines.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct).ConfigureAwait(false);
            if (byId is not null) return byId;
        }
        return await db.PromptPipelines.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == needle, ct)
            .ConfigureAwait(false);
    }

    private async Task<Guid?> ResolveFragmentIdAsync(string reference, CancellationToken ct)
    {
        if (_promptFragments is null) return null;
        var needle = reference.Trim();
        if (Guid.TryParse(needle, out var id) && id != Guid.Empty)
        {
            var byId = await _promptFragments.GetAsync(id, ct).ConfigureAwait(false);
            if (byId is not null) return byId.Id;
        }
        // A fragment's model-facing identity is its key, e.g. "arch-baseline".
        var all = await _promptFragments.ListAsync(cancellationToken: ct).ConfigureAwait(false);
        var matches = all.Where(fragment => string.Equals(fragment.Key, needle, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    /// <summary>One refusal a helper hands back: the code and message <see cref="Fail"/> needs.</summary>
    private readonly record struct WireError(string Code, string Message);

    /// <summary>
    /// Refuses a base_prompt_pipeline_id that names nothing, at write time rather than at
    /// the next run. A draft pipeline is acceptable — an agent is a draft too — but a
    /// fabricated GUID is not, because it would make the agent publish with an empty
    /// prompt layer and nobody would know why.
    /// </summary>
    private static async Task<WireError?> ResolvePromptPipelineAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        JsonElement payload,
        CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("base_prompt_pipeline_id", out var element)
            || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String || !Guid.TryParse(element.GetString(), out var pipelineId) || pipelineId == Guid.Empty)
            return new WireError("invalid_request", "payload.base_prompt_pipeline_id must be a prompt pipeline GUID or null.");
        var exists = await db.PromptPipelines.AsNoTracking()
            .AnyAsync(x => x.Id == pipelineId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct).ConfigureAwait(false);
        return exists
            ? null
            : new WireError("not_found",
                $"No prompt pipeline '{pipelineId}' in this workspace. Call tinadec_inspect target=prompts for the real pipeline ids.");
    }

    /// <summary>One revision-guarded attempt at recording an agent's runtime binding.</summary>
    private sealed record RuntimeBindingOutcome(RuntimeBinding? Binding, WireError? Failure);

    /// <summary>What a model_binding payload settles to; null when the caller sent none.</summary>
    private sealed record RuntimeBinding(
        string Mode,
        Guid? ProviderInstanceId,
        string? Model,
        string? RoutePurpose,
        long Revision);

    /// <summary>
    /// Translates <c>payload.model_binding</c> into the same inherit|route|fixed triple the
    /// runtime-binding endpoint accepts, and refuses the combinations that endpoint
    /// refuses. Validation only: the row is written after the agent exists.
    /// </summary>
    private async Task<RuntimeBindingOutcome> ApplyRuntimeBindingAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        Guid? agentId,
        JsonElement payload,
        CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("model_binding", out var binding))
            return new RuntimeBindingOutcome(null, null);
        if (binding.ValueKind == JsonValueKind.Null)
            return new RuntimeBindingOutcome(new RuntimeBinding("inherit", null, null, null, 0), null);
        if (binding.ValueKind != JsonValueKind.Object)
            return new RuntimeBindingOutcome(null, new WireError("invalid_request", "payload.model_binding must be an object or null."));

        var mode = (Text(binding, "mode") ?? Text(binding, "strategy") ?? "fixed").Trim().ToLowerInvariant();
        if (mode is not ("inherit" or "route" or "fixed"))
            return new RuntimeBindingOutcome(null, new WireError("invalid_request", "model_binding.mode must be inherit, route or fixed (or omit it for fixed)."));
        var providerInstanceId = GuidOf(binding, "provider_instance_id") ?? GuidOf(binding, "provider_id");
        var model = Text(binding, "model") ?? Text(binding, "model_id");
        var routePurpose = Text(binding, "route_purpose") ?? Text(binding, "purpose");
        if (mode == "fixed" && providerInstanceId is null)
            return new RuntimeBindingOutcome(null, new WireError("invalid_request", "model_binding.mode=fixed needs provider_instance_id."));
        if (mode == "route" && string.IsNullOrWhiteSpace(routePurpose))
            return new RuntimeBindingOutcome(null, new WireError("invalid_request", "model_binding.mode=route needs route_purpose."));

        long revision = 0;
        if (agentId is { } existingId)
        {
            var row = await db.AgentRuntimeBindings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.AgentDefinitionId == existingId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct)
                .ConfigureAwait(false);
            revision = row?.Revision ?? 0;
            var expected = ReadInt(payload, "expected_binding_revision", -1);
            if (expected >= 0 && row is not null && row.Revision != expected)
                return new RuntimeBindingOutcome(null, new WireError("revision_conflict", $"Model binding revision conflict: expected {expected}, actual {row.Revision}."));
        }
        return new RuntimeBindingOutcome(new RuntimeBinding(
            mode,
            mode == "fixed" ? providerInstanceId : null,
            mode == "fixed" ? model : null,
            mode == "route" ? routePurpose : null,
            revision), null);
    }

    /// <summary>
    /// Writes (or clears) the agent's runtime binding. The three modes are mutually
    /// exclusive, so switching one clears the other mode's fields — a stale provider id
    /// left behind reads as a valid configuration on the next run.
    /// </summary>
    private async Task SaveRuntimeBindingAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        Guid agentId,
        RuntimeBinding binding,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var row = await db.AgentRuntimeBindings
            .FirstOrDefaultAsync(x => x.AgentDefinitionId == agentId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new AgentRuntimeBindingRecord { AgentDefinitionId = agentId, TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, Revision = 1 };
            db.AgentRuntimeBindings.Add(row);
        }
        else row.Revision++;
        row.Mode = binding.Mode;
        row.ProviderInstanceId = binding.ProviderInstanceId;
        row.Model = binding.Model;
        row.RoutePurpose = binding.RoutePurpose;
        row.UpdatedAt = now;
        row.UpdatedByPrincipalId = scope.PrincipalId;
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException)
        {
            throw Refuse("revision_conflict", "The agent's model binding changed since it was read. Re-inspect and retry.");
        }
    }

    /// <summary>
    /// Applies <c>payload.tool_settings</c> ({ mcp_server_ids, skill_resource_ids, plus the
    /// optional tool-family switches }) as this agent's own tool-settings document.
    /// A supplied list REPLACES that binding (an empty array means "none"), which is what
    /// a caller writing "this agent uses these two MCP servers" means.
    /// </summary>
    private async Task<string?> ApplyToolSettingsAsync(Guid agentId, JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("tool_settings", out var settings) || settings.ValueKind == JsonValueKind.Null)
            return null;
        if (_toolSettingsStore is null) return "tool_settings was ignored: this host registers no tool settings store.";
        if (settings.ValueKind != JsonValueKind.Object)
            throw Refuse("invalid_request", "payload.tool_settings must be an object.");
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        // Each section is built once and then filled, so a caller that sends both a
        // selection and an enabled flag does not lose one of them to a second assignment.
        if (settings.TryGetProperty("mcp_server_ids", out _) || settings.TryGetProperty("mcp_enabled", out _))
        {
            var mcp = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (settings.TryGetProperty("mcp_server_ids", out _)) mcp["server_resource_ids"] = ReadStringArray(settings, "mcp_server_ids");
            if (settings.TryGetProperty("mcp_enabled", out var mcpEnabled) && mcpEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                mcp["enabled"] = mcpEnabled.ValueKind == JsonValueKind.True;
            patch["mcp"] = mcp;
        }
        if (settings.TryGetProperty("skill_resource_ids", out _) || settings.TryGetProperty("skills_enabled", out _))
        {
            var skills = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (settings.TryGetProperty("skill_resource_ids", out _)) skills["resource_ids"] = ReadStringArray(settings, "skill_resource_ids");
            if (settings.TryGetProperty("skills_enabled", out var skillsEnabled) && skillsEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                skills["enabled"] = skillsEnabled.ValueKind == JsonValueKind.True;
            patch["skills"] = skills;
        }
        if (patch.Count == 0)
            throw Refuse("invalid_request",
                "payload.tool_settings needs at least one of mcp_server_ids, skill_resource_ids, mcp_enabled, skills_enabled.");
        var document = await _toolSettingsStore.GetAsync(agentId, ct).ConfigureAwait(false);
        // Merge into the agent's OWN patch rather than its effective settings: a caller
        // adding one MCP server must not flatten overrides someone else configured, and
        // storing the merged effective document would turn shared defaults into agent
        // overrides (which then fail the narrowing check on the next save).
        var merged = MergeJsonObjects(document.Settings, JsonSerializer.SerializeToElement(patch));
        await _toolSettingsStore.SaveAsync(agentId, merged, document.Revision, ct).ConfigureAwait(false);
        return "Applied as this agent's tool settings (MCP server / Skill bindings).";
    }

    /// <summary>
    /// Shallow-per-section merge of two settings patches: an existing section keeps its
    /// fields, a new section is added whole. The store validates the result against the
    /// schema, so this only has to avoid dropping what is already there.
    /// </summary>
    private static JsonElement MergeJsonObjects(JsonElement current, JsonElement patch)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (current.ValueKind == JsonValueKind.Object)
            foreach (var property in current.EnumerateObject()) result[property.Name] = property.Value;
        if (patch.ValueKind == JsonValueKind.Object)
            foreach (var property in patch.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && result.TryGetValue(property.Name, out var existing) && existing.ValueKind == JsonValueKind.Object)
                {
                    var mergedSection = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var field in existing.EnumerateObject()) mergedSection[field.Name] = field.Value;
                    foreach (var field in property.Value.EnumerateObject()) mergedSection[field.Name] = field.Value;
                    result[property.Name] = JsonSerializer.SerializeToElement(mergedSection);
                }
                else result[property.Name] = property.Value;
            }
        return JsonSerializer.SerializeToElement(result);
    }

    // ── prompt helpers ──

    private static PromptFragmentSaveRequestDto FragmentRequest(JsonElement payload) => new()
    {
        Key = Text(payload, "key"),
        Title = Text(payload, "title"),
        Scope = Text(payload, "scope"),
        TargetAgentId = Text(payload, "target_agent_id"),
        TargetAgentSlug = Text(payload, "target_agent_slug"),
        Category = Text(payload, "category"),
        Content = Text(payload, "content"),
        Priority = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number && priority.TryGetInt32(out var value) ? value : null,
        Enabled = ReadNullableBool(payload, "enabled"),
        ChangeSummary = Text(payload, "change_summary"),
    };

    private static object DescribeFragment(PromptFragmentDto fragment) => new
    {
        id = fragment.Id,
        key = fragment.Key,
        title = fragment.Title,
        scope = fragment.Scope,
        category = fragment.Category,
        target_agent_id = fragment.TargetAgentId,
        target_agent_slug = fragment.TargetAgentSlug,
        priority = fragment.Priority,
        enabled = fragment.Enabled,
        is_builtin = fragment.IsBuiltIn,
        revision = fragment.Revision,
        content = fragment.Content,
        updated_at = fragment.UpdatedAt,
    };

    private static string? FragmentNote(PromptFragmentDto fragment) => fragment.IsBuiltIn
        ? "A built-in fragment: readable, not editable. Clone it to customize."
        : null;

    private static object DescribePipeline(PromptPipelineRecord pipeline, bool builtIn) => new
    {
        id = pipeline.Id,
        slug = pipeline.Slug,
        display_name = pipeline.DisplayName,
        description = pipeline.Description,
        status = pipeline.Status,
        version = pipeline.Version,
        revision = pipeline.Revision,
        graph = Element(pipeline.GraphJson),
        built_in = builtIn,
        writable = !builtIn && string.Equals(pipeline.Status, "draft", StringComparison.Ordinal),
        created_at = pipeline.CreatedAt,
        updated_at = pipeline.UpdatedAt,
    };

    private static string? PipelineNote(PromptPipelineRecord pipeline) =>
        string.Equals(pipeline.Status, "draft", StringComparison.Ordinal)
            ? "Saved as a draft; call target=pipeline action=publish when the graph is right."
            : null;

    private static string? GraphPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("graph", out var graph) && graph.ValueKind != JsonValueKind.Null) return graph.GetRawText();
        if (payload.TryGetProperty("graph_json", out var graphJson) && graphJson.ValueKind == JsonValueKind.String) return graphJson.GetString();
        // A caller that sends nodes+edges instead of a wrapper gets the wrapper built for
        // it: the canvas and the tools should not need two different shapes.
        if (payload.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Serialize(new
            {
                nodes,
                edges = payload.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array
                    ? edges
                    : JsonSerializer.SerializeToElement(Array.Empty<object>()),
                canvas_layout = payload.TryGetProperty("canvas_layout", out var layout) ? layout : (JsonElement?)null,
            });
        return null;
    }

    /// <summary>The node kinds the assembler can execute.</summary>
    private static readonly IReadOnlyList<string> PromptNodeKinds =
        ["template", "variable", "fragment_ref", "condition", "workspace_rules", "skills_index", "assemble", "stage", "stage_select", "retrieval", "read_only_retrieval", "memory", "budget", "context_trim"];

    private static string? ValidatePipelineGraph(string graphJson)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(graphJson); }
        catch (JsonException exception) { return $"graph is not valid JSON: {exception.Message}"; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "graph must be an object with nodes[] and edges[].";
            if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return "graph.nodes must be an array (an empty pipeline is a draft, not a graph).";
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) return "graph.nodes must contain objects.";
                var key = Text(node, "node_key") ?? Text(node, "id");
                if (string.IsNullOrWhiteSpace(key)) return "Every prompt node needs a stable node_key.";
                if (!keys.Add(key.Trim())) return $"Duplicate prompt node key '{key}'.";
                var kind = (Text(node, "kind") ?? Text(node, "type"))?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(kind)) return $"Prompt node '{key}' needs a kind (e.g. template, fragment_ref, assemble).";
                if (!PromptNodeKinds.Contains(kind, StringComparer.Ordinal))
                    return $"Prompt node '{key}' has unknown kind '{kind}'. Known kinds: {string.Join(", ", PromptNodeKinds)}.";
            }
            if (root.TryGetProperty("edges", out var edges))
            {
                if (edges.ValueKind != JsonValueKind.Array) return "graph.edges must be an array.";
                foreach (var edge in edges.EnumerateArray())
                {
                    if (edge.ValueKind != JsonValueKind.Object) return "graph.edges must contain objects.";
                    var source = Text(edge, "source_node_key") ?? Text(edge, "source") ?? Text(edge, "from");
                    var target = Text(edge, "target_node_key") ?? Text(edge, "target") ?? Text(edge, "to");
                    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                        return "Every prompt edge needs source_node_key and target_node_key.";
                    if (!keys.Contains(source.Trim()) || !keys.Contains(target.Trim()))
                        return $"Prompt edge '{source}' -> '{target}' references a node that is not declared.";
                }
            }
            return null;
        }
    }

    private static string? PipelinePublishError(string graphJson)
    {
        try
        {
            using var document = JsonDocument.Parse(graphJson);
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var node in nodes.EnumerateArray())
                {
                    var kind = (Text(node, "kind") ?? Text(node, "type"))?.Trim().ToLowerInvariant();
                    if (kind is not null) kinds.Add(kind);
                }
            if (!kinds.Contains("template"))
                return "A pipeline cannot be published without at least one template node: the assembler would produce no text.";
            if (!kinds.Contains("assemble"))
                return "A pipeline cannot be published without an assemble node: the template output would never be collected.";
            return null;
        }
        catch (JsonException exception)
        {
            return $"graph is not valid JSON: {exception.Message}";
        }
    }

    private static int CountGraphNodes(string? graphJson)
    {
        if (string.IsNullOrWhiteSpace(graphJson)) return 0;
        try
        {
            using var document = JsonDocument.Parse(graphJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
                    ? nodes.GetArrayLength()
                    : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static async Task<string> UniquePipelineSlugAsync(
        AgentConfigurationDbContext db,
        string candidate,
        ToolInvocationScope scope,
        CancellationToken ct)
    {
        var slug = AgentTopologyService.Slugify(candidate);
        if (string.IsNullOrWhiteSpace(slug)) slug = "pipeline";
        var taken = (await db.PromptPipelines.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
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

    private static string? NormalizeTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return null;
        var normalized = tier.Trim().ToLowerInvariant();
        if (normalized == "solo") return "solo_dispatch";
        return normalized is "solo_dispatch" or "free_form" or "self_dispatch" or "deterministic" ? normalized : null;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonElement ParseJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<JsonElement>(json);

    private static JsonElement? Element(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return null; }
    }

    private static int? ReadNullableInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>
    /// Where a new agent came from. An explicit payload.source_kind always wins. When it
    /// is absent the CALLING CONTEXT decides: a run means an agent generated this, so the
    /// row is "agent_generated"; no run means a person authored it, so it is "custom".
    /// Guessing "custom" for a run-originated agent would erase the only evidence that a
    /// model, not a human, created it.
    /// </summary>
    private static string DefaultSourceKind(JsonElement payload, ToolInvocationScope scope)
    {
        var explicitKind = Text(payload, "source_kind")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(explicitKind)) return explicitKind;
        return scope.RunId != Guid.Empty ? "agent_generated" : "custom";
    }

    /// <summary>
    /// Records where a generated agent came from. An explicit payload.provenance wins;
    /// otherwise the run that made the call is the provenance, which is what makes
    /// "agent_generated" meaningful instead of a label with no evidence behind it.
    /// </summary>
    private static string? Provenance(ToolInvocationScope scope, JsonElement payload, string sourceKind, Guid? clonedFromAgentId = null)
    {
        var explicitProvenance = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("provenance", out var provenance)
            && provenance.ValueKind == JsonValueKind.Object
                ? provenance
                : (JsonElement?)null;
        var hasRun = scope.RunId != Guid.Empty;
        // A person editing in the Agent Center has no run; an agent runtime always does.
        // Defaulting on that fact is what keeps "custom" honest.
        var generated = !string.Equals(sourceKind, "custom", StringComparison.Ordinal) || hasRun;
        if (!generated && explicitProvenance is null && clonedFromAgentId is null) return null;
        return JsonSerializer.Serialize(new
        {
            created_by_agent_id = Text(explicitProvenance ?? default, "created_by_agent_id")
                ?? Text(explicitProvenance ?? default, "agent_id")
                ?? (scope.AgentInstanceId != Guid.Empty ? scope.AgentInstanceId.ToString() : null),
            created_by_run_id = Text(explicitProvenance ?? default, "created_by_run_id")
                ?? Text(explicitProvenance ?? default, "run_id")
                ?? (hasRun ? scope.RunId.ToString() : null),
            created_by_principal_id = scope.PrincipalId.ToString(),
            cloned_from_agent_id = clonedFromAgentId?.ToString(),
            reason = Text(explicitProvenance ?? default, "reason"),
            source_kind = sourceKind,
            created_at = DateTimeOffset.UtcNow,
        });
    }

    private static ToolWireResponseDto NotFound(ToolWireRequestDto wire, string kind, string reference) =>
        Fail(wire, "not_found", $"No {kind} matches '{reference}' in this tenant and workspace. Call tinadec_inspect for the real ids and slugs.");

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
