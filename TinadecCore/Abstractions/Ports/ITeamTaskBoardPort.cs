using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Protocol constants and tool manifest schema for the decentralized claimable task board (<c>team_tasks</c>, #50).
/// </summary>
public static class TeamTaskBoardProtocol
{
    public const string TeamTasksToolId = "team_tasks";

    public static bool IsTeamTasks(string? toolId) =>
        string.Equals(toolId, TeamTasksToolId, StringComparison.OrdinalIgnoreCase);

    private const string SchemaJson =
        "{\"type\":\"object\",\"properties\":{"
        + "\"action\":{\"type\":\"string\",\"enum\":[\"list\",\"claim\",\"complete\",\"release\"],\"description\":\"Task board operation: 'list' ready/all tasks, atomically 'claim' an unclaimed ready task, 'complete' a claimed task to unlock downstream dependents, or 'release' a blocked/unneeded task back to the pool.\"},"
        + "\"step_id\":{\"type\":\"string\",\"description\":\"Target step identifier (e.g. 's1', 'step-api'). Optional for 'claim' (auto-picks next ready unclaimed task) and ignored for 'list'.\"},"
        + "\"only_ready\":{\"type\":\"boolean\",\"description\":\"For 'list': when true (default), returns only unclaimed pending steps whose 'depends_on' prerequisites are all completed.\"},"
        + "\"expected_revision\":{\"type\":\"integer\",\"description\":\"Optional optimistic concurrency revision from a prior 'list' call.\"},"
        + "\"summary\":{\"type\":\"string\",\"description\":\"Completion outcome or release reason note.\"},"
        + "\"evidence\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Checkable completion evidence paths, test commands, or commit refs for 'complete'.\"},"
        + "\"mark_blocked\":{\"type\":\"boolean\",\"description\":\"For 'release': when true, marks the step 'blocked' instead of returning it to 'pending'.\"}"
        + "},\"required\":[\"action\"],\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = TeamTasksToolId,
        Description = "Decentralized atomic task board for teammates: list dependency-ready tasks (`list`), atomically claim a task without double-assignment (`claim`), complete a task and unlock downstream dependents (`complete`), or release a task back to the pool (`release`).",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(SchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        RetrySafety = "safe",
        ConfirmationFields = [],
    };
}

public sealed record TeamTaskBoardItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("phase_id")]
    public string PhaseId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending";

    [JsonPropertyName("depends_on")]
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    [JsonPropertyName("claimed_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaimedBy { get; init; }

    [JsonPropertyName("assigned_agent_slug")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssignedAgentSlug { get; init; }

    [JsonPropertyName("claimed_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ClaimedAt { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }
}

public sealed record TeamTaskBoardListResult
{
    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("ready_count")]
    public int ReadyCount { get; init; }

    [JsonPropertyName("in_progress_count")]
    public int InProgressCount { get; init; }

    [JsonPropertyName("completed_count")]
    public int CompletedCount { get; init; }

    [JsonPropertyName("blocked_count")]
    public int BlockedCount { get; init; }

    [JsonPropertyName("tasks")]
    public IReadOnlyList<TeamTaskBoardItemDto> Tasks { get; init; } = [];
}

public sealed record TeamTaskClaimResult
{
    [JsonPropertyName("claimed")]
    public bool Claimed { get; init; }

    [JsonPropertyName("step_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepId { get; init; }

    [JsonPropertyName("claimed_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaimedBy { get; init; }

    [JsonPropertyName("already_claimed_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AlreadyClaimedBy { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TeamTaskBoardItemDto? Task { get; init; }
}

public sealed record TeamTaskCompleteResult
{
    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    [JsonPropertyName("step_id")]
    public string StepId { get; init; } = string.Empty;

    [JsonPropertyName("completed_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletedBy { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TeamTaskBoardItemDto? Task { get; init; }

    [JsonPropertyName("unlocked_ready_tasks")]
    public IReadOnlyList<TeamTaskBoardItemDto> UnlockedReadyTasks { get; init; } = [];
}

public sealed record TeamTaskReleaseResult
{
    [JsonPropertyName("released")]
    public bool Released { get; init; }

    [JsonPropertyName("step_id")]
    public string StepId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending";

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TeamTaskBoardItemDto? Task { get; init; }
}

/// <summary>
/// Atomic claimable task board port backed by <c>SessionPlanService</c> (#50).
/// </summary>
public interface ITeamTaskBoardPort
{
    Task<TeamTaskBoardListResult> ListTasksAsync(
        Guid sessionId,
        bool onlyReady = true,
        CancellationToken cancellationToken = default);

    Task<TeamTaskClaimResult> ClaimTaskAsync(
        Guid sessionId,
        string? stepId,
        string claimantHandle,
        long? expectedRevision = null,
        Guid? runId = null,
        CancellationToken cancellationToken = default);

    Task<TeamTaskCompleteResult> CompleteTaskAsync(
        Guid sessionId,
        string stepId,
        string claimantHandle,
        string? summary = null,
        IReadOnlyList<string>? evidence = null,
        Guid? runId = null,
        CancellationToken cancellationToken = default);

    Task<TeamTaskReleaseResult> ReleaseTaskAsync(
        Guid sessionId,
        string stepId,
        string claimantHandle,
        string? reason = null,
        bool markBlocked = false,
        Guid? runId = null,
        CancellationToken cancellationToken = default);
}
