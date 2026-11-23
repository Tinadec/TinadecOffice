using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// Canonical 9 governance and isolation redline signal codes unified across LiveExplainer,
/// RunContextDiagnosticsService, Trajectory views, and P2 self-evolution.
/// </summary>
public static class UnifiedGovernanceSignalCodes
{
    public const string ApprovalRejected = "APPROVAL_REJECTED";
    public const string FsNotObserved = "FS_NOT_OBSERVED";
    public const string FsStaleVersion = "FS_STALE_VERSION";
    public const string WorktreeGuard = "WORKTREE_GUARD";
    public const string WriteScopeLocked = "WRITE_SCOPE_LOCKED";
    public const string Timeout = "TIMEOUT";
    public const string SameArgsRetry = "SAME_ARGS_RETRY";
    public const string LoopDetected = "LOOP_DETECTED";
    public const string ContextPressure = "CONTEXT_PRESSURE";

    public static readonly IReadOnlyList<string> Ordered =
    [
        ApprovalRejected,
        FsNotObserved,
        FsStaleVersion,
        WorktreeGuard,
        WriteScopeLocked,
        Timeout,
        SameArgsRetry,
        LoopDetected,
        ContextPressure
    ];

    public static readonly IReadOnlySet<string> All = new HashSet<string>(Ordered, StringComparer.Ordinal);

    public static string DefaultSeverity(string code) => code switch
    {
        WorktreeGuard or WriteScopeLocked or Timeout or LoopDetected => "danger",
        ApprovalRejected or FsNotObserved or FsStaleVersion or SameArgsRetry or ContextPressure => "warn",
        _ => "neutral"
    };
}

/// <summary>
/// Authoritative 10 context window composition categories for ContextPack classification and reconciliation.
/// </summary>
public static class ContextSourceCategories
{
    public const string SystemPrompt = "system_prompt";
    public const string ModePipeline = "mode_pipeline";
    public const string ProjectInstructions = "project_instructions";
    public const string ToolSchemas = "tool_schemas";
    public const string SkillsCatalog = "skills_catalog";
    public const string Memory = "memory";
    public const string CompactedDigest = "compacted_digest";
    public const string ConversationTurns = "conversation_turns";
    public const string ToolOutputs = "tool_outputs";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> Ordered =
    [
        SystemPrompt,
        ModePipeline,
        ProjectInstructions,
        ToolSchemas,
        SkillsCatalog,
        Memory,
        CompactedDigest,
        ConversationTurns,
        ToolOutputs,
        Other
    ];

    public static string DefaultLabel(string category) => category switch
    {
        SystemPrompt => "System Prompt",
        ModePipeline => "Mode & Pipeline",
        ProjectInstructions => "Project Instructions (AGENTS.md)",
        ToolSchemas => "Tool Schemas & MCP",
        SkillsCatalog => "Skills Catalog",
        Memory => "Memory (L2 / L3)",
        CompactedDigest => "Compacted Digest",
        ConversationTurns => "Conversation Turns",
        ToolOutputs => "Tool Outputs",
        _ => "Other Context"
    };
}

/// <summary>
/// Source kinds audited by the 64-character rolling-hash Winnowing overlap auditor.
/// </summary>
public static class ContextAuditSourceKinds
{
    public const string SystemPrompt = "system_prompt";
    public const string PipelineTemplate = "pipeline_template";
    public const string WorkspaceInstructions = "workspace_instructions";
    public const string ToolSchema = "tool_schema";
    public const string Skill = "skill";
    public const string PromptFragment = "prompt_fragment";
    public const string Other = "other";

    public static string Normalize(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            SystemPrompt => SystemPrompt,
            PipelineTemplate => PipelineTemplate,
            WorkspaceInstructions => WorkspaceInstructions,
            ToolSchema => ToolSchema,
            Skill => Skill,
            PromptFragment => PromptFragment,
            _ => Other
        };
    }
}

