using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;

namespace TinadecCore.Runtime;

/// <summary>
/// Default implementation of ITinadecCoreBuilder.
/// Collects module descriptors and delegates service registration to DI.
/// </summary>
public sealed class TinadecCoreBuilder : ITinadecCoreBuilder
{
    private readonly List<ModuleDescriptor> _modules = [];

    public TinadecCoreBuilder(IServiceCollection services)
    {
        Services = services;
        // Register the builder itself so endpoints can resolve it.
        Services.AddSingleton<ITinadecCoreBuilder>(this);
    }

    public IServiceCollection Services { get; }

    public void RegisterModule(ModuleDescriptor descriptor)
    {
        _modules.Add(descriptor);
    }

    public IReadOnlyList<ModuleDescriptor> GetRegisteredModules() => _modules.AsReadOnly();
}
