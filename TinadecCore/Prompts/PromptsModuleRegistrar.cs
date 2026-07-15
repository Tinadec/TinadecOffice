using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Prompts;

/// <summary>
/// Prompts module registrar. Registers prompt assembler.
/// </summary>
public sealed class PromptsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "prompts";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddSingleton<IPromptAssembler, PromptAssembler>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies"],
            Capabilities = ["fragment_assembly", "agent_instructions", "skill_contributions", "deterministic_assembly"],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Skeleton prompt assembler. Deterministically assembles fragments into ChatOptions.Instructions.
/// Full prompt content stays local to preview UI and in-memory calls.
/// </summary>
internal sealed class PromptAssembler : IPromptAssembler
{
    public Task<PromptAssemblyResult> AssembleAsync(
        string agentId,
        ContextPack? contextPack,
        CancellationToken cancellationToken = default)
    {
        var result = new PromptAssemblyResult
        {
            Instructions = string.Empty,
            EstimatedTokens = 0,
            FragmentIds = [],
            Warnings = ["prompts module is in skeleton state — no fragments registered."]
        };
        return Task.FromResult(result);
    }
}
