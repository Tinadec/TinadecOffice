namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// Health check response matching the legacy {name, status, version, time} structure.
/// </summary>
public sealed class HealthResponseDto
{
    public string Name { get; init; } = "tinadec-core";
    public string Status { get; init; } = "ok";
    public string Version { get; init; } = "0.1.0";
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}
