using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public static class ProjectBoardStorageModes
{
    public const string RepoTinadec = "repo_tinadec";
    public const string AppData = "app_data";
    public const string CustomDir = "custom_dir";

    public static bool IsValid(string? mode) =>
        mode is RepoTinadec or AppData or CustomDir;
}

public static class ProjectBoardTaskStatuses
{
    public const string Backlog = "backlog";
    public const string Ready = "ready";
    public const string InProgress = "in_progress";
    public const string InReview = "in_review";
    public const string Done = "done";

    public static readonly string[] All = [Backlog, Ready, InProgress, InReview, Done];

    public static bool IsValid(string? status) =>
        status is Backlog or Ready or InProgress or InReview or Done;
}

public static class ProjectBoardPriorities
{
    public const string Urgent = "urgent";
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
    public const string None = "none";

    public static bool IsValid(string? priority) =>
        priority is Urgent or High or Medium or Low or None;
}

public static class ProjectBoardAssigneeKinds
{
    public const string Human = "human";
    public const string Native = "native";
    public const string Cli = "cli";
    public const string Coordinator = "coordinator";

    public static bool IsValid(string? kind) =>
        kind is Human or Native or Cli or Coordinator;
}

public sealed class ProjectBoardSettingsDto
{
    [JsonPropertyName("project_id")]
    public Guid ProjectId { get; init; }

    [JsonPropertyName("key_prefix")]
    public string KeyPrefix { get; init; } = "TIN";

    [JsonPropertyName("next_sequence")]
    public long NextSequence { get; init; } = 1;

    [JsonPropertyName("storage_mode")]
    public string StorageMode { get; init; } = ProjectBoardStorageModes.RepoTinadec;

    [JsonPropertyName("custom_sync_root")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CustomSyncRoot { get; init; }

    [JsonPropertyName("resolved_board_dir")]
    public string ResolvedBoardDir { get; init; } = string.Empty;

    [JsonPropertyName("project_instructions")]
    public string ProjectInstructions { get; init; } = string.Empty;

    [JsonPropertyName("default_assignee_kind")]
    public string DefaultAssigneeKind { get; init; } = ProjectBoardAssigneeKinds.Native;

    [JsonPropertyName("max_concurrency")]
    public int MaxConcurrency { get; init; } = 2;

    [JsonPropertyName("autopilot_enabled")]
    public bool AutopilotEnabled { get; init; }

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}

public sealed class UpdateProjectBoardSettingsRequest
{
    private string? _customSyncRoot;

    [JsonIgnore]
    public bool HasCustomSyncRoot { get; private set; }

    [JsonPropertyName("key_prefix")]
    public string? KeyPrefix { get; init; }

    [JsonPropertyName("storage_mode")]
    public string? StorageMode { get; init; }

    [JsonPropertyName("custom_sync_root")]
    public string? CustomSyncRoot
    {
        get => _customSyncRoot;
        set
        {
            _customSyncRoot = value;
            HasCustomSyncRoot = true;
        }
    }

    [JsonPropertyName("project_instructions")]
    public string? ProjectInstructions { get; init; }

    [JsonPropertyName("default_assignee_kind")]
    public string? DefaultAssigneeKind { get; init; }

    [JsonPropertyName("max_concurrency")]
    public int? MaxConcurrency { get; init; }

    [JsonPropertyName("autopilot_enabled")]
    public bool? AutopilotEnabled { get; init; }
}

public sealed class ProjectBoardChecklistItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("done")]
    public bool Done { get; init; }
}

public sealed class ProjectBoardAssigneeDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ProjectBoardAssigneeKinds.Human;

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("mode_version_id")]
    public Guid? ModeVersionId { get; init; }

    [JsonPropertyName("harness_id")]
    public string? HarnessId { get; init; }
}

public sealed class ProjectBoardSpatialRefDto
{
    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("object_id")]
    public string ObjectId { get; init; } = string.Empty;

    [JsonPropertyName("viewport_hint")]
    public JsonElement? ViewportHint { get; init; }
}

public sealed class ProjectBoardDiffStatsDto
{
    [JsonPropertyName("added")]
    public int Added { get; init; }

    [JsonPropertyName("removed")]
    public int Removed { get; init; }

    [JsonPropertyName("files")]
    public int Files { get; init; }
}

