using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;

namespace TinadecCore.Tools;

/// <summary>
/// Tools module registrar. Registers the local TinadecTools provider (hosted),
/// manifest registry, trusted invocation-scope resolver, and durable dispatcher.
/// Dispatch policy values are bound from the frozen TOML runtime profile by the
/// composition root via <see cref="ToolDispatchOptions"/>.
/// </summary>
public sealed class ToolsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "tools";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<ToolsSettingsDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<ToolsSettingsDbContext>>();
        builder.Services.AddSingleton<IToolSettingsStore, ToolSettingsStore>();
        builder.Services.AddSingleton<IConfigurationDocumentValidator, ToolSettingsDocumentValidator>();
        builder.Services.AddSingleton<IMcpResourceRegistry, McpResourceRegistry>();
        builder.Services.AddSingleton<IMcpServerService, McpServerService>();
        builder.Services.AddSingleton<IToolConfigurationResolver, ToolConfigurationResolver>();
        builder.Services.AddSingleton<IManagedMcpProgramService, ManagedMcpProgramService>();
        builder.Services.AddSingleton<TinadecToolsProcessManager>();
        builder.Services.AddSingleton<IToolExecutionContextLifecycle>(sp => sp.GetRequiredService<TinadecToolsProcessManager>());
        // Keep the old process-manager port available for existing embedders;
        builder.Services.AddSingleton<IToolProcessManager>(sp => sp.GetRequiredService<TinadecToolsProcessManager>());
        // Resolve the provider through the compatibility port so hosts that
        // already replace IToolProcessManager continue to work. New hosts may
        // replace IToolProvider directly to install a remote provider.
        builder.Services.AddSingleton<IToolProvider>(sp => sp.GetRequiredService<IToolProcessManager>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TinadecToolsProcessManager>());
        builder.Services.AddSingleton<IToolRegistry, CoreToolRegistry>();
        builder.Services.AddSingleton<IToolManifestSnapshotResolver, ToolManifestSnapshotResolver>();
        builder.Services.AddSingleton<IToolInvocationScopeResolver, ToolInvocationScopeResolver>();
        builder.Services.AddSingleton<IFrozenToolManifestCatalog, FrozenToolManifestCatalog>();
        builder.Services.AddSingleton<ToolDispatchOptions>();
        builder.Services.AddSingleton<ITerminalSessionRegistry, TerminalSessionRegistry>();
        builder.Services.AddSingleton<TerminalSessionEventBridge>();
        builder.Services.AddSingleton<ITerminalSessionControl, TerminalSessionControlService>();
        builder.Services.AddSingleton<InFlightToolCallRegistry>();
        builder.Services.AddSingleton<IInFlightToolCallRegistry>(sp => sp.GetRequiredService<InFlightToolCallRegistry>());
        builder.Services.AddSingleton<IRunInFlightToolCancellation>(sp => sp.GetRequiredService<InFlightToolCallRegistry>());
        builder.Services.AddHostedService<TerminalSessionBridgeHost>();
        builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence"],
            Capabilities = ["tool_process_management", "tool_registry", "tool_manifest_snapshot", "frozen_tool_catalog", "tool_dispatch", "durable_approval"],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}
