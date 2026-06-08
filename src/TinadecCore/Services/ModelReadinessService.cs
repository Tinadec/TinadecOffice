using Tinadec.Contracts.Models;
using TinadecCore.Storage;

namespace TinadecCore.Services;

public sealed class ModelReadinessService(CoreStore store)
{
    public ModelReadinessReceiptDto Check()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var providers = store.ListModelProviderInstances();
        var routes = store.ListModelRoutes();
        var routeGroups = routes
            .GroupBy(route => route.ProviderInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var providersById = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

        var providerReceipts = providers
            .Select(provider => BuildProvider(provider, routeGroups.TryGetValue(provider.Id, out var providerRoutes) ? providerRoutes : []))
            .OrderByDescending(provider => provider.RoutePurposes.Count)
            .ThenBy(provider => StatusSortKey(provider.Status))
            .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var routeReceipts = routes
            .Select(route => BuildRoute(route, providersById))
            .OrderBy(route => StatusSortKey(route.Status))
            .ThenBy(route => route.Purpose, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var readyProviderCount = providerReceipts.Count(provider => Is(provider.Status, "ready"));
        var warningProviderCount = providerReceipts.Count(provider => Is(provider.Status, "warning"));
        var blockedProviderCount = providerReceipts.Count(provider => Is(provider.Status, "blocked"));
        var readyRouteCount = routeReceipts.Count(route => Is(route.Status, "ready"));
        var warningRouteCount = routeReceipts.Count(route => Is(route.Status, "warning"));
        var blockedRouteCount = routeReceipts.Count(route => Is(route.Status, "blocked"));
        var status = blockedProviderCount > 0 || blockedRouteCount > 0
            ? "blocked"
            : warningProviderCount > 0 || warningRouteCount > 0
                ? "warning"
                : "ready";

        return new ModelReadinessReceiptDto(
            status,
            generatedAt,
            $"model_readiness_{generatedAt:yyyyMMddHHmmssfff}",
            providers.Count,
            readyProviderCount,
            warningProviderCount,
            blockedProviderCount,
            routes.Count,
            readyRouteCount,
            warningRouteCount,
            blockedRouteCount,
            providerReceipts,
            routeReceipts,
            [
                "Core treats live provider discovery as advisory; static provider configuration remains visible when probes are unavailable.",
                "Routes are blocked when they target missing, disabled, unconfigured, keyless, or cooling-down providers.",
                "Gateway and Desktop may display this receipt, but must not recompute provider or route readiness."
            ]);
    }

    private static ModelProviderReadinessDto BuildProvider(
        ModelProviderInstanceDto provider,
        IReadOnlyList<ModelRouteDto> routes)
    {
        var routePurposes = routes
            .Select(route => route.Purpose)
            .OrderBy(purpose => purpose, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var providerReady = Is(provider.Status, "ready");
        var status = providerReady
            ? routePurposes.Length > 0 ? "ready" : "warning"
            : routePurposes.Length > 0 ? "blocked" : "warning";
        var summary = status switch
        {
            "ready" => $"Provider is ready for {routePurposes.Length} model route(s).",
            "blocked" => $"Provider status '{provider.Status}' blocks routed model traffic.",
            _ when providerReady => "Provider is configured but not assigned to a model route.",
            _ => $"Provider status '{provider.Status}' is not active for any route."
        };

        return new ModelProviderReadinessDto(
            provider.Id,
            provider.DisplayName,
            provider.Driver,
            provider.ConnectionKind,
            status,
            provider.Status,
            provider.Enabled,
            HasCredential(provider),
            routePurposes,
            summary,
            BuildProviderEvidence(provider, routePurposes));
    }

    private static ModelRouteReadinessDto BuildRoute(
        ModelRouteDto route,
        IReadOnlyDictionary<string, ModelProviderInstanceDto> providersById)
    {
        if (!providersById.TryGetValue(route.ProviderInstanceId, out var provider))
        {
            return new ModelRouteReadinessDto(
                route.Purpose,
                route.ProviderInstanceId,
                null,
                route.Model,
                "blocked",
                "Route points to a missing provider instance.",
                [
                    $"provider_instance_id:{route.ProviderInstanceId}",
                    $"model:{route.Model ?? "(provider-default)"}"
                ]);
        }

        if (!Is(provider.Status, "ready"))
        {
            return new ModelRouteReadinessDto(
                route.Purpose,
                provider.Id,
                provider.DisplayName,
                route.Model ?? provider.Model,
                "blocked",
                $"Route provider is {provider.Status}.",
                [
                    $"provider_instance_id:{provider.Id}",
                    $"provider_status:{provider.Status}",
                    $"provider_enabled:{provider.Enabled}",
                    $"has_credential:{HasCredential(provider)}",
                    $"model:{route.Model ?? provider.Model ?? "(unset)"}"
                ]);
        }

        return new ModelRouteReadinessDto(
            route.Purpose,
            provider.Id,
            provider.DisplayName,
            route.Model ?? provider.Model,
            "ready",
            "Route resolves to a ready provider instance.",
            [
                $"provider_instance_id:{provider.Id}",
                $"provider_status:{provider.Status}",
                $"model:{route.Model ?? provider.Model ?? "(unset)"}"
            ]);
    }

    private static IReadOnlyList<string> BuildProviderEvidence(ModelProviderInstanceDto provider, IReadOnlyList<string> routePurposes)
    {
        return
        [
            $"provider_status:{provider.Status}",
            $"enabled:{provider.Enabled}",
            $"connection_kind:{provider.ConnectionKind}",
            $"requires_api_key:{ProviderTemplateRules.RequiresApiKey(provider.Driver, provider.ConnectionKind, provider.Capabilities)}",
            $"has_credential:{HasCredential(provider)}",
            $"route_count:{routePurposes.Count}",
            $"capability_count:{provider.Capabilities.Count}",
            $"model:{provider.Model ?? "(unset)"}"
        ];
    }

    private static bool HasCredential(ModelProviderInstanceDto provider)
    {
        if (!ProviderTemplateRules.RequiresApiKey(provider.Driver, provider.ConnectionKind, provider.Capabilities))
        {
            return true;
        }

        return provider.HasApiKey;
    }

    private static int StatusSortKey(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "blocked" => 0,
            "warning" => 1,
            "ready" => 2,
            _ => 3
        };
    }

    private static bool Is(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
