using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

public static class ContextEvolutionProposalKinds
{
    public const string ContextPolicyProposal = "context_policy_proposal";
    public const string PromptDedupProposal = "prompt_dedup_proposal";

    public static bool IsValid(string? kind) =>
        string.Equals(kind, ContextPolicyProposal, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, PromptDedupProposal, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string kind) => kind.Trim().ToLowerInvariant();
}

public static class ContextEvolutionProposalStatuses
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Rejected = "rejected";

    public static bool TryNormalize(string? status, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(status)) return false;
        var trimmed = status.Trim().ToLowerInvariant();
        if (trimmed is "pending" or "proposed")
        {
            normalized = Pending;
            return true;
        }
        if (trimmed is "applied" or "promoted" or "approved")
        {
            normalized = Applied;
            return true;
        }
        if (trimmed == "rejected")
        {
            normalized = Rejected;
            return true;
        }
        return false;
    }
}

public static class ContextEvolutionTargetScopes
{
    public const string Global = "global";
    public const string Mode = "mode";
    public const string Workspace = "workspace";

    public static bool IsValid(string? scope) =>
        string.Equals(scope, Global, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, Mode, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, Workspace, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string scope) => scope.Trim().ToLowerInvariant();
}

/// <summary>
/// Proposed context policy parameter delta for a mode-scoped or global-scoped tuning proposal.
/// </summary>
public sealed class ContextPolicyPatchDto
{
    [JsonPropertyName("context_strategy")]
    public string? ContextStrategy { get; init; }

    [JsonPropertyName("compaction_trigger_ratio")]
    public double? CompactionTriggerRatio { get; init; }

    [JsonPropertyName("recent_message_limit")]
    public int? RecentMessageLimit { get; init; }

    [JsonPropertyName("recall_fidelity")]
    public string? RecallFidelity { get; init; }

    [JsonPropertyName("allow_cross_session_recall")]
    public bool? AllowCrossSessionRecall { get; init; }

    [JsonPropertyName("evolution_mode")]
    public string? EvolutionMode { get; init; }

    [JsonPropertyName("core_markdown_max_tokens")]
    public int? CoreMarkdownMaxTokens { get; init; }
}

/// <summary>
/// Concrete Winnowing overlap evidence for a prompt/skill/instruction deduplication proposal.
/// </summary>
public sealed class ContextDedupProposalEvidenceDto
{
    [JsonPropertyName("source_a_id")]
    public string SourceAId { get; init; } = string.Empty;

    [JsonPropertyName("source_a_label")]
    public string SourceALabel { get; init; } = string.Empty;

    [JsonPropertyName("source_a_kind")]
    public string SourceAKind { get; init; } = string.Empty;

    [JsonPropertyName("source_a_path")]
    public string? SourceAPath { get; init; }

    [JsonPropertyName("source_a_start_line")]
    public int SourceAStartLine { get; init; }

    [JsonPropertyName("source_a_end_line")]
    public int SourceAEndLine { get; init; }

    [JsonPropertyName("source_b_id")]
    public string SourceBId { get; init; } = string.Empty;

    [JsonPropertyName("source_b_label")]
    public string SourceBLabel { get; init; } = string.Empty;

    [JsonPropertyName("source_b_kind")]
    public string SourceBKind { get; init; } = string.Empty;

    [JsonPropertyName("source_b_path")]
    public string? SourceBPath { get; init; }

    [JsonPropertyName("source_b_start_line")]
    public int SourceBStartLine { get; init; }

    [JsonPropertyName("source_b_end_line")]
    public int SourceBEndLine { get; init; }

    [JsonPropertyName("line_range_label")]
    public string LineRangeLabel { get; init; } = string.Empty;

    [JsonPropertyName("overlap_chars")]
    public int OverlapChars { get; init; }

    [JsonPropertyName("estimated_wasted_tokens")]
    public int EstimatedWastedTokens { get; init; }

    [JsonPropertyName("remediation_code")]
    public string RemediationCode { get; init; } = string.Empty;

    [JsonPropertyName("excerpt")]
    public string Excerpt { get; init; } = string.Empty;

    [JsonPropertyName("suggested_remediation")]
    public string SuggestedRemediation { get; init; } = string.Empty;
}

