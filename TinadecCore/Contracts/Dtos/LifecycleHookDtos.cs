using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>Supported lifecycle hook event names.</summary>
public static class LifecycleHookEvents
{
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string OnTaskComplete = "OnTaskComplete";
    public const string OnSessionIdle = "OnSessionIdle";

    public static readonly IReadOnlyList<string> All =
    [
        PreToolUse,
        PostToolUse,
        OnTaskComplete,
        OnSessionIdle,
    ];

    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        foreach (var known in All)
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase))
                return known;
        }
        throw new ArgumentException($"Unsupported lifecycle hook event '{value}'. Allowed: {string.Join(", ", All)}.");
    }
}

public sealed record LifecycleHookEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("event")]
    public string Event { get; init; } = LifecycleHookEvents.PreToolUse;

    [JsonPropertyName("matcher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Matcher { get; init; }

    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = 5000;

    [JsonPropertyName("blocking")]
    public bool Blocking { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

public sealed record LifecycleHooksDocumentDto
{
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "workspace";

    [JsonPropertyName("workspace_root")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceRoot { get; init; }

    [JsonPropertyName("hooks")]
    public IReadOnlyList<LifecycleHookEntryDto> Hooks { get; init; } = [];

    [JsonPropertyName("revision")]
    public long Revision { get; init; }
}

public sealed record LifecycleHooksWriteRequestDto
{
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("workspace_root")]
    public string? WorkspaceRoot { get; init; }

    [JsonPropertyName("hooks")]
    public IReadOnlyList<LifecycleHookEntryDto> Hooks { get; init; } = [];

    [JsonPropertyName("expected_revision")]
    public long? ExpectedRevision { get; init; }
}