/// <summary>
/// Canonical 5 remediation action codes produced by <c>ContextOverlapAuditor</c>.
/// </summary>
public static class OverlapRemediationCodes
{
    public const string MoveToSkillReferences = "move_to_skill_references";
    public const string DedupeWorkspaceInstructions = "dedupe_workspace_instructions";
    public const string DedupeToolSchemaProse = "dedupe_tool_schema_prose";
    public const string ConsolidateSharedSkill = "consolidate_shared_skill";
    public const string ExtractSharedFragment = "extract_shared_fragment";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MoveToSkillReferences,
        DedupeWorkspaceInstructions,
        DedupeToolSchemaProse,
        ConsolidateSharedSkill,
        ExtractSharedFragment
    };
}

// ── Context Overlap Audit DTOs (POST /api/v1/context/audit-overlap) ──

public sealed record ContextAuditSourceInputDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ContextAuditSourceKinds.Other;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("meta")]
    public string? Meta { get; init; }
}

public sealed record ContextOverlapAuditRequestDto
{
    [JsonPropertyName("project_id")]
    public Guid? ProjectId { get; init; }

    [JsonPropertyName("mode_id")]
    public string? ModeId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("run_id")]
    public Guid? RunId { get; init; }

    [JsonPropertyName("workspace_root")]
    public string? WorkspaceRoot { get; init; }

    [JsonPropertyName("include_prompts")]
    public bool? IncludePrompts { get; init; }

    [JsonPropertyName("include_skills")]
    public bool? IncludeSkills { get; init; }

    [JsonPropertyName("include_tools")]
    public bool? IncludeTools { get; init; }

    [JsonPropertyName("k_gram_size")]
    public int? KGramSize { get; init; }

    [JsonPropertyName("window_size")]
    public int? WindowSize { get; init; }

    [JsonPropertyName("max_excerpt_length")]
    public int? MaxExcerptLength { get; init; }

    [JsonPropertyName("max_matches_per_pair")]
    public int? MaxMatchesPerPair { get; init; }

    [JsonPropertyName("extra_sources")]
    public IReadOnlyList<ContextAuditSourceInputDto>? ExtraSources { get; init; }
}

public sealed record LineRangeDto
{
    [JsonPropertyName("start")]
    public int Start { get; init; } = 1;

    [JsonPropertyName("end")]
    public int End { get; init; } = 1;

    public string Format() => Start == End ? $"L{Start}" : $"L{Start}-L{End}";

    public static string FormatPair(LineRangeDto a, LineRangeDto b) => $"{a.Format()} <-> {b.Format()}";
}

public sealed record OverlapPassageMatchDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source_a_id")]
    public string SourceAId { get; init; } = string.Empty;

    [JsonPropertyName("source_a_label")]
    public string SourceALabel { get; init; } = string.Empty;

    [JsonPropertyName("source_a_kind")]
    public string SourceAKind { get; init; } = ContextAuditSourceKinds.Other;

    [JsonPropertyName("source_b_id")]
    public string SourceBId { get; init; } = string.Empty;

    [JsonPropertyName("source_b_label")]
    public string SourceBLabel { get; init; } = string.Empty;

    [JsonPropertyName("source_b_kind")]
    public string SourceBKind { get; init; } = ContextAuditSourceKinds.Other;

    [JsonPropertyName("source_a_lines")]
    public LineRangeDto SourceALines { get; init; } = new();

    [JsonPropertyName("source_b_lines")]
    public LineRangeDto SourceBLines { get; init; } = new();

    [JsonPropertyName("line_range_pair")]
    public string LineRangePair { get; init; } = "L1 <-> L1";

    [JsonPropertyName("shared_chars")]
    public int SharedChars { get; init; }

    [JsonPropertyName("estimated_duplicate_tokens")]
    public int EstimatedDuplicateTokens { get; init; }

    [JsonPropertyName("excerpt")]
    public string Excerpt { get; init; } = string.Empty;

    [JsonPropertyName("remediation_code")]
    public string RemediationCode { get; init; } = OverlapRemediationCodes.ExtractSharedFragment;

    [JsonPropertyName("remediation_summary")]
    public string RemediationSummary { get; init; } = string.Empty;
}

