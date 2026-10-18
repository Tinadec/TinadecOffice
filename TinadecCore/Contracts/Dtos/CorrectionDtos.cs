using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// The declared, versioned runtime behavior of a mode. It is intentionally
/// separate from agent capabilities: agent capabilities authorize work, while a
/// runtime policy selects a host-owned workflow.
/// </summary>
public sealed class CorrectionRuntimePolicyDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("features")]
    public IReadOnlyList<string> Features { get; init; } = [];
}

public sealed class CorrectionTraceDto
{
    [JsonPropertyName("episode_id")]
    public Guid EpisodeId { get; init; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; init; }

    [JsonPropertyName("parent_run_id")]
    public Guid? ParentRunId { get; init; }

    [JsonPropertyName("revision")]
    public int Revision { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "OBSERVE";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "investigating";

    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("symptom")]
    public string Symptom { get; init; } = string.Empty;

    [JsonPropertyName("done_when")]
    public string DoneWhen { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("root_cause")]
    public string RootCause { get; init; } = "UNKNOWN";

    [JsonPropertyName("recheck")]
    public string? Recheck { get; init; }

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "low";

    [JsonPropertyName("intervention")]
    public string? Intervention { get; init; }

    [JsonPropertyName("prediction")]
    public string? Prediction { get; init; }

    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    [JsonPropertyName("fix")]
    public string? Fix { get; init; }

    [JsonPropertyName("verify")]
    public string? Verify { get; init; }

    [JsonPropertyName("hypotheses")]
    public List<CorrectionHypothesisDto> Hypotheses { get; init; } = [];

    [JsonPropertyName("evidence")]
    public List<CorrectionEvidenceDto> Evidence { get; init; } = [];

    [JsonPropertyName("interventions")]
    public List<CorrectionInterventionDto> Interventions { get; init; } = [];

    [JsonPropertyName("completion")]
    public CorrectionCompletionDto Completion { get; init; } = new();

    [JsonPropertyName("open_questions")]
    public List<CorrectionQuestionDto> OpenQuestions { get; init; } = [];

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class CorrectionHypothesisDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("statement")]
    public string Statement { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "testing";

    [JsonPropertyName("kill_criterion")]
    public string? KillCriterion { get; init; }

    [JsonPropertyName("evidence_ids")]
    public List<string> EvidenceIds { get; init; } = [];

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "low";
}

public sealed class CorrectionEvidenceDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "tool";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "observation";

    [JsonPropertyName("strength")]
    public string Strength { get; init; } = "inference";

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("execution_id")]
    public string? ExecutionId { get; init; }

    [JsonPropertyName("tool_id")]
    public string? ToolId { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class CorrectionInterventionDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("execution_id")]
    public string? ExecutionId { get; init; }

    [JsonPropertyName("tool_id")]
    public string? ToolId { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("revertible")]
    public bool Revertible { get; init; }

    [JsonPropertyName("reverted_at")]
    public DateTimeOffset? RevertedAt { get; init; }
}

public sealed class CorrectionCompletionDto
{
    [JsonPropertyName("requirement")]
    public string Requirement { get; init; } = "diagnostics";

    [JsonPropertyName("authority")]
    public string Authority { get; init; } = "machine";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "blocked";

    [JsonPropertyName("modified_artifacts")]
    public List<string> ModifiedArtifacts { get; init; } = [];

    [JsonPropertyName("verified_artifacts")]
    public List<string> VerifiedArtifacts { get; init; } = [];

    [JsonPropertyName("strongest_check")]
    public CorrectionVerificationDto? StrongestCheck { get; init; }

    [JsonPropertyName("repro")]
    public CorrectionReproductionDto? Repro { get; init; }

    [JsonPropertyName("unresolved_reason")]
    public string? UnresolvedReason { get; init; }

    [JsonPropertyName("verified_at")]
    public DateTimeOffset? VerifiedAt { get; init; }
}

public sealed class CorrectionVerificationDto
{
    [JsonPropertyName("execution_id")]
    public string? ExecutionId { get; init; }

    [JsonPropertyName("tool_id")]
    public string? ToolId { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;
}

public sealed class CorrectionReproductionDto
{
    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("failed_before_command")]
    public string? FailedBeforeCommand { get; init; }

    [JsonPropertyName("passed_after_command")]
    public string? PassedAfterCommand { get; init; }

    [JsonPropertyName("failed_before_execution_id")]
    public string? FailedBeforeExecutionId { get; init; }

    [JsonPropertyName("passed_after_execution_id")]
    public string? PassedAfterExecutionId { get; init; }
}

public sealed class CorrectionQuestionDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; init; } = string.Empty;

    [JsonPropertyName("blocking")]
    public bool Blocking { get; init; }

    [JsonPropertyName("options")]
    public List<string> Options { get; init; } = [];

    [JsonPropertyName("answer")]
    public string? Answer { get; init; }

    [JsonPropertyName("asked_at")]
    public DateTimeOffset AskedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("answered_at")]
    public DateTimeOffset? AnsweredAt { get; init; }
}

public sealed class CorrectionFeedbackRequestDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("client_request_id")]
    public string? ClientRequestId { get; init; }
}

public sealed class CorrectionAnswerRequestDto
{
    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;

    [JsonPropertyName("option_id")]
    public string? OptionId { get; init; }

    [JsonPropertyName("client_request_id")]
    public string? ClientRequestId { get; init; }
}
