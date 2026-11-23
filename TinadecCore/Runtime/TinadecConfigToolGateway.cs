using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Context;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Tools;

namespace TinadecCore.Runtime;

/// <summary>
/// Executes the <c>tinadec_*</c> workbench-configuration tools against the live Core
/// services. It is the only implementation of that gateway: the HTTP endpoints, agent
/// runtime tool calls, and the loopback MCP server share <see cref="IAgentTopologyService"/>,
/// <see cref="IModelProfileCatalogService"/>, and <see cref="IPromptFragmentService"/>.
///
/// Rules that hold for every action here:
/// <list type="bullet">
/// <item>Scope comes from the run (<see cref="ToolInvocationScope"/>), never from model
/// arguments — a caller cannot name another tenant, workspace or session.</item>
/// <item>Pack-managed resources are read-only and answer <c>built_in_read_only</c>;
/// cloning is how a caller gets an editable copy.</item>
/// <item>Results are sanitized and bounded: no secrets, no environment values, no skill
/// bodies, at most <see cref="MaxRows"/> rows.</item>
/// <item>Writes enforce validation, optimistic revision checks, and version history snapshots.</item>
/// </list>
/// </summary>
public sealed class TinadecConfigToolGateway : ITinadecConfigToolGateway, IContextAuditCatalogReader, IRunModelInvocationReader
{
    private const int MaxRows = 50;
    private const int MaxText = 240;