public sealed record ContextOverlapPairSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source_a_id")]
    public string SourceAId { get; init; } = string.Empty;

    [JsonPropertyName("source_a_label")]
    public string SourceALabel { get; init; } = string.Empty;

    [JsonPropertyName("source_a_kind")]
    public string SourceAKind { get; init; } = ContextAuditSourceKinds.Other;

    [JsonPropertyName("source_b_id")]
    public string SourceBId { get; init; } = string.Empty;

    [JsonPropertyName("source_b_label")]
    public string SourceBLabel { get; init; } = string.Empty;

    [JsonPropertyName("source_b_kind")]
    public string SourceBKind { get; init; } = ContextAuditSourceKinds.Other;

    [JsonPropertyName("shared_chars")]
    public int SharedChars { get; init; }

    [JsonPropertyName("overlap_percent")]
    public double OverlapPercent { get; init; }

    [JsonPropertyName("overlap_percent_a")]
    public double OverlapPercentA { get; init; }

    [JsonPropertyName("overlap_percent_b")]
    public double OverlapPercentB { get; init; }

    [JsonPropertyName("estimated_duplicate_tokens")]
    public int EstimatedDuplicateTokens { get; init; }

    [JsonPropertyName("match_count")]
    public int MatchCount { get; init; }

    [JsonPropertyName("matches")]
    public IReadOnlyList<OverlapPassageMatchDto> Matches { get; init; } = [];

    [JsonPropertyName("remediation_code")]
    public string RemediationCode { get; init; } = OverlapRemediationCodes.ExtractSharedFragment;

    [JsonPropertyName("remediation_summary")]
    public string RemediationSummary { get; init; } = string.Empty;
}

public sealed record ContextOverlapSourceStatsDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ContextAuditSourceKinds.Other;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("char_count")]
    public int CharCount { get; init; }

    [JsonPropertyName("line_count")]
    public int LineCount { get; init; }

    [JsonPropertyName("estimated_tokens")]
    public int EstimatedTokens { get; init; }

    [JsonPropertyName("duplicated_chars")]
    public int DuplicatedChars { get; init; }

    [JsonPropertyName("overlap_percent")]
    public double OverlapPercent { get; init; }
}

public sealed record ContextOverlapAuditDto
{
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "static_config";

    [JsonPropertyName("project_id")]
    public Guid? ProjectId { get; init; }

    [JsonPropertyName("mode_id")]
    public string? ModeId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("run_id")]
    public Guid? RunId { get; init; }

    [JsonPropertyName("k_gram_size")]
    public int KGramSize { get; init; } = 64;

    [JsonPropertyName("window_size")]
    public int WindowSize { get; init; } = 16;

    [JsonPropertyName("source_count")]
    public int SourceCount { get; init; }

    [JsonPropertyName("total_estimated_tokens")]
    public int TotalEstimatedTokens { get; init; }

    [JsonPropertyName("duplicate_estimated_tokens")]
    public int DuplicateEstimatedTokens { get; init; }

    [JsonPropertyName("overall_overlap_percent")]
    public double OverallOverlapPercent { get; init; }

    [JsonPropertyName("pairs")]
    public IReadOnlyList<ContextOverlapPairSummaryDto> Pairs { get; init; } = [];

    [JsonPropertyName("matches")]
    public IReadOnlyList<OverlapPassageMatchDto> Matches { get; init; } = [];

    [JsonPropertyName("sources")]
    public IReadOnlyList<ContextOverlapSourceStatsDto> Sources { get; init; } = [];

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

// ── Run Context Diagnostics DTOs (GET /api/v1/runs/{runId}/context-diagnostics) ──

