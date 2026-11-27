using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Models;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

internal sealed class AgentModelResolver : IAgentModelResolver
{
    private static readonly JsonSerializerOptions FrozenJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<AgentConfigurationDbContext> _agents;
    private readonly GlobalAgentConfiguration _globalConfiguration;
    private readonly IDbContextFactory<ModelControlDbContext> _models;
    private readonly IDbContextFactory<LifecycleDbContext> _lifecycle;
    private readonly IDbContextFactory<AgentControlDbContext> _instances;
    private readonly ILifecycleManager _lifecycleManager;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;
    private readonly ITenantContextAccessor _tenant;
    private readonly ISessionLocator _sessions;
    private readonly ModelHealthRegistry _health;
    private readonly IModelProfileCatalogService? _catalog;

    public AgentModelResolver(
        IDbContextFactory<AgentConfigurationDbContext> agents,
        GlobalAgentConfiguration globalConfiguration,
        IDbContextFactory<ModelControlDbContext> models,
        IDbContextFactory<LifecycleDbContext> lifecycle,
        IDbContextFactory<AgentControlDbContext> instances,
        ILifecycleManager lifecycleManager,
        IContentStore content,
        ISecretStore secrets,
        ITenantContextAccessor tenant,
        ISessionLocator sessions,
        ModelHealthRegistry health,
        IModelProfileCatalogService? catalog = null)
    {
        _agents = agents;
        _globalConfiguration = globalConfiguration;
        _models = models;
        _lifecycle = lifecycle;
        _instances = instances;
        _lifecycleManager = lifecycleManager;
        _content = content;
        _secrets = secrets;
        _tenant = tenant;
        _sessions = sessions;
        _health = health;
        _catalog = catalog;
    }

    public Task<ModelResolutionPreviewDto> PreviewAsync(ModelResolutionPreviewRequestDto request, CancellationToken cancellationToken = default) =>
        PreviewCoreAsync(request, null, cancellationToken);

