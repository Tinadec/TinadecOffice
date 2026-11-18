using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public sealed record ToolSettingsWriteDto(JsonElement Settings);

public sealed class ToolSettingsSchemaDto
{
    public int SchemaVersion { get; init; } = 1;
    public JsonElement Schema { get; init; }
    public JsonElement Defaults { get; init; }
    public ToolHostSettingsDto HostSettings { get; init; } = new();
}
public sealed class ToolHostSettingsDto
{
    public bool Managed { get; init; } = true;
    public string? ExecutablePath { get; init; }
    public string? StartupTimeoutSeconds { get; init; }
    public string? DefaultTimeoutSeconds { get; init; }
    public string? DefaultWorkspaceRoot { get; init; }
}
public sealed class ToolCapabilitiesDto
{
    public string Status { get; init; } = "unavailable";
    public string? Reason { get; init; }
    public JsonElement? Capabilities { get; init; }
    public string? SettingsHash { get; init; }
}
public sealed class McpResourceTestDto
{
    public Guid ResourceId { get; init; }
    public string Status { get; init; } = "unknown";
    public string? Reason { get; init; }
    public int? ToolCount { get; init; }
}

public sealed class ToolSettingsDocumentDto
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public Guid? AgentDefinitionId { get; init; }
    public JsonElement Settings { get; init; }
    public JsonElement EffectiveSettings { get; init; }
    public string SettingsHash { get; init; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class McpResourceWriteDto
{
    public Guid? ProjectId { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    /// <summary><c>stdio</c> (default), <c>http</c> (Streamable HTTP) or <c>sse</c>.</summary>
    public string Transport { get; init; } = "stdio";
    public string? Url { get; init; }
    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string?>? Env { get; init; }
    /// <summary>Header name → value. A value of <c>********</c> preserves the stored secret.</summary>
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public string? Cwd { get; init; }
}

public sealed class McpResourceDto
{
    public Guid ResourceId { get; init; }
    public Guid? ProjectId { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Transport { get; init; } = "stdio";
    public string? Url { get; init; }
    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string?>? Env { get; init; }
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public string? Cwd { get; init; }
    public long Revision { get; init; }
    public string ConfigurationHash { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
    public string? ImportSource { get; init; }
}

public sealed class ToolMcpServerSnapshotDto
{
    [JsonPropertyName("resource_id")] public Guid ResourceId { get; init; }
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("transport")] public string Transport { get; init; } = "stdio";
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("command")] public string Command { get; init; } = string.Empty;
    [JsonPropertyName("args")] public IReadOnlyList<string> Args { get; init; } = [];
    // Versioned secret references are stored in the run freeze; plaintext is resolved only for the wire call.
    [JsonPropertyName("env")] public IReadOnlyDictionary<string, string?>? Env { get; init; }
    [JsonPropertyName("headers")] public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    [JsonPropertyName("secret_references")] public IReadOnlyDictionary<string, string>? SecretReferences { get; init; }
    [JsonPropertyName("header_secret_references")] public IReadOnlyDictionary<string, string>? HeaderSecretReferences { get; init; }
    [JsonPropertyName("cwd")] public string? Cwd { get; init; }
    [JsonPropertyName("revision")] public long Revision { get; init; }
    [JsonPropertyName("configuration_hash")] public string ConfigurationHash { get; init; } = string.Empty;
    [JsonPropertyName("program_root")] public string? ProgramRoot { get; init; }
    [JsonPropertyName("program_hash")] public string? ProgramHash { get; init; }
    [JsonPropertyName("program_status")] public string ProgramStatus { get; init; } = "external";
}

public sealed class ToolReadRootDto
{
    [JsonPropertyName("resource_id")] public Guid ResourceId { get; init; }
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
}

public sealed class ToolSkillSnapshotDto
{
    public Guid ResourceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public string SkillPath { get; init; } = string.Empty;
    public long Revision { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    /// <summary>builtin / user / project / compat / scope — the root the body was resolved from.</summary>
    public string SourceKind { get; init; } = "scope";
    /// <summary>Where relative resources resolve from. Empty for an on-disk package; a
    /// <c>tinadec://builtin/&lt;name&gt;</c> URI for an embedded package.</summary>
    public string ResourceBase { get; init; } = string.Empty;
    public string? WhenToUse { get; init; }
}

public sealed class ToolResourceDiagnosticDto
{
    public string Kind { get; init; } = string.Empty;
    public Guid? ResourceId { get; init; }
    public string Status { get; init; } = "missing";
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Host-authored immutable context, separate from model-supplied tool parameters.</summary>
public sealed class ToolExecutionContextDto
{
    [JsonPropertyName("workspace_roots")] public IReadOnlyList<ToolWorkspaceRootDto> WorkspaceRoots { get; init; } = [];
    [JsonPropertyName("workspace_files")] public IReadOnlyList<ToolWorkspaceFileDto> WorkspaceFiles { get; init; } = [];
    [JsonPropertyName("primary_root_id")] public string? PrimaryRootId { get; init; }
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("run_id")] public string? RunId { get; init; }
    [JsonPropertyName("storage_id")] public string? StorageId { get; init; }
    [JsonPropertyName("working_directory")] public string? WorkingDirectory { get; init; }
    [JsonPropertyName("storage_root")] public string? StorageRoot { get; init; }
    [JsonPropertyName("project_root")] public string? ProjectRoot { get; init; }
    [JsonPropertyName("project_storage_write")] public bool ProjectStorageWrite { get; init; }
    [JsonPropertyName("protected_storage_roots")] public IReadOnlyList<string> ProtectedStorageRoots { get; init; } = [];
    [JsonPropertyName("agent_definition_id")] public Guid? AgentDefinitionId { get; init; }
    [JsonPropertyName("settings_hash")] public string SettingsHash { get; init; } = string.Empty;
    [JsonPropertyName("settings")] public JsonElement Settings { get; init; }
    [JsonPropertyName("mcp_servers")] public IReadOnlyList<ToolMcpServerSnapshotDto> McpServers { get; init; } = [];
    [JsonPropertyName("read_roots")] public IReadOnlyList<ToolReadRootDto> ReadRoots { get; init; } = [];
    [JsonPropertyName("allowed_tool_ids")] public IReadOnlyList<string> AllowedToolIds { get; init; } = [];
    [JsonPropertyName("skill_resources")] public IReadOnlyList<ToolSkillSnapshotDto> SkillResources { get; init; } = [];
    [JsonPropertyName("mcp_import_error")] public string? McpImportError { get; init; }
    [JsonPropertyName("resource_diagnostics")] public IReadOnlyList<ToolResourceDiagnosticDto> ResourceDiagnostics { get; init; } = [];
}

public sealed class ToolWorkspaceRootDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
}

public sealed class ToolWorkspaceFileDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
}

public sealed class FrozenToolConfigurationDto
{
    public int SchemaVersion { get; init; } = 1;
    public long SharedRevision { get; init; }
    public string ConfigurationHash { get; init; } = string.Empty;
    public ToolExecutionContextDto SharedContext { get; init; } = new();
    public IReadOnlyDictionary<Guid, ToolExecutionContextDto> AgentContexts { get; init; } = new Dictionary<Guid, ToolExecutionContextDto>();
}