public sealed class ProjectBoardTaskSnapshotDto
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description_md")]
    public string DescriptionMd { get; init; } = string.Empty;

    [JsonPropertyName("checklist")]
    public List<ProjectBoardChecklistItemDto> Checklist { get; init; } = [];

    [JsonPropertyName("status")]
    public string Status { get; init; } = ProjectBoardTaskStatuses.Backlog;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = ProjectBoardPriorities.None;

    [JsonPropertyName("blocked")]
    public bool Blocked { get; init; }

    [JsonPropertyName("blocked_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BlockedReason { get; init; }

    [JsonPropertyName("assignee")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ProjectBoardAssigneeDto? Assignee { get; init; }

    [JsonPropertyName("parent_task_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? ParentTaskId { get; init; }

    [JsonPropertyName("depends_on")]
    public List<Guid> DependsOn { get; init; } = [];

    [JsonPropertyName("labels")]
    public List<string> Labels { get; init; } = [];

    [JsonPropertyName("spatial_ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ProjectBoardSpatialRefDto? SpatialRef { get; init; }

    [JsonPropertyName("sort_key")]
    public string SortKey { get; init; } = "V";

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}

public sealed class ProjectBoardSyncConflictDto
{
    [JsonPropertyName("file_rel_path")]
    public string FileRelPath { get; init; } = string.Empty;

    [JsonPropertyName("detected_at_unix_ms")]
    public long DetectedAtUnixMs { get; init; }

    [JsonPropertyName("file_snapshot")]
    public ProjectBoardTaskSnapshotDto FileSnapshot { get; init; } = new();

    [JsonPropertyName("board_snapshot")]
    public ProjectBoardTaskSnapshotDto BoardSnapshot { get; init; } = new();

    [JsonPropertyName("conflicting_fields")]
    public List<string> ConflictingFields { get; init; } = [];
}

public sealed class ProjectBoardTaskDto
{
    [JsonPropertyName("task_id")]
    public Guid TaskId { get; init; }

    [JsonPropertyName("project_id")]
    public Guid ProjectId { get; init; }

    [JsonPropertyName("project_name")]
    public string ProjectName { get; init; } = string.Empty;

    [JsonPropertyName("task_key")]
    public string TaskKey { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description_md")]
    public string DescriptionMd { get; init; } = string.Empty;

    [JsonPropertyName("checklist")]
    public List<ProjectBoardChecklistItemDto> Checklist { get; init; } = [];

    [JsonPropertyName("status")]
    public string Status { get; init; } = ProjectBoardTaskStatuses.Backlog;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = ProjectBoardPriorities.None;

    [JsonPropertyName("blocked")]
    public bool Blocked { get; init; }

    [JsonPropertyName("blocked_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BlockedReason { get; init; }

    [JsonPropertyName("assignee")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ProjectBoardAssigneeDto? Assignee { get; init; }

    [JsonPropertyName("parent_task_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? ParentTaskId { get; init; }

    [JsonPropertyName("depends_on")]
    public List<Guid> DependsOn { get; init; } = [];

    [JsonPropertyName("labels")]
    public List<string> Labels { get; init; } = [];

    [JsonPropertyName("spatial_ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ProjectBoardSpatialRefDto? SpatialRef { get; init; }

    [JsonPropertyName("sort_key")]
    public string SortKey { get; init; } = "V";

    [JsonPropertyName("active_session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? ActiveSessionId { get; init; }

    [JsonPropertyName("active_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? ActiveRunId { get; init; }

    [JsonPropertyName("run_status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RunStatus { get; init; }

    [JsonPropertyName("worktree_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? WorktreePath { get; init; }

    [JsonPropertyName("lease_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? LeaseId { get; init; }

    [JsonPropertyName("live_digest_summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LiveDigestSummary { get; init; }

    [JsonPropertyName("diff_stats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ProjectBoardDiffStatsDto? DiffStats { get; init; }

    [JsonPropertyName("has_sync_conflict")]
    public bool HasSyncConflict { get; init; }

    [JsonPropertyName("sync_conflict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ProjectBoardSyncConflictDto? SyncConflict { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; init; }

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}

public sealed class CreateProjectBoardTaskRequest
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description_md")]
    public string? DescriptionMd { get; init; }

    [JsonPropertyName("checklist")]
    public List<ProjectBoardChecklistItemDto>? Checklist { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("blocked")]
    public bool? Blocked { get; init; }

    [JsonPropertyName("blocked_reason")]
    public string? BlockedReason { get; init; }

    [JsonPropertyName("assignee")]
    public ProjectBoardAssigneeDto? Assignee { get; init; }

    [JsonPropertyName("parent_task_id")]
    public Guid? ParentTaskId { get; init; }

    [JsonPropertyName("depends_on")]
    public List<Guid>? DependsOn { get; init; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; init; }

    [JsonPropertyName("spatial_ref")]
    public ProjectBoardSpatialRefDto? SpatialRef { get; init; }

    [JsonPropertyName("sort_key")]
    public string? SortKey { get; init; }
}

public sealed class UpdateProjectBoardTaskRequest
{
    private string? _blockedReason;
    private ProjectBoardAssigneeDto? _assignee;
    private Guid? _parentTaskId;
    private ProjectBoardSpatialRefDto? _spatialRef;

    [JsonIgnore]
    public bool HasBlockedReason { get; private set; }

    [JsonIgnore]
    public bool HasAssignee { get; private set; }

    [JsonIgnore]
    public bool HasParentTaskId { get; private set; }

    [JsonIgnore]
    public bool HasSpatialRef { get; private set; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description_md")]
    public string? DescriptionMd { get; init; }

    [JsonPropertyName("checklist")]
    public List<ProjectBoardChecklistItemDto>? Checklist { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("blocked")]
    public bool? Blocked { get; init; }

    [JsonPropertyName("blocked_reason")]
    public string? BlockedReason
    {
        get => _blockedReason;
        set
        {
            _blockedReason = value;
            HasBlockedReason = true;
        }
    }

    [JsonPropertyName("assignee")]
    public ProjectBoardAssigneeDto? Assignee
    {
        get => _assignee;
        set
        {
            _assignee = value;
            HasAssignee = true;
        }
    }

    [JsonPropertyName("parent_task_id")]
    public Guid? ParentTaskId
    {
        get => _parentTaskId;
        set
        {
            _parentTaskId = value;
            HasParentTaskId = true;
        }
    }

    [JsonPropertyName("depends_on")]
    public List<Guid>? DependsOn { get; init; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; init; }

    [JsonPropertyName("spatial_ref")]
    public ProjectBoardSpatialRefDto? SpatialRef
    {
        get => _spatialRef;
        set
        {
            _spatialRef = value;
            HasSpatialRef = true;
        }
    }

    [JsonPropertyName("sort_key")]
    public string? SortKey { get; init; }

    [JsonPropertyName("expected_revision")]
    public long? ExpectedRevision { get; init; }
}

public sealed class ProjectBoardViewFiltersDto
{
    [JsonPropertyName("project_ids")]
    public List<Guid>? ProjectIds { get; init; }

    [JsonPropertyName("assignee_kinds")]
    public List<string>? AssigneeKinds { get; init; }

    [JsonPropertyName("statuses")]
    public List<string>? Statuses { get; init; }

    [JsonPropertyName("priorities")]
    public List<string>? Priorities { get; init; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; init; }

    [JsonPropertyName("needs_my_action")]
    public bool? NeedsMyAction { get; init; }

    [JsonPropertyName("has_conflict")]
    public bool? HasConflict { get; init; }

    [JsonPropertyName("query")]
    public string? Query { get; init; }
}

public sealed class ProjectBoardViewConfigDto
{
    [JsonPropertyName("layout")]
    public string Layout { get; init; } = "kanban";

    [JsonPropertyName("group_by")]
    public string GroupBy { get; init; } = "status";

    [JsonPropertyName("swimlane_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SwimlaneBy { get; init; }

    [JsonPropertyName("sort_by")]
    public string SortBy { get; init; } = "manual";

    [JsonPropertyName("filters")]
    public ProjectBoardViewFiltersDto Filters { get; init; } = new();

    [JsonPropertyName("visible_fields")]
    public List<string> VisibleFields { get; init; } = [];
}

public sealed class ProjectBoardViewDto
{
    [JsonPropertyName("view_id")]
    public Guid ViewId { get; init; }

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "global";

    [JsonPropertyName("project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? ProjectId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("config")]
    public ProjectBoardViewConfigDto Config { get; init; } = new();

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; init; }

    [JsonPropertyName("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; init; }

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}

public sealed class CreateProjectBoardViewRequest
{
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("project_id")]
    public Guid? ProjectId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("config")]
    public ProjectBoardViewConfigDto? Config { get; init; }

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; init; }
}

public sealed class UpdateProjectBoardViewRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("config")]
    public ProjectBoardViewConfigDto? Config { get; init; }

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; init; }
}

public sealed class ProjectBoardProjectSummaryItemDto
{
    [JsonPropertyName("project_id")]
    public Guid ProjectId { get; init; }

    [JsonPropertyName("project_name")]
    public string ProjectName { get; init; } = string.Empty;

    [JsonPropertyName("total_tasks")]
    public int TotalTasks { get; init; }

    [JsonPropertyName("in_progress_count")]
    public int InProgressCount { get; init; }

    [JsonPropertyName("in_review_count")]
    public int InReviewCount { get; init; }

    [JsonPropertyName("blocked_count")]
    public int BlockedCount { get; init; }

    [JsonPropertyName("conflict_count")]
    public int ConflictCount { get; init; }

    [JsonPropertyName("needs_action_count")]
    public int NeedsActionCount { get; init; }
}

public sealed class ProjectBoardSummaryDto
{
    [JsonPropertyName("total_tasks")]
    public int TotalTasks { get; init; }

    [JsonPropertyName("backlog_count")]
    public int BacklogCount { get; init; }

    [JsonPropertyName("ready_count")]
    public int ReadyCount { get; init; }

    [JsonPropertyName("in_progress_count")]
    public int InProgressCount { get; init; }

    [JsonPropertyName("in_review_count")]
    public int InReviewCount { get; init; }

    [JsonPropertyName("done_count")]
    public int DoneCount { get; init; }

    [JsonPropertyName("blocked_count")]
    public int BlockedCount { get; init; }

    [JsonPropertyName("conflict_count")]
    public int ConflictCount { get; init; }

    [JsonPropertyName("active_run_count")]
    public int ActiveRunCount { get; init; }

    [JsonPropertyName("needs_action_count")]
    public int NeedsActionCount { get; init; }

    [JsonPropertyName("projects")]
    public List<ProjectBoardProjectSummaryItemDto> Projects { get; init; } = [];

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}

public sealed class ProjectBoardSyncResultDto
{
    [JsonPropertyName("project_id")]
    public Guid ProjectId { get; init; }

    [JsonPropertyName("storage_mode")]
    public string StorageMode { get; init; } = ProjectBoardStorageModes.RepoTinadec;

    [JsonPropertyName("resolved_board_dir")]
    public string ResolvedBoardDir { get; init; } = string.Empty;

    [JsonPropertyName("imported_count")]
    public int ImportedCount { get; init; }

    [JsonPropertyName("exported_count")]
    public int ExportedCount { get; init; }

    [JsonPropertyName("merged_count")]
    public int MergedCount { get; init; }

    [JsonPropertyName("unchanged_count")]
    public int UnchangedCount { get; init; }

    [JsonPropertyName("conflict_count")]
    public int ConflictCount { get; init; }

    [JsonPropertyName("synced_at_unix_ms")]
    public long SyncedAtUnixMs { get; init; }
}

public sealed class ResolveProjectBoardConflictRequest
{
    [JsonPropertyName("resolution")]
    public string Resolution { get; init; } = string.Empty;
}

public sealed class DispatchProjectBoardTaskRequest
{
    [JsonPropertyName("assignee_override")]
    public ProjectBoardAssigneeDto? AssigneeOverride { get; init; }

    [JsonPropertyName("use_worktree")]
    public bool? UseWorktree { get; init; }

    [JsonPropertyName("extra_prompt")]
    public string? ExtraPrompt { get; init; }
}

public sealed class ProjectBoardDiffFileDto
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "modified";

    [JsonPropertyName("added")]
    public int Added { get; init; }

    [JsonPropertyName("removed")]
    public int Removed { get; init; }
}

public sealed class ProjectBoardTaskDiffDto
{
    [JsonPropertyName("worktree_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? WorktreePath { get; init; }

    [JsonPropertyName("diff_text")]
    public string DiffText { get; init; } = string.Empty;

    [JsonPropertyName("files")]
    public List<ProjectBoardDiffFileDto> Files { get; init; } = [];
}

public sealed class ApproveProjectBoardTaskRequest
{
    [JsonPropertyName("merge_or_clean_worktree")]
    public bool? MergeOrCleanWorktree { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

public sealed class RequestTaskChangesRequest
{
    [JsonPropertyName("feedback")]
    public string Feedback { get; init; } = string.Empty;
}

public sealed class ProjectBoardEventDto
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("project_id")]
    public Guid? ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public Guid? TaskId { get; init; }

    [JsonPropertyName("task")]
    public ProjectBoardTaskDto? Task { get; init; }

    [JsonPropertyName("settings")]
    public ProjectBoardSettingsDto? Settings { get; init; }

    [JsonPropertyName("view_id")]
    public Guid? ViewId { get; init; }

    [JsonPropertyName("view")]
    public ProjectBoardViewDto? View { get; init; }

    [JsonPropertyName("occurred_at_unix_ms")]
    public long OccurredAtUnixMs { get; init; }
}
