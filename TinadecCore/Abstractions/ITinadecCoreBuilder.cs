using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions;

/// <summary>
/// Builder for composing TinadecCore modules into a DI container.
/// Each module provides an <c>AddTinadec...()</c> extension method on this builder.
/// </summary>
public interface ITinadecCoreBuilder
{
    IServiceCollection Services { get; }

    /// <summary>Registers a module descriptor that will appear in the harness manifest.</summary>
    void RegisterModule(ModuleDescriptor descriptor);

    /// <summary>Returns all registered module descriptors.</summary>
    IReadOnlyList<ModuleDescriptor> GetRegisteredModules();
}

/// <summary>
/// Internal module descriptor (richer than the DTO; not serialized directly).
/// </summary>
public sealed class ModuleDescriptor
{
    public string ModuleId { get; init; } = string.Empty;
    public string Version { get; init; } = "0.1.0";
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string Language { get; init; } = "C#";
    public IReadOnlyList<string> MafPrimitives { get; init; } = [];
    public ModuleRegistrationStatus RegistrationStatus { get; init; } = ModuleRegistrationStatus.Registered;

    public ModuleDescriptorDto ToDto() => new()
    {
        ModuleId = ModuleId,
        Version = Version,
        Dependencies = Dependencies,
        Capabilities = Capabilities,
        Language = Language,
        MafPrimitives = MafPrimitives,
        RegistrationStatus = RegistrationStatus.ToString().ToLowerInvariant()
    };
}

public enum ModuleRegistrationStatus
{
    Registered,
    NotConfigured,
    Disabled
}