/// <summary>
/// Multi-run degradation statistics and before/after policy comparison backing a context policy proposal.
/// </summary>
public sealed class ContextEvolutionMetricsComparisonDto
{
    [JsonPropertyName("window_run_count")]
    public int WindowRunCount { get; init; }

    [JsonPropertyName("degraded_run_count")]
    public int DegradedRunCount { get; init; }

    [JsonPropertyName("degradation_rate")]
    public double DegradationRate { get; init; }

    [JsonPropertyName("dropped_critical_run_count")]
    public int DroppedCriticalRunCount { get; init; }

    [JsonPropertyName("avg_peak_occupancy_pct")]
    public double AvgPeakOccupancyPct { get; init; }

    [JsonPropertyName("high_occupancy_error_rate")]
    public double HighOccupancyErrorRate { get; init; }

    [JsonPropertyName("low_occupancy_error_rate")]
    public double LowOccupancyErrorRate { get; init; }

    [JsonPropertyName("high_occupancy_avg_latency_ms")]
    public double HighOccupancyAvgLatencyMs { get; init; }

    [JsonPropertyName("low_occupancy_avg_latency_ms")]
    public double LowOccupancyAvgLatencyMs { get; init; }

    [JsonPropertyName("dropped_sources")]
    public IReadOnlyList<string> DroppedSources { get; init; } = [];

    [JsonPropertyName("before_values")]
    public IReadOnlyDictionary<string, string> BeforeValues { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("after_values")]
    public IReadOnlyDictionary<string, string> AfterValues { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("expected_benefit")]
    public string ExpectedBenefit { get; init; } = string.Empty;
}

/// <summary>
/// Human-reviewable P2 context evolution proposal (policy tuning or prompt/skill deduplication).
/// Conservative posture: <see cref="RequiresHumanApproval"/> defaults to <c>true</c>.
/// </summary>
public sealed class ContextEvolutionProposalDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ContextEvolutionProposalKinds.ContextPolicyProposal;

    [JsonPropertyName("status")]
    public string Status { get; init; } = ContextEvolutionProposalStatuses.Pending;

    [JsonPropertyName("target_scope")]
    public string TargetScope { get; init; } = ContextEvolutionTargetScopes.Global;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("rationale")]
    public string Rationale { get; init; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; } = 0.8;

    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; init; } = "low";

    [JsonPropertyName("requires_human_approval")]
    public bool RequiresHumanApproval { get; init; } = true;

    [JsonPropertyName("source_run_id")]
    public Guid? SourceRunId { get; init; }

    [JsonPropertyName("evidence_run_ids")]
    public IReadOnlyList<string> EvidenceRunIds { get; init; } = [];

    [JsonPropertyName("project_id")]
    public Guid? ProjectId { get; init; }

    [JsonPropertyName("target_mode_id")]
    public Guid? TargetModeId { get; init; }

    [JsonPropertyName("target_mode_slug")]
    public string? TargetModeSlug { get; init; }

    [JsonPropertyName("target_mode_name")]
    public string? TargetModeName { get; init; }

    [JsonPropertyName("remediation_code")]
    public string? RemediationCode { get; init; }

    [JsonPropertyName("policy_patch")]
    public ContextPolicyPatchDto? PolicyPatch { get; init; }

    [JsonPropertyName("dedup_evidence")]
    public ContextDedupProposalEvidenceDto? DedupEvidence { get; init; }

    [JsonPropertyName("metrics_comparison")]
    public ContextEvolutionMetricsComparisonDto? MetricsComparison { get; init; }

    [JsonPropertyName("linked_agent_candidate_id")]
    public Guid? LinkedAgentCandidateId { get; init; }

    [JsonPropertyName("eval_run_id")]
    public Guid? EvalRunId { get; init; }

    [JsonPropertyName("eval_gate_status")]
    public string? EvalGateStatus { get; init; }

    [JsonPropertyName("decision_reason")]
    public string? DecisionReason { get; init; }

    [JsonPropertyName("applied_toml_path")]
    public string? AppliedTomlPath { get; init; }

    [JsonPropertyName("applied_toml_content_hash")]
    public string? AppliedTomlContentHash { get; init; }

    [JsonPropertyName("applied_mode_id")]
    public Guid? AppliedModeId { get; init; }

    [JsonPropertyName("applied_mode_version")]
    public int? AppliedModeVersion { get; init; }

    [JsonPropertyName("promoted_agent_id")]
    public Guid? PromotedAgentId { get; init; }

    [JsonPropertyName("composer_prompt")]
    public string? ComposerPrompt { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("decided_at")]
    public DateTimeOffset? DecidedAt { get; init; }
}

