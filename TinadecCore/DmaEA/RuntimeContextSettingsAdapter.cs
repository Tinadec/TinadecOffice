using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>Keeps Context module reads aligned with valid-only TOML hot reloads.</summary>
internal sealed class RuntimeContextSettingsAdapter : IRuntimeContextSettings
{
    private readonly AgentRuntimeConfigurationStore _configuration;

    public RuntimeContextSettingsAdapter(AgentRuntimeConfigurationStore configuration) => _configuration = configuration;

    public RuntimeContextSettings Current => _configuration.ContextSettings;
}