public sealed record ContextWindowBucketDto
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("range_start")]
    public int RangeStart { get; init; }

    [JsonPropertyName("range_end")]
    public int RangeEnd { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; init; }

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; init; }

    [JsonPropertyName("error_rate")]
    public double ErrorRate { get; init; }

    [JsonPropertyName("avg_latency_ms")]
    public long AvgLatencyMs { get; init; }

    [JsonPropertyName("max_latency_ms")]
    public long MaxLatencyMs { get; init; }

    [JsonPropertyName("avg_occupancy_pct")]
    public double AvgOccupancyPct { get; init; }
}

public sealed record ContextWindowSamplePointDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source_kind")]
    public string SourceKind { get; init; } = "invocation";

    [JsonPropertyName("occupancy_pct")]
    public double OccupancyPct { get; init; }

    [JsonPropertyName("bucket_index")]
    public int BucketIndex { get; init; }

    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    [JsonPropertyName("latency_ms")]
    public long LatencyMs { get; init; }

    [JsonPropertyName("tokens_used")]
    public long TokensUsed { get; init; }

    [JsonPropertyName("token_budget")]
    public long TokenBudget { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record ContextWindowBucketReportDto
{
    [JsonPropertyName("buckets")]
    public IReadOnlyList<ContextWindowBucketDto> Buckets { get; init; } = [];

    [JsonPropertyName("samples")]
    public IReadOnlyList<ContextWindowSamplePointDto> Samples { get; init; } = [];

    [JsonPropertyName("total_samples")]
    public int TotalSamples { get; init; }

    [JsonPropertyName("total_errors")]
    public int TotalErrors { get; init; }

    [JsonPropertyName("overall_error_rate")]
    public double OverallErrorRate { get; init; }

    [JsonPropertyName("avg_latency_ms")]
    public long AvgLatencyMs { get; init; }

    [JsonPropertyName("peak_occupancy_pct")]
    public double PeakOccupancyPct { get; init; }

    [JsonPropertyName("low_occupancy_error_rate")]
    public double LowOccupancyErrorRate { get; init; }

    [JsonPropertyName("high_occupancy_error_rate")]
    public double HighOccupancyErrorRate { get; init; }

    [JsonPropertyName("low_occupancy_avg_latency_ms")]
    public long LowOccupancyAvgLatencyMs { get; init; }

    [JsonPropertyName("high_occupancy_avg_latency_ms")]
    public long HighOccupancyAvgLatencyMs { get; init; }

    [JsonPropertyName("degradation_detected")]
    public bool DegradationDetected { get; init; }

    [JsonPropertyName("degradation_reason_codes")]
    public IReadOnlyList<string> DegradationReasonCodes { get; init; } = [];
}

public sealed record ContextEvidenceShareItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("tokens")]
    public int Tokens { get; init; }

    [JsonPropertyName("bytes_estimate")]
    public int BytesEstimate { get; init; }

    [JsonPropertyName("dropped")]
    public bool Dropped { get; init; }
}

public sealed record ContextCategoryNodeDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = ContextSourceCategories.Other;

    [JsonPropertyName("source_key")]
    public string? SourceKey { get; init; }

    [JsonPropertyName("depth")]
    public int Depth { get; init; }

    [JsonPropertyName("retained_tokens")]
    public int RetainedTokens { get; init; }

    [JsonPropertyName("dropped_tokens")]
    public int DroppedTokens { get; init; }

    [JsonPropertyName("retained_bytes_estimate")]
    public int RetainedBytesEstimate { get; init; }

    [JsonPropertyName("dropped_bytes_estimate")]
    public int DroppedBytesEstimate { get; init; }

    [JsonPropertyName("share_percent")]
    public double SharePercent { get; init; }

    [JsonPropertyName("budget_share_percent")]
    public double BudgetSharePercent { get; init; }

    [JsonPropertyName("item_count")]
    public int ItemCount { get; init; }

    [JsonPropertyName("dropped_item_count")]
    public int DroppedItemCount { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<ContextEvidenceShareItemDto> Items { get; init; } = [];

    [JsonPropertyName("children")]
    public IReadOnlyList<ContextCategoryNodeDto> Children { get; init; } = [];
}