/// <summary>
/// Response envelope for GET /api/v1/context-evolution/proposals.
/// </summary>
public sealed class ContextEvolutionProposalListDto
{
    [JsonPropertyName("items")]
    public IReadOnlyList<ContextEvolutionProposalDto> Items { get; init; } = [];

    [JsonPropertyName("total_count")]
    public int TotalCount { get; init; }

    [JsonPropertyName("pending_count")]
    public int PendingCount { get; init; }

    [JsonPropertyName("auto_tune_low_risk_context_policy")]
    public bool AutoTuneLowRiskContextPolicy { get; init; }
}

/// <summary>
/// Request body for POST /api/v1/context-evolution/proposals/{proposalId}/apply.
/// </summary>
public sealed class ApplyContextEvolutionProposalRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("eval_run_id")]
    public Guid? EvalRunId { get; init; }

    [JsonPropertyName("require_eval_gate")]
    public bool? RequireEvalGate { get; init; }

    [JsonPropertyName("expected_content_hash")]
    public string? ExpectedContentHash { get; init; }
}

/// <summary>
/// Request body for POST /api/v1/context-evolution/proposals/{proposalId}/reject.
/// </summary>
public sealed class RejectContextEvolutionProposalRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Response body for POST /api/v1/context-evolution/proposals/{proposalId}/apply.
/// </summary>
public sealed class ContextEvolutionApplyResultDto
{
    [JsonPropertyName("proposal")]
    public ContextEvolutionProposalDto Proposal { get; init; } = new();

    [JsonPropertyName("applied")]
    public bool Applied { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("updated_toml_preview")]
    public string? UpdatedTomlPreview { get; init; }

    [JsonPropertyName("applied_mode_id")]
    public Guid? AppliedModeId { get; init; }

    [JsonPropertyName("applied_mode_version")]
    public int? AppliedModeVersion { get; init; }

    [JsonPropertyName("promoted_agent_id")]
    public Guid? PromotedAgentId { get; init; }

    [JsonPropertyName("eval_run_id")]
    public Guid? EvalRunId { get; init; }
}

/// <summary>
/// Filter parameters for listing context evolution proposals.
/// </summary>
public sealed record ContextEvolutionProposalQuery(
    string? Status = null,
    string? Kind = null,
    string? TargetScope = null,
    Guid? ProjectId = null,
    Guid? ModeId = null,
    int Limit = 50);

/// <summary>
/// Bounded overlap pair summary passed from P1 auditor into P2 proposal &amp; curator pipeline.
/// </summary>
public sealed record ContextOverlapPairEvidenceInput(
    string SourceAId,
    string SourceALabel,
    string SourceAKind,
    string? SourceAPath,
    int SourceAStartLine,
    int SourceAEndLine,
    string SourceBId,
    string SourceBLabel,
    string SourceBKind,
    string? SourceBPath,
    int SourceBStartLine,
    int SourceBEndLine,
    int OverlapChars,
    int EstimatedWastedTokens,
    string RemediationCode,
    string Excerpt,
    string? SuggestedRemediation = null);

