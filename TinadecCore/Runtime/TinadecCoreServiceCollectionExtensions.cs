using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Context;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.LoopGuard;
using TinadecCore.Memory;
using TinadecCore.Models;
using TinadecCore.Prompts;
using TinadecCore.Skills;
using TinadecCore.Tenancy;
using TinadecCore.Tools;
using TinadecCore.VectorStore;

namespace TinadecCore.Runtime;

/// <summary>
/// DI extension methods for TinadecCore module registration.
/// Default: registers all Core modules. Custom hosts can register a subset.
/// </summary>
public static class TinadecCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers all TinadecCore modules (full composition).
    /// </summary>
    public static ITinadecCoreBuilder AddTinadecCore(this IServiceCollection services)
    {
        var builder = new TinadecCoreBuilder(services);

        // Register modules in dependency order.
        // Each module calls builder.RegisterModule() to declare its descriptor.
        new TenancyModuleRegistrar().Register(builder);
        new VectorStoreModuleRegistrar().Register(builder);
        new LifecycleModuleRegistrar().Register(builder);
        new ModelsModuleRegistrar().Register(builder);
        new ContextModuleRegistrar().Register(builder);
        new PromptsModuleRegistrar().Register(builder);
        new MemoryModuleRegistrar().Register(builder);
        new SkillsModuleRegistrar().Register(builder);
        new LoopGuardModuleRegistrar().Register(builder);
        new ToolsModuleRegistrar().Register(builder);
        new DmaEAModuleRegistrar().Register(builder);

        // Rebind ToolDispatchOptions from the frozen TOML runtime profile (this factory
        // registration replaces the defaults the Tools module registered; DI resolves
        // the last registration for the type).
        services.AddSingleton(sp =>
        {
            var runtime = sp.GetRequiredService<IAgentRuntimeConfiguration>();
            var policy = runtime.Current.Tools;
            return new ToolDispatchOptions
            {
                MutationRequiresApproval = policy.MutationRequiresApproval,
                SerializeWorkspaceWrites = policy.SerializeWorkspaceWrites,
                DefaultTimeoutSeconds = policy.DefaultTimeoutSeconds,
                WorkerRetryLimit = runtime.Current.Scheduling.WorkerRetryLimit
            };
        });

        return builder;
    }

    /// <summary>
    /// Registers a minimal subset of TinadecCore modules.
    /// Demonstrates compile-time trimming: Memory/Skills/etc. are not runtime-required.
    /// </summary>
    public static ITinadecCoreBuilder AddTinadecCoreMinimal(this IServiceCollection services)
    {
        var builder = new TinadecCoreBuilder(services);

        new TenancyModuleRegistrar().Register(builder);
        new LifecycleModuleRegistrar().Register(builder);
        new ModelsModuleRegistrar().Register(builder);
        new DmaEAModuleRegistrar().Register(builder);

        return builder;
    }
}
