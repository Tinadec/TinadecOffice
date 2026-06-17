using System.Diagnostics;
using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Storage;
using TinadecModel.Tracing;

namespace TinadecModel.Services;

public sealed class ModelRouteResolver(IModelStore store) : IModelRouteResolver
{
    public ResolvedModelInvocationContextDto Resolve(string purpose)
    {
        using var activity = ModelActivitySource.Instance.StartActivity(ModelSpanNames.ModelRouteSelection);
        activity?.SetTag(ModelSpanAttrs.RoutePurpose, purpose);

        var route = store.GetModelRoute(purpose);
        var provider = ResolveProvider(purpose);
        activity?.SetTag(ModelSpanAttrs.ProviderId, provider?.Id)
            .SetTag(ModelSpanAttrs.ProviderInstanceId, provider?.Id);

        if (provider is null)
        {
            activity?.SetTag(ModelSpanAttrs.Status, "failed");
            throw new InvalidOperationException($"No enabled model provider can serve route purpose '{purpose}'.");
        }

        var effective = provider.ToModelSettings(route?.ProviderInstanceId == provider.Id ? route.Model : null);
        var providerDto = provider.ToDto();
        activity?.SetTag(ModelSpanAttrs.Model, effective.Model)
            .SetTag(ModelSpanAttrs.ConnectionKind, provider.ConnectionKind)
            .SetTag(ModelSpanAttrs.Driver, provider.Driver)
            .SetTag(ModelSpanAttrs.HealthStatus, ResolveHealthStatus(provider).ToString())
            .SetTag(ModelSpanAttrs.Status, "selected");

        return new ResolvedModelInvocationContextDto(
            purpose, route, providerDto,
            effective.BaseUrl, effective.Model, effective.EncryptedApiKey,
            provider.Driver, provider.ConnectionKind, provider.Id,
            route is null);
    }

    private StoredModelProviderInstance? ResolveProvider(string purpose)
    {
        var route = store.GetModelRoute(purpose);
        var providers = store.ListModelProviderInstances()
            .Select((provider, index) => new ProviderRouteCandidate(
                store.GetStoredModelProviderInstance(provider.Id), index, null))
            .Where(c => c.Provider is not null)
            .Select(c => c with { Priority = ResolvePriority(c.Provider!) })
            .ToArray();
        var now = ResolveClock(providers) ?? DateTimeOffset.UtcNow;

        if (route is not null)
        {
            var routedProvider = providers
                .FirstOrDefault(c => c.Provider!.Id.Equals(route.ProviderInstanceId, StringComparison.OrdinalIgnoreCase)
                    && CanServe(c.Provider!, purpose, now))?.Provider;
            if (routedProvider is not null) return routedProvider;
        }

        return providers
            .Where(c => CanServe(c.Provider!, purpose, now))
            .OrderBy(c => c.Priority is null ? 1 : 0)
            .ThenBy(c => c.Priority ?? int.MaxValue)
            .ThenBy(c => c.RouteOrder)
            .ThenBy(c => c.Provider!.Id, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Provider)
            .FirstOrDefault();
    }

    private static bool CanServe(StoredModelProviderInstance provider, string purpose, DateTimeOffset now)
        => provider.Enabled
            && provider.Capabilities.Any(c => c.Equals("chat", StringComparison.OrdinalIgnoreCase))
            && AllowsPurpose(provider, purpose)
            && IsAvailable(provider, now);

    private static bool AllowsPurpose(StoredModelProviderInstance provider, string purpose)
    {
        var routeCaps = provider.Capabilities.Where(c => c.StartsWith("route:", StringComparison.OrdinalIgnoreCase)).ToArray();
        return routeCaps.Length == 0 || routeCaps.Any(c => c["route:".Length..].Equals(purpose, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAvailable(StoredModelProviderInstance provider, DateTimeOffset now)
    {
        var health = ResolveHealthStatus(provider);
        if (health is ProviderHealthStatus.Disabled) return false;
        if (health is ProviderHealthStatus.Unhealthy or ProviderHealthStatus.Cooldown)
            return !IsCooldownActive(provider, now);
        return true;
    }

    private static bool IsCooldownActive(StoredModelProviderInstance provider, DateTimeOffset now)
    {
        var cooldownUntil = ResolveCapabilityTime(provider, "cooldown_until") ?? provider.CooldownUntil;
        return cooldownUntil is not null && cooldownUntil > now;
    }

    private static int? ResolvePriority(StoredModelProviderInstance provider)
    {
        var value = ResolveCapabilityValue(provider, "priority");
        return int.TryParse(value, out var priority) ? priority : null;
    }

    private static ProviderHealthStatus ResolveHealthStatus(StoredModelProviderInstance provider)
    {
        var health = ResolveCapabilityValue(provider, "health");
        if (health is null) return provider.HealthStatus;
        return health.Trim().ToLowerInvariant() switch
        {
            "unhealthy" => ProviderHealthStatus.Unhealthy,
            "unknown" => ProviderHealthStatus.Unknown,
            "disabled" => ProviderHealthStatus.Disabled,
            "cooldown" => ProviderHealthStatus.Cooldown,
            _ => ProviderHealthStatus.Healthy
        };
    }

    private static DateTimeOffset? ResolveClock(IEnumerable<ProviderRouteCandidate> providers)
        => providers.Select(c => c.Provider is null ? null : ResolveCapabilityTime(c.Provider, "clock"))
            .FirstOrDefault(v => v is not null);

    private static DateTimeOffset? ResolveCapabilityTime(StoredModelProviderInstance provider, string key)
    {
        var value = ResolveCapabilityValue(provider, key);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? ResolveCapabilityValue(StoredModelProviderInstance provider, string key)
    {
        var prefix = key + ":";
        return provider.Capabilities.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    private sealed record ProviderRouteCandidate(StoredModelProviderInstance? Provider, int RouteOrder, int? Priority);
}
