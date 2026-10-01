using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.AgentGraph;

/// <summary>
/// Graph-shaped governance state: the resource ledger, the per-gate approval records and the
/// evidence archive governance roles recall from.
///
/// Registered as its own module so a host can compose Core without governance at all (a lightweight
/// embed); the ledger is inert until the engine writes to it.
/// </summary>
public sealed class AgentGraphModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "agent_graph";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<AgentGraphDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<AgentGraphDbContext>>();
        builder.Services.AddSingleton<IRunLivenessProbe, LifecycleRunLivenessProbe>();
        builder.Services.AddSingleton<ResourceLeaseService>();
        builder.Services.AddSingleton<IResourceLeaseService>(sp => sp.GetRequiredService<ResourceLeaseService>());
        // The evidence archive: durable rows first, the semantic index built behind them by one indexer.
        builder.Services.AddOptions<EvidenceArchiveOptions>().BindConfiguration(EvidenceArchiveOptions.SectionName);
        builder.Services.AddSingleton<EvidenceArchiveService>();
        builder.Services.AddSingleton<IEvidenceArchive>(sp => sp.GetRequiredService<EvidenceArchiveService>());
        builder.Services.AddSingleton<EvidenceIndexService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EvidenceIndexService>());
        // The environment steward's registry: rows here, occupancy in the ledger above.
        builder.Services.AddSingleton<IEnvironmentRegistry, EnvironmentRegistryService>();
        builder.Services.AddSingleton<IToolExecutionTargetResolver, ToolExecutionTargetResolver>();
        // Standing approvals a person gave: command prefixes, and the per-session shell delegation opt-in.
        builder.Services.AddSingleton<IApprovalRules, ApprovalRulesService>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.5.0",
            Language = "C#",
            Dependencies = ["abstractions", "persistence"],
            Capabilities = ["resource_ledger", "write_scope_leases", "dead_holder_reclaim", "approval_gates", "evidence_archive", "hybrid_recall", "environment_registry", "approval_rules"],
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}

/// <summary>
/// Run liveness over the lifecycle store. Resolved lazily because the lifecycle module may register
/// after this one (or not at all, in a trimmed embed); without it every run counts as live, so the
/// ledger never reclaims a lease it cannot prove is orphaned.
/// </summary>
internal sealed class LifecycleRunLivenessProbe(IServiceProvider services) : IRunLivenessProbe
{
    public async Task<bool> IsLiveAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (services.GetService<ILifecycleManager>() is not { } lifecycle) return true;
        try
        {
            var state = await lifecycle.GetRunStateAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            return state.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled);
        }
        catch (KeyNotFoundException)
        {
            // A run the lifecycle no longer knows cannot be writing anything.
            return false;
        }
    }
}
