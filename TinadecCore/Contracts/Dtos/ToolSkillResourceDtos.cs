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
    /// <summary>Which loader root produced this entry: <c>builtin</c>, <c>user</c>, <c>project</c>,
    /// <c>compat</c> or <c>scope</c>. <see cref="Scope"/> stays the write-model scope (shared/project)
    /// so existing clients and governed writes keep their meaning.</summary>
    public string SourceKind { get; init; } = "scope";
    /// <summary>Lower wins a duplicate name across roots.</summary>
    public int Rank { get; init; } = 100;
    /// <summary>When another root wins the same name, the order reason (empty when this entry wins).</summary>
    public string? ShadowedBy { get; init; }
    /// <summary>Built-ins and compatibility roots are served read-only.</summary>
    public bool ReadOnly { get; init; }
    public string? WhenToUse { get; init; }
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

public sealed class ToolSkillEnabledRequestDto
{
    public bool? Enabled { get; init; }
    public Guid? ProjectId { get; init; }
}

public sealed class ToolSkillInstallRequestDto
{
    /// <summary><c>inline</c>, <c>local_dir</c>, <c>git_url</c>, or <c>external_cli_import</c>.</summary>
    public string SourceType { get; init; } = "inline";
    public string Scope { get; init; } = "shared";
    public Guid? ProjectId { get; init; }
    public string? Name { get; init; }
    public string? Content { get; init; }
    public IReadOnlyDictionary<string, string>? Files { get; init; }
    public string? LocalPath { get; init; }
    public string? GitUrl { get; init; }
    public string? Subpath { get; init; }
    public IReadOnlyList<string>? ItemIds { get; init; }
    public IReadOnlyList<string>? Ecosystems { get; init; }
    public bool? Enabled { get; init; }
    public bool? EnableStdioMcp { get; init; }
    public bool Overwrite { get; init; }
    public bool Governed { get; init; }
}

public sealed class ToolSkillInstallResultDto
{
    public string SourceType { get; init; } = string.Empty;
    public IReadOnlyList<ToolSkillResourceDto> InstalledSkills { get; init; } = [];
    public IReadOnlyList<McpResourceDto> InstalledMcpServers { get; init; } = [];
    public IReadOnlyList<ExternalCliImportedCommandDto> InstalledCommands { get; init; } = [];
    public IReadOnlyList<ExternalCliSkippedItemDto> Skipped { get; init; } = [];
}

public sealed class ExternalCliEcosystemStatusDto
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool Detected { get; init; }
    public IReadOnlyList<string> PathsScanned { get; init; } = [];
}

public sealed class ExternalCliDiscoveredItemDto
{
    public string ItemId { get; init; } = string.Empty;
    public string Ecosystem { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string? Transport { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? Url { get; init; }
    public IReadOnlyList<string> EnvKeys { get; init; } = [];
    public IReadOnlyList<string> HeaderKeys { get; init; } = [];
    public bool HasSecrets { get; init; }
    public bool DefaultEnabled { get; init; }
    public bool AlreadyImported { get; init; }
    public string? ContentPreview { get; init; }
}

public sealed class ExternalCliDiscoveryDto
{
    public string UserHome { get; init; } = string.Empty;
    public string? WorkspaceRoot { get; init; }
    public IReadOnlyList<ExternalCliEcosystemStatusDto> Ecosystems { get; init; } = [];
    public IReadOnlyList<ExternalCliDiscoveredItemDto> Items { get; init; } = [];
}

public sealed class ExternalCliImportRequestDto
{
    public IReadOnlyList<string> ItemIds { get; init; } = [];
    public Guid? ProjectId { get; init; }
    public string Scope { get; init; } = "shared";
    public bool EnableStdioMcp { get; init; }
    public bool Overwrite { get; init; }
}

public sealed class ExternalCliImportedCommandDto
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Ecosystem { get; init; } = string.Empty;
}

public sealed class ExternalCliSkippedItemDto
{
    public string ItemId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class ExternalCliImportResultDto
{
    public IReadOnlyList<ToolSkillResourceDto> InstalledSkills { get; init; } = [];
    public IReadOnlyList<McpResourceDto> InstalledMcpServers { get; init; } = [];
    public IReadOnlyList<ExternalCliImportedCommandDto> InstalledCommands { get; init; } = [];
    public IReadOnlyList<ExternalCliSkippedItemDto> Skipped { get; init; } = [];
}


