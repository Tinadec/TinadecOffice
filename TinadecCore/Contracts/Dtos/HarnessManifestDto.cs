namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// Harness manifest. Existing fields are preserved; <see cref="Framework"/> and
/// <see cref="Modules"/> are incremental additions for the MAF-based rebuild.
/// </summary>
public sealed class HarnessManifestDto
{
    public string Runtime { get; init; } = "tinadec-core-maf";
    public string OwnershipModel { get; init; } = "core-authoritative";
    public ToolRegistrySummaryDto ToolRegistry { get; init; } = new();
    public IReadOnlyList<AgentLayerManifestDto> AgentLayers { get; init; } = [];
    public IReadOnlyList<ToolProviderManifestDto> ToolProviders { get; init; } = [];
    public IReadOnlyList<ToolRiskManifestDto> ToolRisks { get; init; } = [];
    public IReadOnlyList<ToolDescriptorDto> Tools { get; init; } = [];
    public IReadOnlyList<string> DesignNotes { get; init; } = [];

    /// <summary>Incremental: MAF framework metadata.</summary>
    public FrameworkInfoDto Framework { get; init; } = new();

    /// <summary>Incremental: registered module descriptors.</summary>
    public IReadOnlyList<ModuleDescriptorDto> Modules { get; init; } = [];
}

public sealed class FrameworkInfoDto
{
    public string Name { get; init; } = "Microsoft Agent Framework";
    public string Version { get; init; } = "1.13.0";
    public IReadOnlyList<string> Primitives { get; init; } =
    [
        "agent",
        "workflow",
        "context",
        "memory",
        "skills",
        "loop",
        "approval",
        "session",
        "checkpoint"
    ];
}

public sealed class ModuleDescriptorDto
{
    public string ModuleId { get; init; } = string.Empty;
    public string Version { get; init; } = "0.1.0";
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string Language { get; init; } = "C#";
    public IReadOnlyList<string> MafPrimitives { get; init; } = [];
    public string RegistrationStatus { get; init; } = "registered";
}
