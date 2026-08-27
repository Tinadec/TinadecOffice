using System.Text;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Models;

namespace TinadecCore.Api.Endpoints;

public static class ModelAgentControlEndpoints
{
    public static WebApplication MapModelAgentControlEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/model-resolution/preview", Preview);
        app.MapGet("/api/v1/model-references", References);
        app.MapGet("/api/v1/model-invocations", Invocations);
        return app;
    }

    private static async Task<IResult> Preview(
        ModelResolutionPreviewRequestDto request,
        IAgentModelResolver resolver,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await resolver.PreviewAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            return Results.BadRequest(new { code = "invalid_model_strategy", message = exception.Message });
        }
    }

    private static async Task<IResult> References(
        HttpRequest request,
        IDbContextFactory<ModelControlDbContext> modelFactory,
        IDbContextFactory<AgentConfigurationDbContext> agentFactory,
        IDbContextFactory<MemoryDbContext> memoryFactory,
        IDbContextFactory<LifecycleDbContext> lifecycleFactory,
        ITenantContextAccessor tenant,
        CancellationToken cancellationToken)
    {
        Guid? providerId = null;
        var providerText = request.Query["provider_instance_id"].ToString();
        if (!string.IsNullOrWhiteSpace(providerText))
        {
            if (!Guid.TryParse(providerText, out var parsed) || parsed == Guid.Empty)
                return Results.BadRequest(new { code = "invalid_provider_instance_id", message = "provider_instance_id must be a UUID." });
            providerId = parsed;
        }
        var model = request.Query["model"].ToString();
        model = string.IsNullOrWhiteSpace(model) ? null : model;
        var scope = tenant.Current;
        var result = new List<ModelReferenceDto>();

        await using (var models = await modelFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var routes = await models.Routes.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.DeletedAt == null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var routeVersions = routes.Select(x => x.CurrentVersionId).ToArray();
            var candidates = await models.RouteCandidates.AsNoTracking()
                .Where(x => routeVersions.Contains(x.RouteVersionId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var candidate in candidates.Where(x => Matches(x.ProviderInstanceId, x.Model, providerId, model)))
            {
                var route = routes.Single(x => x.CurrentVersionId == candidate.RouteVersionId);
                result.Add(new ModelReferenceDto
                {
                    ReferenceKind = "route", ReferenceId = route.Id, ReferenceKey = route.Purpose,
                    ProviderInstanceId = candidate.ProviderInstanceId, Model = candidate.Model,
                    Detail = $"candidate:{candidate.Position}"
                });
            }
        }

        await using (var agents = await agentFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var definitions = await agents.AgentDefinitions.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var definition in definitions)
            {
                var strategy = TryParse(definition.ModelStrategyJson);
                if (strategy?.Kind == ModelStrategyKinds.Fixed && strategy.ProviderInstanceId is { } fixedProvider
                    && Matches(fixedProvider, strategy.Model, providerId, model))
                {
                    result.Add(new ModelReferenceDto
                    {
                        ReferenceKind = "agent_default", ReferenceId = definition.Id, ReferenceKey = definition.Slug,
                        ProviderInstanceId = fixedProvider, Model = strategy.Model, Detail = definition.Status
                    });
                }
            }

            var modes = await agents.AgentModes.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
                .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
            var nodes = await agents.ModeNodes.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.ModelStrategyOverrideJson != null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var node in nodes)
            {
                var strategy = TryParse(node.ModelStrategyOverrideJson);
                if (strategy?.Kind == ModelStrategyKinds.Fixed && strategy.ProviderInstanceId is { } fixedProvider
                    && Matches(fixedProvider, strategy.Model, providerId, model))
                {
                    result.Add(new ModelReferenceDto
                    {
                        ReferenceKind = "mode_override", ReferenceId = node.ModeId,
                        ReferenceKey = $"{modes.GetValueOrDefault(node.ModeId)?.Slug ?? "missing-mode"}:{node.NodeKey}",
                        ProviderInstanceId = fixedProvider, Model = strategy.Model, Detail = node.Status
                    });
                }
            }
        }

        await using (var memory = await memoryFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var sessions = await memory.Sessions.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                    && x.MeetingModelOverrideProviderInstanceId != null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var session in sessions.Where(x => Matches(x.MeetingModelOverrideProviderInstanceId!.Value, x.MeetingModelOverrideModel, providerId, model)))
            {
                result.Add(new ModelReferenceDto
                {
                    ReferenceKind = "session_override", ReferenceId = session.Id, ReferenceKey = session.Title,
                    ProviderInstanceId = session.MeetingModelOverrideProviderInstanceId!.Value,
                    Model = session.MeetingModelOverrideModel, Detail = session.Status
                });
            }
        }

        await using (var lifecycle = await lifecycleFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var query = lifecycle.ModelInvocations.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "succeeded");
            if (providerId is { } selectedProvider) query = query.Where(x => x.ProviderInstanceId == selectedProvider);
            if (model is not null) query = query.Where(x => x.Model == model);
            var actual = (await query.ToListAsync(cancellationToken).ConfigureAwait(false))
                .OrderByDescending(x => x.CompletedAt)
                .ThenByDescending(x => x.Id)
                .Take(50)
                .ToList();
            result.AddRange(actual.Select(x => new ModelReferenceDto
            {
                ReferenceKind = "recent_invocation", ReferenceId = x.Id, ReferenceKey = x.RunId.ToString(),
                ProviderInstanceId = x.ProviderInstanceId, Model = x.Model, Detail = x.AgentDefinitionId.ToString(), LastUsedAt = x.CompletedAt
            }));
        }

        return Results.Ok(result.OrderBy(x => x.ReferenceKind, StringComparer.Ordinal).ThenBy(x => x.ReferenceKey, StringComparer.Ordinal).ToArray());
    }

    private static async Task<IResult> Invocations(
        HttpRequest request,
        IDbContextFactory<LifecycleDbContext> factory,
        ITenantContextAccessor tenant,
        CancellationToken cancellationToken)
    {
        if (!TryGuid(request, "run_id", out var runId, out var error)
            || !TryGuid(request, "agent_id", out var agentId, out error)
            || !TryGuid(request, "mode_version_id", out var modeVersionId, out error)
            || !TryGuid(request, "provider_instance_id", out var providerId, out error))
            return Results.BadRequest(new { code = "invalid_query", message = error });
        if (!TryDate(request, "from", out var from, out error) || !TryDate(request, "to", out var to, out error))
            return Results.BadRequest(new { code = "invalid_query", message = error });
        var status = request.Query["status"].ToString();
        if (!string.IsNullOrWhiteSpace(status) && status is not ("started" or "succeeded" or "failed" or "cancelled"))
            return Results.BadRequest(new { code = "invalid_query", message = "status must be started|succeeded|failed|cancelled." });
        var model = request.Query["model"].ToString();
        var limit = int.TryParse(request.Query["limit"], out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 200) : 50;
        DateTimeOffset? cursor = null;
        var cursorText = request.Query["cursor"].ToString();
        if (!string.IsNullOrWhiteSpace(cursorText))
        {
            try { cursor = new DateTimeOffset(long.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursorText))), TimeSpan.Zero); }
            catch { return Results.BadRequest(new { code = "invalid_cursor", message = "cursor is invalid." }); }
        }

        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.ModelInvocations.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId);
        if (runId is { } selectedRun) query = query.Where(x => x.RunId == selectedRun);
        if (agentId is { } selectedAgent) query = query.Where(x => x.AgentDefinitionId == selectedAgent);
        if (modeVersionId is { } selectedMode) query = query.Where(x => x.ModeVersionId == selectedMode);
        if (providerId is { } selectedProvider) query = query.Where(x => x.ProviderInstanceId == selectedProvider);
        if (!string.IsNullOrWhiteSpace(model)) query = query.Where(x => x.Model == model);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (from is { } fromValue) query = query.Where(x => x.StartedAt >= fromValue);
        if (to is { } toValue) query = query.Where(x => x.StartedAt <= toValue);
        if (cursor is { } cursorValue) query = query.Where(x => x.StartedAt < cursorValue);
        var rows = (await query.ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(x => x.StartedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit + 1)
            .ToList();
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return Results.Ok(new ModelInvocationPageDto
        {
            Items = rows.Select(ToDto).ToArray(),
            NextCursor = hasMore && rows.Count != 0
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(rows[^1].StartedAt.UtcTicks.ToString()))
                : null
        });
    }

    private static bool Matches(Guid candidateProvider, string? candidateModel, Guid? provider, string? model) =>
        (provider is null || provider == candidateProvider)
        && (model is null || string.Equals(model, candidateModel, StringComparison.Ordinal));

    private static ModelStrategyDto? TryParse(string? json)
    {
        try { return ModelStrategyJson.Parse(json); }
        catch { return null; }
    }

    private static bool TryGuid(HttpRequest request, string key, out Guid? value, out string? error)
    {
        value = null; error = null;
        var text = request.Query[key].ToString();
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!Guid.TryParse(text, out var parsed) || parsed == Guid.Empty) { error = $"{key} must be a UUID."; return false; }
        value = parsed; return true;
    }

    private static bool TryDate(HttpRequest request, string key, out DateTimeOffset? value, out string? error)
    {
        value = null; error = null;
        var text = request.Query[key].ToString();
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTimeOffset.TryParse(text, out var parsed)) { error = $"{key} must be an ISO-8601 timestamp."; return false; }
        value = parsed; return true;
    }

    private static ModelInvocationDto ToDto(ModelInvocationRecord value) => new()
    {
        Id = value.Id, CallId = value.CallId, Attempt = value.Attempt, SessionId = value.SessionId, RunId = value.RunId,
        TurnId = value.TurnId, AgentInstanceId = value.AgentInstanceId, AgentDefinitionId = value.AgentDefinitionId,
        AgentVersionId = value.AgentVersionId, ModeVersionId = value.ModeVersionId, StrategySource = value.StrategySource,
        RouteId = value.RouteId, RouteVersionId = value.RouteVersionId, ProviderInstanceId = value.ProviderInstanceId,
        ProviderVersionId = value.ProviderVersionId, Model = value.Model, Protocol = value.Protocol,
        FallbackPosition = value.FallbackPosition, Status = value.Status, ErrorCategory = value.ErrorCategory,
        SafeErrorMessage = value.SafeErrorMessage, InputTokens = value.InputTokens, OutputTokens = value.OutputTokens,
        TotalTokens = value.TotalTokens, StartedAt = value.StartedAt, CompletedAt = value.CompletedAt
    };
}
