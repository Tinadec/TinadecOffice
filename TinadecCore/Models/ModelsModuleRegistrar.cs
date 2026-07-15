using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Models;

/// <summary>
/// Models module registrar. Registers model provider and routing.
/// </summary>
public sealed class ModelsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "models";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddSingleton<IModelProvider, ModelProvider>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions"],
            Capabilities = ["provider_management", "model_routing", "credential_references", "error_normalization", "readiness"],
            Language = "C#",
            MafPrimitives = ["agent", "chat_client"],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Skeleton model provider. Uses IChatClient / ChatClientAgent as entry point.
/// Does not rewrite model HTTP clients.
/// </summary>
internal sealed class ModelProvider : IModelProvider
{
    public Task<IChatClient?> GetChatClientAsync(
        string? routeId = null,
        CancellationToken cancellationToken = default)
    {
        // Skeleton: no providers configured.
        return Task.FromResult<IChatClient?>(null);
    }

    public Task<ModelReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelReadiness
        {
            IsReady = false,
            StatusMessage = "No model providers configured.",
            Warnings = ["models module is in skeleton state — no providers registered."]
        });
    }
}
