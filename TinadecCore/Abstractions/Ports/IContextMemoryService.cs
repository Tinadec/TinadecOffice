using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Four-layer memory and context policy service: manages TOML policy, immutable DB ledger
/// &lt;-&gt; token-capped Markdown dual-layer projection, 1-click item revert, and conscious
/// conversation history recall.
/// </summary>
public interface IContextMemoryService
{
    Task<ContextMemoryPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default);
    Task<ContextMemoryPolicyDto> UpdatePolicyAsync(UpdateContextMemoryPolicyRequest request, CancellationToken cancellationToken = default);

    Task<MemoryDocumentDto> GetDocumentAsync(
        string scope,
        Guid? projectId = null,
        string? workspaceRoot = null,
        Guid? companionId = null,
        CancellationToken cancellationToken = default);

    Task<MemoryDocumentDto> UpdateDocumentAsync(
        string scope,
        UpdateMemoryDocumentRequest request,
        CancellationToken cancellationToken = default);

    Task<MemoryItemListResultDto> ListItemsAsync(
        string? scope = null,
        Guid? projectId = null,
        Guid? companionId = null,
        string? status = null,
        string? kind = null,
        string? query = null,
        bool includeVersions = true,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<MemoryItemLedgerDto> UpsertItemAsync(
        UpsertMemoryItemRequest request,
        CancellationToken cancellationToken = default);

    Task<MemoryItemLedgerDto> RevokeItemAsync(
        Guid itemId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<MemoryItemLedgerDto> RevertItemAsync(
        Guid itemId,
        RevertMemoryItemRequest? request = null,
        CancellationToken cancellationToken = default);

    Task<ConversationRecallResultDto> RecallConversationHistoryAsync(
        Guid sessionId,
        ConversationRecallRequest request,
        Guid? callerProjectId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Helper for tinadec_inspect(target: "context_memory").</summary>
    Task<ContextMemoryInspectDto> InspectAsync(
        Guid? projectId = null,
        string? workspaceRoot = null,
        Guid? companionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Helper for tinadec_context_memory_apply (self-configuration &amp; memory mutation).</summary>
    Task<ContextMemoryApplyResultDto> ApplySelfConfigAsync(
        ContextMemoryApplyRequest request,
        Guid? callerSessionId = null,
        Guid? callerRunId = null,
        Guid? callerProjectId = null,
        string? callerWorkspaceRoot = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Context management strategy and always-on efficiency primitives (KV-cache friendly append-only
/// hot window + automatic stale tool output micro-compaction + 3-tier strategy presets).
/// </summary>
public interface IContextStrategyService
{
    ContextStrategyProfile ResolveProfile(string? strategyOverride = null, ContextMemoryPolicyDto? policy = null);

    bool ShouldTriggerAsyncCompaction(
        int estimatedTokens,
        int tokenBudget,
        string? strategyOverride = null,
        double? triggerRatioOverride = null);

    ContextMicroCompactionResult MicroCompactMessages(
        IReadOnlyList<ContextWindowMessage> messages,
        int hotTailCount = 6,
        int offloadThresholdBytes = 16_384,
        Func<ContextWindowMessage, string>? offloadArtifact = null);
}

public sealed record ContextStrategyProfile(
    string Strategy,
    bool AlwaysOnKvCacheAppendWindow,
    bool AlwaysOnMicroCompaction,
    bool AsyncCompactionEnabled,
    bool HistoryRecallEnabled,
    bool CrossSessionRecallEnabled,
    bool ExplorationDigestEnabled,
    bool ActiveMemoryEvolutionEnabled,
    double CompactionTriggerRatio,
    string RecallFidelity);

public sealed record ContextWindowMessage(
    long Sequence,
    string Role,
    string Content);

public sealed record ContextMicroCompactionResult(
    IReadOnlyList<ContextWindowMessage> Messages,
    int FoldedCount,
    int OffloadedCount,
    int SavedBytes,
    IReadOnlyList<string> ArtifactPointers);
