namespace TinadecCore.Companion;

/// <summary>
/// Live pub/sub event broker for the Companion domain. Replaces 200ms SQLite polling on
/// <c>/api/v1/companion-events</c> with bounded per-subscriber <see cref="System.Threading.Channels.Channel{T}"/>
/// fan-out, carrying both ephemeral streaming frames (<c>companion.delta</c>, <c>companion.tool_call</c>,
/// <c>companion.proposal</c>, <c>companion.stage_cue</c>, <c>companion.work_session</c>) and immediate
/// wake signals whenever durable <c>companion_activities</c> rows commit.
/// </summary>
public interface ICompanionStreamBroker
{
    void PublishDelta(
        Guid tenantId,
        Guid runId,
        Guid companionId,
        Guid conversationId,
        string delta,
        int seq);

    void PublishToolCall(
        Guid tenantId,
        Guid runId,
        Guid companionId,
        Guid conversationId,
        string callId,
        string toolName,
        string status,
        string? argumentsPreview = null,
        string? resultPreview = null);

    void PublishProposal(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        string kind,
        Guid proposalId,
        string action,
        string status,
        string summary,
        object? payload = null);

    void PublishStageCue(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        Guid? runId,
        string emotion,
        bool speaking,
        double intensity = 0.6,
        string? gesture = null);

    void PublishWorkSession(
        Guid tenantId,
        Guid? companionId,
        Guid? conversationId,
        object payload)
    {
    }

    void NotifyActivityCommitted(Guid tenantId);

    ICompanionStreamSubscription Subscribe(Guid tenantId);
}

public sealed record CompanionStreamSignal(
    bool IsActivityWake,
    string? EventName = null,
    string? DataJson = null);

public interface ICompanionStreamSubscription : IDisposable
{
    ValueTask<CompanionStreamSignal?> ReadOrTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
