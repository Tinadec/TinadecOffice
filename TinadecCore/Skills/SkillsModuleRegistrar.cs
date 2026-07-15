using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Skills;

/// <summary>
/// Skills module registrar. Registers skill provider using MAF AgentSkillsProvider.
/// </summary>
public sealed class SkillsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "skills";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddSingleton<ISkillProvider, SkillProvider>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions"],
            Capabilities = ["file_skills", "class_skills", "inline_skills", "skill_md", "sep_2640"],
            Language = "C#",
            MafPrimitives = ["skills"],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Skeleton skill provider. Uses MAF AgentSkillsProvider, file/class/inline skills, SKILL.md.
/// Script execution delegates to TinadecTools; all writes go through Core approval.
/// </summary>
internal sealed class SkillProvider : ISkillProvider
{
    public Task<SkillDescriptor[]> ListSkillsAsync(
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Array.Empty<SkillDescriptor>());
    }

    public Task<SkillDescriptor?> GetSkillAsync(
        string skillId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<SkillDescriptor?>(null);
    }
}
