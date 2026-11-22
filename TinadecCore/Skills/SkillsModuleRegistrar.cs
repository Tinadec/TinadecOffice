using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Skills;

/// <summary>
/// Skills module registrar. Registers skill provider using MAF AgentSkillsProvider.
/// </summary>
public sealed class SkillsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "skills";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<IntegrationDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<IntegrationDbContext>>();
        builder.Services.AddSingleton<ToolSkillResourceService>();
        builder.Services.AddSingleton<IToolSkillResourceService>(sp => sp.GetRequiredService<ToolSkillResourceService>());
        builder.Services.AddSingleton<IToolSkillCatalog>(sp => sp.GetRequiredService<ToolSkillResourceService>());
        builder.Services.AddSingleton<ISkillProvider, SkillProvider>();
        builder.Services.AddSingleton<IExternalCliDiscoveryService, ExternalCliDiscoveryService>();
        builder.Services.AddSingleton<IMarketCatalogService, MarketCatalogService>();
        builder.Services.AddSingleton<IMarketInstallService, MarketInstallService>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence"],
            // "extension_installations" claimed the workspace_extensions tables, which still have no
            // writer and whose /extensions/* surface still answers 501. What exists now is narrower
            // and named for itself: a market entry becomes one approved config write.
            Capabilities = ["file_skills", "class_skills", "inline_skills", "skill_md", "sep_2640", "market_installs", "mcp_acp_integrations", "market_catalog"],
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
internal sealed class SkillProvider(IToolSkillResourceService resources) : ISkillProvider
{
    public async Task<SkillDescriptor[]> ListSkillsAsync(
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        return (await resources.ListAsync(null, cancellationToken)).Skills.Where(x => x.Valid && x.Enabled)
            .Select(x => new SkillDescriptor { Id = x.ResourceId.ToString(), Name = x.Name, Description = x.Description, AgentId = agentId }).ToArray();
    }

    public async Task<SkillDescriptor?> GetSkillAsync(
        string skillId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(skillId, out var id)) return null;
        var resource = await resources.GetAsync(id, null, cancellationToken);
        return resource is { Valid: true, Enabled: true } ? new SkillDescriptor { Id = skillId, Name = resource.Name, Description = resource.Description } : null;
    }

    public async Task<SkillDescriptor[]> ListSkillsForProjectAsync(Guid projectId, string? agentId = null, CancellationToken cancellationToken = default)
        => (await resources.ListAsync(projectId, cancellationToken)).Skills.Where(x => x.Valid && x.Enabled)
            .Select(x => new SkillDescriptor { Id = x.ResourceId.ToString(), Name = x.Name, Description = x.Description, AgentId = agentId }).ToArray();

    public async Task<SkillDescriptor?> GetSkillForProjectAsync(string skillId, Guid projectId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(skillId, out var id)) return null;
        var resource = await resources.GetAsync(id, projectId, cancellationToken);
        return resource is { Valid: true, Enabled: true }
            ? new SkillDescriptor { Id = skillId, Name = resource.Name, Description = resource.Description }
            : null;
    }
}
