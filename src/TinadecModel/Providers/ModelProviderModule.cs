using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;

namespace TinadecModel.Providers;

public static class ModelProviderModuleServiceCollectionExtensions
{
    public static IServiceCollection AddModelProviderModule<TModule>(this IServiceCollection services)
        where TModule : IModelProviderModule, new()
    {
        var module = new TModule();
        var providerFamily = NormalizeProviderFamily(module.ProviderFamily);

        if (services.Any(descriptor => IsDuplicateProviderFamily(descriptor, providerFamily)))
        {
            throw new InvalidOperationException($"A model provider module for provider family '{providerFamily}' is already registered.");
        }

        var metadata = new ModelProviderModuleMetadata(providerFamily, module.GetCapabilities());

        services.AddSingleton<IModelProviderModule>(module);
        services.AddSingleton(metadata);
        services.TryAddSingleton<IModelProviderModuleCatalog, ModelProviderModuleCatalog>();

        module.RegisterServices(services);
        return services;
    }

    private static bool IsDuplicateProviderFamily(ServiceDescriptor descriptor, string providerFamily)
    {
        return descriptor.ServiceType == typeof(ModelProviderModuleMetadata)
            && descriptor.ImplementationInstance is ModelProviderModuleMetadata metadata
            && string.Equals(metadata.ProviderFamily, providerFamily, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProviderFamily(string providerFamily)
    {
        if (string.IsNullOrWhiteSpace(providerFamily))
        {
            throw new InvalidOperationException("Model provider modules must declare a provider family.");
        }

        return providerFamily.Trim();
    }
}

public sealed class ModelProviderModuleCatalog(IEnumerable<ModelProviderModuleMetadata> modules) : IModelProviderModuleCatalog
{
    private readonly IReadOnlyList<ModelProviderModuleMetadata> _modules = modules.ToArray();

    public IReadOnlyList<ModelProviderModuleMetadata> ListModules()
    {
        return _modules;
    }

    public ProviderCapabilityDto? GetCapabilities(string providerFamily)
    {
        return _modules.FirstOrDefault(module =>
            string.Equals(module.ProviderFamily, providerFamily, StringComparison.OrdinalIgnoreCase))?.Capabilities;
    }
}

public sealed class OpenAiCompatibleModule : IModelProviderModule
{
    public string ProviderFamily => "openai-compatible";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddHttpClient<OpenAiCompatibleClient>();
        services.AddSingleton<IModelProviderRuntime, OpenAiCompatibleProviderRuntime>();
    }

    public ProviderCapabilityDto GetCapabilities()
    {
        return new ProviderCapabilityDto(
            SupportsStreaming: true,
            SupportsTools: true,
            SupportsJsonMode: true,
            SupportsSystemPrompt: true,
            MaxContextTokens: null,
            RequiresWorkspace: false,
            CredentialKind: "api-key",
            HealthStatus: ProviderHealthStatus.Unknown);
    }
}

public sealed class CliModule : IModelProviderModule
{
    public string ProviderFamily => "cli";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IModelProviderRuntime, CliProviderRuntime>();
    }

    public ProviderCapabilityDto GetCapabilities()
    {
        return new ProviderCapabilityDto(
            SupportsStreaming: false,
            SupportsTools: false,
            SupportsJsonMode: false,
            SupportsSystemPrompt: true,
            MaxContextTokens: null,
            RequiresWorkspace: true,
            CredentialKind: "cli",
            HealthStatus: ProviderHealthStatus.Unknown);
    }
}
