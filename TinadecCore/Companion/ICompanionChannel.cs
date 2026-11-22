namespace TinadecCore.Companion;

/// <summary>
/// Transport-agnostic ingress contract for the Companion brain. Decouples companion reasoning,
/// mention relays, and schedule wakes from any specific UI or external IM transport without abusing
/// work-domain <c>TinaChat</c>.
/// </summary>
public interface ICompanionChannel
{
    Task<CompanionChannelPostResult> OnInboundMessageAsync(
        Guid tenantId,
        CompanionEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<CompanionChannelRelayResult> OnMentionAsync(
        Guid tenantId,
        CompanionRunRecord sourceRun,
        CompanionMessageRecord replyMessage,
        IReadOnlyList<Guid> mentionedCompanionIds,
        CancellationToken cancellationToken = default);

    Task<CompanionScheduleTriggerResult> OnScheduleTickAsync(
        Guid tenantId,
        Guid companionId,
        Guid scheduleId,
        bool manualTrigger = false,
        CancellationToken cancellationToken = default);
}

public sealed record CompanionEnvelope(
    string ChannelKind,
    string? ThreadKey,
    Guid ConversationId,
    string AuthorKind,
    Guid? AuthorCompanionId,
    string Body,
    IReadOnlyList<Guid>? AddressedTo = null,
    int HopDepth = 0,
    string? IdempotencyKey = null);

public sealed record CompanionChannelPostResult(
    object Json,
    Guid MessageId,
    IReadOnlyList<Guid> RunIds,
    bool Duplicate);

public sealed record CompanionChannelRelayResult(
    IReadOnlyList<Guid> EnqueuedRunIds,
    IReadOnlyList<object> SkippedRuns);

public sealed record CompanionScheduleTriggerResult(
    object ScheduleJson,
    Guid ConversationId,
    IReadOnlyList<Guid> RunIds);
