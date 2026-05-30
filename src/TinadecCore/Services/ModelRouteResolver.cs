using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

public sealed class ModelRouteResolver(CoreStore store) : IModelRouteResolver
{
    private const string FallbackProviderId = "openai_default";

    public ResolvedModelInvocationContextDto Resolve(string purpose)
    {
        var route = store.GetModelRoute(purpose);
        var provider = route is null
            ? store.GetStoredModelProviderInstance(FallbackProviderId)
            : store.GetStoredModelProviderInstance(route.ProviderInstanceId);

        var effective = provider?.ToModelSettings(route?.Model) ?? store.GetModelSettings();
        var providerDto = provider?.ToDto();

        return new ResolvedModelInvocationContextDto(
            purpose,
            route,
            providerDto,
            effective.BaseUrl,
            effective.Model,
            effective.EncryptedApiKey,
            provider?.Driver,
            provider?.ConnectionKind ?? "api-key",
            provider?.Id ?? FallbackProviderId,
            route is null);
    }
}