    public async Task<IReadOnlyList<ModelResolutionPreviewDto>> PreviewBatchAsync(IReadOnlyList<ModelResolutionPreviewRequestDto> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) return [];
        var scope = _tenant.Current;
        var agentIds = requests.Where(x => x.AgentDefinitionId.HasValue).Select(x => x.AgentDefinitionId!.Value).Distinct().ToArray();
        var agentVersions = requests.Where(x => x.AgentVersionId.HasValue).Select(x => x.AgentVersionId!.Value).Distinct().ToArray();
        var modeVersions = requests.Where(x => x.ModeVersionId.HasValue).Select(x => x.ModeVersionId!.Value).Distinct().ToArray();
        var batch = await LoadBatchAsync(_agents, scope, agentIds, agentVersions, modeVersions, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(_agents, _globalConfiguration.Factory))
        {
            var global = await LoadBatchAsync(_globalConfiguration.Factory, scope, agentIds, agentVersions, modeVersions, cancellationToken).ConfigureAwait(false);
            batch = new PreviewBatch(
                Merge(batch.Bindings, global.Bindings),
                Merge(batch.AgentVersions, global.AgentVersions),
                Merge(batch.ModeVersions, global.ModeVersions),
                Merge(batch.Definitions, global.Definitions));
        }
        try
        {
            var previews = new List<ModelResolutionPreviewDto>(requests.Count);
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { previews.Add(await PreviewCoreAsync(request, batch, cancellationToken).ConfigureAwait(false)); }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or KeyNotFoundException)
                { previews.Add(ModelPreviewFailure.Unavailable(error.Message)); }
            }
            return previews;
        }
        finally
        {
            batch.Dispose();
        }
    }

    // This cache lives only for one read. It never survives a scope switch,
    // another HTTP request, a configuration edit or a run admission.
    private sealed record PreviewBatch(
        IReadOnlyDictionary<Guid, AgentRuntimeBindingRecord> Bindings,
        IReadOnlyDictionary<Guid, AgentVersionRecord> AgentVersions,
        IReadOnlyDictionary<Guid, ModeVersionRecord> ModeVersions,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> Definitions) : IDisposable
    {
        public Dictionary<PlanCacheKey, Task<FrozenModelPlan>> Plans { get; } = new();
        public Dictionary<ResolutionCacheKey, Task<IReadOnlyList<ChatResolution>>> Resolutions { get; } = new();

        internal readonly record struct PlanCacheKey(
            string Kind,
            string? RoutePurpose,
            Guid? ProviderInstanceId,
            string? Model,
            string SpecialtiesKey,
            string? PreferredTier,
            string? MaxCostTier,
            string Source,
            bool HasParent)
        {
            public static PlanCacheKey Create(ModelStrategyDto strategy, string source, bool hasParent) => new(
                strategy.Kind,
                strategy.RoutePurpose,
                strategy.ProviderInstanceId,
                strategy.Model,
                strategy.RequiredSpecialties is { Count: > 0 } specs ? string.Join('\0', specs) : string.Empty,
                strategy.PreferredTier,
                strategy.MaxCostTier,
                source,
                hasParent);
        }

        internal readonly struct ResolutionCacheKey(FrozenModelPlan plan, Guid? parentInstanceId) : IEquatable<ResolutionCacheKey>
        {
            public FrozenModelPlan Plan { get; } = plan;
            public Guid? ParentInstanceId { get; } = parentInstanceId;

            public bool Equals(ResolutionCacheKey other) =>
                ReferenceEquals(Plan, other.Plan) && ParentInstanceId == other.ParentInstanceId;

            public override bool Equals(object? obj) => obj is ResolutionCacheKey other && Equals(other);

            public override int GetHashCode() =>
                HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Plan), ParentInstanceId);
        }
        public Dictionary<Guid, IReadOnlyDictionary<string, ModelStrategyDto?>> ParsedModeNodeStrategies { get; } = new();
        public Dictionary<Guid, ModelStrategyDto> ParsedAgentStrategies { get; } = new();
        public Task<ModelCatalogSnapshot>? ModelsTask { get; set; }
        public Dictionary<Guid, JsonDocument?> ProviderDocuments { get; } = new();
        public Dictionary<string, string?> Secrets { get; } = new(StringComparer.Ordinal);
        public Task<SanitizedModelCatalogDto>? SanitizedCatalogTask { get; set; }
        public Dictionary<Guid, ModelProviderRecord> ExtraProviders { get; } = new();
        public Dictionary<Guid, ModelProviderVersionRecord> ExtraProviderVersions { get; } = new();

        public void Dispose()
        {
            foreach (var doc in ProviderDocuments.Values) doc?.Dispose();
            ProviderDocuments.Clear();
        }
    }

    private sealed record ModelCatalogSnapshot(
        Dictionary<string, ModelRouteRecord> RoutesByPurpose,
        Dictionary<Guid, ModelRouteVersionRecord> RouteVersionsById,
        ILookup<Guid, ModelRouteCandidateRecord> RouteCandidatesByVersionId,
        Dictionary<Guid, ModelProviderRecord> ProvidersById,
        Dictionary<Guid, ModelProviderVersionRecord> ProviderVersionsById);

    private async Task<ModelCatalogSnapshot> GetModelCatalogAsync(PreviewBatch batch, CancellationToken ct)
    {
        if (batch.ModelsTask is not null) return await batch.ModelsTask.ConfigureAwait(false);
        var scope = _tenant.Current;
        batch.ModelsTask = LoadModelCatalogAsync(scope, ct);
        return await batch.ModelsTask.ConfigureAwait(false);
    }

    private async Task<ModelCatalogSnapshot> LoadModelCatalogAsync(TenantContext scope, CancellationToken ct)
    {
        await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
        var routes = await db.Routes.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        var routeVersionIds = routes.Select(r => r.CurrentVersionId).Distinct().ToArray();
        var routeVersions = routeVersionIds.Length == 0
            ? new Dictionary<Guid, ModelRouteVersionRecord>()
            : await db.RouteVersions.AsNoTracking().Where(x => routeVersionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        var candidates = routeVersionIds.Length == 0
            ? new List<ModelRouteCandidateRecord>()
            : await db.RouteCandidates.AsNoTracking().Where(x => routeVersionIds.Contains(x.RouteVersionId)).OrderBy(x => x.Position).ToListAsync(ct).ConfigureAwait(false);
        var providers = await db.Providers.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId))
            .ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        var versionIds = providers.Values.Select(p => p.CurrentVersionId).Distinct().ToArray();
        var providerVersions = versionIds.Length == 0
            ? new Dictionary<Guid, ModelProviderVersionRecord>()
            : await db.ProviderVersions.AsNoTracking().Where(x => versionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        return new ModelCatalogSnapshot(
            routes.GroupBy(r => r.Purpose, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            routeVersions,
            candidates.ToLookup(c => c.RouteVersionId),
            providers,
            providerVersions);
    }

    private static async Task<PreviewBatch> LoadBatchAsync(
        IDbContextFactory<AgentConfigurationDbContext> factory,
        TenantContext scope,
        Guid[] agentIds,
        Guid[] agentVersions,
        Guid[] modeVersions,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return new PreviewBatch(
            await db.AgentRuntimeBindings.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.WorkspaceId == scope.WorkspaceId && agentIds.Contains(x.AgentDefinitionId))
                .ToDictionaryAsync(x => x.AgentDefinitionId, cancellationToken).ConfigureAwait(false),
            await db.AgentVersions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.WorkspaceId == scope.WorkspaceId && agentVersions.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false),
            await db.ModeVersions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.WorkspaceId == scope.WorkspaceId && modeVersions.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false),
            await db.AgentDefinitions.AsNoTracking().Where(x => x.TenantId == scope.TenantId
                && x.WorkspaceId == scope.WorkspaceId && agentIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false));
    }

    private static IReadOnlyDictionary<TKey, TValue> Merge<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> local,
        IReadOnlyDictionary<TKey, TValue> global)
        where TKey : notnull
    {
        var merged = new Dictionary<TKey, TValue>(local);
        foreach (var pair in global) merged[pair.Key] = pair.Value;
        return merged;
    }

    private async Task<T?> FindFirstAsync<T>(
        Func<AgentConfigurationDbContext, IQueryable<T>> query,
        CancellationToken cancellationToken) where T : class
    {
        if (!ReferenceEquals(_agents, _globalConfiguration.Factory))
        {
            await using var global = await _globalConfiguration.Factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var found = await query(global).AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (found is not null) return found;
        }

        await using var local = await _agents.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await query(local).AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelResolutionPreviewDto> PreviewCoreAsync(ModelResolutionPreviewRequestDto request, PreviewBatch? batch,
        CancellationToken cancellationToken)
    {
        var scope = _tenant.Current;
        var chain = new List<ModelResolutionStepDto>();
        ModelStrategyDto? selected = null;
        var source = string.Empty;

        void Consider(string candidateSource, ModelStrategyDto? strategy)
        {
            if (strategy is null) return;
            var use = selected is null;
            chain.Add(new ModelResolutionStepDto { Source = candidateSource, Strategy = strategy, Selected = use });
            if (use) { selected = strategy; source = candidateSource; }
        }

        if (request.MeetingModelOverride is { } meetingOverride)
        {
            Consider("session_override", new ModelStrategyDto
            {
                Kind = ModelStrategyKinds.Fixed,
                ProviderInstanceId = meetingOverride.ProviderInstanceId,
                Model = meetingOverride.Model
            });
        }

        Consider("request_strategy", request.Strategy);

        // 用户级运行时绑定（配置体验改造 A）：与 FormalModeResolver.ResolveRosterAsync
        // 采用同一优先级模型——user_binding > mode_node_override > agent_version。预览路径
        // 此前缺这一档，导致智能体中心保存绑定后 effective_previews 仍反映保存前的解析链，
        // 用户读作“设置成功但没生效”。以 AgentDefinitionId 守卫：InteractionsEndpoints 的
        // meeting-root 预览只传 MeetingModelOverride，不受影响。
        if (request.AgentDefinitionId is { } bindingAgentId)
        {
            AgentRuntimeBindingRecord? binding;
            if (batch is not null) binding = batch.Bindings.GetValueOrDefault(bindingAgentId);
            else
                binding = await FindFirstAsync(db => db.AgentRuntimeBindings
                    .Where(x => x.AgentDefinitionId == bindingAgentId
                        && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId), cancellationToken).ConfigureAwait(false);
            Consider("user_binding", BindingToStrategy(binding));
        }

        if (request.ModeVersionId is { } modeVersionId && !string.IsNullOrWhiteSpace(request.NodeKey))
        {
            ModeVersionRecord? version;
            if (batch is not null) version = batch.ModeVersions.GetValueOrDefault(modeVersionId);
            else
                version = await FindFirstAsync(db => db.ModeVersions
                    .Where(x => x.Id == modeVersionId
                        && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId), cancellationToken).ConfigureAwait(false);
            ModelStrategyDto? strategy = null;
            if (version is not null)
            {
                if (batch is not null)
                {
                    if (!batch.ParsedModeNodeStrategies.TryGetValue(modeVersionId, out var nodeMap))
                        batch.ParsedModeNodeStrategies[modeVersionId] = nodeMap = ReadModeNodeStrategies(version.SnapshotJson);
                    nodeMap.TryGetValue(request.NodeKey!, out strategy);
                }
                else
                {
                    strategy = ReadModeNodeStrategy(version.SnapshotJson, request.NodeKey!);
                }
            }
            Consider("mode_node_override", strategy);
        }

        if (request.AgentVersionId is { } agentVersionId)
        {
            AgentVersionRecord? version;
            if (batch is not null) version = batch.AgentVersions.GetValueOrDefault(agentVersionId);
            else
                version = await FindFirstAsync(db => db.AgentVersions
                    .Where(x => x.Id == agentVersionId
                        && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId), cancellationToken).ConfigureAwait(false);
            ModelStrategyDto? agentStrategy = null;
            if (version is not null)
            {
                if (batch is not null)
                {
                    if (!batch.ParsedAgentStrategies.TryGetValue(agentVersionId, out var parsedAgentStrategy))
                        batch.ParsedAgentStrategies[agentVersionId] = parsedAgentStrategy = ReadAgentStrategy(version.SnapshotJson);
                    agentStrategy = parsedAgentStrategy;
                }
                else
                {
                    agentStrategy = ReadAgentStrategy(version.SnapshotJson);
                }
            }
            Consider("agent_version", agentStrategy);
        }
        else if (request.AgentDefinitionId is { } agentDefinitionId)
        {
            AgentDefinitionRecord? definition;
            if (batch is not null) definition = batch.Definitions.GetValueOrDefault(agentDefinitionId);
            else
                definition = await FindFirstAsync(db => db.AgentDefinitions
                    .Where(x => x.Id == agentDefinitionId
                        && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId), cancellationToken).ConfigureAwait(false);
            Consider("agent_default", definition is null ? null : ModelStrategyJson.Parse(definition.ModelStrategyJson));
        }

        selected ??= new ModelStrategyDto { Kind = ModelStrategyKinds.Inherit };
        if (chain.Count == 0) chain.Add(new ModelResolutionStepDto { Source = "implicit_inherit", Strategy = selected, Selected = true });
        source = source.Length == 0 ? "implicit_inherit" : source;

        var planKey = PreviewBatch.PlanCacheKey.Create(selected, source, request.ParentInstanceId is not null);
        Task<FrozenModelPlan> planRead;
        if (batch is null) planRead = FreezeStrategyAsync(selected, source, request.ParentInstanceId is not null, null, cancellationToken);
        else if (!batch.Plans.TryGetValue(planKey, out planRead!))
            batch.Plans[planKey] = planRead = FreezeStrategyAsync(selected, source, request.ParentInstanceId is not null, batch, cancellationToken);
        var plan = await planRead.ConfigureAwait(false);
        var resolutionKey = new PreviewBatch.ResolutionCacheKey(plan, request.ParentInstanceId);
        Task<IReadOnlyList<ChatResolution>> resolutionRead;
        if (batch is null) resolutionRead = ResolveInvocationCandidatesAsync(plan, request.ParentInstanceId, null, cancellationToken);
        else if (!batch.Resolutions.TryGetValue(resolutionKey, out resolutionRead!))
            batch.Resolutions[resolutionKey] = resolutionRead = ResolveInvocationCandidatesAsync(plan, request.ParentInstanceId, batch, cancellationToken);
        var resolutions = await resolutionRead.ConfigureAwait(false);
        var candidates = resolutions.Select(ToPreview).ToArray();
        return new ModelResolutionPreviewDto
        {
            StrategySource = source,
            Chain = chain,
            Candidates = candidates,
            ExpectedSelection = candidates.FirstOrDefault(x => x.Available)
        };
    }

    /// <summary>
    /// 把用户级运行时绑定映射为模型策略：fixed 带 provider(+可选 model)，route 带用途，
    /// inherit / 空绑定返回 null 使 Consider 跳过。fixed 的 model 允许为 null（CLI/ACP
    /// 运行时），与 FormalModeResolver 的解析口径一致。
    /// </summary>
    private static ModelStrategyDto? BindingToStrategy(AgentRuntimeBindingRecord? binding)
    {
        if (binding is null) return null;
        if (binding.Mode == "fixed" && binding.ProviderInstanceId is { } provider)
            return new ModelStrategyDto { Kind = ModelStrategyKinds.Fixed, ProviderInstanceId = provider, Model = binding.Model };
        if (binding.Mode == "route" && !string.IsNullOrWhiteSpace(binding.RoutePurpose))
            return new ModelStrategyDto { Kind = ModelStrategyKinds.Route, RoutePurpose = binding.RoutePurpose };
        return null;
    }

    public async Task<FrozenModelPlan> FreezeAsync(AgentModelFreezeRequest request, CancellationToken cancellationToken = default)
    {
        var proxyBinding = TryResolveWorkerProxyBinding(request.SessionId);
        if (proxyBinding is { UseProviderProxy: true })
        {
            return BuildPeerProxyFrozenPlan(request.SessionId, proxyBinding.ModelHint, "peer_provider_proxy");
        }

        var strategy = ModelStrategyJson.Parse(request.StrategyJson);
        var source = request.StrategySource;
        if (request.IsConversationRoot)
        {
            // A conversation's chosen model outlives the provider it names: the provider can be
            // deleted or disabled in the model center later, or the session can run in a storage
            // scope that never had it (each workspace scope keeps its own models.toml). Such an
            // override cannot be honoured, so the run uses the default strategy instead of failing
            // with "provider not found". An explicit choice on the request is checked at admission.
            var meetingOverride = request.MeetingModelOverride;
            if (meetingOverride is not null)
            {
                if (await ProviderUsableAsync(meetingOverride.ProviderInstanceId, cancellationToken).ConfigureAwait(false))
                {
                    strategy = new ModelStrategyDto { Kind = ModelStrategyKinds.Fixed, ProviderInstanceId = meetingOverride.ProviderInstanceId, Model = meetingOverride.Model };
                    source = "turn_override";
                }
            }
            else if (request.UseSessionModelOverride)
            {
                var session = await _sessions.FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
                if (session?.MeetingModelOverride is { } sessionOverride
                    && await ProviderUsableAsync(sessionOverride.ProviderInstanceId, cancellationToken).ConfigureAwait(false))
                {
                    strategy = new ModelStrategyDto { Kind = ModelStrategyKinds.Fixed, ProviderInstanceId = sessionOverride.ProviderInstanceId, Model = sessionOverride.Model };
                    source = "session_override";
                }
            }
        }

        try
        {
            var frozen = await FreezeStrategyAsync(strategy, source, !request.IsConversationRoot && strategy.Kind == ModelStrategyKinds.Inherit, null, cancellationToken).ConfigureAwait(false);
            return proxyBinding is not null
                ? AppendPeerProxyFallback(frozen, request.SessionId, proxyBinding.ModelHint)
                : frozen;
        }
        catch (InvalidDataException) when (proxyBinding is not null)
        {
            return BuildPeerProxyFrozenPlan(request.SessionId, proxyBinding.ModelHint, "peer_provider_proxy_fallback");
        }
    }

    private PeerProviderProxySessionBinding? TryResolveWorkerProxyBinding(Guid sessionId)
    {
        if (sessionId != Guid.Empty && PeerProviderProxyRuntimeRegistry.TryGetBySession(sessionId) is { } direct)
        {
            return direct;
        }

        var hostKey = PeerProviderProxyHostRegistry.ResolveHostPartitionKey(_secrets);
        var hostEntry = PeerProviderProxyHostRegistry.TryGetWorkerSession(sessionId, hostKey);
        if (hostEntry is null) return null;

        return new PeerProviderProxySessionBinding(
            SessionId: sessionId == Guid.Empty ? hostEntry.SessionId.ToString() : sessionId.ToString(),
            RunId: hostEntry.RunId?.ToString(),
            DelegationId: hostEntry.DelegationId,
            ControllerDeviceId: hostEntry.ControllerDeviceId,
            ProxySessionToken: hostEntry.ProxySessionToken,
            UseProviderProxy: hostEntry.ForceProviderProxy,
            ExpiresAtMs: hostEntry.ExpiresAtMs,
            GatewayLoopbackUrl: hostEntry.GatewayLoopbackUrl,
            ModelHint: hostEntry.ModelHint);
    }

    private static FrozenModelPlan BuildPeerProxyFrozenPlan(Guid sessionId, string? modelHint, string source) =>
        new(
            ModelStrategyKinds.Fixed,
            source,
            [
                new FrozenModelCandidate(
                    0,
                    sessionId,
                    Guid.Empty,
                    string.IsNullOrWhiteSpace(modelHint) ? ChatProtocols.PeerProxy : modelHint.Trim(),
                    ChatProtocols.PeerProxy)
            ]);

    private static FrozenModelPlan AppendPeerProxyFallback(FrozenModelPlan plan, Guid sessionId, string? modelHint)
    {
        if (plan.Candidates.Any(c => ChatProtocols.Normalize(c.Protocol) == ChatProtocols.PeerProxy))
            return plan;

        var extended = new List<FrozenModelCandidate>(plan.Candidates.Count + 1);
        extended.AddRange(plan.Candidates);
        extended.Add(new FrozenModelCandidate(
            extended.Count,
            sessionId,
            Guid.Empty,
            string.IsNullOrWhiteSpace(modelHint) ? ChatProtocols.PeerProxy : modelHint.Trim(),
            ChatProtocols.PeerProxy));
        return plan with { Candidates = extended };
    }

    /// <summary>The provider exists in the current scope, is not deleted and is enabled.</summary>
    private async Task<bool> ProviderUsableAsync(Guid providerId, CancellationToken ct)
    {
        var scope = _tenant.Current;
        await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Providers.AsNoTracking().AnyAsync(x => x.Id == providerId && x.TenantId == scope.TenantId
            && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId) && x.DeletedAt == null && x.Enabled, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(FrozenModelPlan plan, Guid? parentInstanceId, CancellationToken cancellationToken = default) =>
        ResolveInvocationCandidatesAsync(plan, parentInstanceId, null, cancellationToken);

    private async Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(FrozenModelPlan plan, Guid? parentInstanceId, PreviewBatch? batch, CancellationToken cancellationToken)
    {
        // Resolve first, filter second: the health breaker removes candidates from a chain the frozen plan
        // already authorized, and never adds one. See ApplyModelHealthAsync for why the all-cooling case
        // reports a category instead of substituting a model.
        var chain = await ResolveChainAsync(plan, parentInstanceId, new HashSet<Guid>(), batch, cancellationToken).ConfigureAwait(false);
        return await ApplyModelHealthAsync(chain, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChatResolution>> ApplyModelHealthAsync(
        IReadOnlyList<ChatResolution> chain,
        CancellationToken cancellationToken)
    {
        // The chain arrives from ResolveChainAsync: every entry in it was already authorized by the frozen
        // plan, so this filter may only remove. The pool-exhausted entry it can return is produced by the
        // policy itself (one owner for the rule); see ModelHealthPolicy.ApplyCooling.
        var keys = ModelHealthPolicy.CoolingKeys(chain);
        if (keys.Count == 0) return chain;

        var cooling = await _health.LoadCoolingAsync(keys, cancellationToken).ConfigureAwait(false);
        return ModelHealthPolicy.ApplyCooling(chain, cooling);
    }

    private async Task<IReadOnlyList<ChatResolution>> ResolveChainAsync(
        FrozenModelPlan plan,
        Guid? parentInstanceId,
        HashSet<Guid> visitedParents,
        PreviewBatch? batch,
        CancellationToken cancellationToken)
    {
        if (plan.InheritFromParent && parentInstanceId is { } parentId)
        {
            if (!visitedParents.Add(parentId)) throw new InvalidDataException("Agent instance parent lineage contains a cycle.");
            var scope = _tenant.Current;
            await using var lifecycle = await _lifecycle.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            // SQLite stores DateTimeOffset as text and cannot translate relational
            // ordering reliably. Materialize the scoped rows before choosing the
            // most recent successful invocation.
            var actual = (await lifecycle.ModelInvocations.AsNoTracking()
                .Where(x => x.AgentInstanceId == parentId && x.TenantId == scope.TenantId
                    && x.WorkspaceId == scope.WorkspaceId && x.Status == "succeeded")
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .OrderByDescending(x => x.CompletedAt)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();
            if (actual is not null)
            {
                var inherited = new FrozenModelCandidate(actual.FallbackPosition, actual.ProviderInstanceId, actual.ProviderVersionId, actual.Model, actual.Protocol, actual.RouteId, actual.RouteVersionId);
                return [await BuildResolutionAsync(inherited, "parent_actual", batch, cancellationToken).ConfigureAwait(false)];
            }

            await using var instances = await _instances.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var parent = await instances.Instances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == parentId
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Parent Agent instance '{parentId}' was not found.");
            var frozen = await _lifecycleManager.GetFrozenRunConfigurationAsync(parent.RunId.ToString(), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Run '{parent.RunId}' has no frozen configuration.");
            var configuration = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(frozen.Content, FrozenJsonOptions)
                ?? throw new InvalidDataException($"Run '{parent.RunId}' has an invalid frozen configuration.");
            var parentDefinition = configuration.OperationAgents.Concat(configuration.ExecutionAgents)
                .SingleOrDefault(item => item.AgentVersionId == parent.AgentVersionId)
                ?? throw new InvalidDataException($"Parent Agent version '{parent.AgentVersionId}' is absent from the frozen run configuration.");
            var parentPlan = parentDefinition.ModelPlan
                ?? throw new InvalidDataException($"Parent Agent '{parentDefinition.Id}' has no frozen model plan.");
            var inheritedCandidates = await ResolveChainAsync(parentPlan, parent.ParentInstanceId, visitedParents, batch, cancellationToken).ConfigureAwait(false);
            return inheritedCandidates.Select(item => WithSource(item, "parent_frozen_plan")).ToArray();
        }

        var result = new List<ChatResolution>(plan.Candidates.Count);
        foreach (var candidate in plan.Candidates)
            result.Add(await BuildResolutionAsync(candidate, plan.StrategySource, batch, cancellationToken).ConfigureAwait(false));
        return result;
    }

    public async Task<Guid> StartInvocationAsync(ModelInvocationStart request, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        var row = new ModelInvocationRecord
        {
            Id = Guid.NewGuid(), CallId = request.CallId, Attempt = request.Attempt,
            TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            SessionId = request.SessionId, RunId = request.RunId, TurnId = request.TurnId,
            AgentInstanceId = request.AgentInstanceId, AgentDefinitionId = request.AgentDefinitionId,
            AgentVersionId = request.AgentVersionId, ModeVersionId = request.ModeVersionId,
            StrategySource = request.StrategySource,
            RouteId = request.Resolution.RouteId, RouteVersionId = request.Resolution.RouteVersionId,
            ProviderInstanceId = request.Resolution.ProviderInstanceId ?? Guid.Empty,
            ProviderVersionId = request.Resolution.ProviderVersionId ?? Guid.Empty,
            Model = request.Resolution.Model, Protocol = request.Resolution.Protocol ?? ChatProtocols.OpenAiChat,
            FallbackPosition = request.Resolution.CandidatePosition, Status = "started", StartedAt = DateTimeOffset.UtcNow
        };
        await using var db = await _lifecycle.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.ModelInvocations.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Id;
    }

    public async Task CompleteInvocationAsync(Guid invocationId, string status, ModelUsage? usage = null, string? errorCategory = null, string? safeErrorMessage = null, CancellationToken cancellationToken = default)
    {
        if (status is not ("succeeded" or "failed" or "cancelled")) throw new ArgumentException("Invocation status must be succeeded|failed|cancelled.", nameof(status));
        await using var db = await _lifecycle.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = _tenant.Current;
        var row = await db.ModelInvocations.SingleAsync(x => x.Id == invocationId
            && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        row.Status = status;
        row.ErrorCategory = errorCategory;
        row.SafeErrorMessage = safeErrorMessage is { Length: > 4096 } ? safeErrorMessage[..4096] : safeErrorMessage;
        row.InputTokens = usage?.InputTokens;
        row.OutputTokens = usage?.OutputTokens;
        row.TotalTokens = usage?.TotalTokens;
        row.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Health is updated after the durable invocation row, so a failure in this step loses a count but can
        // never leave an invocation unattributed. The registry swallows its own failures by contract.
        // `cancelled` deliberately records nothing: a stopped run is not evidence about the provider.
        if (status == "succeeded")
        {
            await _health.RecordSuccessAsync(row.ProviderInstanceId, row.Model, cancellationToken).ConfigureAwait(false);
        }
        else if (status == "failed")
        {
            await _health.RecordFailureAsync(row.ProviderInstanceId, row.Model, errorCategory, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<FrozenModelPlan> FreezeStrategyAsync(ModelStrategyDto strategy, string source, bool inheritFromParent, PreviewBatch? batch, CancellationToken ct)
    {
        if (strategy.Kind == ModelStrategyKinds.Inherit)
        {
            var routePlan = await FreezeRouteAsync("chat", batch, ct).ConfigureAwait(false);
            return routePlan with { StrategyKind = ModelStrategyKinds.Inherit, StrategySource = source, InheritFromParent = inheritFromParent };
        }
        if (strategy.Kind == ModelStrategyKinds.Route)
        {
            var route = await FreezeRouteAsync(strategy.RoutePurpose!, batch, ct).ConfigureAwait(false);
            return route with { StrategySource = source };
        }
        if (strategy.Kind == ModelStrategyKinds.Fixed && strategy.ProviderInstanceId is { } providerId)
        {
            var scope = _tenant.Current;
            ModelProviderRecord? provider;
            ModelProviderVersionRecord? providerVersion;
            if (batch is not null)
            {
                var catalog = await GetModelCatalogAsync(batch, ct).ConfigureAwait(false);
                if (!catalog.ProvidersById.TryGetValue(providerId, out provider) || provider.DeletedAt is not null)
                    throw new InvalidDataException($"Provider '{providerId}' was not found.");
                if (!catalog.ProviderVersionsById.TryGetValue(provider.CurrentVersionId, out providerVersion))
                    throw new InvalidDataException($"Provider '{providerId}' has no current version.");
            }
            else
            {
                await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
                provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == providerId
                    && x.TenantId == scope.TenantId && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId)
                    && x.DeletedAt == null, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Provider '{providerId}' was not found.");
                providerVersion = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider.CurrentVersionId, ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Provider '{providerId}' has no current version.");
            }
            var protocol = await ReadProtocolAsync(provider, providerVersion, batch, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(strategy.Model) && !ChatProtocols.IsModelChosenByHarness(protocol))
                throw new InvalidDataException($"fixed model strategy requires model unless the harness chooses it (protocol '{protocol}').");
            return new FrozenModelPlan(ModelStrategyKinds.Fixed, source,
                [new FrozenModelCandidate(0, provider.Id, providerVersion.Id, strategy.Model, protocol)]);
        }
        if (strategy.Kind == ModelStrategyKinds.Auto)
        {
            return await FreezeAutoAsync(strategy, source, batch, ct).ConfigureAwait(false);
        }
        throw new InvalidDataException("Unsupported model strategy.");
    }

    private async Task<FrozenModelPlan> FreezeAutoAsync(ModelStrategyDto strategy, string source, PreviewBatch? batch, CancellationToken ct)
    {
        if (_catalog is null)
            throw new InvalidDataException("Auto model strategy requires IModelProfileCatalogService.");

        var catalog = batch is not null
            ? await (batch.SanitizedCatalogTask ??= _catalog.GetSanitizedCatalogAsync(includeDisabled: false, ct)).ConfigureAwait(false)
            : await _catalog.GetSanitizedCatalogAsync(includeDisabled: false, ct).ConfigureAwait(false);
        if (catalog.Providers.Count == 0)
            throw new InvalidDataException("No enabled model provider is configured for auto model_strategy.");

        var scope = _tenant.Current;
        Dictionary<Guid, Guid> providerVersionById;
        if (batch is not null)
        {
            var modelCatalog = await GetModelCatalogAsync(batch, ct).ConfigureAwait(false);
            providerVersionById = modelCatalog.ProvidersById.Values
                .Where(x => x.DeletedAt == null && x.Enabled)
                .ToDictionary(x => x.Id, x => x.CurrentVersionId);
        }
        else
        {
            await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
            providerVersionById = await db.Providers.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId
                    && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId)
                    && x.DeletedAt == null && x.Enabled)
                .ToDictionaryAsync(x => x.Id, x => x.CurrentVersionId, ct).ConfigureAwait(false);
        }

        var providerById = catalog.Providers.ToDictionary(p => p.Id);
        var coolingSet = catalog.Health
            .Where(h => h.IsCooling)
            .Select(h => (h.ProviderInstanceId, h.Model))
            .ToHashSet();

        var routeCandidates = new List<(SanitizedProviderCatalogItemDto Provider, string Model, Guid? RouteId, int? RoutePos)>();
        var chatRoutePositions = new Dictionary<(Guid ProviderId, string Model), int>();
        var chatRoute = catalog.Routes.FirstOrDefault(r => string.Equals(r.Purpose, "chat", StringComparison.OrdinalIgnoreCase));
        if (chatRoute is not null)
        {
            foreach (var c in chatRoute.Candidates)
            {
                if (!string.IsNullOrWhiteSpace(c.Model))
                    chatRoutePositions.TryAdd((c.ProviderInstanceId, c.Model), c.Position);
            }
        }

        var scopedRoute = !string.IsNullOrWhiteSpace(strategy.RoutePurpose)
            ? catalog.Routes.FirstOrDefault(r => string.Equals(r.Purpose, strategy.RoutePurpose.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;
        if (scopedRoute is not null)
        {
            foreach (var c in scopedRoute.Candidates.OrderBy(c => c.Position))
            {
                if (!providerById.TryGetValue(c.ProviderInstanceId, out var prov)) continue;
                var model = string.IsNullOrWhiteSpace(c.Model) ? prov.DefaultModel ?? prov.Driver : c.Model.Trim();
                routeCandidates.Add((prov, model, scopedRoute.Id, c.Position));
            }
        }
        else
        {
            var seen = new HashSet<(Guid ProviderId, string Model)>();
            foreach (var prov in catalog.Providers)
            {
                var models = new List<string>();
                if (!string.IsNullOrWhiteSpace(prov.DefaultModel)) models.Add(prov.DefaultModel.Trim());
                foreach (var m in prov.Models)
                {
                    if (!string.IsNullOrWhiteSpace(m)) models.Add(m.Trim());
                }
                if (models.Count == 0)
                {
                    foreach (var m in prov.ModelProfiles.Keys)
                    {
                        if (!string.IsNullOrWhiteSpace(m)) models.Add(m.Trim());
                    }
                }
                if (models.Count == 0 && ChatProtocols.IsModelChosenByHarness(prov.Protocol))
                    models.Add(prov.Driver);

                foreach (var model in models)
                {
                    if (seen.Add((prov.Id, model)))
                    {
                        var pos = chatRoutePositions.TryGetValue((prov.Id, model), out var p) ? p : (int?)null;
                        routeCandidates.Add((prov, model, null, pos));
                    }
                }
            }
        }

        var requiredSpecialties = strategy.RequiredSpecialties ?? [];
        var maxCostRank = CostTierRank(strategy.MaxCostTier);

        var scored = new List<(
            SanitizedProviderCatalogItemDto Provider,
            string Model,
            Guid VersionId,
            Guid? RouteId,
            int Usable,
            int Healthy,
            int FullSpecialty,
            int SpecialtyMatches,
            int TierScore,
            int CostScore,
            int RouteBonus,
            decimal PricePenalty,
            int RoutePosition)>();

        foreach (var entry in routeCandidates)
        {
            if (!providerVersionById.TryGetValue(entry.Provider.Id, out var versionId)) continue;
            entry.Provider.ModelProfiles.TryGetValue(entry.Model, out var profile);

            if (maxCostRank is { } maxRank && CostTierRank(profile?.CostTier) is { } actualCost && actualCost > maxRank)
                continue;

            var harnessOwned = ChatProtocols.IsModelChosenByHarness(entry.Provider.Protocol);
            var usable = (entry.Provider.HasApiKey || harnessOwned) ? 1 : 0;
            var healthy = coolingSet.Contains((entry.Provider.Id, entry.Model)) ? 0 : 1;

            var specialtyMatches = 0;
            if (requiredSpecialties.Count > 0 && profile is not null)
            {
                foreach (var req in requiredSpecialties)
                {
                    if (profile.Specialties.Any(s => string.Equals(s, req, StringComparison.OrdinalIgnoreCase)))
                        specialtyMatches++;
                }
            }
            var fullSpecialty = (requiredSpecialties.Count == 0 || specialtyMatches == requiredSpecialties.Count) ? 1 : 0;

            var tierScore = ScoreTierMatch(strategy.PreferredTier, profile?.Tier);
            var costScore = maxCostRank is not null
                ? (profile?.CostTier is not null ? 2 : 1)
                : 0;
            var routeBonus = entry.RoutePos is not null ? 1 : 0;
            var pricePenalty = (profile?.InputPricePer1M ?? 0m) + (profile?.OutputPricePer1M ?? 0m);

            scored.Add((
                entry.Provider,
                entry.Model,
                versionId,
                entry.RouteId,
                usable,
                healthy,
                fullSpecialty,
                specialtyMatches,
                tierScore,
                costScore,
                routeBonus,
                pricePenalty,
                entry.RoutePos ?? int.MaxValue));
        }

        if (scored.Count == 0)
            throw new InvalidDataException("No enabled model candidate satisfies the auto model_strategy constraints.");

        var ordered = scored
            .OrderByDescending(x => x.Usable)
            .ThenByDescending(x => x.Healthy)
            .ThenByDescending(x => x.FullSpecialty)
            .ThenByDescending(x => x.SpecialtyMatches)
            .ThenByDescending(x => x.TierScore)
            .ThenByDescending(x => x.CostScore)
            .ThenByDescending(x => x.RouteBonus)
            .ThenBy(x => x.PricePenalty)
            .ThenBy(x => x.RoutePosition)
            .ThenBy(x => x.Provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Provider.Id)
            .ToArray();

        var candidates = new List<FrozenModelCandidate>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            var item = ordered[i];
            candidates.Add(new FrozenModelCandidate(
                i,
                item.Provider.Id,
                item.VersionId,
                item.Model,
                item.Provider.Protocol,
                item.RouteId,
                null));
        }
        return new FrozenModelPlan(ModelStrategyKinds.Auto, source, candidates);
    }

    private static int? CostTierRank(string? costTier) => costTier?.Trim().ToLowerInvariant() switch
    {
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        _ => null
    };

    private static int ScoreTierMatch(string? preferredTier, string? actualTier)
    {
        if (string.IsNullOrWhiteSpace(preferredTier) || string.IsNullOrWhiteSpace(actualTier)) return 0;
        var pref = TierRank(preferredTier);
        var act = TierRank(actualTier);
        if (pref is null || act is null) return 0;
        var diff = Math.Abs(pref.Value - act.Value);
        return diff switch
        {
            0 => 2,
            1 => 1,
            _ => 0
        };
    }

    private static int? TierRank(string tier) => tier.Trim().ToLowerInvariant() switch
    {
        "fast" => 1,
        "balanced" => 2,
        "deep" => 3,
        _ => null
    };

    private async Task<FrozenModelPlan> FreezeRouteAsync(string purpose, PreviewBatch? batch, CancellationToken ct)
    {
        if (batch is not null)
        {
            var catalog = await GetModelCatalogAsync(batch, ct).ConfigureAwait(false);
            if (!catalog.RoutesByPurpose.TryGetValue(purpose, out var cachedRoute))
                throw new InvalidDataException($"Model route '{purpose}' is not configured.");
            if (!catalog.RouteVersionsById.TryGetValue(cachedRoute.CurrentVersionId, out var cachedVersion))
                throw new InvalidDataException($"Model route '{purpose}' has no current version.");
            var cachedRows = catalog.RouteCandidatesByVersionId[cachedVersion.Id].OrderBy(x => x.Position).ToList();
            if (cachedRows.Count == 0) throw new InvalidDataException($"Model route '{purpose}' has no candidates.");
            var cachedCandidates = new List<FrozenModelCandidate>(cachedRows.Count);
            foreach (var row in cachedRows)
            {
                if (!catalog.ProvidersById.TryGetValue(row.ProviderInstanceId, out var prov))
                    throw new InvalidDataException($"Route '{purpose}' references missing provider '{row.ProviderInstanceId}'.");
                if (!catalog.ProviderVersionsById.TryGetValue(prov.CurrentVersionId, out var provVersion))
                    throw new InvalidDataException($"Provider '{prov.Id}' has no current version.");
                cachedCandidates.Add(new FrozenModelCandidate(row.Position, prov.Id, provVersion.Id, row.Model, await ReadProtocolAsync(prov, provVersion, batch, ct).ConfigureAwait(false), cachedRoute.Id, cachedVersion.Id));
            }
            return new FrozenModelPlan(ModelStrategyKinds.Route, "route", cachedCandidates);
        }

        var scope = _tenant.Current;
        await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
        var route = await db.Routes.AsNoTracking().SingleOrDefaultAsync(x => x.Purpose == purpose && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.DeletedAt == null, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Model route '{purpose}' is not configured.");
        var version = await db.RouteVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == route.CurrentVersionId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Model route '{purpose}' has no current version.");
        var rows = await db.RouteCandidates.AsNoTracking().Where(x => x.RouteVersionId == version.Id).OrderBy(x => x.Position).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0) throw new InvalidDataException($"Model route '{purpose}' has no candidates.");
        var candidates = new List<FrozenModelCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ProviderInstanceId
                && x.TenantId == scope.TenantId && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId), ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Route '{purpose}' references missing provider '{row.ProviderInstanceId}'.");
            var providerVersion = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider.CurrentVersionId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Provider '{provider.Id}' has no current version.");
            candidates.Add(new FrozenModelCandidate(row.Position, provider.Id, providerVersion.Id, row.Model, await ReadProtocolAsync(provider, providerVersion, null, ct).ConfigureAwait(false), route.Id, version.Id));
        }
        return new FrozenModelPlan(ModelStrategyKinds.Route, "route", candidates);
    }

    private async Task<ChatResolution> BuildResolutionAsync(FrozenModelCandidate candidate, string source, PreviewBatch? batch, CancellationToken ct)
    {
        if (ChatProtocols.Normalize(candidate.Protocol) == ChatProtocols.PeerProxy)
        {
            var binding = TryResolveWorkerProxyBinding(candidate.ProviderInstanceId);
            if (binding is null
                || string.IsNullOrWhiteSpace(binding.ControllerDeviceId)
                || string.IsNullOrWhiteSpace(binding.ProxySessionToken))
            {
                return Unavailable(candidate, source, PeerErrorCodes.PolicyDenied, "Peer provider proxy context is missing, expired, or revoked.");
            }

            var hostKey = PeerProviderProxyHostRegistry.ResolveHostPartitionKey(_secrets);
            var proxyModel = !string.IsNullOrWhiteSpace(candidate.Model)
                ? candidate.Model!.Trim()
                : !string.IsNullOrWhiteSpace(binding.ModelHint)
                    ? binding.ModelHint!.Trim()
                    : ChatProtocols.PeerProxy;
            var gatewayUrl = PeerProxyModelProvider.ResolveEnvironmentGatewayUrl(
                binding.GatewayLoopbackUrl ?? PeerProviderProxyHostRegistry.GetHostGatewayLoopbackUrl(hostKey));

            // Security invariant: ApiKey is strictly null; controller credentials NEVER enter worker resolution, memory, SQLite, or SecretStore.
            return new ChatResolution
            {
                IsAvailable = true,
                BaseUrl = gatewayUrl,
                ApiKey = null,
                Model = proxyModel,
                ModelId = $"peer/{proxyModel}",
                Protocol = ChatProtocols.PeerProxy,
                ProxyControllerDeviceId = binding.ControllerDeviceId.Trim(),
                ProxySessionToken = binding.ProxySessionToken.Trim(),
                ProviderInstanceId = candidate.ProviderInstanceId,
                ProviderVersionId = Guid.Empty,
                RouteId = candidate.RouteId,
                RouteVersionId = candidate.RouteVersionId,
                CandidatePosition = candidate.Position,
                StrategySource = source,
                RuntimeBadge = FormatRuntimeBadge(ChatProtocols.PeerProxy, binding.ControllerDeviceId, proxyModel)
            };
        }

        var scope = _tenant.Current;
        ModelProviderRecord? provider = null;
        ModelProviderVersionRecord? version = null;
        if (batch is not null)
        {
            var catalog = await GetModelCatalogAsync(batch, ct).ConfigureAwait(false);
            catalog.ProvidersById.TryGetValue(candidate.ProviderInstanceId, out provider);
            if (provider is null)
            {
                if (!batch.ExtraProviders.TryGetValue(candidate.ProviderInstanceId, out provider))
                {
                    await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
                    provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.ProviderInstanceId
                        && x.TenantId == scope.TenantId && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId), ct).ConfigureAwait(false);
                    if (provider is not null) batch.ExtraProviders[provider.Id] = provider;
                }
            }
            if (provider is null || provider.DeletedAt is not null || !provider.Enabled)
                return Unavailable(candidate, source, "provider_disabled", "Provider is missing, deleted, or disabled.");
            if (!catalog.ProviderVersionsById.TryGetValue(candidate.ProviderVersionId, out version) || version.ProviderId != provider.Id)
            {
                if (!batch.ExtraProviderVersions.TryGetValue(candidate.ProviderVersionId, out version) || version.ProviderId != provider.Id)
                {
                    await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
                    version = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.ProviderVersionId && x.ProviderId == provider.Id, ct).ConfigureAwait(false);
                    if (version is not null) batch.ExtraProviderVersions[version.Id] = version;
                }
            }
        }
        else
        {
            await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
            provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.ProviderInstanceId
                && x.TenantId == scope.TenantId && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId), ct).ConfigureAwait(false);
            if (provider is null || provider.DeletedAt is not null || !provider.Enabled)
                return Unavailable(candidate, source, "provider_disabled", "Provider is missing, deleted, or disabled.");
            version = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.ProviderVersionId && x.ProviderId == provider.Id, ct).ConfigureAwait(false);
        }
        if (version is null) return Unavailable(candidate, source, "provider_version_missing", "Frozen provider version is missing.");

        try
        {
            JsonDocument document;
            bool disposeDocument;
            if (batch is not null)
            {
                if (!batch.ProviderDocuments.TryGetValue(version.Id, out var cachedDoc) || cachedDoc is null)
                {
                    await using var stream = await _content.OpenReadAsync(new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"), ct).ConfigureAwait(false);
                    cachedDoc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    batch.ProviderDocuments[version.Id] = cachedDoc;
                }
                document = cachedDoc;
                disposeDocument = false;
            }
            else
            {
                await using var stream = await _content.OpenReadAsync(new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"), ct).ConfigureAwait(false);
                document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                disposeDocument = true;
            }
            try
            {
                var root = document.RootElement;
                string? Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var protocol = candidate.Protocol;
                var processBacked = ChatProtocols.IsProcessTransport(protocol);
                var harnessOwned = processBacked || protocol == ChatProtocols.OpencodeServe;
                var model = candidate.Model ?? Text("model");
                if (string.IsNullOrWhiteSpace(model) && !harnessOwned) return Unavailable(candidate, source, "model_missing", "Model id is missing.");
                model ??= provider.Driver;
                string? baseUrl;
                if (processBacked)
                {
                    // A process-backed harness is reached by spawning binary_path and talking to its
                    // stdio. Requiring an endpoint here is what silently killed every stdio ACP provider:
                    // the route failed with `endpoint_missing` and named nothing that was actually wrong.
                    if (string.IsNullOrWhiteSpace(Text("binary_path")))
                        return Unavailable(candidate, source, "binary_missing", $"'{protocol}' runs as a local process and needs binary_path.");
                    baseUrl = null;
                }
                else
                {
                    baseUrl = protocol == ChatProtocols.OpencodeServe ? Text("server_url") : Text("base_url");
                    if (string.IsNullOrWhiteSpace(baseUrl))
                        return Unavailable(candidate, source, "endpoint_missing", protocol == ChatProtocols.OpencodeServe ? "CLI runtime server_url is missing." : "Provider endpoint is missing.");
                }
                string? apiKey = null;
                if (!harnessOwned)
                {
                    if (string.IsNullOrWhiteSpace(provider.SecretReference)) return Unavailable(candidate, source, "credential_missing", "Provider credential is not configured.");
                    if (batch is not null)
                    {
                        if (!batch.Secrets.TryGetValue(provider.SecretReference, out apiKey))
                        {
                            apiKey = await _secrets.GetAsync(provider.SecretReference, ct).ConfigureAwait(false);
                            batch.Secrets[provider.SecretReference] = apiKey;
                        }
                    }
                    else
                    {
                        apiKey = await _secrets.GetAsync(provider.SecretReference, ct).ConfigureAwait(false);
                    }
                    if (string.IsNullOrWhiteSpace(apiKey)) return Unavailable(candidate, source, "credential_missing", "Provider credential is not available.");
                }
                var imageInput = ModelProfile.ResolveImageInput(root, protocol, model);
                return new ChatResolution
                {
                    IsAvailable = true, BaseUrl = baseUrl, ApiKey = apiKey, Model = model,
                    Parameters = ModelParameters.ForModel(root, model),
                    SupportsImageInput = imageInput.Enabled,
                    MaxImageBytes = imageInput.MaxImageBytes,
                    MaxImagesPerTurn = imageInput.MaxImagesPerTurn,
                    ModelId = $"{provider.Driver}/{model}", Protocol = protocol,
                    ServerUrl = Text("server_url"), BinaryPath = Text("binary_path"), LaunchArgs = Text("launch_args"), HomePath = Text("home_path"),
                    ProviderInstanceId = provider.Id, ProviderVersionId = version.Id,
                    RouteId = candidate.RouteId, RouteVersionId = candidate.RouteVersionId,
                    CandidatePosition = candidate.Position, StrategySource = source,
                    RuntimeBadge = FormatRuntimeBadge(protocol, provider.Driver, model)
                };
            }
            finally
            {
                if (disposeDocument) document.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(candidate, source, "provider_configuration_invalid", ex.Message);
        }
    }

    internal static string FormatRuntimeBadge(string? protocol, string? driver, string? model)
    {
        var normalized = ChatProtocols.Normalize(protocol);
        var harnessLabel = HarnessCatalog.Find(driver)?.DisplayName
            ?? (!string.IsNullOrWhiteSpace(driver) ? driver.Trim() : model?.Trim() ?? "Harness");
        var apiLabel = !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : (!string.IsNullOrWhiteSpace(driver) ? driver.Trim() : "Model");

        return normalized switch
        {
            ChatProtocols.Acp => $"ACP · {harnessLabel}",
            ChatProtocols.HeadlessCli => $"CLI · {harnessLabel}",
            ChatProtocols.Tui => $"TUI · {harnessLabel}",
            ChatProtocols.OpencodeServe => $"HTTP · {harnessLabel}",
            ChatProtocols.PeerProxy => $"Proxy · {harnessLabel}",
            _ => $"API · {apiLabel}"
        };
    }

    private static ChatResolution Unavailable(FrozenModelCandidate candidate, string source, string code, string message) => new()
    {
        IsAvailable = false, Error = $"{code}: {message}", Model = candidate.Model, Protocol = candidate.Protocol,
        ProviderInstanceId = candidate.ProviderInstanceId, ProviderVersionId = candidate.ProviderVersionId,
        RouteId = candidate.RouteId, RouteVersionId = candidate.RouteVersionId,
        CandidatePosition = candidate.Position, StrategySource = source
    };

    private static ChatResolution WithSource(ChatResolution value, string source) =>
        value with { StrategySource = source };

    private async Task<string> ReadProtocolAsync(ModelProviderRecord provider, ModelProviderVersionRecord version, PreviewBatch? batch, CancellationToken ct)
    {
        JsonDocument document;
        bool disposeDocument;
        if (batch is not null)
        {
            if (!batch.ProviderDocuments.TryGetValue(version.Id, out var cachedDoc) || cachedDoc is null)
            {
                await using var stream = await _content.OpenReadAsync(new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"), ct).ConfigureAwait(false);
                cachedDoc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                batch.ProviderDocuments[version.Id] = cachedDoc;
            }
            document = cachedDoc;
            disposeDocument = false;
        }
        else
        {
            await using var stream = await _content.OpenReadAsync(new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"), ct).ConfigureAwait(false);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            disposeDocument = true;
        }
        try
        {
            string? Text(string key) => document.RootElement.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
            return HarnessCatalog.ResolveProtocol(Text("protocol"), provider.Driver, Text("channel"));
        }
        finally
        {
            if (disposeDocument) document.Dispose();
        }
    }

    private static IReadOnlyDictionary<string, ModelStrategyDto?> ReadModeNodeStrategies(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return new Dictionary<string, ModelStrategyDto?>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(snapshot);
        if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return new Dictionary<string, ModelStrategyDto?>(StringComparer.Ordinal);
        var map = new Dictionary<string, ModelStrategyDto?>(StringComparer.Ordinal);
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("node_key", out var key) || key.GetString() is not { } nodeKey) continue;
            map[nodeKey] = node.TryGetProperty("model_strategy_override", out var strategy) && strategy.ValueKind == JsonValueKind.Object
                ? ModelStrategyJson.Parse(strategy)
                : null;
        }
        return map;
    }

    private static ModelStrategyDto? ReadModeNodeStrategy(string? snapshot, string nodeKey)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return null;
        using var document = JsonDocument.Parse(snapshot);
        if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return null;
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("node_key", out var key) || key.GetString() != nodeKey) continue;
            return node.TryGetProperty("model_strategy_override", out var strategy) && strategy.ValueKind == JsonValueKind.Object
                ? ModelStrategyJson.Parse(strategy)
                : null;
        }
        return null;
    }

    private static ModelStrategyDto ReadAgentStrategy(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        return document.RootElement.TryGetProperty("model_strategy", out var strategy)
            ? ModelStrategyJson.Parse(strategy)
            : new ModelStrategyDto();
    }

    private static ModelResolutionCandidatePreviewDto ToPreview(ChatResolution value) => new()
    {
        Position = value.CandidatePosition,
        ProviderInstanceId = value.ProviderInstanceId ?? Guid.Empty,
        ProviderVersionId = value.ProviderVersionId,
        RouteId = value.RouteId,
        RouteVersionId = value.RouteVersionId,
        Model = value.Model,
        Protocol = value.Protocol,
        Available = value.IsAvailable,
        UnavailableReason = value.Error
    };
}
