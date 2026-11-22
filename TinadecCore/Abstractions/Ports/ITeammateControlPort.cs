using System.Text.Json.Serialization;

namespace TinadecCore.Abstractions.Ports;

public sealed class TeammateControlException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record TeammateSnapshotDto
{
    [JsonPropertyName("handle")]
    public string Handle { get; init; } = string.Empty;

    [JsonPropertyName("agent_slug")]
    public string AgentSlug { get; init; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "teammate";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "working";

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }

    [JsonPropertyName("awaiting_approval")]
    public bool AwaitingApproval { get; init; }

    [JsonPropertyName("current_task_id")]
    public Guid? CurrentTaskId { get; init; }

    [JsonPropertyName("current_task_title")]
    public string? CurrentTaskTitle { get; init; }

    [JsonPropertyName("claimed_step_id")]
    public string? ClaimedStepId { get; init; }

    [JsonPropertyName("latest_summary")]
    public string? LatestSummary { get; init; }

    [JsonPropertyName("runtime_badge")]
    public string? RuntimeBadge { get; init; }

    [JsonPropertyName("worktree_path")]
    public string? WorktreePath { get; init; }

    [JsonPropertyName("branch_name")]
    public string? BranchName { get; init; }

    [JsonPropertyName("write_scope")]
    public IReadOnlyList<string> WriteScope { get; init; } = [];

    [JsonPropertyName("relay_depth")]
    public int RelayDepth { get; init; }

    [JsonPropertyName("pending_steer_count")]
    public int PendingSteerCount { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record TeammateListResponseDto
{
    [JsonPropertyName("run_id")]
    public Guid RunId { get; init; }

    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("run_status")]
    public string RunStatus { get; init; } = string.Empty;

    [JsonPropertyName("teammates")]
    public IReadOnlyList<TeammateSnapshotDto> Teammates { get; init; } = [];
}

public sealed record TeammateSteerRequestDto
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("urgent")]
    public bool? Urgent { get; init; }

    [JsonPropertyName("client_control_id")]
    public string? ClientControlId { get; init; }
}

public sealed record TeammateSteerReceiptDto
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; init; }

    [JsonPropertyName("handle")]
    public string Handle { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }

    [JsonPropertyName("urgent")]
    public bool Urgent { get; init; }

    [JsonPropertyName("woken")]
    public bool Woken { get; init; }

    [JsonPropertyName("delivery_mode")]
    public string DeliveryMode { get; init; } = "injected_next_turn";

    [JsonPropertyName("board_seq")]
    public long BoardSeq { get; init; }

    [JsonPropertyName("pending_steer_count")]
    public int PendingSteerCount { get; init; }

    [JsonPropertyName("task_id")]
    public Guid? TaskId { get; init; }

    [JsonPropertyName("delivered_at")]
    public DateTimeOffset DeliveredAt { get; init; }
}

public sealed record TeammatePauseRequestDto
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record TeammateControlReceiptDto
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; init; }

    [JsonPropertyName("handle")]
    public string Handle { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }

    [JsonPropertyName("current_task_id")]
    public Guid? CurrentTaskId { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Direct human-to-teammate steering, per-teammate pause/resume, and runtime snapshot port (#50).
/// </summary>
public interface ITeammateControlPort
{
    Task<TeammateListResponseDto> ListTeammatesAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<TeammateSteerReceiptDto> SteerTeammateAsync(
        Guid runId,
        string handle,
        TeammateSteerRequestDto request,
        CancellationToken cancellationToken = default);

    Task<TeammateControlReceiptDto> PauseTeammateAsync(
        Guid runId,
        string handle,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<TeammateControlReceiptDto> ResumeTeammateAsync(
        Guid runId,
        string handle,
        CancellationToken cancellationToken = default);
}
