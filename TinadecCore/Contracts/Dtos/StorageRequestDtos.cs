namespace TinadecCore.Contracts.Dtos;

/// <summary>HTTP input contracts for the Core-owned project and session storage surface.</summary>
public sealed class CreateProjectRequest
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class UpdateProjectRequest
{
    public string? Name { get; set; }
}

public sealed class CreateSessionRequest
{
    public string? ViewMode { get; set; }
    public string? ProjectId { get; set; }
    public string? Title { get; set; }
    public Guid? ModeVersionId { get; set; }
    public MeetingModelOverrideDto? MeetingModelOverride { get; set; }
    public string? PermissionMode { get; set; }
    public SpaceRunOptionsDto? SpaceOptions { get; set; }
    // ConversationIdentity (additive, optional): the mode node carrying the
    // conversation role. Omitted → resolved from the selected mode's designated
    // conversation node; provided → must satisfy the resolution tiers or the
    // request fails with conversation_identity_invalid. Frozen after creation —
    // immutable across mode switches.
    public string? ConversationNodeKey { get; set; }
    public string? Origin { get; set; }
    public Guid? BoardTaskId { get; set; }
}

public sealed class UpdateSessionRequest
{
    private SpaceRunOptionsDto? _spaceOptions;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasExplicitSpaceOptions { get; private set; }

    public string? Title { get; set; }
    public Guid? ModeVersionId { get; set; }
    public bool ClearModeVersion { get; set; }
    public MeetingModelOverrideDto? MeetingModelOverride { get; set; }
    public bool ClearMeetingModelOverride { get; set; }
    public string? PermissionMode { get; set; }
    public SpaceRunOptionsDto? SpaceOptions
    {
        get => _spaceOptions;
        set
        {
            _spaceOptions = value;
            HasExplicitSpaceOptions = true;
        }
    }
    public long? ExpectedSettingsRevision { get; set; }
}

/// <summary>Optional per-session or per-mode overrides for context governance parameters.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ContextPolicyParamsOverrideDto(
    string? RecallFidelity = null,
    double? CompactionTriggerRatio = null,
    int? RecentMessageLimit = null,
    bool? AllowCrossSessionRecall = null,
    string? EvolutionMode = null,
    int? CoreMarkdownMaxTokens = null);

/// <summary>Whole replacement of the current spatial session's choices for future tasks.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SpaceRunOptionsDto(
    bool PlanFirst = false,
    bool SpecEnabled = false,
    bool MultiAgent = false,
    Guid? WorkflowModeVersionId = null,
    bool BulletinBoard = false,
    bool Worktree = false,
    string? ContextStrategy = null,
    ContextPolicyParamsOverrideDto? ContextAdvanced = null);

public sealed class MigrateSessionRequest
{
    public string? TargetStorageId { get; set; }
    public string? TargetProjectId { get; set; }
    public string? ProjectName { get; set; }
    public string? ProjectPath { get; set; }
}

public sealed class CreateMessageRequest
{
    public string Content { get; set; } = string.Empty;
}
