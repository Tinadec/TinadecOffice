using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Context;

/// <summary>
/// Context module registrar. Registers context provider with token budget strategy.
/// </summary>
public sealed class ContextModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "context";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddSingleton<IContextProvider, ContextProvider>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies"],
            Capabilities = ["context_pack", "evidence_gathering", "token_budget"],
            Language = "C#",
            MafPrimitives = ["context"],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Skeleton context provider. Produces ContextPack with evidence, sources, and token budget.
/// Does not assemble the final system prompt.
/// </summary>
internal sealed class ContextProvider : IContextProvider
{
    public Task<ContextPack> BuildContextAsync(
        string sessionId,
        string? runId,
        CancellationToken cancellationToken = default)
    {
        var pack = new ContextPack
        {
            SessionId = sessionId,
            RunId = runId,
            TokenBudget = 8192,
            EstimatedTokens = 0,
            Evidence = []
        };
        return Task.FromResult(pack);
    }
}
