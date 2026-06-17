using Microsoft.Extensions.DependencyInjection;
using TinadecModel.Abstractions;
using TinadecModel.Providers;
using TinadecModel.Services;
using TinadecModel.Storage;

namespace TinadecModel;

public static class ModelServiceCollectionExtensions
{
    public static IServiceCollection AddTinadecModel(
        this IServiceCollection services, string databasePath)
    {
        // Storage
        services.AddSingleton<IModelStore>(sp => new ModelStore(databasePath));
        services.AddSingleton<SecretProtector>();

        // Core services
        services.AddSingleton<IModelRouteResolver, ModelRouteResolver>();
        services.AddSingleton<IModelCredentialResolver, ModelCredentialResolver>();
        services.AddSingleton<IModelInvocationRuntime, ModelInvocationRuntime>();
        services.AddSingleton<IModelManagementService, ModelManagementService>();
        services.AddSingleton<ModelReadinessService>();
        services.AddSingleton<ModelCatalogReadinessService>();

        // Provider modules
        services.AddModelProviderModule<LocalHttpModule>();
        services.AddModelProviderModule<AnthropicModule>();
        services.AddModelProviderModule<OpenAiCompatibleModule>();
        services.AddModelProviderModule<CliModule>();

        return services;
    }
}
