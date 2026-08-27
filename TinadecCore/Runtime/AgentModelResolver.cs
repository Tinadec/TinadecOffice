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
    private readonly IDbContextFactory<ModelControlDbContext> _models;
    private readonly IDbContextFactory<LifecycleDbContext> _lifecycle;
    private readonly IDbContextFactory<AgentControlDbContext> _instances;
    private readonly ILifecycleManager _lifecycleManager;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;
    private readonly ITenantContextAccessor _tenant;
    private readonly ISessionLocator _sessions;

    public AgentModelResolver(
        IDbContextFactory<AgentConfigurationDbContext> agents,
        IDbContextFactory<ModelControlDbContext> models,
        IDbContextFactory<LifecycleDbContext> lifecycle,
        IDbContextFactory<AgentControlDbContext> instances,
        ILifecycleManager lifecycleManager,
        IContentStore content,
        ISecretStore secrets,
        ITenantContextAccessor tenant,
        ISessionLocator sessions)
    {
        _agents = agents;
        _models = models;
        _lifecycle = lifecycle;
        _instances = instances;
        _lifecycleManager = lifecycleManager;
        _content = content;
        _secrets = secrets;
        _tenant = tenant;
        _sessions = sessions;
    }

    public async Task<ModelResolutionPreviewDto> PreviewAsync(ModelResolutionPreviewRequestDto request, CancellationToken cancellationToken = default)
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

        if (request.ModeVersionId is { } modeVersionId && !string.IsNullOrWhiteSpace(request.NodeKey))
        {
            await using var db = await _agents.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var version = await db.ModeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == modeVersionId
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
            var strategy = version is null ? null : ReadModeNodeStrategy(version.SnapshotJson, request.NodeKey!);
            Consider("mode_node_override", strategy);
        }

        if (request.AgentVersionId is { } agentVersionId)
        {
            await using var db = await _agents.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var version = await db.AgentVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentVersionId
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
            Consider("agent_version", version is null ? null : ReadAgentStrategy(version.SnapshotJson));
        }
        else if (request.AgentDefinitionId is { } agentDefinitionId)
        {
            await using var db = await _agents.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var definition = await db.AgentDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentDefinitionId
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
            Consider("agent_default", definition is null ? null : ModelStrategyJson.Parse(definition.ModelStrategyJson));
        }

        selected ??= new ModelStrategyDto { Kind = ModelStrategyKinds.Inherit };
        if (chain.Count == 0) chain.Add(new ModelResolutionStepDto { Source = "implicit_inherit", Strategy = selected, Selected = true });
        source = source.Length == 0 ? "implicit_inherit" : source;

        var plan = await FreezeStrategyAsync(selected, source, request.ParentInstanceId is not null, cancellationToken).ConfigureAwait(false);
        var resolutions = await ResolveInvocationCandidatesAsync(plan, request.ParentInstanceId, cancellationToken).ConfigureAwait(false);
        var candidates = resolutions.Select(ToPreview).ToArray();
        return new ModelResolutionPreviewDto
        {
            StrategySource = source,
            Chain = chain,
            Candidates = candidates,
            ExpectedSelection = candidates.FirstOrDefault(x => x.Available)
        };
    }

    public async Task<FrozenModelPlan> FreezeAsync(AgentModelFreezeRequest request, CancellationToken cancellationToken = default)
    {
        var strategy = ModelStrategyJson.Parse(request.StrategyJson);
        var source = request.StrategySource;
        if (request.IsMeetingRoot)
        {
            var meetingOverride = request.MeetingModelOverride;
            if (meetingOverride is not null)
            {
                strategy = new ModelStrategyDto { Kind = ModelStrategyKinds.Fixed, ProviderInstanceId = meetingOverride.ProviderInstanceId, Model = meetingOverride.Model };
                source = "turn_override";
            }
            else
            {
                var session = await _sessions.FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
                if (session?.MeetingModelOverride is { } sessionOverride)
                {
                    strategy = new ModelStrategyDto { Kind = ModelStrategyKinds.Fixed, ProviderInstanceId = sessionOverride.ProviderInstanceId, Model = sessionOverride.Model };
                    source = "session_override";
                }
            }
        }
        return await FreezeStrategyAsync(strategy, source, !request.IsMeetingRoot && strategy.Kind == ModelStrategyKinds.Inherit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(FrozenModelPlan plan, Guid? parentInstanceId, CancellationToken cancellationToken = default)
        => await ResolveInvocationCandidatesAsync(plan, parentInstanceId, new HashSet<Guid>(), cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(
        FrozenModelPlan plan,
        Guid? parentInstanceId,
        HashSet<Guid> visitedParents,
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
                return [await BuildResolutionAsync(inherited, "parent_actual", cancellationToken).ConfigureAwait(false)];
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
            var inheritedCandidates = await ResolveInvocationCandidatesAsync(parentPlan, parent.ParentInstanceId, visitedParents, cancellationToken).ConfigureAwait(false);
            return inheritedCandidates.Select(item => WithSource(item, "parent_frozen_plan")).ToArray();
        }

        var result = new List<ChatResolution>(plan.Candidates.Count);
        foreach (var candidate in plan.Candidates)
            result.Add(await BuildResolutionAsync(candidate, plan.StrategySource, cancellationToken).ConfigureAwait(false));
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
    }

    private async Task<FrozenModelPlan> FreezeStrategyAsync(ModelStrategyDto strategy, string source, bool inheritFromParent, CancellationToken ct)
    {
        if (strategy.Kind == ModelStrategyKinds.Inherit)
        {
            var routePlan = await FreezeRouteAsync("chat", ct).ConfigureAwait(false);
            return routePlan with { StrategyKind = ModelStrategyKinds.Inherit, StrategySource = source, InheritFromParent = inheritFromParent };
        }
        if (strategy.Kind == ModelStrategyKinds.Route)
        {
            var route = await FreezeRouteAsync(strategy.RoutePurpose!, ct).ConfigureAwait(false);
            return route with { StrategySource = source };
        }
        if (strategy.Kind == ModelStrategyKinds.Fixed && strategy.ProviderInstanceId is { } providerId)
        {
            var scope = _tenant.Current;
            await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
            var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == providerId
                && x.TenantId == scope.TenantId && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId)
                && x.DeletedAt == null, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Provider '{providerId}' was not found.");
            var providerVersion = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider.CurrentVersionId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Provider '{providerId}' has no current version.");
            var protocol = await ReadProtocolAsync(provider, providerVersion, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(strategy.Model) && protocol is not (ChatProtocols.Acp or ChatProtocols.OpencodeServe))
                throw new InvalidDataException("fixed model strategy requires model for non-runtime providers.");
            return new FrozenModelPlan(ModelStrategyKinds.Fixed, source,
                [new FrozenModelCandidate(0, provider.Id, providerVersion.Id, strategy.Model, protocol)]);
        }
        throw new InvalidDataException("Unsupported model strategy.");
    }

    private async Task<FrozenModelPlan> FreezeRouteAsync(string purpose, CancellationToken ct)
    {
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
            candidates.Add(new FrozenModelCandidate(row.Position, provider.Id, providerVersion.Id, row.Model, await ReadProtocolAsync(provider, providerVersion, ct).ConfigureAwait(false), route.Id, version.Id));
        }
        return new FrozenModelPlan(ModelStrategyKinds.Route, "route", candidates);
    }

    private async Task<ChatResolution> BuildResolutionAsync(FrozenModelCandidate candidate, string source, CancellationToken ct)
    {
        var scope = _tenant.Current;
        await using var db = await _models.CreateDbContextAsync(ct).ConfigureAwait(false);
        var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.ProviderInstanceId
            && x.TenantId == scope.TenantId && (x.WorkspaceId == null || x.WorkspaceId == scope.WorkspaceId), ct).ConfigureAwait(false);
        if (provider is null || provider.DeletedAt is not null || !provider.Enabled)
            return Unavailable(candidate, source, "provider_disabled", "Provider is missing, deleted, or disabled.");
        var version = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.ProviderVersionId && x.ProviderId == provider.Id, ct).ConfigureAwait(false);
        if (version is null) return Unavailable(candidate, source, "provider_version_missing", "Frozen provider version is missing.");

        try
        {
            await using var stream = await _content.OpenReadAsync(new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"), ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            string? Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var protocol = candidate.Protocol;
            var runtimeOwned = protocol is ChatProtocols.Acp or ChatProtocols.OpencodeServe;
            var model = candidate.Model ?? Text("model");
            if (string.IsNullOrWhiteSpace(model) && !runtimeOwned) return Unavailable(candidate, source, "model_missing", "Model id is missing.");
            model ??= provider.Driver;
            var baseUrl = runtimeOwned ? Text("server_url") : Text("base_url");
            if (string.IsNullOrWhiteSpace(baseUrl)) return Unavailable(candidate, source, "endpoint_missing", "Provider endpoint is missing.");
            string? apiKey = null;
            if (!runtimeOwned)
            {
                if (string.IsNullOrWhiteSpace(provider.SecretReference)) return Unavailable(candidate, source, "credential_missing", "Provider credential is not configured.");
                apiKey = await _secrets.GetAsync(provider.SecretReference, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(apiKey)) return Unavailable(candidate, source, "credential_missing", "Provider credential is not available.");
            }
            return new ChatResolution
            {
                IsAvailable = true, BaseUrl = baseUrl, ApiKey = apiKey, Model = model,
                ModelId = $"{provider.Driver}/{model}", Protocol = protocol,
                ServerUrl = Text("server_url"), BinaryPath = Text("binary_path"), LaunchArgs = Text("launch_args"), HomePath = Text("home_path"),
                ProviderInstanceId = provider.Id, ProviderVersionId = version.Id,
                RouteId = candidate.RouteId, RouteVersionId = candidate.RouteVersionId,
                CandidatePosition = candidate.Position, StrategySource = source
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(candidate, source, "provider_configuration_invalid", ex.Message);
        }
    }

    private static ChatResolution Unavailable(FrozenModelCandidate candidate, string source, string code, string message) => new()
    {
        IsAvailable = false, Error = $"{code}: {message}", Model = candidate.Model, Protocol = candidate.Protocol,
        ProviderInstanceId = candidate.ProviderInstanceId, ProviderVersionId = candidate.ProviderVersionId,
        RouteId = candidate.RouteId, RouteVersionId = candidate.RouteVersionId,
        CandidatePosition = candidate.Position, StrategySource = source
    };

    private static ChatResolution WithSource(ChatResolution value, string source) => new()
    {
        IsAvailable = value.IsAvailable,
        BaseUrl = value.BaseUrl,
        Model = value.Model,
        ApiKey = value.ApiKey,
        ModelId = value.ModelId,
        Protocol = value.Protocol,
        ServerUrl = value.ServerUrl,
        Token = value.Token,
        BinaryPath = value.BinaryPath,
        LaunchArgs = value.LaunchArgs,
        HomePath = value.HomePath,
        Error = value.Error,
        ProviderInstanceId = value.ProviderInstanceId,
        ProviderVersionId = value.ProviderVersionId,
        RouteId = value.RouteId,
        RouteVersionId = value.RouteVersionId,
        CandidatePosition = value.CandidatePosition,
        StrategySource = source
    };

    private async Task<string> ReadProtocolAsync(ModelProviderRecord provider, ModelProviderVersionRecord version, CancellationToken ct)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"), ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var configured = document.RootElement.TryGetProperty("protocol", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return ChatProtocols.Normalize(configured ?? ChatProtocols.InferFromDriver(provider.Driver));
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
