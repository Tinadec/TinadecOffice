namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// Readiness response. MAF assemblies loadable = ready; unconfigured modules use warning.
/// </summary>
public sealed class ReadinessResponseDto
{
    public string Status { get; init; } = "ready";
    public bool FrameworkReady { get; init; } = true;
    public string FrameworkName { get; init; } = "Microsoft Agent Framework";
    public string FrameworkVersion { get; init; } = "1.13.0";
    public ReadinessStorageDto? Storage { get; init; }
    public IReadOnlyList<ReadinessModuleDto> Modules { get; init; } = [];
}

public sealed class ReadinessModuleDto
{
    public string ModuleId { get; init; } = string.Empty;
    public string ModuleState { get; init; } = "not_configured";
    public string? Detail { get; init; }
}

/// <summary>
/// Shared database abstraction readiness receipt (provider-agnostic).
/// </summary>
public sealed class ReadinessStorageDto
{
    public string Provider { get; init; } = "sqlite";
    public string State { get; init; } = "not_configured";
    public string? Detail { get; init; }
}
