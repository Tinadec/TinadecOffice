using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;

namespace TinadecCore.DmaEA;

public sealed class SessionPlanDbContext : DbContext
{
    public SessionPlanDbContext(DbContextOptions<SessionPlanDbContext> options) : base(options) { }
    public DbSet<SessionPlanRecord> SessionPlans => Set<SessionPlanRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SessionPlanRecord>(entity =>
        {
            entity.ToTable("session_plans");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Goal).HasMaxLength(2000);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PlanJson).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkspaceId, x.SessionId }).IsUnique();
        });
        modelBuilder.UseTinadecSnakeCase();
    }
}

public sealed class SessionPlanRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Goal { get; set; }
    public string Status { get; set; } = SessionPlanStatuses.Draft;
    public long Revision { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByPrincipalId { get; set; }
    public Guid? LastRunId { get; set; }
    public string PlanJson { get; set; } = "{\"phases\":[]}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class SessionPlanStatuses
{
    public const string Draft = "draft";
    public const string Approved = "approved";
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Blocked = "blocked";

    public static readonly HashSet<string> AllPlanStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        Draft, Approved, Active, Completed, Blocked
    };

    public const string StepPending = "pending";
    public const string StepInProgress = "in_progress";
    public const string StepCompleted = "completed";
    public const string StepBlocked = "blocked";
    public const string StepFailed = "failed";
    public const string StepSkipped = "skipped";

    public static readonly HashSet<string> AllStepStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        StepPending, StepInProgress, StepCompleted, StepBlocked, StepFailed, StepSkipped
    };

    public static string MapTaskStatus(string? taskStatus) => taskStatus?.Trim().ToLowerInvariant() switch
    {
        "running" or "waiting" => StepInProgress,
        "completed" => StepCompleted,
        "blocked" => StepBlocked,
        "failed" or "cancelled" => StepFailed,
        "skipped" => StepSkipped,
        _ => StepPending
    };
}

public sealed class SessionPlanException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record SessionPlanArtifactDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "file";

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("ref_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefId { get; init; }
}

public sealed record SessionPlanStepDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = SessionPlanStatuses.StepPending;

    [JsonPropertyName("depends_on")]
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    [JsonPropertyName("bound_task_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundTaskKey { get; init; }

    [JsonPropertyName("assigned_agent_slug")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssignedAgentSlug { get; init; }

    [JsonPropertyName("verification_criteria")]
    public IReadOnlyList<string> VerificationCriteria { get; init; } = [];

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<SessionPlanArtifactDto> Artifacts { get; init; } = [];

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonPropertyName("updated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record SessionPlanPhaseDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("order")]
    public int Order { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = SessionPlanStatuses.StepPending;

    [JsonPropertyName("steps")]
    public IReadOnlyList<SessionPlanStepDto> Steps { get; init; } = [];
}

public sealed record SessionPlanProgressDto
{
    [JsonPropertyName("total_steps")]
    public int TotalSteps { get; init; }

    [JsonPropertyName("completed_steps")]
    public int CompletedSteps { get; init; }

    [JsonPropertyName("in_progress_steps")]
    public int InProgressSteps { get; init; }

    [JsonPropertyName("blocked_steps")]
    public int BlockedSteps { get; init; }

    [JsonPropertyName("failed_steps")]
    public int FailedSteps { get; init; }

    [JsonPropertyName("pending_steps")]
    public int PendingSteps { get; init; }
}

public sealed record SessionPlanDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("goal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Goal { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = SessionPlanStatuses.Draft;

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("approved_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ApprovedAt { get; init; }

    [JsonPropertyName("last_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LastRunId { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("phases")]
    public IReadOnlyList<SessionPlanPhaseDto> Phases { get; init; } = [];

    [JsonPropertyName("progress")]
    public SessionPlanProgressDto Progress { get; init; } = new();
}

public sealed record SessionPlanPutRequestDto
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("goal")]
    public string? Goal { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("phases")]
    public IReadOnlyList<SessionPlanPhaseDto> Phases { get; init; } = [];

    [JsonPropertyName("expected_revision")]
    public long? ExpectedRevision { get; init; }
}

public sealed record SessionPlanStepPatchDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("depends_on")]
    public IReadOnlyList<string>? DependsOn { get; init; }

    [JsonPropertyName("bound_task_key")]
    public string? BoundTaskKey { get; init; }

    [JsonPropertyName("assigned_agent_slug")]
    public string? AssignedAgentSlug { get; init; }

    [JsonPropertyName("verification_criteria")]
    public IReadOnlyList<string>? VerificationCriteria { get; init; }

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<SessionPlanArtifactDto>? Artifacts { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

public sealed record SessionPlanAddStepDto
{
    [JsonPropertyName("phase_id")]
    public string? PhaseId { get; init; }

    [JsonPropertyName("phase_title")]
    public string? PhaseTitle { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("depends_on")]
    public IReadOnlyList<string>? DependsOn { get; init; }

    [JsonPropertyName("bound_task_key")]
    public string? BoundTaskKey { get; init; }

    [JsonPropertyName("assigned_agent_slug")]
    public string? AssignedAgentSlug { get; init; }

    [JsonPropertyName("verification_criteria")]
    public IReadOnlyList<string>? VerificationCriteria { get; init; }
}

public sealed record SessionPlanPatchRequestDto
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("goal")]
    public string? Goal { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("phases")]
    public IReadOnlyList<SessionPlanPhaseDto>? Phases { get; init; }

    [JsonPropertyName("step_updates")]
    public IReadOnlyList<SessionPlanStepPatchDto>? StepUpdates { get; init; }

    [JsonPropertyName("add_steps")]
    public IReadOnlyList<SessionPlanAddStepDto>? AddSteps { get; init; }

    [JsonPropertyName("expected_revision")]
    public long? ExpectedRevision { get; init; }
}

public sealed record SessionPlanApproveRequestDto
{
    [JsonPropertyName("expected_revision")]
    public long? ExpectedRevision { get; init; }
}

internal sealed class SessionPlanDocument
{
    [JsonPropertyName("phases")]
    public List<SessionPlanPhaseDto> Phases { get; set; } = [];
}
