using Tinadec.Contracts.Models;
using TinadecModel.Storage;

namespace TinadecModel.Abstractions;

public interface IModelStore
{
    void Initialize();

    // Settings
    StoredModelSettings GetModelSettings();
    StoredModelSettings SaveModelSettings(string baseUrl, string model, string? encryptedApiKey);

    // Provider Instances
    IReadOnlyList<ModelProviderInstanceDto> ListModelProviderInstances();
    StoredModelProviderInstance? GetStoredModelProviderInstance(string providerInstanceId);
    ModelProviderInstanceDto SaveModelProviderInstance(
        SaveModelProviderInstanceRequest request, string? encryptedApiKey);
    bool DeleteModelProviderInstance(string providerInstanceId);

    // Health
    void RecordModelProviderFailure(
        string providerInstanceId, ProviderErrorCategory category, DateTimeOffset now);
    void RecordModelProviderSuccess(string providerInstanceId);

    // Routes
    IReadOnlyList<ModelRouteDto> ListModelRoutes();
    ModelRouteDto? GetModelRoute(string purpose);
    ModelRouteDto SaveModelRoute(string purpose, string providerInstanceId, string? model);
}
