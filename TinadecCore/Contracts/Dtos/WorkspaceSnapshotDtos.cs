using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public sealed class WorkspaceSnapshotCreateRequestDto
{
    [JsonPropertyName("project_id")]
    public Guid ProjectId { get; init; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("expected_workspace_hash")]
    public string? ExpectedWorkspaceHash { get; init; }

    [JsonPropertyName("include_hidden")]
    public bool IncludeHidden { get; init; }

    [JsonPropertyName("max_files")]
    public int MaxFiles { get; init; } = 10_000;

    [JsonPropertyName("max_bytes")]
    public long MaxBytes { get; init; } = 64 * 1024 * 1024;
}

public sealed class WorkspaceRestoreRequestDto
{
    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("expected_workspace_hash")]
    public string? ExpectedWorkspaceHash { get; init; }

    [JsonPropertyName("allow_conflicts")]
    public bool AllowConflicts { get; init; }
}

/// <summary>
/// Undoes one file from one snapshot. <c>expected_sha256</c> is the hash the caller was just
/// shown for that path: omitting it is refused, because a restore without it overwrites an edit
/// nobody looked at.
/// </summary>
public sealed class WorkspaceFileRestoreRequestDto
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("expected_sha256")]
    public string? ExpectedSha256 { get; init; }
}