/// <summary>
/// Per-run context health &amp; overlap observation fed at run closeout into the P2 multi-run aggregator.
/// </summary>
public sealed record RunContextEvolutionObservation
{
    public required string RunId { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? ModeId { get; init; }
    public string? ModeSlug { get; init; }
    public string? ModeName { get; init; }
    public double PeakOccupancyPct { get; init; }
    public bool DegradationDetected { get; init; }
    public IReadOnlyList<string> DegradationReasons { get; init; } = [];
    public double HighOccupancyErrorRate { get; init; }
    public double LowOccupancyErrorRate { get; init; }
    public double HighOccupancyAvgLatencyMs { get; init; }
    public double LowOccupancyAvgLatencyMs { get; init; }
    public IReadOnlyList<string> DroppedSources { get; init; } = [];
    public int DroppedTokens { get; init; }
    public IReadOnlyDictionary<string, int> RedlineCounts { get; init; } = new Dictionary<string, int>();
    public int MaxSameArgsRetryStreak { get; init; }
    public string EffectiveStrategy { get; init; } = "managed";
    public double EffectiveCompactionTriggerRatio { get; init; } = 0.75;
    public int EffectiveRecentMessageLimit { get; init; } = 128;
    public string EffectiveRecallFidelity { get; init; } = "balanced";
    public bool EffectiveAllowCrossSessionRecall { get; init; } = true;
    public string EffectiveEvolutionMode { get; init; } = "auto_evolve";
    public int EffectiveCoreMarkdownMaxTokens { get; init; } = 1500;
    public IReadOnlyList<ContextOverlapPairEvidenceInput> OverlapPairs { get; init; } = [];
    public Guid? LinkedAgentCandidateId { get; init; }
    public bool ForceGlobalPolicyScope { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Audit receipt stored when a pending proposal is applied by human review (or explicit opt-in low-risk switch).
/// </summary>
public sealed record ContextEvolutionAppliedReceipt(
    string? Reason = null,
    string? AppliedTomlPath = null,
    string? AppliedTomlContentHash = null,
    string? UpdatedTomlPreview = null,
    Guid? AppliedModeId = null,
    int? AppliedModeVersion = null,
    Guid? PromotedAgentId = null,
    Guid? EvalRunId = null,
    string? EvalGateStatus = null);

/// <summary>
/// Cross-module port contract for P2 context policy &amp; prompt deduplication proposals.
/// Lives in Contracts so DmaEA, Context, and AspNetCore share a single type without cross-module coupling.
/// </summary>
public interface IContextPolicyProposalService
{
    Task<ContextEvolutionProposalListDto> ListProposalsAsync(
        ContextEvolutionProposalQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<ContextEvolutionProposalDto?> GetProposalAsync(
        Guid proposalId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextEvolutionProposalDto>> RecordRunAndGenerateProposalsAsync(
        RunContextEvolutionObservation observation,
        CancellationToken cancellationToken = default);

    Task<ContextEvolutionProposalDto> UpsertProposalAsync(
        ContextEvolutionProposalDto proposal,
        CancellationToken cancellationToken = default);

    Task<ContextEvolutionApplyResultDto> ApplyGlobalPolicyProposalAsync(
        Guid proposalId,
        ApplyContextEvolutionProposalRequest? request = null,
        string? evalGateStatus = null,
        CancellationToken cancellationToken = default);

    Task<ContextEvolutionProposalDto> MarkAppliedAsync(
        Guid proposalId,
        ContextEvolutionAppliedReceipt receipt,
        CancellationToken cancellationToken = default);

    Task<ContextEvolutionProposalDto> RejectProposalAsync(
        Guid proposalId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    string BuildBoundedCuratorEvidence(
        RunContextEvolutionObservation observation,
        int maxChars = 2200);
}

/// <summary>
/// Scope-safe resolver allowing DmaEA and AspNetCore endpoints to share one singleton
/// <see cref="IContextPolicyProposalService"/> per host storage scope even when not manually wired in DI.
/// </summary>
public static class ContextPolicyProposalServiceRegistry
{
    private static readonly ConditionalWeakTable<object, IContextPolicyProposalService> Instances = new();
    private static Func<IServiceProvider, (object ScopeKey, IContextPolicyProposalService Service)>? _factory;

    public static void RegisterFactory(Func<IServiceProvider, (object ScopeKey, IContextPolicyProposalService Service)> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public static IContextPolicyProposalService? Resolve(IServiceProvider? services)
    {
        if (services is null) return null;
        if (services.GetService(typeof(IContextPolicyProposalService)) is IContextPolicyProposalService registered)
        {
            return registered;
        }
        var factory = _factory;
        if (factory is null) return null;
        var probe = factory(services);
        return Instances.GetValue(probe.ScopeKey, _ => probe.Service);
    }
}
