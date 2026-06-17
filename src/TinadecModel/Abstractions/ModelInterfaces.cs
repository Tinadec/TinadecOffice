using Microsoft.Extensions.DependencyInjection;
using Tinadec.Contracts.Models;

namespace TinadecModel.Abstractions;

public interface IModelRouter
{
    IReadOnlyList<ModelRouteDto> ListRoutes();
    ModelRouteDto? GetRoute(string purpose);
}

public interface IModelRouteResolver
{
    ResolvedModelInvocationContextDto Resolve(string purpose);
}

public interface IModelCredentialResolver
{
    string? ResolveApiKey(ResolvedModelInvocationContextDto context);
}

public interface IModelProviderRuntime
{
    string Id { get; }
    bool CanHandle(ResolvedModelInvocationContextDto context);
    Task<ModelInvocationResultDto> GenerateAsync(
        ResolvedModelInvocationContextDto context,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ModelToolSpecDto>? tools = null);
}

public interface IModelProviderModule
{
    string ProviderFamily { get; }
    void RegisterServices(IServiceCollection services);
    ProviderCapabilityDto GetCapabilities();
}

public sealed record ModelProviderModuleMetadata(
    string ProviderFamily,
    ProviderCapabilityDto Capabilities);

public interface IModelProviderModuleCatalog
{
    IReadOnlyList<ModelProviderModuleMetadata> ListModules();
    ProviderCapabilityDto? GetCapabilities(string providerFamily);
}

public interface IModelInvocationRuntime
{
    Task<ModelInvocationResultDto> InvokeAsync(
        string sessionId,
        string purpose,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default,
        string? systemPrompt = null,
        IReadOnlyList<ModelToolSpecDto>? tools = null);
}

public interface IModelManagementService
{
    IReadOnlyList<ModelProviderTemplateDto> ListProviderTemplates();
    IReadOnlyList<ModelProviderInstanceDto> ListProviders();
    ModelProviderInstanceDto CreateProvider(SaveModelProviderInstanceRequest request);
    ModelProviderInstanceDto? UpdateProvider(string providerInstanceId, SaveModelProviderInstanceRequest request);
    ModelProviderInstanceDto? DeleteProvider(string providerInstanceId);
    IReadOnlyList<ModelRouteDto> ListRoutes();
    ModelRouteDto? SaveRoute(string purpose, SaveModelRouteRequest request);
    ModelSettingsDto GetSettings();
    ModelSettingsDto SaveSettings(SaveModelSettingsRequest request);
}

public interface IModelProviderRegistry
{
    IReadOnlyList<ModelProviderTemplateDto> ListTemplates();
    IReadOnlyList<ModelProviderInstanceDto> ListProviders();
}
