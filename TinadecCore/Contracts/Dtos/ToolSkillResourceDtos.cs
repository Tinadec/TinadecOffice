namespace TinadecCore.Contracts.Dtos;

public sealed record ToolSkillResourceDto
{
    public Guid ResourceId { get; init; }
    public Guid? ProjectId { get; init; }
    public string Scope { get; init; } = "shared";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Valid { get; init; }
    public string? Reason { get; init; }
    /// <summary>Stable identifier of <see cref="Reason"/> (for example <c>name_invalid</c>). Clients word it from this
    /// code and <see cref="ReasonData"/>; Reason stays the English diagnostic.</summary>
    public string? ReasonCode { get; init; }
    /// <summary>Facts the wording of <see cref="ReasonCode"/> needs, such as a name or a limit.</summary>
    public IReadOnlyDictionary<string, string>? ReasonData { get; init; }
    public string Path { get; init; } = "";
    public long Revision { get; init; }
    public string ContentHash { get; init; } = "";
    public string PackageHash { get; init; } = "";
    public string PackageReference { get; init; } = "";
    public string Source { get; init; } = "manual";
    public string? Version { get; init; }
    public string? Commit { get; init; }
    public string Availability { get; init; } = "available";
    public IReadOnlyList<ToolSkillPackageFileDto> PackageFiles { get; init; } = [];
    public string? Content { get; init; }
    public string? FileHash { get; init; }
    public Guid? UserActionId { get; init; }
    public string? ActionStatus { get; init; }
}

public sealed record ToolSkillPackageFileDto
{
    public string Path { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public long SizeBytes { get; init; }
}

public sealed record ToolSkillFileDto
{
    public string Path { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public long SizeBytes { get; init; }
    public string? Content { get; init; }
    public string? Base64 { get; init; }
}

public sealed record ToolSkillListDto(IReadOnlyList<ToolSkillResourceDto> Skills, IReadOnlyList<string> Diagnostics, string Source = "core");
public sealed class ToolSkillWriteDto
{
    public Guid? ReservedResourceId { get; init; }
    public string Scope { get; init; } = "shared";
    public Guid? ProjectId { get; init; }
    public string? Name { get; init; }
    public string? Content { get; init; }
    public bool? Enabled { get; init; }
    public IReadOnlyDictionary<string, string>? Files { get; init; }
    public bool ReplaceFiles { get; init; }
    public string? ExpectedFileHash { get; init; }
    public string? ExpectedPackageDigest { get; init; }
    public string Source { get; init; } = "manual";
    public string? Version { get; init; }
    public string? Commit { get; init; }
}

/// <summary>Frozen, Core-owned update used by an approved market skill installation.</summary>
public sealed class ToolSkillResourceUpdateDto
{
    public string Action { get; init; } = "save";
    public Guid? ResourceId { get; init; }
    public long ExpectedRevision { get; init; }
    public string Scope { get; init; } = "shared";
    public string Name { get; init; } = "";
    public string Content { get; init; } = "";
    public IReadOnlyDictionary<string, string>? Files { get; init; }
    public bool ReplaceFiles { get; init; } = true;
    public string Source { get; init; } = "market";
    public string? Version { get; init; }
    public string? Commit { get; init; }
    public bool? Enabled { get; init; }
    public Guid? ProjectId { get; init; }
    public IReadOnlyDictionary<string, string?>? ExpectedFileHashes { get; init; }
    public string? ExpectedPackageHash { get; init; }
    public string? ExpectedPackageDigest { get; init; }
}

public sealed class ToolSkillProjectPackageDto
{
    public string Action { get; init; } = "save";
    public string? PackageRoot { get; init; }
    public string Name { get; init; } = "";
    public string Content { get; init; } = "";
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string?> ExpectedFileHashes { get; init; } = new Dictionary<string, string?>();
}


