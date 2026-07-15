using Microsoft.Extensions.DependencyInjection;

namespace TinadecCore.Abstractions;

/// <summary>
/// Each business module implements this to register its services and descriptor.
/// Modules are registered explicitly — no reflection scanning.
/// </summary>
public interface IModuleRegistrar
{
    /// <summary>The unique module identifier (e.g. "dma_ea", "models").</summary>
    string ModuleId { get; }

    /// <summary>Registers services into the DI container and descriptor into the builder.</summary>
    void Register(ITinadecCoreBuilder builder);
}
