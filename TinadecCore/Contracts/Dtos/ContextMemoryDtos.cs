namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// Effective [context] and [memory] runtime policy exposed over HTTP and AI self-configuration.
/// </summary>
public sealed class ContextMemoryPolicyDto
{
    public string Strategy { get; init; } = "managed";
    public string RecallFidelity { get; init; } = "balanced";
    public double CompactionTriggerRatio { get; init; } = 0.75;
    public int RecentMessageLimit { get; init; } = 128;
    public int DefaultTokenBudget { get; init; } = 131_072;
    public bool AllowCrossSessionRecall { get; init; } = true;
    public bool MicroCompactStaleToolOutputs { get; init; } = true;
    public int OffloadLargeToolPayloadBytes { get; init; } = 16_384;
    public string EvolutionMode { get; init; } = "auto_evolve";
    public bool CandidateOnly { get; init; }
    public int RetrievalLimit { get; init; } = 8;
    public int CoreMarkdownMaxTokens { get; init; } = 1500;
    public bool ProjectMarkdownProjection { get; init; } = true;
    public string ProjectMemoryPath { get; init; } = ".tinadec/memory/project-core.md";
    public bool CompanionCanWriteProjectMemory { get; init; }
    public IReadOnlyList<string> AllowedScopes { get; init; } = ["principal", "workspace", "project", "agent", "companion"];
    public IReadOnlyList<string> AllowedKinds { get; init; } = ["fact", "preference", "decision", "success_pattern", "failure_pattern", "task_template", "supervision_rule", "bond", "episode"];
    public IReadOnlyList<string> AlwaysOnPrimitives { get; init; } = ["kv_cache_append_only_window", "micro_compact_stale_tool_outputs", "offload_large_tool_payloads"];
    public string? SourcePath { get; init; }
    public string? ContentHash { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Partial update request for [context] and [memory] policy in runtime.toml.
/// </summary>
public sealed class UpdateContextMemoryPolicyRequest
{
    public string? Strategy { get; init; }
    public string? RecallFidelity { get; init; }
    public double? CompactionTriggerRatio { get; init; }
    public int? RecentMessageLimit { get; init; }
    public int? DefaultTokenBudget { get; init; }
    public bool? AllowCrossSessionRecall { get; init; }
    public int? OffloadLargeToolPayloadBytes { get; init; }
    public string? EvolutionMode { get; init; }
    public bool? CandidateOnly { get; init; }
    public int? RetrievalLimit { get; init; }
    public int? CoreMarkdownMaxTokens { get; init; }
    public bool? ProjectMarkdownProjection { get; init; }
    public string? ProjectMemoryPath { get; init; }
    public bool? CompanionCanWriteProjectMemory { get; init; }
    public string? ExpectedContentHash { get; init; }
    public string? ExpectedHash { get; init; }
}

/// <summary>
/// Immutable version record inside a MemoryItem's evolution chain.
/// </summary>
public sealed class MemoryVersionLedgerDto
{
    public Guid Id { get; init; }
    public int Version { get; init; }
    public string Action { get; init; } = "add";
    public string Content { get; init; } = string.Empty;
    public string? PreviousContent { get; init; }
    public Guid? SourceSessionId { get; init; }
    public long? SourceMessageSeq { get; init; }
    public Guid? SourceRunId { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Structured memory item with provenance, hot/cold projection state, and version history.
/// </summary>
public sealed class MemoryItemLedgerDto
{
    public Guid Id { get; init; }
    public string Scope { get; init; } = "project";
    public string Kind { get; init; } = "fact";
    public string? Section { get; init; }
    public string Status { get; init; } = "active";
    public int CurrentVersion { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? PreviousContent { get; init; }
    public string Action { get; init; } = "add";
    public Guid? ProjectId { get; init; }
    public Guid? CompanionId { get; init; }
    public Guid? SourceSessionId { get; init; }
    public long? SourceMessageSeq { get; init; }
    public Guid? SourceRunId { get; init; }
    public Guid? SupersededById { get; init; }
    public string? Applicability { get; init; }
    public string? ExpiryCondition { get; init; }
    public string? RevocationReason { get; init; }
    public int EstimatedTokens { get; init; }
    public bool InHotMarkdown { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public IReadOnlyList<MemoryVersionLedgerDto> Versions { get; init; } = [];
}

public sealed class MemoryItemListResultDto
{
    public IReadOnlyList<MemoryItemLedgerDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int ActiveCount { get; init; }
    public int HotCount { get; init; }
    public int ColdCount { get; init; }
}

public sealed class UpsertMemoryItemRequest
{
    public string Scope { get; init; } = "project";
    public string? Kind { get; init; }
    public string? Section { get; init; }
    public string Content { get; init; } = string.Empty;
    public Guid? ProjectId { get; init; }
    public string? WorkspaceRoot { get; init; }
    public Guid? CompanionId { get; init; }
    public Guid? SourceSessionId { get; init; }
    public long? SourceMessageSeq { get; init; }
    public Guid? SourceRunId { get; init; }
    public Guid? SupersedeItemId { get; init; }
    public Guid? SupersedesId { get; init; }
    public string? Applicability { get; init; }
    public string? ExpiryCondition { get; init; }
    public string? Reason { get; init; }
    public bool IsCompanionWriter { get; init; }
    public bool RespectEvolutionMode { get; init; }
}

public sealed class RevertMemoryItemRequest
{
    public int? TargetVersion { get; init; }
    public string? Reason { get; init; }
}

public sealed class MemoryDocumentSectionDto
{
    public string Heading { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public int EstimatedTokens { get; init; }
}

public sealed class MemoryDocumentDto
{
    public string Scope { get; init; } = "project";
    public Guid? ProjectId { get; init; }
    public Guid? CompanionId { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string Markdown { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public int CoreMarkdownMaxTokens { get; init; }
    public int ActiveHotCount { get; init; }
    public int ActiveItemCount => ActiveHotCount;
    public int ColdArchiveCount { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<MemoryDocumentSectionDto> Sections { get; init; } = [];
    public IReadOnlyList<MemoryItemLedgerDto> Items { get; init; } = [];
}

public sealed class UpdateMemoryDocumentRequest
{
    public string Markdown { get; init; } = string.Empty;
    public Guid? ProjectId { get; init; }
    public string? WorkspaceRoot { get; init; }
    public Guid? CompanionId { get; init; }
    public string? ExpectedContentHash { get; init; }
    public string? ExpectedHash { get; init; }
    public Guid? SourceSessionId { get; init; }
    public long? SourceMessageSeq { get; init; }
    public string? Reason { get; init; }
    public bool IsCompanionWriter { get; init; }
}

public sealed class ConversationRecallRequest
{
    public string Query { get; init; } = string.Empty;
    public bool? CrossSession { get; init; }
    public int? Limit { get; init; }
    public string? Fidelity { get; init; }
}

public sealed class RecallVerbatimSnippetDto
{
    public Guid SessionId { get; init; }
    public string? SessionTitle { get; init; }
    public long MessageSeq { get; init; }
    public string Role { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string Excerpt { get; init; } = string.Empty;
    public double Score { get; init; }
}

public sealed class ConversationRecallResultDto
{
    public Guid SessionId { get; init; }
    public string Query { get; init; } = string.Empty;
    public string Fidelity { get; init; } = "balanced";
    public bool CrossSessionAllowed { get; init; }
    public bool CrossSessionUsed { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<RecallVerbatimSnippetDto> VerbatimSnippets { get; init; } = [];
    public int SearchedSessions { get; init; }
    public int TotalMatches { get; init; }
    public string? Note { get; init; }
}

public sealed class MemoryDocumentSummaryDto
{
    public string Scope { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public int CoreMarkdownMaxTokens { get; init; }
    public int ActiveHotCount { get; init; }
    public int ActiveItemCount => ActiveHotCount;
    public int ColdArchiveCount { get; init; }
    public string ContentHash { get; init; } = string.Empty;
}

public sealed class ContextStrategyPresetDto
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool AsyncCompactionEnabled { get; init; }
    public bool CrossSessionRecallSupported { get; init; }
    public bool ActiveMemoryEvolution { get; init; }
}

public sealed class ContextMemoryInspectDto
{
    public ContextMemoryPolicyDto Policy { get; init; } = new();
    public IReadOnlyList<MemoryDocumentSummaryDto> Documents { get; init; } = [];
    public IReadOnlyList<ContextStrategyPresetDto> StrategyPresets { get; init; } = [];
}

public sealed class ContextMemoryApplyRequest
{
    public string Action { get; init; } = "update_policy";
    public UpdateContextMemoryPolicyRequest? Policy { get; init; }
    public UpsertMemoryItemRequest? Memory { get; init; }
    public Guid? ItemId { get; init; }
    public int? TargetVersion { get; init; }
    public string? Reason { get; init; }
}

public sealed class ContextMemoryApplyResultDto
{
    public string Action { get; init; } = string.Empty;
    public bool Applied { get; init; }
    public string Summary { get; init; } = string.Empty;
    public ContextMemoryPolicyDto? Policy { get; init; }
    public MemoryItemLedgerDto? Item { get; init; }
    public Guid? CandidateId { get; init; }
}
