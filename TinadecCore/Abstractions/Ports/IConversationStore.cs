namespace TinadecCore.Abstractions.Ports;

/// <summary>Core-owned, idempotent conversation and turn storage.</summary>
public interface IConversationStore
{
    Task<ConversationMessage> AppendMessageAsync(
        Guid sessionId,
        string role,
        string content,
        Guid? runId = null,
        Guid? turnId = null,
        string? clientMessageId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(
        Guid sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    Task<ConversationMessage?> FindMessageByClientMessageIdAsync(
        Guid sessionId,
        string clientMessageId,
        CancellationToken cancellationToken = default);

    Task<long> GetContextRevisionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns immutable context-version metadata without exposing full context bodies.
    /// Bodies remain in ContentStore and are resolved only by Core runtime components.
    /// </summary>
    Task<IReadOnlyList<ConversationContextVersion>> ListContextVersionsAsync(
        Guid sessionId,
        Guid? runId = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a context patch only when its base revision matches the current revision.
    /// A rejected stale patch is still retained for audit and replay.
    /// </summary>
    Task<ContextPatchApplyResult> ApplyContextPatchAsync(
        ContextPatchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns applied, run-scoped context patches after a checkpoint's observed
    /// revision. The Core runtime uses the persisted patch body to resume a run
    /// after a host restart without consulting request-local state.
    /// </summary>
    Task<IReadOnlyList<ConversationContextPatch>> ListAppliedContextPatchesAsync(
        Guid sessionId,
        Guid runId,
        long afterRevision,
        CancellationToken cancellationToken = default);

    Task<ConversationTurn> CreateTurnAsync(
        Guid sessionId,
        Guid userMessageId,
        string kind,
        long baseContextRevision,
        CancellationToken cancellationToken = default);

    Task<ConversationTurn?> FindTurnByUserMessageAsync(Guid userMessageId, CancellationToken cancellationToken = default);

    Task AttachRunAsync(Guid turnId, Guid runId, CancellationToken cancellationToken = default);

    Task CompleteTurnAsync(
        Guid turnId,
        Guid runId,
        Guid? assistantMessageId,
        long resultContextRevision,
        string status,
        CancellationToken cancellationToken = default);
}

public sealed record ConversationMessage(
    Guid Id,
    Guid SessionId,
    Guid? RunId,
    Guid? TurnId,
    string? ClientMessageId,
    long Sequence,
    string Role,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record ConversationTurn(
    Guid Id,
    Guid SessionId,
    Guid UserMessageId,
    Guid? AssistantMessageId,
    Guid? RunId,
    string Kind,
    string Status,
    long BaseContextRevision,
    long ResultContextRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ConversationContextVersion(
    Guid Id,
    Guid SessionId,
    Guid? RunId,
    long Revision,
    string Kind,
    string Status,
    long? BaseRevision,
    DateTimeOffset CreatedAt);

public sealed record ContextPatchRequest(
    Guid SessionId,
    long BaseRevision,
    string Content,
    string Summary,
    Guid? RunId = null,
    Guid? AgentInstanceId = null,
    string Kind = "supplement");

public sealed record ContextPatchApplyResult(
    Guid PatchId,
    string Status,
    long CurrentRevision,
    long? AppliedRevision);

public sealed record ConversationContextPatch(
    Guid Id,
    Guid SessionId,
    Guid? RunId,
    Guid? AgentInstanceId,
    long BaseRevision,
    long AppliedRevision,
    string Kind,
    string Summary,
    string Content,
    DateTimeOffset CreatedAt);
