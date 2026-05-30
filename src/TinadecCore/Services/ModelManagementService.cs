using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

public sealed class ModelManagementService(
    CoreStore store,
    SecretProtector protector) : IModelManagementService
{
    public IReadOnlyList<ModelProviderTemplateDto> ListProviderTemplates()
    {
        return ModelProviderCatalog.ListTemplates();
    }

    public IReadOnlyList<ModelProviderInstanceDto> ListProviders()
    {
        return store.ListModelProviderInstances();
    }

    public ModelProviderInstanceDto CreateProvider(SaveModelProviderInstanceRequest request)
    {
        var encryptedApiKey = ResolveEncryptedApiKey(request, null);
        return store.SaveModelProviderInstance(request, encryptedApiKey);
    }

    public ModelProviderInstanceDto? UpdateProvider(string providerInstanceId, SaveModelProviderInstanceRequest request)
    {
        var existing = store.GetStoredModelProviderInstance(providerInstanceId);
        if (existing is null)
        {
            return null;
        }

        var encryptedApiKey = ResolveEncryptedApiKey(request, existing);
        return store.SaveModelProviderInstance(request with { Id = providerInstanceId }, encryptedApiKey);
    }

    public ModelProviderInstanceDto? DeleteProvider(string providerInstanceId)
    {
        var existing = store.GetStoredModelProviderInstance(providerInstanceId);
        if (existing is null)
        {
            return null;
        }

        return store.DeleteModelProviderInstance(providerInstanceId)
            ? existing.ToDto()
            : null;
    }

    public IReadOnlyList<ModelRouteDto> ListRoutes()
    {
        return store.ListModelRoutes();
    }

    public ModelRouteDto? SaveRoute(string purpose, SaveModelRouteRequest request)
    {
        var provider = store.GetStoredModelProviderInstance(request.ProviderInstanceId);
        if (provider is null)
        {
            return null;
        }

        return store.SaveModelRoute(purpose, request.ProviderInstanceId, request.Model ?? provider.Model);
    }

    public ModelSettingsDto GetSettings()
    {
        return store.GetModelSettings().ToDto();
    }

    public ModelSettingsDto SaveSettings(SaveModelSettingsRequest request)
    {
        var existing = store.GetModelSettings();
        string? encryptedApiKey = existing.EncryptedApiKey;

        if (request.ClearApiKey)
        {
            encryptedApiKey = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            encryptedApiKey = protector.Protect(request.ApiKey.Trim());
        }

        var saved = store.SaveModelSettings(request.BaseUrl, request.Model, encryptedApiKey);
        return saved.ToDto();
    }

    private string? ResolveEncryptedApiKey(
        SaveModelProviderInstanceRequest request,
        StoredModelProviderInstance? existing)
    {
        if (request.ClearApiKey)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return protector.Protect(request.ApiKey.Trim());
        }

        return existing?.EncryptedApiKey;
    }
}