public sealed record CompactionReconciliationDto
{
    [JsonPropertyName("pack_id")]
    public string PackId { get; init; } = string.Empty;

    [JsonPropertyName("lane_key")]
    public string? LaneKey { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("token_budget")]
    public int TokenBudget { get; init; }

    [JsonPropertyName("retained_tokens")]
    public int RetainedTokens { get; init; }

    [JsonPropertyName("dropped_tokens")]
    public int DroppedTokens { get; init; }

    [JsonPropertyName("pre_compaction_tokens")]
    public int PreCompactionTokens { get; init; }

    [JsonPropertyName("retained_bytes_estimate")]
    public int RetainedBytesEstimate { get; init; }

    [JsonPropertyName("dropped_bytes_estimate")]
    public int DroppedBytesEstimate { get; init; }

    [JsonPropertyName("utilization_percent")]
    public double UtilizationPercent { get; init; }

    [JsonPropertyName("retained_ratio_percent")]
    public double RetainedRatioPercent { get; init; }

    [JsonPropertyName("dropped_ratio_percent")]
    public double DroppedRatioPercent { get; init; }

    [JsonPropertyName("has_pricing_data")]
    public bool HasPricingData { get; init; }

    [JsonPropertyName("has_compaction_digest")]
    public bool HasCompactionDigest { get; init; }

    [JsonPropertyName("previous_pack_retained_delta")]
    public int? PreviousPackRetainedDelta { get; init; }

    [JsonPropertyName("previous_pack_dropped_delta")]
    public int? PreviousPackDroppedDelta { get; init; }
}

public sealed record ContextCompactionDiagnosticsDto
{
    [JsonPropertyName("selected_pack_id")]
    public string? SelectedPackId { get; init; }

    [JsonPropertyName("pack_count")]
    public int PackCount { get; init; }

    [JsonPropertyName("total_dropped_tokens_across_packs")]
    public int TotalDroppedTokensAcrossPacks { get; init; }

    [JsonPropertyName("critical_dropped_sources")]
    public IReadOnlyList<string> CriticalDroppedSources { get; init; } = [];

    [JsonPropertyName("root")]
    public ContextCategoryNodeDto Root { get; init; } = new();

    [JsonPropertyName("reconciliation")]
    public CompactionReconciliationDto Reconciliation { get; init; } = new();

    [JsonPropertyName("packs")]
    public IReadOnlyList<CompactionReconciliationDto> Packs { get; init; } = [];
}

public sealed record UnifiedGovernanceSignalDto
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "warn";

    [JsonPropertyName("count")]
    public int Count { get; init; } = 1;

    [JsonPropertyName("max_retry_streak")]
    public int? MaxRetryStreak { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("subject_ids")]
    public IReadOnlyList<string> SubjectIds { get; init; } = [];
}

public sealed record ToolRetryStreakDto
{
    [JsonPropertyName("execution_id")]
    public string ExecutionId { get; init; } = string.Empty;

    [JsonPropertyName("tool_id")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("scope_key")]
    public string ScopeKey { get; init; } = string.Empty;

    [JsonPropertyName("streak_count")]
    public int StreakCount { get; init; }

    [JsonPropertyName("arguments_summary")]
    public string? ArgumentsSummary { get; init; }
}

public sealed record DiagnosticFileEditStatsDto
{
    [JsonPropertyName("execution_id")]
    public string ExecutionId { get; init; } = string.Empty;

