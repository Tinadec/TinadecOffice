using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiPreferencesAppearanceDto
{
    [JsonPropertyName("theme")]
    public string Theme { get; init; } = string.Empty;

    [JsonPropertyName("accent")]
    public string Accent { get; init; } = string.Empty;

    [JsonPropertyName("custom_accent_hex")]
    public string CustomAccentHex { get; init; } = string.Empty;

    [JsonPropertyName("panel_effect")]
    public string PanelEffect { get; init; } = string.Empty;

    [JsonPropertyName("workspace_view")]
    public string WorkspaceView { get; init; } = string.Empty;

    [JsonPropertyName("workspace_folder_tone")]
    public string WorkspaceFolderTone { get; init; } = string.Empty;

    public static UiPreferencesAppearanceDto CreateDefaults() => new()
    {
        Theme = "dark",
        Accent = "monochrome",
        CustomAccentHex = "#18181b",
        PanelEffect = "translucent",
        WorkspaceView = "grid",
        WorkspaceFolderTone = "amber",
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiPreferencesInteractionDto
{
    [JsonPropertyName("workspace_drop_action")]
    public string WorkspaceDropAction { get; init; } = string.Empty;

    [JsonPropertyName("spatial_promote_mode")]
    public string SpatialPromoteMode { get; init; } = string.Empty;

    [JsonPropertyName("reduced_motion")]
    public string ReducedMotion { get; init; } = string.Empty;

    public static UiPreferencesInteractionDto CreateDefaults() => new()
    {
        WorkspaceDropAction = "link",
        SpatialPromoteMode = "merge",
        ReducedMotion = "system",
    };
}

public sealed record UiPreferencesDocumentDto
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("file_path")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("content_hash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("appearance")]
    public UiPreferencesAppearanceDto Appearance { get; init; } = UiPreferencesAppearanceDto.CreateDefaults();

    [JsonPropertyName("interaction")]
    public UiPreferencesInteractionDto Interaction { get; init; } = UiPreferencesInteractionDto.CreateDefaults();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiPreferencesPutRequestDto
{
    [JsonPropertyName("expected_content_hash")]
    public string? ExpectedContentHash { get; init; }

    [JsonPropertyName("reset_defaults")]
    public bool? ResetDefaults { get; init; }

    [JsonPropertyName("appearance")]
    public UiPreferencesAppearanceDto? Appearance { get; init; }

    [JsonPropertyName("interaction")]
    public UiPreferencesInteractionDto? Interaction { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiPreferencesAppearancePatchDto
{
    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    [JsonPropertyName("accent")]
    public string? Accent { get; init; }

    [JsonPropertyName("custom_accent_hex")]
    public string? CustomAccentHex { get; init; }

    [JsonPropertyName("panel_effect")]
    public string? PanelEffect { get; init; }

    [JsonPropertyName("workspace_view")]
    public string? WorkspaceView { get; init; }

    [JsonPropertyName("workspace_folder_tone")]
    public string? WorkspaceFolderTone { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiPreferencesInteractionPatchDto
{
    [JsonPropertyName("workspace_drop_action")]
    public string? WorkspaceDropAction { get; init; }

    [JsonPropertyName("spatial_promote_mode")]
    public string? SpatialPromoteMode { get; init; }

    [JsonPropertyName("reduced_motion")]
    public string? ReducedMotion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiPreferencesPatchRequestDto
{
    [JsonPropertyName("expected_content_hash")]
    public string? ExpectedContentHash { get; init; }

    [JsonPropertyName("appearance")]
    public UiPreferencesAppearancePatchDto? Appearance { get; init; }

    [JsonPropertyName("interaction")]
    public UiPreferencesInteractionPatchDto? Interaction { get; init; }
}