    private static readonly HashSet<string> AllowedPromptNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "template", "variable", "condition", "stage", "stage_select",
        "read_only_retrieval", "retrieval", "memory", "budget",
        "assemble", "context_trim", "fragment_ref", "workspace_rules", "skills_index",
    };

    private static readonly HashSet<string> ForbiddenModelProfileSecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key", "api_key_ref", "apikey", "secret", "secret_ref", "token", "password", "base_url", "headers",
    };

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
    private readonly IModelProfileCatalogService? _modelProfiles;
    private readonly IPromptFragmentService? _promptFragments;
    private readonly IToolSettingsStore? _toolSettingsStore;
    private readonly IMcpServerService? _mcpServers;
    private readonly IExternalCliDiscoveryService? _externalCliDiscovery;
    private readonly IRunReplayService? _runReplay;
    private readonly ILifecycleManager? _lifecycle;
    private readonly IDbContextFactory<LifecycleDbContext>? _lifecycleDb;
    private readonly ReadinessService? _readiness;
    private readonly IServerFailureReader? _failureReader;
    private readonly IContextMemoryService? _contextMemory;
    private readonly IToolExecutionCoordinator? _toolExecutions;

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
        IModelProfileCatalogService? modelProfiles = null,
        IPromptFragmentService? promptFragments = null,
        IToolSettingsStore? toolSettingsStore = null,
        IMcpServerService? mcpServers = null,
        IExternalCliDiscoveryService? externalCliDiscovery = null,
        IRunReplayService? runReplay = null,
        ILifecycleManager? lifecycle = null,
        IDbContextFactory<LifecycleDbContext>? lifecycleDb = null,
        ReadinessService? readiness = null,
        IServerFailureReader? failureReader = null,
        IContextMemoryService? contextMemory = null,
        IToolExecutionCoordinator? toolExecutions = null)
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
        _modelProfiles = modelProfiles;
        _promptFragments = promptFragments;
        _toolSettingsStore = toolSettingsStore;
        _mcpServers = mcpServers;
        _externalCliDiscovery = externalCliDiscovery;
        _runReplay = runReplay;
        _lifecycle = lifecycle;
        _lifecycleDb = lifecycleDb;
        _readiness = readiness;
        _failureReader = failureReader;
        _contextMemory = contextMemory;
        _toolExecutions = toolExecutions;
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
            if (CoreVirtualToolPolicy.IsTinadecContextMemoryApply(toolId)) return await ContextMemoryApplyAsync(scope, wire, ct).ConfigureAwait(false);
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
        catch (ModelProfileException exception)
        {
            return Fail(wire, exception.Code, exception.Message);
        }
        catch (PromptFragmentException exception)
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
            case "agent":
                return await InspectAgentDetailAsync(scope, wire, parameters, ct).ConfigureAwait(false);
            case "catalog":
                return Ok(wire, await InspectCatalogAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "modes":
                return Ok(wire, await InspectModesAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "mode":
            {
                var rawModeId = Text(parameters, "mode_id") ?? Text(parameters, "id") ?? Text(parameters, "slug");
                if (string.IsNullOrWhiteSpace(rawModeId))
                    return Fail(wire, "invalid_request", "target=mode needs mode_id.");
                var modeId = await ResolveModeIdAsync(scope, rawModeId, ct).ConfigureAwait(false);
                if (modeId is null)
                    return Fail(wire, AgentTopologyCodes.NotFound, "Mode not found.");
                var result = await _topology.GetAsync(modeId.Value, ct).ConfigureAwait(false);
                return result.Ok ? Ok(wire, new { ok = true, target = "mode", mode = result.Mode }) : TopologyFailure(wire, result);
            }
            case "models":
                return Ok(wire, await InspectModelsAsync(query, limit, ct).ConfigureAwait(false));
            case "prompts":
                return Ok(wire, await InspectPromptsAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "prompt_pipeline":
                return await InspectPromptPipelineDetailAsync(scope, wire, parameters, ct).ConfigureAwait(false);
            case "tools":
                return Ok(wire, await InspectToolsAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "skills":
                return Ok(wire, await InspectSkillsAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "mcp":
                return Ok(wire, await InspectMcpAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "external_cli":
                return Ok(wire, await InspectExternalCliAsync(scope, query, limit, ct).ConfigureAwait(false));
            case "run_replay":
                return await InspectRunReplayAsync(scope, wire, parameters, ct).ConfigureAwait(false);
            case "recent_failures":
                return Ok(wire, InspectRecentFailures(query, limit));
            case "system_health":
                return Ok(wire, await InspectSystemHealthAsync(scope, ct).ConfigureAwait(false));
            case "context_memory":
                return await InspectContextMemoryAsync(scope, wire, parameters, ct).ConfigureAwait(false);
            case "context_audit":
                return await InspectContextAuditAsync(scope, wire, parameters, limit, ct).ConfigureAwait(false);
            case "run_diagnostics":
                return await InspectRunDiagnosticsAsync(scope, wire, parameters, limit, ct).ConfigureAwait(false);
            default:
                return Fail(wire, "invalid_request", "target must be one of agents, agent, modes, mode, catalog, models, prompts, prompt_pipeline, tools, skills, mcp, external_cli, run_replay, recent_failures, system_health, context_memory, context_audit, run_diagnostics.");
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
                    base_prompt_pipeline_id = x.BasePromptPipelineId,
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

    private async Task<ToolWireResponseDto> InspectAgentDetailAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        JsonElement parameters,
        CancellationToken ct)
    {
        var rawId = Text(parameters, "agent_id") ?? Text(parameters, "id") ?? Text(parameters, "slug");
        if (string.IsNullOrWhiteSpace(rawId))
            return Fail(wire, "invalid_request", "target=agent needs agent_id (GUID or slug).");

        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var agent = await ResolveAgentRecordAsync(db, scope, rawId, asNoTracking: true, ct).ConfigureAwait(false);
        if (agent is null)
            return Fail(wire, "not_found", $"Agent '{rawId}' not found.");

        var ownership = await PackOwnershipAsync(db, scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        ownership.TryGetValue(agent.Id, out var owner);
        var builtIn = owner.PackId is not null;
        var binding = await db.AgentRuntimeBindings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentDefinitionId == agent.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct)
            .ConfigureAwait(false);
        ToolSettingsDocumentDto? toolSettings = null;
        if (_toolSettingsStore is not null)
        {
            try { toolSettings = await _toolSettingsStore.GetAsync(agent.Id, ct).ConfigureAwait(false); }
            catch { /* non-fatal if store unavailable */ }
        }

        var toolScope = ParseStringArray(agent.ToolScopeJson);
        var capabilities = ParseStringArray(agent.CapabilitiesJson);
        return Ok(wire, new
        {
            ok = true,
            target = "agent",
            agent = new
            {
                id = agent.Id,
                slug = agent.Slug,
                display_name = agent.DisplayName,
                description = agent.Description,
                layer = agent.Layer,
                role = agent.Role,
                status = agent.Status,
                enabled = agent.Enabled,
                version = agent.Version,
                revision = agent.Revision,
                source_kind = agent.SourceKind,
                provenance = ParseJsonOrNull(agent.ProvenanceJson),
                base_prompt_pipeline_id = agent.BasePromptPipelineId,
                system_prompt = agent.SystemPrompt,
                model_strategy = ParseJsonOrNull(agent.ModelStrategyJson) ?? DefaultModelStrategyElement(),
                tool_scope = toolScope,
                tool_ids = toolScope,
                capabilities,
                pack_id = owner.PackId,
                built_in = builtIn,
                writable = !builtIn && !string.Equals(agent.Status, "archived", StringComparison.Ordinal),
                model_binding = binding is null ? null : DescribeRuntimeBinding(binding),
                tool_settings = toolSettings?.Settings,
            },
        });
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
                source_kind = x.SourceKind,
                base_prompt_pipeline_id = x.BasePromptPipelineId,
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
                    orchestration_tier = x.OrchestrationTier,
                    status = x.Status,
                    revision = x.Revision,
                    version = x.Version,
                    node_count = counts.GetValueOrDefault(x.Id),
                    pack_id = owner.PackId,
                    built_in = builtIn,
                    writable = !builtIn && !string.Equals(x.Status, "archived", StringComparison.Ordinal),
                };
            }).ToArray(),
        };
    }

    private async Task<object> InspectModelsAsync(string? query, int limit, CancellationToken ct)
    {
        if (_modelProfiles is null)
        {
            return new
            {
                target = "models",
                available = false,
                total = 0,
                providers = Array.Empty<object>(),
                routes = Array.Empty<object>(),
                health = Array.Empty<object>(),
                note = "No model profile catalog service is registered in this host.",
            };
        }

        var catalog = await _modelProfiles.GetSanitizedCatalogAsync(includeDisabled: false, ct).ConfigureAwait(false);
        var filteredProviders = catalog.Providers
            .Where(p => Matches(query, p.DisplayName, p.Driver, p.Protocol, p.DefaultModel)
                || p.Models.Any(m => Matches(query, m))
                || p.ModelProfiles.Any(kv => Matches(query, kv.Key, kv.Value.Tier, kv.Value.Notes) || kv.Value.Specialties.Any(s => Matches(query, s))))
            .ToArray();

        return new
        {
            target = "models",
            available = true,
            total = filteredProviders.Length,
            truncated = filteredProviders.Length > limit,
            providers = filteredProviders.Take(limit).ToArray(),
            routes = catalog.Routes.Take(limit).ToArray(),
            health = catalog.Health.Take(limit).ToArray(),
        };
    }

    private async Task<object> InspectPromptsAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        IReadOnlyList<PromptFragmentDto> fragments = [];
        if (_promptFragments is not null)
        {
            fragments = await _promptFragments.ListAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        var filteredFragments = fragments
            .Where(f => Matches(query, f.Key, f.Title, f.Category, f.Scope, f.TargetAgentSlug, f.Content))
            .ToArray();

        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pipelines = await db.PromptPipelines.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(ct).ConfigureAwait(false);
        var ownership = await PackOwnershipAsync(db, scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        var filteredPipelines = pipelines
            .Where(p => Matches(query, p.Slug, p.DisplayName, p.Description))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            target = "prompts",
            fragments_total = filteredFragments.Length,
            fragments_truncated = filteredFragments.Length > limit,
            fragments = filteredFragments.Take(limit).Select(f => new
            {
                id = f.Id,
                key = f.Key,
                title = f.Title,
                scope = f.Scope,
                target_agent_id = f.TargetAgentId,
                target_agent_slug = f.TargetAgentSlug,
                category = f.Category,
                priority = f.Priority,
                enabled = f.Enabled,
                is_builtin = f.IsBuiltIn,
                revision = f.Revision,
                content_preview = Truncate(f.Content, MaxText),
            }).ToArray(),
            pipelines_total = filteredPipelines.Length,
            pipelines_truncated = filteredPipelines.Length > limit,
            pipelines = filteredPipelines.Take(limit).Select(p =>
            {
                ownership.TryGetValue(p.Id, out var owner);
                var builtIn = owner.PackId is not null;
                return new
                {
                    id = p.Id,
                    slug = p.Slug,
                    display_name = p.DisplayName,
                    description = Truncate(p.Description, MaxText),
                    status = p.Status,
                    version = p.Version,
                    revision = p.Revision,
                    pack_id = owner.PackId,
                    built_in = builtIn,
                    writable = !builtIn && !string.Equals(p.Status, "archived", StringComparison.Ordinal),
                };
            }).ToArray(),
        };
    }

    private async Task<ToolWireResponseDto> InspectPromptPipelineDetailAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        JsonElement parameters,
        CancellationToken ct)
    {
        var rawId = Text(parameters, "pipeline_id") ?? Text(parameters, "id") ?? Text(parameters, "slug");
        if (string.IsNullOrWhiteSpace(rawId))
            return Fail(wire, "invalid_request", "target=prompt_pipeline needs pipeline_id (GUID or slug).");

        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pipeline = await ResolvePipelineRecordAsync(db, scope, rawId, asNoTracking: true, ct).ConfigureAwait(false);
        if (pipeline is null)
            return Fail(wire, "not_found", $"Prompt pipeline '{rawId}' not found.");

        var ownership = await PackOwnershipAsync(db, scope.TenantId, scope.WorkspaceId, ct).ConfigureAwait(false);
        ownership.TryGetValue(pipeline.Id, out var owner);
        var builtIn = owner.PackId is not null;
        var versions = await db.PromptVersions.AsNoTracking()
            .Where(x => x.PromptPipelineId == pipeline.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .OrderByDescending(x => x.Version)
            .Take(20)
            .ToListAsync(ct).ConfigureAwait(false);

        return Ok(wire, new
        {
            ok = true,
            target = "prompt_pipeline",
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
                writable = !builtIn && !string.Equals(pipeline.Status, "archived", StringComparison.Ordinal),
                graph = ParseJsonOrNull(pipeline.GraphJson) ?? EmptyGraphElement(),
                versions = versions.Select(v => new
                {
                    id = v.Id,
                    version = v.Version,
                    content_hash = v.ContentHash,
                    status = v.Status,
                    created_at = v.CreatedAt,
                }).ToArray(),
            },
        });
    }

    private async Task<object> InspectToolsAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Id, string Source, string Description, string Risk, bool MutatesWorkspace, bool RequiresApproval)>();

        void AddEntry(string id, string source, string description, string risk, bool mutatesWorkspace, bool requiresApproval)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) return;
            entries.Add((id, source, description, risk, mutatesWorkspace, requiresApproval));
        }

        foreach (var entry in CoreTinadecConfigTools.AllManifestEntries())
        {
            AddEntry(entry.Id, "core_virtual", entry.Description, entry.Risk, entry.MutatesWorkspace, entry.RequiresApproval);
        }

        var extraCoreVirtuals = new (string Id, string Description)[]
        {
            (CoreVirtualToolPolicy.LoadSkillToolId, "Load a Skill package body and bundled resource listing."),
            (CoreVirtualToolPolicy.TaskDispatchToolId, "Dispatch a subtask to an allowed execution or peer agent."),
            (CoreVirtualToolPolicy.TaskWaitToolId, "Wait for dispatched subtask results."),
            (CoreVirtualToolPolicy.PlanUpdateToolId, "Update the live multi-step execution plan."),
            (CoreVirtualToolPolicy.SpecProposeToolId, "Propose a structured specification artifact."),
            (CoreVirtualToolPolicy.CorrectionTraceToolId, "Record a correction trace entry."),
            (CoreVirtualToolPolicy.ReadAttachmentToolId, "Read an uploaded conversation attachment."),
            (CoreVirtualToolPolicy.CreateWorkspaceToolId, "Create and bind a new project workspace."),
            (CoreVirtualToolPolicy.GraphViewToolId, "Inspect the live run orchestration graph."),
            (CoreVirtualToolPolicy.RecallEvidenceToolId, "Recall prior task reports and compacted evidence."),
            (CoreVirtualToolPolicy.TraceSearchToolId, "Search trace events across the active run."),
            (CoreVirtualToolPolicy.RunDossierToolId, "Read the current run dossier summary."),
        };
        foreach (var (id, desc) in extraCoreVirtuals)
            AddEntry(id, "core_virtual", desc, "low", mutatesWorkspace: false, requiresApproval: false);

        foreach (var entry in TeamToolCatalog.Entries)
        {
            var risk = string.Equals(entry.Id, CoreVirtualToolPolicy.TeamIntegrateToolId, StringComparison.OrdinalIgnoreCase) ? "medium" : "low";
            AddEntry(entry.Id, "core_virtual", entry.Description, risk, mutatesWorkspace: false, requiresApproval: false);
        }
        foreach (var id in CoreVirtualToolPolicy.TinaChatToolIds)
            AddEntry(id, "core_virtual", "TinaChat conversation and room coordination tool.", "low", mutatesWorkspace: false, requiresApproval: false);
        foreach (var id in CoreVirtualToolPolicy.OrganizationToolIds)
            AddEntry(id, "core_virtual", "Session organization directory, messaging, and report tool.", "low", mutatesWorkspace: false, requiresApproval: false);
        foreach (var id in CoreVirtualToolPolicy.EnvironmentToolIds)
            AddEntry(id, "core_virtual", "Environment inventory and lease management tool.", "medium", mutatesWorkspace: false, requiresApproval: false);
        foreach (var id in CoreVirtualToolPolicy.ExplorationToolIds)
            AddEntry(id, "core_virtual", "Autonomous exploration graph and evidence tool.", "low", mutatesWorkspace: false, requiresApproval: false);
        foreach (var id in CoreVirtualToolPolicy.PlanLedgerToolIds)
            AddEntry(id, "core_virtual", "Shared plan ledger read/update tool.", "low", mutatesWorkspace: false, requiresApproval: false);

        if (_provider is not null)
        {
            try
            {
                var root = !string.IsNullOrWhiteSpace(scope.WorkspaceRoot)
                    ? scope.WorkspaceRoot
                    : _configuration?["TinadecTools:DefaultWorkspaceRoot"] ?? string.Empty;
                var manifest = await _provider.GetManifestAsync(root, ct).ConfigureAwait(false);
                foreach (var tool in manifest.Tools)
                {
                    AddEntry(tool.Id, "provider", tool.Description, tool.Risk, tool.MutatesWorkspace, tool.RequiresApproval);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                // Provider manifest unavailable without an active workspace root; return Core virtual catalog cleanly.
            }
        }

        var filtered = entries
            .Where(e => Matches(query, e.Id, e.Description, e.Source))
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            target = "tools",
            total = filtered.Length,
            truncated = filtered.Length > limit,
            items = filtered.Take(limit).Select(e => new
            {
                id = e.Id,
                source = e.Source,
                description = Truncate(e.Description, MaxText),
                risk = e.Risk,
                mutates_workspace = e.MutatesWorkspace,
                requires_approval = e.RequiresApproval,
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
        if (_mcpServers is null && _mcp is null)
            return new { target = "mcp", available = false, total = 0, items = Array.Empty<object>(), note = "No MCP registry is registered in this host." };
        var projectId = ProjectOf(scope);
        var resources = _mcpServers is not null
            ? await _mcpServers.ListConfiguredAsync(projectId, ct).ConfigureAwait(false)
            : await _mcp!.ListAsync(projectId, ct).ConfigureAwait(false);
        var filtered = resources.Where(x => Matches(query, x.Id, x.Name, x.Command, x.Url))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            target = "mcp",
            available = true,
            total = filtered.Length,
            truncated = filtered.Length > limit,
            items = filtered.Take(limit).Select(DescribeMcp).ToArray(),
        };
    }

    private async Task<object> InspectExternalCliAsync(ToolInvocationScope scope, string? query, int limit, CancellationToken ct)
    {
        if (_externalCliDiscovery is null)
            return new { target = "external_cli", available = false, total = 0, items = Array.Empty<object>(), note = "External CLI discovery service is not registered in this host." };
        var discovery = await _externalCliDiscovery.DiscoverAsync(ProjectOf(scope), ecosystems: null, ct).ConfigureAwait(false);
        var filtered = discovery.Items
            .Where(x => Matches(query, x.ItemId, x.Ecosystem, x.Kind, x.Name, x.DisplayName, x.Description))
            .ToArray();
        return new
        {
            target = "external_cli",
            available = true,
            ecosystems = discovery.Ecosystems,
            total = filtered.Length,
            truncated = filtered.Length > limit,
            items = filtered.Take(limit).Select(x => new
            {
                item_id = x.ItemId,
                ecosystem = x.Ecosystem,
                kind = x.Kind,
                name = x.Name,
                display_name = x.DisplayName,
                description = Truncate(x.Description, MaxText),
                transport = x.Transport,
                command = x.Command,
                url = x.Url,
                env_keys = x.EnvKeys,
                header_keys = x.HeaderKeys,
                has_secrets = x.HasSecrets,
                default_enabled = x.DefaultEnabled,
                already_imported = x.AlreadyImported,
            }).ToArray(),
        };
    }

    private async Task<ToolWireResponseDto> InspectRunReplayAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        JsonElement parameters,
        CancellationToken ct)
    {
        var rawRunId = Text(parameters, "run_id") ?? Text(parameters, "target_id") ?? Text(parameters, "id");
        if (string.IsNullOrWhiteSpace(rawRunId) || !Guid.TryParse(rawRunId.Trim(), out var runGuid) || runGuid == Guid.Empty)
            return Fail(wire, "invalid_request", "target=run_replay requires a valid run_id (or target_id) GUID.");

        var runIdStr = runGuid.ToString();
        var replay = _runReplay is null ? null : await _runReplay.BuildReplayAsync(runIdStr, ct).ConfigureAwait(false);
        var frozen = _lifecycle is null ? null : await _lifecycle.GetFrozenRunConfigurationAsync(runIdStr, ct).ConfigureAwait(false);
        if (replay is null && frozen is null)
            return Fail(wire, "run_not_found", $"No run replay or frozen snapshot was found for run '{runGuid}'.");

        object? frozenSnapshotSummary = null;
        var promptTrimWarnings = new List<string>();
        if (frozen is not null)
        {
            var frozenJson = ParseJsonOrNull(frozen.Content);
            object? modeSummary = null;
            if (frozenJson is { ValueKind: JsonValueKind.Object } root)
            {
                if (root.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.Object)
                    modeSummary = modeEl.Clone();
                else if (root.TryGetProperty("mode_snapshot", out var msEl) && msEl.ValueKind == JsonValueKind.Object)
                    modeSummary = msEl.Clone();

                if (root.TryGetProperty("prompt_warnings", out var pwEl) && pwEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in pwEl.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } w)
                            promptTrimWarnings.Add(w);
                }
            }

            frozenSnapshotSummary = new
            {
                schema_version = frozen.SchemaVersion,
                frozen_at = frozen.FrozenAt,
                content_hash = frozen.ContentHash,
                content_length = frozen.ContentLength,
                bindings = frozen.Bindings,
                mode_snapshot = modeSummary,
            };
        }

        if (_lifecycle is not null)
        {
            try
            {
                var runState = await _lifecycle.GetRunStateAsync(runIdStr, ct).ConfigureAwait(false);
                if (Guid.TryParse(runState.SessionId, out var sessionGuid))
                {
                    var events = await _lifecycle.ReplayEventsAsync(sessionGuid, 0, ct).ConfigureAwait(false);
                    foreach (var evt in events.Where(e => string.Equals(e.RunId, runIdStr, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (evt.EventType is "context.compacted" or "prompt.trimmed" or "prompt.truncated")
                        {
                            var summary = evt.Payload is not null && evt.Payload.TryGetValue("summary", out var s) && s is not null
                                ? s.ToString()
                                : evt.Payload is not null && evt.Payload.TryGetValue("message", out var m) && m is not null
                                    ? m.ToString()
                                    : evt.EventId;
                            promptTrimWarnings.Add($"{evt.EventType}: {Truncate(summary, MaxText)}");
                        }
                    }
                }
            }
            catch (KeyNotFoundException)
            {
                // Run row may not exist in minimal setups.
            }
        }

        object[] invocations = [];
        if (_lifecycleDb is not null)
        {
            await using var db = await _lifecycleDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = (await db.ModelInvocations.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.RunId == runGuid)
                .ToListAsync(ct).ConfigureAwait(false))
                .OrderBy(x => x.StartedAt)
                .ThenBy(x => x.Attempt)
                .Take(MaxRows)
                .ToArray();
            invocations = rows.Select(x => (object)new
            {
                id = x.Id,
                call_id = x.CallId,
                attempt = x.Attempt,
                provider_instance_id = x.ProviderInstanceId,
                model = x.Model,
                protocol = x.Protocol,
                status = x.Status,
                latency_ms = x.CompletedAt.HasValue ? (long?)Math.Max(0, (x.CompletedAt.Value - x.StartedAt).TotalMilliseconds) : null,
                input_tokens = x.InputTokens,
                output_tokens = x.OutputTokens,
                total_tokens = x.TotalTokens,
                error_category = x.ErrorCategory,
                safe_error_message = Truncate(x.SafeErrorMessage, MaxText),
                started_at = x.StartedAt,
                completed_at = x.CompletedAt,
            }).ToArray();
        }

        return Ok(wire, new
        {
            ok = true,
            target = "run_replay",
            run_id = runGuid,
            replay,
            frozen_snapshot = frozenSnapshotSummary,
            model_invocations = invocations,
            prompt_trim_warnings = promptTrimWarnings.Distinct(StringComparer.Ordinal).Take(MaxRows).ToArray(),
        });
    }

    private object InspectRecentFailures(string? query, int limit)
    {
        if (_failureReader is null)
            return new { target = "recent_failures", available = false, total = 0, items = Array.Empty<object>(), note = "ServerFailureJournal is not registered in this host." };

        var items = _failureReader.Recent()
            .Reverse()
            .Where(f => Matches(query, f.Method, f.Path, f.Code, f.ExceptionType, f.RootType))
            .Take(limit)
            .Select(f => new
            {
                timestamp = f.Timestamp,
                method = f.Method,
                path = RedactSensitiveText(f.Path),
                status = f.Status,
                code = f.Code,
                exception_type = f.ExceptionType,
                exception_message = Truncate(RedactSensitiveText(f.ExceptionMessage), MaxText),
                root_type = f.RootType,
                root_message = Truncate(RedactSensitiveText(f.RootMessage), MaxText),
                trace_id = f.TraceId,
            })
            .ToArray();

        return new
        {
            target = "recent_failures",
            available = true,
            total = items.Length,
            items,
        };
    }

    private async Task<object> InspectSystemHealthAsync(ToolInvocationScope scope, CancellationToken ct)
    {
        ReadinessReceipt? receipt = null;
        if (_readiness is not null)
        {
            try { receipt = await _readiness.GetReceiptAsync(ct).ConfigureAwait(false); }
            catch { /* keep partial diagnostics if readiness fails */ }
        }

        SanitizedModelCatalogDto? modelCatalog = null;
        if (_modelProfiles is not null)
        {
            try { modelCatalog = await _modelProfiles.GetSanitizedCatalogAsync(includeDisabled: false, ct).ConfigureAwait(false); }
            catch { /* non-fatal */ }
        }

        IReadOnlyList<McpResourceDto> configuredMcp = [];
        if (_mcpServers is not null)
            configuredMcp = await _mcpServers.ListConfiguredAsync(ProjectOf(scope), ct).ConfigureAwait(false);
        else if (_mcp is not null)
            configuredMcp = await _mcp.ListAsync(ProjectOf(scope), ct).ConfigureAwait(false);

        return new
        {
            target = "system_health",
            overall_status = receipt?.Status ?? "unknown",
            checked_at = receipt?.CheckedAt ?? DateTimeOffset.UtcNow,
            readiness_items = (receipt?.Items ?? []).Select(i => new
            {
                id = i.Id,
                status = i.Status,
                reason_code = i.ReasonCode,
                reason = Truncate(i.Reason, MaxText),
                action = i.Action,
                checked_at = i.CheckedAt,
            }).ToArray(),
            model_health = (modelCatalog?.Health ?? []).Select(h => new
            {
                provider_instance_id = h.ProviderInstanceId,
                model = h.Model,
                consecutive_failures = h.ConsecutiveFailures,
                is_cooling = h.IsCooling,
                cooling_until = h.CoolingUntil,
                last_category = h.LastCategory,
                updated_at = h.UpdatedAt,
            }).ToArray(),
            mcp_servers = configuredMcp.Take(MaxRows).Select(m => new
            {
                resource_id = m.ResourceId,
                id = m.Id,
                name = m.Name,
                transport = m.Transport,
                enabled = m.Enabled,
                import_source = m.ImportSource,
            }).ToArray(),
        };
    }

    private static string RedactSensitiveText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var redacted = Regex.Replace(input, @"(?i)(bearer\s+)[A-Za-z0-9._\-]+", "$1********");
        redacted = Regex.Replace(redacted, @"(?i)\b(?:sk-[A-Za-z0-9_\-]{8,}|ghp_[A-Za-z0-9]{8,}|xox[baprs]-[A-Za-z0-9\-]{8,})\b", "********");
        redacted = Regex.Replace(redacted, @"(?i)((?:api[_-]?key|token|secret|password|passwd|pwd|authorization)\s*[=:]\s*)([^\s,;""']+)", "$1********");
        return redacted;
    }

    private async Task<ToolWireResponseDto> InspectContextMemoryAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        JsonElement parameters,
        CancellationToken ct)
    {
        if (_contextMemory is null)
            return Fail(wire, "service_unavailable", "Context memory service is not available in this host.");

        var companionId = GuidOf(parameters, "companion_id");
        var projectId = ProjectOf(scope, parameters, parameters);
        var workspaceRoot = string.IsNullOrWhiteSpace(scope.WorkspaceRoot) ? null : scope.WorkspaceRoot;
        var dto = await _contextMemory.InspectAsync(projectId, workspaceRoot, companionId, ct).ConfigureAwait(false);
        return OkSnake(wire, new
        {
            target = "context_memory",
            policy = dto.Policy,
            documents = dto.Documents,
            strategy_presets = dto.StrategyPresets,
        });
    }

    private async Task<ToolWireResponseDto> InspectContextAuditAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        JsonElement parameters,
        int limit,
        CancellationToken ct)
    {
        var projectId = ProjectOf(scope, parameters, parameters);
        var rawRunId = Text(parameters, "run_id");
        Guid? runId = null;
        if (!string.IsNullOrWhiteSpace(rawRunId))
        {
            if (!Guid.TryParse(rawRunId.Trim(), out var parsedRun) || parsedRun == Guid.Empty)
                return Fail(wire, "invalid_request", "run_id must be a valid GUID when provided to target=context_audit.");
            runId = parsedRun;
        }

        var modeId = Text(parameters, "mode_id");
        var agentId = Text(parameters, "agent_id");
        var kGramSize = ReadNullableInt(parameters, "k_gram_size");
        var windowSize = ReadNullableInt(parameters, "window_size");

        var authorizedRoot = !string.IsNullOrWhiteSpace(scope.WorkspaceRoot)
            ? scope.WorkspaceRoot
            : _configuration?["TinadecTools:DefaultWorkspaceRoot"];

        var auditor = new ContextOverlapAuditor(
            _lifecycle,
            _sessions,
            _promptFragments,
            _skills,
            toolRegistry: null,
            _provider,
            _mcpServers,
            catalogReader: this);

        ContextOverlapAuditDto audit;
        try
        {
            audit = await auditor.AuditAsync(
                new ContextOverlapAuditRequestDto
                {
                    ProjectId = projectId,
                    ModeId = modeId,
                    AgentId = agentId,
                    RunId = runId,
                    KGramSize = kGramSize,
                    WindowSize = windowSize,
                    MaxExcerptLength = MaxText,
                    MaxMatchesPerPair = 5
                },
                new ContextAuditExecutionScope(
                    scope.TenantId,
                    scope.WorkspaceId,
                    scope.SessionId,
                    projectId,
                    authorizedRoot),
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            return Fail(wire, "not_found", ex.Message);
        }

        return OkSnake(wire, new
        {
            ok = true,
            target = "context_audit",
            scope = audit.Scope,
            project_id = audit.ProjectId,
            mode_id = audit.ModeId,
            agent_id = audit.AgentId,
            run_id = audit.RunId,
            k_gram_size = audit.KGramSize,
            window_size = audit.WindowSize,
            source_count = audit.SourceCount,
            total_estimated_tokens = audit.TotalEstimatedTokens,
            duplicate_estimated_tokens = audit.DuplicateEstimatedTokens,
            overall_overlap_percent = audit.OverallOverlapPercent,
            pairs_total = audit.Pairs.Count,
            pairs_truncated = audit.Pairs.Count > limit,
            pairs = audit.Pairs.Take(limit).Select(p => new
            {
                id = p.Id,
                source_a_id = p.SourceAId,
                source_a_label = Truncate(RedactSensitiveText(p.SourceALabel), MaxText),
                source_a_kind = p.SourceAKind,
                source_b_id = p.SourceBId,
                source_b_label = Truncate(RedactSensitiveText(p.SourceBLabel), MaxText),
                source_b_kind = p.SourceBKind,
                shared_chars = p.SharedChars,
                overlap_percent = p.OverlapPercent,
                overlap_percent_a = p.OverlapPercentA,
                overlap_percent_b = p.OverlapPercentB,
                estimated_duplicate_tokens = p.EstimatedDuplicateTokens,
                match_count = p.MatchCount,
                remediation_code = p.RemediationCode,
                remediation_summary = p.RemediationSummary,
                matches = p.Matches.Take(5).Select(m => new
                {
                    id = m.Id,
                    source_a_lines = m.SourceALines,
                    source_b_lines = m.SourceBLines,
                    line_range_pair = m.LineRangePair,
                    shared_chars = m.SharedChars,
                    estimated_duplicate_tokens = m.EstimatedDuplicateTokens,
                    excerpt = Truncate(RedactSensitiveText(m.Excerpt), MaxText),
                    remediation_code = m.RemediationCode,
                }).ToArray(),
            }).ToArray(),
            sources_total = audit.Sources.Count,
            sources_truncated = audit.Sources.Count > limit,
            sources = audit.Sources.Take(limit).Select(s => new
            {
                id = s.Id,
                label = Truncate(RedactSensitiveText(s.Label), MaxText),
                kind = s.Kind,
                path = s.Path,
                char_count = s.CharCount,
                line_count = s.LineCount,
                estimated_tokens = s.EstimatedTokens,
                duplicated_chars = s.DuplicatedChars,
                overlap_percent = s.OverlapPercent,
            }).ToArray(),
        });
    }

    private async Task<ToolWireResponseDto> InspectRunDiagnosticsAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        JsonElement parameters,
        int limit,
        CancellationToken ct)
    {
        var rawRunId = Text(parameters, "run_id") ?? Text(parameters, "target_id") ?? Text(parameters, "id");
        if (string.IsNullOrWhiteSpace(rawRunId) || !Guid.TryParse(rawRunId.Trim(), out var runGuid) || runGuid == Guid.Empty)
            return Fail(wire, "invalid_request", "target=run_diagnostics requires a valid run_id (or target_id) GUID.");

        var diagnosticsService = _lifecycle is null
            ? null
            : new RunContextDiagnosticsService(_lifecycle, _runReplay, _toolExecutions, modelInvocations: this);

        if (diagnosticsService is null)
            return Fail(wire, "service_unavailable", "Run context diagnostics service is not available in this host.");

        var diag = await diagnosticsService.DiagnoseRunAsync(
            runGuid,
            scope.TenantId,
            scope.WorkspaceId,
            cancellationToken: ct).ConfigureAwait(false);
        if (diag is null)
            return Fail(wire, "run_not_found", $"No run context diagnostics were found for run '{runGuid}'.");

        return OkSnake(wire, new
        {
            ok = true,
            target = "run_diagnostics",
            run_id = diag.RunId,
            session_id = diag.SessionId,
            status = diag.Status,
            mode_version_id = diag.ModeVersionId,
            generated_at = diag.GeneratedAt,
            window_buckets = new
            {
                total_samples = diag.WindowBuckets.TotalSamples,
                total_errors = diag.WindowBuckets.TotalErrors,
                overall_error_rate = diag.WindowBuckets.OverallErrorRate,
                avg_latency_ms = diag.WindowBuckets.AvgLatencyMs,
                peak_occupancy_pct = diag.WindowBuckets.PeakOccupancyPct,
                low_occupancy_error_rate = diag.WindowBuckets.LowOccupancyErrorRate,
                high_occupancy_error_rate = diag.WindowBuckets.HighOccupancyErrorRate,
                low_occupancy_avg_latency_ms = diag.WindowBuckets.LowOccupancyAvgLatencyMs,
                high_occupancy_avg_latency_ms = diag.WindowBuckets.HighOccupancyAvgLatencyMs,
                degradation_detected = diag.WindowBuckets.DegradationDetected,
                degradation_reason_codes = diag.WindowBuckets.DegradationReasonCodes,
                buckets = diag.WindowBuckets.Buckets,
                samples_truncated = diag.WindowBuckets.Samples.Count > limit,
                samples = diag.WindowBuckets.Samples.Take(limit).ToArray(),
            },
            compaction = new
            {
                selected_pack_id = diag.Compaction.SelectedPackId,
                pack_count = diag.Compaction.PackCount,
                total_dropped_tokens_across_packs = diag.Compaction.TotalDroppedTokensAcrossPacks,
                critical_dropped_sources = diag.Compaction.CriticalDroppedSources,
                reconciliation = diag.Compaction.Reconciliation,
                categories = diag.Compaction.Root.Children.Select(c => new
                {
                    id = c.Id,
                    label = c.Label,
                    category = c.Category,
                    retained_tokens = c.RetainedTokens,
                    dropped_tokens = c.DroppedTokens,
                    share_percent = c.SharePercent,
                    budget_share_percent = c.BudgetSharePercent,
                    item_count = c.ItemCount,
                    dropped_item_count = c.DroppedItemCount,
                }).ToArray(),
                packs = diag.Compaction.Packs.Take(limit).ToArray(),
            },
            governance = new
            {
                signal_counts = diag.Governance.SignalCounts,
                active_signals = diag.Governance.ActiveSignals,
                signals = diag.Governance.Signals.Select(s => new
                {
                    code = s.Code,
                    severity = s.Severity,
                    count = s.Count,
                    max_retry_streak = s.MaxRetryStreak,
                    detail = Truncate(RedactSensitiveText(s.Detail), MaxText),
                    subject_ids = s.SubjectIds.Take(8).ToArray(),
                }).ToArray(),
                max_same_args_retry_streak = diag.Governance.MaxSameArgsRetryStreak,
                retry_streaks = diag.Governance.RetryStreaks.Take(limit).Select(r => new
                {
                    execution_id = r.ExecutionId,
                    tool_id = r.ToolId,
                    scope_key = r.ScopeKey,
                    streak_count = r.StreakCount,
                    arguments_summary = Truncate(RedactSensitiveText(r.ArgumentsSummary), MaxText),
                }).ToArray(),
                total_tool_calls = diag.Governance.TotalToolCalls,
                total_model_calls = diag.Governance.TotalModelCalls,
                error_count = diag.Governance.ErrorCount,
                total_added_lines = diag.Governance.TotalAddedLines,
                total_removed_lines = diag.Governance.TotalRemovedLines,
                file_edits = diag.Governance.FileEdits.Take(limit).ToArray(),
                error_frames = diag.Governance.ErrorFrames.Take(Math.Min(limit, 10)).ToArray(),
            },
            executive_findings = diag.ExecutiveFindings,
        });
    }

    public async Task<IReadOnlyList<ContextAuditSourceInputDto>> GatherCatalogSourcesAsync(
        ContextOverlapAuditRequestDto request,
        ContextAuditExecutionScope scope,
        CancellationToken cancellationToken = default)
    {
        var includePrompts = request.IncludePrompts ?? true;
        var includeTools = request.IncludeTools ?? true;
        var results = new List<ContextAuditSourceInputDto>();

        await using var db = await _agents.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        HashSet<Guid>? allowedAgentIds = null;
        HashSet<Guid>? allowedPipelineIds = null;

        if (!string.IsNullOrWhiteSpace(request.ModeId))
        {
            var trimmedMode = request.ModeId.Trim();
            AgentModeRecord? modeRow;
            if (Guid.TryParse(trimmedMode, out var parsedModeGuid) && parsedModeGuid != Guid.Empty)
            {
                modeRow = await db.AgentModes.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == parsedModeGuid && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var modeMatches = await db.AgentModes.AsNoTracking()
                    .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == trimmedMode)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                modeRow = modeMatches.FirstOrDefault(x => !string.Equals(x.Status, "archived", StringComparison.OrdinalIgnoreCase))
                    ?? modeMatches.FirstOrDefault();
            }

            if (modeRow is null)
            {
                throw new KeyNotFoundException($"Mode '{request.ModeId}' was not found.");
            }

            if (includePrompts && !string.IsNullOrWhiteSpace(modeRow.Instructions))
            {
                results.Add(new ContextAuditSourceInputDto
                {
                    Id = $"mode:{modeRow.Id}:instructions",
                    Label = $"{modeRow.DisplayName} Mode Instructions",
                    Kind = ContextAuditSourceKinds.PipelineTemplate,
                    Content = modeRow.Instructions
                });
            }

            var modeNodeAgentIds = await db.ModeNodes.AsNoTracking()
                .Where(x => x.ModeId == modeRow.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
                .Select(x => x.AgentDefinitionId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            allowedAgentIds = modeNodeAgentIds.ToHashSet();
        }

        if (!string.IsNullOrWhiteSpace(request.AgentId))
        {
            var trimmedAgent = request.AgentId.Trim();
            AgentDefinitionRecord? singleAgent;
            if (Guid.TryParse(trimmedAgent, out var parsedAgentGuid) && parsedAgentGuid != Guid.Empty)
            {
                singleAgent = await db.AgentDefinitions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == parsedAgentGuid && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var agentMatches = await db.AgentDefinitions.AsNoTracking()
                    .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == trimmedAgent)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                singleAgent = agentMatches.FirstOrDefault(x => !string.Equals(x.Status, "archived", StringComparison.OrdinalIgnoreCase))
                    ?? agentMatches.FirstOrDefault();
            }

            if (singleAgent is null)
            {
                throw new KeyNotFoundException($"Agent '{request.AgentId}' was not found.");
            }

            allowedAgentIds = allowedAgentIds is null
                ? [singleAgent.Id]
                : allowedAgentIds.Intersect([singleAgent.Id]).ToHashSet();
        }

        if (includePrompts)
        {
            var agentQuery = db.AgentDefinitions.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status != "archived" && x.Enabled);
            var agentRows = await agentQuery.ToListAsync(cancellationToken).ConfigureAwait(false);
            if (allowedAgentIds is not null)
            {
                agentRows = agentRows.Where(a => allowedAgentIds.Contains(a.Id)).ToList();
            }

            allowedPipelineIds = allowedAgentIds is not null
                ? agentRows.Where(a => a.BasePromptPipelineId.HasValue).Select(a => a.BasePromptPipelineId!.Value).ToHashSet()
                : null;

            var allowedSlugs = agentRows.Select(a => a.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var agent in agentRows.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
                {
                    results.Add(new ContextAuditSourceInputDto
                    {
                        Id = $"agent:{agent.Id}",
                        Label = string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Slug : agent.DisplayName,
                        Kind = ContextAuditSourceKinds.SystemPrompt,
                        Content = agent.SystemPrompt
                    });
                }
            }

            var pipelines = await db.PromptPipelines.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status != "archived")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (allowedPipelineIds is not null)
            {
                pipelines = pipelines.Where(p => allowedPipelineIds.Contains(p.Id)).ToList();
            }

            foreach (var pipeline in pipelines.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var tpl = ContextOverlapAuditor.ExtractPipelineGraphTemplates(pipeline.GraphJson);
                if (!string.IsNullOrWhiteSpace(tpl))
                {
                    results.Add(new ContextAuditSourceInputDto
                    {
                        Id = $"pipeline:{pipeline.Id}",
                        Label = string.IsNullOrWhiteSpace(pipeline.DisplayName) ? pipeline.Slug : pipeline.DisplayName,
                        Kind = ContextAuditSourceKinds.PipelineTemplate,
                        Content = tpl
                    });
                }
            }

            if (_promptFragments is not null)
            {
                var fragments = await _promptFragments.ListAsync(enabled: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var frag in fragments)
                {
                    if (!frag.Enabled || string.IsNullOrWhiteSpace(frag.Content)) continue;
                    if (allowedAgentIds is not null
                        && string.Equals(frag.Scope, "agent", StringComparison.OrdinalIgnoreCase)
                        && (!frag.TargetAgentId.HasValue || !allowedAgentIds.Contains(frag.TargetAgentId.Value))
                        && (string.IsNullOrWhiteSpace(frag.TargetAgentSlug) || !allowedSlugs.Contains(frag.TargetAgentSlug)))
                    {
                        continue;
                    }

                    results.Add(new ContextAuditSourceInputDto
                    {
                        Id = $"fragment:{frag.Id}",
                        Label = string.IsNullOrWhiteSpace(frag.Title) ? frag.Key : frag.Title,
                        Kind = ContextAuditSourceKinds.PromptFragment,
                        Content = frag.Content
                    });
                }
            }
        }

        if (includeTools)
        {
            var seenTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toolLines = new List<string>();
            void AddToolLine(string id, string? desc)
            {
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(desc) && seenTools.Add(id))
                {
                    toolLines.Add($"{id}: {RedactSensitiveText(desc)}");
                }
            }

            foreach (var entry in CoreTinadecConfigTools.AllManifestEntries())
            {
                AddToolLine(entry.Id, entry.Description);
            }

            foreach (var teamEntry in TeamToolCatalog.Entries)
            {
                AddToolLine(teamEntry.Id, teamEntry.Description);
            }

            if (_provider is not null)
            {
                try
                {
                    var root = !string.IsNullOrWhiteSpace(scope.AuthorizedWorkspaceRoot)
                        ? scope.AuthorizedWorkspaceRoot
                        : _configuration?["TinadecTools:DefaultWorkspaceRoot"] ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        var manifest = await _provider.GetManifestAsync(root, cancellationToken).ConfigureAwait(false);
                        foreach (var tool in manifest.Tools)
                        {
                            AddToolLine(tool.Id, tool.Description);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Non-fatal if tool provider process is unavailable.
                }
            }

            if (toolLines.Count > 0)
            {
                results.Add(new ContextAuditSourceInputDto
                {
                    Id = "tools:catalog",
                    Label = $"Tool Schemas ({toolLines.Count})",
                    Kind = ContextAuditSourceKinds.ToolSchema,
                    Content = string.Join('\n', toolLines)
                });
            }

            if (_mcpServers is not null || _mcp is not null)
            {
                try
                {
                    var mcpList = _mcpServers is not null
                        ? await _mcpServers.ListConfiguredAsync(scope.ProjectId, cancellationToken).ConfigureAwait(false)
                        : await _mcp!.ListAsync(scope.ProjectId, cancellationToken).ConfigureAwait(false);
                    var mcpLines = mcpList
                        .Where(m => m.Enabled)
                        .Select(m => $"- {m.Name} ({m.Id}, transport={m.Transport})")
                        .ToArray();
                    if (mcpLines.Length > 0)
                    {
                        results.Add(new ContextAuditSourceInputDto
                        {
                            Id = "mcp:configured_servers",
                            Label = $"MCP Servers ({mcpLines.Length})",
                            Kind = ContextAuditSourceKinds.ToolSchema,
                            Content = "Configured MCP servers:\n" + string.Join('\n', mcpLines)
                        });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Non-fatal.
                }
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<ModelInvocationDto>> ListForRunAsync(
        Guid tenantId,
        Guid workspaceId,
        Guid runId,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (_lifecycleDb is null)
        {
            return [];
        }

        await using var db = await _lifecycleDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await db.ModelInvocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.RunId == runId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.StartedAt)
            .ThenBy(x => x.Attempt)
            .ThenBy(x => x.Id)
            .Take(Math.Clamp(limit, 1, 2000))
            .ToArray();

        return rows.Select(value => new ModelInvocationDto
        {
            Id = value.Id,
            CallId = value.CallId,
            Attempt = value.Attempt,
            SessionId = value.SessionId,
            RunId = value.RunId,
            TurnId = value.TurnId,
            AgentInstanceId = value.AgentInstanceId,
            AgentDefinitionId = value.AgentDefinitionId,
            AgentVersionId = value.AgentVersionId,
            ModeVersionId = value.ModeVersionId,
            StrategySource = value.StrategySource,
            RouteId = value.RouteId,
            RouteVersionId = value.RouteVersionId,
            ProviderInstanceId = value.ProviderInstanceId,
            ProviderVersionId = value.ProviderVersionId,
            Model = value.Model,
            Protocol = value.Protocol,
            FallbackPosition = value.FallbackPosition,
            Status = value.Status,
            ErrorCategory = value.ErrorCategory,
            SafeErrorMessage = value.SafeErrorMessage,
            InputTokens = value.InputTokens,
            OutputTokens = value.OutputTokens,
            TotalTokens = value.TotalTokens,
            StartedAt = value.StartedAt,
            CompletedAt = value.CompletedAt
        }).ToArray();
    }

    // ── tinadec_agent_apply ──

    private async Task<ToolWireResponseDto> AgentApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        var expectedRevision = ReadLong(parameters, "expected_revision", ReadLong(payload, "expected_revision", -1));
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
                var (sourceKind, sourceKindErr) = ResolveAgentSourceKind(payload, parameters, defaultKind: "agent_generated");
                if (sourceKindErr is not null) return Fail(wire, "invalid_request", sourceKindErr);

                var (pipelineId, pipelineErr) = await ResolveOptionalPipelineIdAsync(db, scope, payload, parameters, ct).ConfigureAwait(false);
                if (pipelineErr is not null) return Fail(wire, "invalid_request", pipelineErr);

                var slug = await UniqueDraftSlugAsync(db, Text(payload, "slug") ?? name, scope, ct).ConfigureAwait(false);
                var provenanceJson = BuildProvenanceJson(scope, payload, parameters, now, existingJson: null);
                var record = new AgentDefinitionRecord
                {
                    Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                    Slug = slug, DisplayName = name.Trim(), Layer = layer,
                    Role = Text(payload, "role") ?? string.Empty,
                    CapabilitiesJson = Raw(payload, "capabilities") ?? "[]",
                    BasePromptPipelineId = pipelineId,
                    ModelStrategyJson = Raw(payload, "model_strategy") ?? "{\"kind\":\"inherit\"}",
                    ToolScopeJson = Raw(payload, "tool_scope") ?? Raw(payload, "allowed_tools") ?? "[]",
                    SystemPrompt = Text(payload, "system_prompt"),
                    Description = Text(payload, "description"),
                    SourceKind = sourceKind, SourceKey = slug,
                    ProvenanceJson = provenanceJson,
                    Managed = false,
                    Enabled = ReadBool(payload, "enabled", true),
                    Status = "draft", Revision = 1, Version = 0, CreatedAt = now, UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.AgentDefinitions.Add(record);

                if (TryGetObjectProperty(payload, parameters, "model_binding", out var bindingEl))
                {
                    var bindingErr = await UpsertAgentModelBindingAsync(db, record, scope, bindingEl, now, ct).ConfigureAwait(false);
                    if (bindingErr is not null) return Fail(wire, "invalid_request", bindingErr);
                }

                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateException) { return Fail(wire, "duplicate_slug", "A draft agent already uses this slug."); }

                if (TryGetObjectProperty(payload, parameters, "tool_settings", out var settingsEl))
                {
                    var settingsErr = await ApplyAgentToolSettingsAsync(record.Id, scope, settingsEl, ct).ConfigureAwait(false);
                    if (settingsErr is not null) return Fail(wire, "invalid_request", settingsErr);
                }

                return Ok(wire, new { ok = true, action, agent = DescribeAgent(record, builtIn: false, await LoadBindingAsync(db, record.Id, scope, ct).ConfigureAwait(false)), note = "Saved as a draft; publish it before a new run can use it." });
            }
            case "clone":
            {
                var rawSourceId = Text(parameters, "agent_id") ?? Text(parameters, "source_id") ?? Text(payload, "agent_id");
                if (string.IsNullOrWhiteSpace(rawSourceId))
                    return Fail(wire, "invalid_request", "action=clone needs agent_id (the agent to copy).");
                var source = await ResolveAgentRecordAsync(db, scope, rawSourceId, asNoTracking: true, ct).ConfigureAwait(false);
                if (source is null) return Fail(wire, "not_found", "Agent not found.");
                if (string.Equals(source.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived agent cannot be cloned.");
                var (sourceKind, sourceKindErr) = ResolveAgentSourceKind(payload, parameters, defaultKind: "agent_generated");
                if (sourceKindErr is not null) return Fail(wire, "invalid_request", sourceKindErr);

                var (overridePipelineId, hasPipelineOverride, pipelineErr) = await ResolveOptionalPipelineOverrideAsync(db, scope, payload, parameters, ct).ConfigureAwait(false);
                if (pipelineErr is not null) return Fail(wire, "invalid_request", pipelineErr);

                var name = Text(payload, "display_name") ?? $"{source.DisplayName} (copy)";
                var slug = await UniqueDraftSlugAsync(db, Text(payload, "slug") ?? $"{source.Slug}-copy", scope, ct).ConfigureAwait(false);
                var provenanceJson = BuildProvenanceJson(scope, payload, parameters, now, existingJson: source.ProvenanceJson);
                var copy = new AgentDefinitionRecord
                {
                    Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                    Slug = slug, DisplayName = name.Trim(), Layer = source.Layer,
                    Role = Text(payload, "role") ?? source.Role,
                    CapabilitiesJson = Raw(payload, "capabilities") ?? source.CapabilitiesJson,
                    BasePromptPipelineId = hasPipelineOverride ? overridePipelineId : source.BasePromptPipelineId,
                    ModelStrategyJson = Raw(payload, "model_strategy") ?? source.ModelStrategyJson ?? "{\"kind\":\"inherit\"}",
                    ToolScopeJson = Raw(payload, "tool_scope") ?? Raw(payload, "allowed_tools") ?? source.ToolScopeJson,
                    SystemPrompt = Text(payload, "system_prompt") ?? source.SystemPrompt,
                    Description = Text(payload, "description") ?? source.Description,
                    I18nJson = null, SourceKind = sourceKind, SourceKey = slug,
                    ProvenanceJson = provenanceJson,
                    Managed = false,
                    Enabled = ReadBool(payload, "enabled", source.Enabled), Status = "draft", Revision = 1, Version = 0,
                    CreatedAt = now, UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId, UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.AgentDefinitions.Add(copy);

                if (TryGetObjectProperty(payload, parameters, "model_binding", out var bindingEl))
                {
                    var bindingErr = await UpsertAgentModelBindingAsync(db, copy, scope, bindingEl, now, ct).ConfigureAwait(false);
                    if (bindingErr is not null) return Fail(wire, "invalid_request", bindingErr);
                }

                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateException) { return Fail(wire, "duplicate_slug", "A draft agent already uses this slug."); }

                if (TryGetObjectProperty(payload, parameters, "tool_settings", out var settingsEl))
                {
                    var settingsErr = await ApplyAgentToolSettingsAsync(copy.Id, scope, settingsEl, ct).ConfigureAwait(false);
                    if (settingsErr is not null) return Fail(wire, "invalid_request", settingsErr);
                }

                return Ok(wire, new { ok = true, action, agent = DescribeAgent(copy, builtIn: false, await LoadBindingAsync(db, copy.Id, scope, ct).ConfigureAwait(false)), source_agent_id = source.Id, note = "A custom draft copy; the source is unchanged." });
            }
            case "update":
            {
                var rawAgentId = Text(parameters, "agent_id") ?? Text(payload, "agent_id");
                if (string.IsNullOrWhiteSpace(rawAgentId))
                    return Fail(wire, "invalid_request", "action=update needs agent_id.");
                var record = await ResolveAgentRecordAsync(db, scope, rawAgentId, asNoTracking: false, ct).ConfigureAwait(false);
                if (record is null) return Fail(wire, "not_found", "Agent not found.");
                if (await _packs.FindManagedResourceAsync("agent", record.Id, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages agent '{managed.ResourceKey}'. Clone it and update the copy.");
                if (string.Equals(record.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived agent cannot be edited.");
                if (expectedRevision >= 0 && record.Revision != expectedRevision)
                    return Fail(wire, "conflict", $"Revision conflict: expected {expectedRevision}, current is {record.Revision}.");

                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("layer", out var layerElement))
                {
                    var layer = (layerElement.GetString() ?? "operation").Trim().ToLowerInvariant();
                    if (ValidateLayer(layer) is { } layerError) return Fail(wire, "invalid_layer", layerError);
                    record.Layer = layer;
                }
                if (ModelStrategyError(payload) is { } strategyError) return Fail(wire, "invalid_model_strategy", strategyError);
                if (Text(payload, "display_name") is { } displayName && !string.IsNullOrWhiteSpace(displayName)) record.DisplayName = displayName.Trim();
                if (Text(payload, "role") is { } role) record.Role = role;
                if (Raw(payload, "capabilities") is { } capabilities) record.CapabilitiesJson = capabilities;
                if (Raw(payload, "model_strategy") is { } modelStrategy) record.ModelStrategyJson = modelStrategy;
                if ((Raw(payload, "tool_scope") ?? Raw(payload, "allowed_tools")) is { } toolScope) record.ToolScopeJson = toolScope;
                if (Text(payload, "system_prompt") is { } systemPrompt) record.SystemPrompt = systemPrompt;
                if (Text(payload, "description") is { } description) record.Description = description;
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    record.Enabled = enabled.ValueKind == JsonValueKind.True;

                var (overridePipelineId, hasPipelineOverride, pipelineErr) = await ResolveOptionalPipelineOverrideAsync(db, scope, payload, parameters, ct).ConfigureAwait(false);
                if (pipelineErr is not null) return Fail(wire, "invalid_request", pipelineErr);
                if (hasPipelineOverride) record.BasePromptPipelineId = overridePipelineId;

                if ((Text(payload, "source_kind") ?? Text(parameters, "source_kind")) is { } explicitKind)
                {
                    var (resolvedKind, kindErr) = ResolveAgentSourceKind(payload, parameters, defaultKind: record.SourceKind);
                    if (kindErr is not null) return Fail(wire, "invalid_request", kindErr);
                    record.SourceKind = resolvedKind;
                }
                if (TryGetObjectProperty(payload, parameters, "provenance", out _))
                {
                    record.ProvenanceJson = BuildProvenanceJson(scope, payload, parameters, now, existingJson: record.ProvenanceJson);
                }

                if (TryGetObjectProperty(payload, parameters, "model_binding", out var bindingEl))
                {
                    var bindingErr = await UpsertAgentModelBindingAsync(db, record, scope, bindingEl, now, ct).ConfigureAwait(false);
                    if (bindingErr is not null) return Fail(wire, "invalid_request", bindingErr);
                }

                record.Status = "draft";
                record.Revision++;
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
                catch (DbUpdateConcurrencyException) { return Fail(wire, "conflict", "The agent changed since it was read."); }

                if (TryGetObjectProperty(payload, parameters, "tool_settings", out var settingsEl))
                {
                    var settingsErr = await ApplyAgentToolSettingsAsync(record.Id, scope, settingsEl, ct).ConfigureAwait(false);
                    if (settingsErr is not null) return Fail(wire, "invalid_request", settingsErr);
                }

                return Ok(wire, new { ok = true, action, agent = DescribeAgent(record, builtIn: false, await LoadBindingAsync(db, record.Id, scope, ct).ConfigureAwait(false)), note = "Saved as a draft; publish it before a new run can use it." });
            }
            case "publish":
            {
                var rawAgentId = Text(parameters, "agent_id") ?? Text(payload, "agent_id");
                if (string.IsNullOrWhiteSpace(rawAgentId))
                    return Fail(wire, "invalid_request", "action=publish needs agent_id.");
                var record = await ResolveAgentRecordAsync(db, scope, rawAgentId, asNoTracking: false, ct).ConfigureAwait(false);
                if (record is null) return Fail(wire, "not_found", "Agent not found.");
                if (await _packs.FindManagedResourceAsync("agent", record.Id, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages agent '{managed.ResourceKey}'. Clone it and publish the copy.");
                if (string.Equals(record.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived agent cannot be published.");
                if (expectedRevision >= 0 && record.Revision != expectedRevision)
                    return Fail(wire, "conflict", $"Revision conflict: expected {expectedRevision}, current is {record.Revision}.");
                if (!string.Equals(record.Status, "draft", StringComparison.OrdinalIgnoreCase))
                    return Fail(wire, "not_draft", "Only a draft agent can be published.");
                if (ValidateLayer(record.Layer) is { } layerErr)
                    return Fail(wire, "invalid_layer", layerErr);
                if (string.IsNullOrWhiteSpace(record.DisplayName))
                    return Fail(wire, "invalid_request", "display_name is required before publishing.");
                if (!string.IsNullOrWhiteSpace(record.ModelStrategyJson)
                    && AgentTopologyService.ValidateModelStrategy(record.ModelStrategyJson) is { } strategyErr)
                {
                    return Fail(wire, "invalid_model_strategy", strategyErr);
                }

                var nextVersion = (await db.AgentVersions
                    .Where(x => x.AgentDefinitionId == record.Id)
                    .MaxAsync(x => (int?)x.Version, ct).ConfigureAwait(false) ?? 0) + 1;

                var snapshotObject = new
                {
                    id = record.Id,
                    slug = record.Slug,
                    display_name = record.DisplayName,
                    layer = record.Layer,
                    role = record.Role,
                    capabilities = ParseStringArray(record.CapabilitiesJson),
                    base_prompt_pipeline_id = record.BasePromptPipelineId,
                    model_strategy = ParseJsonOrNull(record.ModelStrategyJson) ?? DefaultModelStrategyElement(),
                    tool_scope = ParseStringArray(record.ToolScopeJson),
                    system_prompt = record.SystemPrompt,
                    description = record.Description,
                    source_kind = record.SourceKind,
                    provenance = ParseJsonOrNull(record.ProvenanceJson),
                    enabled = record.Enabled,
                    status = "published",
                    version = nextVersion,
                    revision = record.Revision + 1,
                };
                var snapshotJson = JsonSerializer.Serialize(snapshotObject);
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
                var versionRecord = new AgentVersionRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    AgentDefinitionId = record.Id,
                    Version = nextVersion,
                    Layer = record.Layer,
                    Role = record.Role,
                    SnapshotJson = snapshotJson,
                    ContentHash = contentHash,
                    ContentLength = snapshotJson.Length,
                    Status = "published",
                    Revision = 1,
                    CreatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId,
                };
                db.AgentVersions.Add(versionRecord);
                record.Version = nextVersion;
                record.Revision++;
                record.Status = "published";
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    action = "publish",
                    agent = DescribeAgent(record, builtIn: false, await LoadBindingAsync(db, record.Id, scope, ct).ConfigureAwait(false)),
                    version = nextVersion,
                    revision = record.Revision,
                    content_hash = contentHash,
                    note = "Published. New runs can now use this agent version.",
                });
            }
            default:
                return Fail(wire, "invalid_request", "action must be clone, create, update or publish.");
        }
    }

    // ── tinadec_mode_apply / tinadec_mode_publish ──

    private async Task<ToolWireResponseDto> ModeApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        var expectedRevision = ReadInt(parameters, "expected_revision", ReadInt(payload, "expected_revision", -1));
        var rawModeId = Text(parameters, "mode_id") ?? Text(parameters, "id") ?? Text(payload, "mode_id");
        Guid? modeId = null;
        if (!string.IsNullOrWhiteSpace(rawModeId))
        {
            modeId = await ResolveModeIdAsync(scope, rawModeId, ct).ConfigureAwait(false);
        }

        AgentTopologyResult result;
        switch (action)
        {
            case "create":
                result = await _topology.CreateDraftAsync(payload.ValueKind == JsonValueKind.Object ? payload : parameters, ct).ConfigureAwait(false);
                break;
            case "clone":
                if (string.IsNullOrWhiteSpace(rawModeId)) return Fail(wire, "invalid_request", "action=clone needs mode_id (the mode to copy).");
                if (modeId is null) return Fail(wire, AgentTopologyCodes.NotFound, $"Mode '{rawModeId}' not found.");
                result = await _topology.CloneDraftAsync(modeId.Value, payload, ct).ConfigureAwait(false);
                break;
            case "upsert":
                if (string.IsNullOrWhiteSpace(rawModeId)) return Fail(wire, "invalid_request", "action=upsert needs mode_id.");
                if (modeId is null) return Fail(wire, AgentTopologyCodes.NotFound, $"Mode '{rawModeId}' not found.");
                result = await _topology.UpsertDraftAsync(modeId.Value, payload.ValueKind == JsonValueKind.Object ? payload : parameters, expectedRevision, ct).ConfigureAwait(false);
                break;
            case "delete":
                if (string.IsNullOrWhiteSpace(rawModeId)) return Fail(wire, "invalid_request", "action=delete needs mode_id.");
                if (modeId is null) return Fail(wire, AgentTopologyCodes.NotFound, $"Mode '{rawModeId}' not found.");
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
        var rawModeId = Text(parameters, "mode_id") ?? Text(parameters, "id") ?? Text(parameters, "slug");
        if (string.IsNullOrWhiteSpace(rawModeId))
            return Fail(wire, "invalid_request", "tinadec_mode_publish needs mode_id.");
        var modeId = await ResolveModeIdAsync(scope, rawModeId, ct).ConfigureAwait(false);
        if (modeId is null)
            return Fail(wire, AgentTopologyCodes.NotFound, $"Mode '{rawModeId}' not found.");
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

    // ── tinadec_prompt_apply ──

    private async Task<ToolWireResponseDto> PromptApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        var parameters = wire.Params ?? default;
        if (parameters.ValueKind != JsonValueKind.Object)
            return Fail(wire, "invalid_request", "tinadec_prompt_apply requires a JSON object.");
        var target = (Text(parameters, "target") ?? string.Empty).Trim().ToLowerInvariant();
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        var expectedRevision = ReadLong(parameters, "expected_revision", ReadLong(payload, "expected_revision", -1));

        return target switch
        {
            "fragment" => await PromptFragmentApplyAsync(wire, action, parameters, payload, expectedRevision, ct).ConfigureAwait(false),
            "pipeline" => await PromptPipelineApplyAsync(scope, wire, action, parameters, payload, expectedRevision, ct).ConfigureAwait(false),
            _ => Fail(wire, "invalid_request", "target must be 'fragment' or 'pipeline'."),
        };
    }

    private async Task<ToolWireResponseDto> PromptFragmentApplyAsync(
        ToolWireRequestDto wire,
        string action,
        JsonElement parameters,
        JsonElement payload,
        long expectedRevision,
        CancellationToken ct)
    {
        if (_promptFragments is null)
            return Fail(wire, "unavailable", "No prompt fragment service is registered in this host.");

        var rawId = Text(parameters, "fragment_id") ?? Text(parameters, "id") ?? Text(payload, "fragment_id") ?? Text(payload, "id");
        var source = payload.ValueKind == JsonValueKind.Object ? payload : parameters;
        long? expected = expectedRevision >= 0 ? expectedRevision : null;

        switch (action)
        {
            case "create":
            {
                var saveReq = new PromptFragmentSaveRequestDto
                {
                    Key = Text(source, "key"),
                    Title = Text(source, "title") ?? Text(source, "name") ?? Text(source, "display_name"),
                    Scope = Text(source, "scope"),
                    TargetAgentId = Text(source, "target_agent_id"),
                    TargetAgentSlug = Text(source, "target_agent_slug"),
                    Category = Text(source, "category"),
                    Content = Text(source, "content"),
                    Priority = ReadNullableInt(source, "priority"),
                    Enabled = ReadNullableBool(source, "enabled"),
                    ChangeSummary = Text(source, "change_summary") ?? Text(source, "notes"),
                };
                var created = await _promptFragments.SaveAsync(null, saveReq, null, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = created });
            }
            case "update":
            {
                if (string.IsNullOrWhiteSpace(rawId))
                    return Fail(wire, "invalid_request", "action=update for target=fragment needs fragment_id.");
                var resolvedId = await ResolveFragmentIdAsync(rawId, ct).ConfigureAwait(false);
                if (resolvedId is null)
                    return Fail(wire, "not_found", $"Prompt fragment '{rawId}' not found.");
                var saveReq = new PromptFragmentSaveRequestDto
                {
                    Key = Text(source, "key"),
                    Title = Text(source, "title") ?? Text(source, "name") ?? Text(source, "display_name"),
                    Scope = Text(source, "scope"),
                    TargetAgentId = Text(source, "target_agent_id"),
                    TargetAgentSlug = Text(source, "target_agent_slug"),
                    Category = Text(source, "category"),
                    Content = Text(source, "content"),
                    Priority = ReadNullableInt(source, "priority"),
                    Enabled = ReadNullableBool(source, "enabled"),
                    ChangeSummary = Text(source, "change_summary") ?? Text(source, "notes"),
                };
                var updated = await _promptFragments.SaveAsync(resolvedId.Value, saveReq, expected, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = updated });
            }
            case "clone":
            {
                if (string.IsNullOrWhiteSpace(rawId))
                    return Fail(wire, "invalid_request", "action=clone for target=fragment needs fragment_id.");
                var resolvedId = await ResolveFragmentIdAsync(rawId, ct).ConfigureAwait(false);
                if (resolvedId is null)
                    return Fail(wire, "not_found", $"Prompt fragment '{rawId}' not found.");
                var cloneReq = new PromptFragmentCloneRequestDto
                {
                    Key = Text(source, "new_key") ?? Text(source, "key"),
                    Title = Text(source, "title") ?? Text(source, "name") ?? Text(source, "display_name"),
                    Scope = Text(source, "scope"),
                    TargetAgentId = Text(source, "target_agent_id"),
                    TargetAgentSlug = Text(source, "target_agent_slug"),
                };
                var cloned = await _promptFragments.CloneAsync(resolvedId.Value, cloneReq, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = cloned, source_fragment_id = resolvedId.Value });
            }
            case "publish":
            {
                if (string.IsNullOrWhiteSpace(rawId))
                    return Fail(wire, "invalid_request", "action=publish for target=fragment needs fragment_id.");
                var resolvedId = await ResolveFragmentIdAsync(rawId, ct).ConfigureAwait(false);
                if (resolvedId is null)
                    return Fail(wire, "not_found", $"Prompt fragment '{rawId}' not found.");
                var existing = await _promptFragments.GetAsync(resolvedId.Value, ct).ConfigureAwait(false);
                if (existing is null)
                    return Fail(wire, "not_found", $"Prompt fragment '{rawId}' not found.");
                var content = Text(source, "content") ?? existing.Content;
                var version = await _promptFragments.CreateVersionAsync(
                    resolvedId.Value,
                    new PromptFragmentVersionCreateRequestDto
                    {
                        Content = content,
                        ChangeSummary = Text(source, "change_summary") ?? Text(source, "notes") ?? "Published snapshot",
                    },
                    expected,
                    ct).ConfigureAwait(false);
                var current = await _promptFragments.GetAsync(resolvedId.Value, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = current, version });
            }
            case "rollback":
            {
                if (string.IsNullOrWhiteSpace(rawId))
                    return Fail(wire, "invalid_request", "action=rollback for target=fragment needs fragment_id.");
                var resolvedId = await ResolveFragmentIdAsync(rawId, ct).ConfigureAwait(false);
                if (resolvedId is null)
                    return Fail(wire, "not_found", $"Prompt fragment '{rawId}' not found.");
                var rawVersion = Text(parameters, "version_id") ?? Text(source, "version_id");
                var targetVersion = ReadNullableInt(source, "target_version") ?? ReadNullableInt(parameters, "target_version");
                Guid? versionGuid = null;
                if (!string.IsNullOrWhiteSpace(rawVersion))
                {
                    if (Guid.TryParse(rawVersion, out var parsedGuid) && parsedGuid != Guid.Empty)
                        versionGuid = parsedGuid;
                    else if (targetVersion is null && int.TryParse(rawVersion, out var parsedInt))
                        targetVersion = parsedInt;
                }
                if (versionGuid is null && targetVersion is null)
                    return Fail(wire, "invalid_request", "action=rollback needs version_id or payload.target_version.");
                var rolledBack = await _promptFragments.RollbackAsync(
                    resolvedId.Value,
                    new PromptFragmentRollbackRequestDto
                    {
                        VersionId = versionGuid,
                        TargetVersion = targetVersion,
                        ChangeSummary = Text(source, "change_summary") ?? Text(source, "notes"),
                    },
                    expected,
                    ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "fragment", action, fragment = rolledBack });
            }
            default:
                return Fail(wire, "invalid_request", "action must be create, update, clone, publish or rollback.");
        }
    }

    private async Task<ToolWireResponseDto> PromptPipelineApplyAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        string action,
        JsonElement parameters,
        JsonElement payload,
        long expectedRevision,
        CancellationToken ct)
    {
        var rawPipelineId = Text(parameters, "pipeline_id") ?? Text(parameters, "id") ?? Text(payload, "pipeline_id") ?? Text(payload, "id");
        var source = payload.ValueKind == JsonValueKind.Object ? payload : parameters;
        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        switch (action)
        {
            case "create":
            {
                var displayName = Text(source, "display_name") ?? Text(source, "name") ?? Text(source, "title");
                if (string.IsNullOrWhiteSpace(displayName))
                    return Fail(wire, "invalid_request", "action=create for target=pipeline needs payload.display_name.");
                var graphJson = ExtractPipelineGraphJson(source) ?? "{\"nodes\":[],\"edges\":[]}";
                if (ValidatePromptPipelineGraph(graphJson, requirePublishNodes: false) is { } graphErr)
                    return Fail(wire, "invalid_request", graphErr);
                var slug = await UniquePipelineSlugAsync(db, Text(source, "slug") ?? displayName, scope, ct).ConfigureAwait(false);
                var record = new PromptPipelineRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    Slug = slug,
                    DisplayName = displayName.Trim(),
                    Description = Text(source, "description"),
                    GraphJson = graphJson,
                    Status = "draft",
                    Revision = 1,
                    Version = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId,
                    UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.PromptPipelines.Add(record);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "pipeline", action, pipeline = DescribePipeline(record, builtIn: false) });
            }
            case "update":
            {
                if (string.IsNullOrWhiteSpace(rawPipelineId))
                    return Fail(wire, "invalid_request", "action=update for target=pipeline needs pipeline_id.");
                var record = await ResolvePipelineRecordAsync(db, scope, rawPipelineId, asNoTracking: false, ct).ConfigureAwait(false);
                if (record is null)
                    return Fail(wire, "not_found", $"Prompt pipeline '{rawPipelineId}' not found.");
                if (await _packs.FindManagedResourceAsync("prompt_pipeline", record.Id, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages prompt pipeline '{managed.ResourceKey}'. Clone it first.");
                if (string.Equals(record.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived prompt pipeline cannot be edited.");
                if (expectedRevision >= 0 && record.Revision != expectedRevision)
                    return Fail(wire, "conflict", $"Revision conflict: expected {expectedRevision}, current is {record.Revision}.");

                if (ExtractPipelineGraphJson(source) is { } nextGraphJson)
                {
                    if (ValidatePromptPipelineGraph(nextGraphJson, requirePublishNodes: false) is { } graphErr)
                        return Fail(wire, "invalid_request", graphErr);
                    record.GraphJson = nextGraphJson;
                }
                if ((Text(source, "display_name") ?? Text(source, "name")) is { } displayName && !string.IsNullOrWhiteSpace(displayName))
                    record.DisplayName = displayName.Trim();
                if (Text(source, "description") is { } description)
                    record.Description = description;
                record.Status = "draft";
                record.Revision++;
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "pipeline", action, pipeline = DescribePipeline(record, builtIn: false) });
            }
            case "clone":
            {
                if (string.IsNullOrWhiteSpace(rawPipelineId))
                    return Fail(wire, "invalid_request", "action=clone for target=pipeline needs pipeline_id.");
                var sourceRec = await ResolvePipelineRecordAsync(db, scope, rawPipelineId, asNoTracking: true, ct).ConfigureAwait(false);
                if (sourceRec is null)
                    return Fail(wire, "not_found", $"Prompt pipeline '{rawPipelineId}' not found.");
                if (string.Equals(sourceRec.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived prompt pipeline cannot be cloned.");
                var displayName = Text(source, "display_name") ?? Text(source, "name") ?? $"{sourceRec.DisplayName} (copy)";
                var slug = await UniquePipelineSlugAsync(db, Text(source, "slug") ?? $"{sourceRec.Slug}-copy", scope, ct).ConfigureAwait(false);
                var copy = new PromptPipelineRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    Slug = slug,
                    DisplayName = displayName.Trim(),
                    Description = Text(source, "description") ?? sourceRec.Description,
                    GraphJson = ExtractPipelineGraphJson(source) ?? sourceRec.GraphJson,
                    Status = "draft",
                    Revision = 1,
                    Version = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId,
                    UpdatedByPrincipalId = scope.PrincipalId,
                };
                db.PromptPipelines.Add(copy);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, target = "pipeline", action, pipeline = DescribePipeline(copy, builtIn: false), source_pipeline_id = sourceRec.Id });
            }
            case "publish":
            {
                if (string.IsNullOrWhiteSpace(rawPipelineId))
                    return Fail(wire, "invalid_request", "action=publish for target=pipeline needs pipeline_id.");
                var record = await ResolvePipelineRecordAsync(db, scope, rawPipelineId, asNoTracking: false, ct).ConfigureAwait(false);
                if (record is null)
                    return Fail(wire, "not_found", $"Prompt pipeline '{rawPipelineId}' not found.");
                if (await _packs.FindManagedResourceAsync("prompt_pipeline", record.Id, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages prompt pipeline '{managed.ResourceKey}'. Clone it first.");
                if (string.Equals(record.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived prompt pipeline cannot be published.");
                if (expectedRevision >= 0 && record.Revision != expectedRevision)
                    return Fail(wire, "conflict", $"Revision conflict: expected {expectedRevision}, current is {record.Revision}.");
                if (ValidatePromptPipelineGraph(record.GraphJson, requirePublishNodes: true) is { } graphErr)
                    return Fail(wire, "invalid_request", graphErr);

                var nextVersion = (await db.PromptVersions
                    .Where(x => x.PromptPipelineId == record.Id)
                    .MaxAsync(x => (int?)x.Version, ct).ConfigureAwait(false) ?? 0) + 1;
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.GraphJson))).ToLowerInvariant();
                db.PromptVersions.Add(new PromptVersionRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    PromptPipelineId = record.Id,
                    Version = nextVersion,
                    GraphJson = record.GraphJson,
                    ContentHash = hash,
                    ContentLength = record.GraphJson.Length,
                    Status = "published",
                    Revision = 1,
                    CreatedAt = now,
                    CreatedByPrincipalId = scope.PrincipalId,
                });
                record.Version = nextVersion;
                record.Revision++;
                record.Status = "published";
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    target = "pipeline",
                    action = "publish",
                    pipeline = DescribePipeline(record, builtIn: false),
                    version = nextVersion,
                    revision = record.Revision,
                    content_hash = hash,
                });
            }
            case "rollback":
            {
                if (string.IsNullOrWhiteSpace(rawPipelineId))
                    return Fail(wire, "invalid_request", "action=rollback for target=pipeline needs pipeline_id.");
                var record = await ResolvePipelineRecordAsync(db, scope, rawPipelineId, asNoTracking: false, ct).ConfigureAwait(false);
                if (record is null)
                    return Fail(wire, "not_found", $"Prompt pipeline '{rawPipelineId}' not found.");
                if (await _packs.FindManagedResourceAsync("prompt_pipeline", record.Id, ct).ConfigureAwait(false) is { } managed)
                    return Fail(wire, AgentTopologyCodes.BuiltInReadOnly,
                        $"Pack '{managed.PackId}' manages prompt pipeline '{managed.ResourceKey}'. Clone it first.");
                if (string.Equals(record.Status, "archived", StringComparison.Ordinal))
                    return Fail(wire, "conflict", "An archived prompt pipeline cannot be rolled back.");
                if (expectedRevision >= 0 && record.Revision != expectedRevision)
                    return Fail(wire, "conflict", $"Revision conflict: expected {expectedRevision}, current is {record.Revision}.");

                var rawVersion = Text(parameters, "version_id") ?? Text(source, "version_id");
                var targetVersionNum = ReadNullableInt(source, "target_version") ?? ReadNullableInt(parameters, "target_version");
                PromptVersionRecord? versionRec = null;
                if (!string.IsNullOrWhiteSpace(rawVersion))
                {
                    if (Guid.TryParse(rawVersion, out var vGuid) && vGuid != Guid.Empty)
                    {
                        versionRec = await db.PromptVersions.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.Id == vGuid && x.PromptPipelineId == record.Id, ct).ConfigureAwait(false);
                    }
                    else if (targetVersionNum is null && int.TryParse(rawVersion, out var parsedNum))
                    {
                        targetVersionNum = parsedNum;
                    }
                }
                if (versionRec is null && targetVersionNum is { } vNum)
                {
                    versionRec = await db.PromptVersions.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.PromptPipelineId == record.Id && x.Version == vNum, ct).ConfigureAwait(false);
                }
                if (versionRec is null)
                    return Fail(wire, "not_found", "Target prompt pipeline version not found.");

                record.GraphJson = versionRec.GraphJson;
                record.Status = "draft";
                record.Revision++;
                record.UpdatedAt = now;
                record.UpdatedByPrincipalId = scope.PrincipalId;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    target = "pipeline",
                    action = "rollback",
                    pipeline = DescribePipeline(record, builtIn: false),
                    restored_version = versionRec.Version,
                    revision = record.Revision,
                });
            }
            default:
                return Fail(wire, "invalid_request", "action must be create, update, clone, publish or rollback.");
        }
    }

    // ── tinadec_model_profile_apply ──

    private async Task<ToolWireResponseDto> ModelProfileApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        if (_modelProfiles is null)
            return Fail(wire, "unavailable", "No model profile catalog service is registered in this host.");

        var parameters = wire.Params ?? default;
        if (parameters.ValueKind != JsonValueKind.Object)
            return Fail(wire, "invalid_request", "tinadec_model_profile_apply requires a JSON object.");

        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var payload = ChildObject(parameters, "payload");
        var rawProviderId = Text(parameters, "provider_id") ?? Text(payload, "provider_id");
        var modelId = Text(parameters, "model_id") ?? Text(payload, "model_id");
        var expectedRevision = ReadLong(parameters, "expected_revision", ReadLong(payload, "expected_revision", -1));

        switch (action)
        {
            case "inspect":
            {
                var catalog = await _modelProfiles.GetSanitizedCatalogAsync(includeDisabled: false, ct).ConfigureAwait(false);
                var providers = catalog.Providers
                    .Where(p => string.IsNullOrWhiteSpace(rawProviderId)
                        || (Guid.TryParse(rawProviderId, out var pid) && p.Id == pid)
                        || string.Equals(p.DisplayName, rawProviderId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Driver, rawProviderId, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new
                    {
                        id = p.Id,
                        display_name = p.DisplayName,
                        driver = p.Driver,
                        protocol = p.Protocol,
                        default_model = p.DefaultModel,
                        models = string.IsNullOrWhiteSpace(modelId)
                            ? p.Models
                            : p.Models.Where(m => string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase)).ToArray(),
                        model_profiles = string.IsNullOrWhiteSpace(modelId)
                            ? p.ModelProfiles
                            : p.ModelProfiles
                                .Where(kv => string.Equals(kv.Key, modelId, StringComparison.OrdinalIgnoreCase))
                                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                        revision = p.Revision,
                    })
                    .ToArray();

                return Ok(wire, new
                {
                    ok = true,
                    action = "inspect",
                    providers,
                });
            }
            case "update":
            {
                // Validate 400 invalid_request conditions BEFORE looking up provider (404 not_found).
                if (string.IsNullOrWhiteSpace(rawProviderId))
                    return Fail(wire, "invalid_request", "action=update needs provider_id.");
                if (string.IsNullOrWhiteSpace(modelId))
                    return Fail(wire, "invalid_request", "action=update needs model_id.");
                if (payload.ValueKind != JsonValueKind.Object)
                    return Fail(wire, "invalid_request", "action=update needs a non-empty payload object.");

                var hasProfileField = false;
                foreach (var prop in payload.EnumerateObject())
                {
                    if (ForbiddenModelProfileSecretKeys.Contains(prop.Name))
                        return Fail(wire, "invalid_request", $"Field '{prop.Name}' is forbidden: tinadec_model_profile_apply never touches credentials or connection settings.");
                    if (prop.Name is "specialties" or "tier" or "notes" or "context_window" or "cost_tier")
                        hasProfileField = true;
                }
                if (!hasProfileField)
                    return Fail(wire, "invalid_request", "action=update requires at least one model_profile field (specialties, tier, notes, context_window, cost_tier).");

                var catalog = await _modelProfiles.GetSanitizedCatalogAsync(includeDisabled: false, ct).ConfigureAwait(false);
                var matchedProvider = catalog.Providers.FirstOrDefault(p =>
                    (Guid.TryParse(rawProviderId, out var pid) && p.Id == pid)
                    || string.Equals(p.DisplayName, rawProviderId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Driver, rawProviderId, StringComparison.OrdinalIgnoreCase));

                Guid targetProviderGuid;
                ModelProfileDto? existingProfile = null;
                if (matchedProvider is not null)
                {
                    targetProviderGuid = matchedProvider.Id;
                    matchedProvider.ModelProfiles.TryGetValue(modelId.Trim(), out existingProfile);
                }
                else if (Guid.TryParse(rawProviderId, out var parsedProviderGuid) && parsedProviderGuid != Guid.Empty)
                {
                    targetProviderGuid = parsedProviderGuid;
                }
                else
                {
                    return Fail(wire, "not_found", $"Enabled model provider '{rawProviderId}' was not found.");
                }

                ModelProfileDto candidate;
                try
                {
                    var specialties = payload.TryGetProperty("specialties", out var specEl)
                        ? specEl.ValueKind == JsonValueKind.Array
                            ? specEl.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : throw new ArgumentException("specialties must contain only strings.")).ToArray()
                            : specEl.ValueKind == JsonValueKind.Null ? [] : throw new ArgumentException("specialties must be an array of strings.")
                        : existingProfile?.Specialties ?? [];

                    var tier = payload.TryGetProperty("tier", out var tierEl)
                        ? tierEl.ValueKind == JsonValueKind.String ? tierEl.GetString() ?? "balanced" : tierEl.ValueKind == JsonValueKind.Null ? "balanced" : throw new ArgumentException("tier must be a string.")
                        : existingProfile?.Tier ?? "balanced";

                    var notes = payload.TryGetProperty("notes", out var notesEl)
                        ? notesEl.ValueKind == JsonValueKind.String ? notesEl.GetString() : notesEl.ValueKind == JsonValueKind.Null ? null : throw new ArgumentException("notes must be a string.")
                        : existingProfile?.Notes;

                    var contextWindow = payload.TryGetProperty("context_window", out var cwEl)
                        ? cwEl.ValueKind == JsonValueKind.Number && cwEl.TryGetInt32(out var cw) ? (int?)cw : cwEl.ValueKind == JsonValueKind.Null ? null : throw new ArgumentException("context_window must be an integer.")
                        : existingProfile?.ContextWindow;

                    var costTier = payload.TryGetProperty("cost_tier", out var costEl)
                        ? costEl.ValueKind == JsonValueKind.String ? costEl.GetString() : costEl.ValueKind == JsonValueKind.Null ? null : throw new ArgumentException("cost_tier must be a string.")
                        : existingProfile?.CostTier;

                    candidate = ModelProfile.Normalize(new ModelProfileDto
                    {
                        Specialties = specialties,
                        Tier = tier,
                        Notes = notes,
                        ContextWindow = contextWindow,
                        CostTier = costTier,
                    });
                }
                catch (ArgumentException ex)
                {
                    return Fail(wire, "invalid_request", ex.Message);
                }

                var updated = await _modelProfiles.UpdateModelProfileAsync(
                    targetProviderGuid,
                    modelId.Trim(),
                    candidate,
                    expectedRevision >= 0 ? expectedRevision : null,
                    ct).ConfigureAwait(false);

                return Ok(wire, new
                {
                    ok = true,
                    action = "update",
                    provider_instance_id = updated.ProviderInstanceId,
                    model = updated.Model,
                    profile = updated.Profile,
                    model_profiles = updated.ModelProfiles,
                    revision = updated.Revision,
                });
            }
            default:
                return Fail(wire, "invalid_request", "action must be 'inspect' or 'update'.");
        }
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

                var sourceType = (Text(input, "source_type") ?? Text(parameters, "source_type") ?? string.Empty).Trim().ToLowerInvariant();
                if (sourceType is "local_dir" or "git_url" or "external_cli_import" || (sourceType == "inline" && !ReadBool(input, "governed", true)))
                {
                    var installed = await _skills.InstallAsync(new ToolSkillInstallRequestDto
                    {
                        SourceType = sourceType,
                        Scope = requestedScope,
                        ProjectId = targetProject,
                        Name = Text(input, "name"),
                        Content = Text(input, "content"),
                        Files = ReadFileMap(input),
                        LocalPath = Text(input, "local_path"),
                        GitUrl = Text(input, "git_url"),
                        Subpath = Text(input, "subpath"),
                        ItemIds = ReadStringArray(input, "item_ids"),
                        Ecosystems = ReadStringArray(input, "ecosystems"),
                        Enabled = ReadNullableBool(input, "enabled"),
                        EnableStdioMcp = ReadNullableBool(input, "enable_stdio_mcp"),
                        Overwrite = ReadBool(input, "overwrite", false),
                        Governed = ReadBool(input, "governed", false),
                    }, ct).ConfigureAwait(false);
                    return Ok(wire, new
                    {
                        ok = true,
                        action,
                        source_type = installed.SourceType,
                        skills = installed.InstalledSkills.Select(DescribeSkill).ToArray(),
                        mcp_servers = installed.InstalledMcpServers.Select(DescribeMcp).ToArray(),
                        commands = installed.InstalledCommands,
                        skipped = installed.Skipped,
                    });
                }

                var name = Text(input, "name");
                var content = Text(input, "content");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
                    return Fail(wire, "invalid_request", "install needs input.name and input.content (the SKILL.md body), or source_type=local_dir|git_url|external_cli_import.");
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
                var rawExpected = ReadInt(input, "expected_revision", ReadInt(parameters, "expected_revision", -1));
                long? expectedRev = rawExpected >= 0 ? rawExpected : null;
                try
                {
                    var saved = await _skills.SetEnabledAsync(resourceId.Value, action == "enable", projectId, expectedRev, ct).ConfigureAwait(false);
                    return Ok(wire, new { ok = true, action, skill = DescribeSkill(saved), note = GovernedSkillNote(saved) });
                }
                catch (NotSupportedException)
                {
                    var revision = expectedRev ?? existing.Revision;
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
            }
            case "reload":
            {
                var reloaded = await _skills.ReloadAsync(projectId, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, action, total = reloaded.Skills.Count, diagnostics = reloaded.Diagnostics.Take(10).ToArray() });
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
                return Fail(wire, "invalid_request", "action must be install, import, enable, disable, reload or remove.");
        }
    }

    // ── tinadec_mcp_apply ──

    private async Task<ToolWireResponseDto> McpApplyAsync(ToolInvocationScope scope, ToolWireRequestDto wire, CancellationToken ct)
    {
        if (_mcpServers is null && _mcp is null)
            return Fail(wire, "unavailable", "No MCP registry is registered in this host.");
        var parameters = wire.Params ?? default;
        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        var input = ChildObject(parameters, "input");
        var rawResourceId = Text(parameters, "resource_id") ?? Text(input, "resource_id") ?? Text(parameters, "server_id") ?? Text(input, "id");
        var resourceId = GuidOf(parameters, "resource_id") ?? GuidOf(input, "resource_id");
        var expected = ReadInt(input, "expected_revision", ReadInt(parameters, "expected_revision", int.MinValue));
        var projectId = ProjectOf(scope, parameters, input);
        switch (action)
        {
            case "upsert":
            {
                var serverId = Text(input, "id") ?? Text(parameters, "id");
                var command = Text(input, "command") ?? Text(parameters, "command");
                var url = Text(input, "url") ?? Text(parameters, "url");
                var transport = (Text(input, "transport") ?? Text(parameters, "transport")
                    ?? (!string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(command) ? "http" : "stdio")).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(serverId))
                    return Fail(wire, "invalid_request", "upsert needs input.id.");
                if (transport == "stdio" && string.IsNullOrWhiteSpace(command))
                    return Fail(wire, "invalid_request", "upsert with transport=stdio needs input.command.");
                if (transport is "http" or "sse" && string.IsNullOrWhiteSpace(url))
                    return Fail(wire, "invalid_request", $"upsert with transport={transport} needs input.url.");

                var existing = _mcpServers is not null
                    ? (rawResourceId is not null ? await _mcpServers.GetAsync(rawResourceId, projectId, ct).ConfigureAwait(false) : null)
                    : (resourceId is { } id ? await _mcp!.GetAsync(id, ct).ConfigureAwait(false) : null);
                if (resourceId is not null && existing is null)
                    return Fail(wire, "mcp_resource_not_found", "MCP resource not found.");
                var write = new McpResourceWriteDto
                {
                    ProjectId = projectId,
                    Id = serverId,
                    Name = Text(input, "name") ?? serverId,
                    Enabled = ReadBool(input, "enabled", existing?.Enabled ?? true),
                    Transport = transport,
                    Url = url,
                    Command = command ?? string.Empty,
                    Args = ReadStringArray(input, "args"),
                    Env = ReadStringMap(input, "env", "invalid_mcp_environment"),
                    Headers = ReadStringMap(input, "headers", "invalid_mcp_headers"),
                    Cwd = Text(input, "cwd"),
                    ImportSource = Text(input, "import_source"),
                };
                var revision = expected != int.MinValue ? expected : existing?.Revision ?? 0;
                var saved = _mcpServers is not null
                    ? (existing is null
                        ? await _mcpServers.CreateAsync(write, revision, ct).ConfigureAwait(false)
                        : await _mcpServers.UpdateAsync(existing.ResourceId.ToString("N"), write, revision, ct).ConfigureAwait(false))
                    : await _mcp!.SaveAsync(resourceId, write, revision, ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    action,
                    server = DescribeMcp(saved),
                    note = "Registered in Core; active tool calls resolve the updated MCP list dynamically.",
                });
            }
            case "import_json":
            {
                if (_mcpServers is null)
                    return Fail(wire, "unavailable", "IMcpServerService is required for import_json.");
                var jsonText = Text(input, "json") ?? Text(parameters, "json");
                JsonElement? configEl = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("config", out var c)
                    ? c
                    : parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("config", out var c2) ? c2 : null;
                var result = await _mcpServers.ImportJsonAsync(new McpImportJsonRequestDto
                {
                    Json = jsonText,
                    Config = configEl,
                    ProjectId = projectId,
                    Overwrite = ReadBool(input, "overwrite", ReadBool(parameters, "overwrite", false)),
                    DefaultEnabled = ReadNullableBool(input, "default_enabled") ?? ReadNullableBool(parameters, "default_enabled"),
                    ImportSource = Text(input, "import_source") ?? "tinadec_mcp_apply",
                }, ct).ConfigureAwait(false);
                return Ok(wire, new
                {
                    ok = true,
                    action,
                    imported_count = result.ImportedCount,
                    skipped_count = result.SkippedCount,
                    imported = result.Imported.Select(DescribeMcp).ToArray(),
                    skipped = result.Skipped,
                });
            }
            case "enable":
            case "disable":
            {
                if (string.IsNullOrWhiteSpace(rawResourceId))
                    return Fail(wire, "invalid_request", $"action={action} needs resource_id or server_id.");
                if (_mcpServers is null)
                    return Fail(wire, "unavailable", "IMcpServerService is required for enable/disable.");
                long? expectedRev = expected != int.MinValue ? expected : null;
                var updated = await _mcpServers.SetEnabledAsync(rawResourceId, action == "enable", projectId, expectedRev, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, action, server = DescribeMcp(updated) });
            }
            case "test":
            {
                if (string.IsNullOrWhiteSpace(rawResourceId))
                    return Fail(wire, "invalid_request", "action=test needs resource_id or server_id.");
                if (_mcpServers is not null)
                {
                    var tested = await _mcpServers.TestAsync(rawResourceId, projectId, ct).ConfigureAwait(false);
                    return Ok(wire, new
                    {
                        ok = true,
                        resource_id = tested.ResourceId,
                        id = tested.Id,
                        status = tested.Status,
                        reason = tested.Reason,
                        tool_count = tested.ToolCount,
                        tools = tested.Tools.Take(MaxRows).Select(t => new { id = t.Id, name = t.Name, description = Truncate(t.Description, MaxText) }).ToArray(),
                    });
                }
                if (resourceId is null) return Fail(wire, "invalid_request", "action=test needs resource_id.");
                return await TestMcpAsync(scope, wire, resourceId.Value, ct).ConfigureAwait(false);
            }
            case "remove":
            {
                if (string.IsNullOrWhiteSpace(rawResourceId))
                    return Fail(wire, "invalid_request", "action=remove needs resource_id or server_id.");
                if (_mcpServers is not null)
                {
                    long? expectedRev = expected != int.MinValue ? expected : null;
                    await _mcpServers.DeleteAsync(rawResourceId, projectId, expectedRev, ct).ConfigureAwait(false);
                    return Ok(wire, new { ok = true, action, resource_id = rawResourceId });
                }
                if (resourceId is null) return Fail(wire, "invalid_request", "action=remove needs resource_id.");
                var existing = await _mcp!.GetAsync(resourceId.Value, ct).ConfigureAwait(false);
                if (existing is null) return Fail(wire, "mcp_resource_not_found", "MCP resource not found.");
                var revision = expected != int.MinValue ? expected : existing.Revision;
                await _mcp.DeleteAsync(resourceId.Value, revision, ct).ConfigureAwait(false);
                return Ok(wire, new { ok = true, action, resource_id = resourceId });
            }
            default:
                return Fail(wire, "invalid_request", "action must be upsert, import_json, enable, disable, test or remove.");
        }
    }

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

    // ── resolution & sub-operations ──

    private async Task<Guid?> ResolveModeIdAsync(ToolInvocationScope scope, string rawIdOrSlug, CancellationToken ct)
    {
        var trimmed = rawIdOrSlug.Trim();
        if (Guid.TryParse(trimmed, out var parsed) && parsed != Guid.Empty)
            return parsed;

        await using var db = await _agents.CreateDbContextAsync(ct).ConfigureAwait(false);
        var matches = await db.AgentModes.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == trimmed)
            .ToListAsync(ct).ConfigureAwait(false);
        var active = matches.FirstOrDefault(x => !string.Equals(x.Status, "archived", StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
        return active?.Id;
    }

    private static async Task<AgentDefinitionRecord?> ResolveAgentRecordAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        string rawIdOrSlug,
        bool asNoTracking,
        CancellationToken ct)
    {
        var trimmed = rawIdOrSlug.Trim();
        var query = asNoTracking ? db.AgentDefinitions.AsNoTracking() : db.AgentDefinitions.AsQueryable();
        if (Guid.TryParse(trimmed, out var parsed) && parsed != Guid.Empty)
        {
            return await query.FirstOrDefaultAsync(
                x => x.Id == parsed && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                ct).ConfigureAwait(false);
        }

        var matches = await query
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == trimmed)
            .ToListAsync(ct).ConfigureAwait(false);
        return matches.FirstOrDefault(x => !string.Equals(x.Status, "archived", StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
    }

    private static async Task<PromptPipelineRecord?> ResolvePipelineRecordAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        string rawIdOrSlug,
        bool asNoTracking,
        CancellationToken ct)
    {
        var trimmed = rawIdOrSlug.Trim();
        var query = asNoTracking ? db.PromptPipelines.AsNoTracking() : db.PromptPipelines.AsQueryable();
        if (Guid.TryParse(trimmed, out var parsed) && parsed != Guid.Empty)
        {
            return await query.FirstOrDefaultAsync(
                x => x.Id == parsed && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                ct).ConfigureAwait(false);
        }

        var matches = await query
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == trimmed)
            .ToListAsync(ct).ConfigureAwait(false);
        return matches.FirstOrDefault(x => !string.Equals(x.Status, "archived", StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
    }

    private async Task<Guid?> ResolveFragmentIdAsync(string rawIdOrKey, CancellationToken ct)
    {
        var trimmed = rawIdOrKey.Trim();
        if (Guid.TryParse(trimmed, out var parsed) && parsed != Guid.Empty)
            return parsed;
        if (_promptFragments is null)
            return null;
        var list = await _promptFragments.ListAsync(cancellationToken: ct).ConfigureAwait(false);
        return list.FirstOrDefault(f => string.Equals(f.Key, trimmed, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static async Task<(Guid? PipelineId, string? Error)> ResolveOptionalPipelineIdAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        JsonElement payload,
        JsonElement parameters,
        CancellationToken ct)
    {
        var (id, _, err) = await ResolveOptionalPipelineOverrideAsync(db, scope, payload, parameters, ct).ConfigureAwait(false);
        return (id, err);
    }

    private static async Task<(Guid? PipelineId, bool HasOverride, string? Error)> ResolveOptionalPipelineOverrideAsync(
        AgentConfigurationDbContext db,
        ToolInvocationScope scope,
        JsonElement payload,
        JsonElement parameters,
        CancellationToken ct)
    {
        JsonElement prop = default;
        var found = (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("base_prompt_pipeline_id", out prop))
            || (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("base_prompt_pipeline_id", out prop));
        if (!found) return (null, false, null);
        if (prop.ValueKind == JsonValueKind.Null) return (null, true, null);
        if (prop.ValueKind != JsonValueKind.String) return (null, true, "base_prompt_pipeline_id must be a string (GUID or slug) or null.");
        var raw = prop.GetString()?.Trim();
        if (string.IsNullOrEmpty(raw)) return (null, true, null);
        var resolved = await ResolvePipelineRecordAsync(db, scope, raw, asNoTracking: true, ct).ConfigureAwait(false);
        if (resolved is null)
            return (null, true, $"Prompt pipeline '{raw}' not found.");
        return (resolved.Id, true, null);
    }

    private static (string SourceKind, string? Error) ResolveAgentSourceKind(JsonElement payload, JsonElement parameters, string defaultKind)
    {
        var raw = Text(payload, "source_kind") ?? Text(parameters, "source_kind");
        if (string.IsNullOrWhiteSpace(raw))
            return (defaultKind, null);
        var normalized = raw.Trim().ToLowerInvariant();
        return normalized is "agent_generated" or "custom"
            ? (normalized, null)
            : (defaultKind, "source_kind must be 'agent_generated' or 'custom'.");
    }

    private static string? BuildProvenanceJson(
        ToolInvocationScope scope,
        JsonElement payload,
        JsonElement parameters,
        DateTimeOffset now,
        string? existingJson)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (ParseJsonOrNull(existingJson) is { ValueKind: JsonValueKind.Object } existingObj)
        {
            foreach (var p in existingObj.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
        }
        if (TryGetObjectProperty(payload, parameters, "provenance", out var provObj))
        {
            foreach (var p in provObj.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
        }
        if (scope.SessionId != Guid.Empty && !dict.ContainsKey("session_id"))
            dict["session_id"] = scope.SessionId;
        if (scope.RunId != Guid.Empty)
        {
            if (!dict.ContainsKey("run_id")) dict["run_id"] = scope.RunId;
            if (!dict.ContainsKey("created_by_run_id")) dict["created_by_run_id"] = scope.RunId;
        }
        if (scope.AgentInstanceId != Guid.Empty && !dict.ContainsKey("created_by_agent_id"))
            dict["created_by_agent_id"] = scope.AgentInstanceId;
        if (!dict.ContainsKey("created_at"))
            dict["created_at"] = now.ToString("O");
        return JsonSerializer.Serialize(dict);
    }

    private static async Task<string?> UpsertAgentModelBindingAsync(
        AgentConfigurationDbContext db,
        AgentDefinitionRecord agent,
        ToolInvocationScope scope,
        JsonElement bindingEl,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rawMode = Text(bindingEl, "mode") ?? Text(bindingEl, "strategy") ?? Text(bindingEl, "kind") ?? Text(bindingEl, "selection_kind");
        var providerId = GuidOf(bindingEl, "provider_instance_id") ?? GuidOf(bindingEl, "provider_id");
        var model = Text(bindingEl, "model") ?? Text(bindingEl, "model_id");
        var routePurpose = Text(bindingEl, "route_purpose") ?? Text(bindingEl, "purpose") ?? Text(bindingEl, "tier");
        var mode = (rawMode ?? (providerId is not null && !string.IsNullOrWhiteSpace(model) ? "fixed" : !string.IsNullOrWhiteSpace(routePurpose) ? "route" : "inherit"))
            .Trim().ToLowerInvariant();

        if (mode is not ("inherit" or "route" or "fixed"))
            return "model_binding.mode must be 'inherit', 'route', or 'fixed'.";
        if (mode == "fixed" && (providerId is null || string.IsNullOrWhiteSpace(model)))
            return "model_binding with mode='fixed' requires provider_id (UUID) and model_id.";
        if (mode == "route" && string.IsNullOrWhiteSpace(routePurpose))
            return "model_binding with mode='route' requires route_purpose.";

        var toolScopeOverride = Raw(bindingEl, "tool_scope_override");
        var existing = await db.AgentRuntimeBindings
            .FirstOrDefaultAsync(x => x.AgentDefinitionId == agent.Id && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            db.AgentRuntimeBindings.Add(new AgentRuntimeBindingRecord
            {
                AgentDefinitionId = agent.Id,
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                Mode = mode,
                ProviderInstanceId = mode == "fixed" ? providerId : null,
                Model = mode == "fixed" ? model!.Trim() : null,
                RoutePurpose = mode == "route" ? routePurpose!.Trim() : null,
                ToolScopeOverrideJson = toolScopeOverride,
                Revision = 1,
                UpdatedAt = now,
                UpdatedByPrincipalId = scope.PrincipalId,
            });
        }
        else
        {
            existing.Mode = mode;
            existing.ProviderInstanceId = mode == "fixed" ? providerId : null;
            existing.Model = mode == "fixed" ? model!.Trim() : null;
            existing.RoutePurpose = mode == "route" ? routePurpose!.Trim() : null;
            if (toolScopeOverride is not null) existing.ToolScopeOverrideJson = toolScopeOverride;
            existing.Revision++;
            existing.UpdatedAt = now;
            existing.UpdatedByPrincipalId = scope.PrincipalId;
        }
        return null;
    }

    private async Task<string?> ApplyAgentToolSettingsAsync(
        Guid agentId,
        ToolInvocationScope scope,
        JsonElement settingsEl,
        CancellationToken ct)
    {
        if (_toolSettingsStore is null)
            return null;

        var patchDict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (settingsEl.TryGetProperty("mcp", out var mcpObj) && mcpObj.ValueKind == JsonValueKind.Object)
        {
            patchDict["mcp"] = mcpObj;
        }
        else if (settingsEl.TryGetProperty("mcp_server_ids", out var mcpIds) || settingsEl.TryGetProperty("server_resource_ids", out mcpIds))
        {
            patchDict["mcp"] = new { enabled = true, server_resource_ids = mcpIds };
        }

        if (settingsEl.TryGetProperty("skills", out var skillsObj) && skillsObj.ValueKind == JsonValueKind.Object)
        {
            patchDict["skills"] = skillsObj;
        }
        else if (settingsEl.TryGetProperty("skill_resource_ids", out var skillIds) || settingsEl.TryGetProperty("resource_ids", out skillIds))
        {
            patchDict["skills"] = new { enabled = true, resource_ids = skillIds };
        }

        var patchElement = patchDict.Count > 0
            ? JsonSerializer.SerializeToElement(patchDict)
            : settingsEl;

        var current = await _toolSettingsStore.GetAsync(agentId, ct).ConfigureAwait(false);
        var merged = MergeSparseJsonObjects(current.Settings, patchElement);
        await _toolSettingsStore.SaveAsync(agentId, merged, current.Revision, ct, ProjectOf(scope)).ConfigureAwait(false);
        return null;
    }

    private static JsonElement MergeSparseJsonObjects(JsonElement existing, JsonElement patch)
    {
        var baseNode = existing.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(existing.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        if (patch.ValueKind == JsonValueKind.Object && JsonNode.Parse(patch.GetRawText()) is JsonObject patchObj)
        {
            MergeJsonObjectInPlace(baseNode, patchObj);
        }
        using var doc = JsonDocument.Parse(baseNode.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static void MergeJsonObjectInPlace(JsonObject target, JsonObject patch)
    {
        foreach (var kv in patch)
        {
            if (kv.Value is JsonObject patchChild && target[kv.Key] is JsonObject existingChild)
            {
                MergeJsonObjectInPlace(existingChild, patchChild);
            }
            else
            {
                target[kv.Key] = kv.Value?.DeepClone();
            }
        }
    }

    private static string? ExtractPipelineGraphJson(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null;
        if (source.TryGetProperty("graph", out var graphEl) && graphEl.ValueKind == JsonValueKind.Object)
            return graphEl.GetRawText();
        var hasNodes = source.TryGetProperty("nodes", out var nodesEl) && nodesEl.ValueKind == JsonValueKind.Array;
        var hasEdges = source.TryGetProperty("edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array;
        if (!hasNodes && !hasEdges) return null;
        return JsonSerializer.Serialize(new
        {
            nodes = hasNodes ? nodesEl : JsonSerializer.SerializeToElement(Array.Empty<object>()),
            edges = hasEdges ? edgesEl : JsonSerializer.SerializeToElement(Array.Empty<object>()),
        });
    }

    private static string? ValidatePromptPipelineGraph(string graphJson, bool requirePublishNodes)
    {
        try
        {
            using var doc = JsonDocument.Parse(graphJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "Pipeline graph must be a JSON object.";

            var nodes = new List<JsonElement>();
            if (root.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Array)
                nodes.AddRange(n.EnumerateArray());
            else if (root.TryGetProperty("prompt_nodes", out var pn) && pn.ValueKind == JsonValueKind.Array)
                nodes.AddRange(pn.EnumerateArray());

            var hasAssemble = false;
            var hasTemplate = false;
            var nodesById = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes)
            {
                if (!node.TryGetProperty("type", out var t) && !node.TryGetProperty("kind", out t))
                    return "Each pipeline node must have a 'type' or 'kind'.";
                var type = t.GetString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(type) || !AllowedPromptNodeTypes.Contains(type))
                    return $"Unknown prompt pipeline node type '{type}'.";
                if (string.Equals(type, "assemble", StringComparison.OrdinalIgnoreCase)) hasAssemble = true;
                if (string.Equals(type, "template", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "fragment_ref", StringComparison.OrdinalIgnoreCase))
                {
                    hasTemplate = true;
                }
                var id = node.TryGetProperty("id", out var idEl) ? idEl.GetString()
                    : node.TryGetProperty("node_key", out var nk) ? nk.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id)) nodesById.Add(id!);
            }

            if (requirePublishNodes && (!hasAssemble || !hasTemplate))
                return "Published prompt pipeline must contain at least one template/fragment_ref node and one assemble node.";

            if (root.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
            {
                var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in edges.EnumerateArray())
                {
                    var src = e.TryGetProperty("source", out var s) ? s.GetString()
                        : e.TryGetProperty("source_node_key", out var sn) ? sn.GetString() : null;
                    var dst = e.TryGetProperty("target", out var tg) ? tg.GetString()
                        : e.TryGetProperty("target_node_key", out var tn) ? tn.GetString() : null;
                    if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
                        return "Each pipeline edge must declare source and target node ids.";
                    if (nodesById.Count > 0 && (!nodesById.Contains(src!) || !nodesById.Contains(dst!)))
                        return $"Pipeline edge '{src}' -> '{dst}' references an undeclared node.";
                    if (!adj.TryGetValue(src!, out var list))
                        adj[src!] = list = [];
                    list.Add(dst!);
                }

                var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                bool HasCycle(string u)
                {
                    state[u] = 1;
                    if (adj.TryGetValue(u, out var nexts))
                    {
                        foreach (var v in nexts)
                        {
                            var st = state.GetValueOrDefault(v);
                            if (st == 1 || (st == 0 && HasCycle(v))) return true;
                        }
                    }
                    state[u] = 2;
                    return false;
                }
                foreach (var nodeKey in adj.Keys)
                {
                    if (state.GetValueOrDefault(nodeKey) == 0 && HasCycle(nodeKey))
                        return "Prompt pipeline graph cannot contain cycles.";
                }
            }
            return null;
        }
        catch (JsonException ex)
        {
            return $"Invalid prompt pipeline graph JSON: {ex.Message}";
        }
    }

    // ── tinadec_context_memory_apply ──

    private async Task<ToolWireResponseDto> ContextMemoryApplyAsync(
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken ct)
    {
        if (_contextMemory is null)
            return Fail(wire, "service_unavailable", "Context memory service is not available in this host.");

        var parameters = wire.Params ?? default;
        if (parameters.ValueKind != JsonValueKind.Object)
            return Fail(wire, "invalid_request", "Parameters must be a JSON object.");

        var action = (Text(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
            return Fail(wire, "invalid_request", "action is required.");

        try
        {
            var parsed = JsonSerializer.Deserialize<ContextMemoryApplyRequest>(parameters.GetRawText(), SnakeJson)
                ?? new ContextMemoryApplyRequest();
            var payload = ChildObject(parameters, "payload");
            var policy = parsed.Policy;
            var memory = parsed.Memory;

            if (policy is null && action is "update_policy" or "set_strategy" && payload.ValueKind == JsonValueKind.Object)
            {
                policy = JsonSerializer.Deserialize<UpdateContextMemoryPolicyRequest>(payload.GetRawText(), SnakeJson);
            }
            if (policy is not null && string.IsNullOrWhiteSpace(policy.ExpectedContentHash) && string.IsNullOrWhiteSpace(policy.ExpectedHash))
            {
                var topHash = Text(parameters, "expected_content_hash") ?? Text(parameters, "expected_hash");
                if (!string.IsNullOrWhiteSpace(topHash))
                {
                    policy = new UpdateContextMemoryPolicyRequest
                    {
                        Strategy = policy.Strategy,
                        RecallFidelity = policy.RecallFidelity,
                        CompactionTriggerRatio = policy.CompactionTriggerRatio,
                        RecentMessageLimit = policy.RecentMessageLimit,
                        DefaultTokenBudget = policy.DefaultTokenBudget,
                        AllowCrossSessionRecall = policy.AllowCrossSessionRecall,
                        OffloadLargeToolPayloadBytes = policy.OffloadLargeToolPayloadBytes,
                        EvolutionMode = policy.EvolutionMode,
                        CandidateOnly = policy.CandidateOnly,
                        RetrievalLimit = policy.RetrievalLimit,
                        CoreMarkdownMaxTokens = policy.CoreMarkdownMaxTokens,
                        ProjectMarkdownProjection = policy.ProjectMarkdownProjection,
                        ProjectMemoryPath = policy.ProjectMemoryPath,
                        CompanionCanWriteProjectMemory = policy.CompanionCanWriteProjectMemory,
                        ExpectedContentHash = topHash,
                    };
                }
            }

            if (memory is null && action is "upsert_memory" or "supersede_memory" && payload.ValueKind == JsonValueKind.Object)
            {
                memory = JsonSerializer.Deserialize<UpsertMemoryItemRequest>(payload.GetRawText(), SnakeJson);
            }

            var req = new ContextMemoryApplyRequest
            {
                Action = action,
                Policy = policy,
                Memory = memory,
                ItemId = parsed.ItemId ?? GuidOf(payload, "item_id"),
                TargetVersion = parsed.TargetVersion ?? ReadNullableInt(payload, "target_version"),
                Reason = parsed.Reason ?? Text(payload, "reason"),
            };

            var result = await _contextMemory.ApplySelfConfigAsync(
                req,
                scope.SessionId == Guid.Empty ? null : scope.SessionId,
                scope.RunId == Guid.Empty ? null : scope.RunId,
                ProjectOf(scope, parameters, payload),
                string.IsNullOrWhiteSpace(scope.WorkspaceRoot) ? null : scope.WorkspaceRoot,
                ct).ConfigureAwait(false);

            return OkSnake(wire, result);
        }
        catch (ConfigurationDocumentException ex)
        {
            var code = string.Equals(ex.Code, "configuration_conflict", StringComparison.OrdinalIgnoreCase)
                ? "conflict"
                : "invalid_request";
            return Fail(wire, code, ex.Message, ex.Diagnostics);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(wire, "forbidden", ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return Fail(wire, "not_found", ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return Fail(wire, "invalid_request", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(wire, "conflict", ex.Message);
        }
    }

    // ── projections ──

    private static Task<AgentRuntimeBindingRecord?> LoadBindingAsync(
        AgentConfigurationDbContext db,
        Guid agentId,
        ToolInvocationScope scope,
        CancellationToken ct) =>
        db.AgentRuntimeBindings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentDefinitionId == agentId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct);

    private static object DescribeAgent(AgentDefinitionRecord agent, bool builtIn, AgentRuntimeBindingRecord? binding = null) => new
    {
        id = agent.Id,
        slug = agent.Slug,
        display_name = agent.DisplayName,
        layer = agent.Layer,
        role = agent.Role,
        status = agent.Status,
        enabled = agent.Enabled,
        source_kind = agent.SourceKind,
        base_prompt_pipeline_id = agent.BasePromptPipelineId,
        model_strategy = ParseJsonOrNull(agent.ModelStrategyJson) ?? DefaultModelStrategyElement(),
        model_binding = binding is null ? null : DescribeRuntimeBinding(binding),
        provenance = ParseJsonOrNull(agent.ProvenanceJson),
        version = agent.Version,
        revision = agent.Revision,
        built_in = builtIn,
        writable = !builtIn && !string.Equals(agent.Status, "archived", StringComparison.Ordinal),
        capabilities = ParseStringArray(agent.CapabilitiesJson),
        tool_ids = ParseStringArray(agent.ToolScopeJson).Take(MaxRows).ToArray(),
    };

    private static object DescribeRuntimeBinding(AgentRuntimeBindingRecord binding) => new
    {
        mode = binding.Mode,
        provider_instance_id = binding.ProviderInstanceId,
        model = binding.Model,
        route_purpose = binding.RoutePurpose,
        tool_scope_override = ParseJsonOrNull(binding.ToolScopeOverrideJson),
        revision = binding.Revision,
        updated_at = binding.UpdatedAt,
    };

    private static object DescribePipeline(PromptPipelineRecord pipeline, bool builtIn) => new
    {
        id = pipeline.Id,
        slug = pipeline.Slug,
        display_name = pipeline.DisplayName,
        description = pipeline.Description,
        status = pipeline.Status,
        version = pipeline.Version,
        revision = pipeline.Revision,
        built_in = builtIn,
        writable = !builtIn && !string.Equals(pipeline.Status, "archived", StringComparison.Ordinal),
        graph = ParseJsonOrNull(pipeline.GraphJson) ?? EmptyGraphElement(),
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
        transport = server.Transport,
        url = RedactUrlForInspect(server.Url),
        command = server.Command,
        args = server.Args.Take(MaxRows).ToArray(),
        // Names only: values are secret references and never leave Core.
        env_keys = (server.Env?.Keys ?? Enumerable.Empty<string>()).OrderBy(key => key, StringComparer.Ordinal).Take(MaxRows).ToArray(),
        header_keys = (server.Headers?.Keys ?? Enumerable.Empty<string>()).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).Take(MaxRows).ToArray(),
        has_secrets = (server.Env?.Count ?? 0) > 0 || (server.Headers?.Count ?? 0) > 0,
        cwd = server.Cwd,
        project_id = server.ProjectId,
        revision = server.Revision,
        import_source = server.ImportSource,
    };

    private static string? RedactUrlForInspect(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)) return url;
        var builder = new UriBuilder(uri);
        if (!string.IsNullOrEmpty(builder.Password)) builder.Password = "********";
        if (!string.IsNullOrEmpty(uri.UserInfo)) builder.UserName = "********";
        if (!string.IsNullOrEmpty(uri.Query)) builder.Query = "redacted=********";
        return builder.Uri.ToString();
    }

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

    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static ToolWireResponseDto Ok(ToolWireRequestDto wire, object payload) => new()
    {
        CallId = wire.ToolCallId,
        IsSuccess = true,
        Result = JsonSerializer.SerializeToElement(payload),
    };

    private static ToolWireResponseDto OkSnake(ToolWireRequestDto wire, object payload) => new()
    {
        CallId = wire.ToolCallId,
        IsSuccess = true,
        Result = JsonSerializer.SerializeToElement(payload, SnakeJson),
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

    private static bool TryGetObjectProperty(JsonElement primary, JsonElement fallback, string name, out JsonElement value)
    {
        if (primary.ValueKind == JsonValueKind.Object && primary.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object)
        {
            value = p;
            return true;
        }
        if (fallback.ValueKind == JsonValueKind.Object && fallback.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.Object)
        {
            value = f;
            return true;
        }
        value = default;
        return false;
    }

    private static Guid? GuidOf(JsonElement element, string name) =>
        Text(element, name) is { } raw && Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : null;

    private static int ReadInt(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static int? ReadNullableInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static long ReadLong(JsonElement element, string name, long fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : fallback;

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

    private static JsonElement? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement DefaultModelStrategyElement() =>
        JsonDocument.Parse("{\"kind\":\"inherit\"}").RootElement.Clone();

    private static JsonElement EmptyGraphElement() =>
        JsonDocument.Parse("{\"nodes\":[],\"edges\":[]}").RootElement.Clone();

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

    private static IReadOnlyDictionary<string, string?>? ReadStringMap(JsonElement input, string propertyName, string errorCode)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(propertyName, out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in mapEl.EnumerateObject())
            map[pair.Name] = pair.Value.ValueKind switch
            {
                JsonValueKind.String => pair.Value.GetString(),
                JsonValueKind.Null => null,
                _ => throw new ToolSettingsException(errorCode, $"{propertyName} values must be strings or null."),
            };
        return map.Count == 0 ? null : map;
    }
}