    [JsonPropertyName("tool_id")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    [JsonPropertyName("added")]
    public int Added { get; init; }

    [JsonPropertyName("removed")]
    public int Removed { get; init; }
}

public sealed record DiagnosticCodeFrameLineDto
{
    [JsonPropertyName("line_number")]
    public int? LineNumber { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("highlight")]
    public bool Highlight { get; init; }
}

public sealed record DiagnosticCodeFrameDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source_id")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("column")]
    public int? Column { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("error_category")]
    public string? ErrorCategory { get; init; }

    [JsonPropertyName("lines")]
    public IReadOnlyList<DiagnosticCodeFrameLineDto> Lines { get; init; } = [];
}

public sealed record RunGovernanceDiagnosticsDto
{
    [JsonPropertyName("signal_counts")]
    public IReadOnlyDictionary<string, int> SignalCounts { get; init; } = new Dictionary<string, int>();

    [JsonPropertyName("active_signals")]
    public IReadOnlyList<string> ActiveSignals { get; init; } = [];

    [JsonPropertyName("signals")]
    public IReadOnlyList<UnifiedGovernanceSignalDto> Signals { get; init; } = [];

    [JsonPropertyName("max_same_args_retry_streak")]
    public int MaxSameArgsRetryStreak { get; init; }

    [JsonPropertyName("retry_streaks")]
    public IReadOnlyList<ToolRetryStreakDto> RetryStreaks { get; init; } = [];

    [JsonPropertyName("total_tool_calls")]
    public int TotalToolCalls { get; init; }

    [JsonPropertyName("total_model_calls")]
    public int TotalModelCalls { get; init; }

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; init; }

    [JsonPropertyName("total_added_lines")]
    public int TotalAddedLines { get; init; }

    [JsonPropertyName("total_removed_lines")]
    public int TotalRemovedLines { get; init; }

    [JsonPropertyName("file_edits")]
    public IReadOnlyList<DiagnosticFileEditStatsDto> FileEdits { get; init; } = [];

    [JsonPropertyName("error_frames")]
    public IReadOnlyList<DiagnosticCodeFrameDto> ErrorFrames { get; init; } = [];
}

public sealed record ExecutiveDiagnosticFindingDto
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "info";

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("evidence_refs")]
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    [JsonPropertyName("suggested_action")]
    public string SuggestedAction { get; init; } = string.Empty;
}

public sealed record RunContextDiagnosticsDto
{
    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "running";

    [JsonPropertyName("mode_version_id")]
    public Guid? ModeVersionId { get; init; }

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("window_buckets")]
    public ContextWindowBucketReportDto WindowBuckets { get; init; } = new();

    [JsonPropertyName("compaction")]
    public ContextCompactionDiagnosticsDto Compaction { get; init; } = new();

    [JsonPropertyName("governance")]
    public RunGovernanceDiagnosticsDto Governance { get; init; } = new();

    [JsonPropertyName("executive_findings")]
    public IReadOnlyList<ExecutiveDiagnosticFindingDto> ExecutiveFindings { get; init; } = [];
}

/// <summary>
/// Bounded context health summary attached to <c>RunDossier</c>.
/// </summary>
public sealed record ContextHealthSummaryDto
{
    [JsonPropertyName("peak_occupancy_pct")]
    public double PeakOccupancyPct { get; init; }

    [JsonPropertyName("degradation_detected")]
    public bool DegradationDetected { get; init; }

    [JsonPropertyName("dropped_tokens")]
    public int DroppedTokens { get; init; }

    [JsonPropertyName("pack_count")]
    public int PackCount { get; init; }

    [JsonPropertyName("compaction_count")]
    public int CompactionCount { get; init; }

    [JsonPropertyName("max_same_args_retry_streak")]
    public int MaxSameArgsRetryStreak { get; init; }

    [JsonPropertyName("active_signals")]
    public IReadOnlyList<string> ActiveSignals { get; init; } = [];

    [JsonPropertyName("signal_counts")]
    public IReadOnlyDictionary<string, int> SignalCounts { get; init; } = new Dictionary<string, int>();
}
